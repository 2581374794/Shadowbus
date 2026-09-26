using System;
using System.Collections.Generic;
using System.IO;
using Cute;
using HarmonyLib;
using Newtonsoft.Json;
using Wizard;

namespace Shadowbus
{
    /// <summary>
    /// 简体 / 繁体两套文本表，各自独立存放、按当前文本语言取用。
    ///
    /// 游戏里所有文本表（卡名 / 卡牌说明 / 卡面记述 / 能力关键词 / 表情台词 / 系统 UI …，
    /// 一共 50 张）都走同一个入口：
    /// <c>Master.LoadLocalizeJsonAndParseWithRegion(dic, region, fileName, isTrimKey)</c>
    /// —— 把某个语言区段的 JSON 灌进一张字典。区段名就是当前文本语言
    /// （<c>Data.SystemText.RegionCode = CustomPreference.GetTextLanguage()</c>）。
    ///
    /// 所以这里不碰任何素材包，只在解析完之后把对应语言的那一份**盖上去**：
    ///
    /// <code>
    /// &lt;资源根&gt;\text\chs\cardnametext.json      ← 国服（网易）译文
    /// &lt;资源根&gt;\text\cht\cardnametext.json      ← 国际服原有繁体
    /// </code>
    ///
    /// 表名取自 <c>fileName</c> 的最后一段（<c>Master/text/cardnametext</c> → <c>cardnametext</c>），
    /// 目录名是语言区段名小写。没有对应目录的语言（Jpn / Eng / …）完全不覆盖。
    /// 键按游戏自己的规则 <c>Trim</c>（<c>isTrimKey</c> 为真时），否则查不到。
    ///
    /// 数据由 <c>_tools/build_text_data.py</c> 生成（简体取自国服客户端，繁体取自游戏本体）；
    /// 想让某张表回到原样，删掉对应的 json 即可。
    /// </summary>
    internal static class CnTextOverrides
    {
        private static readonly object CacheLock = new object();

        /// <summary>"语言目录/表名" → 数据；查不到的缓存成 null，免得每次加载都去碰磁盘。</summary>
        private static readonly Dictionary<string, Dictionary<string, string>> Cache =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>已经汇报过的表，避免刷日志。</summary>
        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool _warnedAboutFolder;

        private static bool _warned;

        [HarmonyPatch(typeof(Master), nameof(Master.LoadLocalizeJsonAndParseWithRegion))]
        [HarmonyPostfix]
        private static void Master_LoadLocalizeJsonAndParseWithRegion_Postfix(
            IDictionary<string, string> dic, string region, string fileName, bool isTrimKey)
        {
            try
            {
                if (dic == null || string.IsNullOrEmpty(fileName))
                {
                    return;
                }

                // 只覆盖"当前文本语言"和"游戏正在解析的语言区段"**一致**的那一套：
                //   简体语言 + Chs 区段 → 用 text/chs（国服）
                //   繁体语言 + Cht 区段 → 用 text/cht（本来就是这份内容，等于不动）
                //   两者不一致（切了语言但游戏还在解析另一个区段）→ 一个字都不碰，
                //   保持切换前的样子。繁体必须原样。
                string folder = LanguageFolder(region);
                if (folder == null || folder != CurrentLanguageFolder())
                {
                    return;
                }

                string table = TableName(fileName);
                if (string.IsNullOrEmpty(table))
                {
                    return;
                }

                Dictionary<string, string> lines = LoadTable(folder, table);
                if (lines == null || lines.Count == 0)
                {
                    return;
                }

                int applied = 0;
                foreach (KeyValuePair<string, string> pair in lines)
                {
                    string key = isTrimKey ? pair.Key.Trim() : pair.Key;
                    if (string.IsNullOrEmpty(key))
                    {
                        continue;
                    }

                    // 用索引器而不是 Add：这是覆盖，不是追加，重复键不能抛。
                    dic[key] = pair.Value;
                    applied++;
                }

                ReportApplied(folder, table, applied);
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[TextOverride] Could not apply '{fileName}': {exception.Message}");
            }
        }

        /// <summary>
        /// 当前文本语言对应的目录名（<c>Cht</c> → <c>cht</c>、<c>Chs</c> → <c>chs</c>；
        /// 读不出来或不是语言码时返回 null，那就一律不覆盖）。
        /// </summary>
        private static string CurrentLanguageFolder()
        {
            try
            {
                return LanguageFolder(CustomPreference.GetTextLanguage());
            }
            catch (Exception exception)
            {
                WarnOnce($"[TextOverride] Could not read the text language: {exception.Message}");
                return null;
            }
        }

        /// <summary>
        /// 语言区段名 → 目录名。只认纯小写字母的区段（<c>Chs</c> → <c>chs</c>），
        /// 别的（<c>Jpn</c> / <c>Eng</c> / <c>Kor</c> …）也能对上目录，只是我们没放那份数据。
        /// </summary>
        private static string LanguageFolder(string region)
        {
            string code = (region ?? string.Empty).Trim().ToLowerInvariant();
            if (code.Length == 0 || code.Length > 4)
            {
                return null;
            }

            foreach (char c in code)
            {
                if (c < 'a' || c > 'z')
                {
                    return null;
                }
            }

            return code;
        }

        /// <summary><c>Master/text/cardnametext</c> → <c>cardnametext</c>。</summary>
        private static string TableName(string fileName)
        {
            string normalized = fileName.Replace('\\', '/');
            int slash = normalized.LastIndexOf('/');
            return slash >= 0 ? normalized.Substring(slash + 1) : normalized;
        }

        private static Dictionary<string, string> LoadTable(string folder, string table)
        {
            string key = folder + "/" + table;
            lock (CacheLock)
            {
                if (Cache.TryGetValue(key, out Dictionary<string, string> cached))
                {
                    return cached;
                }

                Dictionary<string, string> loaded = ReadTable(folder, table);
                // 资源根还没解析出来时路径是错的，别把"没有"记死，等它接上再读一次。
                if (loaded != null || !string.IsNullOrEmpty(ResourceRootPatches.ResourceRoot))
                {
                    Cache[key] = loaded;
                }

                return loaded;
            }
        }

        private static Dictionary<string, string> ReadTable(string folder, string table)
        {
            string path = Path.Combine(Path.Combine(PathHelper.LanguageTextPath, folder), table + ".json");
            if (!File.Exists(path))
            {
                if (!_warnedAboutFolder && !Directory.Exists(PathHelper.LanguageTextPath))
                {
                    _warnedAboutFolder = true;
                    Plugin.Logger.LogInfo(
                        $"[TextOverride] No ported text at '{PathHelper.LanguageTextPath}'; " +
                        "the game's own text is used as-is.");
                }

                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[TextOverride] Could not read '{path}': {exception.Message}");
                return null;
            }
        }

        private static void ReportApplied(string folder, string table, int applied)
        {
            string key = folder + "/" + table;
            lock (CacheLock)
            {
                if (!Reported.Add(key))
                {
                    return;
                }
            }

            Plugin.Logger.LogInfo($"[TextOverride] Replaced {applied} line(s) in '{folder}/{table}'.");
        }

        private static void WarnOnce(string message)
        {
            lock (CacheLock)
            {
                if (!_warned)
                {
                    _warned = true;
                }
                else
                {
                    return;
                }
            }

            Plugin.Logger.LogWarning(message);
        }
    }
}
