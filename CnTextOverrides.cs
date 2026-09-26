using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Newtonsoft.Json;
using Wizard;

namespace Shadowbus
{
    /// <summary>
    /// 国服（网易）简体中文文本覆盖。
    ///
    /// 游戏里所有文本表（卡名 / 卡牌说明 / 卡面记述 / 能力关键词 / 系统 UI …，一共 50 张）
    /// 都走同一个入口：<c>Master.LoadLocalizeJsonAndParseWithRegion(dic, region, fileName, isTrimKey)</c>
    /// —— 把某个语言区段的 JSON 灌进一张字典。区段名就是当前文本语言
    /// （<c>Data.SystemText.RegionCode = CustomPreference.GetTextLanguage()</c>）。
    ///
    /// 所以这里不碰任何素材包，只在解析完之后把国服那一份**盖上去**：
    /// <c>&lt;Mods&gt;/Text/&lt;语言码小写&gt;/&lt;表名&gt;.json</c>，表名取自
    /// <c>fileName</c> 的最后一段（<c>Master/text/cardnametext</c> → <c>cardnametext</c>）。
    /// 键按游戏自己的规则 <c>Trim</c>（<c>isTrimKey</c> 为真时），否则查不到。
    ///
    /// 数据由 <c>_tools/build_text_data.py</c> 从国服客户端导出；想让某个表回到原样，
    /// 删掉对应的 json 即可。
    /// </summary>
    internal static class CnTextOverrides
    {
        /// <summary>文本数据目录名（<c>&lt;Mods&gt;/Text</c>）。</summary>
        private const string TextFolder = "Text";

        /// <summary>简体中文的语言码（也是 <c>RegionCode</c> 的取值）。</summary>
        private const string SimplifiedRegion = "Chs";

        private static readonly object CacheLock = new object();

        /// <summary>表名 → 国服数据；缺失的表缓存成 null，免得每次加载都去碰磁盘。</summary>
        private static readonly Dictionary<string, Dictionary<string, string>> Cache =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>已经汇报过的表，避免刷日志。</summary>
        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool _warnedAboutFolder;

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

                // 只在简体中文区段上盖国服的译文；别的语言保持游戏原样。
                if (!string.Equals(region, SimplifiedRegion, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string table = TableName(fileName);
                if (string.IsNullOrEmpty(table))
                {
                    return;
                }

                Dictionary<string, string> lines = LoadTable(table);
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

                ReportApplied(table, applied);
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[TextOverride] Could not apply '{fileName}': {exception.Message}");
            }
        }

        /// <summary><c>Master/text/cardnametext</c> → <c>cardnametext</c>。</summary>
        private static string TableName(string fileName)
        {
            string normalized = fileName.Replace('\\', '/');
            int slash = normalized.LastIndexOf('/');
            return slash >= 0 ? normalized.Substring(slash + 1) : normalized;
        }

        private static Dictionary<string, string> LoadTable(string table)
        {
            lock (CacheLock)
            {
                if (Cache.TryGetValue(table, out Dictionary<string, string> cached))
                {
                    return cached;
                }

                Dictionary<string, string> loaded = ReadTable(table);
                Cache[table] = loaded;
                return loaded;
            }
        }

        private static Dictionary<string, string> ReadTable(string table)
        {
            string path = Path.Combine(
                Path.Combine(PathHelper.ModPath, TextFolder),
                SimplifiedRegion.ToLowerInvariant(),
                table + ".json");
            if (!File.Exists(path))
            {
                if (!_warnedAboutFolder && !Directory.Exists(Path.GetDirectoryName(path)))
                {
                    _warnedAboutFolder = true;
                    Plugin.Logger.LogInfo(
                        $"[TextOverride] No CN text data at '{Path.GetDirectoryName(path)}'; " +
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

        private static void ReportApplied(string table, int applied)
        {
            lock (CacheLock)
            {
                if (!Reported.Add(table))
                {
                    return;
                }
            }

            Plugin.Logger.LogInfo($"[TextOverride] Replaced {applied} CN line(s) in '{table}'.");
        }
    }
}
