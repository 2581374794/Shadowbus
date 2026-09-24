using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Shadowbus.Server.SocketIO
{
    /// <summary>
    /// One Engine.IO WebSocket connection. Framing is handled by RawWebSocket
    /// because Unity's Mono runtime does not implement HttpListener WebSockets.
    /// </summary>
    public sealed class SocketIoConnection : IDisposable
    {
        private readonly RawWebSocket _socket;
        private readonly SocketIoServer _server;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly object _sendLock = new object();
        private PendingBinaryEvent _pendingBinary;
        private int _closed;

        internal SocketIoConnection(RawWebSocket socket, string query, SocketIoServer server)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _server = server ?? throw new ArgumentNullException(nameof(server));
            Query = query ?? string.Empty;
            SessionId = Guid.NewGuid().ToString("N");
        }

        public string SessionId { get; }
        public string Query { get; }
        public string BattleId { get; internal set; }
        public string PlayerId { get; internal set; }
        public bool IsHost { get; internal set; }
        internal JToken LastRoomEntry { get; set; }
        internal JToken LastProfile { get; set; }
        public bool IsOpen => Volatile.Read(ref _closed) == 0 && _socket.IsOpen;

        /// <summary>
        /// 最后一次从这个连接收到任何数据的时刻（UTC）。
        ///
        /// 客户端每 2 秒就会和服务器对一次 Engine.IO 心跳（它收到我们的 ping 会回 pong，
        /// 自己也会发 ping），战斗里还有每 5 秒一次的 Gungnir。所以这个时间戳是很可靠的
        /// 存活指示：**已经建立了连接、但对端悄悄消失（拔网线、VPN 掉线、进程被杀，
        /// 没有 FIN/RST）时，TCP 层不会告诉我们，只有这里能看出来。**
        /// </summary>
        public DateTime LastReceivedUtc { get; private set; } = DateTime.UtcNow;

        /// <summary>
        /// 这次关闭是"被重连的新连接顶掉"（对端其实还活着，只是旧 socket 哑了）。
        /// 置位时服务器**不要**按掉线处理：不标记玩家断线、也不结算对局。
        /// </summary>
        internal bool SupersededByReconnect { get; set; }

        /// <summary>这个连接静默了多少秒。</summary>
        public double IdleSeconds => (DateTime.UtcNow - LastReceivedUtc).TotalSeconds;

        internal void MarkReceived()
        {
            LastReceivedUtc = DateTime.UtcNow;
        }

        /// <summary>日志用的一行摘要（谁、什么角色、静默多久）。</summary>
        internal string Describe()
        {
            string role = string.IsNullOrEmpty(BattleId)
                ? "unbound"
                : (IsHost ? "host" : "guest");
            return $"{SessionId} role={role} player={PlayerId ?? "<none>"} idle={IdleSeconds:F1}s";
        }

        internal event Action<SocketIoConnection, string, JToken, byte[], int?> PacketReceived;
        internal event Action<SocketIoConnection, string> Closed;

        internal void Start()
        {
            // The bundled BestHTTP transport hard-codes EIO=4.
            SendText("0" + JsonConvert.SerializeObject(new
            {
                sid = SessionId,
                upgrades = new string[0],
                pingInterval = 2000,
                pingTimeout = 5000
            }));

            // Open the default Socket.IO namespace.
            SendText("40");
            _server.OnSocketConnected(this);

            Task.Factory.StartNew(
                HeartbeatLoop,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Task.Factory.StartNew(
                ReceiveLoop,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public void SendEvent(string eventName, object payload = null)
        {
            if (string.IsNullOrEmpty(eventName) || !IsOpen)
                return;

            var packet = new JArray { eventName };
            if (payload != null)
                packet.Add(JToken.FromObject(payload));
            SendText("42" + packet.ToString(Formatting.None));
        }

        public bool SendBinaryEvent(string eventName, byte[] payload)
        {
            if (string.IsNullOrEmpty(eventName) || payload == null || !IsOpen)
                return false;

            LogOutgoingBinary(eventName, payload);

            var packet = new JArray
            {
                eventName,
                new JObject
                {
                    ["_placeholder"] = true,
                    ["num"] = 0
                }
            };
            byte[] binary = new byte[payload.Length + 1];
            binary[0] = 4;
            Buffer.BlockCopy(payload, 0, binary, 1, payload.Length);

            // A binary Socket.IO packet is a text header immediately followed
            // by its attachment. Keep both writes under one lock so another
            // event cannot insert a second header between them and confuse
            // the client's attachment matcher.
            try
            {
                lock (_sendLock)
                {
                    if (!IsOpen)
                        return false;
                    _socket.SendText(Encoding.UTF8.GetBytes(
                        "451-" + packet.ToString(Formatting.None)));
                    _socket.SendBinary(binary);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[SocketIO] Binary event send failed ({SessionId}): {ex.Message}");
                Close("send_failed");
                return false;
            }
            return IsOpen;
        }

        public void SendAck(int packetId, object payload = null, bool binary = false)
        {
            if (!IsOpen)
                return;

            Plugin.Logger.LogInfo(
                $"[SocketIO] OUT ack to {SessionId} role={(IsHost ? "host" : "guest")}, packetId={packetId}");

            var packet = new JArray();
            if (payload != null)
                packet.Add(JToken.FromObject(payload));
            // Binary event ACKs use Socket.IO packet type 6 and retain the
            // zero-attachment marker (`460-`) even when the ACK payload is
            // JSON-only. Text events use the ordinary packet type 3.
            string prefix = binary ? "460-" : "43";
            SendText(prefix + packetId + packet.ToString(Formatting.None));
        }

        internal void SendText(string text)
        {
            if (!IsOpen || string.IsNullOrEmpty(text))
                return;

            try
            {
                lock (_sendLock)
                    _socket.SendText(Encoding.UTF8.GetBytes(text));
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[SocketIO] Text send failed ({SessionId}): {ex.Message}");
                Close("send_failed");
            }
        }

        private void LogOutgoingBinary(string eventName, byte[] payload)
        {
            if (SocketIoPayloadCodec.TryDecodeQuiet(
                    payload,
                out Newtonsoft.Json.Linq.JToken message,
                out int sequence,
                out string uri))
            {
                string fields = SocketIoPayloadCodec.DescribeSequenceFields(message, sequence);
                string structure = SocketIoPayloadCodec.DescribeBattleStructure(message);
                string hiddenStructure = SocketIoPayloadCodec.DescribeHiddenConditionStructure(uri, message);
                Plugin.Logger.LogInfo(
                    $"[SocketIO] OUT {eventName} {uri ?? "<unknown>"} to {SessionId} " +
                    $"role={(IsHost ? "host" : "guest")}, seq={sequence}, fields={fields}, " +
                    $"shape={structure}, bytes={payload.Length}" +
                    (string.IsNullOrEmpty(hiddenStructure) ? string.Empty : ", hidden=" + hiddenStructure));
            }
            else
            {
                Plugin.Logger.LogInfo(
                    $"[SocketIO] OUT {eventName} <opaque> to {SessionId} " +
                    $"role={(IsHost ? "host" : "guest")}, bytes={payload.Length}");
            }
        }

        private void HeartbeatLoop()
        {
            // Engine.IO v4 servers send a ping at the advertised interval.
            // The stock client answers with a pong (packet 3).
            while (!_stop.IsCancellationRequested && IsOpen)
            {
                if (_stop.Token.WaitHandle.WaitOne(2000))
                    break;
                SendText("2");
            }
        }

        private void ReceiveLoop()
        {
            try
            {
                while (!_stop.IsCancellationRequested && IsOpen)
                {
                    if (!_socket.TryReceive(out bool isBinary, out byte[] payload))
                        return;

                    // 任何收到的字节都算"对端还活着"（含 Engine.IO 的 ping/pong 与心跳）。
                    MarkReceived();

                    if (payload == null || payload.Length == 0)
                        continue;

                    if (isBinary)
                        HandleBinaryPacket(payload);
                    else
                        HandleTextPacket(Encoding.UTF8.GetString(payload));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[SocketIO] Receive failed ({SessionId}): {ex.Message}");
            }
            finally
            {
                Close("receive_loop_stopped");
            }
        }

        private void HandleTextPacket(string packet)
        {
            if (packet == "1")
            {
                Close("engine_close");
                return;
            }

            if (packet.StartsWith("2probe", StringComparison.Ordinal))
            {
                SendText("3probe");
                return;
            }

            if (packet == "2")
            {
                SendText("3");
                return;
            }

            if (packet == "3")
                return;

            if (packet == "5")
                return;

            if (packet == "40")
            {
                _server.OnSocketConnected(this);
                return;
            }

            if (packet == "41")
            {
                Close("socketio_disconnect");
                return;
            }

            if (packet.StartsWith("42", StringComparison.Ordinal))
            {
                ParseEvent(packet.Substring(2), null, null);
                return;
            }

            if (packet.StartsWith("43", StringComparison.Ordinal))
            {
                int index = 2;
                while (index < packet.Length && char.IsDigit(packet[index]))
                    index++;
                if (int.TryParse(packet.Substring(2, index - 2), out int packetId))
                    ParseEvent(packet.Substring(index), null, packetId);
                return;
            }

            if (packet.StartsWith("45", StringComparison.Ordinal) ||
                packet.StartsWith("46", StringComparison.Ordinal))
            {
                ParseBinaryEvent(
                    packet.Substring(2),
                    packet.StartsWith("46", StringComparison.Ordinal));
                return;
            }

            Plugin.Logger.LogDebug($"[SocketIO] Unhandled text packet ({SessionId}): {packet.Substring(0, Math.Min(packet.Length, 96))}");
        }

        private void ParseEvent(string json, byte[] binary, int? packetId)
        {
            try
            {
                JArray array = JArray.Parse(json);
                if (array.Count == 0)
                    return;
                string eventName = array[0]?.Value<string>();
                JToken payload = array.Count > 1 ? array[1] : null;
                PacketReceived?.Invoke(this, eventName, payload, binary, packetId);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[SocketIO] Invalid event packet ({SessionId}): {ex.Message}");
            }
        }

        private void ParseBinaryEvent(string packet, bool isAck)
        {
            try
            {
                int dash = packet.IndexOf('-');
                if (dash < 0)
                    return;

                if (!int.TryParse(packet.Substring(0, dash), out int attachmentCount) ||
                    attachmentCount < 0)
                {
                    return;
                }

                // Socket.IO's binary packet header is:
                //   <attachments>-<namespace>,<ack id><json payload>
                // The ACK id is after the dash, unlike a normal packet. The
                // previous parser looked before the dash and silently lost
                // every callback id on binary emits.
                int prefixEnd = dash + 1;
                int arrayStart = packet.IndexOf('[', prefixEnd);
                if (arrayStart < 0)
                    return;

                JArray array = JArray.Parse(packet.Substring(arrayStart));
                if (array.Count == 0)
                    return;

                // The game server never emits an event that requires a
                // client-side ACK, so a client BinaryAck has no callback to
                // dispatch here. Clear any stale state regardless of whether
                // the peer included attachments.
                if (isAck)
                {
                    _pendingBinary = null;
                    return;
                }

                // A binary event with zero attachments has no binary payload
                // for this endpoint to decode. Do not leave a stale pending
                // attachment that could consume the next binary frame.
                if (attachmentCount == 0)
                {
                    _pendingBinary = null;
                    return;
                }

                _pendingBinary = new PendingBinaryEvent
                {
                    EventName = array[0]?.Value<string>(),
                    Payload = array.Count > 1 ? array[1] : null,
                    PacketId = ParsePacketId(packet, prefixEnd, arrayStart)
                };
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[SocketIO] Invalid binary event header ({SessionId}): {ex.Message}");
            }
        }

        private void HandleBinaryPacket(byte[] binary)
        {
            if (_pendingBinary == null)
                return;

            byte[] payload = binary;
            if (payload.Length > 0 && payload[0] == 4)
            {
                payload = new byte[payload.Length - 1];
                Buffer.BlockCopy(binary, 1, payload, 0, payload.Length);
            }

            PendingBinaryEvent pending = _pendingBinary;
            _pendingBinary = null;
            PacketReceived?.Invoke(this, pending.EventName, pending.Payload, payload, pending.PacketId);
        }

        private static int? ParsePacketId(string packet, int start, int end)
        {
            if (packet == null || start < 0 || end <= start || end > packet.Length)
                return null;

            int index = start;
            if (packet[index] == '/')
            {
                int comma = packet.IndexOf(',', index, end - index);
                if (comma < 0)
                    return null;
                index = comma + 1;
            }

            int idStart = index;
            while (index < end && char.IsDigit(packet[index]))
                index++;
            if (index == idStart)
                return null;

            int id;
            return int.TryParse(packet.Substring(idStart, index - idStart), out id)
                ? id
                : (int?)null;
        }

        public void Close(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;

            try
            {
                _stop.Cancel();
                _socket.Close();
            }
            catch
            {
            }
            finally
            {
                Closed?.Invoke(this, reason ?? "closed");
            }
        }

        public void Dispose()
        {
            Close("disposed");
            _stop.Dispose();
            _socket.Dispose();
        }

        private sealed class PendingBinaryEvent
        {
            public string EventName;
            public JToken Payload;
            public int? PacketId;
        }
    }
}
