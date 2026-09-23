using Cute;
using LitJson;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using Wizard;

namespace Shadowbus
{
    internal static class StoryOfflineData
    {
        private const string StoryInfoTaskName = nameof(StoryInfoTask);
        private const string StoryLeaderSelectTaskName = nameof(StoryLeaderSelectTask);
        private const string StoryStartTaskName = nameof(StoryStartTask);
        private const string StoryDeckListTaskName = nameof(StoryDeckListTask);
        private const string StoryFinishTaskName = nameof(StoryFinishTask);
        private const string StoryAllFinishTaskName = nameof(StoryAllFinishTask);
        private const int DefaultChapterButtonBackgroundId = 2;

        /// <summary>
        /// 最近一次生成离线数据的剧情 id。StoryStartTask 离战斗最近，所以它最权威。
        /// 供本地 AI 覆盖（Mods/StorySpecialBattles/&lt;story_id&gt;.json 的 _ai 段）使用。
        /// </summary>
        internal static int CurrentStoryId { get; private set; }

        internal static void RememberStoryId(int storyId)
        {
            if (storyId > 0)
            {
                CurrentStoryId = storyId;
            }
        }

        private static readonly Regex ScenarioParamRegex = new Regex(
            @"^story_scenario_param_(?<section>\d+)_(?<class>\d+)_(?<chapter>\d+[a-z]?)_(?<part>[12])(?:_subchapter(?<subchapter>\d+))?\.unity3d$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // The story AI master only links an AI id to deck/style/emote CSV files. Enemy
        // class is server metadata, so recover it from the dominant non-neutral class
        // in each original AI deck bundled with the client.
        private static readonly HashSet<int>[] OriginalEnemyAiIdsByClass =
        {
            new HashSet<int>(),
            new HashSet<int> { 1006, 1007, 1009, 1010, 2003, 2012, 3007, 4006, 5005, 5009, 7009, 8003, 101003, 110303, 110503, 110706, 110708, 110202, 110206, 110212, 120005, 120013, 130607, 130509, 150101, 150108, 150407, 150201, 150206, 150702, 150708, 160005, 160012, 160015, 170005, 200108, 270013, 280004 },
            new HashSet<int> { 1001, 1008, 2001, 2006, 2011, 3009, 3011, 3012, 4005, 6001, 6008, 6009, 7005, 7010, 101002, 110106, 110112, 110312, 110609, 110611, 110510, 110408, 110409, 110707, 110806, 110807, 110809, 110810, 110211, 120003, 120010, 130608, 130306, 150411, 150204, 160004, 180303, 180703, 220010, 230611, 270010, 280010 },
            new HashSet<int> { 1005, 1014, 2007, 2010, 2014, 3001, 3006, 3010, 3014, 4004, 4008, 4013, 4014, 5003, 5010, 5014, 6005, 6013, 6014, 7002, 7014, 8004, 101004, 101009, 110105, 110302, 110307, 110605, 110507, 110410, 110204, 120014, 130604, 130308, 130807, 130808, 150111, 150202, 150705, 160006, 170006, 280020 },
            new HashSet<int> { 1004, 2004, 3004, 3008, 3013, 4001, 4007, 4009, 4010, 4011, 5004, 6003, 7003, 7012, 101005, 110102, 110311, 110504, 120004, 130507, 130804, 140004, 150104, 150401, 150405, 150408, 150207, 200405, 200407, 200408, 200504, 200606, 220006, 240407, 240411, 240508, 250007, 260709, 270009, 280015 },
            new HashSet<int> { 1003, 1012, 3003, 4002, 5001, 5006, 5008, 5011, 6002, 6011, 7004, 7011, 8005, 101008, 110103, 110304, 110604, 110502, 110511, 110402, 110406, 110710, 110802, 120008, 120009, 130303, 130810, 150102, 150404, 150210, 180310, 190008, 200503, 200508, 200607, 230607, 240410, 240509, 270008, 290008 },
            new HashSet<int> { 1002, 1011, 2002, 2008, 3005, 4003, 5002, 5012, 6004, 6007, 6010, 7006, 7013, 101007, 110110, 110603, 110512, 110404, 110711, 110803, 120002, 130603, 130503, 130309, 150105, 150203, 150703, 170003, 180802, 180809, 200103, 200104, 200506, 200604, 210006, 210007, 210008, 210010, 230612, 250015, 290032 },
            new HashSet<int> { 1013, 2005, 2009, 2013, 3002, 4012, 5007, 5013, 6006, 6012, 7001, 7007, 7008, 101006, 110111, 110306, 110607, 110703, 110804, 110210, 120012, 130609, 130508, 130805, 150103, 150402, 150701, 150704, 150706, 160007, 170004, 180807 },
            new HashSet<int> { 8001, 8002, 8006, 101010, 101011, 110602, 110411, 110705, 110207, 120015, 130002, 130610, 130505, 130511, 130305, 130312, 130811, 140006, 140007, 140008, 140011, 170007, 170011, 170012, 180206, 180209, 180210, 180309, 180707, 180711, 190006, 190007, 190010, 210012, 210013, 220004, 220005, 220007, 220021, 220024, 240511, 250005, 250010, 260710, 290017, 290024, 290035, 290038 }
        };

        private static readonly Dictionary<int, int> OriginalEnemyCharaIdByAiId =
            new Dictionary<int, int>
            {
                [140006] = 500049, // Megaera
                [140007] = 500048, // Alecto
                [140008] = 500047, // Tisiphone
                [140011] = 500046, // Belphomet
                [160004] = 500101, // Bayleon
                [160015] = 500107, // Viridia Magna
                [170007] = 500217, // mechanical infantry
                [170012] = 500211, // fused Belphomet
                [190010] = 500310, // Iceschillendrig
                [220021] = 508,    // Nexus
                [220024] = 500601, // transformed Iceschillendrig
                [240410] = 3505,   // Anserge
                [240411] = 3514,   // Suhlon
                [240508] = 3504,   // Mizuchi
                [250007] = 3514,   // Suhlon
                [250015] = 500712, // transformed Taketsumi
                [260709] = 500943, // dragon
                [270008] = 4105,   // Cornelius
                [270009] = 4104,   // Lilium
                [270010] = 4102,   // Weiss
                [270013] = 500948, // Castelle
                [290008] = 500211, // fused Belphomet
                [290017] = 500731, // Maiser
                [290024] = 508,    // Nexus
                [290032] = 500712, // transformed Taketsumi
                [290035] = 4528,   // Nerva
                [290038] = 4528    // Nerva
            };

        private static readonly object ManifestLock = new object();
        private static StoryManifest _cachedManifest;
        private static DateTime _cachedManifestWriteTimeUtc;

        internal static bool CanHandle(string taskName)
        {
            return taskName == StoryInfoTaskName ||
                taskName == StoryLeaderSelectTaskName ||
                taskName == StoryStartTaskName ||
                taskName == StoryDeckListTaskName ||
                taskName == StoryFinishTaskName ||
                taskName == StoryAllFinishTaskName;
        }

        internal static bool TryCreateResponse(NetworkTask task, out JsonData response)
        {
            response = null;
            try
            {
                object responseObject;
                if (task is StoryInfoTask && task.Params is StoryInfoTask.StoryInfoTaskParam infoParam)
                {
                    responseObject = CreateStoryInfoResponse(infoParam);
                }
                else if (task is StoryLeaderSelectTask && task.Params is StoryLeaderSelectTask.StoryLeaderSelectTaskParam leaderParam)
                {
                    responseObject = CreateLeaderSelectResponse(leaderParam);
                }
                else if (task is StoryStartTask startTask)
                {
                    responseObject = CreateStoryStartResponse(
                        startTask.Params as StoryStartTask.StoryStartTaskParam);
                }
                else if (task is StoryDeckListTask)
                {
                    responseObject = CreateStoryDeckListResponse();
                }
                else if (task is StoryFinishTask)
                {
                    responseObject = CreateStoryFinishResponse();
                }
                else if (task is StoryAllFinishTask)
                {
                    responseObject = CreateResponseEnvelope(new Dictionary<string, object>());
                }
                else
                {
                    return false;
                }

                response = JsonMapper.ToObject(JsonConvert.SerializeObject(responseObject));
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Offlinizer] Failed to create local story data for {task.GetType().Name}: {ex}");
                return false;
            }
        }

        private static object CreateStoryInfoResponse(StoryInfoTask.StoryInfoTaskParam param)
        {
            StoryManifest manifest = GetManifest();
            int? selectedClassId = GetClassId(param.chara_id);
            List<ScenarioChapter> chapters = manifest.GetChapters(param.section_id, selectedClassId);
            List<Dictionary<string, object>> chapterResponses = new List<Dictionary<string, object>>();

            for (int index = 0; index < chapters.Count; index++)
            {
                chapterResponses.Add(CreateChapterResponse(
                    chapters[index],
                    GetNextChapterId(chapters, index),
                    param.chara_id,
                    index));
            }

            Plugin.Logger.LogInfo(
                $"[Offlinizer] Created StoryInfoTask data: section={param.section_id}, " +
                $"class={(selectedClassId?.ToString(CultureInfo.InvariantCulture) ?? "all")}, " +
                $"chapters={chapterResponses.Count}, battles={chapters.Count(chapter => chapter.HasSecondHalf)}.");

            return CreateResponseEnvelope(new Dictionary<string, object>
            {
                ["story_master_list"] = chapterResponses,
                ["maintenance_card_list"] = Array.Empty<int>()
            });
        }

        private static object CreateLeaderSelectResponse(StoryLeaderSelectTask.StoryLeaderSelectTaskParam param)
        {
            StoryManifest manifest = GetManifest();

            // 官方行为（抓包实测）：leader 列表 = **该篇各章的 chara_id**，按出现顺序去重。
            //   section 1  → 1..8（职业默认）
            //   section 15 → 500701 / 500732 / 500704（该篇的三位主角，各自带 13 章）
            //   section 12 → 500401 / 500402 / 500403 / 500404（暗黑世界篇的四位主角）
            // 之前"按职业各取一个战斗皮肤角色"形状就错了 —— 角色剧情篇根本不是按职业分的。
            List<int> leaderIds = GetOfficialSectionLeaders(param.section_id);

            // 官方关卡表里这一篇没有 chara_id 时（少数篇目），回退到"每职业取剧情里的那个人"，
            // 再不行才是职业默认角色 —— 与加这个功能之前的行为一致。
            if (leaderIds.Count == 0)
            {
                leaderIds = manifest.GetClassIds(param.section_id)
                    .Select(classId => ResolveLeaderCharacterId(manifest, param.section_id, classId))
                    .Where(charaId => charaId != 0)
                    .Distinct()
                    .ToList();
            }

