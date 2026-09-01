using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Cute;
using LitJson;
using Shadowbus.Server.Network;
using Shadowbus.Server.Room;
using Wizard;

namespace Shadowbus.Server
{
    /// <summary>
    /// Converts the game's HTTP room tasks into requests handled by the local
    /// room protocol, then supplies responses in the original API shape.
    /// </summary>
    public static class OnlineTaskRouter
    {
        public static bool CanHandle(NetworkTask task)
        {
            if (task == null)
                return false;
            string taskType = task.GetType().Name;
            return (OnlineRuntime.IsEnabled && IsRoomBattleDoMatchingTask(taskType)) ||
                   taskType == "OpenRoomBattleCreateRoomTask" ||
                   taskType == "OpenRoomBattleEnterRoomTask" ||
                   taskType == "OpenRoomBattleCloseRoomTask" ||
                   taskType == "OpenRoomBattleLeaveRoomTask" ||
                   (OnlineRuntime.IsEnabled &&
                    (taskType.StartsWith("OpenRoomBattleSetDeckTask", StringComparison.Ordinal) ||
                     taskType == "OpenRoomBattleChooseDeckTask"));
        }

        public static IEnumerator Process(NetworkManager networkManager, NetworkTask task)
        {
            while (networkManager.isConnect)
                yield return 0;

            networkManager.isConnect = true;
            networkManager.isTimeOut = false;
            networkManager.isError = false;

            IEnumerator handler;
            string taskTypeName = task.GetType().Name;
            if (OnlineRuntime.IsEnabled && IsRoomBattleDoMatchingTask(taskTypeName))
            {
                handler = ProcessRoomBattleDoMatching(task);
            }
            else
            {
                switch (taskTypeName)
                {
                    case "OpenRoomBattleCreateRoomTask":
                        handler = ProcessCreateRoom(task);
                        break;
                    case "OpenRoomBattleEnterRoomTask":
                        handler = ProcessEnterRoom(task);
                        break;
                    case "OpenRoomBattleCloseRoomTask":
                        handler = ProcessCloseRoom(task);
                        break;
                    case "OpenRoomBattleLeaveRoomTask":
                        handler = ProcessLeaveRoom(task);
                        break;
                    case "OpenRoomBattleChooseDeckTask":
                        handler = ProcessChooseDeck(task);
                        break;
                    default:
                        if (taskTypeName.StartsWith(
                            "OpenRoomBattleSetDeckTask",
                            StringComparison.Ordinal))
                        {
                            handler = ProcessSetDeck(task);
                        }
                        else
                        {
                            FailTask(task, "UNSUPPORTED_ROOM_TASK", "Unsupported room task");
                            handler = EmptyCoroutine();
                        }
                        break;
                }
            }

            bool failed = false;
            Exception failure = null;
            while (true)
            {
                bool hasNext = false;
                object current = null;
                try
                {
                    hasNext = handler.MoveNext();
                    if (hasNext)
                        current = handler.Current;
                }
                catch (Exception ex)
                {
                    failed = true;
                    failure = ex;
                }

                if (failed || !hasNext)
                    break;
                yield return current;
            }

            if (failed)
            {
                Plugin.Logger.LogError($"[OnlineTaskRouter] {task.GetType().Name} failed: {failure}");
                FailTask(task, "ROOM_TASK_ERROR", failure.Message);
            }

            if (networkManager.NetworkUI != null)
                networkManager.NetworkUI.StopLoading();
            networkManager.ClearLastRequestTask();
            networkManager.isConnect = false;
        }

        private static IEnumerator ProcessCreateRoom(NetworkTask task)
        {
            Plugin.Logger.LogInfo("[OnlineTaskRouter] Creating host room");
            RoomRules rules = ReadRoomRules(task.Params);
            CustomRoomFormatSelection.EndSelectionSession();
            Plugin.Logger.LogInfo(
                $"[OnlineTaskRouter] Host custom format: {rules.CustomFormatId} " +
                $"({rules.CustomFormat?.DisplayName ?? "<unknown>"})");
            if (!OnlineRuntime.StartServer(rules, null))
            {
                FailTask(task, "ROOM_CREATE_FAILED", OnlineRuntime.LastError ?? "Unable to create room");
                yield break;
            }

            string roomCode = OnlineRuntime.RoomCode;
            RoomInfoMessage room = OnlineRuntime.CurrentRoomInfo;
            JsonData data = CreateSuccessEnvelope();
            data["data"]["room_id"] = room?.RoomId ?? roomCode;
            data["data"]["display_room_id"] = roomCode;
            data["data"]["battle_id"] = room?.BattleId ?? room?.RoomId ?? roomCode;
            data["data"]["is_invitation_user"] = false;
            data["data"]["is_enabled_all_card"] = false;
            data["data"]["battle_type"] = room?.Rules?.BattleType ?? 6;
            data["data"]["deck_format"] = Data.FormatConvertApi(Format.Unlimited);
            data["data"]["battle_rule"] = room?.Rules?.BattleRule ?? 1;
            data["data"]["two_pick_type"] = room?.Rules?.TwoPickType ?? 0;
            data["data"]["is_deck_confirmable"] = room?.Rules?.IsDeckConfirmable == true ? 1 : 0;
            data["data"]["node_server_url"] = BuildNodeServerUrl();
            Plugin.Logger.LogInfo($"[OnlineTaskRouter] Host node_server_url: {BuildNodeServerUrl()}");

            CompleteTask(task, data);
            Plugin.Logger.LogInfo($"[OnlineTaskRouter] Host room ready: {roomCode}");
            yield break;
        }

