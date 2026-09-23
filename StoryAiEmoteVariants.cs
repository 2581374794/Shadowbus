using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Wizard;

namespace Shadowbus
{
    /// <summary>
    /// 剧情对战的「章节表情表」索引。
    ///
    /// 官方把剧情用的表情/脸/动作/台词做成了 <c>emote_chara_&lt;皮肤&gt;_&lt;变体&gt;.csv</c>：变体表里的
    /// ST 台词后缀带篇章号（<c>ET_ST_進化2_500211_09_01</c> / <c>_20_01</c>），所以「第 N 篇该读哪张表」
    /// 可以从官方数据自身推导，导出工具已把它写成 <c>story_ai/emotechara/_variants.csv</c>：
    /// <code>
    /// chara,variation,arcs
    /// 500211,1,9
    /// 500211,3,9|20
    /// 2515,1,20
    /// </code>
    ///
    /// 客户端的选择方式是
    /// <c>BattleSettingData.GetEmotionId(charaId, variationId)</c>：
    /// <list type="bullet">
    /// <item>variationId == 0 → <c>"&lt;skin_id&gt;"</c>：通用占位表，动作列为空（回落 idle）、文本用的是
    /// 不存在的通用 id，界面会显示原始 id；</item>
    /// <item>variationId != 0 → <c>"&lt;skin_id&gt;_&lt;variation&gt;"</c>：章节表，脸/动作/语音/ST 台词齐全。</item>
    /// </list>
    /// 所以这里只换算「篇章 → 变体号」，选对表之后动作与台词都来自官方数据，不需要任何自造。
    /// </summary>
    internal static class StoryAiEmoteVariants
    {
        private sealed class Variant
        {
            public int Variation;
            public List<int> Arcs = new List<int>();
        }

        private static readonly Dictionary<int, List<Variant>> VariantsByChara =
            new Dictionary<int, List<Variant>>();

        private static readonly Dictionary<int, int> SectionByAiId = new Dictionary<int, int>();
        private static string _loadedPath;
        private static DateTime _loadedWriteTimeUtc = DateTime.MinValue;

        /// <summary>
        /// 这一章/这一场该用的表情表 key（<c>"500211_3"</c>）；推导不出来时返回 <paramref name="charaId"/>
        /// 对应的皮肤 id（等价于原来的行为，界面会用「原文」提示这里没配好）。
        /// </summary>
        internal static string ResolveEmotionId(int charaId, int arc)
        {
            return FormatEmotionId(charaId, ResolveVariation(charaId, arc));
        }

        /// <summary>
        /// 按官方那套命名拼出表情表 key：<c>&lt;皮肤&gt;</c>（变体 0）或 <c>&lt;皮肤&gt;_&lt;变体&gt;</c>。
        /// </summary>
        internal static string FormatEmotionId(int charaId, int variation)
        {
            int skinId = ResolveSkinId(charaId);
            if (skinId <= 0)
            {
                return null;
            }

            return variation > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0}_{1}", skinId, variation)
                : skinId.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 这一章该用的变体号（写进 battle_settings 的 *_emotion_override）；推导不出来时返回 0
        /// （等价于原来的行为，界面会用「原文」提示这里没配好）。
        /// </summary>
        internal static int ResolveVariation(int charaId, int arc)
        {
            int skinId = ResolveSkinId(charaId);
            if (skinId <= 0 || arc <= 0)
            {
                return 0;
            }

            EnsureLoaded();
            if (!VariantsByChara.TryGetValue(skinId, out List<Variant> variants))
            {
                return 0;
            }

            // 取「覆盖该篇章」的变体里编号最小的那个：同一篇章有多张表时（例如第 9 篇的前/后半）
            // 单靠篇章号区分不了，选最小以保证结果稳定、可预期。
            Variant picked = variants
                .Where(variant => variant.Variation > 0 && variant.Arcs.Contains(arc))
                .OrderBy(variant => variant.Variation)
                .FirstOrDefault();
            return picked?.Variation ?? 0;
        }

        /// <summary>
        /// 从敌方 AI id 反查它属于哪一篇（自定义练习选了「剧情AI」时没有章节上下文，只能这样拿篇章）。
        /// 用的是官方那套 <c>CreateOriginalStoryAiId</c> 的正向公式做穷举反查，结果会缓存。
        /// </summary>
        internal static int ResolveSectionForAiId(int enemyAiId)
        {
            if (enemyAiId <= 0)
            {
                return 0;
            }

            lock (SectionByAiId)
            {
                if (SectionByAiId.Count == 0)
                {
                    BuildSectionMap();
                }

                return SectionByAiId.TryGetValue(enemyAiId, out int section) ? section : 0;
            }
        }

        private static void BuildSectionMap()
        {
            for (int section = 1; section <= 30; section++)
            {
                for (int classId = 1; classId <= 8; classId++)
                {
                    for (int chapter = 1; chapter <= 60; chapter++)
                    {
                        int aiId = StoryOfflineData.CreateOriginalStoryAiId(section, classId, chapter);
                        if (aiId > 0 && !SectionByAiId.ContainsKey(aiId))
                        {
                            SectionByAiId[aiId] = section;
                        }
                    }
                }
            }

            Plugin.Logger.LogInfo(
                $"[StoryAI] Story AI id -> section map built ({SectionByAiId.Count} entries).");
        }

        private static int ResolveSkinId(int charaId)
        {
            if (charaId <= 0)
            {
                return 0;
            }

            try
            {
                ClassCharacterMasterData chara =
                    GameMgr.GetIns().GetDataMgr().GetCharaPrmByCharaId(charaId);
                if (chara != null && chara.skin_id > 0)
                {
                    return chara.skin_id;
                }
            }
            catch (Exception)
            {
                // 角色表还没加载时退回角色 id 本身。
            }

            return charaId;
        }