            List<Dictionary<string, object>> leaders = leaderIds
                .Select(charaId => new Dictionary<string, object>
                {
                    ["chara_id"] = charaId,
                    ["is_finished"] = false,
                    ["current_chapter"] = "1"
                })
                .ToList();

            // 官方数据不全时补人：这一篇应该有几个 leader 由 released_chara_count 说了算
            // （section 17 是 4，我们手上只有 1 个的官方记录）。缺的用**该篇（含最终章）出现过
            // 的皮肤号**反查：皮肤 ↔ 剧情 leader 同角色不同记录（4107 ↔ 500901），
            // 没有对应记录的皮肤就用它自己。
            int quota = GetSectionLeaderQuota(param.section_id);
            if (quota > leaders.Count)
            {
                foreach (int skin in GetSectionProtagonistSkins(param.section_id))
                {
                    if (leaders.Count >= quota)
                    {
                        break;
                    }

                    int candidate = ResolveLeaderAlias(skin);
                    if (candidate <= 0 || leaderIds.Contains(candidate))
                    {
                        continue;
                    }

                    leaderIds.Add(candidate);
                    leaders.Add(new Dictionary<string, object>
                    {
                        ["chara_id"] = candidate,
                        ["is_finished"] = false,
                        ["current_chapter"] = "1"
                    });
                }

                if (leaders.Count != quota)
                {
                    // 还差人：按职业逐个解析（章节覆盖 / 官方关卡表里的皮肤），再经别名表
                    // 换成剧情 leader 号 —— 每章一位主角，正好补齐缺的那几个。
                    foreach (int classId in manifest.GetClassIds(param.section_id))
                    {
                        if (leaders.Count >= quota)
                        {
                            break;
                        }

                        // 数据推得出人物就用它（过别名表换 leader 号）；推不出**不要**退回职业默认，
                        // 而是按职业从剧情 leader 候选表里补一个（才气学院篇缺的精灵那位是 500902）。
                        int storyChara = TryResolveStoryCharacter(manifest, param.section_id, classId);
                        int candidate = storyChara > 0
                            ? ResolveLeaderAlias(storyChara)
                            : GetClanLeaderCandidate(classId, leaderIds);
                        if (candidate <= 0 || leaderIds.Contains(candidate))
                        {
                            continue;
                        }

                        leaderIds.Add(candidate);
                        leaders.Add(new Dictionary<string, object>
                        {
                            ["chara_id"] = candidate,
                            ["is_finished"] = false,
                            ["current_chapter"] = "1"
                        });
                    }
                }

                if (leaders.Count != quota)
                {
                    Plugin.Logger.LogInfo(
                        $"[Offlinizer] Section {param.section_id} expects {quota} leader(s) but only " +
                        $"{leaders.Count} could be derived (official data is incomplete for this section).");
                }
            }

            Plugin.Logger.LogInfo(
                $"[Offlinizer] Created StoryLeaderSelectTask data: section={param.section_id}, leaders={leaders.Count}.");

            return CreateResponseEnvelope(new Dictionary<string, object>
            {
                ["leader_list"] = leaders,
                ["leader_count"] = leaders.Count
            });
        }

        /// <summary>
        /// 这一篇的 leader（主角）列表：官方关卡表里该 section 各章的 <c>chara_id</c>，
        /// 按文件顺序去重、跳过 0。实测与官方 leader_select 抓包一致
        /// （section 1 → 1..8；section 15 → 500701/500732/500704）。
        /// </summary>
        private static List<int> GetOfficialSectionLeaders(int sectionId)
        {
            List<int> leaders = new List<int>();
            try
            {
                EnsureStoryLeaderTable();
                lock (StorySectionLeaders)
                {
                    List<int> cached;
                    if (StorySectionLeaders.TryGetValue(sectionId, out cached))
                    {
                        return new List<int>(cached);
                    }
                }

                string directory = PathHelper.OfficialAIDataPath;
                if (string.IsNullOrEmpty(directory))
                {
                    return leaders;
                }

                // 抓包挖出来的那份最权威（<资源根>/story_ai/story_leaders.csv，两列
                // section_id,chara_id，由 _tools/harvest_story_leaders.py 从官方响应导出）；
                // 没有它才退回官方关卡表各章的 chara_id。
                string harvested = Path.Combine(directory, "story_leaders.csv");
                if (File.Exists(harvested))
                {
                    leaders = ReadSectionLeaders(harvested, sectionId, 0, 1);
                }

                if (leaders.Count == 0)
                {
                    leaders = ReadSectionLeaders(
                        Path.Combine(directory, "story_battles_official.csv"),
                        sectionId,
                        "section_id",
                        "chara_id");
                }

                lock (StorySectionLeaders)
                {
                    StorySectionLeaders[sectionId] = new List<int>(leaders);
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning(
                    $"[Offlinizer] Could not read the official leader list of section {sectionId}: {exception.Message}");
            }

            return leaders;
        }

        /// <summary>
        /// 从 CSV 里取某一篇的 leader 列表：按文件顺序去重。
        /// 列可以按名字给（<paramref name="sectionColumn"/> 是列名）也可以按序号给
        /// （传 int 时表示列下标，抓包导出的那份是固定两列）。
        /// </summary>
        private static List<int> ReadSectionLeaders(
            string path,
            int sectionId,
            object sectionColumn,
            object charaColumn)
        {
            List<int> leaders = new List<int>();
            if (!File.Exists(path))
            {
                return leaders;
            }

            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 2)
            {
                return leaders;
            }

            int sectionIndex;
            int charaIndex;
            if (sectionColumn is string sectionName && charaColumn is string charaName)
            {
                string[] header = lines[0].Split(',');
                sectionIndex = Array.IndexOf(header, sectionName);
                charaIndex = Array.IndexOf(header, charaName);
            }
            else
            {
                sectionIndex = Convert.ToInt32(sectionColumn, CultureInfo.InvariantCulture);
                charaIndex = Convert.ToInt32(charaColumn, CultureInfo.InvariantCulture);
            }

            if (sectionIndex < 0 || charaIndex < 0)
            {
                return leaders;
            }

            HashSet<int> seen = new HashSet<int>();
            for (int i = 1; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split(',');
                if (parts.Length <= Math.Max(sectionIndex, charaIndex))
                {
                    continue;
                }

                int rowSection;
                if (!int.TryParse(parts[sectionIndex].Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out rowSection) || rowSection != sectionId)
                {
                    continue;
                }

                int chara;
                if (!int.TryParse(parts[charaIndex].Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out chara) || chara <= 0)
                {
                    continue;
                }

                if (seen.Add(chara))
                {
                    leaders.Add(chara);
                }
            }

            return leaders;
        }

        private static readonly Dictionary<int, List<int>> StorySectionLeaders = new Dictionary<int, List<int>>();

        // ---------------------------------------------------------------- leader 补齐

        /// <summary>皮肤号 → 剧情 leader 号（<c>story_ai/leader_aliases.csv</c>）。</summary>
        private static readonly Dictionary<int, int> LeaderAliases = new Dictionary<int, int>();
        private static DateTime LeaderAliasWriteTime = DateTime.MinValue;
        private static bool LeaderAliasLoaded;

        private static int ResolveLeaderAlias(int skinId)
        {
            try
            {
                string directory = PathHelper.OfficialAIDataPath;
                if (string.IsNullOrEmpty(directory))
                {
                    return skinId;
                }

                string path = Path.Combine(directory, "leader_aliases.csv");
                if (File.Exists(path))
                {
                    DateTime writeTime = File.GetLastWriteTimeUtc(path);
                    if (!LeaderAliasLoaded || writeTime != LeaderAliasWriteTime)
                    {
                        Dictionary<int, int> aliases = new Dictionary<int, int>();
                        string[] lines = File.ReadAllLines(path);
                        for (int i = 1; i < lines.Length; i++)
                        {
                            string[] parts = lines[i].Split(',');
                            if (parts.Length < 2)
                            {
                                continue;
                            }

                            int skin;
                            int leader;
                            if (int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out skin) &&
                                int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out leader) &&
                                skin > 0 && leader > 0)
                            {
                                aliases[skin] = leader;
                            }
                        }

                        lock (LeaderAliases)
                        {
                            LeaderAliases.Clear();
                            foreach (KeyValuePair<int, int> pair in aliases)
                            {
                                LeaderAliases[pair.Key] = pair.Value;
                            }
                        }

                        LeaderAliasWriteTime = writeTime;
                        LeaderAliasLoaded = true;
                    }
                }
            }
            catch (Exception exception)
            {
                LeaderAliasLoaded = true;
                Plugin.Logger.LogWarning($"[Offlinizer] Could not read the leader alias table: {exception.Message}");
            }

