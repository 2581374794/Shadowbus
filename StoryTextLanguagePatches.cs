using System;
using System.IO;
using Cute;
using HarmonyLib;

namespace Shadowbus
{
    /// <summary>
    /// 运行时简繁切换：剧情正文 / 角色名 / 章节概要 / 影片字幕都是
    /// <c>storylang_*.unity3d</c> 里的 TextAsset，游戏取文件的路径只有一条：
    /// <c>Cute.AssetHandle.BuildLocalCachePath()</c>（<c>AssetHandle/&lt;_Load&gt;</c> 拿它去
    /// <c>AssetBundle.LoadFromFile</c>，<c>AssetManager.LoadObject</c> 拿它做 File.Exists）。
    ///
    /// 剧情文本因此**不再放在 `a/`**，而是按语言分目录放在资源目录里：
    ///
    /// <code>
    /// &lt;资源根&gt;\story_text\chs\storylang_scenario_text_1_10_1.unity3d   （简体）
    /// &lt;资源根&gt;\story_text\cht\storylang_scenario_text_1_10_1.unity3d   （繁体）
    /// </code>
    ///
    /// 文件名与原来的包名完全一致，插件按当前文本语言
    /// （<c>Cute.CustomPreference.GetTextLanguage()</c>，设置里切的那一项）改路径：
    /// 先找该语言的目录，没有这份包再退回 <c>chs</c>（简体），都不在就交回游戏原路径。
    /// 语言目录名就是语言码小写（<c>Chs</c>/<c>Cht</c>/<c>Eng</c>/…），
    /// 所以以后想加别的语言，照着放一个同名目录即可。
    ///
    /// 文本用 <c>_tools/collect_story_text_bundles.py</c> 收集（`--lang cht` 从繁体备份、
    /// `--lang chs --source deployed --move-source` 把简体从 `a/` 挪进来）。
    /// </summary>
    internal static class StoryTextLanguagePatches
    {
        /// <summary>剧情文本包的文件名前缀（与原 <c>a/</c> 下的名字一致）。</summary>
        private const string BundlePrefix = "storylang_";

        private const string BundleSuffix = ".unity3d";

        /// <summary>所有语言都会退回的兜底目录：简体。</summary>
        private const string SimplifiedFolder = "chs";

        /// <summary>繁体目录名。</summary>
        private const string TraditionalFolder = "cht";

        private static readonly object StateLock = new object();

        private static string lastLanguage;

        private static string lastFolderSummary;

        private static bool lastOverrideServed;

        private static string lastWarning;

        [HarmonyPatch(typeof(AssetHandle), nameof(AssetHandle.BuildLocalCachePath))]
        [HarmonyPostfix]
        private static void AssetHandle_BuildLocalCachePath_Postfix(ref string __result)
        {
            string overridden = ResolveStoryBundle(__result);
            if (overridden != null)
            {
                __result = overridden;
            }
        }

