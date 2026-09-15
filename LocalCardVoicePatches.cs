using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Networking;
using Wizard;
using Wizard.Battle.View;

namespace Shadowbus
{
    /// <summary>
    /// Local files for the voice slots exposed by CardParameter. Null fields
    /// preserve the current value; an empty string or array clears that slot.
    /// </summary>
    public sealed class CardVoiceFilePatch
    {
        public string play;
        public string evolve;
        public string attack;
        public string evolvedAttack;
        public string destroy;
        public string evolvedDestroy;
        public string[] skills;
        public string[] evolvedSkills;
    }

    /// <summary>
    /// Card voices normally use CRI ACB cue sheets. Local WAV/MP3/etc. files
    /// are loaded as Unity AudioClips and substituted only for generated cues.
    /// </summary>
    public static class LocalCardVoicePatches
    {
        private static readonly Dictionary<string, AudioType> SupportedAudioTypes =
            new Dictionary<string, AudioType>(StringComparer.OrdinalIgnoreCase)
            {
                [".wav"] = AudioType.WAV,
                [".mp3"] = AudioType.MPEG,
                [".ogg"] = AudioType.OGGVORBIS,
                [".aif"] = AudioType.AIFF,
                [".aiff"] = AudioType.AIFF
            };

        private static readonly Dictionary<string, LocalVoiceAsset> AssetsByPath =
            new Dictionary<string, LocalVoiceAsset>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, LocalVoiceAsset> AssetsByCue =
            new Dictionary<string, LocalVoiceAsset>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> LocalCueSheetIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Warnings =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConditionalWeakTable<BattleCardView, AudioSource>
            BattleSources = new ConditionalWeakTable<BattleCardView, AudioSource>();
        private static readonly List<WeakReference> KnownSources = new List<WeakReference>();
        private static readonly Dictionary<AudioSource, PendingPlayback> PendingBySource =
            new Dictionary<AudioSource, PendingPlayback>();

        private static AudioSource GlobalSource;
        private static int RegistryGeneration;

        private sealed class LocalVoiceAsset
        {
            public string RelativePath;
            public string FullPath;
            public AudioType AudioType;
            public AudioClip Clip;
            public bool IsLoading;
            public bool HasFailed;
        }

        private sealed class PendingPlayback
        {
            public LocalVoiceAsset Asset;
            public int Generation;
        }

        public static void Clear()
        {
            RegistryGeneration++;
            StopAllLocalVoices();
            PendingBySource.Clear();

            foreach (LocalVoiceAsset asset in AssetsByPath.Values)
            {
                if (asset.Clip != null)
                {
                    UnityEngine.Object.Destroy(asset.Clip);
                }
            }

            AssetsByPath.Clear();
            AssetsByCue.Clear();
            LocalCueSheetIds.Clear();
            Warnings.Clear();
        }