        private static IEnumerator ProcessRoomBattleDoMatching(NetworkTask task)
        {
            RoomInfoMessage room = OnlineRuntime.CurrentRoomInfo;
            if (!OnlineRuntime.IsEnabled || room == null)
            {
                FailTask(task, "ROOM_MATCHING_FAILED", "The current Socket.IO room is unavailable");
                yield break;
            }

            bool isHost = OnlineRuntime.IsServerRunning;
            int deckNo = ReadInt(task.Params, "deck_no", 0);
            if (!OnlineRuntime.CaptureLocalBattleDeck(deckNo))
            {
                FailTask(task, "ROOM_MATCHING_FAILED",
                    "The selected local deck could not be captured for online battle");
                yield break;
            }
            int matchingState = isHost ? 3007 : 3004;
            string battleId = room.BattleId ?? room.RoomId;
            int cardMasterId = GetCurrentCardMasterId();

            JsonData response = CreateSuccessEnvelope();
            JsonData body = response["data"];
            body["matching_state"] = matchingState;
            body["battle_state"] = 0;
            body["battle_id"] = battleId ?? string.Empty;
            body["card_master_id"] = cardMasterId;

            CompleteTask(task, response);
            Plugin.Logger.LogInfo(
                $"[OnlineTaskRouter] Local room matching response: role={(isHost ? "host" : "guest")}, " +
                $"matching_state={matchingState}, battle_state=0, battle_id={battleId}, " +
                $"card_master_id={cardMasterId}");
            yield break;
        }

        private static IEnumerator ProcessEnterRoom(NetworkTask task)
        {
            string roomCode = ReadString(task.Params, "room_id");
            if (string.IsNullOrWhiteSpace(roomCode))
            {
                FailTask(task, "INVALID_ROOM_ID", "Room code is empty");
                yield break;
            }

            Plugin.Logger.LogInfo($"[OnlineTaskRouter] Joining room: {roomCode}");
            if (!OnlineRuntime.ConnectToServer(roomCode))
            {
                FailTask(task, "ROOM_JOIN_FAILED", OnlineRuntime.LastError ?? "Unable to join room");
                yield break;
            }

            JoinSuccessResponse joined = OnlineRuntime.LastJoinSuccess;
            RoomInfoMessage room = OnlineRuntime.CurrentRoomInfo;
            RoomRules rules = joined?.Rules ?? room?.Rules ?? new RoomRules();
            Plugin.Logger.LogInfo(
                $"[OnlineTaskRouter] Guest room format: {rules.CustomFormatId} " +
                $"({rules.CustomFormat?.DisplayName ?? "<unknown>"})");
            PlayerInfo opponent = FindHost(joined, room);

            JsonData data = CreateSuccessEnvelope();
            JsonData body = data["data"];
            body["room_id"] = joined?.RoomId ?? room?.RoomId ?? roomCode;
            body["display_room_id"] = roomCode;
            body["battle_id"] = joined?.BattleId ?? room?.BattleId ?? joined?.RoomId ?? roomCode;
            body["result_reason"] = 0;
            body["is_friend"] = 0;
            body["guild_id"] = 0;
            body["oppo_guild_id"] = 0;
            body["battle_type"] = rules.BattleType;
            body["deck_format"] = rules.DeckFormat;
            body["battle_rule"] = rules.BattleRule;
            body["two_pick_type"] = rules.TwoPickType;
            body["is_deck_confirmable"] = rules.IsDeckConfirmable ? 1 : 0;
            body["node_server_url"] = BuildNodeServerUrl();
            Plugin.Logger.LogInfo($"[OnlineTaskRouter] Guest node_server_url: {BuildNodeServerUrl()}");
            body["is_invitation_user"] = false;
            body["is_enabled_all_card"] = false;

            JsonData opponentData = new JsonData();
            opponentData["oppoId"] = opponent?.ViewerId ?? 1;
            opponentData["battlePoint"] = opponent?.BattlePoint ?? 0;
            opponentData["degreeId"] = opponent?.DegreeId ?? 0;
            opponentData["emblemId"] = opponent?.EmblemId ?? 0L;
            opponentData["country_code"] = opponent?.CountryCode ?? "";
            opponentData["rank"] = opponent?.Rank ?? 0;
            opponentData["max_rank"] = opponent?.Rank ?? 0;
            opponentData["userName"] = opponent?.Name ?? "Host";
            opponentData["isOfficial"] = opponent?.IsOfficial == true ? 1 : 0;
            body["oppo_info"] = opponentData;

            CompleteTask(task, data);
            Plugin.Logger.LogInfo($"[OnlineTaskRouter] Joined room successfully: {joined?.RoomId}; task callback completed");
            yield break;
        }

