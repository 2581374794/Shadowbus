using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Cute;
using Newtonsoft.Json.Linq;
using Shadowbus.Server.Core;
using Shadowbus.Server.Network;
using Shadowbus.Server.Room;

namespace Shadowbus.Server.SocketIO
{
    /// <summary>
    /// Socket.IO endpoint used by the room host. The HTTP upgrade and
    /// RFC6455 framing are implemented directly over TcpListener so this
    /// works on Unity's Mono runtime.
    /// </summary>
    public sealed class SocketIoServer : IDisposable
    {
        private const string WebSocketMagic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly ServerConfig _config;
        private readonly RoomManager _rooms;
        private readonly Dictionary<string, SocketIoConnection> _connections =
            new Dictionary<string, SocketIoConnection>();
        private readonly HashSet<string> _roomReadySent =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly RealtimeMessageRouter _messageRouter =
            new RealtimeMessageRouter();
        private readonly object _connectionLock = new object();
        private TcpListener _listener;
        private Thread _listenerThread;
        private volatile bool _running;

        public SocketIoServer(ServerConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _rooms = new RoomManager();
        }

        public bool IsRunning => _running;
        public int Port => _config.Port;
        public string RoomCode { get; private set; }
        public RoomManager Rooms => _rooms;

        public bool Start()
        {
            if (_running)
                return false;

            try
            {
                IPAddress address = ResolveBindAddress(_config.BindAddress);
                _listener = new TcpListener(address, _config.Port);
                _listener.Start(100);
                _running = true;
                _listenerThread = new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = "Shadowbus-SocketIO-Listener"
                };
                _listenerThread.Start();
                Plugin.Logger.LogInfo($"[SocketIO] Listener started on {BuildPrefix()}");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[SocketIO] Failed to start listener: {ex.Message}");
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            _running = false;
            try
            {
                _listener?.Stop();
            }
            catch
            {
            }
            finally
            {
                _listener = null;
            }

            SocketIoConnection[] connections;
            lock (_connectionLock)
                connections = new List<SocketIoConnection>(_connections.Values).ToArray();
            foreach (SocketIoConnection connection in connections)
                connection.Close("server_stopped");
            lock (_connectionLock)
                _connections.Clear();
        }

        public string CreateRoomCode(string roomId)
        {
            return CreateRoomCode(roomId, null);
        }

        public string CreateRoomCode(string roomId, global::Shadowbus.P2PProfile hostProfile)
        {
            return CreateRoomCode(roomId, hostProfile, null);
        }

        public string CreateRoomCode(
            string roomId,
            global::Shadowbus.P2PProfile hostProfile,
            RoomRules roomRules)
        {
            string address = _config.AdvertisedAddress;
            if (string.IsNullOrWhiteSpace(address))
                address = _config.BindAddress == "0.0.0.0" ? "127.0.0.1" : _config.BindAddress;
            RoomCode = ConnectionCode.Generate(address, Port, roomId, hostProfile, roomRules);
            return RoomCode;
        }

        internal void OnSocketConnected(SocketIoConnection connection)
        {
            if (connection == null)
                return;

            lock (_connectionLock)
            {
                if (_connections.ContainsKey(connection.SessionId))
                    return;
                if (_config.MaxConnections > 0 && _connections.Count >= _config.MaxConnections)
                {
                    Plugin.Logger.LogWarning($"[SocketIO] Rejecting {connection.SessionId}: max connections reached");
                    connection.SendEvent("error", new { code = "server_full" });
                    connection.Close("server_full");
                    return;
                }
            }

            Dictionary<string, string> query = ParseQuery(connection.Query);
            string battleId = GetQuery(query, "BattleId", "battleId");
            if (string.IsNullOrWhiteSpace(battleId))
            {
                Plugin.Logger.LogWarning($"[SocketIO] Rejecting {connection.SessionId}: missing BattleId");
                connection.SendEvent("error", new { code = "missing_battle_id" });
                connection.Close("missing_battle_id");
                return;
            }

            GameRoom room = _rooms.GetRoom(battleId);
            if (room == null)
            {
                Plugin.Logger.LogWarning($"[SocketIO] Rejecting {connection.SessionId}: room {battleId} not found");
                connection.SendEvent("error", new { code = "room_not_found" });
                connection.Close("room_not_found");
                return;
            }

            // The query viewerId is the game's original account id and is not
            // used as the Shadowbus identity. Bind connections to room slots;
            // the persistent client profile supplies the real viewer id.
            int? queryViewerId = TryGetQueryViewerId(query);
            Player player = FindConnectionPlayer(room, battleId, queryViewerId);
            if (player == null)
            {
                if (room.IsFull())
                {
                    Plugin.Logger.LogWarning($"[SocketIO] Rejecting {connection.SessionId}: room {battleId} is full");
                    connection.SendEvent("error", new { code = "room_full" });
                    connection.Close("room_full");
                    return;
                }

                string playerId = "socket-" + connection.SessionId;
                if (!room.AddPlayer(playerId))
                {
                    connection.SendEvent("error", new { code = "room_join_failed" });
                    connection.Close("room_join_failed");
                    return;
                }
                player = room.GetPlayer(playerId);
                player.ViewerId = 0;
                player.Name = "Player";
            }

            if (IsPlayerConnected(player.PlayerId, battleId))
            {
                Plugin.Logger.LogWarning(
                    $"[SocketIO] Rejecting {connection.SessionId}: player {player.PlayerId} is already connected");
                connection.SendEvent("error", new { code = "player_already_connected" });
                connection.Close("player_already_connected");
                return;
            }

            connection.BattleId = battleId;
            connection.PlayerId = player.PlayerId;
            connection.IsHost = player.IsHost;
            lock (_connectionLock)
                _connections[connection.SessionId] = connection;

            bool reconnecting = player.HasConnected;
            player.HasConnected = true;

            // RoomConnectController/RealTimeNetworkAgent registers this
            // native event before opening the socket.  Sending it after the
            // logical slot is bound makes the transport lifecycle visible to
            // the stock agent and is also the trigger it uses to restart its
            // reconnect bookkeeping.
            connection.SendEvent(reconnecting ? "reconnect_socket" : "connect_socket");

            Plugin.Logger.LogInfo($"[SocketIO] Client {connection.SessionId} bound to room {battleId} as {(player.IsHost ? "host" : "guest")}; query viewerId hint={(queryViewerId.HasValue ? queryViewerId.Value.ToString() : "none")}");

            if (reconnecting)
            {
                // A reconnect can happen while the peer is still online or
                // while frames are queued for this logical slot. Refresh the
                // room view and flush those frames immediately; Reenter is
                // still handled below for the native client's explicit
                // recovery request.
                SendOpponentSnapshot(connection, room);
                FlushPendingMessages(connection, room);
            }
        }