            lock (LeaderAliases)
            {
                int leader;
                return LeaderAliases.TryGetValue(skinId, out leader) ? leader : skinId;
            }
        }

        /// <summary>这一篇的 leader 个数（离线数据里 StorySectionTask 的 released_chara_count）。</summary>
        private static readonly Dictionary<int, int> SectionLeaderQuota = new Dictionary<int, int>();
        private static DateTime SectionQuotaWriteTime = DateTime.MinValue;
        private static bool SectionQuotaLoaded;

        private static int GetSectionLeaderQuota(int sectionId)
        {
            try
            {
                string path = Path.Combine(Plugin.ModPath, "OfflinizedTasks", "StorySectionTask.json");
                if (!File.Exists(path))
                {
                    return 0;
                }

                DateTime writeTime = File.GetLastWriteTimeUtc(path);
                if (!SectionQuotaLoaded || writeTime != SectionQuotaWriteTime)
                {
                    Dictionary<int, int> quotas = new Dictionary<int, int>();
                    JsonData root = JsonMapper.ToObject(File.ReadAllText(path));
                    JsonData sections = root != null && root.Keys.Contains("data")
                        ? root["data"]
                        : null;
                    sections = sections != null && sections.Keys.Contains("world_list") ? sections["world_list"] : null;
                    if (sections != null)
                    {
                        foreach (string world in sections.Keys)
                        {
                            JsonData list = sections[world];
                            list = list != null && list.Keys.Contains("section_list") ? list["section_list"] : null;
                            if (list == null)
                            {
                                continue;
                            }

                            for (int i = 0; i < list.Count; i++)
                            {
                                JsonData entry = list[i];
                                if (entry == null || !entry.Keys.Contains("section_id"))
                                {
                                    continue;
                                }

                                int id = entry["section_id"].ToInt();
                                int count = entry.Keys.Contains("released_chara_count")
                                    ? entry["released_chara_count"].ToInt()
                                    : 0;
                                if (id > 0 && count > 0)
                                {
                                    quotas[id] = count;
                                }
                            }
                        }
                    }

                    lock (SectionLeaderQuota)
                    {
                        SectionLeaderQuota.Clear();
                        foreach (KeyValuePair<int, int> pair in quotas)
                        {
                            SectionLeaderQuota[pair.Key] = pair.Value;
                        }
                    }

                    SectionQuotaWriteTime = writeTime;
                    SectionQuotaLoaded = true;
                }
            }
            catch (Exception exception)
            {
                SectionQuotaLoaded = true;
                Plugin.Logger.LogWarning($"[Offlinizer] Could not read the section leader counts: {exception.Message}");
            }

            lock (SectionLeaderQuota)
            {
                int quota;
                return SectionLeaderQuota.TryGetValue(sectionId, out quota) ? quota : 0;
            }
        }

        /// <summary>
        /// 这一篇（以及紧随其后的「-最终章-」篇）出现过的主战者皮肤号，按出现顺序去重。
        /// 最终章每章就是一位主角的故事线，所以缺数据时用它反推主角名单最靠谱。
        /// </summary>
        private static List<int> GetSectionProtagonistSkins(int sectionId)
        {
            List<int> skins = new List<int>();
            try
            {
                string directory = PathHelper.OfficialAIDataPath;
                if (string.IsNullOrEmpty(directory))
                {
                    return skins;
                }

                string path = Path.Combine(directory, "story_battles_official.csv");
                if (!File.Exists(path))
                {
                    return skins;
                }

                string[] lines = File.ReadAllLines(path);
                if (lines.Length < 2)
                {
                    return skins;
                }

                string[] header = lines[0].Split(',');
                int sectionColumn = Array.IndexOf(header, "section_id");
                int skinColumn = Array.IndexOf(header, "skin_id_override");
                int deckSkinColumn = Array.IndexOf(header, "deck_skin_id_override");
                if (sectionColumn < 0)
                {
                    return skins;
                }

                HashSet<int> seen = new HashSet<int>();
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] parts = lines[i].Split(',');
                    if (parts.Length <= Math.Max(sectionColumn, Math.Max(skinColumn, deckSkinColumn)))
                    {
                        continue;
                    }

                    int rowSection;
                    if (!int.TryParse(parts[sectionColumn].Trim(), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out rowSection))
                    {
                        continue;
                    }

                    if (rowSection != sectionId && rowSection != sectionId + 1)
                    {
                        continue;
                    }

                    foreach (int column in new[] { skinColumn, deckSkinColumn })
                    {
                        if (column < 0)
                        {
                            continue;
                        }

                        int skin;
                        if (int.TryParse(parts[column].Trim(), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out skin) && skin > 0 && seen.Add(skin))
                        {
                            skins.Add(skin);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning(
                    $"[Offlinizer] Could not read the protagonist skins of section {sectionId}: {exception.Message}");
            }

            return skins;
        }

        /// <summary>
        /// 「角色剧情选择」界面该显示**这个 section 剧情里真正的那个人**，而不是"该职业的默认主战者"
        /// —— 后者会让所有篇章都显示成默认职业头像（每个职业一张，跟剧情人物无关）。
        ///
        /// 真实角色从章节数据推：同一 section、同一职业的章节 → story id →
        /// <c>Mods/StorySpecialBattles/&lt;story&gt;.json</c> 的 <c>_chapter.player_chara_id</c>
        /// （章节覆盖用的同一份数据；实测 424 个文件里 114 个带这个字段），
        /// 取到第一个非 0 的就是这个人。
        /// 推不出来时**原样回退**到职业默认角色，所以不会比现在更差。
        /// </summary>
        private static int ResolveLeaderCharacterId(StoryManifest manifest, int sectionId, int classId)
        {
            int resolved = TryResolveStoryCharacter(manifest, sectionId, classId);
            return resolved > 0 ? resolved : GetDefaultCharacterId(classId);
        }

        /// <summary>
        /// 只从**剧情数据**里解析这个职业在这一篇用的角色（章节覆盖 / 官方关卡表的皮肤），
        /// 取不到返回 0 —— 调用方据此决定是"用职业默认"还是"换个来源补"。
        /// </summary>
        private static int TryResolveStoryCharacter(StoryManifest manifest, int sectionId, int classId)
        {
            try
            {
                foreach (ScenarioChapter chapter in manifest.GetChapters(sectionId, classId))
                {
                    int storyId = CreateStoryId(chapter.SectionId, chapter.ClassId, chapter.ChapterId);
                    JsonData chapterOverride = TryGetChapterOverride(storyId);
                    if (chapterOverride != null && chapterOverride.Keys.Contains("player_chara_id"))
                    {
                        int player = chapterOverride["player_chara_id"].ToInt();
                        if (player > 0 && IsUsableLeader(player))
                        {
                            return player;
                        }

                        if (player > 0 && UnusableLeaderWarnings.Add(player))
                        {
                            Plugin.Logger.LogInfo(
                                $"[Offlinizer] Story character {player} is not usable as a leader " +
                                "(is_usable=false); the leader select list keeps the class default for it " +
                                "instead of showing a blank avatar.");
                        }
                    }

                    // 章节覆盖里没有这个人的篇章（多数篇章都没有），用官方关卡表兜住：
                    // skin_id_override 优先，没有才是 chara_id。
                    int official = GetOfficialStoryLeader(storyId);
                    if (official > 0 && IsUsableLeader(official))
                    {
                        return official;
                    }
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning(
                    $"[Offlinizer] Could not resolve the story leader of section {sectionId} for class {classId}: " +
                    $"{exception.Message}");
            }

            return 0;
        }

        /// <summary>职业 → 剧情 leader 候选（<c>story_ai/leader_candidates.csv</c>，由
        /// <c>_tools/export_leader_aliases.py</c> 导出）。官方数据不全时按职业补人用。</summary>
        private static readonly Dictionary<int, List<int>> ClanLeaderCandidates = new Dictionary<int, List<int>>();
        private static DateTime ClanCandidateWriteTime = DateTime.MinValue;
        private static bool ClanCandidateLoaded;

        /// <summary>
        /// 这个职业的剧情 leader 候选里，挑一个还没用过的 —— 取与已选 leader 号**最接近**的
        /// （同一篇的主角号是连号的：才气学院篇是 500901..500904）。
        /// </summary>
        private static int GetClanLeaderCandidate(int classId, List<int> chosen)
        {
            try
            {
                string directory = PathHelper.OfficialAIDataPath;
                if (string.IsNullOrEmpty(directory))
                {
                    return 0;
                }

                string path = Path.Combine(directory, "leader_candidates.csv");
                if (File.Exists(path))
                {
                    DateTime writeTime = File.GetLastWriteTimeUtc(path);
                    if (!ClanCandidateLoaded || writeTime != ClanCandidateWriteTime)
                    {
                        Dictionary<int, List<int>> table = new Dictionary<int, List<int>>();
                        string[] lines = File.ReadAllLines(path);
                        for (int i = 1; i < lines.Length; i++)
                        {
                            string[] parts = lines[i].Split(',');
                            if (parts.Length < 2)
                            {
                                continue;
                            }

                            int clan;
                            int leader;
                            if (int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out clan) &&
                                int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out leader) &&
                                clan > 0 && leader > 0)
                            {
                                List<int> list;
                                if (!table.TryGetValue(clan, out list))
                                {
                                    list = new List<int>();
                                    table[clan] = list;
                                }

                                list.Add(leader);
                            }
                        }

                        lock (ClanLeaderCandidates)
                        {
                            ClanLeaderCandidates.Clear();
                            foreach (KeyValuePair<int, List<int>> pair in table)
                            {
                                ClanLeaderCandidates[pair.Key] = pair.Value;
                            }
                        }

                        ClanCandidateWriteTime = writeTime;
                        ClanCandidateLoaded = true;
                    }
                }
            }
            catch (Exception exception)
            {
                ClanCandidateLoaded = true;
                Plugin.Logger.LogWarning($"[Offlinizer] Could not read the leader candidates: {exception.Message}");
            }

            List<int> candidates;
            lock (ClanLeaderCandidates)
            {
                if (!ClanLeaderCandidates.TryGetValue(classId, out candidates))
                {
                    return 0;
                }

                candidates = new List<int>(candidates);
            }

            int best = 0;
            long bestDistance = long.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                int candidate = candidates[i];
                if (chosen.Contains(candidate))
                {
                    continue;
                }

                long distance = 0;
                if (chosen.Count > 0)
                {
                    distance = long.MaxValue;
                    for (int j = 0; j < chosen.Count; j++)
                    {
                        distance = Math.Min(distance, Math.Abs((long)candidate - chosen[j]));
                    }
                }

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            return best;
        }
        private static readonly HashSet<int> UnusableLeaderWarnings = new HashSet<int>();

        /// <summary>
        /// story(local_story_id) → 该章**玩家主战者**（官方关卡表
        /// <c>&lt;资源根&gt;/story_ai/story_battles_official.csv</c> 的 skin_id_override，
        /// 没有时退回 chara_id）。
        ///
        /// 只靠 <c>Mods/StorySpecialBattles/*.json</c> 的 <c>_chapter.player_chara_id</c>
        /// 只能覆盖一部分篇章（实测 424 个文件里 114 个带这个字段），剩下的篇章就会退回
        /// "职业默认头像"。官方关卡表每一章都有玩家主战者，用它兜住这些篇章。
        /// 优先级：章节覆盖（玩家自己/工具写的那份） &gt; 官方关卡表 &gt; 职业默认角色。
        /// </summary>
        private static readonly Dictionary<int, int> StoryLeaderCharaIds = new Dictionary<int, int>();
        private static DateTime StoryLeaderTableWriteTime = DateTime.MinValue;
        private static bool StoryLeaderTableLoaded;

        private static void EnsureStoryLeaderTable()
        {
            try
            {
                string directory = PathHelper.OfficialAIDataPath;
                if (string.IsNullOrEmpty(directory))
                {
                    return;
                }

                string path = Path.Combine(directory, "story_battles_official.csv");
                if (!File.Exists(path))
                {
                    return;
                }

                DateTime writeTime = File.GetLastWriteTimeUtc(path);
                if (StoryLeaderTableLoaded && writeTime == StoryLeaderTableWriteTime)
                {
                    return;
                }

                string[] lines = File.ReadAllLines(path);
                if (lines.Length < 2)
                {
                    return;
                }

                string[] header = lines[0].Split(',');
                int localStoryColumn = Array.IndexOf(header, "local_story_id");
                int charaColumn = Array.IndexOf(header, "chara_id");
                int skinColumn = Array.IndexOf(header, "skin_id_override");
                if (localStoryColumn < 0 || charaColumn < 0)
                {
                    return;
                }

                Dictionary<int, int> table = new Dictionary<int, int>();
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] parts = lines[i].Split(',');
                    if (parts.Length <= Math.Max(localStoryColumn, Math.Max(charaColumn, skinColumn)))
                    {
                        continue;
                    }

                    int localStoryId;
                    if (!int.TryParse(parts[localStoryColumn].Trim(), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out localStoryId) || localStoryId <= 0)
                    {
                        continue;
                    }

                    int skin = 0;
                    if (skinColumn >= 0)
                    {
                        int.TryParse(parts[skinColumn].Trim(), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out skin);
                    }

                    int chara = 0;
                    int.TryParse(parts[charaColumn].Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out chara);

                    int leader = skin > 0 ? skin : chara;
                    if (leader > 0)
                    {
                        table[localStoryId] = leader;
                    }
                }

                lock (StoryLeaderCharaIds)
                {
                    StoryLeaderCharaIds.Clear();
                    foreach (KeyValuePair<int, int> pair in table)
                    {
                        StoryLeaderCharaIds[pair.Key] = pair.Value;
                    }
                }

                StoryLeaderTableWriteTime = writeTime;
                StoryLeaderTableLoaded = true;
                Plugin.Logger.LogInfo(
                    $"[Offlinizer] Official story leader table loaded: {table.Count} chapter(s).");
            }
            catch (Exception exception)
            {
                StoryLeaderTableLoaded = true;
                Plugin.Logger.LogWarning($"[Offlinizer] Could not read the official story leader table: {exception.Message}");
            }
        }

        private static int GetOfficialStoryLeader(int storyId)
        {
            EnsureStoryLeaderTable();
            int charaId;
            lock (StoryLeaderCharaIds)
            {
                return StoryLeaderCharaIds.TryGetValue(storyId, out charaId) ? charaId : 0;
            }
        }

        /// <summary>
        /// 「这个剧情角色能不能当主战者显示」——读
        /// <c>&lt;资源根&gt;/story_ai/class_chara_usable.csv</c>（两列小表，
        /// 由 <c>_tools/export_class_chara_master.py</c> 从 master_class_chara_master 的
        /// <c>is_usable</c> 列导出）。
        ///
        /// 角色选择界面拿到一个 <c>is_usable=false</c> 的角色（剧情里的 NPC 专属角色）时，
        /// 那一个头像会是**空白** —— 就是"头像消失"。所以这种角色不拿来做 leader，回退到
        /// 职业默认头像（宁可显示职业头像，也不要空白）。
        ///
        /// 表不存在时一律当作可用 —— 行为与加这个检查之前完全相同，不会因为缺文件变差。
        /// </summary>
        private static bool IsUsableLeader(int charaId)
        {
            EnsureLeaderCharaUsableTable();
            return !UnusableLeaderCharas.Contains(charaId);
        }

        private static readonly HashSet<int> UnusableLeaderCharas = new HashSet<int>();
        private static DateTime LeaderCharaUsableWriteTime = DateTime.MinValue;
        private static bool LeaderCharaUsableLoaded;

        private static void EnsureLeaderCharaUsableTable()
        {
            try
            {
                string directory = PathHelper.OfficialAIDataPath;
                if (string.IsNullOrEmpty(directory))
                {
                    return;
                }

                string path = Path.Combine(directory, "class_chara_usable.csv");
                if (!File.Exists(path))
                {
                    if (!LeaderCharaUsableLoaded)
                    {
                        LeaderCharaUsableLoaded = true;
                        Plugin.Logger.LogInfo(
                            "[Offlinizer] No story_ai/class_chara_usable.csv; every story character counts " +
                            "as a usable leader.");
                    }

                    return;
                }

                DateTime writeTime = File.GetLastWriteTimeUtc(path);
                if (LeaderCharaUsableLoaded && writeTime == LeaderCharaUsableWriteTime)
                {
                    return;
                }

                HashSet<int> unusable = new HashSet<int>();
                string[] lines = File.ReadAllLines(path);
                for (int i = 1; i < lines.Length; i++)      // 跳过表头
                {
                    string[] parts = lines[i].Split(',');
                    if (parts.Length < 2)
                    {
                        continue;
                    }

                    int charaId;
                    if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out charaId) ||
                        charaId <= 0)
                    {
                        continue;
                    }

                    if (string.Equals(parts[1].Trim(), "false", StringComparison.OrdinalIgnoreCase))
                    {
                        unusable.Add(charaId);
                    }
                }

                lock (UnusableLeaderCharas)
                {
                    UnusableLeaderCharas.Clear();
                    foreach (int charaId in unusable)
                    {
                        UnusableLeaderCharas.Add(charaId);
                    }
                }

                LeaderCharaUsableWriteTime = writeTime;
                LeaderCharaUsableLoaded = true;
                Plugin.Logger.LogInfo(
                    $"[Offlinizer] Leader usability table loaded: {unusable.Count} character(s) cannot be " +
                    "shown as a story leader.");
            }
            catch (Exception exception)
            {
                LeaderCharaUsableLoaded = true;
                Plugin.Logger.LogWarning($"[Offlinizer] Could not read the leader usability table: {exception.Message}");
            }
        }

        /// <summary>
        /// StoryStartTask 在官方服务器上是「这一章的特殊战斗覆盖表」：开局先后手、双方初始 PP/生命、
        /// 双方附加技能、战斗日志 id、特效覆盖、跳过结算等（客户端 StoryStartTask.Parse 读的
        /// special_battle_setting 字段）。这些值是服务端策划配的，客户端与资源包里都没有，
        /// 所以离线时只能返回空表 —— 带特殊机制的剧情战斗就会退化成普通 AI 对战
        /// （敌方不按脚本出牌、关键效果不触发）。
        ///
        /// 现在支持用本地文件补上：把官方那一份 special_battle_setting 的内容
        /// （一个 JSON 对象，字段名与官方一致，例如 player_start_pp / enemy_attach_skill / id …）
        /// 放到 <c>Mods/StorySpecialBattles/&lt;story_id&gt;.json</c>，这里就会带上它。
        /// </summary>
        private static object CreateStoryStartResponse(StoryStartTask.StoryStartTaskParam param)
        {
            Dictionary<string, object> chapterOverride = null;

            foreach (int storyId in param?.story_ids ?? Array.Empty<int>())
            {
                RememberStoryId(storyId);
                string path = Path.Combine(
                    Plugin.ModPath,
                    "StorySpecialBattles",
                    storyId.ToString(CultureInfo.InvariantCulture) + ".json");

                if (!File.Exists(path))
                {
                    if (MissingSpecialBattleSettingIds.Add(storyId))
                    {
                        Plugin.Logger.LogInfo(
                            $"[Offlinizer] Story {storyId} has no local special battle setting; " +
                            $"the battle runs without the server-defined gimmick. Optional: put the " +
                            $"official special_battle_setting object in " +
                            $"Mods/StorySpecialBattles/{storyId}.json.");
                    }

                    continue;
                }

                try
                {
                    // 同一份文件里还允许带 "_chapter"（章节级敌我配置，离线时用来修正
                    // 敌方主战者/职业，避免 AI 表情、语音、皮肤全错），那段不能塞进 special_battle_setting。
                    Dictionary<string, object> setting =
                        JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(path));
                    setting?.Remove("_chapter");

                    // 只有 _chapter、没有服务端正文时，**必须什么都不返回**，让 data["0"] 保持空对象。
                    // 客户端 StoryStartTask.Parse 的判断是：
                    //     if (jsonData.Count == 0) { ResetStorySpecialBattleResultSkipFlag(); return; }
                    // 一旦塞进 {"special_battle_setting": {}}，jsonData.Count 就非 0，客户端会继续
                    // 直接索引 player_first_turn / id / result_skip / player_attach_skill 等键，
                    // 抛 KeyNotFoundException，这一章直接进不去战斗
                    // （Player.log: "Error processing local data for StoryStartTask"）。
                    if (setting == null || setting.Count == 0)
                    {
                        continue;
                    }

                    EnsureRequiredSettingKeys(setting);
                    chapterOverride = new Dictionary<string, object>
                    {
                        ["special_battle_setting"] = setting
                    };
                    Plugin.Logger.LogInfo(
                        $"[Offlinizer] Applied local special battle setting " +
                        $"'{Path.GetFileName(path)}' for story {storyId}.");
                    break;
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning(
                        $"[Offlinizer] Ignoring invalid special battle setting " +
                        $"'{Path.GetFileName(path)}': {ex.Message}");
                }
            }

            // StoryStartTask 读 data 里的第一个值（下标 0），所以这里必须是对象而不是数组，
            // 而且 0 里如果是空对象，客户端会当成"这章没有服务端特殊战斗覆盖"。
            return CreateResponseEnvelope(new Dictionary<string, object>
            {
                ["0"] = chapterOverride ?? new Dictionary<string, object>()
            });
        }

        private static readonly HashSet<int> MissingSpecialBattleSettingIds = new HashSet<int>();

        /// <summary>
        /// StoryStartTask.Parse 对下面这些键用的是直接索引（不是 GetValueOrDefault），
        /// 少任何一个都会抛 KeyNotFoundException 让整章进不去战斗。官方正文里一直都齐全，
        /// 这里只是兜底，本地手写或旧版本存下来的正文缺字段时也还能开打。
        /// </summary>
        private static readonly string[] RequiredSettingIntKeys =
        {
            "player_first_turn", "player_start_pp", "enemy_start_pp",
            "player_start_life", "enemy_start_life",
            "result_skip", "vs_effect_override", "class_destroy_effect_override"
        };

        private static readonly string[] RequiredSettingStringKeys =
        {
            "id", "player_attach_skill", "enemy_attach_skill",
            "id_override_in_battle_log", "banish_effect_override"
        };

        private static void EnsureRequiredSettingKeys(Dictionary<string, object> setting)
        {
            if (setting == null)
            {
                return;
            }

            foreach (string key in RequiredSettingIntKeys)
            {
                if (!setting.ContainsKey(key) || setting[key] == null)
                {
                    setting[key] = 0;
                }
            }

            foreach (string key in RequiredSettingStringKeys)
            {
                if (!setting.ContainsKey(key) || setting[key] == null)
                {
                    setting[key] = string.Empty;
                }
            }
        }

        private static object CreateStoryDeckListResponse()
        {
            List<object> unlimitedDecks = new List<object>();
            if (Directory.Exists(Plugin.UnlimitedDeckPath))
            {
                foreach (string path in Directory.GetFiles(Plugin.UnlimitedDeckPath, "*.json")
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        unlimitedDecks.Add(JsonConvert.DeserializeObject<object>(File.ReadAllText(path)));
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger.LogWarning(
                            $"[Offlinizer] Ignoring invalid story deck file '{Path.GetFileName(path)}': {ex.Message}");
                    }
                }
            }

            Plugin.Logger.LogInfo($"[Offlinizer] Created StoryDeckListTask data with {unlimitedDecks.Count} unlimited decks.");
            return CreateResponseEnvelope(new Dictionary<string, object>
            {
                ["user_deck_rotation"] = Array.Empty<object>(),
                ["user_deck_unlimited"] = unlimitedDecks,
                ["user_deck_my_rotation"] = Array.Empty<object>(),
                ["maintenance_card_list"] = Array.Empty<int>()
            });
        }

        private static object CreateStoryFinishResponse()
        {
            GetCurrentClassProgress(out int classLevel, out int classExperience);
            return CreateResponseEnvelope(new Dictionary<string, object>
            {
                ["get_class_experience"] = 0,
                ["class_experience"] = classExperience,
                ["class_level"] = classLevel,
                ["achieved_info"] = Array.Empty<object>(),
                ["story_reward_list"] = Array.Empty<object>(),
                ["reward_list"] = Array.Empty<object>(),
                ["quest"] = new Dictionary<string, object> { ["is_display_badge"] = false },
                ["story_notification"] = new Dictionary<string, object> { ["is_display_badge"] = false },
                ["basic_puzzle"] = new Dictionary<string, object> { ["is_display_badge"] = false },
                ["shop_notification"] = new Dictionary<string, object>
                {
                    ["card_pack"] = false,
                    ["build_deck"] = false,
                    ["sleeve"] = false,
                    ["leader_skin"] = false
                },
                ["receive_friend_apply_count"] = 0,
                ["competition_info"] = new Dictionary<string, object> { ["is_competition_period"] = false },
                ["gathering_info"] = new Dictionary<string, object> { ["has_invite"] = 0 },
                ["is_available_colosseum_free_entry"] = false
            });
        }

        private static void GetCurrentClassProgress(out int classLevel, out int classExperience)
        {
            classLevel = 1;
            classExperience = 0;
            try
            {
                DataMgr dataMgr = GameMgr.GetIns().GetDataMgr();
                int classId = dataMgr.GetPlayerClassId();
                ClassCharaPrm classData = classId > 0 ? dataMgr.GetClassPrm(classId) : null;
                if (classData != null)
                {
                    classLevel = Math.Max(1, classData.GetClassCharaLv());
                    classExperience = Math.Max(0, classData.GetClassCharaExp());
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Offlinizer] Could not read story class progress: {ex.Message}");
            }
        }

        private static object CreateResponseEnvelope(object data)
        {
            return new Dictionary<string, object>
            {
                ["data_headers"] = new Dictionary<string, object>
                {
                    ["short_udid"] = 0,
                    ["viewer_id"] = 0,
                    ["sid"] = string.Empty,
                    ["servertime"] = 0L,
                    ["result_code"] = 1
                },
                ["data"] = data
            };
        }

        private static Dictionary<string, object> CreateChapterResponse(
            ScenarioChapter chapter,
            string nextChapterId,
            int selectedCharaId,
            int displayIndex)
        {
            int chapterCharaId = selectedCharaId;
            if (chapterCharaId == 0 && chapter.ClassId != 0)
            {
                chapterCharaId = GetDefaultCharacterId(chapter.ClassId);
            }

            bool hasBattle = chapter.HasSecondHalf && chapter.BattleClassIds.Count > 0;
            StoryAISettingData aiSetting = hasBattle ? ResolveOriginalStoryAiSetting(chapter) : null;
            int battleAiId = aiSetting?.EnemyAiId ?? 0;
            int enemyClassId = hasBattle ? ResolveOriginalEnemyClassId(aiSetting) : 0;
            int enemyCharaId = hasBattle
                ? ResolveOriginalEnemyCharaId(aiSetting, enemyClassId)
                : 0;
            // skin_id_override stays 0 on purpose.  BattleSettingData.GetPlayerCharaId only
            // falls back to the chapter's chara_id while the override is 0; a non-zero value
            // replaced the story protagonist with the plain class default, which is what made
            // the player leader wrong in offline story battles.
            //
            // *_emotion_override 则相反：它是**必须**给对的。官方把剧情用的表情/动作/台词做成
            // emote_chara_<皮肤>_<篇章变体>.csv，override 为 0 会去读通用占位表（动作空、文本缺失，
            // 界面显示原始 id）。变体号从官方表自身推导（见 StoryAiEmoteVariants）。
            int playerEmotionVariation = StoryAiEmoteVariants.ResolveVariation(chapterCharaId, chapter.SectionId);
            int enemyEmotionVariation = hasBattle
                ? StoryAiEmoteVariants.ResolveVariation(enemyCharaId, chapter.SectionId)
                : 0;
            List<Dictionary<string, object>> battleSettings = hasBattle
                ? chapter.BattleClassIds.Select(classId => new Dictionary<string, object>
                {
                    ["deck_class_id"] = classId,
                    ["deck_skin_id_override"] = 0,
                    ["skin_id_override"] = 0,
                    ["player_emotion_override"] = playerEmotionVariation,
                    ["enemy_emotion_override"] = enemyEmotionVariation,
                    ["battle3dfield_id_override"] = 0,
                    ["bgm_id_override"] = "0"
                }).ToList()
                : new List<Dictionary<string, object>>();

            int column = displayIndex % 6;
            int mapRow = displayIndex / 6;
            int storyId = CreateStoryId(chapter.SectionId, chapter.ClassId, chapter.ChapterId);
            RememberStoryId(storyId);

            // 同一篇章有多张官方变体表时（第 9 篇那样），允许用 _chapter_variations.csv 精确指定。
            StoryAiEmoteVariants.ApplyChapterPin(storyId, battleSettings);

            var response = new Dictionary<string, object>
            {
                ["story_id"] = storyId,
                ["section_id"] = chapter.SectionId,
                ["chara_id"] = chapterCharaId,
                ["chapter_id"] = chapter.ChapterId,
                ["next_chapter_id"] = nextChapterId,
                ["sub_chapters"] = chapter.SubChapterIds.Select(subChapterId => new Dictionary<string, object>
                {
                    ["story_id"] = CreateStoryId(chapter.SectionId, chapter.ClassId, chapter.ChapterId) + subChapterId,
                    ["sub_chapter_id"] = subChapterId,
                    ["is_finish"] = false,
                    ["is_maintenance_chapter"] = false
                }).ToList(),
                ["battle_exists"] = hasBattle,
                ["show_subtitles"] = 1,
                ["is_maintenance_chapter"] = false,
                ["is_released"] = true,
                ["is_lock"] = false,
                ["unlock_text"] = string.Empty,
                ["is_finish"] = false,
                ["is_skipped"] = false,
                ["story_reward"] = Array.Empty<object>(),
                ["chapter_clear_text_id"] = string.Empty,
                ["is_skip_enabled"] = false,
                ["enemy_chara_id"] = enemyCharaId,
                ["enemy_class"] = enemyClassId,
                ["enemy_ai_id"] = hasBattle ? battleAiId : 0,
                ["battle3dfield_id"] = hasBattle ? 1 : 0,
                ["bgm_id"] = "0",
                ["battle_settings"] = battleSettings,
                ["x_coordinate"] = -500 + column * 200,
                ["y_coordinate"] = 260 - mapRow * 150,
                ["show_coordinate"] = 1,
                ["is_camera_ movable"] = 1,
                ["bg_file_name"] = DefaultChapterButtonBackgroundId.ToString(CultureInfo.InvariantCulture),
                ["chapter_effect_path"] = string.Empty,
                ["selection_display_position"] = GetSelectionDisplayPosition(chapter.ChapterId),
                ["selection_text_id"] = string.Empty,
                ["required_chapter_id"] = string.Empty,
                ["is_released_another_end"] = false,
                ["is_play_another_end_appearance_animation"] = false
            };

            ApplyChapterOverride(storyId, response);
            return response;
        }

        private static readonly Dictionary<int, JsonData> ChapterOverrides = new Dictionary<int, JsonData>();
        private static readonly Dictionary<int, DateTime> ChapterOverrideWriteTimes = new Dictionary<int, DateTime>();

        /// <summary>
        /// 读 <c>Mods/StorySpecialBattles/&lt;story_id&gt;.json</c> 里可选的 <c>_chapter</c> 段。
        /// 用来修正离线生成数据里猜错的章节级字段（敌我主战者、职业、敌方 AI、战场、BGM）。
        ///
        /// 按文件写入时间缓存：改完 json 重进章节就生效，不用重启游戏（战场/BGM 这类值
        /// 本来就要靠试）。
        /// </summary>
        private static JsonData TryGetChapterOverride(int storyId)
        {
            try
            {
                string path = Path.Combine(
                    Plugin.ModPath,
                    "StorySpecialBattles",
                    storyId.ToString(CultureInfo.InvariantCulture) + ".json");
                if (!File.Exists(path))
                {
                    return null;
                }

                DateTime writeTimeUtc = File.GetLastWriteTimeUtc(path);
                lock (ChapterOverrides)
                {
                    if (ChapterOverrides.TryGetValue(storyId, out JsonData cached) &&
                        ChapterOverrideWriteTimes.TryGetValue(storyId, out DateTime cachedTime) &&
                        cachedTime == writeTimeUtc)
                    {
                        return cached;
                    }
                }

                JsonData data = JsonMapper.ToObject(File.ReadAllText(path));
                JsonData chapter = data != null && data.Keys.Contains("_chapter")
                    ? data["_chapter"]
                    : null;

                lock (ChapterOverrides)
                {
                    ChapterOverrideWriteTimes[storyId] = writeTimeUtc;
                    if (chapter != null)
                    {
                        ChapterOverrides[storyId] = chapter;
                    }
                    else
                    {
                        ChapterOverrides.Remove(storyId);
                    }
                }

                return chapter;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[Offlinizer] Could not read chapter override for story {storyId}: {ex.Message}");
                return null;
            }
        }

        private static void ApplyChapterOverride(int storyId, Dictionary<string, object> response)
        {
            JsonData o = TryGetChapterOverride(storyId);
            if (o == null)
            {
                return;
            }

            // 章节覆盖只是「锦上添花」：master 的某些表（尤其 AIDeckDic）要等 AI master 加载
            // 完才有，而这条路径在剧情列表阶段就会跑到。这里整体兜住，任何意外状态都只记警告，
            // 绝不能把 StoryInfoTask 的本地数据生成一起带崩（崩了就会去读不存在的 json）。
            try
            {
                ApplyChapterOverrideCore(storyId, o, response);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[Offlinizer] Story {storyId}: applying the local chapter override failed and was " +
                    $"skipped (chapter data kept as generated): {ex}");
            }
        }

        private static void ApplyChapterOverrideCore(
            int storyId,
            JsonData o,
            Dictionary<string, object> response)
        {
            string[] keys =
            {
                "player_chara_id|chara_id",
                "enemy_chara_id|enemy_chara_id",
                "enemy_ai_id|enemy_ai_id",
                "battle3dfield_id|battle3dfield_id"
            };

            foreach (string pair in keys)
            {
                string[] parts = pair.Split('|');
                if (!o.Keys.Contains(parts[0]) || o[parts[0]] == null)
                {
                    continue;
                }

                response[parts[1]] = ReadOverrideInt(o, parts[0]);
            }

            string bgmId = ReadOverrideString(o, "bgm_id");
            if (!string.IsNullOrEmpty(bgmId))
            {
                response["bgm_id"] = bgmId;
            }

            // 后路：一般不需要手填（变体号按篇章自动推导），但万一某章的官方表和篇章对不上，
            // 可以在 _chapter 里用 player_emotion_variation / enemy_emotion_variation 钉住。
            ApplyEmotionVariationOverride(o, response, "player_emotion_variation", "player_emotion_override");
            ApplyEmotionVariationOverride(o, response, "enemy_emotion_variation", "enemy_emotion_override");

            // 国服捕获里带的是敌方 AI 的 _id_（ai_deck_id / ai_style_id / ai_emote_id），
            // 而插件给客户端的是 enemy_ai_id。用 master 的 StoryAISettingData 反查匹配的那一条，
            // 这样 AI 的牌组、表情集、逻辑等级会一起对上（猜的话敌方就会不打牌/表情乱）。
            if (!o.Keys.Contains("enemy_ai_id") && o.Keys.Contains("enemy_deck_id"))
            {
                StoryAISettingData matched = ResolveAiSettingByCapture(o);
                if (matched != null)
                {
                    response["enemy_ai_id"] = matched.EnemyAiId;

                    // 顺带确认本地到底有没有这个 AI 的牌组数据（缺了的话 AI 就没牌可出）。
                    // 注意 AIDeckDic 在 AI master 加载完之前是 null，这里必须单独判断，
                    // 否则剧情列表阶段进这条路径会直接 NRE。
                    string deckName = Data.Master?.AIDeckFileNameList?.GetFileName(matched.DeckId);
                    var deckDic = Data.Master?.AIDeckDic;
                    string deckState;
                    if (string.IsNullOrEmpty(deckName))
                    {
                        deckState = "deck-file-name-unknown";
                    }
                    else if (deckDic == null)
                    {
                        deckState = "deck-master-not-loaded";
                    }
                    else
                    {
                        deckState = deckDic.ContainsKey("ai/" + deckName) ? "loaded" : "MISSING";
                    }

                    Plugin.Logger.LogInfo(
                        $"[Offlinizer] Matched official story AI from capture: " +
                        $"ai_id={matched.EnemyAiId}, deck={matched.DeckId}, style={matched.StyleId}, " +
                        $"emote={matched.EmoteId}, logic={matched.LogicLevel}, " +
                        $"deckFile='{deckName}', deckData={deckState}.");
                }
                else
                {
                    Plugin.Logger.LogWarning(
                        "[Offlinizer] No StoryAISettingData matched the captured enemy AI " +
                        "(deck/style/emote); keeping the guessed AI.");
                }
            }

            // 敌方职业的权威顺序：**本地覆盖文件里的 enemy_class** > 敌方主战者自身的职业 > AI 牌组主职业。
            // 本地覆盖里的 enemy_class 现在由官方 main_story/info 表生成，它就是服务端下发的
            // story_master_list.enemy_class，客户端直接拿来当 BattleSettingBaseData.EnemyClassId，
            // 也就是线上行为本身。剧情里大量复用同一个杂兵模型（class_chara_master 里固定是 1 职业）
            // 去打别的职业，所以旧顺序（模型职业优先）反而会把官方值改错。
            TryReadInt(response, "enemy_ai_id", out int enemyAiId);
            TryReadInt(response, "enemy_chara_id", out int enemyCharaId);
            int capturedClassId = ReadOverrideInt(o, "enemy_class");
            int charaClassId = ResolveCharacterClassId(enemyCharaId);
            int deckClassId = charaClassId > 0 ? 0 : ResolveEnemyClassFromAiDeck(enemyAiId);
            int finalClassId = capturedClassId > 0
                ? capturedClassId
                : charaClassId > 0
                    ? charaClassId
                    : deckClassId;

            if (finalClassId > 0)
            {
                response["enemy_class"] = finalClassId;
            }

            if (capturedClassId > 0 && charaClassId > 0 && capturedClassId != charaClassId)
            {
                Plugin.Logger.LogInfo(
                    $"[Offlinizer] Story {storyId}: the official enemy_class={capturedClassId} overrides the " +
                    $"model's own class {charaClassId} (enemy_chara_id={enemyCharaId}); that is what the " +
                    $"server sends for this chapter.");
            }

            LogStoryBattleDiagnostics(storyId, response, charaClassId, deckClassId);
        }

        /// <summary>
        /// 在 _chapter 里显式指定表情表变体号时，把它写进每个 battle_settings 条目。
        /// </summary>
        private static void ApplyEmotionVariationOverride(
            JsonData o,
            Dictionary<string, object> response,
            string jsonKey,
            string settingKey)
        {
            if (o == null || response == null || !o.Keys.Contains(jsonKey) || o[jsonKey] == null)
            {
                return;
            }

            if (!response.TryGetValue("battle_settings", out object raw) ||
                !(raw is IEnumerable<Dictionary<string, object>> settings))
            {
                return;
            }

            int variation = ReadOverrideInt(o, jsonKey);
            foreach (Dictionary<string, object> setting in settings)
            {
                if (setting != null)
                {
                    setting[settingKey] = variation;
                }
            }

            Plugin.Logger.LogInfo(
                $"[Offlinizer] {jsonKey}={variation} pinned by the local chapter override.");
        }

        /// <summary>字典里读一个 int；键不存在、值为 null 或类型不对都当作 0（不抛异常）。</summary>
        private static bool TryReadInt(Dictionary<string, object> data, string key, out int value)
        {
            value = 0;
            if (data == null || !data.TryGetValue(key, out object raw) || raw == null)
            {
                return false;
            }

            try
            {
                value = Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>覆盖文件里读一个 int；缺失或 null 都当作 0（不抛异常）。</summary>
        private static int ReadOverrideInt(JsonData node, string key)
        {
            if (node == null || !node.Keys.Contains(key) || node[key] == null)
            {
                return 0;
            }

            try
            {
                return (int)node[key];
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static string ReadOverrideString(JsonData node, string key)
        {
            if (node == null || !node.Keys.Contains(key) || node[key] == null)
            {
                return null;
            }

            return node[key].ToString();
        }

        /// <summary>主战者号 → 职业（class_chara_master 表）。回放修主战者那一路也要用，所以放开到 internal。</summary>
        internal static int ResolveCharacterClassId(int charaId)
        {
            if (charaId <= 0)
            {
                return 0;
            }

            try
            {
                ClassCharacterMasterData chara = GameMgr.GetIns().GetDataMgr().GetCharaPrmByCharaId(charaId);
                return chara != null && chara.class_id >= 1 && chara.class_id <= 8 ? chara.class_id : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static readonly Dictionary<int, int> EnemyClassByAiId = new Dictionary<int, int>();

        /// <summary>
        /// 取敌方 AI 牌组的主职业（兜底用；剧情 AI 常是混职业牌组，判定可能不准）。
        /// </summary>
        private static int ResolveEnemyClassFromAiDeck(int enemyAiId)
        {
            if (enemyAiId <= 0)
            {
                return 0;
            }

            lock (EnemyClassByAiId)
            {
                if (EnemyClassByAiId.TryGetValue(enemyAiId, out int cached))
                {
                    return cached;
                }
            }

            int classId = 0;
            try
            {
                StoryAISettingData setting = Data.Master?.StoryAISettingList?.GetSettingData(enemyAiId);
                if (setting != null)
                {
                    string deckName = Data.Master?.AIDeckFileNameList?.GetFileName(setting.DeckId);
                    string deckKey = "ai/" + deckName;
                    if (!string.IsNullOrEmpty(deckName) &&
                        Data.Master.AIDeckDic != null &&
                        Data.Master.AIDeckDic.TryGetValue(deckKey, out AICardDataAssetSet deck) &&
                        deck?.Set != null)
                    {
                        classId = deck.Set
                            .Where(card => card != null)
                            .GroupBy(card => GetCardClassId(card.CardID))
                            .Where(group => group.Key >= 1 && group.Key <= 8)
                            .OrderByDescending(group => group.Sum(card => Math.Max(1, card.CardNum)))
                            .Select(group => group.Key)
                            .FirstOrDefault();
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[Offlinizer] Could not read the deck class of story AI {enemyAiId}: {ex.Message}");
            }

            // Only cache a real answer: the deck master may still be loading while the
            // chapter list is built, and a cached 0 would stick for the whole session.
            if (classId > 0)
            {
                lock (EnemyClassByAiId)
                {
                    EnemyClassByAiId[enemyAiId] = classId;
                }
            }

            return classId;
        }

        /// <summary>
        /// 打完这一条日志，剧情对战的三个数据来源（章节覆盖 / master 的 StoryAISettingList /
        /// 本地 AIData 资源）就全部可核对，出问题时不用再猜。
        /// </summary>
        private static void LogStoryBattleDiagnostics(
            int storyId,
            Dictionary<string, object> response,
            int charaClassId = 0,
            int deckClassId = 0)
        {
            TryReadInt(response, "chara_id", out int playerCharaId);
            TryReadInt(response, "enemy_chara_id", out int enemyCharaId);
            TryReadInt(response, "enemy_class", out int enemyClassId);
            TryReadInt(response, "enemy_ai_id", out int enemyAiId);
            TryReadInt(response, "battle3dfield_id", out int fieldId);
            string bgmId = response.TryGetValue("bgm_id", out object rawBgm) ? rawBgm as string : null;

            Plugin.Logger.LogInfo(
                $"[Offlinizer] Applied local chapter override for story {storyId}: " +
                $"player={playerCharaId} ({DescribeCharacter(playerCharaId)}), " +
                $"enemy={enemyCharaId} ({DescribeCharacter(enemyCharaId)}), " +
                $"enemyClass={enemyClassId} (charaClass={charaClassId}, deckClass={deckClassId}), " +
                $"enemyAi={enemyAiId}, " +
                $"field={fieldId}, bgm='{bgmId}', " +
                $"emoteTable={DescribeEmotionTable(response, playerCharaId, enemyCharaId)}.");

            if (enemyAiId <= 0)
            {
                Plugin.Logger.LogWarning(
                    $"[Offlinizer] Story {storyId} has no enemy AI id; the enemy will not play cards.");
                return;
            }

            try
            {
                StoryAISettingData setting = Data.Master?.StoryAISettingList?.GetSettingData(enemyAiId);
                if (setting == null)
                {
                    Plugin.Logger.LogWarning(
                        $"[Offlinizer] Story AI {enemyAiId} is missing from the local story AI master; " +
                        "the enemy will not play cards.");
                    return;
                }

                string deckName = Data.Master?.AIDeckFileNameList?.GetFileName(setting.DeckId);
                string styleName = Data.Master?.AIStyleFileNameList?.GetFileName(setting.StyleId);
                string emoteName = Data.Master?.AIEmoteFileNameList?.GetFileName(setting.EmoteId);

                Plugin.Logger.LogInfo(
                    $"[Offlinizer] Story AI {enemyAiId}: deck#={setting.DeckId} file='{deckName}' " +
                    $"({DescribeAsset(Data.Master?.AIDeckDic, "ai/" + deckName)}), " +
                    $"style#={setting.StyleId} file='{styleName}' " +
                    $"({DescribeAsset(Data.Master?.AIStyleDic, "ai/" + styleName)}), " +
                    $"emote#={setting.EmoteId} file='{emoteName}' " +
                    $"({DescribeAsset(Data.Master?.AIEmoteDic, "ai/" + emoteName)}), " +
                    $"logic={setting.LogicLevel}, innerEmote={setting.UseInnerEmote}.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[Offlinizer] Could not describe story AI {enemyAiId}: {ex.Message}");
            }
        }

        /// <summary>
        /// 这一章实际选中的表情表（<c>&lt;皮肤&gt;_&lt;变体&gt;</c>）。日志里能直接看到「用了哪张表」，
        /// 若显示成光秃秃的皮肤号，就说明变体没推导出来 —— 那一章的表情/动作会是占位表的表现。
        /// </summary>
        private static string DescribeEmotionTable(
            Dictionary<string, object> response,
            int playerCharaId,
            int enemyCharaId)
        {
            if (response == null ||
                !response.TryGetValue("battle_settings", out object raw) ||
                !(raw is IEnumerable<Dictionary<string, object>> settings))
            {
                return "<none>";
            }

            foreach (Dictionary<string, object> setting in settings)
            {
                if (setting == null)
                {
                    continue;
                }

                TryReadInt(setting, "player_emotion_override", out int playerVariation);
                TryReadInt(setting, "enemy_emotion_override", out int enemyVariation);
                return $"player={StoryAiEmoteVariants.FormatEmotionId(playerCharaId, playerVariation)}, " +
                       $"enemy={StoryAiEmoteVariants.FormatEmotionId(enemyCharaId, enemyVariation)}";
            }

            return "<none>";
        }

        private static string DescribeCharacter(int charaId)
        {
            try
            {
                ClassCharacterMasterData chara = GameMgr.GetIns().GetDataMgr().GetCharaPrmByCharaId(charaId);
                return chara == null
                    ? "unknown chara"
                    : $"class={chara.class_id}, skin={chara.skin_id}";
            }
            catch (Exception)
            {
                return "unknown chara";
            }
        }

        private static string DescribeAsset<TValue>(Dictionary<string, TValue> table, string key)
        {
            if (table == null)
            {
                return "table null";
            }

            return table.TryGetValue(key, out TValue value) && (object)value != null
                ? "loaded"
                : "MISSING";
        }

        /// <summary>
        /// 用捕获到的敌方 AI 参数在 master 的 StoryAISettingList 里反查对应的 AI 设置。
        ///
        /// 捕获里的 <c>enemy_deck_id</c>（例如 210008）就是 ai_deck_filelist 的第一列，
        /// 也就是 StoryAISettingData.DeckId 本体 —— 客户端是用它去
        /// <c>AIDeckFileNameList.GetFileName(DeckId)</c> 取文件名的（210008 → "ai_deck_purge_isunia_08"）。
        /// 所以直接用 DeckId 相等来匹配，别再拿文件名里的数字去比。
        /// </summary>
        private static StoryAISettingData ResolveAiSettingByCapture(JsonData captured)
        {
            IReadOnlyList<StoryAISettingData> settings = Data.Master?.StoryAISettingList?.GetSettingDataTable();
            if (settings == null || settings.Count == 0)
            {
                return null;
            }

            int deckId = (int)captured["enemy_deck_id"];
            int styleId = captured.Keys.Contains("enemy_style_id") ? (int)captured["enemy_style_id"] : -1;
            int emoteId = captured.Keys.Contains("enemy_emote_id") ? (int)captured["enemy_emote_id"] : -1;

            StoryAISettingData match = settings.FirstOrDefault(setting =>
                setting != null && setting.DeckId == deckId);
            if (match != null)
            {
                return match;
            }

            // 兜底：DeckId 列在部分表里是下标，这时才用文件名反推。
            match = settings.FirstOrDefault(setting =>
                setting != null && GetAiDeckId(setting.DeckId) == deckId);
            if (match != null)
            {
                return match;
            }

            if (captured.Keys.Contains("enemy_deck_id"))
            {
                Plugin.Logger.LogWarning(
                    $"[Offlinizer] No StoryAISettingData uses deck id {deckId} " +
                    $"(style={styleId}, emote={emoteId}); keeping the locally guessed AI.");
            }

            return null;
        }

        /// <summary>把 AI 设置的 DeckId 还原成牌组文件名里的数字（仅在 DeckId 不是 deck id 本体时使用）。</summary>
        private static int GetAiDeckId(int deckIndex)
        {
            try
            {
                string name = Data.Master?.AIDeckFileNameList?.GetFileName(deckIndex);
                if (string.IsNullOrEmpty(name))
                {
                    return -1;
                }

                name = Path.GetFileNameWithoutExtension(name);
                if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                {
                    return id;
                }

                // 文件名可能形如 "ai_210008" / "deck210008"：取结尾那段数字
                System.Text.RegularExpressions.Match digits =
                    System.Text.RegularExpressions.Regex.Match(name, @"(\d+)\s*$");
                return digits.Success &&
                       int.TryParse(digits.Groups[1].Value, NumberStyles.Integer,
                           CultureInfo.InvariantCulture, out int trailing)
                    ? trailing
                    : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static StoryAISettingData ResolveOriginalStoryAiSetting(ScenarioChapter chapter)
        {
            IReadOnlyList<StoryAISettingData> settings = Data.Master?.StoryAISettingList?.GetSettingDataTable();
            if (settings == null || settings.Count == 0)
            {
                throw new InvalidOperationException("No Story AI setting is available in the loaded master data.");
            }

            int chapterNumber = GetChapterRow(chapter.ChapterId);
            int originalAiId = CreateOriginalStoryAiId(
                chapter.SectionId,
                chapter.ClassId,
                chapterNumber);
            StoryAISettingData originalSetting = settings.FirstOrDefault(
                setting => setting != null && setting.EnemyAiId == originalAiId);
            if (originalSetting != null)
            {
                return originalSetting;
            }

            StoryAISettingData fallback = settings.FirstOrDefault(
                setting => setting != null && setting.EnemyAiId > 0 && setting.DeckId >= 0);
            if (fallback == null)
            {
                throw new InvalidOperationException("No usable Story AI setting is available in the loaded master data.");
            }

            Plugin.Logger.LogWarning(
                $"[Offlinizer] Original Story AI was not resolved for section={chapter.SectionId}, " +
                $"class={chapter.ClassId}, chapter={chapter.ChapterId}; using AI {fallback.EnemyAiId}.");
            return fallback;
        }

        private static int ResolveOriginalEnemyClassId(StoryAISettingData aiSetting)
        {
            if (aiSetting == null)
            {
                return 8;
            }

            // 敌方主战者自身的职业最可靠（客户端就是靠 enemy_chara_id 查职业表）；
            // 其次是 AI 牌组的主职业；硬编码表只作兜底 —— 剧情 AI 常用混职业牌组，
            // 主职业经常判不出来，那张表也覆盖不全。
            int charaClassId = ResolveCharacterClassId(ResolveOriginalEnemyCharaId(aiSetting, 8));
            if (charaClassId > 0)
            {
                return charaClassId;
            }

            int deckClassId = ResolveEnemyClassFromAiDeck(aiSetting.EnemyAiId);
            if (deckClassId > 0)
            {
                return deckClassId;
            }

            for (int classId = 1; classId < OriginalEnemyAiIdsByClass.Length; classId++)
            {
                if (OriginalEnemyAiIdsByClass[classId].Contains(aiSetting.EnemyAiId))
                {
                    return classId;
                }
            }

            // Keep support for future locally available AI data that was not in the
            // final client table used to generate OriginalEnemyAiIdsByClass.
            try
            {
                string deckName = Data.Master.AIDeckFileNameList.GetFileName(aiSetting.DeckId);
                string deckKey = "ai/" + deckName;
                if (Data.Master.AIDeckDic.TryGetValue(deckKey, out AICardDataAssetSet deck))
                {
                    IGrouping<int, AICardDataAsset> dominantClass = deck.Set
                        .Where(card => card != null)
                        .GroupBy(card => GetCardClassId(card.CardID))
                        .Where(group => group.Key >= 1 && group.Key <= 8)
                        .OrderByDescending(group => group.Sum(card => Math.Max(1, card.CardNum)))
                        .FirstOrDefault();
                    if (dominantClass != null)
                    {
                        return dominantClass.Key;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[Offlinizer] Could not infer enemy class for story AI {aiSetting.EnemyAiId}: {ex.Message}");
            }

            Plugin.Logger.LogWarning(
                $"[Offlinizer] Enemy class is unknown for story AI {aiSetting.EnemyAiId}; using Portalcraft.");
            return 8;
        }

        private static int ResolveOriginalEnemyCharaId(StoryAISettingData aiSetting, int enemyClassId)
        {
            if (aiSetting != null &&
                OriginalEnemyCharaIdByAiId.TryGetValue(aiSetting.EnemyAiId, out int originalCharaId) &&
                IsKnownCharacter(originalCharaId))
            {
                return originalCharaId;
            }

            // Several late-story emote ids are the actual story character id. Smaller
            // ids are often shared dialogue sets and must not be treated as leaders.
            if (aiSetting != null && aiSetting.EmoteId >= 500000 && IsKnownCharacter(aiSetting.EmoteId))
            {
                return aiSetting.EmoteId;
            }

            return GetDefaultCharacterId(enemyClassId);
        }

        private static bool IsKnownCharacter(int charaId)
        {
            try
            {
                return GameMgr.GetIns().GetDataMgr().GetCharaPrmByCharaId(charaId) != null;
            }
            catch
            {
                return false;
            }
        }

        private static int GetCardClassId(int cardId)
        {
            return cardId / 100000 % 10;
        }

        internal static int CreateOriginalStoryAiId(int sectionId, int classId, int chapterNumber)
        {
            switch (sectionId)
            {
                case 1:
                    return classId * 1000 + chapterNumber;
                case 2:
                    return 101000 + chapterNumber;
                case 3:
                    return 110000 + classId * 100 + chapterNumber;
                case 4:
                    return 120000 + chapterNumber;
                case 5:
                    return chapterNumber == 2
                        ? 130002
                        : 130000 + classId * 100 + chapterNumber;
                case 6:
                    return 140000 + chapterNumber;
                case 7:
                    return 150000 + classId * 100 + chapterNumber;
                case 8:
                    return 160000 + chapterNumber;
                case 9:
                    return 170000 + chapterNumber;
                case 10:
                    return 180000 + classId * 100 + chapterNumber;
                case 11:
                    return 190000 + chapterNumber;
                case 12:
                    return 200000 + classId * 100 + chapterNumber;
                case 13:
                    return 210000 + chapterNumber;
                case 14:
                    return 220000 + chapterNumber;
                case 15:
                    return (classId == 6 ? 230000 : 240000) + classId * 100 + chapterNumber;
                case 16:
                    return 250000 + chapterNumber;
                case 17:
                    return 260000 + classId * 100 + chapterNumber;
                case 18:
                    return 270000 + chapterNumber;
                case 19:
                    if (chapterNumber == 5)
                    {
                        return 280004;
                    }
                    if (chapterNumber == 11)
                    {
                        return 280010;
                    }
                    return 280000 + chapterNumber;
                case 20:
                    return 290000 + chapterNumber;
                default:
                    return 0;
            }
        }

        private static StoryManifest GetManifest()
        {
            // 资源目录可能是游戏目录同级的 Resources（见 ResourceRootPatches）。
            string manifestPath = ResourceRootPatches.ResolveResourcePath(
                "manifest/story_assetmanifest");
            if (!File.Exists(manifestPath))
            {
                throw new FileNotFoundException("The local story asset manifest was not found.", manifestPath);
            }

            DateTime writeTimeUtc = File.GetLastWriteTimeUtc(manifestPath);
            lock (ManifestLock)
            {
                if (_cachedManifest != null && _cachedManifestWriteTimeUtc == writeTimeUtc)
                {
                    return _cachedManifest;
                }

                _cachedManifest = StoryManifest.Load(manifestPath);
                _cachedManifestWriteTimeUtc = writeTimeUtc;
                Plugin.Logger.LogInfo(
                    $"[Offlinizer] Loaded {_cachedManifest.ChapterCount} story chapters from the local asset manifest.");
                return _cachedManifest;
            }
        }

        private static int? GetClassId(int charaId)
        {
            if (charaId == 0)
            {
                return null;
            }

            ClassCharacterMasterData charaData = GameMgr.GetIns().GetDataMgr().GetCharaPrmByCharaId(charaId);
            if (charaData == null)
            {
                Plugin.Logger.LogWarning($"[Offlinizer] Story character {charaId} was not found; using the combined chapter list.");
                return null;
            }

            return charaData.class_id;
        }

        internal static int GetDefaultCharacterId(int classId)
        {
            try
            {
                return GameMgr.GetIns().GetDataMgr().GetCharaPrmByClassId(classId, false)?.chara_id ?? 0;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[Offlinizer] Could not resolve the default character for story class {classId}: {ex.Message}");
                return 0;
            }
        }

        private static int CreateStoryId(int sectionId, int classId, string chapterId)
        {
            int row = GetChapterRow(chapterId);
            int suffix = GetChapterSuffixIndex(chapterId);
            return checked(sectionId * 100000 + classId * 10000 + row * 100 + suffix * 10);
        }

        private static string GetNextChapterId(IReadOnlyList<ScenarioChapter> chapters, int currentIndex)
        {
            ScenarioChapter current = chapters[currentIndex];
            int currentRow = GetChapterRow(current.ChapterId);
            int nextRow = chapters
                .Select(chapter => GetChapterRow(chapter.ChapterId))
                .Where(row => row > currentRow)
                .DefaultIfEmpty(0)
                .Min();
            if (nextRow == 0)
            {
                return "0";
            }

            List<ScenarioChapter> nextRowChapters = chapters
                .Where(chapter => GetChapterRow(chapter.ChapterId) == nextRow)
                .ToList();
            string currentSuffix = GetChapterSuffix(current.ChapterId);
            if (!string.IsNullOrEmpty(currentSuffix))
            {
                ScenarioChapter matchingBranch = nextRowChapters.FirstOrDefault(
                    chapter => GetChapterSuffix(chapter.ChapterId) == currentSuffix);
                if (matchingBranch != null)
                {
                    return matchingBranch.ChapterId;
                }
            }

            ScenarioChapter mainRoute = nextRowChapters.FirstOrDefault(
                chapter => string.IsNullOrEmpty(GetChapterSuffix(chapter.ChapterId)));
            return mainRoute?.ChapterId ?? string.Join(" ", nextRowChapters.Select(chapter => chapter.ChapterId).ToArray());
        }

        private static string GetSelectionDisplayPosition(string chapterId)
        {
            int suffix = GetChapterSuffixIndex(chapterId);
            return suffix == 0 ? string.Empty : suffix.ToString(CultureInfo.InvariantCulture);
        }

        private static int GetChapterRow(string chapterId)
        {
            int digitCount = 0;
            while (digitCount < chapterId.Length && char.IsDigit(chapterId[digitCount]))
            {
                digitCount++;
            }

            return int.Parse(chapterId.Substring(0, digitCount), CultureInfo.InvariantCulture);
        }

        private static string GetChapterSuffix(string chapterId)
        {
            int digitCount = 0;
            while (digitCount < chapterId.Length && char.IsDigit(chapterId[digitCount]))
            {
                digitCount++;
            }

            return chapterId.Substring(digitCount);
        }

        private static int GetChapterSuffixIndex(string chapterId)
        {
            string suffix = GetChapterSuffix(chapterId);
            return string.IsNullOrEmpty(suffix) ? 0 : char.ToLowerInvariant(suffix[0]) - 'a' + 1;
        }

        private sealed class StoryManifest
        {
            private readonly List<ScenarioChapter> _chapters;

            private StoryManifest(List<ScenarioChapter> chapters)
            {
                _chapters = chapters;
            }

            internal int ChapterCount => _chapters.Count;

            internal static StoryManifest Load(string manifestPath)
            {
                Dictionary<string, ScenarioChapter> chapters = new Dictionary<string, ScenarioChapter>(StringComparer.Ordinal);
                foreach (string line in File.ReadLines(manifestPath))
                {
                    int commaIndex = line.IndexOf(',');
                    string assetName = commaIndex >= 0 ? line.Substring(0, commaIndex) : line;
                    Match match = ScenarioParamRegex.Match(assetName);
                    if (!match.Success)
                    {
                        continue;
                    }

                    int sectionId = int.Parse(match.Groups["section"].Value, CultureInfo.InvariantCulture);
                    int classId = int.Parse(match.Groups["class"].Value, CultureInfo.InvariantCulture);
                    string chapterId = match.Groups["chapter"].Value;
                    string key = sectionId.ToString(CultureInfo.InvariantCulture) + ":" +
                        classId.ToString(CultureInfo.InvariantCulture) + ":" + chapterId;
                    if (!chapters.TryGetValue(key, out ScenarioChapter chapter))
                    {
                        chapter = new ScenarioChapter(sectionId, classId, chapterId);
                        chapters.Add(key, chapter);
                    }

                    int part = int.Parse(match.Groups["part"].Value, CultureInfo.InvariantCulture);
                    chapter.AvailableClassIds.Add(classId);
                    if (part == 1)
                    {
                        chapter.HasFirstHalf = true;
                    }
                    else
                    {
                        chapter.HasSecondHalf = true;
                        if (classId != 0)
                        {
                            chapter.BattleClassIds.Add(classId);
                        }
                    }

                    if (match.Groups["subchapter"].Success)
                    {
                        chapter.SubChapterIds.Add(int.Parse(
                            match.Groups["subchapter"].Value,
                            CultureInfo.InvariantCulture));
                    }
                }

                return new StoryManifest(chapters.Values.ToList());
            }

            internal List<int> GetClassIds(int sectionId)
            {
                return _chapters
                    .Where(chapter => chapter.SectionId == sectionId && chapter.ClassId != 0)
                    .Select(chapter => chapter.ClassId)
                    .Distinct()
                    .OrderBy(classId => classId)
                    .ToList();
            }

            internal List<ScenarioChapter> GetChapters(int sectionId, int? selectedClassId)
            {
                IEnumerable<ScenarioChapter> sectionChapters = _chapters
                    .Where(chapter => chapter.SectionId == sectionId);

                if (selectedClassId != null)
                {
                    sectionChapters = sectionChapters.Where(chapter => chapter.ClassId == selectedClassId.Value);
                }
                else
                {
                    sectionChapters = sectionChapters
                        .GroupBy(chapter => chapter.ChapterId, StringComparer.Ordinal)
                        .Select(MergeChapterVariants);
                }

                return sectionChapters
                    .OrderBy(chapter => GetChapterRow(chapter.ChapterId))
                    .ThenBy(chapter => GetChapterSuffixIndex(chapter.ChapterId))
                    .ToList();
            }

            private static ScenarioChapter MergeChapterVariants(IGrouping<string, ScenarioChapter> variants)
            {
                ScenarioChapter preferred = variants.FirstOrDefault(chapter => chapter.ClassId == 0) ?? variants.First();
                ScenarioChapter merged = new ScenarioChapter(preferred.SectionId, preferred.ClassId, preferred.ChapterId);
                foreach (ScenarioChapter variant in variants)
                {
                    merged.MergeFrom(variant);
                }

                return merged;
            }
        }

        private sealed class ScenarioChapter
        {
            internal ScenarioChapter(int sectionId, int classId, string chapterId)
            {
                SectionId = sectionId;
                ClassId = classId;
                ChapterId = chapterId;
            }

            internal int SectionId { get; }
            internal int ClassId { get; }
            internal string ChapterId { get; }
            internal bool HasFirstHalf { get; set; }
            internal bool HasSecondHalf { get; set; }
            internal SortedSet<int> AvailableClassIds { get; } = new SortedSet<int>();
            internal SortedSet<int> BattleClassIds { get; } = new SortedSet<int>();
            internal SortedSet<int> SubChapterIds { get; } = new SortedSet<int>();

            internal void MergeFrom(ScenarioChapter other)
            {
                HasFirstHalf |= other.HasFirstHalf;
                HasSecondHalf |= other.HasSecondHalf;
                AvailableClassIds.UnionWith(other.AvailableClassIds);
                BattleClassIds.UnionWith(other.BattleClassIds);
                SubChapterIds.UnionWith(other.SubChapterIds);
            }
        }
    }
}