        public static void ApplyVoiceFiles(
            CardParameter card,
            CardVoiceFilePatch files,
            string sourceLabel)
        {
            if (card == null || files == null)
            {
                return;
            }

            string cueSheetId = GetExistingVoiceCueSheetId(card);
            bool usesVirtualCueSheet = string.IsNullOrEmpty(cueSheetId);
            if (usesVirtualCueSheet)
            {
                cueSheetId = $"sbv{card.CardId}";
            }

            if (files.play != null)
            {
                card.PlayVoice = RegisterVoiceFile(
                    card.CardId,
                    cueSheetId,
                    usesVirtualCueSheet,
                    "play",
                    files.play,
                    sourceLabel);
            }

            if (files.evolve != null)
            {
                card.EvoVoice = RegisterVoiceFile(
                    card.CardId,
                    cueSheetId,
                    usesVirtualCueSheet,
                    "evolve",
                    files.evolve,
                    sourceLabel);
            }

            if (files.attack != null || files.evolvedAttack != null)
            {
                card.AtkVoice = MergeVoicePair(
                    card.AtkVoice,
                    files.attack != null,
                    RegisterOptionalVoiceFile(
                        card.CardId,
                        cueSheetId,
                        usesVirtualCueSheet,
                        "attack",
                        files.attack,
                        sourceLabel),
                    files.evolvedAttack != null,
                    RegisterOptionalVoiceFile(
                        card.CardId,
                        cueSheetId,
                        usesVirtualCueSheet,
                        "attack_evolved",
                        files.evolvedAttack,
                        sourceLabel));
            }

            if (files.destroy != null || files.evolvedDestroy != null)
            {
                card.DestroyVoice = MergeVoicePair(
                    card.DestroyVoice,
                    files.destroy != null,
                    RegisterOptionalVoiceFile(
                        card.CardId,
                        cueSheetId,
                        usesVirtualCueSheet,
                        "destroy",
                        files.destroy,
                        sourceLabel),
                    files.evolvedDestroy != null,
                    RegisterOptionalVoiceFile(
                        card.CardId,
                        cueSheetId,
                        usesVirtualCueSheet,
                        "destroy_evolved",
                        files.evolvedDestroy,
                        sourceLabel));
            }

            if (files.skills != null || files.evolvedSkills != null)
            {
                card.SkillVoice = MergeVoicePair(
                    card.SkillVoice,
                    files.skills != null,
                    RegisterVoiceFileList(
                        card.CardId,
                        cueSheetId,
                        usesVirtualCueSheet,
                        "skill",
                        files.skills,
                        sourceLabel),
                    files.evolvedSkills != null,
                    RegisterVoiceFileList(
                        card.CardId,
                        cueSheetId,
                        usesVirtualCueSheet,
                        "skill_evolved",
                        files.evolvedSkills,
                        sourceLabel));
            }
        }

        public static void BeginPreload()
        {
            if (Plugin.Instance == null)
            {
                WarnOnce("missing-plugin", "[CardVoice] Cannot preload local card voices: plugin instance is unavailable.");
                return;
            }

            int generation = RegistryGeneration;
            foreach (LocalVoiceAsset asset in AssetsByPath.Values)
            {
                if (!asset.IsLoading && !asset.HasFailed && asset.Clip == null)
                {
                    asset.IsLoading = true;
                    Plugin.Instance.StartCoroutine(LoadAudioClip(asset, generation));
                }
            }
        }

        public static void RemoveLocalVoiceResourcePaths(List<string> resourcePaths)
        {
            if (resourcePaths == null || resourcePaths.Count == 0 || LocalCueSheetIds.Count == 0)
            {
                return;
            }

            resourcePaths.RemoveAll(IsLocalVoiceCueSheet);
        }

        private static string RegisterOptionalVoiceFile(
            int cardId,
            string cueSheetId,
            bool usesVirtualCueSheet,
            string slot,
            string relativePath,
            string sourceLabel)
        {
            return relativePath == null
                ? null
                : RegisterVoiceFile(
                    cardId,
                    cueSheetId,
                    usesVirtualCueSheet,
                    slot,
                    relativePath,
                    sourceLabel);
        }

        private static string RegisterVoiceFileList(
            int cardId,
            string cueSheetId,
            bool usesVirtualCueSheet,
            string slot,
            IReadOnlyList<string> relativePaths,
            string sourceLabel)
        {
            if (relativePaths == null || relativePaths.Count == 0)
            {
                return string.Empty;
            }

            string[] cues = new string[relativePaths.Count];
            for (int index = 0; index < relativePaths.Count; index++)
            {
                cues[index] = RegisterVoiceFile(
                    cardId,
                    cueSheetId,
                    usesVirtualCueSheet,
                    slot + index,
                    relativePaths[index],
                    sourceLabel);
            }
            return string.Join(",", cues);
        }

        private static string RegisterVoiceFile(
            int cardId,
            string cueSheetId,
            bool usesVirtualCueSheet,
            string slot,
            string relativePath,
            string sourceLabel)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return string.Empty;
            }

