using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Cute;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Wizard;

namespace Shadowbus
{
    /// <summary>
    /// 把国服独占的主战者（地区独占联动皮肤）补进国际服的角色表。
    ///
    /// 数据在 <c>&lt;资源根&gt;/LeaderSkins/imported_leaders.json</c>：每个角色带国服
    /// <c>class_chara_master</c> 的原始 20 列 + 名字（简/繁/英）+ 可用职业列表。
    /// 国服里职业是 99（<c>ClanType.SHADOW</c>，国际服枚举里本来就有）的那几个是"中立"，
    /// 国际服的主战者列表是按 <c>class_id</c> 过滤的，所以中立皮肤按 1~8 各注入一行
    /// （skin_id 相同、class_id 不同，游戏里没有"主战者职业必须等于卡组职业"的校验）。
    ///
    /// 素材：缩略图/按钮图/卡组立绘/立绘/胜负/资料图走 <see cref="LeaderSkinAssetFallback"/>
    /// 的补图机制（<c>&lt;资源根&gt;/LeaderSkins/&lt;皮肤号&gt;</c>）；战斗 spine 是另外做好的
    /// 2020 版包（壳 = 国际服 skeleton 包，内容换成国服骨骼/图集/贴图）。
    /// </summary>
    internal static class ImportedLeaders
    {
        private const string ResourceName = "imported_leaders.json";

        /// <summary>名字 key → { 简, 繁, 英 }。</summary>
        private static readonly Dictionary<string, string[]> Names = new Dictionary<string, string[]>(StringComparer.Ordinal);

        /// <summary>台词 key（ET_...）→ { 简, 繁, 英, 日 }。</summary>
        private static readonly Dictionary<string, string[]> EmoteLines = new Dictionary<string, string[]>(StringComparer.Ordinal);

        /// <summary>主战者号 → 皮肤号：素材按皮肤号命名，界面有时按主战者号取图。</summary>
        private static readonly Dictionary<int, int> CharaToSkin = new Dictionary<int, int>();

        private static bool _loaded;
        private static bool _warned;
        private static bool _hashesRegistered;
        private static bool _assetsRegistered;
        private static bool _missingReported;
        private static int _missingCheckTick;

        /// <summary>主战者号换成皮肤号（不是我们导入的就原样返回）。</summary>
        internal static int ResolveSkinId(int id)
        {
            EnsureLoaded();
            return CharaToSkin.TryGetValue(id, out int skin) ? skin : id;
        }

        private static string ConfigurationPath =>
            Path.Combine(PathHelper.LeaderSkinsPath, ResourceName);

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            string path = ConfigurationPath;
            if (!File.Exists(path))
            {
                // 资源根还没解析出来时 LeaderSkins 会退回 <游戏>/Mods，所以找不到不能就此
                // 认定"没有这份数据"——只按秒节流重试，等资源目录接上了再读。
                if (string.IsNullOrEmpty(ResourceRootPatches.ResourceRoot))
                {
                    return;
                }

                int now = Environment.TickCount;
                if (!_missingReported || unchecked(now - _missingCheckTick) > 2000)
                {
                    _missingCheckTick = now;
                    if (!_missingReported)
                    {
                        _missingReported = true;
                        Plugin.Logger.LogWarning($"[Import] '{path}' not found; the imported leaders are off.");
                    }
                }

                return;
            }

