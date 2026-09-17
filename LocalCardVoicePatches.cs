using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;
using Wizard;
using Wizard.Battle.View;

namespace Shadowbus
{
    /// <summary>
    /// 卡牌语音文件补丁：把卡自己文件夹里的本地音频当这张卡的语音来播放。
    /// 每个字段都是相对卡文件夹的路径，留空表示该时点不覆盖。
    /// </summary>
    public class CardVoiceFilePatch
    {
        /// <summary>出场语音。</summary>
        public string play;
        /// <summary>进化语音。</summary>
        public string evolve;
        /// <summary>攻击语音。</summary>
        public string attack;
        /// <summary>进化后攻击语音。</summary>
        public string evolvedAttack;
        /// <summary>被破坏语音。</summary>
        public string destroy;
        /// <summary>进化后被破坏语音。</summary>
        public string evolvedDestroy;
        /// <summary>技能语音，按顺序对应技能槽位。</summary>
        public string[] skills;
        /// <summary>进化后技能语音。</summary>
        public string[] evolvedSkills;
    }

    /// <summary>
    /// 本地卡牌语音。游戏只为卡牌自己的语音库加载 ACB，本地音频不在其中，
    /// 所以这里自己维护一套：
    ///   · ApplyVoiceFiles 把补丁里的文件路径登记进 AssetsByCue，
    ///     并写出这个卡牌会去查找的 cue 名；
    ///   · BattleCardView / SoundMgr 的补丁在播放前拦截，命中本地 cue 就自己播；
    ///   · 音频在预加载阶段解好、按当前音量缩放好并固定住，播放时只剩一次系统调用。
    ///
    /// 为什么不用 Unity 的音频：这个工程的 Unity 音频在项目设置里就是关掉的
    /// （游戏音频全部由 CRIWARE/ADX2 原生输出），日志里会看到
    /// "Audio system is disabled" 和 "AudioClip.SetData failed"。
    /// 所以本地音频统一走 Windows 原生输出（见 NativeWavPlayer），只有 WAV 能播。
    /// </summary>
    public static class LocalCardVoicePatches
    {
        private sealed class LocalVoiceAsset
        {
            public string RelativePath;
            public string FullPath;
            public AudioType AudioType;
            public bool IsLoading;
            public bool HasFailed;
            /// <summary>预缩放、已固定的原生音频缓冲。</summary>
            public NativeWavClip NativeClip;
        }

        private static readonly Dictionary<string, AudioType> SupportedAudioTypes =
            new Dictionary<string, AudioType>(StringComparer.OrdinalIgnoreCase)
            {
                { ".wav", AudioType.WAV },
                { ".mp3", AudioType.MPEG },
                { ".ogg", AudioType.OGGVORBIS },
                { ".aif", AudioType.AIFF },
                { ".aiff", AudioType.AIFF }
            };

        private static readonly Dictionary<string, LocalVoiceAsset> AssetsByPath =
            new Dictionary<string, LocalVoiceAsset>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, LocalVoiceAsset> AssetsByCue =
            new Dictionary<string, LocalVoiceAsset>(StringComparer.OrdinalIgnoreCase);
        // 被本地音频接管的 cue sheet 名。游戏的资源加载列表里没有这些 ACB，
        // 所以要按名字把它们剔出去，否则加载会失败。
        private static readonly HashSet<string> LocalCueSheetIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Warnings =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 音频还没准备好时被请求过的 cue，准备好之后补播。
        private static readonly HashSet<LocalVoiceAsset> PendingAssets = [];
        // 每次 CardMaster 重载都会 +1，用来丢弃仍在加载中的上一次结果。
        private static int RegistryGeneration;

        /// <summary>
        /// CardMaster 重载时清空全部状态，并释放已加载的音频。
        /// </summary>
        public static void Clear()
        {
            RegistryGeneration++;
            NativeWavPlayer.Stop();
            PendingAssets.Clear();

            foreach (LocalVoiceAsset asset in AssetsByPath.Values)
            {
                if (asset.NativeClip != null)
                {
                    asset.NativeClip.Dispose();
                    asset.NativeClip = null;
                }
            }

            AssetsByPath.Clear();
            AssetsByCue.Clear();
            LocalCueSheetIds.Clear();
            Warnings.Clear();
        }