            if (!TryResolveVoicePath(relativePath, out string fullPath, out AudioType audioType, out string error))
            {
                WarnOnce(
                    $"invalid:{sourceLabel}:{slot}:{relativePath}",
                    $"[CardVoice] {sourceLabel} {slot}: {error}");
                return string.Empty;
            }

            if (!File.Exists(fullPath))
            {
                WarnOnce(
                    $"missing:{fullPath}",
                    $"[CardVoice] {sourceLabel} {slot}: file not found under Mods/CardVoices: {relativePath}");
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

            string cue = $"{cueSheetId}_shadowbus_{cardId}_{slot}";
            AssetsByCue[cue] = asset;
            if (usesVirtualCueSheet)
            {
                LocalCueSheetIds.Add(cueSheetId);
            }
            return cue;
        }

        private static string GetExistingVoiceCueSheetId(CardParameter card)
        {
            string[] voiceGroups =
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

            foreach (string voiceGroup in voiceGroups)
            {
                if (string.IsNullOrEmpty(voiceGroup))
                {
                    continue;
                }

                foreach (string entry in voiceGroup.Split(','))
                {
                    string voice = entry;
                    int waitTimeIndex = voice.IndexOf('{');
                    if (waitTimeIndex >= 0)
                    {
                        voice = voice.Substring(0, waitTimeIndex);
                    }
                    voice = voice.Trim();
                    if (string.IsNullOrEmpty(voice) ||
                        string.Equals(voice, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int separatorIndex = voice.IndexOf('_');
                    return separatorIndex > 0
                        ? voice.Substring(0, separatorIndex)
                        : voice;
                }
            }

            return null;
        }

        private static string GetVoiceForm(string value, int formIndex)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            string[] forms = value.Split(new[] { "//" }, StringSplitOptions.None);
            return formIndex < forms.Length ? forms[formIndex] : string.Empty;
        }

        private static bool TryResolveVoicePath(
            string relativePath,
            out string fullPath,
            out AudioType audioType,
            out string error)
        {
            fullPath = null;
            audioType = AudioType.UNKNOWN;
            error = null;

            try
            {
                if (Path.IsPathRooted(relativePath))
                {
                    error = "the path must be relative to Mods/CardVoices";
                    return false;
                }

                string extension = Path.GetExtension(relativePath);
                if (!SupportedAudioTypes.TryGetValue(extension, out audioType))
                {
                    error = $"unsupported audio extension '{extension}'; use WAV, MP3, OGG, AIF or AIFF";
                    return false;
                }

                string root = Path.GetFullPath(PathHelper.CardVoicePath);
                string rootPrefix = root.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
                if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    error = "the path leaves Mods/CardVoices";
                    fullPath = null;
                    return false;
                }
            }
            catch (Exception exception)
            {
                error = $"invalid path ({exception.Message})";
                return false;
            }

            return true;
        }

        private static string MergeVoicePair(
            string current,
            bool replaceNormal,
            string normalReplacement,
            bool replaceEvolved,
            string evolvedReplacement)
        {
            string[] currentParts = (current ?? string.Empty).Split(
                new[] { "//" },
                StringSplitOptions.None);
            string normal = replaceNormal ? normalReplacement ?? string.Empty : currentParts[0];
            string evolved = replaceEvolved
                ? evolvedReplacement ?? string.Empty
                : currentParts.Length > 1 ? currentParts[1] : string.Empty;
            return replaceEvolved || currentParts.Length > 1
                ? normal + "//" + evolved
                : normal;
        }

