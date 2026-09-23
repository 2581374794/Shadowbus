using Cute;
using HarmonyLib;
using LitJson;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Wizard;

namespace Shadowbus
{
    internal static class ProfileOfflineData
    {
        private const int MaxClassLevel = 150;
        private const int MaxClassExperience = 199250;
        private const int MaxProfileStat = 999999;

        private static readonly object SettingsLock = new object();

        private sealed class LocalProfileSettings
        {
            [JsonProperty("viewer_id")]
            public int ViewerId { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("emblem_id")]
            public long? EmblemId { get; set; }

            [JsonProperty("degree_id")]
            public int? DegreeId { get; set; }

            [JsonProperty("country_code")]
            public string CountryCode { get; set; }

            [JsonProperty("is_official_mark_displayed")]
            public bool? IsOfficialMarkDisplayed { get; set; }

            [JsonProperty("leader_skins")]
            public Dictionary<int, LocalLeaderSkinSetting> LeaderSkins { get; set; } =
                new Dictionary<int, LocalLeaderSkinSetting>();

            /// <summary>金币（<c>PlayerStaticData.UserRupyCount</c>）。</summary>
            [JsonProperty("rupy")]
            public int? Rupy { get; set; }

            /// <summary>以太（<c>PlayerStaticData.UserRedEtherCount</c>）。</summary>
            [JsonProperty("red_ether")]
            public int? RedEther { get; set; }

            /// <summary>入场券 / 兑换券类道具的数量，按道具 id 存（挑战券 1、杯赛入场券 2 …）。</summary>
            [JsonProperty("ticket_items")]
            public Dictionary<int, int> TicketItems { get; set; } = new Dictionary<int, int>();
        }

        private sealed class LocalLeaderSkinSetting
        {
            [JsonProperty("current_chara_id")]
            public int CurrentCharaId { get; set; }

            [JsonProperty("is_random")]
            public bool IsRandom { get; set; }

            [JsonProperty("skin_ids")]
            public List<int> SkinIds { get; set; } = new List<int>();
        }

        internal static bool CanHandle(string taskName)
        {
            return taskName == nameof(ProfileTask) ||
                taskName == nameof(NameUpdateTask) ||
                taskName == nameof(EmblemUpdateTask) ||
                taskName == nameof(DegreeUpdateTask) ||
                taskName == nameof(CountryCodeSetTask) ||
                taskName == nameof(OfficialMarkDisplayTask) ||
                taskName == nameof(LeaderSkinUpdateTask) ||
                taskName == nameof(RankingMasterMyHistoriesTask) ||
                taskName == nameof(GetGrandMasterTask) ||
                taskName == nameof(MasterResetMonthTask);
        }