        /// <summary>
        /// 这份资源是剧情文本包、而且 <c>story_text/</c> 下有对应语言的包时，返回它的完整路径；
        /// 其余情况一律返回 null（保持游戏原来的路径）。
        /// </summary>
        private static string ResolveStoryBundle(string gamePath)
        {
            if (string.IsNullOrEmpty(gamePath))
            {
                return null;
            }

            string fileName = Path.GetFileName(gamePath);
            if (!fileName.StartsWith(BundlePrefix, StringComparison.OrdinalIgnoreCase) ||
                !fileName.EndsWith(BundleSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string language = CurrentLanguage();
            string root = ResourceRootPatches.ResourceRoot;
            string storyRoot = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "story_text");
            string[] folders = CandidateFolders(language);
            ReportLanguageOnce(language, storyRoot, folders);

            if (storyRoot == null)
            {
                return null;
            }

            foreach (string folder in folders)
            {
                string candidate = Path.Combine(storyRoot, folder, fileName);
                if (File.Exists(candidate))
                {
                    ReportOverrideServed(candidate);
                    return candidate;
                }
            }

            return null;
        }

        private static string CurrentLanguage()
        {
            try
            {
                return CustomPreference.GetTextLanguage();
            }
            catch (Exception exception)
            {
                LogWarningOnce($"[StoryText] Could not read the text language: {exception.Message}");
                return null;
            }
        }

        /// <summary>当前文字语言是不是繁体中文（BossRush 那类本地文本也按它选简繁）。</summary>
        internal static bool IsTraditionalChinese()
        {
            return LanguageFolder(CurrentLanguage()) == TraditionalFolder;
        }

        /// <summary>
        /// 插件自己加的 UI 文本按当前文字语言选一版：繁体设置取繁体、简体设置取简体，
        /// 其余语言（<c>Eng</c>/<c>Kor</c>/…）取英文那一版；语言读不出来（或认不出来）
        /// 按简体处理 —— 和插件里其它只写了中文的标签保持一致。
        /// </summary>
        internal static string ResolveUiText(string simplified, string traditional, string english)
        {
            string folder = LanguageFolder(CurrentLanguage());
            if (folder == TraditionalFolder)
            {
                return string.IsNullOrEmpty(traditional) ? simplified : traditional;
            }

            if (folder == null || folder == SimplifiedFolder)
            {
                return string.IsNullOrEmpty(simplified) ? traditional : simplified;
            }

            return string.IsNullOrEmpty(english) ? simplified : english;
        }

        /// <summary>查找顺序：当前语言的目录，然后简体目录。</summary>
        private static string[] CandidateFolders(string language)
        {
            string primary = LanguageFolder(language);
            if (primary == null || primary == SimplifiedFolder)
            {
                return new[] { SimplifiedFolder };
            }

            return new[] { primary, SimplifiedFolder };
        }

        /// <summary>
        /// 语言码 → 目录名：<c>Cht</c>/<c>zh-TW</c> → <c>cht</c>，<c>Chs</c>/<c>zh-CN</c> → <c>chs</c>，
        /// 其余（<c>Eng</c>/<c>Kor</c>/…）就是语言码小写；认不出来返回 null。
        /// </summary>
        private static string LanguageFolder(string language)
        {
            string code = (language ?? string.Empty).Trim().ToLowerInvariant();
            switch (code)
            {
                case "cht":
                case "zh-tw":
                case "zh-hk":
                case "zh-hant":
                    return "cht";
                case "chs":
                case "zh-cn":
                case "zh-hans":
                    return SimplifiedFolder;
                case "":
                    return null;
                default:
                    // 目录名只允许纯小写字母，免得奇怪的语言码被当成路径用。
                    foreach (char c in code)
                    {
                        if (c < 'a' || c > 'z')
                        {
                            return null;
                        }
                    }

                    return code.Length <= 4 ? code : null;
            }
        }

        /// <summary>
        /// 语言变了就重新说明一次：现在是什么语言、按什么顺序去哪些目录找。
        /// </summary>
        private static void ReportLanguageOnce(string language, string storyRoot, string[] folders)
        {
            string summary = storyRoot == null
                ? null
                : storyRoot + " → " + string.Join(", ", folders);

            lock (StateLock)
            {
                if (language == lastLanguage && summary == lastFolderSummary)
                {
                    return;
                }

                lastLanguage = language;
                lastFolderSummary = summary;
                lastOverrideServed = false;
            }

            if (summary == null)
            {
                LogInfo(
                    $"[StoryText] Text language is '{language}'; no resource folder was found, " +
                    "so the game's own paths are kept.");
                return;
            }

            LogInfo(
                $"[StoryText] Text language is '{language}'; story text is looked up in " +
                $"{summary}.{DescribeFolders(storyRoot, folders)}");
        }

        private static string DescribeFolders(string storyRoot, string[] folders)
        {
            try
            {
                foreach (string folder in folders)
                {
                    string directory = Path.Combine(storyRoot, folder);
                    if (Directory.Exists(directory))
                    {
                        return $" '{folder}' holds " +
                               $"{Directory.GetFiles(directory, BundlePrefix + "*" + BundleSuffix).Length} " +
                               "bundle(s).";
                    }
                }

                return " None of those folders exists yet, so the bundles are used as-is.";
            }
            catch (Exception exception)
            {
                return $" (could not enumerate them: {exception.Message})";
            }
        }

        /// <summary>真的换掉一份包之后再说一句（语言切换后重新记一次），用来确认覆盖生效了。</summary>
        private static void ReportOverrideServed(string path)
        {
            lock (StateLock)
            {
                if (lastOverrideServed)
                {
                    return;
                }

                lastOverrideServed = true;
            }

            LogInfo(
                $"[StoryText] Served '{Path.GetFileName(path)}' from {Path.GetDirectoryName(path)} " +
                "(the other story bundles come from the same folder).");
        }

        private static void LogWarningOnce(string message)
        {
            lock (StateLock)
            {
                if (message == lastWarning)
                {
                    return;
                }

                lastWarning = message;
            }

            LogWarning(message);
        }

        private static void LogInfo(string message)
        {
            try
            {
                Plugin.Logger?.LogInfo(message);
            }
            catch (Exception)
            {
                // 日志不能影响补丁。
            }
        }

        private static void LogWarning(string message)
        {
            try
            {
                Plugin.Logger?.LogWarning(message);
            }
            catch (Exception)
            {
                // 日志不能影响补丁。
            }
        }
    }
}