        internal void OnSocketPacket(SocketIoConnection connection, string eventName, JToken payload, byte[] binary, int? packetId)
        {
            if (connection == null || string.IsNullOrEmpty(eventName))
                return;

            if (binary == null)
            {
                if (eventName == "heartbeat_ping")
                    connection.SendEvent("heartbeat_pong");
                else if (eventName == "alive")
                    connection.SendEvent("alive");
                if (packetId.HasValue)
                    connection.SendAck(packetId.Value, new { result = 1 });
                Plugin.Logger.LogDebug($"[SocketIO] JSON event {eventName} from {connection.SessionId}");
                return;
            }

            if (eventName == "alive")
            {
                HandleAlivePacket(connection, binary, packetId);
                return;
            }

            if (eventName != "msg" && eventName != "hand")
            {
                Plugin.Logger.LogWarning($"[SocketIO] Ignoring binary event {eventName} from {connection.SessionId}");
                return;
            }

            if (!SocketIoPayloadCodec.TryDecode(binary, out JToken message, out int sequence, out string uri))
            {
                if (packetId.HasValue)
                    connection.SendAck(packetId.Value, 0, true);
                return;
            }

            string sequenceFields = SocketIoPayloadCodec.DescribeSequenceFields(message, sequence);
            Plugin.Logger.LogInfo(
                $"[SocketIO] IN {eventName} {uri ?? "<unknown>"} from {connection.SessionId} " +
                $"role={(connection.IsHost ? "host" : "guest")}, seq={sequence}, fields={sequenceFields}, " +
                $"ack={(packetId.HasValue ? packetId.Value.ToString() : "none")}, bytes={binary.Length}");

            if (uri == "ShadowbusProfile")
            {
                GameRoom profileRoom = _rooms.GetRoom(connection.BattleId);
                Player profilePlayer = profileRoom == null ? null : FindPlayer(profileRoom, connection.PlayerId);
                connection.LastProfile = message?.DeepClone();
                if (profilePlayer != null && ApplyPlayerProfile(profilePlayer, message))
                {
                    Plugin.Logger.LogInfo($"[SocketIO] Profile updated for {connection.SessionId}: name={profilePlayer.Name}, viewerId={profilePlayer.ViewerId}");
                    if (connection.LastRoomEntry != null)
                    {
                        connection.LastRoomEntry = CreateRoomEntryWithProfile(
                            connection.LastRoomEntry,
                            profilePlayer);
                    }
                }
                if (packetId.HasValue)
                    connection.SendAck(packetId.Value, sequence, true);
                return;
            }

            // InitNetwork is echoed to the sender. The stock agent marks its
            // network initialization complete only after this synchronize.
            if (uri == "InitNetwork")
            {
                connection.SendBinaryEvent("synchronize", binary);
                return;
            }

            if (packetId.HasValue)
                connection.SendAck(packetId.Value, sequence, true);

            GameRoom room = _rooms.GetRoom(connection.BattleId);
            if (room == null)
                return;

            if (uri == "InitRoomBattle")
            {
                HandleInitRoomBattle(connection, room, message, binary);
                return;
            }

            if (uri == "Loaded")
            {
                HandleLoaded(connection, room, message, binary);
                return;
            }

            if (uri == "Deal")
            {
                HandleDeal(connection, room);
                return;
            }

            if (uri == "Swap")
            {
                HandleSwap(connection, room, message);
                return;
            }

            if (uri == "SetupComplete")
            {
                room.SetPlayerReady(connection.PlayerId, true);
            }
            else if (uri == "SetupCancel")
            {
                room.SetPlayerReady(connection.PlayerId, false);
                lock (_connectionLock)
                    _roomReadySent.Remove(room.RoomId);
            }

            if (uri == "Reenter")
            {
                SendOpponentSnapshot(connection, room);
                FlushPendingMessages(connection, room);
            }

            JToken relayMessage = message;
            if (uri == "RoomEntry")
            {
                Player entryPlayer = FindPlayer(room, connection.PlayerId);
                if (entryPlayer != null)
                {
                    // The approved client transport embeds its profile in the
                    // initial RoomEntry. Merge it before relaying so each peer
                    // receives one complete RoomEntry instead of a later
                    // duplicate profile snapshot.
                    if (HasEmbeddedProfile(message) && ApplyPlayerProfile(entryPlayer, message))
                    {
                        relayMessage = CreateRoomEntryWithProfile(message, entryPlayer);
                    }
                    else if (connection.LastProfile != null && ApplyPlayerProfile(entryPlayer, connection.LastProfile))
                    {
                        relayMessage = CreateRoomEntryWithProfile(message, entryPlayer);
                    }
                    connection.LastRoomEntry = relayMessage?.DeepClone();
                }
            }

            // Route against logical room slots rather than only currently
            // open sockets. A reliable frame received while the opponent is
            // reconnecting must stay in BattleSession.pending and be flushed
            // by Reenter once that slot binds again.
            foreach (Player targetPlayer in room.GetPlayers())
            {
                if (targetPlayer == null ||
                    string.Equals(targetPlayer.PlayerId, connection.PlayerId, StringComparison.Ordinal))
                    continue;

                RoutedMessage routed = _messageRouter.Accept(
                    room.RoomId,
                    connection.PlayerId,
                    targetPlayer.PlayerId,
                    eventName,
                    relayMessage,
                    relayMessage == message ? binary : SocketIoPayloadCodec.Encode(relayMessage));
                if (routed == null || routed.IsDuplicate)
                    continue;

                SocketIoConnection peer = FindActiveConnection(connection.BattleId, targetPlayer.PlayerId);
                bool sent = peer != null && SendRoutedMessage(peer, routed);
                if (sent && routed.HasSequence)
                {
                    _messageRouter.MarkDelivered(
                        room.RoomId,
                        connection.PlayerId,
                        targetPlayer.PlayerId,
                        eventName,
                        routed.SourceSequence);
                }
            }

            // RoomBase waits for the server's RoomReady URI before it starts
            // Matching_Room and emits InitRoomBattle. Both clients must have
            // sent SetupComplete before this one-shot transition is broadcast.
            if (uri == "SetupComplete")
                TryBroadcastRoomReady(room);

            if (uri == "RoomCreate" || uri == "RoomEntry")
            {
                JObject result = new JObject
                {
                    ["uri"] = uri,
                    ["resultCode"] = 1,
                    ["isSelf"] = 1,
                    ["viewerId"] = FindPlayer(room, connection.PlayerId)?.ViewerId ?? 0
                };
                connection.SendBinaryEvent("synchronize", SocketIoPayloadCodec.Encode(result));

                if (uri == "RoomEntry")
                {
                    // Send the host snapshot only after the sender has
                    // received its own RoomEntry result. At this point the
                    // stock client has switched from OnReceiveFast to its
                    // normal room receiver, so the snapshot is not dropped.
                    SendOpponentSnapshot(connection, room);
                }
            }

            // The stock room controller waits for a node result event before
            // it starts CloseRoomTask/LeaveRoomTask. A Socket.IO ACK alone is
            // not sufficient because PlayerControllerForOpponent handles
            // these room URIs through the normal synchronize event path.
            if (uri == "Release" || uri == "Leave" || uri == "ForceRelease")
            {
                JObject result = new JObject
                {
                    ["uri"] = uri,
                    ["resultCode"] = 1,
                    ["isSelf"] = 1,
                    ["viewerId"] = FindPlayer(room, connection.PlayerId)?.ViewerId ?? 0
                };
                connection.SendBinaryEvent("synchronize", SocketIoPayloadCodec.Encode(result));
                Plugin.Logger.LogInfo(
                    $"[SocketIO] Sent {uri} success result to {connection.SessionId}");
            }
        }

