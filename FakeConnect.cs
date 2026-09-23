using Cute;
using HarmonyLib;
using LitJson;
using MessagePack;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using Wizard;
using Wizard.Bingo;
using Wizard.Scripts.Network.Data.TaskData.BuildDeckPurchase;
using Wizard.Scripts.Network.Data.TaskData.ItemPurchase;
using Wizard.Scripts.Network.Data.TaskData.SkinPurchase;
using Wizard.Scripts.Network.Data.TaskData.SpotCardExchange;

namespace Shadowbus
{
    public class FakeConnect
    {
        [HarmonyPatch(typeof(NetworkManager), nameof(NetworkManager.Connect), [typeof(bool)])]
        [HarmonyPrefix]
        public static bool NetworkManager_Connect_Prefix(ref IEnumerator __result, NetworkManager __instance, bool showErrorDialog)
        {
            __result = CustomConnectCoroutine(__instance, showErrorDialog);
            return false;
        }

        private static IEnumerator CustomConnectCoroutine(NetworkManager __instance, bool showErrorDialog)
        {

            NetworkTask currentTask = __instance.lastRequestTask;
            string taskTypeName = currentTask.GetType().Name;

            if (Server.OnlineTaskRouter.CanHandle(currentTask))
            {
                Plugin.Logger.LogInfo($"[Online] Intercepted room task: {taskTypeName}");
                yield return Server.OnlineTaskRouter.Process(__instance, currentTask);
            }
            else if (P2PTaskRouter.CanHandle(currentTask))
            {
                Plugin.Logger.LogInfo($"[P2P] Intercepted room task: {taskTypeName}");
                yield return P2PTaskRouter.Process(__instance, currentTask);
            }
            else if (BossRushOfflineData.IsActive && BossRushOfflineData.TryCreateResponse(currentTask, out _))
            {
                Plugin.Logger.LogInfo($"[BossRush] Intercepted task: {taskTypeName}");
                yield return ProcessBossRushTask(__instance, currentTask);
            }
            else if (IsTaskSkipped(taskTypeName))
            {
                Plugin.Logger.LogInfo($"[Offlinizer] Skipped Task: {taskTypeName}");
                yield return ProcessSkipTask(__instance, currentTask);
            }
            else if (IsTaskOfflinized(taskTypeName))
            {
                Plugin.Logger.LogInfo($"[Offlinizer] Intercepted Task: {taskTypeName}. Reading local data...");
                yield return ProcessOfflineTask(__instance, currentTask, taskTypeName);
            }
            else
            {
                Plugin.Logger.LogInfo($"[Offlinizer] Task {taskTypeName} not offlinized yet. Sending real request...");
                yield return ProcessOnlineTask(__instance, currentTask, showErrorDialog);
            }
        }
        private static string[] SkippedTaskes = new string[]
            {
            "MyPageRefreshTask",
            };
        private static bool IsTaskSkipped(string taskName)
        {
            return Array.Exists(SkippedTaskes, t => t == taskName);
        }
        private static bool IsTaskOfflinized(string taskName)
        {
            return LocalDeckCodeService.CanHandleTaskName(taskName) ||
                IsLocalDeckListTask(taskName) ||
                ProfileOfflineData.CanHandle(taskName) ||
                StoryOfflineData.CanHandle(taskName) ||
                PuzzleOfflineData.CanHandle(taskName) ||
                ReplayOfflineData.CanHandle(taskName) ||
                EmptyOfflineResponses.ContainsKey(taskName) ||
                IsEmptyOfflineTask(taskName) ||
                File.Exists((Path.Combine("Mods", "OfflinizedTasks", $"{taskName}.json")));
        }

        /// <summary>
        /// Tasks whose honest offline answer is "the server has nothing for you".
        /// Their payload is service-side (the client never stores it), so instead
        /// of inventing content we answer with the empty shape the stock parser
        /// expects. Leaving them to <see cref="ProcessOnlineTask"/> is worse than
        /// useless offline: the request comes back empty, MessagePack fails to
        /// decode it and the player gets a 「復号化に失敗しました」 dialog.
        ///
        /// · <c>QuestMissionInfoTask</c> (api 68) — the mission list behind the
        ///   "任务一览" dialog. <c>QuestMissionInfoTask.Parse</c> treats
        ///   <c>data</c> as a flat array of missions and only runs when
        ///   <c>result_code == 1</c>; an empty array means "no missions".
        ///
        /// 下面 <see cref="IsEmptyOfflineTask"/> 里那几个要现算时间，所以不放进这张表。
        /// </summary>
        private static readonly Dictionary<string, string> EmptyOfflineResponses =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                {
                    nameof(QuestMissionInfoTask),
                    "{\"data_headers\":{\"short_udid\":0,\"viewer_id\":0,\"sid\":\"\"," +
                    "\"servertime\":0,\"result_code\":1},\"data\":[]}"
                },
            };