        private static IEnumerator ProcessCloseRoom(NetworkTask task)
        {
            Plugin.Logger.LogInfo("[OnlineTaskRouter] Closing host room");

            if (!OnlineRuntime.IsServerRunning)
            {
                FailTask(task, "ROOM_CLOSE_FAILED", "Host room server is not running");
                yield break;
            }

            JsonData data = CreateSuccessEnvelope();
            data["data"]["room_result"] = 1;
            data["data"]["room_id"] = OnlineRuntime.CurrentRoomInfo?.RoomId ?? string.Empty;
            CompleteTask(task, data);
            OnlineRuntime.StopServer();
            Plugin.Logger.LogInfo("[OnlineTaskRouter] Host room closed and Socket.IO server stopped");
            yield break;
        }

        private static IEnumerator ProcessLeaveRoom(NetworkTask task)
        {
            string roomId = ReadString(task.Params, "room_id") ?? string.Empty;
            Plugin.Logger.LogInfo($"[OnlineTaskRouter] Leaving room {roomId}");

            JsonData data = CreateSuccessEnvelope();
            data["data"]["room_id"] = roomId;
            data["data"]["result_reason"] = 0;
            data["data"]["room_result"] = 1;
            CompleteTask(task, data);
            OnlineRuntime.Disconnect();
            Plugin.Logger.LogInfo("[OnlineTaskRouter] Guest room leave completed");
            yield break;
        }

        private static IEnumerator ProcessSetDeck(NetworkTask task)
        {
            int deckNo = ReadInt(task.Params, "deck_no", 0);
            if (!ValidateLocalDeck(deckNo, out string reason))
            {
                FailTask(task, "DECK_FORMAT_INVALID", reason ?? "Deck does not satisfy the room format");
                yield break;
            }

            CompleteTask(task, CreateSuccessEnvelope());
            yield break;
        }

        private static IEnumerator ProcessChooseDeck(NetworkTask task)
        {
            object raw = ReadMember(task.Params, "deck_no_list");
            List<int> deckNos = ReadIntList(raw);
            if (deckNos.Count == 0)
            {
                FailTask(task, "DECK_FORMAT_INVALID", "No deck was submitted");
                yield break;
            }

            foreach (int deckNo in deckNos)
            {
                if (!ValidateLocalDeck(deckNo, out string reason))
                {
                    FailTask(task, "DECK_FORMAT_INVALID", reason ?? "Deck does not satisfy the room format");
                    yield break;
                }
            }

            CompleteTask(task, CreateSuccessEnvelope());
            yield break;
        }

        private static bool ValidateLocalDeck(int deckNo, out string reason)
        {
            reason = null;
            if (deckNo <= 0)
            {
                reason = "Deck ID is invalid";
                return false;
            }

            try
            {
                JsonData source = Data.Load?.data?.UserDeckListUnlimited;
                if (source == null)
                {
                    reason = "Local deck data is unavailable";
                    return false;
                }

                DeckData deck = DeckListUtility.CreateDeckGroup(
                        source,
                        Format.Unlimited,
                        DeckAttributeType.CustomDeck)
                    .DeckDataList
                    .Find(item => item.GetDeckID() == deckNo);
                if (deck == null || deck.IsNoCard())
                {
                    // Trial/default decks are supplied by the game's native
                    // deck groups rather than UserDeckListUnlimited. Their
                    // DeckData is still checked by the selection UI, so the
                    // local task adapter must not reject them as unknown.
                    Plugin.Logger.LogDebug(
                        $"[OnlineTaskRouter] Deck {deckNo} is not in the local custom deck list; " +
                        "leaving native deck validation to the client.");
                    return true;
                }

                CustomFormatDefinition definition = OnlineRuntime.CurrentRoomInfo?.Rules?.CustomFormat ??
                    CustomFormats.Get(CustomFormatContext.RoomFormatId);
                if (!CustomFormats.IsDeckCompliant(
                    deck.GetCardIdList(),
                    definition,
                    CardMaster.GetInstanceForBattle(),
                    out CustomFormatViolation violation))
                {
                    reason = $"{definition.DisplayName}: {CustomFormatViolationText.Describe(
                        violation,
                        CardMaster.GetInstanceForBattle())}";
                    Plugin.Logger.LogWarning(
                        $"[OnlineTaskRouter] Rejected deck {deckNo} for {definition.Id}: {reason}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                Plugin.Logger.LogError($"[OnlineTaskRouter] Deck validation failed: {ex}");
                return false;
            }
        }