        /// <summary>
        /// 手工钉住某一章的表情表变体号（<c>story_ai/emotechara/_chapter_variations.csv</c>，
        /// 列 <c>story_id,player_variation,enemy_variation</c>）。
        ///
        /// 绝大多数章节不需要它：变体号能从官方表自身的 ST 后缀（<c>_20_01</c>）按篇章推出来。
        /// 只有「同一篇章存在多张官方表」（例如第 9 篇的 <c>_09_01</c> 与 <c>_09_02</c>）时，
        /// 本地数据无法判断当前章属于哪一张，默认取编号最小的那张 —— 想改就写这个文件。
        /// </summary>
        internal static void ApplyChapterPin(
            int storyId,
            List<Dictionary<string, object>> battleSettings)
        {
            if (storyId <= 0 || battleSettings == null || battleSettings.Count == 0)
            {
                return;
            }

            EnsureChapterPinsLoaded();
            if (!ChapterPins.TryGetValue(storyId, out ChapterPin pin))
            {
                return;
            }

            foreach (Dictionary<string, object> setting in battleSettings)
            {
                if (setting == null)
                {
                    continue;
                }

                if (pin.PlayerVariation > 0)
                {
                    setting["player_emotion_override"] = pin.PlayerVariation;
                }

                if (pin.EnemyVariation > 0)
                {
                    setting["enemy_emotion_override"] = pin.EnemyVariation;
                }
            }

            // 一次会话里每章只报一次：官方表给 282 章钉了变体，逐次打印会让剧情列表刷屏。
            if (LoggedChapterPins.Add(storyId))
            {
                Plugin.Logger.LogInfo(
                    $"[StoryAI] Story {storyId} emote tables pinned by _chapter_variations.csv: " +
                    $"player={pin.PlayerVariation}, enemy={pin.EnemyVariation}.");
            }
        }

        private static readonly HashSet<int> LoggedChapterPins = new HashSet<int>();

        private sealed class ChapterPin
        {
            public int PlayerVariation;
            public int EnemyVariation;
        }

        private static readonly Dictionary<int, ChapterPin> ChapterPins =
            new Dictionary<int, ChapterPin>();

        private static string _loadedPinPath;
        private static DateTime _loadedPinWriteTimeUtc = DateTime.MinValue;

        private static void EnsureChapterPinsLoaded()
        {
            string path = StoryAiEmoteVariantsPath == null
                ? null
                : Path.Combine(Path.GetDirectoryName(StoryAiEmoteVariantsPath) ?? string.Empty,
                    "_chapter_variations.csv");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                DateTime writeTimeUtc = File.GetLastWriteTimeUtc(path);
                lock (ChapterPins)
                {
                    if (path == _loadedPinPath && writeTimeUtc == _loadedPinWriteTimeUtc)
                    {
                        return;
                    }

                    ChapterPins.Clear();
                    foreach (string line in File.ReadLines(path).Skip(1))
                    {
                        string[] cells = line.Split(',');
                        if (cells.Length < 3 || !int.TryParse(cells[0].Trim(), out int storyId))
                        {
                            continue;
                        }

                        int.TryParse(cells[1].Trim(), out int playerVariation);
                        int.TryParse(cells[2].Trim(), out int enemyVariation);
                        ChapterPins[storyId] = new ChapterPin
                        {
                            PlayerVariation = playerVariation,
                            EnemyVariation = enemyVariation
                        };
                    }

                    _loadedPinPath = path;
                    _loadedPinWriteTimeUtc = writeTimeUtc;
                    Plugin.Logger.LogInfo(
                        $"[StoryAI] Loaded {ChapterPins.Count} pinned chapter emote table(s) from '{path}'.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[StoryAI] Could not read the chapter emote pin file '{path}': {ex.Message}");
            }
        }

        private static void EnsureLoaded()
        {
            string path = StoryAiEmoteVariantsPath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                DateTime writeTimeUtc = File.GetLastWriteTimeUtc(path);
                lock (VariantsByChara)
                {
                    // 文件可能之后再补进来，按写入时间热重载。
                    if (path == _loadedPath && writeTimeUtc == _loadedWriteTimeUtc)
                    {
                        return;
                    }

                    VariantsByChara.Clear();
                    int count = 0;
                    foreach (string line in File.ReadLines(path).Skip(1))
                    {
                        string[] cells = line.Split(',');
                        if (cells.Length < 3 ||
                            !int.TryParse(cells[0].Trim(), out int charaId) ||
                            !int.TryParse(cells[1].Trim(), out int variation))
                        {
                            continue;
                        }

                        var variant = new Variant { Variation = variation };
                        foreach (string arc in cells[2].Split('|'))
                        {
                            if (int.TryParse(arc.Trim(), out int value) && value > 0)
                            {
                                variant.Arcs.Add(value);
                            }
                        }

                        if (!VariantsByChara.TryGetValue(charaId, out List<Variant> list))
                        {
                            list = new List<Variant>();
                            VariantsByChara[charaId] = list;
                        }

                        list.Add(variant);
                        count++;
                    }

                    _loadedPath = path;
                    _loadedWriteTimeUtc = writeTimeUtc;
                    Plugin.Logger.LogInfo(
                        $"[StoryAI] Loaded {count} chapter emote variant(s) from '{path}'.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[StoryAI] Could not read the emote variant index '{path}': {ex.Message}");
            }
        }

        private static string StoryAiEmoteVariantsPath
        {
            get
            {
                try
                {
                    string root = PathHelper.OfficialAIDataPath;
                    return string.IsNullOrEmpty(root)
                        ? null
                        : Path.Combine(root, "emotechara", "_variants.csv");
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }
    }
}
