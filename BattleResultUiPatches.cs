using HarmonyLib;
using System;
using UnityEngine;

namespace Shadowbus
{
    /// <summary>
    /// 删掉战斗结算界面里的「任务」按钮。
    ///
    /// 结算界面是共用的：<c>BattleResultUIController</c> 的 <c>ButtonGrid</c>（一个 UIGrid）
    /// 里排着 MissionBtnObj / HomeBtnObj / RetryBtnObj / ReportBtnObj 四个按钮，对应
    /// 「任务 / 首页 / 再战 / 举报」。四个按钮的点击事件都是 prefab 里连好的，代码里从不隐藏
    /// <c>MissionBtnObj</c>，<c>ResultSetupEnd</c> 第 489 行还会把整个 ButtonGrid 打开 ——
    /// 所以「任务」在**所有模式**（剧情 / 练习 / 排位 / 双选 / 房间）结算后都会出现。
    ///
    /// 这里在 <c>ResultSetupEnd</c> 之后把这个按钮整个删掉（Destroy，不是 SetActive(false)），
    /// 并让 ButtonGrid 重新排版，剩下的按钮自动补位。
    /// </summary>
    internal static class BattleResultUiPatches
    {
        private static bool _removedLogged;

        [HarmonyPatch(typeof(BattleResultUIController), "ResultSetupEnd")]
        [HarmonyPostfix]
        private static void ResultSetupEnd_Postfix(BattleResultUIController __instance)
        {
            try
            {
                NguiObjs mission = __instance != null ? __instance.MissionBtnObj : null;
                if (mission != null)
                {
                    // 先清引用再销毁：按钮格重排/其它方法以后即使再拿到这个字段也不会碰到已销毁对象。
                    __instance.MissionBtnObj = null;
                    mission.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(mission.gameObject);

                    if (!_removedLogged)
                    {
                        _removedLogged = true;
                        Plugin.Logger.LogInfo(
                            "[BattleUI] Removed the post-battle '任务' button from the result button grid.");
                    }
                }

                if (__instance != null && __instance.ButtonGrid != null)
                {
                    __instance.ButtonGrid.Reposition();
                    __instance.ButtonGrid.repositionNow = true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[BattleUI] Could not remove the post-battle '任务' button: {ex}");
            }
        }
    }
}
