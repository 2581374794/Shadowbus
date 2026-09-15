using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Cute;
using Newtonsoft.Json.Linq;
using Shadowbus.Server.Core;
using Shadowbus.Server.Network;
using Shadowbus.Server.Room;
using Wizard;

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
        private const int ResultLifeWin = 101;
        private const int ResultLifeLose = 102;
        private const int ResultDeckoutWin = 103;
        private const int ResultDeckoutLose = 104;
        private const int ResultRetireWin = 105;
        private const int ResultRetireLose = 106;
        private const int ResultSpecialWin = 107;
        private const int ResultSpecialLose = 108;
        private const int ResultDisconnectWin = 201;
        private const int ResultDisconnectLose = 202;
        private const int ResultFirstcardWin = 203;
        private const int ResultFirstcardLose = 204;
        private const int ResultTurnendWin = 205;
        private const int ResultTurnendLose = 206;
        private const int ResultTurnstartWin = 207;
        private const int ResultTurnstartLose = 208;
        private const int ResultNoContest = 1;

        private readonly ServerConfig _config;
        private readonly RoomManager _rooms;
        private readonly Dictionary<string, SocketIoConnection> _connections =
            new Dictionary<string, SocketIoConnection>();
        private readonly HashSet<string> _roomReadySent =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _pendingRoomReentries =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly RealtimeMessageRouter _messageRouter =
            new RealtimeMessageRouter();
        private readonly object _connectionLock = new object();
        private TcpListener _listener;
        private Thread _listenerThread;
        private volatile bool _running;
        private CardMaster _cardMaster;

        public SocketIoServer(ServerConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _rooms = new RoomManager();
        }

        public bool IsRunning => _running;
        public int Port => _config.Port;
        public string RoomCode { get; private set; }
        public RoomManager Rooms => _rooms;

        public void SetCardMaster(CardMaster cardMaster)
        {
            _cardMaster = cardMaster;
            _messageRouter.SetCardMaster(cardMaster);
        }

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
            // A host closing its room while a battle is active is an explicit
            // exit, not a transport failure. Finish the battle while sockets
            // are still open so both stock clients receive their terminal
            // result before the listener is torn down.
            FinishBattlesForServerStop();
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
            {
                _connections.Clear();
                _pendingRoomReentries.Clear();
            }
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
            string address = ResolveAdvertisedAddress();
            RoomCode = ConnectionCode.Generate(address, Port, roomId, hostProfile, roomRules);
            return RoomCode;
        }

        private string ResolveAdvertisedAddress()
        {
            if (!string.IsNullOrWhiteSpace(_config.AdvertisedAddress))
                return _config.AdvertisedAddress.Trim();

            // A specific bind address is already an address peers can use.
            // Wildcard binds (0.0.0.0/+) must be replaced with a real local
            // address before writing the connection code; 127.0.0.1 only
            // works for two clients on the same machine.
            if (!string.IsNullOrWhiteSpace(_config.BindAddress) &&
                !IsWildcardBindAddress(_config.BindAddress))
            {
                return _config.BindAddress.Trim();
            }

            string detected = DetectAdvertisedIpv4Address();
            if (!string.IsNullOrEmpty(detected))
            {
                Plugin.Logger.LogInfo(
                    $"[SocketIO] AdvertisedAddress is empty; using detected local address {detected}");
                return detected;
            }

            Plugin.Logger.LogWarning(
                "[SocketIO] Could not detect a non-loopback local address; " +
                "falling back to 127.0.0.1. Set SocketIO.AdvertisedAddress for remote/VPN play.");
            return "127.0.0.1";
        }

        private static bool IsWildcardBindAddress(string value)
        {
            return string.Equals(value, "0.0.0.0", StringComparison.Ordinal) ||
                string.Equals(value, "+", StringComparison.Ordinal) ||
                string.Equals(value, "::", StringComparison.Ordinal);
        }

        private static string DetectAdvertisedIpv4Address()
        {
            IPAddress bestAddress = null;
            int bestScore = int.MinValue;

            try
            {
                foreach (NetworkInterface networkInterface in
                    NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface == null ||
                        networkInterface.OperationalStatus != OperationalStatus.Up ||
                        networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    string adapterText =
                        (networkInterface.Name ?? string.Empty) + " " +
                        (networkInterface.Description ?? string.Empty);
                    int adapterScore = ScoreAdapter(adapterText);

                    IPInterfaceProperties properties;
                    try
                    {
                        properties = networkInterface.GetIPProperties();
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                    {
                        IPAddress address = unicast?.Address;
                        if (address == null || address.AddressFamily != AddressFamily.InterNetwork ||
                            IPAddress.IsLoopback(address))
                            continue;

                        byte[] bytes = address.GetAddressBytes();
                        // Ignore APIPA/link-local addresses; they are not
                        // reachable through a normal virtual LAN connection.
                        if (bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254)
                            continue;

                        int score = adapterScore + ScoreAddress(bytes);
                        if (bestAddress == null || score > bestScore)
                        {
                            bestAddress = address;
                            bestScore = score;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[SocketIO] Local address detection failed: {ex.Message}");
            }

            return bestAddress?.ToString();
        }

        private static int ScoreAdapter(string adapterText)
        {
            string value = (adapterText ?? string.Empty).ToLowerInvariant();
            int score = 0;
            string[] virtualKeywords =
            {
                "radmin", "hamachi", "zerotier", "tailscale", "wireguard",
                "openvpn", "vpn", "virtual"
            };
            for (int i = 0; i < virtualKeywords.Length; i++)
            {
                if (value.Contains(virtualKeywords[i]))
                {
                    score += 1000;
                    break;
                }
            }
            return score;
        }

        private static int ScoreAddress(byte[] bytes)
        {
            if (bytes == null || bytes.Length != 4)
                return 0;

            // Common virtual-LAN ranges: Radmin (26/8), Hamachi (25/8),
            // and Tailscale CGNAT (100.64/10).
            if (bytes[0] == 25 || bytes[0] == 26 ||
                (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127))
                return 300;

            if (bytes[0] == 10)
                return 120;
            if (bytes[0] == 192 && bytes[1] == 168)
                return 100;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return 80;
            return 20;
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

            if (reconnecting && room.State != RoomState.Finished &&
                !IsPendingRoomReentry(room.RoomId, connection.PlayerId))
            {
                // Preserve live recovery. Post-battle room agents must first
                // send Reenter, after reaching the native RoomReady status.
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
            string structure = SocketIoPayloadCodec.DescribeBattleStructure(message);
            string hiddenStructure = SocketIoPayloadCodec.DescribeHiddenConditionStructure(uri, message);
            Plugin.Logger.LogInfo(
                $"[SocketIO] IN {eventName} {uri ?? "<unknown>"} from {connection.SessionId} " +
                $"role={(connection.IsHost ? "host" : "guest")}, seq={sequence}, fields={sequenceFields}, " +
                $"shape={structure}, ack={(packetId.HasValue ? packetId.Value.ToString() : "none")}, " +
                $"bytes={binary.Length}" +
                (string.IsNullOrEmpty(hiddenStructure) ? string.Empty : ", hidden=" + hiddenStructure));

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

            if (uri == "Reenter")
            {
                JToken isRecovery = (message as JObject)?["isRecovery"];
                if (isRecovery != null && isRecovery.Type == JTokenType.Boolean &&
                    !isRecovery.Value<bool>())
                {
                    PrepareRoomForRematch(room);
                    if (TryHandleRematchReentry(connection, room, eventName, message, binary))
                        return;
                }
            }

            BattleSession battleSession = _messageRouter.GetOrCreate(room.RoomId);
            if (room.State == RoomState.InGame)
                battleSession?.RecordBattleEvent(connection.PlayerId, uri, message);

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

            // The stock battle flow sends Judge back to the client that sent
            // TurnEnd. NetworkOperationCollection.JudgeOperation then calls
            // ControlTurnStartPlayer on that same client. Routing Judge to
            // the opponent makes the player who ended the turn start another
            // local turn, leaving both clients on the wrong turn state.
            if (string.Equals(uri, "Judge", StringComparison.Ordinal))
            {
                // Judge is a server-generated loopback operation, but the
                // native client has one receive playSeq stream per socket,
                // not one stream per logical sender. Reserve its sequence on
                // the already-established opponent -> sender bridge; using a
                // fresh sender -> sender bridge would restart at playSeq=1
                // and StockReceiveMgr would discard the packet as stale.
                // The official response also marks the loopback recipient as
                // the active player. Without this field, a second-player
                // client keeps Matched.turnState=1 and its native
                // SendTurnEnd() guard suppresses the next TurnEnd.
                JToken judgeRelayMessage = relayMessage;
                JObject judgeObject = relayMessage as JObject;
                if (judgeObject != null)
                {
                    judgeRelayMessage = judgeObject.DeepClone();
                    ((JObject)judgeRelayMessage)["turnState"] = 0;
                }
                Player opponent = FindOpponent(connection, room);
                string sequenceSource = opponent == null
                    ? connection.PlayerId
                    : opponent.PlayerId;
                RoutedMessage routed = _messageRouter.CreateServerMessage(
                    room.RoomId,
                    sequenceSource,
                    connection.PlayerId,
                    eventName,
                    judgeRelayMessage);
                if (routed != null && !routed.IsDuplicate)
                {
                    bool sent = SendRoutedMessage(connection, routed);
                    if (sent)
                        _messageRouter.MarkDelivered(room.RoomId, routed);
                    Plugin.Logger.LogInfo(
                        $"[SocketIO] Routed Judge back to sender {connection.SessionId}; " +
                        $"sequenceSource={(opponent == null ? "self-fallback" : "opponent-stream")}, " +
                        $"sent={sent}, sourceSeq={routed.SourceSequence}, " +
                        $"deliverySeq={routed.DeliverySequence}");
                }
                return;
            }

            if (string.Equals(uri, "JudgeResult", StringComparison.Ordinal))
            {
                HandleJudgeResult(connection, room, relayMessage as JObject);
                return;
            }

            if (string.Equals(uri, "Retire", StringComparison.Ordinal))
            {
                RelayToOpponent(connection, room, eventName, relayMessage, message, binary);
                if (room.State == RoomState.InGame)
                {
                    FinishBattle(
                        room,
                        battleSession,
                        connection.PlayerId,
                        ResultRetireLose,
                        ResultRetireWin,
                        "retire");
                }
                return;
            }

            // Route against logical room slots rather than only currently
            // open sockets. A reliable frame received while the opponent is
            // reconnecting must stay in BattleSession.pending and be flushed
            // by Reenter once that slot binds again.
            RelayToOpponent(connection, room, eventName, relayMessage, message, binary);

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

                if (room.State == RoomState.InGame)
                {
                    FinishBattle(
                        room,
                        battleSession,
                        connection.PlayerId,
                        ResultRetireLose,
                        ResultRetireWin,
                        "room-exit");
                }
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

                if (_running && room.State == RoomState.InGame)
                {
                    FinishBattle(
                        room,
                        _messageRouter.GetOrCreate(room.RoomId),
                        connection.PlayerId,
                        ResultDisconnectLose,
                        ResultDisconnectWin,
                        "disconnect");
                }
            }

            Plugin.Logger.LogInfo($"[SocketIO] Client disconnected: {connection.SessionId} ({reason})");

            if (room != null && room.State != RoomState.Finished)
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

        private void HandleJudgeResult(
            SocketIoConnection connection,
            GameRoom room,
            JObject message)
        {
            if (connection == null || room == null ||
                (room.State != RoomState.InGame && room.State != RoomState.Finished))
                return;

            BattleSession session = _messageRouter.GetOrCreate(room.RoomId);
            if (session == null)
                return;

            if (session.TryGetOutcome(
                    out string existingWinner,
                    out int existingWinnerResult,
                    out string existingLoser,
                    out int existingLoserResult))
            {
                // A client may retry JudgeResult after a lost response. The
                // outcome is immutable; resend only that player's result
                // without creating a second finish event for the peer.
                if (string.Equals(connection.PlayerId, existingWinner, StringComparison.Ordinal))
                {
                    SendBattleFinishToPlayer(
                        session,
                        room,
                        existingWinner,
                        existingLoser,
                        existingWinnerResult);
                }
                else if (string.Equals(connection.PlayerId, existingLoser, StringComparison.Ordinal))
                {
                    SendBattleFinishToPlayer(
                        session,
                        room,
                        existingLoser,
                        existingWinner,
                        existingLoserResult);
                }
                return;
            }

            int status = -1;
            TryGetInt(message?["log"], out status);

            // A custom/native client may include the computed result in the
            // request. Trust it when it is one of the stock terminal codes.
            if (TryGetInt(message?["result"], out int reportedResult) &&
                IsTerminalResult(reportedResult))
            {
                if (IsWinResult(reportedResult))
                {
                    FinishBattle(
                        room,
                        session,
                        connection.PlayerId,
                        reportedResult,
                        OppositeResult(reportedResult),
                        "client-result");
                }
                else
                {
                    FinishBattle(
                        room,
                        session,
                        FindOpponent(connection, room)?.PlayerId,
                        OppositeResult(reportedResult),
                        reportedResult,
                        "client-result");
                }
                return;
            }

            switch (status)
            {
                case 300: // OppoDisconnectVictory
                    FinishBattle(
                        room,
                        session,
                        connection.PlayerId,
                        ResultDisconnectWin,
                        ResultDisconnectLose,
                        "disconnect-result");
                    return;
                case 301: // DisconnectLose
                    FinishBattle(
                        room,
                        session,
                        FindOpponent(connection, room)?.PlayerId,
                        ResultDisconnectWin,
                        ResultDisconnectLose,
                        "disconnect-result");
                    return;
                case 400: // OpponentNotTurnStartVictory
                    FinishBattle(
                        room,
                        session,
                        connection.PlayerId,
                        ResultTurnstartWin,
                        ResultTurnstartLose,
                        "turn-start-timeout");
                    return;
                case 401: // TurnStartLose
                    FinishBattle(
                        room,
                        session,
                        FindOpponent(connection, room)?.PlayerId,
                        ResultTurnstartWin,
                        ResultTurnstartLose,
                        "turn-start-timeout");
                    return;
                case 500: // OpponentNotTurnEndVictory
                    FinishBattle(
                        room,
                        session,
                        connection.PlayerId,
                        ResultTurnendWin,
                        ResultTurnendLose,
                        "turn-end-timeout");
                    return;
                case 501: // TurnEndLose
                    FinishBattle(
                        room,
                        session,
                        FindOpponent(connection, room)?.PlayerId,
                        ResultTurnendWin,
                        ResultTurnendLose,
                        "turn-end-timeout");
                    return;
                case 600: // OppoNotMulliganVictory
                    FinishBattle(
                        room,
                        session,
                        connection.PlayerId,
                        ResultFirstcardWin,
                        ResultFirstcardLose,
                        "mulligan-timeout");
                    return;
                case 601: // MulliganLose
                    FinishBattle(
                        room,
                        session,
                        FindOpponent(connection, room)?.PlayerId,
                        ResultFirstcardWin,
                        ResultFirstcardLose,
                        "mulligan-timeout");
                    return;
                case 800: // ReceiveRetire: receiver is the winner
                    FinishBattle(
                        room,
                        session,
                        connection.PlayerId,
                        ResultRetireWin,
                        ResultRetireLose,
                        "retire-result");
                    return;
                case 900: // ReceiveConsistencyLose
                case 901: // Invalid
                    FinishBattle(
                        room,
                        session,
                        connection.PlayerId,
                        ResultNoContest,
                        ResultNoContest,
                        "no-contest");
                    return;
                case 100: // BattleFinishToJudge
                case 110: // RecoveryBattleFinishToJudge
                    session.ResolveLikelyOutcome(
                        connection.PlayerId,
                        out string winner,
                        out int winnerResult,
                        out int loserResult);
                    FinishBattle(
                        room,
                        session,
                        winner,
                        winnerResult,
                        loserResult,
                        "normal-judge");
                    return;
                default:
                    // RetrySend and diagnostic judge statuses are requests to
                    // continue the native protocol, not terminal outcomes.
                    Plugin.Logger.LogInfo(
                        $"[SocketIO] Ignored non-terminal JudgeResult from " +
                        $"{connection.SessionId}: status={status}");
                    return;
            }
        }

        private void FinishBattle(
            GameRoom room,
            BattleSession session,
            string winnerPlayerId,
            int winnerResult,
            int loserResult,
            string reason)
        {
            if (room == null || session == null || string.IsNullOrEmpty(winnerPlayerId))
                return;

            if (!session.TryRecordOutcome(
                    winnerPlayerId,
                    winnerResult,
                    loserResult))
            {
                return;
            }

            if (!session.TryGetOutcome(
                    out string recordedWinner,
                    out int recordedWinnerResult,
                    out string recordedLoser,
                    out int recordedLoserResult))
            {
                return;
            }

            room.ChangeState(RoomState.Finished);
            SendBattleFinish(
                session,
                room,
                recordedWinner,
                recordedWinnerResult,
                recordedLoser,
                recordedLoserResult);
            Plugin.Logger.LogInfo(
                $"[SocketIO] Battle finished in room {room.RoomId}: " +
                $"winner={recordedWinner}, loser={recordedLoser}, reason={reason}");
        }

        private void FinishBattlesForServerStop()
        {
            if (!_running)
                return;

            foreach (GameRoom room in _rooms.GetAllRooms())
            {
                if (room == null || room.State != RoomState.InGame)
                    continue;

                Player host = null;
                foreach (Player player in room.GetPlayers())
                {
                    if (player != null && player.IsHost)
                    {
                        host = player;
                        break;
                    }
                }

                if (host == null)
                    continue;

                FinishBattle(
                    room,
                    _messageRouter.GetOrCreate(room.RoomId),
                    host.PlayerId,
                    ResultRetireLose,
                    ResultRetireWin,
                    "server-stop");
            }
        }

        private void SendBattleFinish(
            BattleSession session,
            GameRoom room,
            string winnerPlayerId,
            int winnerResult,
            string loserPlayerId,
            int loserResult)
        {
            SendBattleFinishToPlayer(
                session,
                room,
                winnerPlayerId,
                loserPlayerId,
                winnerResult);
            SendBattleFinishToPlayer(
                session,
                room,
                loserPlayerId,
                winnerPlayerId,
                loserResult);
        }

        private void SendBattleFinishToPlayer(
            BattleSession session,
            GameRoom room,
            string targetPlayerId,
            string sourcePlayerId,
            int result)
        {
            if (session == null || room == null ||
                string.IsNullOrEmpty(targetPlayerId) || string.IsNullOrEmpty(sourcePlayerId))
                return;

            Player source = FindPlayer(room, sourcePlayerId);
            JObject payload = new JObject
            {
                ["uri"] = "BattleFinish",
                ["bid"] = room.RoomId,
                ["result"] = result,
                ["viewerId"] = source?.ViewerId ?? 0,
                ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            RoutedMessage routed = _messageRouter.CreateServerMessage(
                room.RoomId,
                sourcePlayerId,
                targetPlayerId,
                "msg",
                payload);
            SocketIoConnection target = FindActiveConnection(room.RoomId, targetPlayerId);
            bool sent = routed != null && target != null && SendRoutedMessage(target, routed);
            if (sent)
                _messageRouter.MarkDelivered(room.RoomId, routed);

            Plugin.Logger.LogInfo(
                $"[SocketIO] OUT BattleFinish to {targetPlayerId}: " +
                $"result={result}, sent={sent}, " +
                $"playSeq={(routed == null ? 0 : routed.DeliverySequence)}");
        }

        private void RelayToOpponent(
            SocketIoConnection connection,
            GameRoom room,
            string eventName,
            JToken relayMessage,
            JToken originalMessage,
            byte[] binary)
        {
            if (connection == null || room == null)
                return;

            byte[] payload = relayMessage == null || relayMessage == originalMessage
                ? binary
                : SocketIoPayloadCodec.Encode(relayMessage);
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
                    payload);
                if (routed == null || routed.IsDuplicate)
                    continue;

                SocketIoConnection peer = FindActiveConnection(
                    connection.BattleId,
                    targetPlayer.PlayerId);
                // A connected peer may still be in the previous battle or
                // initializing its new agent. Keep the new lobby stream queued.
                bool sent = peer != null &&
                    !IsPendingRoomReentry(room.RoomId, targetPlayer.PlayerId) &&
                    SendRoutedMessage(peer, routed);
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
        }

        private static bool IsTerminalResult(int result)
        {
            return result == ResultLifeWin || result == ResultLifeLose ||
                   result == ResultDeckoutWin || result == ResultDeckoutLose ||
                   result == ResultRetireWin || result == ResultRetireLose ||
                   result == ResultSpecialWin || result == ResultSpecialLose ||
                   result == ResultDisconnectWin || result == ResultDisconnectLose ||
                   result == ResultFirstcardWin || result == ResultFirstcardLose ||
                   result == ResultTurnendWin || result == ResultTurnendLose ||
                   result == ResultTurnstartWin || result == ResultTurnstartLose ||
                   result == ResultNoContest;
        }

        private static bool IsWinResult(int result)
        {
            return result == ResultLifeWin || result == ResultDeckoutWin ||
                   result == ResultRetireWin || result == ResultSpecialWin ||
                   result == ResultDisconnectWin ||
                   result == ResultFirstcardWin || result == ResultTurnendWin ||
                   result == ResultTurnstartWin;
        }

        private static int OppositeResult(int result)
        {
            switch (result)
            {
                case ResultLifeWin: return ResultLifeLose;
                case ResultLifeLose: return ResultLifeWin;
                case ResultDeckoutWin: return ResultDeckoutLose;
                case ResultDeckoutLose: return ResultDeckoutWin;
                case ResultRetireWin: return ResultRetireLose;
                case ResultRetireLose: return ResultRetireWin;
                case ResultSpecialWin: return ResultSpecialLose;
                case ResultSpecialLose: return ResultSpecialWin;
                case ResultDisconnectWin: return ResultDisconnectLose;
                case ResultDisconnectLose: return ResultDisconnectWin;
                case ResultFirstcardWin: return ResultFirstcardLose;
                case ResultFirstcardLose: return ResultFirstcardWin;
                case ResultTurnendWin: return ResultTurnendLose;
                case ResultTurnendLose: return ResultTurnendWin;
                case ResultTurnstartWin: return ResultTurnstartLose;
                case ResultTurnstartLose: return ResultTurnstartWin;
                default: return ResultNoContest;
            }
        }

        private void PrepareRoomForRematch(GameRoom room)
        {
            // Both clients send Reenter, possibly concurrently. Only the first
            // normal return after a terminal result creates the next session.
            lock (room)
            {
                if (room.State != RoomState.Finished)
                    return;

                var pendingReentries = new HashSet<string>(StringComparer.Ordinal);
                foreach (Player player in room.GetPlayers())
                {
                    room.SetPlayerReady(player.PlayerId, false);
                    pendingReentries.Add(player.PlayerId);
                }
                lock (_connectionLock)
                {
                    _roomReadySent.Remove(room.RoomId);
                    _pendingRoomReentries[room.RoomId] = pendingReentries;
                }

                // Discard old pubSeq history and all per-battle state together,
                // before accepting Reenter into the new lobby's message stream.
                _messageRouter.Remove(room.RoomId);
                _messageRouter.GetOrCreate(room.RoomId);
                room.ChangeState(RoomState.Waiting);
                Plugin.Logger.LogInfo(
                    $"[SocketIO] Room {room.RoomId} reset for rematch: " +
                    "new battle session, readiness and sequence history cleared");
            }
        }

        private bool IsPendingRoomReentry(string roomId, string playerId)
        {
            lock (_connectionLock)
                return _pendingRoomReentries.TryGetValue(roomId, out HashSet<string> pending) &&
                    pending.Contains(playerId);
        }

        private bool TryHandleRematchReentry(
            SocketIoConnection connection,
            GameRoom room,
            string eventName,
            JToken message,
            byte[] binary)
        {
            Player player = FindPlayer(room, connection.PlayerId);
            if (player == null)
                return false;

            bool firstReturn;
            lock (_connectionLock)
            {
                if (!_pendingRoomReentries.TryGetValue(room.RoomId, out HashSet<string> pending))
                    return false;
                firstReturn = pending.Remove(player.PlayerId);
            }

            // Reenter is acknowledged to the sender, but the native opponent
            // controller restores membership only for RoomEntry. Retain pubSeq
            // for deduplication and let the existing bridge assign playSeq so
            // initialization gates defer consumption instead of losing the entry.
            JObject entry = CreateRoomEntryWithProfile(message, player);
            connection.LastRoomEntry = entry.DeepClone();
            RelayToOpponent(connection, room, eventName, entry, message, binary);
            if (firstReturn)
                SendRematchPlayerState(connection, room, player);
            FlushPendingMessages(connection, room);

            if (firstReturn)
            {
                Plugin.Logger.LogInfo(
                    $"[SocketIO] Room {room.RoomId} rematch reentry from " +
                    $"{(connection.IsHost ? "host" : "guest")}: " +
                    "routed sequenced RoomEntry and readiness to opponent, released pending lobby messages");
            }
            return true;
        }

        private void SendRematchPlayerState(SocketIoConnection connection, GameRoom room, Player player)
        {
            Player opponent = FindOpponent(connection, room);
            if (opponent == null)
                return;

            // A normal guest does not refresh its UI on the opponent's RoomEntry.
            // Follow the profile with the actual lobby readiness: the native
            // readiness handler updates both roles' displays without a UI patch.
            JObject state = new JObject
            {
                ["uri"] = player.IsReady ? "SetupComplete" : "SetupCancel",
                ["isSelf"] = 0,
                ["viewerId"] = player.ViewerId
            };
            RoutedMessage routed = _messageRouter.CreateServerMessage(
                room.RoomId,
                player.PlayerId,
                opponent.PlayerId,
                "msg",
                state);
            SocketIoConnection target = FindActiveConnection(room.RoomId, opponent.PlayerId);
            if (routed != null && target != null &&
                !IsPendingRoomReentry(room.RoomId, opponent.PlayerId) &&
                SendRoutedMessage(target, routed))
            {
                _messageRouter.MarkDelivered(room.RoomId, routed);
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
                !session.TryBeginMatched(host.PlayerId, guest.PlayerId, _cardMaster))
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
                    out bool selfGoesFirst,
                    out int battleFieldId) ||
                !session.TryGetOpponentSetup(
                    target.PlayerId,
                    out PlayerDeck opponentDeck,
                    out int[] opponentCards))
                return;

            JObject payload = battleStart
                ? CreateBattleStartPayload(room, target, source, selfDeck, opponentDeck,
                    battleSeed, battleFieldId)
                : CreateMatchedPayload(room, target, source, selfDeck, opponentDeck,
                    selfCards, battleSeed, selfGoesFirst, battleFieldId);
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
            bool selfGoesFirst,
            int battleFieldId)
        {
            return new JObject
            {
                ["uri"] = "Matched",
                ["bid"] = room.RoomId,
                ["turnState"] = selfGoesFirst ? 0 : 1,
                ["selfInfo"] = CreateBattleInfo(target, selfDeck, source, opponentDeck,
                    battleSeed, battleFieldId),
                ["oppoInfo"] = CreateBattleInfo(source, opponentDeck, target, selfDeck,
                    battleSeed, battleFieldId),
                ["selfDeck"] = CreateDeckData(selfCards)
            };
        }

        private static JObject CreateBattleStartPayload(
            GameRoom room,
            Player target,
            Player source,
            PlayerDeck selfDeck,
            PlayerDeck opponentDeck,
            int battleSeed,
            int battleFieldId)
        {
            return new JObject
            {
                ["uri"] = "BattleStart",
                ["bid"] = room.RoomId,
                ["battleStartDate"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["selfInfo"] = CreateBattleInfo(target, selfDeck, source, opponentDeck,
                    battleSeed, battleFieldId),
                ["oppoInfo"] = CreateBattleInfo(source, opponentDeck, target, selfDeck,
                    battleSeed, battleFieldId)
            };
        }

        private static JObject CreateBattleInfo(
            Player player,
            PlayerDeck deck,
            Player opponent,
            PlayerDeck opponentDeck,
            int battleSeed,
            int battleFieldId)
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
                ["fieldId"] = battleFieldId,
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
                if (cards == null || cards.Count < 6)
                {
                    error = "cardIds must contain at least 6 cards";
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

        private static bool TryGetInt(JToken value, out int result)
        {
            result = 0;
            return value != null && int.TryParse(value.ToString(), out result);
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
            // Player.OnEnter uses bool.Parse for guild flags, but int.Parse for isFriend.
            if (result["isGuildMember"] == null)
                result["isGuildMember"] = false;
            if (result["isGuildJoined"] == null)
                result["isGuildJoined"] = false;
            if (result["isFriend"] == null)
                result["isFriend"] = 0;
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
                ["isGuildMember"] = false,
                ["isGuildJoined"] = false,
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
            string host = string.IsNullOrWhiteSpace(_config.BindAddress)
                ? "0.0.0.0"
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
