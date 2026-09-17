using System;
using HarmonyLib;
using UnityEngine;
using Wizard.Battle.Tutorial;
using Wizard.Battle.View.Vfx;

namespace Shadowbus
{
    /// <summary>
    /// 修游戏里「按牌库格数取子物体」的越界。
    ///
    /// 游戏的牌库容器（<c>BattleManagerBase.CardHolder</c> / <c>ECardHolder</c>）按牌库格数
    /// 摆卡，多处特效都这样取某一张：
    ///
    ///     if (holderObject.transform.childCount &gt; 0)
    ///         card = holderObject.transform.GetChild(battleMgr.GetMaxDeckCount(isPlayer)).gameObject;
    ///
    /// 只判断了「有没有子物体」，却把「牌库格数」直接当下标用。容器子物体不足格数 + 1 个时
    /// <c>GetChild</c> 抛 <c>UnityException: Transform child out of bounds</c>。
    ///
    /// 全程序集共 7 处这样取下标，横跨各模式：
    ///   · <c>SetShortageDeckWinVfx</c>「牌库耗尽获胜」翻牌（单人对局 / 双选 / 联机 / 房间对战）
    ///   · <c>PuzzleBattleManager.SetReaperCard</c>（解谜模式的死神卡）
    ///   · <c>NewReplayBattleMgr.SetShortageDeckWin</c>（对局回放）
    ///   · <c>PlayerDeckOutVfx</c> / <c>EnemyDeckOutVfx</c> / <c>DeckOutWinVfx</c>（牌库耗尽特效）
    ///
    /// 崩在 <c>SetShortageDeckWinVfx</c> 里最要命，因为那个构造函数跑在技能胜负判定<b>之后</b>
    /// （buff 和获胜状态早已发下去），异常会把整条出牌流程断掉，随后 CheckPlayCardInHand
    /// 再抛一次，表现为按下即卡死。
    ///
    /// 这 7 处调的都是同一个虚方法 <c>GetMaxDeckCount(bool)</c>，它在全程序集只有三个实现
    /// （<c>BattleManagerBase</c> 常量 40、<c>NetworkBattleManagerBase</c> 取
    /// <c>DataMgr.GetDeckMaxCount</c>、<c>TutorialBattleMgrBase</c>），联机与房间对战走的是
    /// 重写版本，所以三个都要挂。
    ///
    /// 处理办法不是砍掉特效，而是把那一次取值收敛到合法下标：取值越界时改成容器最后一个
    /// 子物体，动画照常播（动的是一张真实存在的牌）。取值本来就合法时一个字都不改，
    /// 所以正常情况下对所有调用点都没有影响。
    /// </summary>
    public static class BattleVfxGuards
    {
        private const string OutOfBoundsMarker = "child out of bounds";
        private const int DiagnosticLines = 5;

        // 前缀里需要「问一次原值」，这里防止又绕回自己。
        [ThreadStatic] private static bool _resolvingDeckSlot;

        private static int _clampedLogCount;

        [HarmonyPatch(typeof(BattleManagerBase), nameof(BattleManagerBase.GetMaxDeckCount))]
        [HarmonyPrefix]
        public static bool BattleManagerBase_GetMaxDeckCount_Prefix(bool isSelf, ref int __result)
        {
            return TryClampDeckSlot(isSelf, ref __result);
        }

        // 联机、房间对战走的是这个重写。
        [HarmonyPatch(typeof(NetworkBattleManagerBase), nameof(NetworkBattleManagerBase.GetMaxDeckCount))]
        [HarmonyPrefix]
        public static bool NetworkBattleManagerBase_GetMaxDeckCount_Prefix(bool isSelf, ref int __result)
        {
            return TryClampDeckSlot(isSelf, ref __result);
        }

        [HarmonyPatch(typeof(TutorialBattleMgrBase), nameof(TutorialBattleMgrBase.GetMaxDeckCount))]
        [HarmonyPrefix]
        public static bool TutorialBattleMgrBase_GetMaxDeckCount_Prefix(bool isSelf, ref int __result)
        {
            return TryClampDeckSlot(isSelf, ref __result);
        }

        private static bool TryClampDeckSlot(bool isSelf, ref int __result)
        {
            if (_resolvingDeckSlot)
            {
                return true;
            }

            BattleManagerBase battleMgr = BattleManagerBase.GetIns();
            if (battleMgr == null)
            {
                return true;
            }

            GameObject holderObject = isSelf ? battleMgr.CardHolder : battleMgr.ECardHolder;
            if (holderObject == null || holderObject.transform == null)
            {
                return true;
            }

            int childCount = holderObject.transform.childCount;
            if (childCount <= 0)
            {
                // 调用方自己会走 childCount > 0 的 else 分支直接 return，不用管。
                return true;
            }

            // 先问出原值。临时立起标志，免得又绕回这个前缀。
            int requested;
            _resolvingDeckSlot = true;
            try
            {
                requested = battleMgr.GetMaxDeckCount(isSelf);
            }
            finally
            {
                _resolvingDeckSlot = false;
            }

            if (requested <= childCount - 1)
            {
                // 本来就不越界，一个字都不改。
                return true;
            }

            __result = childCount - 1;

            if (_clampedLogCount < DiagnosticLines)
            {
                _clampedLogCount++;
                Plugin.Logger.LogInfo(
                    $"[BattleVfx] Deck slot {requested} is out of bounds " +
                    $"({(isSelf ? "own" : "enemy")} holder has {childCount} child(ren)); " +
                    $"using slot {__result} instead so the effect can still play.");
            }

            return false;
        }

        /// <summary>
        /// 保险网：万一还有没覆盖到的取下标路径，至少只丢那一下动画，不再把整场对局拖死。
        /// </summary>
        [HarmonyPatch(typeof(SetShortageDeckWinVfx), MethodType.Constructor, new[] { typeof(bool) })]
        [HarmonyFinalizer]
        public static Exception SetShortageDeckWinVfx_Ctor_Finalizer(Exception __exception)
        {
            if (__exception == null)
            {
                return null;
            }

            if (!(__exception is UnityException) ||
                __exception.Message.IndexOf(OutOfBoundsMarker, StringComparison.OrdinalIgnoreCase) < 0)
            {
                // 不是已知的那一种越界，原样抛回。
                return __exception;
            }

            Plugin.Logger.LogWarning(
                "[BattleVfx] Skipped the shortage-deck-win card flip VFX after an " +
                "out-of-bounds deck slot. The skill's win effect is already applied, so the " +
                "battle continues without that animation.");

            // 返回 null = 吞掉。base..ctor() 已经跑过，对象是空的 SequentialVfxPlayer，
            // 队列为空、没有当前步骤，对外就是「立刻播完」。
            return null;
        }
    }
}
