using Cute;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Wizard;

namespace Shadowbus
{
    /// <summary>
    /// 剧情脚本对战的敌方 AI 本地化。
    ///
    /// 官方服务器会把「这一章的敌方 AI 用哪套牌组 / 风格 / 表情」写在 chapter 响应里，客户端拿
    /// enemy_ai_id 去 master 的 story_ai_setting 查，再按 ai_deck_filelist / ai_style_filelist /
    /// ai_emote_filelist 里的文件名去把三份 CSV 从资源里读出来。离线时这条链任何一环没走到，
    /// 敌方就是「有场上随从、但一副空牌组 → 完全不出牌」。
    ///
    /// 所以这里做两件事：
    ///   1. 战斗开始前把这一章 AI 需要的三份 CSV 从本地资源补读进 AIDeckDic / AIStyleDic /
    ///      AIEmoteDic，并重建 AIDataLibrary（修复「不出牌」）；
    ///   2. 允许用 <c>Mods/StorySpecialBattles/&lt;story_id&gt;.json</c> 的 <c>_ai</c> 段完全接管
    ///      敌方 AI，牌组/风格/表情都从 <c>Mods/AIData/{deck,style,emote}</c> 的本地 CSV 读，
    ///      这样没有官方数据也能离线还原脚本对战。
    /// </summary>
    internal static class StoryBattleAiPatches
    {
        private static bool _fileNameListsChecked;
        private static bool _storyAiSettingChecked;
        private static readonly HashSet<int> LoggedAiIds = new HashSet<int>();

        [HarmonyPatch(typeof(DataMgr), nameof(DataMgr.SetStoryAILogicAndDeckData))]
        [HarmonyPrefix]
        private static bool SetStoryAILogicAndDeckData_Prefix(DataMgr __instance, int classId, int enemyAiId)
        {
            try
            {
                EnsureAiFileNameLists();
                EnsureStoryAiSettingList();

                // 选人界面会按角色把整张表情表预载一遍（几百次查询），这里把那些统计汇总成一行再清零。
                LogAndResetEmoteLookupSummary();

                StoryBattleAiOverride local = StoryBattleAiOverride.Load(StoryOfflineData.CurrentStoryId);
                if (local != null)
                {
                    return !local.Apply(__instance, classId, enemyAiId);
                }

                EnsureOfficialAiAssets(__instance, enemyAiId);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[StoryAI] Story AI preparation failed: {ex}");
            }

            return true;
        }

        /// <summary>
        /// 重建 AIDataLibrary（= master 各表的镜像）。
        ///
        /// 注意 <c>DataMgr.RegisterAllAIData()</c> 是**先 Clear() 再重填**：如果前置的 AI 基础/公共表
        /// 还没加载，重填会半路抛异常，结果把已经注册好的牌组全清掉 —— 敌方就彻底没牌了。
        /// 所以这里先把那几张表补齐，缺任何一张就**不重建**（宁可保持现状，也不清空）。
        /// </summary>
        private static void RebuildAiDataLibrary(DataMgr dataMgr, string deckName)
        {
            Master master = Data.Master;
            if (master == null)
            {
                return;
            }

            EnsureAiCommonData(master);

            if (master.AIBasicDataList == null ||
                master.AICommonDataList == null ||
                master.AIAllyCommonDataList == null ||
                master.AIStyleCommonDataList == null)
            {
                Plugin.Logger.LogWarning(
                    "[StoryAI] AI basic/common master tables are incomplete " +
                    $"(basic={master.AIBasicDataList != null}, common={master.AICommonDataList != null}, " +
                    $"allyCommon={master.AIAllyCommonDataList != null}, " +
                    $"commonStyle={master.AIStyleCommonDataList != null}); " +
                    $"keeping the current AIDataLibrary so '{deckName}' is not dropped.");
                return;
            }

            dataMgr.RegisterAllAIData();
        }

