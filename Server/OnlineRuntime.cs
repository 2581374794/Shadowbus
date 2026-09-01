using System;
using System.Collections.Generic;
using Shadowbus.Server.Core;
using Shadowbus.Server.Network;
using Shadowbus.Server.Room;
using Shadowbus.Server.SocketIO;
using Wizard;
using Wizard.RoomMatch;
using GamePlayer = Shadowbus.Server.Room.Player;

namespace Shadowbus.Server
{
    /// <summary>
    /// Owns the host Socket.IO endpoint and the room metadata used by the
    /// offline HTTP task adapter. Realtime gameplay is handled by the game's
    /// native RealTimeNetworkAgent, not by a second custom client.
    /// </summary>
    public static class OnlineRuntime
    {
        private static SocketIoServer _server;
        private static ServerConfig _config;
        private static bool _isInitialized;

        public static bool IsEnabled { get; private set; }
        public static bool IsServerRunning => _server != null && _server.IsRunning;
        public static string RoomCode => _server?.RoomCode;
        public static int ServerPort => _server?.Port ?? 0;
        public static string SocketIoUrl { get; private set; }
        public static RoomInfoMessage CurrentRoomInfo { get; private set; }
        public static JoinSuccessResponse LastJoinSuccess { get; private set; }
        public static string LastError { get; private set; }

        private static PlayerDeck _localBattleDeck;

        internal static PlayerDeck LocalBattleDeck => _localBattleDeck?.Clone();

        internal static bool CaptureLocalBattleDeck(int deckNo)
        {
            try
            {
                DeckData selected = null;
                foreach (DeckGroup group in DeckListUtility.DeckGroupDataBaseClone())
                {
                    if (group?.DeckDataList == null)
                        continue;
                    selected = group.DeckDataList.Find(deck =>
                        deck != null && deck.GetDeckID() == deckNo);
                    if (selected != null)
                        break;
                }

                DataMgr dataMgr = GameMgr.GetIns()?.GetDataMgr();
                IList<int> currentCards = selected?.GetCardIdList() ??
                    dataMgr?.GetCurrentDeckData();
                if (currentCards == null || currentCards.Count == 0)
                {
                    Plugin.Logger.LogError(
                        $"[OnlineRuntime] Cannot capture deck {deckNo}: card list is empty");
                    return false;
                }

                int classId = selected?.GetDeckClassID() ?? dataMgr?.GetPlayerClassId() ?? 0;
                int subclassId = selected?.GetDeckSubClassID() ?? dataMgr?.GetPlayerSubClassId() ?? 10;
                int skinId = 0;
                string rotationId = selected?.MyRotationId;
                try
                {
                    skinId = selected != null
                        ? selected.GetSkinId(false)
                        : dataMgr?.GetPlayerSkinId() ?? 0;
                }
                catch
                {
                    skinId = dataMgr?.GetPlayerSkinId() ?? 0;
                }

                int charaId = dataMgr?.GetPlayerCharaId() ?? 0;
                if (skinId > 0)
                {
                    try
                    {
                        ClassCharacterMasterData chara = dataMgr?.GetCharaPrmBySkinId(skinId);
                        if (chara != null)
                            charaId = chara.chara_id;
                    }
                    catch
                    {
                    }
                }

                _localBattleDeck = new PlayerDeck
                {
                    ClanId = classId,
                    SubclassId = subclassId,
                    CharaId = charaId,
                    RotationId = rotationId ?? string.Empty,
                    CardIds = new List<int>(currentCards).ToArray(),
                    SkinId = skinId,
                    SleeveId = (int)(selected?.GetDeckSleeveID() ?? dataMgr?.GetPlayerSleeveId() ?? 0L)
                };
                Plugin.Logger.LogInfo(
                    $"[OnlineRuntime] Captured local battle deck {deckNo}: " +
                    $"class={classId}, subclass={subclassId}, cards={_localBattleDeck.CardIds.Length}");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError(
                    $"[OnlineRuntime] Failed to capture local battle deck {deckNo}: {ex}");
                return false;
            }
        }