        private static IEnumerator LoadAudioClip(LocalVoiceAsset asset, int generation)
        {
            UnityWebRequest request;
            try
            {
                request = UnityWebRequestMultimedia.GetAudioClip(
                    new Uri(asset.FullPath).AbsoluteUri,
                    asset.AudioType);
            }
            catch (Exception exception)
            {
                asset.IsLoading = false;
                asset.HasFailed = true;
                WarnOnce(
                    $"load:{asset.FullPath}",
                    $"[CardVoice] Failed to open '{asset.RelativePath}': {exception.Message}");
                RemovePending(asset);
                yield break;
            }

            DownloadHandlerAudioClip handler = request.downloadHandler as DownloadHandlerAudioClip;
            if (handler != null)
            {
                handler.streamAudio = false;
            }

            UnityWebRequestAsyncOperation operation;
            try
            {
                operation = request.SendWebRequest();
            }
            catch (Exception exception)
            {
                asset.IsLoading = false;
                asset.HasFailed = true;
                WarnOnce(
                    $"load:{asset.FullPath}",
                    $"[CardVoice] Failed to load '{asset.RelativePath}': {exception.Message}");
                RemovePending(asset);
                request.Dispose();
                yield break;
            }

            yield return operation;

            if (generation != RegistryGeneration)
            {
                request.Dispose();
                yield break;
            }

            asset.IsLoading = false;
            if (request.result != UnityWebRequest.Result.Success)
            {
                asset.HasFailed = true;
                WarnOnce(
                    $"load:{asset.FullPath}",
                    $"[CardVoice] Failed to load '{asset.RelativePath}': {request.error}");
                RemovePending(asset);
                request.Dispose();
                yield break;
            }

            try
            {
                asset.Clip = DownloadHandlerAudioClip.GetContent(request);
                if (asset.Clip == null)
                {
                    asset.HasFailed = true;
                    WarnOnce(
                        $"decode:{asset.FullPath}",
                        $"[CardVoice] Failed to decode '{asset.RelativePath}'.");
                    RemovePending(asset);
                }
                else
                {
                    asset.Clip.name = "Shadowbus.CardVoice." + Path.GetFileName(asset.RelativePath);
                    PlayPending(asset, generation);
                }
            }
            catch (Exception exception)
            {
                asset.HasFailed = true;
                WarnOnce(
                    $"decode:{asset.FullPath}",
                    $"[CardVoice] Failed to decode '{asset.RelativePath}': {exception.Message}");
                RemovePending(asset);
            }
            request.Dispose();
        }

        private static bool TryPlayLocalVoice(string cueName, AudioSource source)
        {
            string cue = NormalizeCueName(cueName);
            if (!AssetsByCue.TryGetValue(cue, out LocalVoiceAsset asset))
            {
                return false;
            }

            if (source == null)
            {
                WarnOnce("missing-source", "[CardVoice] Cannot play a local card voice: AudioSource is unavailable.");
                return true;
            }

            if (IsRejectingNewSound())
            {
                return true;
            }

            source.Stop();
            PendingBySource.Remove(source);
            if (asset.Clip != null)
            {
                PlaySource(source, asset.Clip);
            }
            else if (!asset.HasFailed)
            {
                PendingBySource[source] = new PendingPlayback
                {
                    Asset = asset,
                    Generation = RegistryGeneration
                };
                if (!asset.IsLoading && Plugin.Instance != null)
                {
                    asset.IsLoading = true;
                    Plugin.Instance.StartCoroutine(LoadAudioClip(asset, RegistryGeneration));
                }
                else if (!asset.IsLoading)
                {
                    asset.HasFailed = true;
                    PendingBySource.Remove(source);
                    WarnOnce("missing-plugin", "[CardVoice] Cannot load local card voices: plugin instance is unavailable.");
                }
            }
            return true;
        }

        private static bool HasLocalVoiceCue(string cueName)
        {
            return AssetsByCue.ContainsKey(NormalizeCueName(cueName));
        }

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