        internal static bool TryCreateResponse(NetworkTask task, out JsonData response)
        {
            response = null;
            try
            {
                object data;
                if (task is ProfileTask)
                {
                    data = CreateProfileData();
                }
                else if (task is NameUpdateTask &&
                    task.Params is NameUpdateTask.NameUpdateTaskParam nameParam)
                {
                    UpdateSettings(settings => settings.Name = nameParam.name);
                    data = new Dictionary<string, object>();
                }
                else if (task is EmblemUpdateTask &&
                    task.Params is EmblemUpdateTask.EmblemUpdateTaskParam emblemParam)
                {
                    UpdateSettings(settings => settings.EmblemId = emblemParam.emblem_id);
                    data = new Dictionary<string, object>();
                }
                else if (task is DegreeUpdateTask &&
                    task.Params is DegreeUpdateTask.DegreeUpdateTaskParam degreeParam)
                {
                    UpdateSettings(settings => settings.DegreeId = degreeParam.degree_id);
                    data = new Dictionary<string, object>();
                }
                else if (task is CountryCodeSetTask &&
                    task.Params is CountryCodeSetTask.CountryCodeSetTaskParam countryParam)
                {
                    UpdateSettings(settings => settings.CountryCode = countryParam.country_code ?? string.Empty);
                    data = new Dictionary<string, object>();
                }
                else if (task is OfficialMarkDisplayTask &&
                    task.Params is OfficialMarkDisplayTask.OfficialMarkDisplayTaskParam officialParam)
                {
                    UpdateSettings(settings =>
                        settings.IsOfficialMarkDisplayed = officialParam.is_official_mark_displayed != 0);
                    data = new Dictionary<string, object>();
                }
                else if (task is LeaderSkinUpdateTask &&
                    task.Params is LeaderSkinUpdateTask.LeaderSkinUpdateTaskParam leaderParam)
                {
                    data = CreateLeaderSkinUpdateData(leaderParam);
                }
                else if (task is RankingMasterMyHistoriesTask)
                {
                    data = CreateMasterHistoryData(IsCrossoverTask(task));
                }
                else if (task is GetGrandMasterTask)
                {
                    data = CreateGrandMasterData(IsCrossoverTask(task));
                }
                else if (task is MasterResetMonthTask)
                {
                    data = CreateMasterResetData();
                }
                else
                {
                    return false;
                }

                response = JsonMapper.ToObject(JsonConvert.SerializeObject(CreateResponseEnvelope(data)));
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError(
                    $"[ProfileOffline] Failed to create local data for {task.GetType().Name}: {ex}");
                return false;
            }
        }

        internal static void ApplyAfterLoad(LoadDetail loadDetail)
        {
            if (loadDetail == null)
            {
                return;
            }

            ApplyMaxProfileStats(loadDetail);
            LocalProfileSettings settings = LoadSettings();
            ApplySettings(loadDetail, settings, false);

            Plugin.Logger.LogInfo(
                $"[ProfileOffline] Applied full profile data and local settings for '{loadDetail._userInfo.name}'.");
        }

        internal static void ApplyToResponse(NetworkTask task, JsonData response)
        {
            if (task == null || response == null || !response.IsObject ||
                !response.Keys.Contains("data"))
            {
                return;
            }

            JsonData data = response["data"];
            if (data == null || !data.IsObject || !data.Keys.Contains("user_info"))
            {
                return;
            }

            JsonData userInfo = data["user_info"];
            if (userInfo == null || !userInfo.IsObject)
            {
                return;
            }

            LocalProfileSettings settings = LoadSettings();
            if (settings.Name != null)
            {
                userInfo["name"] = settings.Name;
            }
            if (settings.EmblemId.HasValue)
            {
                userInfo["selected_emblem_id"] = settings.EmblemId.Value;
            }
            if (settings.DegreeId.HasValue)
            {
                userInfo["selected_degree_id"] = settings.DegreeId.Value;
            }
            if (settings.CountryCode != null)
            {
                userInfo["country_code"] = settings.CountryCode;
            }
            if (settings.IsOfficialMarkDisplayed.HasValue)
            {
                userInfo["is_official_mark_displayed"] =
                    settings.IsOfficialMarkDisplayed.Value ? 1 : 0;
            }
            if (settings.ViewerId > 0)
            {
                // 「货币修改」里改的 ID：显示用的那个 viewer_id 就在这里。
                userInfo["viewer_id"] = settings.ViewerId;
            }
        }

        /// <summary>「货币修改」里改的 ID（显示用的 viewer_id），写进 Mods/Profile.json。</summary>
        internal static void SaveViewerId(int viewerId)
        {
            if (viewerId <= 0)
            {
                return;
            }

            // 旧 ID 必须**先**取：下面把 Certification.ViewerId 改成新值之后，
            // 再去读它拿到的就是新值了（上一版就是这么把自己比没了的）。
            int previousViewerId = GetViewerId();

            _cachedViewerId = viewerId;

            UpdateSettings(settings => settings.ViewerId = viewerId);

            // 真正让「ID 变掉」还得改客户端自己的 viewer id：
            // Certification.ViewerId 的 setter 会写 savedata 并更新缓存。
            try
            {
                Certification.ViewerId = viewerId;
                Plugin.Logger.LogInfo($"[Other] Client viewer id changed to {viewerId}.");
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Other] Could not change the client viewer id: {exception.Message}");
            }