        internal void OnSocketClosed(SocketIoConnection connection, string reason)
        {
            if (connection == null)
                return;

            lock (_connectionLock)
                _connections.Remove(connection.SessionId);

            GameRoom room = _rooms.GetRoom(connection.BattleId);
            if (room != null && !string.IsNullOrEmpty(connection.PlayerId))
            {
                // Keep the logical slot, profile and battle-session history
                // alive so the native agent can reconnect and receive pending
                // frames. Explicit room shutdown is handled separately by
                // the room/task lifecycle; a transport close alone is not a
                // logical leave.
                room.MarkPlayerDisconnected(connection.PlayerId);
                foreach (SocketIoConnection peer in GetRoomPeers(connection))
                    peer.SendEvent("opponent_lost");
            }

            Plugin.Logger.LogInfo($"[SocketIO] Client disconnected: {connection.SessionId} ({reason})");

            if (room != null)
                BroadcastAliveStatus(room, connection);
        }

        private void HandleAlivePacket(SocketIoConnection connection, byte[] binary, int? packetId)
        {
            if (!SocketIoPayloadCodec.TryDecode(binary, out JToken message, out _, out string uri) ||
                !string.Equals(uri, "Gungnir", StringComparison.Ordinal))
            {
                Plugin.Logger.LogWarning($"[SocketIO] Invalid alive payload from {connection.SessionId}");
                if (packetId.HasValue)
                    connection.SendAck(packetId.Value, new { result = 1 }, true);
                return;
            }

            GameRoom room = _rooms.GetRoom(connection.BattleId);
            if (packetId.HasValue)
                connection.SendAck(packetId.Value, new { result = 1 }, true);
            if (room == null)
                return;

            JObject selfResponse = CreateAliveResponse(connection, room);
            connection.SendBinaryEvent("alive", SocketIoPayloadCodec.Encode(selfResponse));

            foreach (SocketIoConnection peer in GetRoomPeers(connection))
            {
                JObject peerResponse = CreateAliveResponse(peer, room);
                peer.SendBinaryEvent("alive", SocketIoPayloadCodec.Encode(peerResponse));
            }
        }

        private void BroadcastAliveStatus(GameRoom room, SocketIoConnection disconnected)
        {
            foreach (SocketIoConnection peer in GetRoomPeers(disconnected))
            {
                JObject response = CreateAliveResponse(peer, room);
                peer.SendBinaryEvent("alive", SocketIoPayloadCodec.Encode(response));
            }
        }

        private void TryBroadcastRoomReady(GameRoom room)
        {
            if (room == null || !room.AreAllPlayersReady())
                return;

            var targets = new List<SocketIoConnection>();
            lock (_connectionLock)
            {
                if (!_roomReadySent.Add(room.RoomId))
                    return;

                foreach (SocketIoConnection connection in _connections.Values)
                {
                    if (connection.IsOpen &&
                        string.Equals(connection.BattleId, room.RoomId, StringComparison.Ordinal))
                    {
                        targets.Add(connection);
                    }
                }

                if (targets.Count == 0)
                    _roomReadySent.Remove(room.RoomId);
            }

            if (targets.Count == 0)
                return;

            byte[] payload = SocketIoPayloadCodec.Encode(new JObject
            {
                ["uri"] = "RoomReady"
            });
            foreach (SocketIoConnection target in targets)
                target.SendBinaryEvent("synchronize", payload);

            Plugin.Logger.LogInfo(
                $"[SocketIO] Room {room.RoomId} all players ready; " +
                $"broadcast RoomReady to {targets.Count} client(s)");
        }

        private void HandleInitRoomBattle(
            SocketIoConnection connection,
            GameRoom room,
            JToken message,
            byte[] binary)
        {
            Player player = FindPlayer(room, connection.PlayerId);
            if (player == null)
                return;

            PlayerDeck deck;
            string error;
            JObject snapshot = (message as JObject)?["shadowbusDeck"] as JObject;
            if (snapshot != null)
            {
                if (!TryParseDeckSnapshot(snapshot, out deck, out error))
                {
                    Plugin.Logger.LogError(
                        $"[SocketIO] Rejected InitRoomBattle from {connection.SessionId}: {error}");
                    return;
                }
            }
            else
            {
                // A native reconnect may resend the matching frame without
                // the optional client snapshot. Reuse the accepted deck when
                // this logical slot has already initialized; a first frame
                // without a snapshot is still rejected below.
                BattleSession previousSession = _messageRouter.GetSession(room.RoomId);
                if (previousSession == null ||
                    !previousSession.TryGetInitRoomBattle(
                        connection.PlayerId,
                        out deck,
                        out _))
                {
                    Plugin.Logger.LogError(
                        $"[SocketIO] InitRoomBattle from {connection.SessionId} has no deck snapshot");
                    return;
                }
                Plugin.Logger.LogWarning(
                    $"[SocketIO] InitRoomBattle retry from {connection.SessionId} " +
                    "has no deck snapshot; reusing the previous deck");
            }

            player.Deck = deck;

            Player opponent = FindOpponent(connection, room);
            if (opponent == null)
            {
                Plugin.Logger.LogError(
                    $"[SocketIO] InitRoomBattle from {connection.SessionId} has no opponent slot");
                return;
            }

            BattleSession session = _messageRouter.GetOrCreate(room.RoomId);
            RoutedMessage reservation = _messageRouter.Accept(
                room.RoomId,
                connection.PlayerId,
                opponent.PlayerId,
                "msg",
                message,
                binary);
            if (reservation != null && reservation.HasSequence)
            {
                _messageRouter.MarkDelivered(
                    room.RoomId,
                    connection.PlayerId,
                    opponent.PlayerId,
                    "msg",
                    reservation.SourceSequence);
            }
            if (reservation == null || reservation.DeliverySequence <= 0)
            {
                Plugin.Logger.LogError(
                    $"[SocketIO] InitRoomBattle from {connection.SessionId} has no usable pubSeq");
                return;
            }
            if (!session.RecordInitRoomBattle(
                connection.PlayerId,
                deck,
                reservation?.DeliverySequence ?? 0))
            {
                Plugin.Logger.LogError(
                    $"[SocketIO] InitRoomBattle from {connection.SessionId} could not be recorded");
                return;
            }
            Plugin.Logger.LogInfo(
                $"[SocketIO] InitRoomBattle accepted from {connection.SessionId}: " +
                $"cards={deck.CardIds.Length}, deliverySeq={reservation?.DeliverySequence ?? 0}");

            Player host = null;
            Player guest = null;
            foreach (Player candidate in room.GetPlayers())
            {
                if (candidate.IsHost)
                    host = candidate;
                else
                    guest = candidate;
            }

            if (host == null || guest == null ||
                !session.TryBeginMatched(host.PlayerId, guest.PlayerId))
                return;

            SendMatched(session, room, host, guest);
        }