        public static void Initialize(ServerConfig config)
        {
            if (_isInitialized)
                return;

            _config = config ?? throw new ArgumentNullException(nameof(config));
            _isInitialized = true;
            Plugin.Logger.LogInfo("[OnlineRuntime] Socket.IO runtime initialized");
        }

        public static bool StartServer()
        {
            return StartServer(new RoomRules(), CreateDefaultProfile());
        }

        public static bool StartServer(RoomRules rules, P2PProfile profile)
        {
            if (!_isInitialized || _server != null)
            {
                LastError = "Socket.IO runtime is already running or not initialized";
                return false;
            }

            try
            {
                _server = new SocketIoServer(_config);
                if (!_server.Start())
                {
                    LastError = "Unable to start Socket.IO listener";
                    _server.Dispose();
                    _server = null;
                    return false;
                }

                string roomId = CreateNumericBattleId();
                string hostPlayerId = Guid.NewGuid().ToString("N").Substring(0, 16);
                GameRoom room = _server.Rooms.CreateRoom(roomId, hostPlayerId);
                room.Rules = NormalizeRoomRules(rules);
                CustomFormatContext.RoomFormatId = room.Rules.CustomFormatId;
                P2PProfile hostProfile = profile ?? CreateDefaultProfile();
                ApplyProfile(room.GetPlayer(hostPlayerId), hostProfile);

                string code = _server.CreateRoomCode(roomId, hostProfile, room.Rules);
                SocketIoUrl = BuildSocketIoUrl(ParseAddress(code), _server.Port);
                CurrentRoomInfo = CreateRoomInfo(room, code);
                LastJoinSuccess = null;
                LastError = null;
                IsEnabled = true;

                Plugin.Logger.LogInfo($"[OnlineRuntime] Socket.IO room created: {code}");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Plugin.Logger.LogError($"[OnlineRuntime] Socket.IO room creation failed: {ex.Message}");
                StopServer();
                return false;
            }
        }

        public static void StopServer()
        {
            try
            {
                _server?.Dispose();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[OnlineRuntime] Error stopping Socket.IO server: {ex.Message}");
            }
            finally
            {
                _server = null;
                SocketIoUrl = null;
                CurrentRoomInfo = null;
                LastJoinSuccess = null;
                IsEnabled = false;
                _localBattleDeck = null;
                CustomFormatContext.RoomFormatId = CustomFormats.UnlimitedId;
            }
        }

