using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Shadowbus
{
    /// <summary>
    /// 隐藏（或变暗）主界面上用不到的入口。隐藏只关掉控件自身的 GameObject、不动任何逻辑；
    /// 变暗走游戏自己的 <see cref="UIManager.SetObjectToGrey"/>，控件留在原位但点不动。
    /// 调整时把对应的一行删掉即可。
    ///
    /// 首页 MyPageItemHome：礼物 / 任务 / 公会 / 通行证
    ///                     活动框（MyPageBattleCampaign）
    ///                     主横幅（_bannerObject / MyPageBanner，含左右切换按钮与底部小圆点）
    ///                     副横幅（_subBannerObject / MyPageSubBanner）
    /// 卡组页 MyPageItemCard：比赛精选牌组（_deckIntroductionButtons，与 卡牌、组编牌组 同一排）
    ///                      —— **变暗不可点**，保留占位
    /// 商店 MyPageItemShop：购买卡套、购买角色皮肤、购买道具、交换临时卡牌、购买预组卡牌
    ///                      （连同各自的介绍条与维护提示牌）—— **变暗不可点**，保留占位
    /// 单人页 MyPageItemSoroPlay：解谜入口（曾隐藏，2.5.6 已恢复，见文件末尾注释）
    /// 解谜挑战 PracticePuzzleUI：右上角「解谜任务」（_missionButton）
    ///
    /// 刻意不动：商店「礼包 / 特供」页入口 _supplyButton 及其介绍条 _supplyAppealItem，
    /// 动了整页会连入口一起消失。
    ///
    /// 这里也刻意不用 Harmony 的 ___字段 注入：它按「去掉三个下划线后的名字」查找字段，
    /// 而这些字段本身就叫 _xxx，写成 ___x 会去找 x 而失败，写成 ____x 又极易看错。
    /// </summary>
    public static class HomeMenuPatches
    {
        [HarmonyPatch(typeof(MyPageItemHome), nameof(MyPageItemHome.Show))]
        [HarmonyPostfix]
        public static void MyPageItemHome_Show_Postfix(MyPageItemHome __instance)
        {
            Hide(Find<Component>(__instance, "_giftButton"));
            Hide(Find<Component>(__instance, "_missionButton"));
            Hide(Find<Component>(__instance, "_guildButton"));
            Hide(Find<Component>(__instance, "_battlePassButton"));

            // 首页左上角那个带倒计时的活动显示框由 MyPageBattleCampaign 驱动，根节点是 _boxRoot。
            // 找不到该组件时静默跳过，不影响其它入口的隐藏。
            MyPageBattleCampaign campaign = __instance.GetComponentInChildren<MyPageBattleCampaign>(true);
            if (campaign != null)
            {
                Hide(Find<GameObject>(campaign, "_boxRoot"));
                Hide(Find<Component>(campaign, "_timeLabel"));
            }

            // 主横幅：整个容器先关掉，再把容器内部几个独立节点也点名关掉，
            // 这样即使以后 prefab 层级改了、容器不再包含某个按钮，也不会漏。
            Hide(Find<GameObject>(__instance, "_bannerObject"));
            Hide(Find<GameObject>(__instance, "_subBannerObject"));

            MyPageBanner banner = Find<MyPageBanner>(__instance, "_banner");
            if (banner != null)
            {
                Hide(Find<GameObject>(banner, "_bgObject"));           // 横幅底图
                Hide(Find<GameObject>(banner, "_buttonLeftObject"));   // 左翻页
                Hide(Find<GameObject>(banner, "_buttonRightObject"));  // 右翻页
                Hide(Find<GameObject>(banner, "_buttonBase"));         // 翻页按钮底板
                Hide(Find<GameObject>(banner, "_pagerBaseObject"));    // 底部小圆点
            }

            MyPageSubBanner subBanner = Find<MyPageSubBanner>(__instance, "_subBanner");
            if (subBanner != null)
            {
                Hide(Find<GameObject>(subBanner, "_bannerRoot"));
            }
        }

        [HarmonyPatch(typeof(MyPageItemCard), nameof(MyPageItemCard.Show))]
        [HarmonyPostfix]
        public static void MyPageItemCard_Show_Postfix(MyPageItemCard __instance)
        {
            // 比赛精选牌组这一排：留着占位，但变暗、点不动（原来是整排隐藏）。
            ApplyDim(__instance);
            KeepDimmed(__instance, () => ApplyDim(__instance));
        }

        [HarmonyPatch(typeof(MyPageItemShop), nameof(MyPageItemShop.Show))]
        [HarmonyPostfix]
        public static void MyPageItemShop_Show_Postfix(MyPageItemShop __instance)
        {
            ApplyDim(__instance);
            KeepDimmed(__instance, () => ApplyDim(__instance));
        }

        /// <summary>
        /// 把一页上要变暗的控件全部压成禁用态。按钮自己的 BoxCollider 由
        /// <see cref="UIManager.SetObjectToGrey"/> 关掉，所以「变暗」和「点不动」是一件事。
        /// </summary>
        private static void ApplyDim(Component page)
        {
            if (page is MyPageItemCard)
            {
                DimAll(Find<Component[]>(page, "_deckIntroductionButtons"));
                return;
            }

            if (!(page is MyPageItemShop))
            {
                return;
            }

            // 按钮
            Dim(Find<Component>(page, "_buyCardSleeveButton"));    // 购买卡套
            Dim(Find<Component>(page, "_buyLeaderSkinButton"));    // 购买角色皮肤
            Dim(Find<Component>(page, "_buyItemButton"));          // 购买道具
            Dim(Find<Component>(page, "_exchangeSpotCardButton")); // 交换临时卡牌
            Dim(Find<Component>(page, "_buyDeckButton"));          // 购买预组卡牌

            // 各自上方的介绍条
            Dim(Find<Component>(page, "_sleeveAppealItem"));
            Dim(Find<Component>(page, "_skinAppealItem"));
            Dim(Find<Component>(page, "_deckAppealItem"));

            // 维护提示牌（官方维护时才会出现）
            Dim(Find<Component>(page, "_buyCardSleeveMaintenancePlate"));
            Dim(Find<Component>(page, "_buyLeaderSkinMaintenancePlate"));
            Dim(Find<Component>(page, "_buyItemMaintenancePlate"));
            Dim(Find<Component>(page, "_buyBuildDeckMaintenancePlate"));
            Dim(Find<Component>(page, "_exchangeSpotCardMaintenancePlate"));
        }

        /// <summary>
        /// 游戏自己会把已经变暗的按钮重新点亮：切页走 <c>ShowSupplyMenu</c> / <c>ShowCardMenu</c>
        /// 时，<c>MyPageItem.SetMaintenanceVisible</c> 会执行 <c>SetObjectToGrey(button, false)</c> 并把
        /// <c>button.isEnabled</c> 设回 true；介绍条也会被自己的动画重新上色；「其他」页的入场动画
        /// 还会把 <c>defaultColor</c> 刷回白色。所以一次性变暗一定会被顶掉，这里在页面还活着的
        /// 时候每 0.1 秒压一次；页面被销毁或失活，协程自己结束。
        /// </summary>
        internal static void KeepDimmed(Component page, Action apply)
        {
            if (page == null || apply == null || Plugin.Instance == null)
            {
                return;
            }

            Plugin.Instance.StartCoroutine(KeepDimmedCoroutine(page, apply));
        }

        private static IEnumerator KeepDimmedCoroutine(Component page, Action apply)
        {
            WaitForSeconds wait = new WaitForSeconds(0.1f);

            while (true)
            {
                yield return wait;

                if (page == null || !page.gameObject.activeInHierarchy)
                {
                    yield break;
                }

                apply();
            }
        }

        // 解谜入口（_practiceBattlePazzle）以前在这里被隐藏，因为那时解谜的离线数据还是空的。
        // 现在解谜按本地 master 复原（见 PuzzleOfflineData），按钮必须留着，所以这段补丁已删除。

        /// <summary>
        /// 解谜挑战（<c>PracticePuzzleUI</c>）右上角的「解谜任务」按钮。
        /// 它触发 <c>PracticePuzzleMissionListTask</c> 再弹出 <c>PracticePuzzleMissionDialog</c>；
        /// 按要求把这个入口藏掉。只关按钮自己的 GameObject，<c>OnClickMissionButton</c> 的调用链、
        /// 以及本地的 <c>puzzle/puzzle_mission.json</c> 都原样留着，以后想恢复删掉本方法即可。
        /// </summary>
        [HarmonyPatch(typeof(Wizard.PracticePuzzleUI), nameof(Wizard.PracticePuzzleUI.onFirstStart))]
        [HarmonyPostfix]
        public static void PracticePuzzleUI_onFirstStart_Postfix(Wizard.PracticePuzzleUI __instance)
        {
            Hide(Find<Component>(__instance, "_missionButton"));
        }

        private static T Find<T>(object instance, string fieldName) where T : class
        {
            if (instance == null)
            {
                return null;
            }

            FieldInfo field = AccessTools.Field(instance.GetType(), fieldName);
            return field != null ? field.GetValue(instance) as T : null;
        }

        /// <summary>
        /// 变暗 + 点不动，而不是隐藏：<see cref="UIManager.SetObjectToGrey"/> 会把它自己和
        /// 子节点上的 UIWidget 全部涂成禁用色、并关掉根节点的 BoxCollider（NGUI 的点击靠它），
        /// 所以按钮还在原位、只是灰掉且点不下去。找不到控件时静默跳过。
        /// </summary>
        private static void Dim(Component control)
        {
            if (control == null)
            {
                return;
            }

            UIButton button = control as UIButton;
            if (button == null)
            {
                button = control.GetComponent<UIButton>();
            }

            if (button != null)
            {
                button.isEnabled = false;
            }

            UIManager.SetObjectToGrey(control.gameObject, true);
        }

        private static void DimAll(Component[] controls)
        {
            if (controls == null)
            {
                return;
            }

            foreach (Component control in controls)
            {
                Dim(control);
            }
        }

        private static void Hide(Component control)
        {
            if (control != null && control.gameObject.activeSelf)
            {
                control.gameObject.SetActive(false);
            }
        }

        // 横幅那几个字段直接就是 GameObject（不是 Component），所以单开一个重载。
        private static void Hide(GameObject control)
        {
            if (control != null && control.activeSelf)
            {
                control.SetActive(false);
            }
        }
    }
}