        /// <summary>
        /// 需要现算内容的「空应答」任务。
        ///
        /// · <c>QuestPointInfoTask</c>（任务点数一览，api 71）—— 本地没有任务点数也没有
        ///   报酬可领，但 <c>Parse</c> 会直接 <c>DateTime.Parse(data.start_time/end_time)</c>
        ///   并读 <c>total_point</c> / <c>max_point</c> / <c>reward_list</c>，所以不能给空对象，
        ///   要给一个「从现在起到十年后、0 点、没有奖励」的窗口。
        /// </summary>
        private static bool IsEmptyOfflineTask(string taskName)
        {
            return string.Equals(taskName, nameof(QuestPointInfoTask), StringComparison.Ordinal);
        }

        private static JsonData CreateEmptyOfflineResponse(string taskName)
        {
            if (EmptyOfflineResponses.TryGetValue(taskName, out string json))
            {
                return JsonMapper.ToObject(json);
            }

            if (string.Equals(taskName, nameof(QuestPointInfoTask), StringComparison.Ordinal))
            {
                string start = DateTime.UtcNow.AddDays(-1).ToString("o");
                string end = DateTime.UtcNow.AddYears(10).ToString("o");
                return JsonMapper.ToObject(
                    "{\"data_headers\":{\"short_udid\":0,\"viewer_id\":0,\"sid\":\"\"," +
                    "\"servertime\":0,\"result_code\":1},\"data\":{\"start_time\":\"" + start + "\"," +
                    "\"end_time\":\"" + end + "\",\"total_point\":0,\"max_point\":0,\"reward_list\":[]}}");
            }

            return null;
        }

        /// <summary>
        /// 等上一笔请求结束的**有上限**版本。
        ///
        /// 原版只有它自己的 Connect 协程（就是被我们整个替换掉的那个 <c>NetworkManager/&lt;Connect&gt;d__18</c>）
        /// 会清 <c>isConnect</c>，而全程序集里也没别的地方把它置 true —— 也就是说**只有插件会置 true**。
        /// 只要有任何一条路径带着异常退出（真实请求那条路到处都是 <c>throw</c>，比如 MessagePack 解密失败、
        /// `HandleDeserializeException` 抛出），协程直接死掉、末尾那句 <c>isConnect = false</c> 就不执行，
        /// 于是这个标志永远留着 true：之后**每一个**任务都卡在入口的 `while (isConnect) yield return 0;`
        /// 上——表现就是「游戏卡死」：进程还在跑帧、CPU 接近 0、窗口也能点，但什么都不再发生
        /// （实测：进剧情页停在 StoryInfoTask，之后再没有一行日志），而游戏偶尔调用
        /// <c>StopConnectCoroutine</c> 时又会自己恢复，正好对应「有时候过很久会自己好」。
        ///
        /// 所以这里最多等 <see cref="NetworkBusyTimeoutSeconds"/> 秒，超时就自己清掉并继续，把死锁掐掉。
        /// </summary>
        private const float NetworkBusyTimeoutSeconds = 3f;

        private static IEnumerator WaitUntilNetworkIdle(NetworkManager manager, string taskName)
        {
            float deadline = Time.realtimeSinceStartup + NetworkBusyTimeoutSeconds;
            while (manager.isConnect && Time.realtimeSinceStartup < deadline)
            {
                yield return 0;
            }

            if (manager.isConnect)
            {
                Plugin.Logger.LogWarning(
                    $"[Offlinizer] The network busy flag was still set {NetworkBusyTimeoutSeconds:F0}s after the " +
                    $"last request started (a request likely died before clearing it); clearing it so '{taskName}' " +
                    "can run instead of freezing the whole game.");
                manager.isConnect = false;
            }
        }