        private void HandleLoaded(
            SocketIoConnection connection,
            GameRoom room,
            JToken message,
            byte[] binary)
        {
            Player opponent = FindOpponent(connection, room);
            if (opponent == null)
                return;

            BattleSession session = _messageRouter.GetOrCreate(room.RoomId);
            RoutedMessage reservation = _messageRouter.Accept(
                room.RoomId,
                connection.PlayerId,
                opponent.PlayerId,
                "msg",
                message,
                binary);
            if (reservation != null && reservation.HasSequence)
            {
                _messageRouter.MarkDelivered(
                    room.RoomId,
                    connection.PlayerId,
                    opponent.PlayerId,
                    "msg",
                    reservation.SourceSequence);
            }
            if (reservation == null || reservation.DeliverySequence <= 0)
            {
                Plugin.Logger.LogError(
                    $"[SocketIO] Loaded from {connection.SessionId} has no usable pubSeq");
                return;
            }
            session.RecordLoaded(connection.PlayerId, reservation?.DeliverySequence ?? 0);
            Plugin.Logger.LogInfo(
                $"[SocketIO] Loaded accepted from {connection.SessionId}: " +
                $"deliverySeq={reservation?.DeliverySequence ?? 0}");

            Player host = null;
            Player guest = null;
            foreach (Player candidate in room.GetPlayers())
            {
                if (candidate.IsHost)
                    host = candidate;
                else
                    guest = candidate;
            }
            if (host == null || guest == null || !session.TryBeginBattleStart())
                return;

            SendBattleStart(session, room, host, guest);
        }

        private void SendMatched(
            BattleSession session,
            GameRoom room,
            Player host,
            Player guest)
        {
            SendServerBattleMessage(session, room, host, guest, false);
            SendServerBattleMessage(session, room, guest, host, false);
            room.ChangeState(RoomState.Preparing);
            Plugin.Logger.LogInfo($"[SocketIO] Matched sent to room {room.RoomId}");
        }

        private void SendBattleStart(
            BattleSession session,
            GameRoom room,
            Player host,
            Player guest)
        {
            SendServerBattleMessage(session, room, host, guest, true);
            SendServerBattleMessage(session, room, guest, host, true);
            room.ChangeState(RoomState.InGame);
            Plugin.Logger.LogInfo($"[SocketIO] BattleStart sent to room {room.RoomId}");
        }

        private void HandleDeal(SocketIoConnection connection, GameRoom room)
        {
            BattleSession session = _messageRouter.GetOrCreate(room.RoomId);
            if (!session.TryRecordDeal(connection.PlayerId, out bool firstRequest))
            {
                Plugin.Logger.LogWarning(
                    $"[SocketIO] Ignored Deal request from {connection.SessionId}: " +
                    "battle setup is not ready");
                return;
            }
            Plugin.Logger.LogInfo(
                $"[SocketIO] Deal request accepted from {connection.SessionId}: " +
                $"role={(connection.IsHost ? "host" : "guest")}, duplicate={!firstRequest}");

            if (!session.TryBeginDeal())
                return;

            if (!TryGetRoomPlayers(room, out Player host, out Player guest))
                return;

            SendServerMulliganMessage(session, room, host, guest, "Deal");
            SendServerMulliganMessage(session, room, guest, host, "Deal");
            Plugin.Logger.LogInfo($"[SocketIO] Deal sent to room {room.RoomId}");
        }

        private void HandleSwap(SocketIoConnection connection, GameRoom room, JToken message)
        {
            if (!(message is JObject data) || !(data["idxList"] is JArray rawIndexes))
            {
                Plugin.Logger.LogError(
                    $"[SocketIO] Swap from {connection.SessionId} has no idxList");
                return;
            }

            var indexes = new List<int>(rawIndexes.Count);
            try
            {
                foreach (JToken value in rawIndexes)
                    indexes.Add(value.Value<int>());
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError(
                    $"[SocketIO] Swap from {connection.SessionId} has invalid idxList: {ex.Message}");
                return;
            }

            BattleSession session = _messageRouter.GetOrCreate(room.RoomId);
            if (!session.TryRecordSwap(
                    connection.PlayerId,
                    indexes,
                    out bool firstSubmission,
                    out string error))
            {
                Plugin.Logger.LogError(
                    $"[SocketIO] Rejected Swap from {connection.SessionId}: {error}");
                return;
            }

            Player target = FindPlayer(room, connection.PlayerId);
            Player source = FindOpponent(connection, room);
            if (target == null || source == null)
                return;

            if (firstSubmission)
            {
                SendServerMulliganMessage(session, room, target, source, "Swap");
                Plugin.Logger.LogInfo(
                    $"[SocketIO] Swap accepted from {connection.SessionId}: changed={indexes.Count}");
            }

            if (!session.TryBeginReady() ||
                !TryGetRoomPlayers(room, out Player host, out Player guest))
                return;

            SendServerMulliganMessage(session, room, host, guest, "Ready");
            SendServerMulliganMessage(session, room, guest, host, "Ready");
            Plugin.Logger.LogInfo($"[SocketIO] Ready sent to room {room.RoomId}");
        }