        /// <summary>
        /// 补齐 RegisterAllAIData 需要的那几张 AI 公共表。这几张本来由 Master.Refresh 的
        /// StartLoadAIBasicAndCommonData 负责，但离线标题流程不保证走到，所以缺什么补什么。
        /// </summary>
        private static void EnsureAiCommonData(Master master)
        {
            try
            {
                if (master.AIBasicDataList == null)
                {
                    master.StartLoadAIBasicData();
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[StoryAI] Could not load the AI basic table: {ex.Message}");
            }

            try
            {
                if (master.AICommonFileNameList == null)
                {
                    master.AICommonFileNameList = ReadMasterCsvColumn("ai/ai_common_list");
                }

                if (master.AICommonDataList == null && master.AICommonFileNameList != null)
                {
                    master.StartLoadAICommonData(master.AICommonFileNameList);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[StoryAI] Could not load the AI common table: {ex.Message}");
            }

            try
            {
                if (master.AIAllyCommonFileNameList == null)
                {
                    master.AIAllyCommonFileNameList = ReadMasterCsvColumn("ai/ai_ally_common_list");
                }

                if (master.AIAllyCommonDataList == null && master.AIAllyCommonFileNameList != null)
                {
                    master.StartLoadAIAllyCommonData(master.AIAllyCommonFileNameList);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[StoryAI] Could not load the AI ally-common table: {ex.Message}");
            }

            try
            {
                if (master.AIStyleCommonDataList == null)
                {
                    string path = Toolbox.ResourcesManager.GetAssetTypePath(
                        "ai/ai_style_common",
                        ResourcesManager.AssetLoadPathType.Master,
                        true);
                    master.StartLoadAIStyleCommonParam(path);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[StoryAI] Could not load the AI common style table: {ex.Message}");
            }
        }

        /// <summary>读一张「一行一个文件名」的 master 列表（ai_common_list / ai_ally_common_list）。</summary>
        private static List<string> ReadMasterCsvColumn(string assetName)
        {
            List<string[]> csv = TryReadMasterCsv(assetName);
            if (csv == null)
            {
                return null;
            }

            var names = new List<string>();
            foreach (string[] row in csv)
            {
                if (row != null && row.Length > 0 && !string.IsNullOrEmpty(row[0]))
                {
                    names.Add(row[0].Trim());
                }
            }

            return names.Count > 0 ? names : null;
        }

        private static readonly HashSet<string> LoggedEmoteTextIds =
            new HashSet<string>(StringComparer.Ordinal);

        private const int EmoteSampleLimit = 12;
        private static int _emoteResolvedCount;
        private static int _emoteOfficialGapCount;
        private static int _emoteMisconfiguredCount;
        private static readonly List<string> EmoteMisconfiguredSamples = new List<string>();

        /// <summary>官方文本表里的全部 id（懒加载，见 <see cref="EnsureOfficialEmoteTextIds"/>）。</summary>
        private static HashSet<string> _officialEmoteTextIds;
        private static bool _officialEmoteTextIdsLoaded;

        /// <summary>逐条警告的上限：同一次战斗最多打这么多条，剩下的靠汇总行带数量。</summary>
        private const int EmoteWarningLimit = 20;
        private static int _emoteMisconfiguredWarnings;

        /// <summary>查失败过的 key；之后同一个 key 又查成功就说明是加载时序问题（见 <see cref="LoggedLateEmoteResolves"/>）。</summary>
        private static readonly HashSet<string> EmoteMissedKeys = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> LoggedLateEmoteResolves = new HashSet<string>(StringComparer.Ordinal);
        private static int _emoteLateResolveCount;

        /// <summary>
        /// <c>true</c> = 官方文本表里本来就没有这个 id，官方客户端同样只会显示原始 key
        /// （杂兵/小角色的表情文本官方从来没做过），**不是**我们选错了表情表。
        ///
        /// 判据是资源目录里导出的官方文本表 <c>&lt;资源根&gt;/story_ai/emotetext/*.csv</c>
        /// （<c>_tools/export_story_ai_emotes.py</c> 从 <c>master_emotetext</c> 导出，五个语区合计 5298 个 id）。
        /// 表读不到时退回旧规则（<c>ET_ST_*</c> 算官方缺口），至少不会把真问题吞掉。
        /// </summary>
        private static bool IsOfficialEmoteGap(string key)
        {
            // 客户端自己的表情文本字典才是权威：查失败只有两种可能 ——
            //   · 字典里压根没有这个 key（官方从没做过，例如 ET_挨拶_500042、ET_進化1_500211）
            //   · 字典里有，但值是空串（官方留的空条目，例如 ET_心配_301..307 / _403 / _404）
            // 两种都是**官方数据自己的空洞**，官方客户端同样显示原始 id，不需要我们修，只计数。
            // 只有"字典里有非空文本却仍然解析失败"才说明是我们把表搞错了 —— 那才值得报警。
            IDictionary<string, string> dictionary = EmoteWordDictionary;
            if (dictionary != null)
            {
                if (dictionary.TryGetValue(key, out string value))
                {
                    return string.IsNullOrEmpty(value);
                }

                return true;
            }

            // 读不到客户端字典时，退回资源目录里导出的官方文本表（某语区有非空文本即算官方有）。
            HashSet<string> ids = EnsureOfficialEmoteTextIds();
            return ids == null || !ids.Contains(key);
        }

        /// <summary>客户端当前语言的表情文本表（<c>Master.EmoteWordDic</c>，私有属性，反射取）。</summary>
        private static IDictionary<string, string> EmoteWordDictionary
        {
            get
            {
                if (_emoteWordDictionaryLoaded)
                {
                    return _emoteWordDictionary;
                }

                _emoteWordDictionaryLoaded = true;
                try
                {
                    System.Reflection.PropertyInfo property =
                        AccessTools.Property(typeof(Master), "EmoteWordDic");
                    _emoteWordDictionary = property?.GetValue(Data.Master, null) as IDictionary<string, string>;
                    Plugin.Logger.LogInfo(
                        $"[StoryAI] Client emote text dictionary: {( _emoteWordDictionary == null ? "unavailable" : _emoteWordDictionary.Count + " entrie(s)" )}.");
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning(
                        $"[StoryAI] Could not read the client emote text dictionary: {ex.Message}");
                }

                return _emoteWordDictionary;
            }
        }

        private static IDictionary<string, string> _emoteWordDictionary;
        private static bool _emoteWordDictionaryLoaded;

        private static HashSet<string> EnsureOfficialEmoteTextIds()
        {
            if (_officialEmoteTextIdsLoaded)
            {
                return _officialEmoteTextIds;
            }

            _officialEmoteTextIdsLoaded = true;
            try
            {
                string root = PathHelper.OfficialAIDataPath;
                string dir = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "emotetext");
                if (dir == null || !Directory.Exists(dir))
                {
                    return _officialEmoteTextIds;
                }

                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (string path in Directory.GetFiles(dir, "*.csv"))
                {
                    foreach (string line in File.ReadLines(path))
                    {
                        int comma = line.IndexOf(',');
                        if (comma <= 0)
                        {
                            continue;
                        }

                        string id = line.Substring(0, comma).Trim().Trim('"');
                        if (id.Length == 0 || string.Equals(id, "id", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        // 只有**非空文本**才算"官方定义了这个 id"。
                        // 官方表里值为空串的条目，客户端查到空值会当成没有、原样返回 key ——
                        // 那同样是官方缺口，不是我们选错了表。Chs 表 5298 条里正好 9 条是空的：
                        // ET_心配_301..307 / ET_心配_403 / ET_心配_404。
                        string value = line.Substring(comma + 1).Trim().Trim('"');
                        if (value.Length > 0)
                        {
                            ids.Add(id);
                        }
                    }
                }

                if (ids.Count == 0)
                {
                    return _officialEmoteTextIds;
                }

                _officialEmoteTextIds = ids;
                Plugin.Logger.LogInfo(
                    $"[StoryAI] Loaded {ids.Count} official emote text id(s) from '{dir}' for gap classification.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[StoryAI] Could not read the official emote text table: {ex.Message}");
            }

            return _officialEmoteTextIds;
        }

        /// <summary>
        /// 表情文本的唯一底层出口：<c>Master.GetEmoteWordText(key)</c> → <c>GetText(key, EmoteWordDic)</c>，
        /// <c>GetText</c> 查不到时**把 key 原样返回**，界面于是显示 `ET_進化2_500211` 这种原始键。
        ///
        /// 这里**故意不做任何兜底**（不返回空串、不改写结果）：原始 id 就是"这里有问题"的可视提示。
        /// 缺文本分两类，判据是**官方文本表里到底有没有这个 id**（见 <see cref="IsOfficialEmoteGap"/>），
        /// 不是靠 id 前缀猜：
        /// <list type="bullet">
        /// <item>官方表里没有：官方客户端同样只显示原始 id（杂兵/小角色的表情文本官方从没做过，
        /// 例如 <c>ET_挨拶_500042</c>、<c>ET_進化1_500211</c>）。只计数，不逐条打日志。</item>
        /// <item>官方表里有却解析不出来：说明客户端读的是**通用占位表**（它的 text_id 是不存在的通用 id），
        /// 也就是我们的 emotion 表选择出了问题 —— 这才是要修的 bug，逐条警告（上限 20 条）并给出请求方。</item>
        /// </list>
        /// </summary>
        [HarmonyPatch(typeof(Master), nameof(Master.GetEmoteWordText))]
        [HarmonyPostfix]
        private static void GetEmoteWordText_Postfix(string key, ref string __result)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            bool missing = string.IsNullOrEmpty(__result) || __result == key;
            lock (LoggedEmoteTextIds)
            {
                // 同一个 id 只统计一次（客户端会反复查同一个 id）。
                if (!LoggedEmoteTextIds.Add(key + (missing ? "#miss" : "#ok")))
                {
                    return;
                }
            }

            if (!missing)
            {
                _emoteResolvedCount++;

                // 之前查失败过、现在查成功了 —— 说明那个 key 第一次查的时候文本表还没就绪，
                // 而 Wizard.Emotion / AIEmoteDataAsset 的构造函数会把结果**冻结**进对象里，
                // 于是那一份永久留着原始 id（同一张表里其它条目又是正常的）。
                if (EmoteMissedKeys.Contains(key) && LoggedLateEmoteResolves.Add(key))
                {
                    _emoteLateResolveCount++;
                    Plugin.Logger.LogWarning(
                        $"[StoryAI] Emote text '{key}' missed earlier but resolves now: its text was " +
                        $"frozen before the emote text table was ready (load-order problem). " +
                        $"Requested by: {DescribeEmoteRequester()}");
                }

                return;
            }

            EmoteMissedKeys.Add(key);

            // 官方文本表里本来就没有这个 id：官方客户端同样显示原始 key，不是我们配错了表，只计数。
            if (IsOfficialEmoteGap(key))
            {
                _emoteOfficialGapCount++;
                return;
            }

            _emoteMisconfiguredCount++;
            if (EmoteMisconfiguredSamples.Count < EmoteSampleLimit)
            {
                EmoteMisconfiguredSamples.Add(key);
            }

            // 逐条警告有上限：同一次战斗最多 EmoteWarningLimit 条，其余靠汇总行带数量。
            if (_emoteMisconfiguredWarnings >= EmoteWarningLimit)
            {
                return;
            }

            _emoteMisconfiguredWarnings++;
            Plugin.Logger.LogWarning(
                $"[StoryAI] Emote text '{key}' IS defined in the official emote text table but the client " +
                $"could not resolve it, so the loaded emote table is probably the generic placeholder one " +
                $"(client shows the raw id). Requested by: {DescribeEmoteRequester()}");
        }

        /// <summary>
        /// 一行给出请求方（跳过引擎与插件自身的帧），比整段调用堆栈省太多日志。
        /// </summary>
        private static string DescribeEmoteRequester()
        {
            try
            {
                StackTrace trace = new StackTrace(false);
                for (int i = 0; i < trace.FrameCount; i++)
                {
                    MethodBase method = trace.GetFrame(i)?.GetMethod();
                    string declaring = method?.DeclaringType?.FullName;
                    if (string.IsNullOrEmpty(declaring) ||
                        declaring.StartsWith("System.", StringComparison.Ordinal) ||
                        declaring.StartsWith("Shadowbus.", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    return declaring + "." + method.Name;
                }
            }
            catch (Exception)
            {
            }

            return "<unknown>";
        }

        /// <summary>
        /// 进战斗时把「上一场战斗之后」累积的表情文本统计汇总成一行（选人界面的批量预载
        /// 就是靠这个从几百行压成一行），然后清零。
        /// </summary>
        private static void LogAndResetEmoteLookupSummary()
        {
            int resolved = _emoteResolvedCount;
            int gaps = _emoteOfficialGapCount;
            int misconfigured = _emoteMisconfiguredCount;
            int late = _emoteLateResolveCount;
            string samples = EmoteMisconfiguredSamples.Count > 0
                ? string.Join(", ", EmoteMisconfiguredSamples.ToArray())
                : "<none>";

            _emoteResolvedCount = 0;
            _emoteOfficialGapCount = 0;
            _emoteMisconfiguredCount = 0;
            _emoteLateResolveCount = 0;
            _emoteMisconfiguredWarnings = 0;
            EmoteMissedKeys.Clear();
            LoggedLateEmoteResolves.Clear();
            EmoteMisconfiguredSamples.Clear();

            if (gaps == 0 && misconfigured == 0)
            {
                Plugin.Logger.LogInfo(
                    $"[StoryAI] Emote text: {resolved} entrie(s) resolved, no gaps." +
                    (late > 0 ? $" {late} resolved late (load order)." : string.Empty));
                return;
            }

            Plugin.Logger.LogInfo(
                $"[StoryAI] Emote text: resolved={resolved}, " +
                $"official-gap={gaps} (ids the official emote text table itself never defines - the " +
                $"official client shows the raw id for these too), " +
                $"misconfigured={misconfigured} (defined officially but unresolvable; examples: {samples}), " +
                $"late-resolve={late} (missed first, resolved later = load-order freeze).");
        }

        /// <summary>
        /// 把这一章 AI 的三份 CSV 读齐。资源缺失时保持原样，只记录警告 —— 客户端接下来就会
        /// 因为 SearchDeckData 返回 null 而让敌方拿着空牌组上场。
        /// </summary>
        private static void EnsureOfficialAiAssets(DataMgr dataMgr, int enemyAiId)
        {
            if (enemyAiId <= 0 || Data.Master == null)
            {
                return;
            }

            StoryAISettingData setting = TryGetSetting(enemyAiId);
            if (setting == null)
            {
                LogOnce(enemyAiId, () => Plugin.Logger.LogWarning(
                    $"[StoryAI] Story AI {enemyAiId} is not in the local story AI master; " +
                    "the enemy will be deployed with an empty deck."));
                return;
            }

            string deckName = SafeDeckName(setting.DeckId);
            string styleName = SafeStyleName(setting.StyleId);
            string emoteName = SafeEmoteName(setting.EmoteId);

            bool deckLoaded = EnsureDeck(dataMgr, setting.DeckId, deckName);
            bool styleLoaded = EnsureStyle(dataMgr, setting.StyleId, styleName);
            bool emoteLoaded = EnsureEmote(dataMgr, setting.EmoteId, emoteName);

            // AIDataLibrary 只是 master 表的镜像，补读之后必须重建，否则 SearchDeckData 依旧 miss。
            RebuildAiDataLibrary(dataMgr, deckName);

            LogOnce(enemyAiId, () => Plugin.Logger.LogInfo(
                $"[StoryAI] Story AI {enemyAiId}: deck#={setting.DeckId} '{deckName}' " +
                $"{(deckLoaded ? "loaded" : "MISSING")}, style#={setting.StyleId} '{styleName}' " +
                $"{(styleLoaded ? "loaded" : "MISSING")}, emote#={setting.EmoteId} '{emoteName}' " +
                $"{(emoteLoaded ? "loaded" : "MISSING")}, logic={setting.LogicLevel}, " +
                $"innerEmote={setting.UseInnerEmote}, deckCards={CountDeckCards(deckName)}."));

            if (!deckLoaded)
            {
                Plugin.Logger.LogWarning(
                    $"[StoryAI] The enemy deck CSV for story AI {enemyAiId} ('{deckName}') is not available " +
                    $"locally; the enemy has no cards to play. Provide it as " +
                    $"Mods/AIData/deck/{deckName}.csv and reference it from the story's _ai block.");
            }
        }

        private static bool EnsureDeck(DataMgr dataMgr, int deckId, string deckName)
        {
            if (IsDeckLoaded(deckName))
            {
                return true;
            }

            try
            {
                Data.Master.StartLoadAIDeckData(deckId);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[StoryAI] Could not load AI deck #{deckId} ('{deckName}'): {ex.Message}");
            }

            return IsDeckLoaded(deckName);
        }

        private static bool EnsureStyle(DataMgr dataMgr, int styleId, string styleName)
        {
            if (IsStyleLoaded(styleName))
            {
                return true;
            }

            try
            {
                Data.Master.StartLoadAIStyleData(styleId);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[StoryAI] Could not load AI style #{styleId} ('{styleName}'): {ex.Message}");
            }

            return IsStyleLoaded(styleName);
        }

        private static bool EnsureEmote(DataMgr dataMgr, int emoteId, string emoteName)
        {
            if (IsEmoteLoaded(emoteName))
            {
                return true;
            }

            try
            {
                Data.Master.StartLoadAIEmoteData(emoteId);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[StoryAI] Could not load AI emote #{emoteId} ('{emoteName}'): {ex.Message}");
            }

            return IsEmoteLoaded(emoteName);
        }

        private static bool IsDeckLoaded(string deckName)
        {
            return !string.IsNullOrEmpty(deckName) &&
                   Data.Master?.AIDeckDic != null &&
                   Data.Master.AIDeckDic.TryGetValue("ai/" + deckName, out AICardDataAssetSet set) &&
                   set?.Set != null &&
                   set.Set.Count > 0;
        }

        private static bool IsStyleLoaded(string styleName)
        {
            return !string.IsNullOrEmpty(styleName) &&
                   Data.Master?.AIStyleDic != null &&
                   Data.Master.AIStyleDic.TryGetValue("ai/" + styleName, out List<AIPolicyDataAsset> list) &&
                   list != null &&
                   list.Count > 0;
        }

        private static bool IsEmoteLoaded(string emoteName)
        {
            return !string.IsNullOrEmpty(emoteName) &&
                   Data.Master?.AIEmoteDic != null &&
                   Data.Master.AIEmoteDic.TryGetValue("ai/" + emoteName, out List<AIEmoteDataAsset> list) &&
                   list != null &&
                   list.Count > 0;
        }

        private static int CountDeckCards(string deckName)
        {
            try
            {
                if (string.IsNullOrEmpty(deckName) ||
                    Data.Master?.AIDeckDic == null ||
                    !Data.Master.AIDeckDic.TryGetValue("ai/" + deckName, out AICardDataAssetSet set) ||
                    set?.Set == null)
                {
                    return 0;
                }

                return set.Set.Sum(asset => Math.Max(0, asset?.CardNum ?? 0));
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static StoryAISettingData TryGetSetting(int enemyAiId)
        {
            try
            {
                return Data.Master?.StoryAISettingList?.GetSettingData(enemyAiId);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string SafeDeckName(int deckId)
        {
            try
            {
                return Data.Master?.AIDeckFileNameList?.GetFileName(deckId) ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string SafeStyleName(int styleId)
        {
            try
            {
                return Data.Master?.AIStyleFileNameList?.GetFileName(styleId) ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string SafeEmoteName(int emoteId)
        {
            try
            {
                return Data.Master?.AIEmoteFileNameList?.GetFileName(emoteId) ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static void LogOnce(int enemyAiId, Action log)
        {
            lock (LoggedAiIds)
            {
                if (!LoggedAiIds.Add(enemyAiId))
                {
                    return;
                }
            }

            log();
        }

        /// <summary>
        /// 离线标题流程不一定会跑 Master.Refresh 的那一段，这三张文件名表可能一直是 null。
        /// 直接从本地 master 资源里补读一次。
        /// </summary>
        private static void EnsureAiFileNameLists()
        {
            if (_fileNameListsChecked || Data.Master == null)
            {
                return;
            }

            _fileNameListsChecked = true;

            if (Data.Master.AIDeckFileNameList == null)
            {
                List<string[]> csv = TryReadMasterCsv("ai/ai_deck_filelist");
                if (csv != null && csv.Count > 0)
                {
                    Data.Master.AIDeckFileNameList = new AIDeckFileNameList(csv);
                    Plugin.Logger.LogInfo(
                        $"[StoryAI] Loaded the local AI deck filename list ({csv.Count} entries).");
                }
                else
                {
                    _fileNameListsChecked = false;
                }
            }

            if (Data.Master.AIStyleFileNameList == null)
            {
                List<string[]> csv = TryReadMasterCsv("ai/ai_style_filelist");
                if (csv != null && csv.Count > 0)
                {
                    Data.Master.AIStyleFileNameList = new AIStyleFileNameList(csv);
                    Plugin.Logger.LogInfo(
                        $"[StoryAI] Loaded the local AI style filename list ({csv.Count} entries).");
                }
            }

            if (Data.Master.AIEmoteFileNameList == null)
            {
                List<string[]> csv = TryReadMasterCsv("ai/ai_emote_filelist");
                if (csv != null && csv.Count > 0)
                {
                    Data.Master.AIEmoteFileNameList = new AIEmoteFileNameList(csv);
                    Plugin.Logger.LogInfo(
                        $"[StoryAI] Loaded the local AI emote filename list ({csv.Count} entries).");
                }
            }
        }

        private static void EnsureStoryAiSettingList()
        {
            if (_storyAiSettingChecked || Data.Master == null)
            {
                return;
            }

            _storyAiSettingChecked = true;

            IReadOnlyList<StoryAISettingData> table = null;
            try
            {
                table = Data.Master.StoryAISettingList?.GetSettingDataTable();
            }
            catch (Exception)
            {
            }

            if (table != null && table.Count > 0)
            {
                return;
            }

            try
            {
                string path = Toolbox.ResourcesManager.GetAssetTypePath(
                    "ai/story_ai_setting",
                    ResourcesManager.AssetLoadPathType.Master,
                    true);
                Data.Master.StartLoadStoryAISettingData(path);
                Plugin.Logger.LogInfo(
                    $"[StoryAI] Loaded the local story AI setting master " +
                    $"({Data.Master.StoryAISettingList?.GetSettingDataTable()?.Count ?? 0} entries).");
            }
            catch (Exception ex)
            {
                _storyAiSettingChecked = false;
                Plugin.Logger.LogWarning($"[StoryAI] Could not load the story AI setting master: {ex.Message}");
            }
        }

        private static List<string[]> TryReadMasterCsv(string assetName)
        {
            try
            {
                string path = Toolbox.ResourcesManager.GetAssetTypePath(
                    assetName,
                    ResourcesManager.AssetLoadPathType.Master,
                    true);
                TextAsset asset = Toolbox.ResourcesManager.LoadObject(path, true, false) as TextAsset;
                if (asset == null)
                {
                    return null;
                }

                return Utility.ConvertCSV_Array(asset.ToString(), true);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[StoryAI] Could not read master CSV '{assetName}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// <c>Mods/StorySpecialBattles/&lt;story_id&gt;.json</c> 里可选的 <c>_ai</c> 段。
        /// </summary>
        private sealed class StoryBattleAiOverride
        {
            private string _deckPath;
            private string _stylePath;
            private string _emotePath;
            private int _logicLevel = 2;
            private int _maxLife = 20;
            private bool _useInnerEmote = true;
            private List<int> _cards;
            private int _storyId;

            internal static StoryBattleAiOverride Load(int storyId)
            {
                if (storyId <= 0)
                {
                    return null;
                }

                string path = Path.Combine(
                    Plugin.ModPath,
                    "StorySpecialBattles",
                    storyId.ToString(CultureInfo.InvariantCulture) + ".json");
                if (!File.Exists(path))
                {
                    return null;
                }

                try
                {
                    JObject root = JObject.Parse(File.ReadAllText(path));
                    JToken block = root["_ai"];
                    if (block == null || block.Type != JTokenType.Object)
                    {
                        return null;
                    }

                    var result = new StoryBattleAiOverride
                    {
                        _storyId = storyId,
                        _deckPath = ResolvePath(PathHelper.AIDeckPath, block.Value<string>("deck")),
                        _stylePath = ResolvePath(PathHelper.AIStylePath, block.Value<string>("style")),
                        _emotePath = ResolvePath(PathHelper.AIEmotePath, block.Value<string>("emote")),
                        _logicLevel = block.Value<int?>("logic") ?? 2,
                        _maxLife = block.Value<int?>("max_life") ?? 20,
                        _useInnerEmote = block.Value<bool?>("use_inner_emote") ?? true
                    };

                    JToken cards = block["cards"];
                    if (cards is JArray array)
                    {
                        result._cards = ParseCards(array);
                    }

                    return result;
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning(
                        $"[StoryAI] Ignoring invalid _ai block in " +
                        $"'{Path.GetFileName(path)}': {ex.Message}");
                    return null;
                }
            }

            private static List<int> ParseCards(JArray array)
            {
                var cards = new List<int>();
                foreach (JToken entry in array)
                {
                    if (entry.Type == JTokenType.Integer)
                    {
                        int single = entry.Value<int>();
                        if (single > 0)
                        {
                            cards.Add(single);
                        }

                        continue;
                    }

                    int id = entry.Value<int?>("id") ?? 0;
                    int num = entry.Value<int?>("num") ?? entry.Value<int?>("count") ?? 1;
                    if (id <= 0 || num <= 0)
                    {
                        continue;
                    }

                    for (int i = 0; i < num; i++)
                    {
                        cards.Add(id);
                    }
                }

                return cards;
            }

            private static string ResolvePath(string folder, string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return null;
                }

                // 查找顺序：玩家自己的 Mods/AIData/<类型>/ 优先（方便覆盖官方数据），
                // 其次是资源目录里的官方导出 <资源根>/story_ai/<类型>/。
                var candidates = new List<string>();
                if (!Path.IsPathRooted(value))
                {
                    candidates.Add(Path.Combine(folder, value));
                    string official = PathHelper.OfficialAIDataPath;
                    if (!string.IsNullOrEmpty(official))
                    {
                        candidates.Add(Path.Combine(official, Path.GetFileName(folder), value));
                    }

                    // 也接受相对 Mods 的写法（例如 "AIData/deck/xxx.csv"）。
                    candidates.Add(Path.Combine(Plugin.ModPath, value));
                }
                else
                {
                    candidates.Add(value);
                }

                foreach (string candidate in candidates)
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                Plugin.Logger.LogWarning(
                    $"[StoryAI] Local AI CSV was not found ('{value}'). Looked in: " +
                    string.Join(" | ", candidates.ToArray()));
                return null;
            }

            /// <summary>返回 true 表示已经完全接管，原方法不用再跑。</summary>
            internal bool Apply(DataMgr dataMgr, int classId, int enemyAiId)
            {
                string deckKey = AIManager.RegisterLocalDeckCsv(_deckPath);
                string styleKey = AIManager.RegisterLocalStyleCsv(_stylePath);
                string emoteKey = AIManager.RegisterLocalEmoteCsv(_emotePath);

                if (deckKey == null && (_cards == null || _cards.Count == 0))
                {
                    Plugin.Logger.LogWarning(
                        $"[StoryAI] Story {_storyId} asks for a local enemy AI but neither a readable deck CSV " +
                        "nor an inline card list was provided; falling back to the master AI data.");
                    return false;
                }

                List<int> cards = _cards != null && _cards.Count > 0
                    ? _cards
                    : TryGetDeckCards(deckKey);

                if (cards == null || cards.Count == 0)
                {
                    Plugin.Logger.LogWarning(
                        $"[StoryAI] Story {_storyId} local enemy deck '{_deckPath}' produced no cards; " +
                        "falling back to the master AI data.");
                    return false;
                }

                // 先重建 AIDataLibrary，注册进去的本地 CSV 才会被 AI 真正读到。
                RebuildAiDataLibrary(dataMgr, deckKey ?? "(inline cards)");

                dataMgr.SetEnemyAIDeckFromCustomDeck(
                    classId,
                    cards,
                    -1,
                    _logicLevel,
                    _maxLife,
                    0,
                    0,
                    _useInnerEmote,
                    enemyAiId);

                // SetEnemyAIDeckFromCustomDeck 会把 deckName 写成空串，这里补回真正的本地 key。
                dataMgr.m_AIDataLibrary.SaveBattleSetUpInfo(
                    classId,
                    ToLogicLevel(_logicLevel),
                    deckKey ?? string.Empty,
                    styleKey ?? string.Empty,
                    emoteKey ?? string.Empty,
                    true,
                    _useInnerEmote,
                    enemyAiId,
                    null);

                Plugin.Logger.LogInfo(
                    $"[StoryAI] Story {_storyId} enemy AI uses local data: class={classId}, cards={cards.Count}, " +
                    $"logic={_logicLevel}, maxLife={_maxLife}, deck='{deckKey ?? "(inline cards)"}', " +
                    $"style='{styleKey ?? "(none)"}', emote='{emoteKey ?? "(none)"}'.");
                return true;
            }

            private static List<int> TryGetDeckCards(string deckKey)
            {
                try
                {
                    return AIDataLibrary.GetAIDeckCardList(deckKey);
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning(
                        $"[StoryAI] Could not read the local AI deck '{deckKey}': {ex.Message}");
                    return null;
                }
            }

            private static AI_LOGIC_LV ToLogicLevel(int logicLevel)
            {
                return logicLevel == 0
                    ? AI_LOGIC_LV.WEAK
                    : logicLevel == 1
                        ? AI_LOGIC_LV.MIDDLE
                        : AI_LOGIC_LV.STRONG;
            }
        }
    }
}
