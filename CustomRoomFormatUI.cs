using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Cute;
using UnityEngine;
using Wizard;
using Wizard.RoomMatch;

namespace Shadowbus
{
    /// <summary>
    /// 仅承载房间创建阶段的自定义赛制选择。原版 BattleParameter 仍使用
    /// Format.Unlimited 作为底层承载，真正的限制由 CustomFormatDefinition 提供。
    /// </summary>
    internal static class CustomRoomFormatSelection
    {
        internal static string SelectedFormatId { get; private set; } = CustomFormats.UnlimitedId;
        internal static bool IsSelectionSessionActive { get; private set; }

        internal static void BeginSelectionSession()
        {
            if (!IsSelectionSessionActive)
            {
                SelectedFormatId = CustomFormats.UnlimitedId;
                CustomFormatContext.RoomFormatId = SelectedFormatId;
            }
            IsSelectionSessionActive = true;
        }

        internal static void SetSelected(string id)
        {
            CustomFormatDefinition definition = CustomFormats.Get(id);
            SelectedFormatId = definition.Id;
            CustomFormatContext.RoomFormatId = definition.Id;
        }

        internal static void EndSelectionSession()
        {
            IsSelectionSessionActive = false;
        }

        internal static void Reset()
        {
            SelectedFormatId = CustomFormats.UnlimitedId;
            CustomFormatContext.RoomFormatId = CustomFormats.UnlimitedId;
            IsSelectionSessionActive = false;
        }
    }

    [HarmonyPatch(typeof(RoomRuleSelectDialog), "Create")]
    internal static class RoomRuleSelectDialogCreatePatch
    {
        [HarmonyPrefix]
        private static void Prefix(bool isTwoPick)
        {
            if (!isTwoPick && !Server.OnlineRuntime.IsEnabled)
            {
                CustomRoomFormatSelection.BeginSelectionSession();
            }
        }
    }

    [HarmonyPatch(typeof(RoomRuleSelectDialog), "Initialize")]
    internal static class RoomRuleSelectDialogInitializePatch
    {
        [HarmonyPostfix]
        private static void Postfix(
            RoomRuleSelectDialog __instance,
            RoomRuleSetting setting,
            bool isTwoPick)
        {
            if (__instance == null || setting == null || isTwoPick)
            {
                return;
            }

            // Keep the native state machine on its Unlimited path. The custom
            // definition is applied by Shadowbus independently.
            setting.BattleParameterInstance.DeckFormat = Format.Unlimited;
            RefreshLabel(__instance);
        }

        internal static void RefreshLabel(RoomRuleSelectDialog dialog)
        {
            if (dialog?._formatLabel == null || dialog._is2pick)
            {
                return;
            }

            dialog._formatLabel.text = CustomFormats.Get(
                CustomRoomFormatSelection.SelectedFormatId).DisplayName;
        }
    }

    [HarmonyPatch(typeof(RoomRuleSelectDialog), "CheckDefaultSetting")]
    internal static class RoomRuleSelectDialogDefaultSettingPatch
    {
        [HarmonyPrefix]
        private static void Prefix(RoomRuleSelectDialog __instance)
        {
            if (__instance?._setting != null && !__instance._is2pick)
            {
                // Make the stock rule list expose BO1/BO3/BO5 for every
                // custom constructed format before it computes defaults.
                __instance._setting.BattleParameterInstance.DeckFormat = Format.Unlimited;
            }
        }
    }

    [HarmonyPatch(typeof(RoomRuleSelectDialog), "RefreshSetting")]
    internal static class RoomRuleSelectDialogRefreshPatch
    {
        [HarmonyPostfix]
        private static void Postfix(RoomRuleSelectDialog __instance)
        {
            if (__instance == null || __instance._is2pick)
            {
                return;
            }

            if (__instance._setting != null)
            {
                __instance._setting.BattleParameterInstance.DeckFormat = Format.Unlimited;
            }
            RoomRuleSelectDialogInitializePatch.RefreshLabel(__instance);
        }
    }

    [HarmonyPatch(typeof(RoomRuleSelectDialog), "OnClickFormatChangeButton")]
    internal static class RoomRuleSelectDialogFormatButtonPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(RoomRuleSelectDialog __instance)
        {
            if (__instance == null || __instance._is2pick)
            {
                return true;
            }

            CustomFormats.ReloadForUi("room creation");
            List<CustomFormatDefinition> definitions = CustomFormats.All.ToList();
            if (definitions.Count == 0)
            {
                return true;
            }