        /// <summary>
        /// Validates the custom room code and prepares the native realtime
        /// agent to connect. No custom TCP or replacement client is created.
        /// </summary>
        public static bool ConnectToServer(string roomCode)
        {
            if (!_isInitialized || string.IsNullOrWhiteSpace(roomCode))
            {
                LastError = "Room code is empty or runtime is not initialized";
                return false;
            }

            if (!ConnectionCode.TryParse(roomCode, out ConnectionData endpoint, out string error))
            {
                LastError = error ?? "Invalid room code";
                Plugin.Logger.LogError($"[OnlineRuntime] Invalid Socket.IO room code: {LastError}");
                return false;
            }

            SocketIoUrl = BuildSocketIoUrl(endpoint.ServerIp, endpoint.Port);
            var players = new System.Collections.Generic.List<PlayerInfo>();
            if (endpoint.HostProfile != null)
            {
                players.Add(new PlayerInfo
                {
                    PlayerId = "host",
                    Name = string.IsNullOrEmpty(endpoint.HostProfile.UserName) ? "Host" : endpoint.HostProfile.UserName,
                    ViewerId = endpoint.HostProfile.ViewerId,
                    Rank = endpoint.HostProfile.Rank,
                    BattlePoint = endpoint.HostProfile.BattlePoint,
                    MasterPoint = endpoint.HostProfile.MasterPoint,
                    DegreeId = endpoint.HostProfile.DegreeId,
                    EmblemId = endpoint.HostProfile.EmblemId,
                    CountryCode = endpoint.HostProfile.CountryCode ?? string.Empty,
                    IsOfficial = endpoint.HostProfile.IsOfficial,
                    IsHost = true
                });
            }

            RoomRules endpointRules = endpoint.RoomRules ?? new RoomRules();
            if ((endpointRules.CustomFormat == null ||
                 string.Equals(endpointRules.CustomFormat.Id, CustomFormats.UnlimitedId, StringComparison.OrdinalIgnoreCase)) &&
                endpointRules.DeckRules != null &&
                !string.IsNullOrWhiteSpace(endpointRules.DeckRules.FormatId) &&
                !string.Equals(endpointRules.DeckRules.FormatId, CustomFormats.UnlimitedId, StringComparison.OrdinalIgnoreCase))
            {
                endpointRules.CustomFormat = CustomFormats.Get(endpointRules.DeckRules.FormatId).Clone();
            }
            if (endpointRules.CustomFormat != null)
            {
                endpointRules.CustomFormat = CustomFormats.InstallRoomDefinition(
                    endpointRules.CustomFormat);
                endpointRules.DeckRules = endpointRules.DeckRules ?? new DeckRules();
                endpointRules.DeckRules.FormatId = endpointRules.CustomFormat.Id;
            }
            endpointRules.DeckFormat = Data.FormatConvertApi(Format.Unlimited);
            CustomFormatContext.RoomFormatId = endpointRules.CustomFormatId;

            LastJoinSuccess = new JoinSuccessResponse
            {
                RoomId = endpoint.RoomId,
                RoomCode = roomCode.Trim(),
                BattleId = endpoint.RoomId,
                HostPlayerId = "host",
                Rules = endpointRules,
                Players = players
            };
            CurrentRoomInfo = new RoomInfoMessage
            {
                RoomId = endpoint.RoomId,
                RoomCode = roomCode.Trim(),
                BattleId = endpoint.RoomId,
                HostPlayerId = "host",
                State = RoomState.Waiting.ToString(),
                Rules = endpointRules,
                Players = LastJoinSuccess.Players
            };
            RoomConnectController controller = RoomBase.ConnectController;
            if (controller != null)
            {
                controller.RoomID = endpoint.RoomId;
                controller.DisplayRoomID = roomCode.Trim();
            }
            LastError = null;
            IsEnabled = true;

            Plugin.Logger.LogInfo($"[OnlineRuntime] Socket.IO endpoint accepted: ws://{SocketIoUrl}, BattleId={endpoint.RoomId}");
            return true;
        }

        public static void Disconnect()
        {
            if (!IsServerRunning)
            {
                SocketIoUrl = null;
                CurrentRoomInfo = null;
                LastJoinSuccess = null;
                IsEnabled = false;
                _localBattleDeck = null;
                CustomFormatContext.RoomFormatId = CustomFormats.UnlimitedId;
            }
        }

        public static void Update()
        {
            // Socket.IO connections use their own listener/receive threads.
            // No Unity Update polling is needed for transport progress.
        }

        internal static bool ResendHostOpponentSnapshot()
        {
            return _server != null && _server.ResendHostOpponentSnapshot();
        }

        public static void Shutdown()
        {
            StopServer();
            Disconnect();
            _isInitialized = false;
        }

        private static RoomInfoMessage CreateRoomInfo(GameRoom room, string code)
        {
            return new RoomInfoMessage
            {
                RoomId = room.RoomId,
                RoomCode = code,
                BattleId = room.RoomId,
                HostPlayerId = room.HostPlayerId,
                State = room.State.ToString(),
                Rules = room.Rules,
                Players = room.GetPlayers().ConvertAll(ToPlayerInfo)
            };
        }

