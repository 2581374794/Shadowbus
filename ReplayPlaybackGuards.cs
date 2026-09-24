using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Shadowbus
{
    /// <summary>
    /// 回放播放的兜底：一条对不上的操作不再把整场回放"钉死"。
    ///
    /// 现场（玩家反馈的 `回放报错.txt`）：
    ///
    ///   NewReplayBattleMgr.ReplaceReceivedCard(originalCard, …)   ← originalCard 为 null → NRE
    ///    ↑ NewReplayOperationCollection.CompleteFusionOperation()  ← 第 605/613 行
    ///    ↑ NewReplayOperateReceive.StartReplayOperate()
    ///    ↑ NetworkBattleManagerBase.ConductReplayReceiveData()
    ///    ↑ NetworkBattleReceiver.ReceivedReplayMessage()
    ///    ↑ Wizard.RoomMatch.ReplayDataHandler.NewStockDataPlayer()  ← 播放协程
    ///
    /// 这类 NRE 的根都在同一个写法上：原版**不判空**地拿
    /// `BattleManagerBase.GetBattleCardIdx(手牌列表, 索引)` 的返回值直接用，
    /// 而它找不到就返回 null（`list.SingleOrDefault(c =&gt; c.Index == idx)`）：
    ///
    ///   · 融合完成时，对手那张卡在手牌里查不到（记录里 `CardInfo.Index` 与实际手牌序列对不上）；
    ///   · 同理还有开融合/取消融合（`StartFusionOperation` / `CancelFusionOperation`，
    ///     它们甚至不判 `isSelf`，永远拿**自己**的手牌去查对手的索引 —— 说明这条路原版就没测过）。
    ///
    /// 更糟的是异常会从 `NewStockDataPlayer()` 协程里逃出去，**协程直接终止**：
    /// 玩家的观感是回放停在那不动（我们之前已经用同样的思路修过发牌那一步：
    /// `ReplayOfflineData.UpdateSkillDescriptionValueList_Prefix`）。
    ///
    /// 处理分两层，数据正常时行为一个字都不变：
    ///   1. 融合相关操作先校验：目标卡/材料卡在当前手牌里找不到就**整条跳过**并记日志
    ///      （跳过比"只改一半状态"安全：卡还是原来那张，索引也还找得到）；
    ///   2. 给 `ConductReplayReceiveData` 加 finalizer：漏网的"数据对不上"类异常只记日志、
    ///      不再往上抛，于是单条坏数据不会杀死整条播放协程，回放继续往下播。
    /// </summary>
    public static class ReplayPlaybackGuards
    {
        private static readonly FieldInfo ReceivedDataField =
            AccessTools.Field(typeof(NewReplayOperationCollection), "_receivedData");

        private static readonly FieldInfo BattlePlayerField =
            AccessTools.Field(typeof(NewReplayOperationCollection), "ReplayBattlePlayer");

        private static readonly FieldInfo BattleEnemyField =
            AccessTools.Field(typeof(NewReplayOperationCollection), "ReplayBattleEnemy");

        private static int _skippedOps;
        private static int _swallowedExceptions;
        private const int MaxDetailLogs = 20;

        // ---------------------------------------------------------------- 融合：先校验再执行

        [HarmonyPatch(typeof(NewReplayOperationCollection), "CompleteFusionOperation")]
        [HarmonyPrefix]
        private static bool CompleteFusionOperation_Prefix(NewReplayOperationCollection __instance)
        {
            NetworkBattleReceiver.ReplayReceiveData data;
            List<BattleCardBase> hand;
            try
            {
                data = GetReceiveData(__instance);
                if (data == null)
                {
                    return true;
                }

                if (data.CardInfo == null)
                {
                    return SkipFusionOp("CompleteFusion", "记录里没有 CardInfo（融合目标卡）");
                }

                hand = GetHand(__instance, data.isSelf);
                if (hand == null)
                {
                    // 拿不到手牌列表就不干预，交给原逻辑（并让 finalizer 兜底）。
                    return true;
                }

                if (!ContainsIndex(hand, data.CardInfo.Index))
                {
                    return SkipFusionOp(
                        "CompleteFusion",
                        $"融合目标卡 index={data.CardInfo.Index} 不在" +
                        (data.isSelf ? "自己" : "对手") + "手牌里");
                }

                if (data.CardInfoList != null)
                {
                    foreach (NetworkBattleReceiver.CardInfo ingredient in data.CardInfoList)
                    {
                        if (ingredient == null || !ContainsIndex(hand, ingredient.Index))
                        {
                            return SkipFusionOp(
                                "CompleteFusion",
                                "融合材料卡 index=" +
                                (ingredient == null ? "<null>" : ingredient.Index.ToString()) +
                                " 不在" + (data.isSelf ? "自己" : "对手") + "手牌里");
                        }
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug(
                    $"[ReplayGuard] fusion pre-check failed, leaving the op to the stock code: {exception.Message}");
                return true;
            }
        }

        [HarmonyPatch(typeof(NewReplayOperationCollection), "StartFusionOperation")]
        [HarmonyPrefix]
        private static bool StartFusionOperation_Prefix(NewReplayOperationCollection __instance)
        {
            return CheckHandIndexes(__instance, "StartFusion", useSelfHandOnly: true);
        }

        [HarmonyPatch(typeof(NewReplayOperationCollection), "CancelFusionOperation")]
        [HarmonyPrefix]
        private static bool CancelFusionOperation_Prefix(NewReplayOperationCollection __instance)
        {
            return CheckHandIndexes(__instance, "CancelFusion", useSelfHandOnly: true);
        }

        /// <summary>
        /// 开融合/取消融合都只查 `CardIndex`（以及可选的 `TargetIndexList`），
        /// 而且原版固定用**自己**的手牌去查，这里照它的语义校验。
        /// </summary>
        private static bool CheckHandIndexes(
            NewReplayOperationCollection instance,
            string opName,
            bool useSelfHandOnly)
        {
            try
            {
                NetworkBattleReceiver.ReplayReceiveData data = GetReceiveData(instance);
                if (data == null)
                {
                    return true;
                }

                List<BattleCardBase> hand = GetHand(instance, useSelfHandOnly || data.isSelf);
                if (hand == null)
                {
                    return true;
                }

                if (!ContainsIndex(hand, data.CardIndex))
                {
                    return SkipFusionOp(opName, $"index={data.CardIndex} 不在手牌里");
                }

                if (data.TargetIndexList != null)
                {
                    foreach (int target in data.TargetIndexList)
                    {
                        if (!ContainsIndex(hand, target))
                        {
                            return SkipFusionOp(opName, $"可融合材料 index={target} 不在手牌里");
                        }
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug(
                    $"[ReplayGuard] {opName} pre-check failed, leaving the op to the stock code: {exception.Message}");
                return true;
            }
        }

        private static bool SkipFusionOp(string opName, string reason)
        {
            _skippedOps++;
            if (_skippedOps <= MaxDetailLogs)
            {
                Plugin.Logger.LogWarning(
                    $"[ReplayGuard] skipped a broken {opName} op ({reason}); " +
                    "the replay keeps playing instead of stopping here " +
                    "(stock code dereferences the missing card and kills the playback coroutine).");
            }
            else if (_skippedOps == MaxDetailLogs + 1)
            {
                Plugin.Logger.LogWarning(
                    "[ReplayGuard] further broken fusion ops in this replay are skipped silently.");
            }

            // false = 不执行原方法，这条操作被丢弃。
            return false;
        }

        // ---------------------------------------------------------------- 漏网异常：不再杀死播放协程

        [HarmonyPatch(typeof(NetworkBattleManagerBase), "ConductReplayReceiveData")]
        [HarmonyFinalizer]
        private static Exception ConductReplayReceiveData_Finalizer(
            Exception __exception,
            NetworkBattleReceiver.ReplayReceiveData receiveData)
        {
            if (__exception == null)
            {
                return null;
            }

            string opName = receiveData == null
                ? "<null>"
                : receiveData.Operation.ToString();

            if (!IsRecoverable(__exception))
            {
                Plugin.Logger.LogError(
                    $"[ReplayGuard] op {opName} failed with an unexpected exception " +
                    $"(not swallowed): {__exception}");
                return __exception;
            }

            _swallowedExceptions++;
            if (_swallowedExceptions <= MaxDetailLogs)
            {
                Plugin.Logger.LogWarning(
                    $"[ReplayGuard] op {opName} threw {__exception.GetType().Name}; " +
                    "swallowed it so the replay keeps advancing. " +
                    "The recorded data does not match the playback state at this point " +
                    "(mod cards changed/removed, or the recording is incomplete). " +
                    $"Detail: {__exception.Message}");
            }
            else if (_swallowedExceptions == MaxDetailLogs + 1)
            {
                Plugin.Logger.LogWarning(
                    "[ReplayGuard] further broken ops in this replay are swallowed silently.");
            }

            // 返回 null = 吞掉异常。播放协程 NewStockDataPlayer 于是继续处理下一条 op。
            return null;
        }

        /// <summary>「数据对不上」这一族的异常（返回 null 的取下标、找不到的字典键、重复索引等）。</summary>
        private static bool IsRecoverable(Exception exception)
        {
            return exception is NullReferenceException
                || exception is IndexOutOfRangeException
                || exception is ArgumentOutOfRangeException
                || exception is KeyNotFoundException
                || exception is InvalidOperationException
                || exception is ArgumentException;
        }

        // ---------------------------------------------------------------- 反射取值

        private static NetworkBattleReceiver.ReplayReceiveData GetReceiveData(
            NewReplayOperationCollection instance)
        {
            return instance == null
                ? null
                : ReceivedDataField?.GetValue(instance) as NetworkBattleReceiver.ReplayReceiveData;
        }

        private static List<BattleCardBase> GetHand(
            NewReplayOperationCollection instance,
            bool isSelf)
        {
            if (instance == null)
            {
                return null;
            }

            BattlePlayerBase player = (isSelf ? BattlePlayerField : BattleEnemyField)
                ?.GetValue(instance) as BattlePlayerBase;
            return player?.HandCardList;
        }

        private static bool ContainsIndex(List<BattleCardBase> hand, int index)
        {
            if (hand == null)
            {
                return false;
            }

            for (int i = 0; i < hand.Count; i++)
            {
                BattleCardBase card = hand[i];
                if (card != null && card.Index == index)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