        /// <summary>
        /// 把补丁声明的本地语音文件写入卡牌对应的语音字段。
        /// sourceLabel 只用于日志，形如 "补丁文件名:卡号"。
        /// cardFolder 是这张卡自己的文件夹（Mods/CardMaster/&lt;卡文件夹&gt;），
        /// 所有语音路径都相对于它。
        /// 补丁里没写某个时点时，会去卡文件夹里找约定文件名（play.wav / attack.wav ...），
        /// 所以卡文件夹放齐了音频就可以完全不写 voiceFiles；名字不叫约定名时才需要写。
        /// </summary>
        public static void ApplyVoiceFiles(
            CardParameter card,
            CardVoiceFilePatch patch,
            string sourceLabel,
            string cardFolder)
        {
            if (card == null)
            {
                return;
            }

            string play = ResolveVoiceSource(patch?.play, cardFolder, "play");
            string evolve = ResolveVoiceSource(patch?.evolve, cardFolder, "evolve");
            string attack = ResolveVoiceSource(patch?.attack, cardFolder, "attack");
            string evolvedAttack = ResolveVoiceSource(patch?.evolvedAttack, cardFolder, "attack_evolved");
            string destroy = ResolveVoiceSource(patch?.destroy, cardFolder, "destroy");
            string evolvedDestroy = ResolveVoiceSource(patch?.evolvedDestroy, cardFolder, "destroy_evolved");
            string[] skills = patch?.skills;
            string[] evolvedSkills = patch?.evolvedSkills;

            if (play == null && evolve == null && attack == null && evolvedAttack == null &&
                destroy == null && evolvedDestroy == null && skills == null && evolvedSkills == null)
            {
                return;
            }

            string cueSheetId = GetExistingVoiceCueSheetId(card);
            // True when the card had no voice bank of its own, so the id below is one we
            // made up ("sbv<cardId>"). Only invented ids join LocalCueSheetIds: the game
            // has no ACB for them and would fail to load one, whereas a reused real id
            // still has its own bank that other cues depend on.
            bool inventedCueSheet = string.IsNullOrEmpty(cueSheetId);
            if (inventedCueSheet)
            {
                cueSheetId = string.Format("sbv{0}", card.CardId);
            }

            if (play != null)
            {
                card.PlayVoice = RegisterVoiceFile(
                    card.CardId, cueSheetId, inventedCueSheet, "play", play, sourceLabel, cardFolder);
            }

            if (evolve != null)
            {
                card.EvoVoice = RegisterVoiceFile(
                    card.CardId, cueSheetId, inventedCueSheet, "evolve", evolve, sourceLabel, cardFolder);
            }

            if (attack != null || evolvedAttack != null)
            {
                card.AtkVoice = MergeVoicePair(
                    card.AtkVoice,
                    attack != null,
                    RegisterOptionalVoiceFile(
                        card.CardId, cueSheetId, inventedCueSheet, "attack", attack, sourceLabel, cardFolder),
                    evolvedAttack != null,
                    RegisterOptionalVoiceFile(
                        card.CardId, cueSheetId, inventedCueSheet, "attack_evolved", evolvedAttack, sourceLabel, cardFolder));
            }

            if (destroy != null || evolvedDestroy != null)
            {
                card.DestroyVoice = MergeVoicePair(
                    card.DestroyVoice,
                    destroy != null,
                    RegisterOptionalVoiceFile(
                        card.CardId, cueSheetId, inventedCueSheet, "destroy", destroy, sourceLabel, cardFolder),
                    evolvedDestroy != null,
                    RegisterOptionalVoiceFile(
                        card.CardId, cueSheetId, inventedCueSheet, "destroy_evolved", evolvedDestroy, sourceLabel, cardFolder));
            }

            if (skills != null || evolvedSkills != null)
            {
                card.SkillVoice = MergeVoicePair(
                    card.SkillVoice,
                    skills != null,
                    RegisterVoiceFileList(
                        card.CardId, cueSheetId, inventedCueSheet, "skill", skills, sourceLabel, cardFolder),
                    evolvedSkills != null,
                    RegisterVoiceFileList(
                        card.CardId, cueSheetId, inventedCueSheet, "skill_evolved", evolvedSkills, sourceLabel, cardFolder));
            }
        }

        /// <summary>
        /// 取某个时点要用的音频路径：补丁里声明了就用声明的；
        /// 没声明时看卡文件夹里有没有约定文件名（如 play.wav），有就自动用上。
        /// 都没有返回 null，表示这个时点不覆盖。
        /// </summary>
        private static string ResolveVoiceSource(string declared, string cardFolder, string form)
        {
            if (!string.IsNullOrWhiteSpace(declared))
            {
                return declared;
            }

            if (string.IsNullOrEmpty(cardFolder))
            {
                return null;
            }

            string conventional = form + ".wav";
            return File.Exists(Path.Combine(cardFolder, conventional)) ? conventional : null;
        }

