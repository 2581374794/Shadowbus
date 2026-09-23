using System;
using System.Collections.Generic;
using HarmonyLib;

namespace Shadowbus
{
    /// <summary>
    /// 语言切换界面只留中文：客户端的语言表 <c>Global.LanguagePropList</c> 一共 8 项
    /// （English / 한국어 / 繁體中文 / Français / Italiano / Deutsch / Español / 简体中文），
    /// 这里在 <c>SwitchLanguage.CreateSwitchLanguageDialog</c> 执行期间把那块静态数组
    /// **临时**换成只含 繁體中文 / 简体中文 的版本，方法一返回就还原。
    ///
    /// 为什么用「临时换」而不是启动时直接改掉：那个数组别处也在用（字体、语音语言表都按
    /// LangType 去里面找），改成两项会让别的语言查不到东西。只在这一个方法里换，
    /// 下游的 <c>_languageKeyList</c> / <c>_languageTextList</c> 自然就只有两项，
    /// 序号和回调都对得上，不用去改对话框。
    /// </summary>
    internal static class LanguageSelectionPatches
    {
        private static Global.LanguageProps[] savedList;

        private static bool loggedOnce;

        [HarmonyPatch(typeof(SwitchLanguage), "CreateSwitchLanguageDialog")]
        [HarmonyPrefix]
        private static void CreateSwitchLanguageDialog_Prefix()
        {
            try
            {
                Global.LanguageProps[] all = Global.LanguagePropList;
                if (all == null || all.Length <= 2)
                {
                    return;
                }

                var kept = new List<Global.LanguageProps>(2);
                foreach (Global.LanguageProps entry in all)
                {
                    string langType = entry.LangType ?? string.Empty;
                    if (langType.Equals("Cht", StringComparison.OrdinalIgnoreCase) ||
                        langType.Equals("Chs", StringComparison.OrdinalIgnoreCase))
                    {
                        kept.Add(entry);
                    }
                }

                if (kept.Count == 0 || kept.Count == all.Length)
                {
                    return;
                }

                savedList = all;
                Global.LanguagePropList = kept.ToArray();

                if (!loggedOnce)
                {
                    loggedOnce = true;
                    Plugin.Logger?.LogInfo(
                        $"[Language] Language selection limited to {Describe(kept)} " +
                        $"(dropped {all.Length - kept.Count} other option(s)).");
                }
            }
            catch (Exception exception)
            {
                // 探针式改动不能把界面弄崩：出错就原样显示全部语言。
                Plugin.Logger?.LogWarning($"[Language] Could not limit the language list: {exception.Message}");
            }
        }

        [HarmonyPatch(typeof(SwitchLanguage), "CreateSwitchLanguageDialog")]
        [HarmonyPostfix]
        private static void CreateSwitchLanguageDialog_Postfix()
        {
            if (savedList == null)
            {
                return;
            }

            Global.LanguagePropList = savedList;
            savedList = null;
        }

        private static string Describe(List<Global.LanguageProps> entries)
        {
            var parts = new List<string>(entries.Count);
            foreach (Global.LanguageProps entry in entries)
            {
                parts.Add((entry.DisplayName ?? "?") + "(" + (entry.LangType ?? "?") + ")");
            }

            return string.Join("/", parts);
        }
    }
}