            // 离线 profile 的源头文件也改掉：这样登录时注入的 LoadTask 数据本身就带新 ID，
            // 不管哪个页面读都不会再看到旧值。
            try
            {
                RewriteOfflineViewerId(previousViewerId, viewerId);
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Other] Could not rewrite LoadTask.json: {exception.Message}");
            }
        }

        /// <summary>
        /// 把 Mods/OfflinizedTasks 下**所有**离线响应里的 viewer_id 都改成新值。
        /// （LoadTask / SignUpTask / DeckInfoTask … 每个文件里都有这个字段，
        ///  之前只改 LoadTask 显然不够 —— 登录时那份会把旧 ID 又带回来。）
        /// </summary>
        private static void RewriteOfflineViewerId(int oldViewerId, int viewerId)
        {
            string directory = Path.Combine(PathHelper.ModPath, "OfflinizedTasks");
            if (!Directory.Exists(directory))
            {
                return;
            }

            int files = 0;
            int fields = 0;

            foreach (string path in Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    string text = File.ReadAllText(path);
                    if (!text.Contains("viewer_id"))
                    {
                        continue;
                    }

                    JObject root = JObject.Parse(text);
                    int changed = ReplaceViewerIds(root, viewerId);
                    if (changed == 0)
                    {
                        continue;
                    }

                    File.WriteAllText(path, root.ToString(Formatting.None), new UTF8Encoding(false));
                    files++;
                    fields += changed;
                }
                catch (Exception exception)
                {
                    Plugin.Logger.LogWarning(
                        $"[Other] Could not rewrite '{Path.GetFileName(path)}': {exception.Message}");
                }
            }

            Plugin.Logger.LogInfo(
                $"[Other] OfflinizedTasks: rewrote {fields} viewer_id in {files} file(s) " +
                $"(previous setting {oldViewerId:D9} -> {viewerId}).");
        }

        private static int ReplaceViewerIds(JToken token, int viewerId)
        {
            int changed = 0;

            if (token is JObject obj)
            {
                foreach (JProperty property in obj.Properties().ToList())
                {
                    if (property.Name == "viewer_id" && property.Value.Type == JTokenType.Integer)
                    {
                        property.Value = viewerId;
                        changed++;
                    }
                    else
                    {
                        changed += ReplaceViewerIds(property.Value, viewerId);
                    }
                }
            }
            else if (token is JArray array)
            {
                foreach (JToken item in array)
                {
                    changed += ReplaceViewerIds(item, viewerId);
                }
            }

            return changed;
        }

        /// <summary>
        /// 弹窗 / 界面里用到的 ID，**内存缓存**：<c>get_UserViewerID</c> 会在很多地方被频繁调用，
        /// 每次都去读盘 + 解析 Profile.json 会卡到整个游戏（实测就是它引起的全局卡顿）。
        /// </summary>
        private static int _cachedViewerId = -1;

        internal static int GetViewerId()
        {
            if (_cachedViewerId > 0)
            {
                return _cachedViewerId;
            }

            int saved = LoadSettings().ViewerId;
            if (saved > 0)
            {
                _cachedViewerId = saved;
                return _cachedViewerId;
            }

            try
            {
                _cachedViewerId = PlayerStaticData.UserViewerID;
            }
            catch (Exception)
            {
                _cachedViewerId = 0;
            }

            return _cachedViewerId;
        }

        /// <summary>
        /// 改过 ID 之后，凡是读 <c>PlayerStaticData.UserViewerID</c>（= Certification.ViewerId）的地方
        /// 都返回缓存里的那个值。**只读缓存，不碰磁盘**。
        /// </summary>
        [HarmonyPatch(typeof(PlayerStaticData), "get_UserViewerID")]
        [HarmonyPostfix]
        internal static void UserViewerID_Postfix(ref int __result)
        {
            if (_cachedViewerId < 0)
            {
                GetViewerId();      // 整个进程只读一次盘
            }

            if (_cachedViewerId > 0)
            {
                __result = _cachedViewerId;
            }
        }

        internal static void ReapplyCurrentSettings()
        {
            ApplySettings(Data.Load?.data, LoadSettings(), true);
        }

        internal static P2PProfile CreateP2PProfile()
        {
            int viewerId = GetOrCreateViewerId();
            P2PProfile profile = new P2PProfile
            {
                ViewerId = viewerId,
                UserName = "Player",
                CountryCode = string.Empty
            };

            try
            {
                profile.UserName = PlayerStaticData.UserName ?? "Player";
                profile.Rank = PlayerStaticData.UserRankHighAllFormat();
                profile.BattlePoint = PlayerStaticData.UserBattlePointHighFormat();
                profile.MasterPoint = PlayerStaticData.UserMasterPointHighAllFormat();
                profile.DegreeId = PlayerStaticData.UserDegreeID;
                profile.EmblemId = PlayerStaticData.UserEmblemID;
                profile.CountryCode = PlayerStaticData.UserCountryCode ?? string.Empty;
                profile.IsOfficial = PlayerStaticData.IsOfficialUserDisplay;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    "[ProfileOffline] Some live profile fields were unavailable for P2P: " + ex.Message);
            }

            LocalProfileSettings settings = LoadSettings();
            if (settings.Name != null)
            {
                profile.UserName = settings.Name;
            }
            if (settings.EmblemId.HasValue)
            {
                profile.EmblemId = settings.EmblemId.Value;
            }
            if (settings.DegreeId.HasValue)
            {
                profile.DegreeId = settings.DegreeId.Value;
            }
            if (settings.CountryCode != null)
            {
                profile.CountryCode = settings.CountryCode;
            }
            if (settings.IsOfficialMarkDisplayed.HasValue)
            {
                profile.IsOfficial = settings.IsOfficialMarkDisplayed.Value;
            }
            return profile;
        }

        /// <summary>
        /// Returns the stable per-installation online identity. This is kept
        /// separate from the game's original account viewer id because the
        /// latter is shared by offline/test clients.
        /// </summary>
        internal static int GetOrCreateViewerId()
        {
            lock (SettingsLock)
            {
                LocalProfileSettings settings = LoadSettingsUnlocked();
                if (settings.ViewerId > 0)
                {
                    return settings.ViewerId;
                }

                settings.ViewerId = GenerateViewerId();
                SaveSettingsUnlocked(settings);
                Plugin.Logger.LogInfo(
                    $"[ProfileOffline] Generated persistent online viewer id {settings.ViewerId}.");
                return settings.ViewerId;
            }
        }

        private static int GenerateViewerId()
        {
            byte[] bytes = new byte[4];
            using (var random = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }

            uint value = BitConverter.ToUInt32(bytes, 0);
            return (int)(100000000u + (value % 900000000u));
        }

        private static object CreateProfileData()
        {
            LoadDetail loadDetail = Data.Load?.data;
            ApplyMaxProfileStats(loadDetail);
            LocalProfileSettings settings = LoadSettings();
            ApplySettings(loadDetail, settings, true);
            IDictionary<int, ClassCharaPrm> classParameters =
                GameMgr.GetIns().GetDataMgr().GetClassPrmDictionary();
            List<Dictionary<string, object>> classList = new List<Dictionary<string, object>>();

            for (int classId = 1; classId <= 8; classId++)
            {
                if (!classParameters.TryGetValue(classId, out ClassCharaPrm classParameter))
                {
                    continue;
                }

                int currentCharaId = classParameter.CurrentCharaData?.chara_id ?? classId;
                bool isRandom = classParameter.IsRandomLeaderSkin;
                List<int> skinIds = classParameter.LeaderSkinIdList.ToList();
                if (settings.LeaderSkins != null &&
                    settings.LeaderSkins.TryGetValue(classId, out LocalLeaderSkinSetting leaderSetting))
                {
                    currentCharaId = leaderSetting.CurrentCharaId > 0
                        ? leaderSetting.CurrentCharaId
                        : currentCharaId;
                    isRandom = leaderSetting.IsRandom;
                    skinIds = leaderSetting.SkinIds?.Where(id => id > 0).Distinct().ToList() ??
                        new List<int>();
                }
                if (skinIds.Count == 0)
                {
                    skinIds.Add(classParameter.CurrentCharaData?.skin_id ?? classId);
                }

                classList.Add(new Dictionary<string, object>
                {
                    ["class_id"] = classId,
                    ["is_available"] = 1,
                    ["level"] = MaxClassLevel,
                    ["exp"] = MaxClassExperience,
                    ["is_random_leader_skin"] = isRandom ? 1 : 0,
                    ["leader_skin_id"] = currentCharaId,
                    ["leader_skin_id_list"] = skinIds,
                    ["default_leader_skin_id"] = classParameter.DefaultCharaData?.chara_id ?? classId
                });
            }

            return new Dictionary<string, object>
            {
                ["user_rank_match_total_win"] = MaxProfileStat,
                ["user_class_list"] = classList
            };
        }

        private static object CreateLeaderSkinUpdateData(
            LeaderSkinUpdateTask.LeaderSkinUpdateTaskParam param)
        {
            List<int> skinIds = (param.is_random_leader_skin
                    ? param.leader_skin_id_list ?? Array.Empty<int>()
                    : new[] { param.leader_skin_id })
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            if (skinIds.Count == 0)
            {
                throw new InvalidOperationException("Leader skin selection did not contain a valid skin id.");
            }

            int selectedSkinId = skinIds[0];
            ClassCharacterMasterData selectedSkin =
                GameMgr.GetIns().GetDataMgr().GetCharaPrmBySkinId(selectedSkinId);
            int currentCharaId = selectedSkin?.chara_id ?? selectedSkinId;

            UpdateSettings(settings =>
            {
                if (settings.LeaderSkins == null)
                {
                    settings.LeaderSkins = new Dictionary<int, LocalLeaderSkinSetting>();
                }
                settings.LeaderSkins[param.class_id] = new LocalLeaderSkinSetting
                {
                    CurrentCharaId = currentCharaId,
                    IsRandom = param.is_random_leader_skin,
                    SkinIds = skinIds
                };
            });

            return new Dictionary<string, object>
            {
                ["is_random_leader_skin"] = param.is_random_leader_skin,
                ["leader_skin_id"] = selectedSkinId,
                ["leader_skin_id_list"] = skinIds
            };
        }

        private static object CreateMasterHistoryData(bool isCrossover)
        {
            int maxRankId = GetMaxRankId(Data.Load?.data);
            int periodId = 1;
            Dictionary<string, object> period = new Dictionary<string, object>
            {
                ["id"] = periodId,
                ["period_num"] = 1,
                ["begin_time"] = "2026-01-01 00:00:00",
                ["end_time"] = "2099-12-31 23:59:59",
                ["is_calculated"] = true
            };

            Dictionary<string, object> histories = new Dictionary<string, object>();
            IEnumerable<Format> formats = isCrossover
                ? new[] { Format.Crossover }
                : new[] { Format.Rotation, Format.Unlimited };
            foreach (Format format in formats)
            {
                string formatKey = Data.FormatConvertApi(format).ToString(CultureInfo.InvariantCulture);
                histories[formatKey] = new Dictionary<string, object>
                {
                    [periodId.ToString(CultureInfo.InvariantCulture)] = new Dictionary<string, object>
                    {
                        ["rank"] = 1,
                        ["score"] = MaxProfileStat,
                        ["rank_id"] = maxRankId
                    }
                };
            }

            return new Dictionary<string, object>
            {
                ["periods"] = new Dictionary<string, object>
                {
                    [isCrossover ? "crossover" : "normal"] = new[] { period }
                },
                ["histories"] = histories
            };
        }

        private static object CreateGrandMasterData(bool isCrossover)
        {
            int maxRankId = GetMaxRankId(Data.Load?.data);
            Dictionary<string, object> pointsByFormat = new Dictionary<string, object>();
            IEnumerable<Format> formats = isCrossover
                ? new[] { Format.Crossover }
                : new[] { Format.Rotation, Format.Unlimited };
            foreach (Format format in formats)
            {
                List<Dictionary<string, object>> periods = new List<Dictionary<string, object>>();
                for (int index = 0; index < UserRank.GRAND_MASTER_PERIOD; index++)
                {
                    periods.Add(new Dictionary<string, object>
                    {
                        ["ranking_period_id"] = index + 1,
                        ["ranking_period_num"] = index + 1,
                        ["master_point"] = MaxProfileStat,
                        ["rank"] = maxRankId
                    });
                }
                pointsByFormat[Data.FormatConvertApi(format).ToString(CultureInfo.InvariantCulture)] = periods;
            }

            return new Dictionary<string, object>
            {
                ["user_period_master_point"] = pointsByFormat
            };
        }

        private static bool IsCrossoverTask(NetworkTask task)
        {
            return task.Url.IndexOf("crossover/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static object CreateMasterResetData()
        {
            int maxRankId = GetMaxRankId(Data.Load?.data);
            Dictionary<string, object> data = new Dictionary<string, object>();
            foreach (Format format in new[] { Format.Rotation, Format.Unlimited })
            {
                data[Data.FormatConvertApi(format).ToString(CultureInfo.InvariantCulture)] =
                    new Dictionary<string, object>
                    {
                        ["rank"] = maxRankId,
                        ["master_point"] = MaxProfileStat,
                        ["target_grand_master_point"] = MaxProfileStat,
                        ["current_grand_master_point"] = MaxProfileStat,
                        ["is_promotion"] = 0
                    };
            }
            return data;
        }

        private static void ApplyMaxProfileStats(LoadDetail loadDetail)
        {
            if (loadDetail == null)
            {
                return;
            }

            IDictionary<int, ClassCharaPrm> classParameters =
                GameMgr.GetIns().GetDataMgr().GetClassPrmDictionary();
            for (int classId = 1; classId <= 8; classId++)
            {
                if (!classParameters.TryGetValue(classId, out ClassCharaPrm classParameter))
                {
                    continue;
                }
                classParameter.SetClassCharaLv(MaxClassLevel);
                classParameter.SetClassCharaExp(MaxClassExperience);
                classParameter.SetClassCharaWin(MaxProfileStat);
                classParameter.SetClassCharaBattleCount(MaxProfileStat);
            }

            int maxRankId = GetMaxRankId(loadDetail);
            foreach (Format format in new[] { Format.Rotation, Format.Unlimited })
            {
                if (!loadDetail._userRank.TryGetValue((int)format, out UserRank userRank))
                {
                    continue;
                }
                userRank.rank = maxRankId;
                userRank.battle_point = MaxProfileStat;
                userRank.master_point = MaxProfileStat;
                userRank.is_master_rank = true;
                userRank.is_grand_master_rank = true;
                userRank.grandMasterData.targetMasterPoint = MaxProfileStat;
                userRank.grandMasterData.currentMasterPoint = MaxProfileStat;
                for (int index = 0; index < UserRank.GRAND_MASTER_PERIOD; index++)
                {
                    userRank.grandMasterData.id[index] = index + 1;
                    userRank.grandMasterData.periodNum[index] = index + 1;
                    userRank.grandMasterData.masterPoint[index] = MaxProfileStat;
                    userRank.grandMasterData.rankId[index] = maxRankId;
                }
            }
            UserRank.IsGrandMasterAvailability = true;
        }

        private static int GetMaxRankId(LoadDetail loadDetail)
        {
            if (loadDetail?.RankInfoList == null || loadDetail.RankInfoList.Count == 0)
            {
                return UserRank.MASTER_RANK_INDEX + 1;
            }
            return loadDetail.RankInfoList.Max(rank => rank.RankId);
        }

        private static Dictionary<string, object> CreateResponseEnvelope(object data)
        {
            return new Dictionary<string, object>
            {
                ["data_headers"] = new Dictionary<string, object>
                {
                    ["short_udid"] = Certification.ShortUdid,
                    ["viewer_id"] = Certification.ViewerId,
                    ["sid"] = Certification.SessionId ?? string.Empty,
                    ["servertime"] = 0L,
                    ["result_code"] = 1
                },
                ["data"] = data
            };
        }

        private static LocalProfileSettings LoadSettings()
        {
            lock (SettingsLock)
            {
                return LoadSettingsUnlocked();
            }
        }

        private static void UpdateSettings(Action<LocalProfileSettings> update)
        {
            lock (SettingsLock)
            {
                LocalProfileSettings settings = LoadSettingsUnlocked();
                update(settings);
                SaveSettingsUnlocked(settings);
                ApplySettings(Data.Load?.data, settings, true);
                Plugin.Logger.LogInfo(
                    $"[ProfileOffline] Saved local profile settings to {PathHelper.ProfileSettingsPath}.");
            }
        }

        private static void SaveSettingsUnlocked(LocalProfileSettings settings)
        {
            string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
            File.WriteAllText(PathHelper.ProfileSettingsPath, json, Encoding.UTF8);
        }

        private static void ApplySettings(
            LoadDetail loadDetail,
            LocalProfileSettings settings,
            bool refreshCachedResources)
        {
            if (settings == null)
            {
                return;
            }

            if (loadDetail?._userInfo != null)
            {
                if (settings.Name != null)
                {
                    loadDetail._userInfo.name = settings.Name;
                }
                if (settings.ViewerId > 0)
                {
                    // 「货币修改」里改的 ID：个人信息页显示的就是这个 viewer_id。
                    loadDetail._userInfo.viewer_id = settings.ViewerId;
                }
                if (settings.EmblemId.HasValue)
                {
                    if (refreshCachedResources)
                    {
                        PlayerStaticData.UserEmblemID = settings.EmblemId.Value;
                    }
                    else
                    {
                        loadDetail._userInfo.selected_emblem_id = settings.EmblemId.Value;
                    }
                }
                if (settings.DegreeId.HasValue)
                {
                    loadDetail._userInfo.selected_degree_id = settings.DegreeId.Value;
                }
                if (settings.CountryCode != null)
                {
                    if (refreshCachedResources)
                    {
                        PlayerStaticData.UserCountryCode = settings.CountryCode;
                    }
                    else
                    {
                        loadDetail._userInfo.country_code = settings.CountryCode;
                    }
                }
            }
            if (settings.IsOfficialMarkDisplayed.HasValue)
            {
                PlayerStaticData.IsOfficialUserDisplay = settings.IsOfficialMarkDisplayed.Value;
            }

            ApplyLeaderSkinSettings(settings);
            ApplyCurrencySettings(loadDetail, settings);
        }

        /// <summary>
        /// 把本地保存的金币 / 以太 / 入场券数量写回 profile（「其他 → 货币修改」改的就是这三个）。
        /// 直接写 <c>LoadDetail</c> 而不是走 <c>PlayerStaticData</c>，因为 LoadTask 刚解析完时
        /// 传入的 loadDetail 才是要生效的那一份。
        /// </summary>
        private static void ApplyCurrencySettings(LoadDetail loadDetail, LocalProfileSettings settings)
        {
            if (loadDetail == null || settings == null)
            {
                return;
            }

            try
            {
                if (loadDetail._userCrystalCount != null)
                {
                    if (settings.Rupy.HasValue)
                    {
                        loadDetail._userCrystalCount.rupy = settings.Rupy.Value;
                    }

                    if (settings.RedEther.HasValue)
                    {
                        loadDetail._userCrystalCount.red_ether = settings.RedEther.Value;
                    }
                }

                if (settings.TicketItems != null && settings.TicketItems.Count > 0)
                {
                    if (loadDetail._userItemDict == null)
                    {
                        loadDetail._userItemDict = new Dictionary<int, int>();
                    }

                    foreach (KeyValuePair<int, int> pair in settings.TicketItems)
                    {
                        if (pair.Key > 0 && pair.Value >= 0)
                        {
                            loadDetail._userItemDict[pair.Key] = pair.Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("[ProfileOffline] Could not apply local currency settings: " + ex.Message);
            }
        }

        /// <summary>「货币修改」保存：金币 / 以太 / 各入场券数量，写进 Mods/Profile.json 并立即生效。</summary>
        internal static void SaveCurrencies(int rupy, int redEther, Dictionary<int, int> ticketItems)
        {
            UpdateSettings(settings =>
            {
                settings.Rupy = Math.Max(0, rupy);
                settings.RedEther = Math.Max(0, redEther);
                settings.TicketItems = new Dictionary<int, int>();
                if (ticketItems == null)
                {
                    return;
                }

                foreach (KeyValuePair<int, int> pair in ticketItems)
                {
                    if (pair.Key > 0)
                    {
                        settings.TicketItems[pair.Key] = Math.Max(0, pair.Value);
                    }
                }
            });
        }

        private static void ApplyLeaderSkinSettings(LocalProfileSettings settings)
        {
            if (settings?.LeaderSkins == null || settings.LeaderSkins.Count == 0)
            {
                return;
            }

            IDictionary<int, ClassCharaPrm> classParameters;
            try
            {
                classParameters = GameMgr.GetIns().GetDataMgr().GetClassPrmDictionary();
            }
            catch
            {
                return;
            }

            if (classParameters == null)
            {
                return;
            }

            foreach (KeyValuePair<int, LocalLeaderSkinSetting> pair in settings.LeaderSkins)
            {
                LocalLeaderSkinSetting leaderSetting = pair.Value;
                if (leaderSetting == null ||
                    !classParameters.TryGetValue(pair.Key, out ClassCharaPrm classParameter) ||
                    classParameter == null)
                {
                    continue;
                }

                if (leaderSetting.CurrentCharaId > 0)
                {
                    classParameter.SetCurrentCharaId(leaderSetting.CurrentCharaId);
                }
                classParameter.IsRandomLeaderSkin = leaderSetting.IsRandom;
                if (classParameter.LeaderSkinIdList == null)
                {
                    continue;
                }
                classParameter.LeaderSkinIdList.Clear();
                if (leaderSetting.SkinIds != null)
                {
                    classParameter.LeaderSkinIdList.AddRange(
                        leaderSetting.SkinIds.Where(id => id > 0).Distinct());
                }
            }
        }

        private static LocalProfileSettings LoadSettingsUnlocked()
        {
            if (!File.Exists(PathHelper.ProfileSettingsPath))
            {
                return new LocalProfileSettings();
            }
            try
            {
                LocalProfileSettings settings = JsonConvert.DeserializeObject<LocalProfileSettings>(
                    File.ReadAllText(PathHelper.ProfileSettingsPath, Encoding.UTF8));
                if (settings == null)
                {
                    return new LocalProfileSettings();
                }
                settings.LeaderSkins = settings.LeaderSkins ??
                    new Dictionary<int, LocalLeaderSkinSetting>();
                return settings;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[ProfileOffline] Ignored invalid local profile settings: {ex.Message}");
                return new LocalProfileSettings();
            }
        }
    }
}