        /// <summary>
        /// 在全部补丁应用完之后调用，把所有登记过但还没加载的音频排进加载队列。
        /// </summary>
        public static void BeginPreload()
        {
            if (Plugin.Instance == null)
            {
                WarnOnce(
                    "missing-plugin",
                    "[CardVoice] Cannot preload local card voices: plugin instance is unavailable.");
                return;
            }

            // 预热 winmm：第一次 PlaySound 要初始化音频设备，会有几十毫秒开销。
            // 现在放一段听不见的静音先把它做掉，真正播放时就不会顿。
            NativeWavPlayer.WarmUp();

            int generation = RegistryGeneration;
            foreach (LocalVoiceAsset asset in AssetsByPath.Values)
            {
                if (asset.IsLoading || asset.HasFailed || asset.NativeClip != null)
                {
                    continue;
                }

                asset.IsLoading = true;
                Plugin.Instance.StartCoroutine(PrepareAsset(asset, generation));
            }
        }

        /// <summary>
        /// 从资源加载列表中剔除本地 cue sheet：它们没有对应的 ACB 文件。
        /// </summary>
        public static void RemoveLocalVoiceResourcePaths(List<string> paths)
        {
            if (paths == null || paths.Count == 0 || LocalCueSheetIds.Count == 0)
            {
                return;
            }

            paths.RemoveAll(IsLocalVoiceCueSheet);
        }

        private static string RegisterOptionalVoiceFile(
            int cardId,
            string cueSheetId,
            bool inventedCueSheet,
            string form,
            string relativePath,
            string sourceLabel,
            string cardFolder)
        {
            return relativePath != null
                ? RegisterVoiceFile(cardId, cueSheetId, inventedCueSheet, form, relativePath, sourceLabel, cardFolder)
                : null;
        }

        private static string RegisterVoiceFileList(
            int cardId,
            string cueSheetId,
            bool inventedCueSheet,
            string form,
            IReadOnlyList<string> relativePaths,
            string sourceLabel,
            string cardFolder)
        {
            if (relativePaths == null || relativePaths.Count == 0)
            {
                return string.Empty;
            }

            string[] cues = new string[relativePaths.Count];
            for (int i = 0; i < relativePaths.Count; i++)
            {
                cues[i] = RegisterVoiceFile(
                    cardId, cueSheetId, inventedCueSheet, form + i, relativePaths[i], sourceLabel, cardFolder);
            }

            return string.Join(",", cues);
        }

        /// <summary>
        /// 登记一个本地音频文件，返回该卡牌字段应写入的 cue 名。失败时返回空串。
        /// </summary>
        private static string RegisterVoiceFile(
            int cardId,
            string cueSheetId,
            bool inventedCueSheet,
            string form,
            string relativePath,
            string sourceLabel,
            string cardFolder)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return string.Empty;
            }

            if (!TryResolveVoicePath(
                    relativePath, cardFolder, out string fullPath, out AudioType audioType, out string error))
            {
                WarnOnce(
                    string.Concat("invalid:", sourceLabel, ":", form, ":", relativePath),
                    string.Concat("[CardVoice] ", sourceLabel, " ", form, ": ", error));
                return string.Empty;
            }

            if (!File.Exists(fullPath))
            {
                WarnOnce(
                    "missing:" + fullPath,
                    string.Concat(
                        "[CardVoice] ", sourceLabel, " ", form, ": file not found: ", relativePath,
                        " (relative to the card folder)"));
                return string.Empty;
            }

            if (!AssetsByPath.TryGetValue(fullPath, out LocalVoiceAsset asset))
            {
                asset = new LocalVoiceAsset
                {
                    RelativePath = relativePath,
                    FullPath = fullPath,
                    AudioType = audioType
                };
                AssetsByPath.Add(fullPath, asset);
            }

            string cueName = string.Format("{0}_shadowbus_{1}_{2}", cueSheetId, cardId, form);
            // 登记的 key 必须和查询时用同一套归一化：游戏会把 cue 名拼成 "vo_<字段值>"，
            // 查询侧用 NormalizeCueName 去掉这个前缀，这里若原样存下去就对不上了。
            AssetsByCue[NormalizeCueName(cueName)] = asset;

            if (inventedCueSheet)
            {
                LocalCueSheetIds.Add(cueSheetId);
            }

