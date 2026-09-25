using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Wizard;

namespace Shadowbus
{
    /// <summary>
    /// 把国服独占的主战者（地区独占联动皮肤）补进国际服的角色表。
    ///
    /// 数据在 <c>Mods/LeaderSkins/imported_leaders.json</c>：每个角色带国服
    /// <c>class_chara_master</c> 的原始 20 列 + 名字（简/繁/英）+ 可用职业列表。
    /// 国服里职业是 99（<c>ClanType.SHADOW</c>，国际服枚举里本来就有）的那几个是"中立"，
    /// 国际服的主战者列表是按 <c>class_id</c> 过滤的，所以中立皮肤按 1~8 各注入一行
    /// （skin_id 相同、class_id 不同，游戏里没有"主战者职业必须等于卡组职业"的校验）。
    ///
    /// 素材：缩略图/按钮图/卡组立绘/立绘/胜负/资料图走 <see cref="LeaderSkinAssetFallback"/>
    /// 的补图机制（<c>Mods/LeaderSkins/&lt;皮肤号&gt;</c>）；战斗 spine 是另外做好的
    /// 2020 版包（壳 = 国际服 skeleton 包，内容换成国服骨骼/图集/贴图）。
    /// </summary>
    internal static class ImportedLeaders
    {
        private const string SubDirectory = "LeaderSkins";
        private const string ResourceName = "imported_leaders.json";

        /// <summary>名字 key → { 简, 繁, 英 }。</summary>
        private static readonly Dictionary<string, string[]> Names = new Dictionary<string, string[]>(StringComparer.Ordinal);

        /// <summary>主战者号 → 皮肤号：素材按皮肤号命名，界面有时按主战者号取图。</summary>
        private static readonly Dictionary<int, int> CharaToSkin = new Dictionary<int, int>();

        private static bool _loaded;
        private static bool _warned;

        /// <summary>主战者号换成皮肤号（不是我们导入的就原样返回）。</summary>
        internal static int ResolveSkinId(int id)
        {
            EnsureLoaded();
            return CharaToSkin.TryGetValue(id, out int skin) ? skin : id;
        }

        private static string ConfigurationPath =>
            Path.Combine(Path.Combine(PathHelper.ModPath, SubDirectory), ResourceName);

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            try
            {
                string path = ConfigurationPath;
                if (!File.Exists(path))
                {
                    return;
                }

                JObject root = JObject.Parse(File.ReadAllText(path));
                JArray leaders = root["leaders"] as JArray;
                if (leaders == null)
                {
                    return;
                }

                int leadersCount = 0;
                int rowsCount = 0;
                foreach (JToken token in leaders)
                {
                    string key = (string)token["name_key"];
                    if (string.IsNullOrEmpty(key))
                    {
                        continue;
                    }

                    Names[key] = new[]
                    {
                        (string)token["chs"] ?? key,
                        (string)token["cht"] ?? key,
                        (string)token["eng"] ?? key,
                    };

                    JArray columns = token["columns"] as JArray;
                    if (columns == null || columns.Count < 20)
                    {
                        Plugin.Logger.LogWarning($"[Import] '{key}' has no usable master columns; skipped.");
                        continue;
                    }

                    string[] template = columns.Select(value => (string)value).ToArray();
                    var classes = (token["classes"] as JArray)?.Select(value => (int)value).ToList()
                                  ?? new List<int> { ParseInt(template, 4, 1) };
                    foreach (int classId in classes)
                    {
                        var row = (string[])template.Clone();
                        row[4] = classId.ToString(CultureInfo.InvariantCulture);
                        row[8] = "0";
                        Pending.Add(row);
                        rowsCount++;
                    }

                    leadersCount++;
                }

                Plugin.Logger.LogInfo(
                    $"[Import] {leadersCount} imported leader(s) / {rowsCount} master row(s) prepared " +
                    $"from {path}.");
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Import] Could not read '{ConfigurationPath}': {exception.Message}");
            }
        }

        private static int ParseInt(string[] columns, int index, int fallback)
        {
            return index < columns.Length && int.TryParse(columns[index], out int value) ? value : fallback;
        }

        private static readonly List<string[]> Pending = new List<string[]>();

        /// <summary>角色表建好之后补行（同名同职业已有就跳过，重复加载也安全）。</summary>
        [HarmonyPatch(typeof(Master), nameof(Master.StartLoadClassCharacter))]
        [HarmonyPostfix]
        private static void Master_StartLoadClassCharacter_Postfix(Master __instance)
        {
            try
            {
                EnsureLoaded();
                if (Pending.Count == 0 || __instance?.ClassCharacterList == null)
                {
                    return;
                }

                int added = 0;
                foreach (string[] columns in Pending)
                {
                    ClassCharacterMasterData row = null;
                    try
                    {
                        row = new ClassCharacterMasterData(columns);
                    }
                    catch (Exception exception)
                    {
                        if (!_warned)
                        {
                            _warned = true;
                            Plugin.Logger.LogWarning($"[Import] A master row could not be built: {exception.Message}");
                        }

                        continue;
                    }

                    bool exists = __instance.ClassCharacterList.Any(item => item != null &&
                        item.skin_id == row.skin_id && item.class_id == row.class_id);
                    if (!exists)
                    {
                        __instance.ClassCharacterList.Add(row);
                        added++;
                    }

                    CharaToSkin[row.chara_id] = row.skin_id;
                }

                if (added > 0)
                {
                    Plugin.Logger.LogInfo(
                        $"[Import] {added} leader row(s) added to the class character table " +
                        $"(total {__instance.ClassCharacterList.Count}).");
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Import] Could not extend the class character table: {exception.Message}");
            }
        }

        /// <summary>国服名字（游戏语言对应的简/繁/英）。</summary>
        [HarmonyPatch(typeof(Master), nameof(Master.GetClassCharaText))]
        [HarmonyPostfix]
        private static void Master_GetClassCharaText_Postfix(string key, ref string __result)
        {
            EnsureLoaded();
            if (!string.IsNullOrEmpty(key) && Names.TryGetValue(key, out string[] names))
            {
                __result = StoryTextLanguagePatches.ResolveUiText(names[0], names[1], names[2]);
            }
        }
    }
}