        private void SendServerMulliganMessage(
            BattleSession session,
            GameRoom room,
            Player target,
            Player source,
            string uri)
        {
            int[] selfIndexes;
            int[] opponentIndexes = null;
            bool hasData;
            if (uri == "Deal")
            {
                hasData = session.TryGetDeal(
                    target.PlayerId,
                    out selfIndexes,
                    out opponentIndexes);
            }
            else if (uri == "Ready")
            {
                hasData = session.TryGetReady(
                    target.PlayerId,
                    out selfIndexes,
                    out opponentIndexes);
            }
            else
            {
                hasData = session.TryGetSwapResult(target.PlayerId, out selfIndexes);
            }

            if (!hasData)
                return;

            var payload = new JObject
            {
                ["uri"] = uri,
                ["self"] = CreateMulliganIndexData(selfIndexes),
                ["viewerId"] = source.ViewerId,
                ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            if (opponentIndexes != null)
                payload["oppo"] = CreateMulliganIndexData(opponentIndexes);

            RoutedMessage routed = _messageRouter.CreateServerMessage(
                room.RoomId,
                source.PlayerId,
                target.PlayerId,
                "msg",
                payload);
            SocketIoConnection targetConnection =
                FindActiveConnection(room.RoomId, target.PlayerId);
            if (routed != null && targetConnection != null &&
                SendRoutedMessage(targetConnection, routed))
            {
                _messageRouter.MarkDelivered(room.RoomId, routed);
                Plugin.Logger.LogInfo(
                    $"[SocketIO] OUT synchronize {uri} to {targetConnection.SessionId} " +
                    $"role={(target.IsHost ? "host" : "guest")}, playSeq={routed.DeliverySequence}");
            }
        }

        private static JArray CreateMulliganIndexData(int[] indexes)
        {
            var result = new JArray();
            if (indexes == null)
                return result;
            for (int i = 0; i < indexes.Length; i++)
            {
                result.Add(new JObject
                {
                    ["pos"] = i,
                    ["idx"] = indexes[i]
                });
            }
            return result;
        }

        private static bool TryGetRoomPlayers(
            GameRoom room,
            out Player host,
            out Player guest)
        {
            host = null;
            guest = null;
            if (room == null)
                return false;

            foreach (Player candidate in room.GetPlayers())
            {
                if (candidate.IsHost)
                    host = candidate;
                else
                    guest = candidate;
            }
            return host != null && guest != null;
        }

        private void SendServerBattleMessage(
            BattleSession session,
            GameRoom room,
            Player target,
            Player source,
            bool battleStart)
        {
            if (target == null || source == null)
                return;

            if (!session.TryGetBattleSetup(
                    target.PlayerId,
                    out PlayerDeck selfDeck,
                    out int[] selfCards,
                    out int battleSeed,
                    out bool selfGoesFirst) ||
                !session.TryGetOpponentSetup(
                    target.PlayerId,
                    out PlayerDeck opponentDeck,
                    out int[] opponentCards))
                return;

            JObject payload = battleStart
                ? CreateBattleStartPayload(room, target, source, selfDeck, opponentDeck, battleSeed)
                : CreateMatchedPayload(room, target, source, selfDeck, opponentDeck,
                    selfCards, battleSeed, selfGoesFirst);
            payload["viewerId"] = source.ViewerId;
            payload["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            int sequence = 0;
            if (battleStart)
                session.TryGetLoadedSequence(source.PlayerId, out sequence);
            else
                session.TryGetInitRoomBattle(source.PlayerId, out _, out sequence);

            RoutedMessage routed = _messageRouter.CreateServerMessage(
                room.RoomId,
                source.PlayerId,
                target.PlayerId,
                "msg",
                payload,
                sequence > 0 ? (int?)sequence : null);
            SocketIoConnection connection = FindActiveConnection(room.RoomId, target.PlayerId);
            if (routed != null && connection != null && SendRoutedMessage(connection, routed))
            {
                _messageRouter.MarkDelivered(room.RoomId, routed);
                Plugin.Logger.LogInfo(
                    $"[SocketIO] OUT synchronize {payload["uri"]} to {connection.SessionId} " +
                    $"role={(target.IsHost ? "host" : "guest")}, playSeq={routed.DeliverySequence}");
            }
        }

        private static JObject CreateMatchedPayload(
            GameRoom room,
            Player target,
            Player source,
            PlayerDeck selfDeck,
            PlayerDeck opponentDeck,
            int[] selfCards,
            int battleSeed,
            bool selfGoesFirst)
        {
            return new JObject
            {
                ["uri"] = "Matched",
                ["bid"] = room.RoomId,
                ["turnState"] = selfGoesFirst ? 0 : 1,
                ["selfInfo"] = CreateBattleInfo(target, selfDeck, source, opponentDeck, battleSeed),
                ["oppoInfo"] = CreateBattleInfo(source, opponentDeck, target, selfDeck, battleSeed),
                ["selfDeck"] = CreateDeckData(selfCards)
            };
        }

        private static JObject CreateBattleStartPayload(
            GameRoom room,
            Player target,
            Player source,
            PlayerDeck selfDeck,
            PlayerDeck opponentDeck,
            int battleSeed)
        {
            return new JObject
            {
                ["uri"] = "BattleStart",
                ["bid"] = room.RoomId,
                ["battleStartDate"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["selfInfo"] = CreateBattleInfo(target, selfDeck, source, opponentDeck, battleSeed),
                ["oppoInfo"] = CreateBattleInfo(source, opponentDeck, target, selfDeck, battleSeed)
            };
        }

        private static JObject CreateBattleInfo(
            Player player,
            PlayerDeck deck,
            Player opponent,
            PlayerDeck opponentDeck,
            int battleSeed)
        {
            return new JObject
            {
                ["viewerId"] = player.ViewerId,
                ["oppoId"] = opponent.ViewerId,
                ["userName"] = string.IsNullOrEmpty(player.Name) ? "Player" : player.Name,
                ["rank"] = player.Rank,
                ["battlePoint"] = player.BattlePoint,
                ["masterPoint"] = player.MasterPoint,
                ["isMasterRank"] = player.MasterPoint > 0 ? 1 : 0,
                ["classId"] = deck?.ClanId ?? 0,
                ["subclassId"] = deck?.SubclassId ?? 10,
                ["charaId"] = deck?.CharaId ?? 0,
                ["rotationId"] = deck?.RotationId ?? string.Empty,
                ["sleeveId"] = deck?.SleeveId ?? 0,
                ["emblemId"] = player.EmblemId,
                ["degreeId"] = player.DegreeId,
                ["country_code"] = player.CountryCode ?? string.Empty,
                ["isOfficial"] = player.IsOfficial,
                ["fieldId"] = 1,
                ["seed"] = battleSeed,
                ["deckCount"] = deck?.CardIds?.Length ?? 0,
                ["oppoDeckCount"] = opponentDeck?.CardIds?.Length ?? 0
            };
        }

        private static JArray CreateDeckData(int[] cards)
        {
            var result = new JArray();
            if (cards == null)
                return result;
            for (int i = 0; i < cards.Length; i++)
            {
                result.Add(new JObject
                {
                    ["idx"] = i + 1,
                    ["cardId"] = cards[i]
                });
            }
            return result;
        }

        private static bool TryParseDeckSnapshot(
            JObject snapshot,
            out PlayerDeck deck,
            out string error)
        {
            deck = null;
            error = null;
            try
            {
                JArray cards = snapshot["cardIds"] as JArray;
                if (cards == null || cards.Count < 6 || cards.Count > 60)
                {
                    error = "cardIds must contain between 6 and 60 cards";
                    return false;
                }

                var ids = new List<int>(cards.Count);
                foreach (JToken card in cards)
                {
                    int id = card.Value<int>();
                    if (id <= 0)
                    {
                        error = "cardIds contains an invalid card id";
                        return false;
                    }
                    ids.Add(id);
                }
                deck = new PlayerDeck
                {
                    CardIds = ids.ToArray(),
                    ClanId = snapshot["classId"]?.Value<int>() ?? 0,
                    SubclassId = snapshot["subclassId"]?.Value<int>() ?? 10,
                    CharaId = snapshot["charaId"]?.Value<int>() ?? 0,
                    SkinId = snapshot["skinId"]?.Value<int>() ?? 0,
                    SleeveId = snapshot["sleeveId"]?.Value<int>() ?? 0,
                    RotationId = snapshot["rotationId"]?.Value<string>() ?? string.Empty
                };
                return deck.ClanId > 0;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private JObject CreateAliveResponse(SocketIoConnection target, GameRoom room)
        {
            bool hasOpponent = false;
            foreach (SocketIoConnection peer in GetRoomPeers(target))
            {
                hasOpponent = true;
                break;
            }

            return new JObject
            {
                ["uri"] = "Gungnir",
                ["scs"] = "ONLINE",
                ["ocs"] = hasOpponent ? "ONLINE" : "WAITING"
            };
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                    client.NoDelay = true;
                    HandleClient(client);
                }
                catch (SocketException)
                {
                    client?.Close();
                    if (_running)
                        Plugin.Logger.LogWarning("[SocketIO] Listener stopped unexpectedly");
                    break;
                }
                catch (ObjectDisposedException)
                {
                    client?.Close();
                    break;
                }
                catch (Exception ex)
                {
                    client?.Close();
                    if (_running)
                        Plugin.Logger.LogError($"[SocketIO] Listener error: {ex.Message}");
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            NetworkStream stream = null;
            try
            {
                stream = client.GetStream();
                client.ReceiveTimeout = 10000;
                string header = ReadHttpHeader(stream);
                if (string.IsNullOrEmpty(header))
                    return;

                string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
                string requestLine = lines.Length > 0 ? lines[0] : string.Empty;
                string requestTarget = ExtractRequestTarget(requestLine);
                var headers = ParseHeaders(lines);
                string upgrade = GetHeader(headers, "Upgrade");
                string connectionHeader = GetHeader(headers, "Connection");
                string websocketKey = GetHeader(headers, "Sec-WebSocket-Key");
                string websocketVersion = GetHeader(headers, "Sec-WebSocket-Version");
                bool isWebSocketRequest = string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase) &&
                    connectionHeader.IndexOf("upgrade", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    !string.IsNullOrEmpty(websocketKey);

                Plugin.Logger.LogInfo($"[SocketIO] HTTP request: {requestLine}; target={requestTarget}; websocket={isWebSocketRequest}; upgrade={upgrade}; connection={connectionHeader}; key={(string.IsNullOrEmpty(websocketKey) ? "missing" : "present")}; version={websocketVersion}");
                if (!isWebSocketRequest)
                {
                    WriteHttpError(stream, 400, "Bad Request");
                    return;
                }

                string accept = ComputeWebSocketAccept(websocketKey);
                string response = "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    "Sec-WebSocket-Accept: " + accept + "\r\n" +
                    "\r\n";
                byte[] responseBytes = Encoding.ASCII.GetBytes(response);
                stream.Write(responseBytes, 0, responseBytes.Length);
                stream.Flush();
                client.ReceiveTimeout = 0;
                Plugin.Logger.LogInfo("[SocketIO] WebSocket handshake accepted");

                var socket = new RawWebSocket(client);
                client = null;
                // RawWebSocket now owns both the TcpClient and its stream.
                stream = null;
                var connection = new SocketIoConnection(socket, ExtractQuery(requestTarget), this);
                connection.PacketReceived += OnSocketPacket;
                connection.Closed += OnSocketClosed;
                connection.Start();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[SocketIO] WebSocket handshake failed: {ex.Message}");
                if (stream != null)
                {
                    try
                    {
                        WriteHttpError(stream, 500, "Internal Server Error");
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                client?.Close();
                stream?.Dispose();
            }
        }

        private IEnumerable<SocketIoConnection> GetRoomPeers(SocketIoConnection source)
        {
            var result = new List<SocketIoConnection>();
            lock (_connectionLock)
            {
                foreach (SocketIoConnection connection in _connections.Values)
                {
                    if (connection != source &&
                        connection.IsOpen &&
                        string.Equals(connection.BattleId, source.BattleId, StringComparison.Ordinal))
                    {
                        result.Add(connection);
                    }
                }
            }
            return result;
        }

        private SocketIoConnection FindActiveConnection(string battleId, string playerId)
        {
            if (string.IsNullOrEmpty(battleId) || string.IsNullOrEmpty(playerId))
                return null;

            lock (_connectionLock)
            {
                foreach (SocketIoConnection connection in _connections.Values)
                {
                    if (connection.IsOpen &&
                        string.Equals(connection.BattleId, battleId, StringComparison.Ordinal) &&
                        string.Equals(connection.PlayerId, playerId, StringComparison.Ordinal))
                    {
                        return connection;
                    }
                }
            }
            return null;
        }

        internal bool ResendHostOpponentSnapshot()
        {
            SocketIoConnection hostConnection = null;
            lock (_connectionLock)
            {
                foreach (SocketIoConnection connection in _connections.Values)
                {
                    if (connection.IsOpen && connection.IsHost)
                    {
                        hostConnection = connection;
                        break;
                    }
                }
            }

            if (hostConnection == null)
                return false;

            GameRoom room = _rooms.GetRoom(hostConnection.BattleId);
            if (!HasOpponent(hostConnection, room))
                return false;

            SendOpponentSnapshot(hostConnection, room);
            return true;
        }

        private static Player FindPlayer(GameRoom room, string playerId)
        {
            return string.IsNullOrEmpty(playerId) ? null : room.GetPlayer(playerId);
        }

        private Player FindConnectionPlayer(GameRoom room, string battleId, int? queryViewerId)
        {
            if (room == null || string.IsNullOrEmpty(battleId))
                return null;

            bool hasRoomConnection = false;
            var boundPlayerIds = new HashSet<string>(StringComparer.Ordinal);
            lock (_connectionLock)
            {
                foreach (SocketIoConnection connection in _connections.Values)
                {
                    if (connection.IsOpen &&
                        string.Equals(connection.BattleId, battleId, StringComparison.Ordinal))
                    {
                        hasRoomConnection = true;
                        if (!string.IsNullOrEmpty(connection.PlayerId))
                            boundPlayerIds.Add(connection.PlayerId);
                    }
                }
            }

            List<Player> players = room.GetPlayers();

            // A reconnect carries the same encrypted viewerId as the
            // original native agent. Prefer the matching unbound slot when a
            // profile has already been observed in this room.
            if (queryViewerId.HasValue && queryViewerId.Value > 0)
            {
                foreach (Player player in players)
                {
                    if (player.ViewerId == queryViewerId.Value &&
                        !boundPlayerIds.Contains(player.PlayerId))
                    {
                        return player;
                    }
                }
            }

            if (!hasRoomConnection)
            {
                // The first connection for a newly created room is the host.
                // Reuse the pre-created host slot without consulting the
                // game's original query viewerId.
                Player host = players.Find(player => player.IsHost &&
                    !boundPlayerIds.Contains(player.PlayerId));
                if (host != null)
                    return host;
            }

            // If the host is currently connected, a new connection occupies
            // the unbound guest slot. If only the guest remains connected,
            // the host slot is the only valid reconnect target.
            bool hostConnected = false;
            bool guestConnected = false;
            foreach (Player player in players)
            {
                if (!boundPlayerIds.Contains(player.PlayerId))
                    continue;
                if (player.IsHost)
                    hostConnected = true;
                else
                    guestConnected = true;
            }

            if (hostConnected && !guestConnected)
            {
                Player guest = players.Find(player => !player.IsHost &&
                    !boundPlayerIds.Contains(player.PlayerId));
                if (guest != null)
                    return guest;
            }

            if (guestConnected && !hostConnected)
            {
                Player host = players.Find(player => player.IsHost &&
                    !boundPlayerIds.Contains(player.PlayerId));
                if (host != null)
                    return host;
            }

            // Preserve the normal host-then-guest order for a partially
            // initialized room. This also handles a stale connection entry
            // that has disappeared between the two snapshots above.
            foreach (Player player in players)
            {
                if (player.IsHost && !boundPlayerIds.Contains(player.PlayerId))
                    return player;
            }
            foreach (Player player in players)
            {
                if (!player.IsHost && !boundPlayerIds.Contains(player.PlayerId))
                    return player;
            }
            return null;
        }

        private bool IsPlayerConnected(string playerId, string battleId)
        {
            if (string.IsNullOrEmpty(playerId) || string.IsNullOrEmpty(battleId))
                return false;

            lock (_connectionLock)
            {
                foreach (SocketIoConnection connection in _connections.Values)
                {
                    if (connection.IsOpen &&
                        string.Equals(connection.BattleId, battleId, StringComparison.Ordinal) &&
                        string.Equals(connection.PlayerId, playerId, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private bool SendRoutedMessage(SocketIoConnection target, RoutedMessage routed)
        {
            if (target == null || routed == null)
                return false;

            string eventName = routed.EventName == "hand" ? "hand" : "synchronize";
            return target.SendBinaryEvent(eventName, routed.Payload);
        }

        private void FlushPendingMessages(SocketIoConnection connection, GameRoom room)
        {
            if (connection == null || room == null || string.IsNullOrEmpty(connection.PlayerId))
                return;

            IList<RoutedMessage> pending = _messageRouter.TakePendingForTarget(
                room.RoomId,
                connection.PlayerId);
            foreach (RoutedMessage routed in pending)
            {
                if (!SendRoutedMessage(connection, routed))
                    break;
                _messageRouter.MarkDelivered(room.RoomId, routed);
            }
        }

        private static int? TryGetQueryViewerId(Dictionary<string, string> query)
        {
            if (query == null)
                return null;

            string encrypted = GetQuery(query, "viewerId", "ViewerId");
            if (string.IsNullOrWhiteSpace(encrypted))
                return null;

            try
            {
                string decrypted = CryptAES.decryptForNode(encrypted);
                return int.TryParse(decrypted, out int viewerId) && viewerId > 0
                    ? (int?)viewerId
                    : null;
            }
            catch
            {
                // This is only an identity hint. A malformed or legacy query
                // must not prevent slot binding through room state/profile.
                return null;
            }
        }

        private void SendOpponentSnapshot(SocketIoConnection target, GameRoom room)
        {
            Player opponent = FindOpponent(target, room);
            if (opponent == null)
                return;

            JToken snapshot = null;
            foreach (SocketIoConnection peer in GetRoomPeers(target))
            {
                if (string.Equals(peer.PlayerId, opponent.PlayerId, StringComparison.Ordinal))
                {
                    snapshot = peer.LastRoomEntry;
                    break;
                }
            }

            target.SendBinaryEvent(
                "synchronize",
                SocketIoPayloadCodec.Encode(snapshot ?? CreatePlayerSnapshot(opponent)));
            Plugin.Logger.LogInfo(
                $"[SocketIO] Sent opponent snapshot to {target.SessionId}: " +
                $"name={opponent.Name}, viewerId={opponent.ViewerId}, isHost={opponent.IsHost}");
        }

        private static bool HasOpponent(SocketIoConnection target, GameRoom room)
        {
            return FindOpponent(target, room) != null;
        }

        private static Player FindOpponent(SocketIoConnection target, GameRoom room)
        {
            if (target == null || room == null)
                return null;

            foreach (Player player in room.GetPlayers())
            {
                if (player.PlayerId != target.PlayerId)
                    return player;
            }
            return null;
        }

        private static JObject CreateRoomEntryWithProfile(JToken roomEntry, Player player)
        {
            JObject result = roomEntry as JObject == null
                ? CreatePlayerSnapshot(player)
                : (JObject)roomEntry.DeepClone();

            result["uri"] = "RoomEntry";
            result["isSelf"] = 0;
            result["userName"] = string.IsNullOrEmpty(player.Name) ? "Player" : player.Name;
            result["oppoId"] = player.ViewerId;
            result["battlePoint"] = player.BattlePoint;
            result["degreeId"] = player.DegreeId;
            result["emblemId"] = player.EmblemId;
            result["countryCode"] = player.CountryCode ?? string.Empty;
            result["rank"] = player.Rank;
            result["maxRank"] = player.Rank;
            result["isOfficial"] = player.IsOfficial ? 1 : 0;
            if (result["battleNum"] == null)
                result["battleNum"] = 1;
            if (result["ownerWin"] == null)
                result["ownerWin"] = 0;
            if (result["guestWin"] == null)
                result["guestWin"] = 0;
            return result;
        }

        private static JObject CreatePlayerSnapshot(Player player)
        {
            return new JObject
            {
                ["uri"] = "RoomEntry",
                ["isSelf"] = 0,
                ["userName"] = string.IsNullOrEmpty(player.Name) ? "Player" : player.Name,
                ["oppoId"] = player.ViewerId,
                ["battlePoint"] = player.BattlePoint,
                ["masterPoint"] = player.MasterPoint,
                ["degreeId"] = player.DegreeId,
                ["emblemId"] = player.EmblemId,
                ["countryCode"] = player.CountryCode ?? string.Empty,
                ["country_code"] = player.CountryCode ?? string.Empty,
                ["rank"] = player.Rank,
                ["maxRank"] = player.Rank,
                ["isOfficial"] = player.IsOfficial ? 1 : 0,
                ["isFriend"] = 0,
                ["isGuildMember"] = 0,
                ["isGuildJoined"] = 0,
                ["battleNum"] = 1,
                ["ownerWin"] = 0,
                ["guestWin"] = 0
            };
        }

        private static bool ApplyPlayerProfile(Player player, JToken message)
        {
            if (player == null || !(message is JObject data))
                return false;

            if (data["profileViewerId"] != null)
                player.ViewerId = data["profileViewerId"].Value<int>();
            else if (data["oppoId"] != null)
                player.ViewerId = data["oppoId"].Value<int>();
            if (data["userName"] != null)
                player.Name = data["userName"].Value<string>();
            if (data["rank"] != null)
                player.Rank = data["rank"].Value<int>();
            if (data["battlePoint"] != null)
                player.BattlePoint = data["battlePoint"].Value<int>();
            if (data["masterPoint"] != null)
                player.MasterPoint = data["masterPoint"].Value<int>();
            if (data["degreeId"] != null)
                player.DegreeId = data["degreeId"].Value<int>();
            if (data["emblemId"] != null)
                player.EmblemId = data["emblemId"].Value<long>();
            if (data["countryCode"] != null)
                player.CountryCode = data["countryCode"].Value<string>() ?? string.Empty;
            if (data["isOfficial"] != null)
                player.IsOfficial = data["isOfficial"].Type == JTokenType.Boolean
                    ? data["isOfficial"].Value<bool>()
                    : data["isOfficial"].Value<int>() != 0;
            return true;
        }

        private static bool HasEmbeddedProfile(JToken message)
        {
            return message is JObject data && data["profileViewerId"] != null;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query))
                return result;
            foreach (string part in query.TrimStart('?').Split('&'))
            {
                if (string.IsNullOrEmpty(part))
                    continue;
                string[] pair = part.Split(new[] { '=' }, 2);
                string key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
                string value = pair.Length == 1 ? string.Empty : Uri.UnescapeDataString(pair[1].Replace('+', ' '));
                result[key] = value;
            }
            return result;
        }

        private static string GetQuery(Dictionary<string, string> query, params string[] names)
        {
            foreach (string name in names)
            {
                if (query.TryGetValue(name, out string value) && !string.IsNullOrEmpty(value))
                    return value;
            }
            return null;
        }

        private string BuildPrefix()
        {
            string host = string.IsNullOrWhiteSpace(_config.BindAddress) || _config.BindAddress == "0.0.0.0"
                ? "127.0.0.1"
                : _config.BindAddress;
            string path = string.IsNullOrWhiteSpace(_config.SocketPath) ? "/socket.io/" : _config.SocketPath;
            if (!path.StartsWith("/", StringComparison.Ordinal))
                path = "/" + path;
            if (!path.EndsWith("/", StringComparison.Ordinal))
                path += "/";
            return $"http://{host}:{_config.Port}{path}";
        }

        private static IPAddress ResolveBindAddress(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "0.0.0.0" || value == "+")
                return IPAddress.Any;
            if (value == "::")
                return IPAddress.IPv6Any;
            if (IPAddress.TryParse(value, out IPAddress address))
                return address;

            foreach (IPAddress candidate in Dns.GetHostAddresses(value))
            {
                if (candidate.AddressFamily == AddressFamily.InterNetwork)
                    return candidate;
            }
            return IPAddress.Any;
        }

        private static string ReadHttpHeader(NetworkStream stream)
        {
            var bytes = new List<byte>(1024);
            while (bytes.Count < 16 * 1024)
            {
                int value = stream.ReadByte();
                if (value < 0)
                    break;
                bytes.Add((byte)value);
                int count = bytes.Count;
                if (count >= 4 && bytes[count - 4] == 13 && bytes[count - 3] == 10 &&
                    bytes[count - 2] == 13 && bytes[count - 1] == 10)
                    return Encoding.ASCII.GetString(bytes.ToArray());
            }
            throw new InvalidDataException("Invalid or oversized HTTP upgrade request");
        }

        private static Dictionary<string, string> ParseHeaders(string[] lines)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0)
                    continue;
                string name = lines[i].Substring(0, colon).Trim();
                string value = lines[i].Substring(colon + 1).Trim();
                headers[name] = value;
            }
            return headers;
        }

        private static string GetHeader(Dictionary<string, string> headers, string name)
        {
            return headers.TryGetValue(name, out string value) ? value : string.Empty;
        }

        private static string ExtractRequestTarget(string requestLine)
        {
            if (string.IsNullOrEmpty(requestLine))
                return string.Empty;
            int start = requestLine.IndexOf(' ');
            if (start < 0)
                return string.Empty;
            start++;
            int end = requestLine.LastIndexOf(" HTTP/", StringComparison.OrdinalIgnoreCase);
            if (end <= start)
            {
                end = requestLine.IndexOf(' ', start);
                if (end < 0)
                    end = requestLine.Length;
            }
            return requestLine.Substring(start, end - start);
        }

        private static string ExtractQuery(string target)
        {
            int index = target == null ? -1 : target.IndexOf('?');
            return index >= 0 ? target.Substring(index) : string.Empty;
        }

        private static string ComputeWebSocketAccept(string key)
        {
            byte[] input = Encoding.ASCII.GetBytes((key ?? string.Empty).Trim() + WebSocketMagic);
            using (var sha1 = SHA1.Create())
                return Convert.ToBase64String(sha1.ComputeHash(input));
        }

        private static void WriteHttpError(NetworkStream stream, int statusCode, string reason)
        {
            string response = $"HTTP/1.1 {statusCode} {reason}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
            byte[] bytes = Encoding.ASCII.GetBytes(response);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