        private static string ParseAddress(string roomCode)
        {
            return ConnectionCode.TryParse(roomCode, out ConnectionData data, out _)
                ? data.ServerIp
                : (_config.BindAddress == "0.0.0.0" ? "127.0.0.1" : _config.BindAddress);
        }

        private static string BuildSocketIoUrl(string address, int port)
        {
            string host = string.IsNullOrWhiteSpace(address) ? "127.0.0.1" : address;
            string path = _config?.SocketPath;
            if (string.IsNullOrWhiteSpace(path))
                path = "/socket.io/";
            if (!path.StartsWith("/", StringComparison.Ordinal))
                path = "/" + path;
            if (!path.EndsWith("/", StringComparison.Ordinal))
                path += "/";
            return $"{host}:{port}{path}";
        }

        private static P2PProfile CreateDefaultProfile()
        {
            try
            {
                return ProfileOfflineData.CreateP2PProfile();
            }
            catch
            {
                return new P2PProfile
                {
                    ViewerId = ProfileOfflineData.GetOrCreateViewerId(),
                    UserName = "Player",
                    CountryCode = string.Empty
                };
            }
        }

        private static string CreateNumericBattleId()
        {
            long value = 100000000000L + (uint)new Random().Next(int.MaxValue);
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void ApplyProfile(GamePlayer player, P2PProfile profile)
        {
            if (player == null || profile == null)
                return;
            player.Name = string.IsNullOrEmpty(profile.UserName) ? "Player" : profile.UserName;
            player.ViewerId = profile.ViewerId;
            player.Rank = profile.Rank;
            player.BattlePoint = profile.BattlePoint;
            player.MasterPoint = profile.MasterPoint;
            player.DegreeId = profile.DegreeId;
            player.EmblemId = profile.EmblemId;
            player.CountryCode = profile.CountryCode ?? string.Empty;
            player.IsOfficial = profile.IsOfficial;
        }

        private static RoomRules NormalizeRoomRules(RoomRules rules)
        {
            rules = rules ?? new RoomRules();
            CustomFormatDefinition definition = rules.CustomFormat;
            if ((definition == null ||
                 string.Equals(definition.Id, CustomFormats.UnlimitedId, StringComparison.OrdinalIgnoreCase)) &&
                rules.DeckRules != null &&
                !string.IsNullOrWhiteSpace(rules.DeckRules.FormatId) &&
                !string.Equals(rules.DeckRules.FormatId, CustomFormats.UnlimitedId, StringComparison.OrdinalIgnoreCase))
            {
                definition = CustomFormats.Get(rules.DeckRules.FormatId).Clone();
            }
            if (definition == null)
            {
                definition = CustomFormats.Get(rules.DeckRules?.FormatId).Clone();
            }
            else
            {
                definition = CustomFormats.InstallRoomDefinition(definition);
            }

            rules.CustomFormat = definition.Clone();
            rules.DeckRules = rules.DeckRules ?? new DeckRules();
            rules.DeckRules.FormatId = definition.Id;
            rules.DeckFormat = Data.FormatConvertApi(Format.Unlimited);
            return rules;
        }

        private static PlayerInfo ToPlayerInfo(GamePlayer player)
        {
            return new PlayerInfo
            {
                PlayerId = player.PlayerId,
                Name = string.IsNullOrEmpty(player.Name) ? "Player" : player.Name,
                ViewerId = player.ViewerId,
                Rank = player.Rank,
                BattlePoint = player.BattlePoint,
                MasterPoint = player.MasterPoint,
                DegreeId = player.DegreeId,
                EmblemId = player.EmblemId,
                CountryCode = player.CountryCode ?? string.Empty,
                IsOfficial = player.IsOfficial,
                IsHost = player.IsHost,
                IsReady = player.IsReady
            };
        }
    }
}
