using System;
using HarmonyLib;
using Wizard.Battle.Mulligan;
using Wizard.Battle.Phase;

namespace Shadowbus
{
    /// <summary>
    /// 临时诊断（定位完就删）：回放播放停在换牌界面时，日志里现在什么都没有，
    /// 分不清是「播放器没吃到 op」「换牌阶段没收尾」「对局没开始」还是「谁把回放暂停了」。
    /// 这里只打日志、不改任何状态：
    ///
    ///   · <c>[ReplayTrace] op …</c>      —— 播放器实际消费的每一条 op（开头 40 条 + 所有开局 op）
    ///   · <c>[ReplayTrace] mulligan …</c> —— 换牌收尾 / 换牌阶段拆除
    ///   · <c>[ReplayTrace] StartBattle</c> —— 对局真正开始（以及它抛的异常）
    ///   · <c>[ReplayTrace] paused</c>     —— 谁把回放置成暂停（带调用栈）
    ///   · 操作抛异常时的记录与兜底见 <see cref="ReplayPlaybackGuards"/>（`[ReplayGuard] …`）
    /// </summary>
    public static class ReplayPlaybackTrace
    {
        private const int MaxOpsToLog = 40;

        private static int _opCount;

        private static int _pausedLogCount;

        [HarmonyPatch(typeof(NetworkBattleManagerBase), "ConductReplayReceiveData")]
        [HarmonyPrefix]
        private static void ConductReplayReceiveData_Prefix(NetworkBattleReceiver.ReplayReceiveData receiveData)
        {
            try
            {
                if (receiveData == null)
                {
                    return;
                }

                int operation = (int)receiveData.Operation;
                _opCount++;
                if (operation > 3 && _opCount > MaxOpsToLog)
                {
                    return;
                }

                int self = receiveData.selfIdxList?.Count ?? -1;
                int oppo = receiveData.oppoIdxList?.Count ?? -1;
                Plugin.Logger.LogInfo(
                    $"[ReplayTrace] op#{_opCount} type={receiveData.Operation}({operation}) " +
                    $"isSelf={receiveData.isSelf} selfIdx={self} oppoIdx={oppo}");
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[ReplayTrace] op log failed: {exception.Message}");
            }
        }

        // 异常的处理（记录 + 兜底吞掉，避免整条播放协程被打死）在 ReplayPlaybackGuards 里，
        // 这里只保留诊断日志 —— 同一个方法上只挂一个 finalizer，免得两者互相覆盖返回值。

        [HarmonyPatch(typeof(WatchMulliganMgr), "CompleteMulligan")]
        [HarmonyPostfix]
        private static void WatchMulliganMgr_CompleteMulligan_Postfix()
        {
            Plugin.Logger.LogInfo("[ReplayTrace] mulligan complete (battle should start now)");
        }

        [HarmonyPatch(typeof(WatchMulliganPhase), "Teardown")]
        [HarmonyPostfix]
        private static void WatchMulliganPhase_Teardown_Postfix()
        {
            Plugin.Logger.LogInfo("[ReplayTrace] mulligan phase teardown");
        }

        [HarmonyPatch(typeof(BattleManagerBase), "StartBattle")]
        [HarmonyPrefix]
        private static void BattleManagerBase_StartBattle_Prefix()
        {
            Plugin.Logger.LogInfo("[ReplayTrace] StartBattle ->");
        }

        [HarmonyPatch(typeof(BattleManagerBase), "StartBattle")]
        [HarmonyPostfix]
        private static void BattleManagerBase_StartBattle_Postfix()
        {
            Plugin.Logger.LogInfo("[ReplayTrace] StartBattle done");
        }

        [HarmonyPatch(typeof(BattleManagerBase), "StartBattle")]
        [HarmonyFinalizer]
        private static Exception BattleManagerBase_StartBattle_Finalizer(Exception __exception)
        {
            if (__exception != null)
            {
                Plugin.Logger.LogError($"[ReplayTrace] StartBattle threw: {__exception}");
            }

            return __exception;
        }

        [HarmonyPatch(typeof(NewReplayBattleMgr), "PauseReplay")]
        [HarmonyPostfix]
        private static void NewReplayBattleMgr_PauseReplay_Postfix()
        {
            if (_pausedLogCount >= 5)
            {
                return;
            }

            _pausedLogCount++;
            Plugin.Logger.LogWarning(
                "[ReplayTrace] paused the replay" +
                (_pausedLogCount == 1 ? "\n" + Environment.StackTrace : string.Empty));
        }
    }
}