            int selectedIndex = definitions.FindIndex(definition =>
                string.Equals(
                    definition.Id,
                    CustomRoomFormatSelection.SelectedFormatId,
                    StringComparison.OrdinalIgnoreCase));
            if (selectedIndex < 0)
            {
                selectedIndex = 0;
            }

            DialogBase selectorDialog = null;
            Action<int> onSelected = index =>
            {
                if (index >= 0 && index < definitions.Count)
                {
                    selectedIndex = index;
                    selectorDialog?.SetTitleLabel(
                        "房间赛制：" + definitions[index].DisplayName);
                }
            };
            selectorDialog = DrumrollDialog.Create(
                definitions.Select(definition => definition.DisplayName).ToList(),
                selectedIndex,
                onSelected,
                null,
                null,
                string.Empty);
            selectorDialog.SetTitleLabel("房间赛制");
            selectorDialog.SetButtonLayout(DialogBase.ButtonLayout.DecisionBtn);
            selectorDialog.onPushButton1 = () =>
            {
                int index = selectedIndex;
                // DrumrollDialog invokes the callback with the current index;
                // retaining the value here also handles keyboard/controller input.
                CustomFormatDefinition selected = definitions[index];
                CustomRoomFormatSelection.SetSelected(selected.Id);
                if (__instance._setting != null)
                {
                    __instance._setting.BattleParameterInstance.DeckFormat = Format.Unlimited;
                }
                RoomRuleSelectDialogInitializePatch.RefreshLabel(__instance);
                selectorDialog.SetDisp(false);
                __instance._dialogSelf?.SetDisp(true);
            };
            selectorDialog.onCloseWithoutSelect = () =>
            {
                selectorDialog.SetDisp(false);
                __instance._dialogSelf?.SetDisp(true);
            };
            __instance._dialogSelf?.SetDisp(false);
            return false;
        }
    }

    [HarmonyPatch(typeof(MyPageItemBattle), "OnClickRoomCreateNormalRule")]
    internal static class MyPageItemBattleRoomFormatResetPatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            CustomRoomFormatSelection.Reset();
            CustomRoomFormatSelection.BeginSelectionSession();
        }
    }

    [HarmonyPatch(typeof(PlayerControllerForOwn), "SelectDeck")]
    internal static class PlayerControllerForOwnSelectDeckFormatPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(DeckData deck)
        {
            if (!P2PRuntime.IsActive || deck == null || deck.IsNoCard())
            {
                return true;
            }

            if (P2PRuntime.IsDeckAllowed(
                deck,
                out CustomFormatDefinition definition,
                out CustomFormatViolation violation))
            {
                return true;
            }

            Plugin.Logger.LogWarning(
                $"[CustomFormats] Blocked selecting deck {deck.GetDeckID()} for " +
                $"{definition?.Id ?? CustomFormats.UnlimitedId}: " +
                CustomFormatViolationText.Describe(violation, CardMaster.GetInstanceForBattle()));
            return false;
        }
    }

    [HarmonyPatch(typeof(RoomRuleSetting), "GetTopBarString")]
    internal static class RoomRuleSettingTopBarFormatPatch
    {
        [HarmonyPostfix]
        private static void Postfix(
            BattleParameter battleParameter,
            ref string __result)
        {
            if (!Server.OnlineRuntime.IsEnabled ||
                battleParameter == null ||
                battleParameter.BattleType == NetworkDefine.ServerBattleType.RoomTwoPick)
            {
                return;
            }

            CustomFormatDefinition definition = CustomFormats.Get(
                CustomFormatContext.RoomFormatId);
            if (definition.Id == CustomFormats.UnlimitedId ||
                string.IsNullOrEmpty(__result))
            {
                return;
            }

            __result = definition.DisplayName + " - " + __result;
        }
    }

    [HarmonyPatch(typeof(BaseRoomBattleEnterRoomTask), "Parse")]
    internal static class GuestRoomUnlimitedBasePatch
    {
        [HarmonyPostfix]
        private static void Postfix(BaseRoomBattleEnterRoomTask __instance)
        {
            BattleParameter parameter = __instance?.BattleParameterInstance;
            if (!Server.OnlineRuntime.IsEnabled || parameter == null || parameter.IsTwoPick)
            {
                return;
            }

            parameter.DeckFormat = Format.Unlimited;
            Plugin.Logger.LogInfo(
                "[CustomFormats] Guest native room base forced to Unlimited after enter-room parse.");
        }
    }
}
