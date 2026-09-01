using System;
using System.Collections.Generic;
using Shadowbus.Server.Room;

namespace Shadowbus.Server.SocketIO
{
    /// <summary>
    /// Routes a decoded game frame through the room's logical battle session.
    /// SocketIoServer remains responsible for transport and ACKs; this class
    /// owns only direction/channel selection and pending reconnect frames.
    /// </summary>
    internal sealed class RealtimeMessageRouter
    {
        private readonly Dictionary<string, BattleSession> _sessions =
            new Dictionary<string, BattleSession>(StringComparer.Ordinal);
        private readonly object _sync = new object();

        public BattleSession GetOrCreate(string roomId)
        {
            if (string.IsNullOrEmpty(roomId))
                return null;

            lock (_sync)
            {
                if (!_sessions.TryGetValue(roomId, out BattleSession session))
                {
                    session = new BattleSession(roomId);
                    _sessions.Add(roomId, session);
                }
                return session;
            }
        }

        public RoutedMessage Accept(
            string roomId,
            string sourcePlayerId,
            string targetPlayerId,
            string eventName,
            Newtonsoft.Json.Linq.JToken message,
            byte[] payload)
        {
            return GetOrCreate(roomId)?.Accept(
                sourcePlayerId,
                targetPlayerId,
                eventName,
                message,
                payload);
        }

        public RoutedMessage CreateServerMessage(
            string roomId,
            string sourcePlayerId,
            string targetPlayerId,
            string eventName,
            Newtonsoft.Json.Linq.JToken message,
            int? deliverySequence = null)
        {
            return GetOrCreate(roomId)?.CreateServerMessage(
                sourcePlayerId,
                targetPlayerId,
                eventName,
                message,
                deliverySequence);
        }

        public void MarkDelivered(string roomId, RoutedMessage message)
        {
            if (message == null)
                return;
            lock (_sync)
            {
                if (_sessions.TryGetValue(roomId, out BattleSession session))
                    session.MarkDelivered(message);
            }
        }

        public BattleSession GetSession(string roomId)
        {
            lock (_sync)
            {
                return _sessions.TryGetValue(roomId, out BattleSession session)
                    ? session
                    : null;
            }
        }

        public IList<RoutedMessage> TakePendingForTarget(string roomId, string targetPlayerId)
        {
            lock (_sync)
            {
                return _sessions.TryGetValue(roomId, out BattleSession session)
                    ? session.TakePendingForTarget(targetPlayerId)
                    : new List<RoutedMessage>();
            }
        }

        public void MarkDelivered(
            string roomId,
            string sourcePlayerId,
            string targetPlayerId,
            string eventName,
            int sourceSequence)
        {
            lock (_sync)
            {
                if (_sessions.TryGetValue(roomId, out BattleSession session))
                {
                    session.MarkDelivered(
                        sourcePlayerId,
                        targetPlayerId,
                        eventName,
                        sourceSequence);
                }
            }
        }

        public void MarkDeliveredForTarget(
            string roomId,
            string targetPlayerId,
            string eventName,
            int sourceSequence)
        {
            lock (_sync)
            {
                if (_sessions.TryGetValue(roomId, out BattleSession session))
                {
                    session.MarkDeliveredForTarget(targetPlayerId, eventName, sourceSequence);
                }
            }
        }

        public void Remove(string roomId)
        {
            if (string.IsNullOrEmpty(roomId))
                return;
            lock (_sync)
                _sessions.Remove(roomId);
        }
    }
}
