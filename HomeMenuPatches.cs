using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Shadowbus
{
    /// <summary>
    /// 隐藏主界面上用不到的入口。只关掉控件自身的 GameObject，不动任何逻辑，
    /// 所以调整时把对应的一行删掉即可。
    ///
    /// 首页 MyPageItemHome：礼物 / 任务 / 公会 / 通行证
    ///                     活动框（MyPageBattleCampaign）
    ///                     主横幅（_bannerObject / MyPageBanner，含左右切换按钮与底部小圆点）
    ///                     副横幅（_subBannerObject / MyPageSubBanner）
    /// 卡组页 MyPageItemCard：比赛精选牌组（_deckIntroductionButtons，与 卡牌、组编牌组 同一排）
    /// 商店 MyPageItemShop：购买卡套、购买角色皮肤、购买道具、交换临时卡牌、购买预组卡牌
    ///                      （连同各自的介绍条与维护提示牌）
    /// 单人页 MyPageItemSoroPlay：解谜
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
            HideAll(Find<Component[]>(__instance, "_deckIntroductionButtons"));
        }

        [HarmonyPatch(typeof(MyPageItemShop), nameof(MyPageItemShop.Show))]
        [HarmonyPostfix]
        public static void MyPageItemShop_Show_Postfix(MyPageItemShop __instance)
        {
            // 按钮
            Hide(Find<Component>(__instance, "_buyCardSleeveButton"));    // 购买卡套
            Hide(Find<Component>(__instance, "_buyLeaderSkinButton"));    // 购买角色皮肤
            Hide(Find<Component>(__instance, "_buyItemButton"));          // 购买道具
            Hide(Find<Component>(__instance, "_exchangeSpotCardButton")); // 交换临时卡牌
            Hide(Find<Component>(__instance, "_buyDeckButton"));          // 购买预组卡牌

            // 各自上方的介绍条，留着会变成悬空的一块
            Hide(Find<Component>(__instance, "_sleeveAppealItem"));
            Hide(Find<Component>(__instance, "_skinAppealItem"));
            Hide(Find<Component>(__instance, "_deckAppealItem"));

            // 维护提示牌
            Hide(Find<Component>(__instance, "_buyCardSleeveMaintenancePlate"));
            Hide(Find<Component>(__instance, "_buyLeaderSkinMaintenancePlate"));
            Hide(Find<Component>(__instance, "_buyItemMaintenancePlate"));
            Hide(Find<Component>(__instance, "_buyBuildDeckMaintenancePlate"));
            Hide(Find<Component>(__instance, "_exchangeSpotCardMaintenancePlate"));
        }

        [HarmonyPatch(typeof(MyPageItemSoroPlay), nameof(MyPageItemSoroPlay.Show))]
        [HarmonyPostfix]
        public static void MyPageItemSoroPlay_Show_Postfix(MyPageItemSoroPlay __instance)
        {
            Hide(Find<Component>(__instance, "_practiceBattlePazzle"));   // 解谜
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

        private static void HideAll(Component[] controls)
        {
            if (controls == null)
            {
                return;
            }

            foreach (Component control in controls)
            {
                Hide(control);
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