        private static IEnumerator ProcessBossRushTask(NetworkManager networkManager, NetworkTask task)
        {
            yield return WaitUntilNetworkIdle(networkManager, task?.GetType().Name ?? "BossRush");

            yield return new WaitForSeconds(0.01f);
            networkManager.isConnect = true;
            networkManager.isTimeOut = false;
            networkManager.isError = false;
            try
            {
                if (task is BossRushRetireTask)
                {
                    // Retire always ends the local run. Clearing the registered
                    // deck makes the next entry follow the new-challenge deck
                    // selection flow instead of the stale Continue path.
                    BossRushOfflineData.ClearRun(true, true);
                }
                else if (task is BossRushLoseFinishTask)
                {
                    BossRushOfflineData.ClearRun(false);
                }
                JsonData data;
                if (!BossRushOfflineData.TryCreateResponse(task, out data))
                {
                    throw new InvalidOperationException("No BossRush response was generated.");
                }

                data["data_headers"]["servertime"] = (long)TimeNativePlugin.GetDeviceOperatingTime();
                task.SetResponseData(data);
                task.CheckResultCodeToPopupCreate_ReturnStatus(0);
                task.CallbackOnUnityWebRequestDone?.Invoke(null);
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogError($"[BossRush] Error processing {task.GetType().Name}: {exception}");
                task.CallbackOnFailure?.Invoke(NetworkTask.ResultCode.Error);
            }

            if (networkManager.NetworkUI != null)
            {
                networkManager.NetworkUI.StopLoading();
            }
            networkManager.ClearLastRequestTask();
            networkManager.isConnect = false;
        }

        private static bool IsLocalDeckListTask(string taskName)
        {
            return taskName == nameof(DeckDeleteTask) ||
                taskName == nameof(DeckOrderTask);
        }
        private static IEnumerator ProcessSkipTask(NetworkManager __instance, NetworkTask task)
        {
            yield return null;
            try
            {
                task.SetResponseData(LitJson.JsonMapper.ToObject("{}"));

                task.CheckResultCodeToPopupCreate_ReturnStatus(0);

                if (task.CallbackOnUnityWebRequestDone != null)
                {
                    task.CallbackOnUnityWebRequestDone(null);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Offlinizer] 跳过任务时出现警告 (通常可忽略) {task.GetType().Name}: {ex.Message}");
            }
            if (__instance.NetworkUI != null)
            {
                __instance.NetworkUI.StopLoading();
            }
            __instance.ClearLastRequestTask();
            __instance.isConnect = false;
        }

        private static IEnumerator ProcessOfflineTask(NetworkManager __instance, NetworkTask task, string taskName)
        {
            
            yield return WaitUntilNetworkIdle(__instance, taskName);
            yield return new WaitForSeconds(0.01f); // Optional: slight delay to simulate network latency

            __instance.isConnect = true;
            __instance.isTimeOut = false;
            __instance.isError = false;

            try
            {
                string filePath = Path.Combine("Mods", "OfflinizedTasks", $"{taskName}.json");
                JsonData data;

                if (LocalDeckCodeService.TryCreateResponse(task, out data))
                {
                    Plugin.Logger.LogInfo($"[DeckCode] Creating local response for {taskName}...");
                }
                else if (TryCreateLocalDeckListResponse(task, out data))
                {
                    Plugin.Logger.LogInfo($"[CustomFormats] Creating local deck-list response for {taskName}...");
                }
                else if (ProfileOfflineData.TryCreateResponse(task, out data))
                {
                    Plugin.Logger.LogInfo($"[Offlinizer] Injecting generated local profile data for {taskName}...");
                }
                else if (StoryOfflineData.TryCreateResponse(task, out data))
                {
                    Plugin.Logger.LogInfo($"[Offlinizer] Injecting generated local data for {taskName}...");
                }
                else if (PuzzleOfflineData.TryCreateResponse(task, out data))
                {
                    Plugin.Logger.LogInfo($"[Offlinizer] Injecting generated local puzzle data for {taskName}...");
                }
                else if (ReplayOfflineData.TryCreateResponse(task, out data))
                {
                    Plugin.Logger.LogInfo($"[Offlinizer] Injecting generated local replay data for {taskName}...");
                }
                else if (EmptyOfflineResponses.ContainsKey(taskName) || IsEmptyOfflineTask(taskName))
                {
                    Plugin.Logger.LogInfo($"[Offlinizer] Injecting empty local data for {taskName}...");
                    data = CreateEmptyOfflineResponse(taskName);
                }
                else if (File.Exists(filePath))
                {
                    string jsonText = File.ReadAllText(filePath);
                    data = JsonMapper.ToObject(jsonText);
                }
                else
                {
                    throw new FileNotFoundException("Local offline task data was not found.", filePath);
                }

                bool isLocalDeckCodeTask = LocalDeckCodeService.CanHandle(task);
                if (!isLocalDeckCodeTask)
                {
                    ProfileOfflineData.ApplyToResponse(task, data);
                }
                data["data_headers"]["servertime"] = (long)TimeNativePlugin.GetDeviceOperatingTime();
                task.SetResponseData(data);
                if (task is CheckSpecialTitleTask specialTitleTask)
                {
                    specialTitleTask.ParseTitleCheckData();
                }
                else
                {
                    task.CheckResultCodeToPopupCreate_ReturnStatus(0);
                }
                if (!isLocalDeckCodeTask)
                {
                    ProfileOfflineData.ReapplyCurrentSettings();
                }

                Plugin.Logger.LogInfo($"[Offlinizer] Successfully injected local data for {taskName}");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError(
                    $"[Offlinizer] Error processing local data for {taskName}: {ex}");
                if (LocalDeckCodeService.CanHandle(task))
                {
                    DialogBase dialog = UIManager.GetInstance().CreateConfirmationDialog(
                        "\u724c\u7ec4\u4ee3\u7801\u65e0\u6548\u3002\n" + ex.Message);
                    dialog.SetPanelDepth(2000, false);
                    dialog.SetSize(DialogBase.Size.M);
                    task.CallbackOnFailure?.Invoke(NetworkTask.ResultCode.Error);
                }
            }

            __instance.ClearLastRequestTask();
            __instance.isConnect = false;
        }

