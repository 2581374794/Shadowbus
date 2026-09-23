using System;
using System.Collections.Generic;
using HarmonyLib;
using Wizard;
using Wizard.Battle.UI;

namespace Shadowbus
{
    /// <summary>
    /// 技巧卡（勇气抉择的第一个选项）在原版数据里 <c>Cost = -1</c> —— 24 个 leader 里
    /// 每一个「XX的技巧」都是 -1（秘术/奥义是正常费用，见 <c>_tools/ability_card_costs.py</c>）。
    ///
    /// 而战斗日志 <c>BattleLogManager.SetUp</c> 只给费用 0..30 建了桶：
    /// <c>_AddDestLogCommonOne</c> 拿 <c>BaseParameter.Cost</c> 去桶里
    /// <c>FirstOrDefault(x =&gt; x.Cost == cost)</c>，-1 找不到就返回 null，紧接着取
    /// <c>.LogInfoList</c> —— 直接空引用。这个异常发生在
    /// <c>TouchControl.Update → PlayCardProcessor.End → OperateMgr.PlayCard</c> 的重试链上，
    /// 于是**每帧抛一次**，那张技巧卡永远打不出去、就一直半透明地挂在手上。
    ///
    /// 这里只做一件事：**这个费用根本没有桶**时跳过这条日志（原版本来也画不出这一行），
    /// 并打一行说明是哪张卡、费用多少。其余情况一律走原版，不碰任何数据。
    /// </summary>
    internal static class BattleLogPatch
    {
        /// <summary>已经警告过的卡号，避免每帧刷日志。</summary>
        private static readonly HashSet<int> Warned = new HashSet<int>();

        [HarmonyPatch(typeof(BattleLogManager), "_AddDestLogCommonOne")]
        [HarmonyPrefix]
        internal static bool AddDestLogCommonOne_Prefix(
            BattleLogManager __instance,
            BattleCardBase __0,
            BattleLogWindow.BattleLogType __2,
            bool __3)
        {
            if (__instance == null || __0 == null)
            {
                return true;
            }

            int cost;
            try
            {
                CardParameter parameter = __0.BaseParameter;
                if (parameter == null)
                {
                    return true;
                }

                cost = parameter.Cost;
            }
            catch (Exception)
            {
                return true;
            }

            List<BattleLogManager.CostCardLogInfo> buckets;
            try
            {
                buckets = __instance.GetCostCardLogInfoList(__2, __3);
            }
            catch (Exception)
            {
                return true;
            }

            if (buckets == null)
            {
                return true;
            }

            for (int i = 0; i < buckets.Count; i++)
            {
                BattleLogManager.CostCardLogInfo bucket = buckets[i];
                if (bucket != null && bucket.Cost == cost)
                {
                    return true;      // 有对应的桶，原版照走
                }
            }

            int cardId;
            try
            {
                cardId = __0.CardId;
            }
            catch (Exception)
            {
                cardId = 0;
            }

            if (Warned.Add(cardId))
            {
                Plugin.Logger.LogWarning(
                    $"[BattleLog] card {cardId} has cost {cost}, and the battle log has no cost bucket for it " +
                    "(vanilla only builds 0-30; -1 is how the official data marks the 勇气抉择 cards). " +
                    "Skipping that log entry instead of letting the vanilla null reference repeat every frame.");
            }

            return false;
        }
    }
}