        private static bool IsRoomBattleDoMatchingTask(string taskTypeName)
        {
            return !string.IsNullOrEmpty(taskTypeName) &&
                   taskTypeName.StartsWith("RoomBattle", StringComparison.Ordinal) &&
                   taskTypeName.EndsWith("DoMatchingTask", StringComparison.Ordinal);
        }

        private static int GetCurrentCardMasterId()
        {
            try
            {
                int value = (int)CardMaster.BatttleCardMasterId;
                return value > 0 ? value : 1;
            }
            catch
            {
                return 1;
            }
        }

        private static List<int> ReadIntList(object value)
        {
            var result = new List<int>();
            if (value == null)
                return result;
            if (value is int[] intArray)
            {
                result.AddRange(intArray);
                return result;
            }
            if (value is IEnumerable enumerable)
            {
                foreach (object item in enumerable)
                {
                    try
                    {
                        result.Add(Convert.ToInt32(item));
                    }
                    catch
                    {
                    }
                }
            }
            return result;
        }

        private static IEnumerator EmptyCoroutine()
        {
            yield break;
        }

        private static RoomRules ReadRoomRules(object parameters)
        {
            int battleRule = ReadInt(parameters, "battle_rule", 1);
            CustomFormatDefinition format = CustomFormats.Get(
                CustomRoomFormatSelection.SelectedFormatId).Clone();
            return new RoomRules
            {
                BattleType = ReadInt(parameters, "battle_type", 6),
                BattleRule = battleRule,
                DeckFormat = Data.FormatConvertApi(Format.Unlimited),
                TwoPickType = ReadInt(parameters, "two_pick_type", 0),
                BestOf = battleRule == 3 || battleRule == 5 ? 5 :
                    battleRule == 2 || battleRule == 4 ? 3 : 1,
                AllowSpectators = ReadInt(parameters, "can_friend_watch", 0) != 0 ||
                                  ReadInt(parameters, "can_guild_watch", 0) != 0,
                CustomFormat = format,
                DeckRules = new DeckRules
                {
                    FormatId = format.Id,
                    DeckSize = format.DeckSizeLimit ?? int.MaxValue,
                    CardLimit = format.SameCardLimit ?? int.MaxValue
                }
            };
        }

        private static PlayerInfo FindHost(JoinSuccessResponse joined, RoomInfoMessage room)
        {
            var players = joined?.Players ?? room?.Players;
            if (players == null)
                return null;
            foreach (PlayerInfo player in players)
            {
                if (player.IsHost)
                    return player;
            }
            return null;
        }

        private static int ReadInt(object target, string name, int fallback)
        {
            object value = ReadMember(target, name);
            if (value == null)
                return fallback;
            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return fallback;
            }
        }

        private static string ReadString(object target, string name)
        {
            return ReadMember(target, name) as string;
        }

        private static object ReadMember(object target, string name)
        {
            if (target == null)
                return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = target.GetType();
            FieldInfo field = type.GetField(name, flags);
            if (field != null)
                return field.GetValue(target);
            PropertyInfo property = type.GetProperty(name, flags);
            return property?.GetValue(target, null);
        }

        private static JsonData CreateSuccessEnvelope()
        {
            JsonData response = new JsonData();
            response["data"] = new JsonData();
            response["data_headers"] = new JsonData();
            response["data_headers"]["result_code"] = 1;
            response["data_headers"]["servertime"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return response;
        }

        private static string BuildNodeServerUrl()
        {
            return OnlineRuntime.SocketIoUrl ?? string.Empty;
        }

        private static void CompleteTask(NetworkTask task, JsonData response)
        {
            task.SetResponseData(response);
            task.CheckResultCodeToPopupCreate_ReturnStatus(0);
        }

        private static void FailTask(NetworkTask task, string errorCode, string message)
        {
            JsonData response = new JsonData();
            response["data"] = new JsonData();
            response["data_headers"] = new JsonData();
            response["data_headers"]["result_code"] = 0;
            response["data_headers"]["servertime"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            response["error"] = new JsonData();
            response["error"]["code"] = errorCode;
            response["error"]["message"] = message ?? "Room task failed";
            task.SetResponseData(response);
            task.CallbackOnFailure?.Invoke(NetworkTask.ResultCode.Error);
        }
    }
}