        private static bool IsLocalVoiceCueSheet(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            string name = path.Replace('\\', '/');
            int slashIndex = name.LastIndexOf('/');
            if (slashIndex >= 0)
            {
                name = name.Substring(slashIndex + 1);
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

        private static AudioSource GetBattleSource(BattleCardView view)
        {
            if (view == null || view.GameObject == null)
            {
                return null;
            }

            return BattleSources.GetValue(view, key => CreateSource(key.GameObject));
        }

        private static AudioSource GetGlobalSource()
        {
            if (GlobalSource == null && Plugin.Instance != null)
            {
                GlobalSource = CreateSource(Plugin.Instance.gameObject);
            }
            return GlobalSource;
        }

        private static AudioSource CreateSource(GameObject owner)
        {
            if (owner == null)
            {
                return null;
            }

            AudioSource source = owner.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            ConfigureSource(source);
            KnownSources.Add(new WeakReference(source));
            return source;
        }

        private static void PlaySource(AudioSource source, AudioClip clip)
        {
            if (source == null || clip == null || IsRejectingNewSound())
            {
                return;
            }

            ConfigureSource(source);
            source.clip = clip;
            source.Play();
        }

        private static void ConfigureSource(AudioSource source)
        {
            if (source == null)
            {
                return;
            }

            try
            {
                SoundMgr soundMgr = GameMgr.GetIns()?.GetSoundMgr();
                if (soundMgr != null)
                {
                    source.volume = soundMgr.GetVoiceVolume();
                    source.mute = soundMgr.IsVoiceMuted();
                }
            }
            catch (Exception)
            {
                source.volume = 1f;
            }
        }

        private static bool IsRejectingNewSound()
        {
            try
            {
                SoundMgr soundMgr = GameMgr.GetIns()?.GetSoundMgr();
                return soundMgr != null && soundMgr.IsRejectNewSound();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void PlayPending(LocalVoiceAsset asset, int generation)
        {
            foreach (KeyValuePair<AudioSource, PendingPlayback> pair in
                PendingBySource.ToList())
            {
                if (pair.Key == null || pair.Value.Generation != generation)
                {
                    PendingBySource.Remove(pair.Key);
                    continue;
                }
                if (ReferenceEquals(pair.Value.Asset, asset))
                {
                    PendingBySource.Remove(pair.Key);
                    PlaySource(pair.Key, asset.Clip);
                }
            }
        }

        private static void RemovePending(LocalVoiceAsset asset)
        {
            foreach (KeyValuePair<AudioSource, PendingPlayback> pair in
                PendingBySource.Where(pair => ReferenceEquals(pair.Value.Asset, asset)).ToList())
            {
                PendingBySource.Remove(pair.Key);
            }
        }

        private static void StopAllLocalVoices()
        {
            for (int index = KnownSources.Count - 1; index >= 0; index--)
            {
                AudioSource source = KnownSources[index].Target as AudioSource;
                if (source == null)
                {
                    KnownSources.RemoveAt(index);
                    continue;
                }
                source.Stop();
            }
        }

        private static bool IsAnyLocalVoicePlaying()
        {
            for (int index = KnownSources.Count - 1; index >= 0; index--)
            {
                AudioSource source = KnownSources[index].Target as AudioSource;
                if (source == null)
                {
                    KnownSources.RemoveAt(index);
                    continue;
                }
                if (source.isPlaying)
                {
                    return true;
                }
            }
            return false;
        }

        private static void UpdateLocalVoiceVolume(float volume)
        {
            ForEachSource(source => source.volume = volume);
        }

        private static void UpdateLocalVoiceMute(bool muted)
        {
            ForEachSource(source => source.mute = muted);
        }

        private static void ForEachSource(Action<AudioSource> action)
        {
            for (int index = KnownSources.Count - 1; index >= 0; index--)
            {
                AudioSource source = KnownSources[index].Target as AudioSource;
                if (source == null)
                {
                    KnownSources.RemoveAt(index);
                    continue;
                }
                action(source);
            }
        }

        private static void WarnOnce(string key, string message)
        {
            if (Warnings.Add(key))
            {
                Plugin.Logger.LogWarning(message);
            }
        }

        [HarmonyPatch(typeof(BattleCardView), nameof(BattleCardView.PlayVoice))]
        [HarmonyPrefix]
        public static bool BattleCardView_PlayVoice_Prefix(
            BattleCardView __instance,
            string voiceName)
        {
            if (!HasLocalVoiceCue(voiceName))
            {
                return true;
            }
            return !TryPlayLocalVoice(voiceName, GetBattleSource(__instance));
        }

        [HarmonyPatch(typeof(BattleCardView), nameof(BattleCardView.StopVoice))]
        [HarmonyPostfix]
        public static void BattleCardView_StopVoice_Postfix(BattleCardView __instance)
        {
            if (__instance == null || !BattleSources.TryGetValue(__instance, out AudioSource source))
            {
                return;
            }
            if (source != null)
            {
                source.Stop();
                PendingBySource.Remove(source);
            }
        }

        [HarmonyPatch(typeof(SoundMgr), nameof(SoundMgr.PlayVoiceScenario))]
        [HarmonyPrefix]
        public static bool SoundMgr_PlayVoiceScenario_Prefix(
            SoundMgr __instance,
            string cuename,
            float fadeout)
        {
            if (!HasLocalVoiceCue(cuename))
            {
                if (GlobalSource != null)
                {
                    GlobalSource.Stop();
                    PendingBySource.Remove(GlobalSource);
                }
                return true;
            }

            // Voice.PlayScenario normally stops the previous CRI voice before
            // advancing to another source. Preserve that behavior when the
            // replacement itself bypasses Voice.PlayScenario.
            __instance.StopVoiceAll(fadeout);
            return !TryPlayLocalVoice(cuename, GetGlobalSource());
        }

        [HarmonyPatch(typeof(SoundMgr), nameof(SoundMgr.LoadVoice), new Type[] { typeof(string), typeof(Action) })]
        [HarmonyPrefix]
        public static bool SoundMgr_LoadVoice_Prefix(
            string cueSheet,
            Action onLoaded,
            ref string __result)
        {
            if (!IsLocalVoiceCueSheet(cueSheet))
            {
                return true;
            }

            __result = cueSheet;
            onLoaded?.Invoke();
            return false;
        }

        [HarmonyPatch(typeof(SoundMgr), nameof(SoundMgr.UnloadVoice), new Type[] { typeof(string) })]
        [HarmonyPrefix]
        public static bool SoundMgr_UnloadVoice_Prefix(string cueSheet)
        {
            return !IsLocalVoiceCueSheet(cueSheet);
        }

        [HarmonyPatch(typeof(SoundMgr), nameof(SoundMgr.StopVoice))]
        [HarmonyPostfix]
        public static void SoundMgr_StopVoice_Postfix()
        {
            AudioSource source = GlobalSource;
            if (source != null)
            {
                source.Stop();
                PendingBySource.Remove(source);
            }
        }

        [HarmonyPatch(typeof(SoundMgr), nameof(SoundMgr.StopVoiceAll))]
        [HarmonyPostfix]
        public static void SoundMgr_StopVoiceAll_Postfix()
        {
            StopAllLocalVoices();
            PendingBySource.Clear();
        }

        [HarmonyPatch(typeof(SoundMgr), nameof(SoundMgr.IsVoicePlaying))]
        [HarmonyPostfix]
        public static void SoundMgr_IsVoicePlaying_Postfix(ref bool __result)
        {
            __result = __result || IsAnyLocalVoicePlaying();
        }

        [HarmonyPatch(typeof(SoundMgr), nameof(SoundMgr.SetVoiceVolume))]
        [HarmonyPostfix]
        public static void SoundMgr_SetVoiceVolume_Postfix(float prm)
        {
            UpdateLocalVoiceVolume(prm);
        }

        [HarmonyPatch(typeof(SoundMgr), nameof(SoundMgr.VoiceMute))]
        [HarmonyPostfix]
        public static void SoundMgr_VoiceMute_Postfix(bool isMute)
        {
            UpdateLocalVoiceMute(isMute);
        }
    }
}