        private static bool TryCreateLocalDeckListResponse(NetworkTask task, out JsonData data)
        {
            if (!(task is DeckDeleteTask) && !(task is DeckOrderTask))
            {
                data = null;
                return false;
            }

            data = JsonMapper.ToObject(
                "{\"data_headers\":{\"short_udid\":0,\"viewer_id\":0," +
                "\"sid\":\"\",\"servertime\":0,\"result_code\":1}," +
                "\"data\":{\"user_deck_list\":[]}}");
            return true;
        }

        private static IEnumerator ProcessOnlineTask(NetworkManager __instance, NetworkTask task, bool showErrorDialog)
        {
            yield return WaitUntilNetworkIdle(__instance, task != null ? task.GetType().Name : "online");

            __instance.isConnect = true;
            __instance.isTimeOut = false;
            __instance.isError = false;
            if (__instance.NetworkUI != null && __instance._showLoadingIcon)
            {
                __instance.NetworkUI.StartLoading(false);
            }

            // 这条路径到处都会 throw（MessagePack 解密失败、HandleDeserializeException…）。
            // 一旦异常逃出协程，下面那句 isConnect = false 就不会执行，网络忙标志会永远留着，
            // 后续任务全部卡死 —— 所以用 finally 兜住，保证一定清掉。
            try
            {
            bool isLogTraceCheckUri = false;
            if (__instance.lastRequestTask is DoMatchingBase || __instance.lastRequestTask is FinishTaskBase)
            {
                isLogTraceCheckUri = true;
            }
            string url = __instance.lastRequestTask.Url;
            NetworkTask networkTask = __instance.lastRequestTask;
            if (isLogTraceCheckUri)
            {
                __instance.LogTraceCheck("1");
            }
            using (UnityWebRequest unityWebRequest = __instance.GetUnityWebRequestInstance(url))
            {
                yield return unityWebRequest.SendWebRequest();
                if (isLogTraceCheckUri)
                {
                    __instance.LogTraceCheck("2");
                }
                float endTime = Time.realtimeSinceStartup + 30f;
                if (__instance.lastRequestTask.GetType().Equals(typeof(CheckSpecialTitleTask)))
                {
                    endTime = Time.realtimeSinceStartup + 2f;
                }
                while (!unityWebRequest.isDone && Time.realtimeSinceStartup < endTime)
                {
                    yield return 0;
                }
                if (isLogTraceCheckUri)
                {
                    __instance.LogTraceCheck("3");
                }
                if (__instance.NetworkUI != null)
                {
                    __instance.NetworkUI.StopLoading();
                }
                if (!unityWebRequest.isDone)
                {
                    __instance.isTimeOut = true;
                    LocalLog.AccumulateTraceLog("Connect is TimeOut");
                    __instance.disposeUnityWebRequest(unityWebRequest);
                    if (!__instance.lastRequestTask.isSkipCommonTimeOutPopUp())
                    {
                        if (__instance.lastRequestTask.GetType().Equals(typeof(PackOpenTask)) || __instance.lastRequestTask.GetType().Equals(typeof(BuildDeckBuyTask)) || __instance.lastRequestTask.GetType().Equals(typeof(SleeveBuyTask)) || __instance.lastRequestTask.GetType().Equals(typeof(SkinBuyMultiRewardTask)) || __instance.lastRequestTask.GetType().Equals(typeof(SkinBuyMultiTask)) || __instance.lastRequestTask.GetType().Equals(typeof(SkinBuySingleTask)) || __instance.lastRequestTask.GetType().Equals(typeof(ItemPurchaseBuyTask)) || __instance.lastRequestTask.GetType().Equals(typeof(SpotCardExchangeTask)) || __instance.lastRequestTask.GetType().Equals(typeof(CardCreateTask)) || __instance.lastRequestTask.GetType().Equals(typeof(CardDestructTask)) || __instance.lastRequestTask.GetType().Equals(typeof(StoryFinishTask)) || __instance.lastRequestTask.GetType().Equals(typeof(PracticeFinishTask)) || __instance.lastRequestTask.GetType().Equals(typeof(BingoDrawTask)) || __instance.lastRequestTask.GetType().Equals(typeof(MypageTreasureBoxCpOpenTask)) || __instance.lastRequestTask.GetType().Equals(typeof(MypageReceiveSpecialTreasureTask)) || __instance.lastRequestTask.GetType().Equals(typeof(FreeCardPackCampaignFinishTask)))
                        {
                            __instance.NetworkUI.OpenGoToTitleErrorPopUp(Data.SystemText.Get("ErrorHeader_0012"), Data.SystemText.Get("Error_0012"), "");
                        }
                        else
                        {
                            __instance.NetworkUI.OpenTimeOutErrorPopUp();
                        }
                    }
                    if (__instance.lastRequestTask.CallbackOnFailure != null)
                    {
                        if (__instance.lastRequestTask.GetType().Equals(typeof(PaymentPCFinishTask)))
                        {
                            __instance.NetworkUI.OpenGoToTitleErrorPopUp(Data.SystemText.Get("ErrorHeader_0012"), Data.SystemText.Get("Error_0012"), "");
                        }
                        else
                        {
                            __instance.lastRequestTask.CallbackOnFailure(NetworkTask.ResultCode.TimeOut);
                        }
                    }
                    Toolbox.DeviceManager.ClearIpAddress();
                }
                else if (!string.IsNullOrEmpty(unityWebRequest.error))
                {
                    LocalLog.AccumulateTraceLog("Connect is Error!" + unityWebRequest.error + " responseCode:" + unityWebRequest.responseCode.ToString());
                    __instance.isError = true;
                    if (showErrorDialog && !__instance.lastRequestTask.isSkipCommonHttpStatusErrorPopUp())
                    {
                        if (__instance.lastRequestTask.GetType().Equals(typeof(PackOpenTask)) || __instance.lastRequestTask.GetType().Equals(typeof(PaymentPCFinishTask)))
                        {
                            __instance.NetworkUI.OpenGoToTitleErrorPopUp(Data.SystemText.Get("ErrorHeader_0012"), Data.SystemText.Get("Error_0012"), "");
                        }
                        else
                        {
                            __instance.NetworkUI.OpenHttpStatusErrorPopUp();
                        }
                    }
                    __instance.disposeUnityWebRequest(unityWebRequest);
                    if (__instance.lastRequestTask.CallbackOnFailure != null)
                    {
                        __instance.lastRequestTask.CallbackOnFailure(NetworkTask.ResultCode.Error);
                    }
                    Toolbox.DeviceManager.ClearIpAddress();
                }
                else if (unityWebRequest.isDone)
                {
                    if (__instance.lastRequestTask.CallbackOnUnityWebRequestDone != null)
                    {
                        __instance.lastRequestTask.CallbackOnUnityWebRequestDone(unityWebRequest);
                    }
                    else
                    {
                        if (unityWebRequest.downloadHandler.text != null && unityWebRequest.downloadHandler.text != "")
                        {
                            try
                            {
                                byte[] array;
                                if (__instance.isEncrypt)
                                {
                                    array = CryptAES.decrypt(unityWebRequest.downloadHandler.text);
                                }
                                else
                                {
                                    array = Convert.FromBase64String(unityWebRequest.downloadHandler.text);
                                }
                                string text;
                                if (!__instance.isUseJson)
                                {
                                    text = MessagePackSerializer.ToJson(array);
                                }
                                else
                                {
                                    text = MessagePackSerializer.ToJson(array);
                                }
                                string taskTypeName = task.GetType().Name;
                                JsonData parsedResponse = JsonMapper.ToObject(text);
                                __instance.lastRequestTask.SetResponseData(parsedResponse);

                                // BossRush's opponent table and ability candidates are
                                // service data, not part of the AI master. Preserve the
                                // original response so it can be converted into a local
                                // authoring reference by BossRushReferenceExporter.
                                BossRushReferenceExporter.CaptureResponse(taskTypeName, parsedResponse);

                                // Offlinizer: Save the response data to a local file for future offline use
                                File.WriteAllText((Path.Combine("Mods", "OfflinizedTasks", $"{taskTypeName}.json")), text);
                            }
                            catch (Exception ex)
                            {
                                string text2 = unityWebRequest.downloadHandler.text;
                                __instance.disposeUnityWebRequest(unityWebRequest);
                                if (!__instance.lastRequestTask.GetType().Equals(typeof(CheckSpecialTitleTask)))
                                {
                                    if (!__instance.isEncrypt)
                                    {
                                        LocalLog.AccumulateTraceLog(ex.ToString());
                                        throw ex;
                                    }
                                    global::Debug.LogError(text2, null);
                                    global::Debug.LogError(ex.Message, null);
                                    global::Debug.LogError(ex.StackTrace, null);
                                    if (text2.Contains("php"))
                                    {
                                        if (text2.Length > 1800)
                                        {
                                            throw new Exception(text2.Substring(1, 1800));
                                        }
                                        throw new Exception(text2);
                                    }
                                    else
                                    {
                                        __instance.HandleDeserializeException(ex);
                                    }
                                }
                            }
                            try
                            {
                                if (__instance.lastRequestTask != null)
                                {
                                    if (__instance.lastRequestTask.GetType().Equals(typeof(CheckSpecialTitleTask)))
                                    {
                                        ((CheckSpecialTitleTask)__instance.lastRequestTask).ParseTitleCheckData();
                                    }
                                    else
                                    {
                                        NetworkTask.ERROR_CODE_STATUS error_CODE_STATUS = __instance.lastRequestTask.CheckResultCodeToPopupCreate_ReturnStatus(0);
                                        if (error_CODE_STATUS == NetworkTask.ERROR_CODE_STATUS.ERROR)
                                        {
                                            __instance.isError = true;
                                        }
                                        if (error_CODE_STATUS == NetworkTask.ERROR_CODE_STATUS.ERROR_TO_MAINTENANCE_POPUP && __instance.lastRequestTask.CallbackOnFailure != null)
                                        {
                                            __instance.lastRequestTask.CallbackOnFailure(NetworkTask.ResultCode.Maintenance);
                                        }
                                        if (error_CODE_STATUS == NetworkTask.ERROR_CODE_STATUS.ERROR && __instance.lastRequestTask.CallbackOnFailure != null)
                                        {
                                            __instance.lastRequestTask.CallbackOnFailure(NetworkTask.ResultCode.Title);
                                        }
                                    }
                                }
                                goto IL_0838;
                            }
                            catch (Exception ex2)
                            {
                                __instance.disposeUnityWebRequest(unityWebRequest);
                                if (!__instance.lastRequestTask.GetType().Equals(typeof(CheckSpecialTitleTask)))
                                {
                                    string text3 = "NetworkManager Connect Error 2：";
                                    Exception ex3 = ex2;
                                    LocalLog.AccumulateTraceLog(text3 + ((ex3 != null) ? ex3.ToString() : null));
                                    throw ex2;
                                }
                                goto IL_0838;
                            }
                        }
                        LocalLog.AccumulateTraceLog("NetworkManager Connect Error 3");
                    }
                }
            IL_0838:
                __instance.ClearLastRequestTask();
                __instance.disposeUnityWebRequest(unityWebRequest);
                __instance.isConnect = false;
            }
            }
            finally
            {
                // 正常走完时上面已经清过了，这里再做一次幂等的收尾；异常逃出时全靠它。
                __instance.ClearLastRequestTask();
                __instance.isConnect = false;
            }
            //UnityWebRequest unityWebRequest = null;
            yield break;
        }
    }
}
