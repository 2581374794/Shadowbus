using System;
using HarmonyLib;
using Wizard;

namespace Shadowbus
{
    /// <summary>
    /// 失焦不再暂停对局。
    ///
    /// 游戏原本的 <c>UnityEventAgent.OnApplicationFocus(false)</c> 会调 <c>m_battleMgr.Pause()</c>：
    /// **只停本地的输入与表现，Socket.IO 心跳、服务器转发、回合计时器照跑**。于是玩家一切出窗口，
    /// 牌桌就彻底不接受操作（观感就是"卡死"），而服务器那边只看到"这个玩家一直不操作"，
    /// 轻则被判回合超时，重则整局对不上 —— 之前几份玩家反馈的"联机卡死"实测都是这个。
    ///
    /// 这里在失焦时**不改状态机**（只把焦点标记写回去），对局照常继续；重新获得焦点时仍然交给
    /// 原方法（它本来就会 <c>Pause(); Resume();</c> 刷新一次）。
    /// </summary>
    public static class FocusPauseGuard
    {
        private static readonly System.Reflection.PropertyInfo HasFocusProperty =
            AccessTools.Property(typeof(UnityEventAgent), "HasFocus");

        private static readonly System.Reflection.FieldInfo HasFocusField =
            AccessTools.Field(typeof(UnityEventAgent), "<HasFocus>k__BackingField");

        /// <summary>最近一次失焦有没有被我们跳过暂停（给诊断日志用）。</summary>
        public static bool SkippedLastPause { get; private set; }

        private static int _logCount;
        private const int MaxLogs = 5;

        [HarmonyPatch(typeof(UnityEventAgent), "OnApplicationFocus")]
        [HarmonyPrefix]
        public static bool OnApplicationFocus_Prefix(UnityEventAgent __instance, bool focus)
        {
            // 获得焦点：照原样（Pause + Resume 刷新一次）。
            if (focus)
            {
                return true;
            }

            try
            {
                // 标记还是要写：别的代码会读 HasFocus（它是只读属性，只能反射 setter）。
                if (__instance != null)
                {
                    if (HasFocusProperty?.GetSetMethod(true) != null)
                    {
                        HasFocusProperty.GetSetMethod(true).Invoke(__instance, new object[] { false });
                    }
                    else
                    {
                        HasFocusField?.SetValue(__instance, false);
                    }
                }

                SkippedLastPause = true;
                if (_logCount < MaxLogs)
                {
                    _logCount++;
                    Plugin.Logger.LogInfo(
                        "[Focus] 窗口失焦：已跳过原有的暂停（对局继续，回合计时器本来就不会停）——" +
                        "避免切出去之后牌桌不再接受操作、被判回合超时。");
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug($"[Focus] Could not skip the focus pause: {exception.Message}");
                return true;
            }

            return false;
        }
    }
}