            return cueName;
        }

        /// <summary>
        /// 从卡牌现有的语音字段里推断它原本使用的 cue sheet 名。
        /// 找不到时返回 null，调用方会改用自建名。
        /// </summary>
        private static string GetExistingVoiceCueSheetId(CardParameter card)
        {
            string[] sources =
            {
                GetVoiceForm(card.PlayVoice, 0),
                GetVoiceForm(card.EvoVoice, 0),
                GetVoiceForm(card.AtkVoice, 0),
                GetVoiceForm(card.AtkVoice, 1),
                GetVoiceForm(card.DestroyVoice, 0),
                GetVoiceForm(card.DestroyVoice, 1),
                GetVoiceForm(card.SkillVoice, 0),
                GetVoiceForm(card.SkillVoice, 1)
            };

            for (int i = 0; i < sources.Length; i++)
            {
                string source = sources[i];
                if (string.IsNullOrEmpty(source))
                {
                    continue;
                }

                string[] entries = source.Split(',');
                for (int j = 0; j < entries.Length; j++)
                {
                    string entry = entries[j];
                    int braceIndex = entry.IndexOf('{');
                    if (braceIndex >= 0)
                    {
                        // 形如 "vo_123456_1{skill}" 的写法，取花括号之前的部分。
                        entry = entry.Substring(0, braceIndex).Trim();
                    }

                    if (string.IsNullOrEmpty(entry) ||
                        string.Equals(entry, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int underscore = entry.IndexOf('_');
                    return underscore > 0 ? entry.Substring(0, underscore) : entry;
                }
            }

            return null;
        }

        /// <summary>
        /// 取语音字段的普通/进化两种形态。"a//b" 里 0 是普通，1 是进化。
        /// </summary>
        private static string GetVoiceForm(string voice, int index)
        {
            if (string.IsNullOrEmpty(voice))
            {
                return string.Empty;
            }

            string[] forms = voice.Split(new[] { "//" }, StringSplitOptions.None);
            return index >= forms.Length ? string.Empty : forms[index];
        }

        /// <summary>
        /// 把相对路径解析成卡文件夹下的绝对路径并校验音频格式。
        /// 路径不允许是绝对路径，也不允许跳出卡文件夹。
        /// </summary>
        private static bool TryResolveVoicePath(
            string relativePath,
            string cardFolder,
            out string fullPath,
            out AudioType audioType,
            out string error)
        {
            fullPath = null;
            audioType = 0;
            error = null;

            string extension = Path.GetExtension(relativePath);
            if (!SupportedAudioTypes.TryGetValue(extension, out audioType))
            {
                error = string.Concat(
                    "unsupported audio extension '", extension,
                    "'; use WAV, MP3, OGG, AIF or AIFF");
                return false;
            }

            return ModCardAssets.TryResolveRelativePath(
                relativePath, cardFolder, out fullPath, out error);
        }

        /// <summary>
        /// 合并普通/进化两个形态的替换结果，保留未替换的那一侧。
        /// </summary>
        private static string MergeVoicePair(
            string current,
            bool replaceNormal,
            string normalReplacement,
            bool replaceEvolved,
            string evolvedReplacement)
        {
            string[] forms = (current ?? string.Empty).Split(new[] { "//" }, StringSplitOptions.None);

            string normal = replaceNormal ? normalReplacement ?? string.Empty : forms[0];
            string evolved = replaceEvolved
                ? evolvedReplacement ?? string.Empty
                : (forms.Length > 1 ? forms[1] : string.Empty);

            if (!replaceEvolved && forms.Length <= 1)
            {
                return normal;
            }

            return normal + "//" + evolved;
        }

        /// <summary>
        /// 预加载阶段把本地音频准备好：读文件、解 WAV、按当前音量缩放、固定缓冲。
        /// 播放那一帧就只剩一次 PlaySound，不会卡住出牌动画。
        /// generation 与当前值不一致时说明期间发生过重载，直接丢弃结果。
        /// </summary>
        private static IEnumerator PrepareAsset(LocalVoiceAsset asset, int generation)
        {
            // 让出一帧，避免在一帧里把整批音频都解完。
            yield return null;

            if (generation != RegistryGeneration)
            {
                yield break;
            }

            asset.IsLoading = false;

            if (!NativeWavPlayer.IsSupported)
            {
                asset.HasFailed = true;
                WarnOnce(
                    "platform:" + asset.FullPath,
                    "[CardVoice] Local card voices need Windows native playback; " +
                    "this platform is not supported, so '" + asset.RelativePath + "' stays silent.");
                PendingAssets.Remove(asset);
                yield break;
            }

            if (asset.AudioType != AudioType.WAV)
            {
                // 压缩格式没法自己解，而 Unity 的音频在本工程里是关掉的，试也没用。
                asset.HasFailed = true;
                WarnOnce(
                    "format:" + asset.RelativePath,
                    string.Concat(
                        "[CardVoice] '", asset.RelativePath, "' is not a WAV. This game has Unity ",
                        "audio disabled, so only WAV can be played — re-encode it as 16-bit PCM WAV."));
                PendingAssets.Remove(asset);
                yield break;
            }

            string readError;
            byte[] raw = WavDecoder.ReadAllBytes(asset.FullPath, out readError);
            if (raw == null)
            {
                asset.HasFailed = true;
                WarnOnce(
                    "read:" + asset.FullPath,
                    string.Concat(
                        "[CardVoice] Failed to read '", asset.RelativePath, "': ", readError));
                PendingAssets.Remove(asset);
                yield break;
            }

            string prepareError;
            NativeWavClip nativeClip = NativeWavPlayer.Prepare(raw, GetVoiceVolumeSafe(), out prepareError);
            if (nativeClip == null)
            {
                asset.HasFailed = true;
                WarnOnce(
                    "decode:" + asset.FullPath,
                    string.Concat(
                        "[CardVoice] Failed to decode '", asset.RelativePath, "': ", prepareError));
                PendingAssets.Remove(asset);
                yield break;
            }

            asset.NativeClip = nativeClip;
            asset.HasFailed = false;

            if (PendingAssets.Remove(asset))
            {
                // 这期间已经有人请求过它了，补播。
                PlayNativeVoice(asset);
            }
        }


        /// <summary>
        /// 播放本地语音。返回 false 表示不是本地 cue，调用方应继续走原逻辑；
        /// 返回 true 表示已经接管（包括音频还在准备、已经失败等情况）。
        /// </summary>
        private static bool TryPlayLocalVoice(string cueName)
        {
            string key = NormalizeCueName(cueName);
            if (!AssetsByCue.TryGetValue(key, out LocalVoiceAsset asset))
            {
                return false;
            }

            if (asset.NativeClip != null)
            {
                PlayNativeVoice(asset);
            }
            else if (!asset.HasFailed && !asset.IsLoading)
            {
                // 还没准备好：登记下来，PrepareAsset 完成时会补播。
                PendingAssets.Add(asset);

                if (Plugin.Instance != null)
                {
                    asset.IsLoading = true;
                    Plugin.Instance.StartCoroutine(PrepareAsset(asset, RegistryGeneration));
                }
            }

            return true;
        }

        private static bool HasLocalVoiceCue(string cueName)
        {
            return AssetsByCue.ContainsKey(NormalizeCueName(cueName));
        }

        /// <summary>
        /// 走 winmm 原生播放（Unity 音频不可用时的回退）。
        /// 音量没有通道可调，所以在样本上预先缩放；静音时不发声。
        /// </summary>
        private static void PlayNativeVoice(LocalVoiceAsset asset)
        {
            if (IsRejectingNewSound())
            {
                return;
            }

            float volume = GetVoiceVolumeSafe();
            bool muted = IsVoiceMutedSafe();
            NativeWavPlayer.Play(asset.NativeClip, volume, muted);
        }

        private static float GetVoiceVolumeSafe()
        {
            try
            {
                SoundMgr soundMgr = GameMgr.GetIns()?.GetSoundMgr();
                if (soundMgr != null)
                {
                    return soundMgr.GetVoiceVolume();
                }
            }
            catch (Exception)
            {
            }

            return 1f;
        }

        private static bool IsVoiceMutedSafe()
        {
            try
            {
                SoundMgr soundMgr = GameMgr.GetIns()?.GetSoundMgr();
                if (soundMgr != null)
                {
                    return soundMgr.IsVoiceMuted();
                }
            }
            catch (Exception)
            {
            }

            return false;
        }

        /// <summary>
        /// 游戏查找 cue 时会带 "vo_" 前缀，登记时没有，这里统一去掉。
        /// </summary>
        private static string NormalizeCueName(string cueName)
        {
            if (string.IsNullOrEmpty(cueName))
            {
                return string.Empty;
            }

            return cueName.StartsWith("vo_", StringComparison.OrdinalIgnoreCase)
                ? cueName.Substring(3)
                : cueName;
        }

        /// <summary>
        /// 判断一个资源路径是不是被本地语音接管的 cue sheet。
        /// 传入的可能是 "v/vo_xxx.acb" 这类路径，所以先取文件名再去掉扩展名。
        /// </summary>
        private static bool IsLocalVoiceCueSheet(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            string name = path.Replace('\\', '/');
            int separator = name.LastIndexOf('/');
            if (separator >= 0)
            {
                name = name.Substring(separator + 1);
            }

            if (name.EndsWith(".acb", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - 4);
            }

            if (name.StartsWith("vo_", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(3);
            }

            return LocalCueSheetIds.Contains(name);
        }

        private static bool IsRejectingNewSound()
        {
            try
            {
                SoundMgr soundMgr = GameMgr.GetIns()?.GetSoundMgr();
                if (soundMgr != null)
                {
                    return soundMgr.IsRejectNewSound();
                }
            }
            catch (Exception)
            {
            }

            return false;
        }

        private static void WarnOnce(string key, string message)
        {
            if (Warnings.Add(key))
            {
                Plugin.Logger.LogWarning(message);
            }
        }

        // 注意：下面这些 Prefix/Postfix 的「实参」形参名由 Harmony 按**参数名**与游戏方法
        // 的元数据匹配，名字对不上会抛 Parameter "xxx" not found 并导致整条 patch 挂不上。
        // 因此它们必须逐字等于 Assembly-CSharp 里的形参名（哪怕拼写不规范），不要为了
        // 好看而改写。改动用 _reverse/ParamDump.exe 核对：
        //   ParamDump.exe <Assembly-CSharp.dll> <方法名清单>
        [HarmonyPatch(typeof(BattleCardView), nameof(BattleCardView.PlayVoice))]
        [HarmonyPrefix]
        public static bool BattleCardView_PlayVoice_Prefix(string voiceName)
        {
            return !TryPlayLocalVoice(voiceName);
        }

        [HarmonyPatch(typeof(BattleCardView), nameof(BattleCardView.StopVoice))]
        [HarmonyPostfix]
        public static void BattleCardView_StopVoice_Postfix()
        {
            NativeWavPlayer.Stop();
        }

        [HarmonyPatch(typeof(SoundMgr), nameof(SoundMgr.PlayVoiceScenario))]
        [HarmonyPrefix]
        public static bool SoundMgr_PlayVoiceScenario_Prefix(SoundMgr __instance, string cuename, float fadeout)
        {
            if (!HasLocalVoiceCue(cuename))
            {
                // 剧情语音要开口了，先掐掉正在播的卡牌语音，免得叠在一起。
                NativeWavPlayer.Stop();
                return true;
            }

            __instance.StopVoiceAll(fadeout);
            return !TryPlayLocalVoice(cuename);
        }

        [HarmonyPatch(typeof(SoundMgr), nameof(SoundMgr.LoadVoice), new[] { typeof(string), typeof(Action) })]
        [HarmonyPrefix]
        public static bool SoundMgr_LoadVoice_Prefix(string cueSheet, Action onLoaded, ref string __result)
        {
            if (!IsLocalVoiceCueSheet(cueSheet))
            {
                return true;
            }

            // 本地 cue sheet 没有对应的 ACB，直接当成已加载。
            __result = cueSheet;
            onLoaded?.Invoke();
            return false;
        }

        [HarmonyPatch(typeof(SoundMgr), nameof(SoundMgr.UnloadVoice), new[] { typeof(string) })]
        [HarmonyPrefix]
        public static bool SoundMgr_UnloadVoice_Prefix(string cueSheet)
        {
            return !IsLocalVoiceCueSheet(cueSheet);
        }

        [HarmonyPatch(typeof(SoundMgr), nameof(SoundMgr.StopVoice))]
        [HarmonyPostfix]
        public static void SoundMgr_StopVoice_Postfix()
        {
            NativeWavPlayer.Stop();
        }

        [HarmonyPatch(typeof(SoundMgr), nameof(SoundMgr.StopVoiceAll))]
        [HarmonyPostfix]
        public static void SoundMgr_StopVoiceAll_Postfix()
        {
            NativeWavPlayer.Stop();
        }

        [HarmonyPatch(typeof(SoundMgr), nameof(SoundMgr.IsVoicePlaying))]
        [HarmonyPostfix]
        public static void SoundMgr_IsVoicePlaying_Postfix(ref bool __result)
        {
            __result = __result || NativeWavPlayer.IsPlaying;
        }
    }
}
