using System;
using HarmonyLib;

namespace Shadowbus
{
    /// <summary>
    /// 把「游戏窗口失去焦点」这件事明确写进日志。
    ///
    /// 游戏自己规定：窗口一失焦就 <c>UnityEventAgent.OnApplicationFocus(false)</c> →
    /// <c>m_battleMgr.Pause()</c>，同时所有战斗操作入口都拿焦点当门槛
    /// （<c>TouchControl</c>、<c>SelectCardProcessor</c>、<c>AttackTargetSelectTouchProcessor</c>、
    /// <c>EvolutionTouchProcessor</c>、<c>FusionWaitProcessor</c>、<c>MulliganOperateControl</c>
    /// 等全部判 <c>BattleManagerBase.HasFocus</c> / <c>UIManager.ApplicationHasFocus</c>）。
    ///
    /// 关键点是**这个暂停只停本地输入/表现，不停网络**：Socket.IO 心跳、服务器转发、
    /// 回合计时器（`TurnEndTimeController`）都照常跑。于是玩家的观感是「牌桌卡住、
    /// 点什么都没反应」，而服务器那边只看到「这个玩家一直不发操作」，对手则一直等 ——
    /// 几份玩家反馈的「联机卡死」实测都是这个原因（客户端日志里能看到
    /// `【UnityEventAgent】OnApplicationFocus is called!! isFocus：False` 之后再没出现过 True，
    /// 期间连一次触摸/选牌帧都没发出来）。
    ///
    /// 这个补丁**只打日志、不改任何行为**，目的就是让以后任何一份玩家日志都能一眼看出
    /// 「当时窗口是不是没焦点」。同时提醒玩家：点回游戏窗口即可恢复。
    /// </summary>
    public static class FocusDiagnostics
    {
        private static int _logCount;
        private const int MaxLogs = 40;

        /// <summary>本机窗口最后一次的焦点状态（null = 还没收到过焦点事件）。联机看门狗会把它写进告警里。</summary>
        public static bool? LastWindowFocused { get; private set; }

        /// <summary>给日志用的一句摘要，例如“本机窗口焦点：无（对局已暂停）”。</summary>
        public static string DescribeLocalFocus()
        {
            if (!LastWindowFocused.HasValue)
            {
                return " 本机窗口焦点：未知。";
            }

            return LastWindowFocused.Value
                ? " 本机窗口焦点：有。"
                : " 本机窗口焦点：**无（游戏已暂停对局并屏蔽出牌，回合计时器仍在走）**。";
        }

        [HarmonyPatch(typeof(UnityEventAgent), "OnApplicationFocus")]
        [HarmonyPostfix]
        public static void OnApplicationFocus_Postfix(bool focus)
        {
            try
            {
                LastWindowFocused = focus;

                if (_logCount >= MaxLogs)
                {
                    return;
                }

                _logCount++;

                bool inBattle = BattleManagerBase.GetIns() != null;
                string phase = DescribePhase();
                if (focus)
                {
                    Plugin.Logger.LogInfo(
                        "[Focus] 游戏窗口重新获得焦点：对局恢复，可以继续操作。" +
                        (inBattle ? $"（phase={phase}）" : string.Empty));
                }
                else
                {
                    Plugin.Logger.LogWarning(
                        "[Focus] 游戏窗口失去焦点：对局已暂停，牌桌不再接受任何操作，" +
                        "但**回合计时器与网络连接仍在继续**（切出去就可能被判回合超时）。" +
                        "点回游戏窗口即可恢复。" +
                        (inBattle ? $"（phase={phase}）" : string.Empty));
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug($"[Focus] focus log failed: {exception.Message}");
            }
        }

        private static string DescribePhase()
        {
            try
            {
                BattleManagerBase battleMgr = BattleManagerBase.GetIns();
                return battleMgr == null ? "<none>" : battleMgr.GetCurrentPhase()?.ToString() ?? "<null>";
            }
            catch (Exception)
            {
                return "<unknown>";
            }
        }
    }
}