            _loaded = true;
            try
            {
                JObject root = JObject.Parse(File.ReadAllText(path));
                if (root["emote_text"] is JObject emoteText)
                {
                    foreach (KeyValuePair<string, JToken> pair in emoteText)
                    {
                        EmoteLines[pair.Key] = new[]
                        {
                            (string)pair.Value["chs"] ?? string.Empty,
                            (string)pair.Value["cht"] ?? string.Empty,
                            (string)pair.Value["eng"] ?? string.Empty,
                            (string)pair.Value["jpn"] ?? string.Empty,
                        };
                    }
                }

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
                RegisterLocalHashes();
                RegisterLocalAssets();
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Import] Could not read '{ConfigurationPath}': {exception.Message}");
            }
        }

        /// <summary>
        /// 把移植进来的文件注册成"游戏认得的资产"。
        ///
        /// 游戏判断一个资源能不能加载是 <c>AssetManager.IsEnableAssetName(name)</c> →
        /// <c>handleDictionary.ContainsKey(name)</c>，而这张表是从素材清单建的；我们直接塞进
        /// 资源目录的文件不在清单里，于是：**战斗 spine 包不加载（模型不显示）、表情表不加载
        /// （没语音）、语音包不认**。剧情本地语音早就在用同一招（<c>RegistHandle</c> +
        /// 本地 <c>AssetHandle</c>），这里照搬到主战者身上。
        /// </summary>
        private static void RegisterLocalAssets()
        {
            if (_assetsRegistered)
            {
                return;
            }

            try
            {
                Cute.AssetManager manager = Toolbox.AssetManager;
                string root = ResourceRootPatches.ResourceRoot;
                if (manager == null || string.IsNullOrEmpty(root) || Pending.Count == 0)
                {
                    return;
                }

                int registered = 0;
                foreach (string[] columns in Pending)
                {
                    int charaId = ParseInt(columns, 0, 0);
                    int skinId = ParseInt(columns, 7, 0);
                    if (charaId <= 0 || skinId <= 0)
                    {
                        continue;
                    }

                    registered += RegisterLocalAsset(manager, Toolbox.ResourcesManager.GetAssetTypePath(
                        skinId.ToString(CultureInfo.InvariantCulture),
                        ResourcesManager.AssetLoadPathType.ClassCharaSpine, false));

                    // 表情表故意**不**登记 master_emote_chara_*.unity3d：登记了游戏就会自己去加载
                    // 这个包并把表建出来，于是 StartLoadEmoteData 的后缀里我们的 CSV 表就被
                    // "已经有了"挡掉，语音反而没了。表一律由我们自己的 CSV 建，结果才是确定的。

                    string voiceDirectory = Path.Combine(root, "v");
                    if (Directory.Exists(voiceDirectory))
                    {
                        foreach (string file in Directory.GetFiles(voiceDirectory, $"vo_{charaId}_*.acb"))
                        {
                            registered += RegisterLocalAsset(manager, "v/" + Path.GetFileName(file));
                        }
                    }

                    registered += RegisterLocalAsset(manager, $"v/vo_char_select_{charaId}.acb");
                }

                int cues = RegisterVoiceCueSheets(manager, root);
                _assetsRegistered = true;
                if (registered > 0 || cues > 0)
                {
                    Plugin.Logger.LogInfo(
                        $"[Import] Registered {registered} local asset(s) and {cues} voice cue sheet(s).");
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Import] Could not register the local assets: {exception.Message}");
            }
        }

        /// <summary>
        /// 直接用 <c>&lt;资源根&gt;/LeaderSkins/&lt;皮肤号&gt;/emote_chara_&lt;角色号&gt;.csv</c> 建表情表。
        /// 这样不用管素材包能不能被游戏加载（那条路要进清单，很容易又踩坑），
        /// 表里有了皮肤号，开场/胜负/表情的语音就不会再抛异常，也会真的去播。
        /// </summary>
        private static int InjectEmotionTables(
            Dictionary<string, Dictionary<ClassCharaPrm.EmotionType, Emotion>> table,
            bool force = false)
        {
            int built = 0;
            foreach (string[] columns in Pending)
            {
                try
                {
                    int charaId = ParseInt(columns, 0, 0);
                    int skinId = ParseInt(columns, 7, 0);
                    if (charaId <= 0 || skinId <= 0)
                    {
                        continue;
                    }

                    string key = skinId.ToString(CultureInfo.InvariantCulture);
                    if (table.ContainsKey(key) && !force)
                    {
                        continue;
                    }

                    string path = Path.Combine(
                        PathHelper.LeaderSkinsPath,
                        skinId.ToString(CultureInfo.InvariantCulture),
                        $"emote_chara_{charaId}.csv");
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    var emotions = new Dictionary<ClassCharaPrm.EmotionType, Emotion>();
                    bool first = true;
                    foreach (string line in File.ReadLines(path))
                    {
                        if (first)
                        {
                            first = false;
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        string[] cells = line.Split(',');
                        if (cells.Length < 5)
                        {
                            continue;
                        }

                        var emotion = new Emotion(cells);
                        var type = (ClassCharaPrm.EmotionType)emotion.emotion_id;
                        if (!emotions.ContainsKey(type))
                        {
                            emotions[type] = emotion;
                        }
                    }

                    if (emotions.Count == 0)
                    {
                        continue;
                    }

                    table[key] = emotions;
                    built++;
                }
                catch (Exception exception)
                {
                    Plugin.Logger.LogWarning(
                        $"[Import] Could not build an emotion table from CSV: {exception.Message}");
                }
            }

            return built;
        }

        /// <summary>登记本地语音的 cue sheet（照剧情本地语音那套，幂等）。</summary>
        private static int RegisterVoiceCueSheets(Cute.AssetManager manager, string root)
        {
            int registered = 0;
            try
            {
                AudioManager audio = Toolbox.AudioManager;
                if (audio == null)
                {
                    return 0;
                }

                foreach (string[] columns in Pending)
                {
                    int charaId = ParseInt(columns, 0, 0);
                    if (charaId <= 0)
                    {
                        continue;
                    }

                    foreach (string name in VoiceFileNames(root, charaId))
                    {
                        AssetHandle handle = manager.GetAssetHandle(name, false);
                        if (handle == null)
                        {
                            continue;
                        }

                        if (audio.AddCueSheet(handle.filename, Path.GetFileName(handle.filename),
                                handle.directory, string.Empty))
                        {
                            registered++;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug($"[Import] Could not register the voice cue sheets: {exception.Message}");
            }

            return registered;
        }

        private static IEnumerable<string> VoiceFileNames(string root, int charaId)
        {
            var names = new List<string> { $"v/vo_char_select_{charaId}.acb" };
            try
            {
                string directory = Path.Combine(root, "v");
                if (Directory.Exists(directory))
                {
                    foreach (string file in Directory.GetFiles(directory, $"vo_{charaId}_*.acb"))
                    {
                        names.Add("v/" + Path.GetFileName(file));
                    }
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug($"[Import] Could not list the voice files of {charaId}: {exception.Message}");
            }

            return names;
        }

        /// <summary>
        /// 把一份本地文件登记进 <c>handleDictionary</c>，否则 <c>AssetManager.CacheAsset</c>
        /// 里 <c>handleDictionary.TryGetValue</c> 查不到就直接跳过，包永远加载不了
        /// （战斗里主战者不显示就是这个原因）。<c>AssetHandle</c> 的名字是"逻辑名"，
        /// 不是磁盘路径：素材包名 <c>ui_class_1110.unity3d</c> 会被 <c>BuildLocalCachePath</c>
        /// 拼到 <c>&lt;root&gt;/a/</c> 下，语音 <c>v/xxx.acb</c> 拼到 <c>&lt;root&gt;/v/</c> 下。
        /// 所以存在性检查必须用 <c>BuildLocalCachePath()</c> 的结果，不能直接拼 root。
        /// </summary>
        private static int RegisterLocalAsset(Cute.AssetManager manager, string name)
        {
            try
            {
                if (string.IsNullOrEmpty(name))
                {
                    return 0;
                }

                string normalized = name.Replace('\\', '/');
                if (manager.GetAssetHandle(normalized, false) != null)
                {
                    return 0;
                }

                string hash = string.Empty;
                try
                {
                    hash = manager.GetLocalDatahash(normalized) ?? string.Empty;
                }
                catch
                {
                }

                var handle = new AssetHandle(normalized, hash, null, null, null, null, false, false);
                string localPath = null;
                try
                {
                    localPath = handle.BuildLocalCachePath();
                }
                catch (Exception exception)
                {
                    Plugin.Logger.LogDebug($"[Import] '{normalized}': no local cache path ({exception.Message}).");
                }

                if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
                {
                    // 只有真的在磁盘上才登记；否则等于给游戏一个"存在但读不到"的句柄。
                    Plugin.Logger.LogDebug($"[Import] '{normalized}': not on disk at '{localPath}'.");
                    return 0;
                }

                if (manager.RegistHandle(normalized, handle))
                {
                    return 1;
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug($"[Import] Could not register '{name}': {exception.Message}");
            }

            return 0;
        }

        private static int ParseInt(string[] columns, int index, int fallback)
        {
            return index < columns.Length && int.TryParse(columns[index], out int value) ? value : fallback;
        }

        /// <summary>
        /// 我们塞进资源目录的文件（战斗 spine、表情表、语音）游戏并不知道，
        /// 用游戏自己的接口把本地哈希登记进去（<c>SaveLocalDatahash</c>），免得它当成"没下载"。
        /// 资源管理器还没起来时先跳过，下次再试。
        /// </summary>
        private static void RegisterLocalHashes()
        {
            if (_hashesRegistered)
            {
                return;
            }

            try
            {
                Cute.AssetManager manager = Toolbox.AssetManager;
                string root = ResourceRootPatches.ResourceRoot;
                if (manager == null || string.IsNullOrEmpty(root))
                {
                    return;
                }

                int registered = 0;
                foreach (string[] columns in Pending)
                {
                    int charaId = ParseInt(columns, 0, 0);
                    int skinId = ParseInt(columns, 7, 0);
                    if (charaId <= 0 || skinId <= 0)
                    {
                        continue;
                    }

                    // 表情表不走素材包（见 Master_StartLoadEmoteData_Postfix），所以只登记
                    // 战斗 spine 包和语音；素材名是裸包名，键必须和 AssetHandle 查的一致。
                    registered += Register(manager, root, $"a/ui_class_{skinId}.unity3d",
                        $"ui_class_{skinId}.unity3d");
                    registered += RegisterVoiceFiles(manager, root, charaId);
                }

                _hashesRegistered = true;
                if (registered > 0)
                {
                    Plugin.Logger.LogInfo($"[Import] Registered {registered} local asset hash(es).");
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug($"[Import] Could not register local asset hashes: {exception.Message}");
            }
        }

        /// <summary>
        /// 登记本地哈希（<c>SaveLocalDatahash</c>）。键必须是游戏的"逻辑素材名"
        /// （素材包是裸名 <c>ui_class_x.unity3d</c>，语音自带 <c>v/</c>），因为
        /// <c>AssetHandle.dataHash</c> 查的就是 <c>directory + filename</c>。
        /// 哈希和磁盘不一致时刷新 —— 我们重做过素材包，旧哈希不能留着。
        /// </summary>
        private static int Register(Cute.AssetManager manager, string root, string relative, string key)
        {
            try
            {
                string path = Path.Combine(root, relative);
                if (!File.Exists(path))
                {
                    return 0;
                }

                if (string.IsNullOrEmpty(key))
                {
                    key = relative.Replace('\\', '/');
                }

                using (var md5 = System.Security.Cryptography.MD5.Create())
                using (FileStream stream = File.OpenRead(path))
                {
                    string hash = string.Concat(md5.ComputeHash(stream).Select(b => b.ToString("x2")));
                    if (string.Equals(manager.GetLocalDatahash(key), hash, StringComparison.OrdinalIgnoreCase))
                    {
                        return 0;
                    }

                    manager.SaveLocalDatahash(key, hash);
                    return 1;
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug($"[Import] Could not register '{relative}': {exception.Message}");
                return 0;
            }
        }

        private static int RegisterVoiceFiles(Cute.AssetManager manager, string root, int charaId)
        {
            int count = 0;
            try
            {
                string directory = Path.Combine(root, "v");
                if (!Directory.Exists(directory))
                {
                    return 0;
                }

                foreach (string file in Directory.GetFiles(directory, $"vo_{charaId}_*.acb"))
                {
                    // 语音的素材名本身就带目录（v/xxx.acb），所以读文件和键都用同一个。
                    count += Register(manager, root, "v/" + Path.GetFileName(file), null);
                }

                // 选择主战者时的语音（vo_char_select_<角色号>.acb）
                count += Register(manager, root, $"v/vo_char_select_{charaId}.acb", null);
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug($"[Import] Could not register the voice files of {charaId}: {exception.Message}");
            }

            return count;
        }

        // 注意：EmoteWordDic / _emotionDic 都是"自动属性"（字段名带 <...>k__BackingField），
        // 用 AccessTools.Field 查不到 —— 之前那两行 Could not find field 就是这里来的。
        private static readonly System.Reflection.PropertyInfo EmoteWordDictionaryProperty =
            AccessTools.Property(typeof(Master), "EmoteWordDic")
            ?? AccessTools.DeclaredProperty(typeof(Master), "EmoteWordDic");

        /// <summary>
        /// 把国服的表情表补进 <c>_emotionDic</c>。
        ///
        /// 游戏建这张表的方式是：<c>遍历角色表 → CheckBeforeFileRequeset(清单里有才加载) →
        /// 预加载 → StartLoadEmoteData</c>。我们塞进去的主战者不在清单里，游戏会跳过它们；
        /// 表里没有这个皮肤号，开场语音就会 <c>KeyNotFoundException</c>、协程断掉、对局卡死。
        /// 所以这里直接用我们带过来的 CSV 自己建表（<c>&lt;资源根&gt;/LeaderSkins/&lt;皮肤号&gt;/emote_chara_*.csv</c>），
        /// 与素材包能不能被游戏加载完全无关。
        /// </summary>
        [HarmonyPatch(typeof(Master), nameof(Master.StartLoadEmoteData))]
        [HarmonyPostfix]
        private static void Master_StartLoadEmoteData_Postfix(Master __instance)
        {
            try
            {
                EnsureLoaded();
                Dictionary<string, Dictionary<ClassCharaPrm.EmotionType, Emotion>> table = __instance?._emotionDic;
                if (table == null || Pending.Count == 0)
                {
                    return;
                }

                // 一律重建（force）：CSV 才是我们验过的那份，谁先建谁赢会让语音时有时无。
                int built = InjectEmotionTables(table, true);
                if (built > 0)
                {
                    Plugin.Logger.LogInfo(
                        $"[Import] {built} imported leader emotion table(s) built from the bundled CSV " +
                        $"(total {table.Count}).");
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Import] Could not load the imported emotion tables: {exception.Message}");
            }
        }

        /// <summary>
        /// 表情台词表加载完之后，把国服那批台词（ET_..._&lt;角色号&gt;）补进去，
        /// 否则表情只有声音没有字。
        /// </summary>
        [HarmonyPatch(typeof(Master), nameof(Master.StartLoadEmoteMasterText))]
        [HarmonyPostfix]
        private static void Master_StartLoadEmoteMasterText_Postfix(Master __instance)
        {
            try
            {
                EnsureLoaded();
                if (EmoteLines.Count == 0 || __instance == null)
                {
                    return;
                }

                if (!(EmoteWordDictionaryProperty?.GetValue(__instance, null) is IDictionary<string, string> words))
                {
                    Plugin.Logger.LogWarning("[Import] Could not reach the emote word table; the lines stay as ids.");
                    return;
                }

                int added = 0;
                foreach (KeyValuePair<string, string[]> pair in EmoteLines)
                {
                    string text = StoryTextLanguagePatches.ResolveUiText(pair.Value[0], pair.Value[1], pair.Value[2]);
                    if (string.IsNullOrEmpty(text))
                    {
                        // 国服表的英文列是空的，英文界面就用日文原文兜。
                        text = pair.Value[3];
                    }

                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    words[pair.Key] = text;
                    added++;
                }

                Plugin.Logger.LogInfo($"[Import] {added} emote line(s) added to the text table.");

                // 表情对象是在 StartLoadEmoteData 时用 CSV 构造的，构造当时就会把 text_id
                // 拿去查台词表 —— 那时台词表还没好，于是存下来的是 "ET_xxx_1520" 这种 id。
                // 台词表补好之后把我们的表重建一遍，字幕才会是真正的台词。
                Dictionary<string, Dictionary<ClassCharaPrm.EmotionType, Emotion>> emotionTable =
                    __instance._emotionDic;
                if (emotionTable != null)
                {
                    int rebuilt = InjectEmotionTables(emotionTable, true);
                    if (rebuilt > 0)
                    {
                        Plugin.Logger.LogInfo(
                            $"[Import] Rebuilt {rebuilt} imported emotion table(s) so the lines resolve.");
                    }
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Import] Could not add the emote lines: {exception.Message}");
            }
        }

        /// <summary>
        /// 没有表情表的主战者（例如我们只搬了角色没搬语音的皮肤）播语音时，
        /// <c>DataMgr.GetEmotionDataBySkinId</c> 会 KeyNotFoundException，而它是在
        /// "换皮肤成功"的回调里调的 —— 一抛就把整个界面卡死。这里让语音直接跳过：
        /// <c>Voice.Play</c> 拿到空 cue 名本来就会 return。
        /// </summary>
        [HarmonyPatch(typeof(Voice), nameof(Voice.GetEmotionCueName),
            new[] { typeof(ClassCharaPrm.EmotionType), typeof(int), typeof(bool) })]
        [HarmonyPrefix]
        private static bool Voice_GetEmotionCueName_Prefix(int skinId, ref string __result)
        {
            if (HasEmotionData(skinId))
            {
                return true;
            }

            __result = string.Empty;
            return false;
        }

        private static readonly System.Reflection.PropertyInfo EmotionDictionaryProperty =
            AccessTools.Property(typeof(Master), "_emotionDic")
            ?? AccessTools.DeclaredProperty(typeof(Master), "_emotionDic");

        private static bool HasEmotionData(int skinId)
        {
            try
            {
                object table = EmotionDictionaryProperty?.GetValue(Data.Master, null) ?? Data.Master?._emotionDic;
                if (table is System.Collections.IDictionary dictionary)
                {
                    return dictionary.Contains(skinId.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug($"[Import] Could not inspect the emotion table: {exception.Message}");
            }

            // 查不到就当作"有数据"，不干扰正常流程。
            return true;
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
                // 角色表加载时资源管理器肯定起来了，这两步在这里再试一次（幂等）。
                RegisterLocalHashes();
                RegisterLocalAssets();
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
