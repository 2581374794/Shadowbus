using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using CriWare;
using Cute;
using HarmonyLib;
using UnityEngine;

namespace Shadowbus
{
    /// <summary>
    /// 让游戏把 <c>Resources</c> 目录当成自己的资源目录。
    ///
    /// 游戏把下载下来的资源（卡图 bundle、音效、影片、语音 ACB、manifest……）都写在
    /// <c>Application.persistentDataPath</c>，也就是
    /// <c>C:\Users\&lt;用户&gt;\AppData\LocalLow\Cygames\Shadowverse</c>，
    /// 根路径存在静态字段 <c>AssetManager._savePath</c> 里，外面统一通过
    /// <c>GetAssetSaveRootPath()</c> 和 <c>getAssetSavePath(AssetType)</c> 取。
    ///
    /// 额外资源目录的找法（先命中先用，不写死盘符/用户名/文件夹名）：
    /// <list type="number">
    ///   <item>BepInEx 配置 <c>[Resources] Root</c>（绝对路径，或相对游戏目录的路径）</item>
    ///   <item>环境变量 <c>SHADOWBUS_RESOURCES</c>（同上）</item>
    ///   <item>游戏目录同级的 <c>Resources</c>（默认，也是最常见的放法）</item>
    ///   <item>游戏目录下的 <c>Resources</c></item>
    ///   <item>游戏目录下的 <c>Shadowbus\Resources</c>（早期版本的放法）</item>
    /// </list>
    /// 里的文件保持和游戏自己的目录一样的相对结构（<c>a/card_xxx.unity3d</c>、<c>v/vo_xxx.acb</c>）。
    ///
    /// 优先级：**只要找到这样一个目录，它就是唯一的资源根目录**，上面那两个方法直接返回
    /// 它的路径（读写都算，跟以前挂目录联接的效果一样），不会因为"游戏自己的目录里也有"
    /// 就退回原版目录。玩家把别人的资源文件夹整个复制过来、机器上根本没有原版资源目录，
    /// 也能直接玩。一个都没找到时才完全不动游戏的行为（之后玩家再放进来也能认，见下面的重试）。
    ///
    /// 另外 <see cref="PersistentDataPathRedirect"/> 会把游戏里**所有**
    /// <c>Application.persistentDataPath</c> 的取值也换到那个目录（回放目录、HTTP 下载缓存、
    /// 统计日志、NGUITools 存档、游戏自带 CardMaster 导出目录……），所以"全部"都走新目录。
    ///
    /// 底下还留着几层单文件补丁（<c>AssetHandle.BuildLocalCachePath</c>、
    /// <c>AssetManager.AssetFileExists</c> / <c>BuildAssetLocalCachePath</c>、
    /// 剧情语音 <c>AudioManager.AddCueSheet</c>、影片、首页称号 BGM 检查）。
    /// 它们一律**先认额外资源目录**，那里没有这份文件才交回原方法；正常情况下只是多一层保险。
    /// </summary>
    public static class ResourceRootPatches
    {
        /// <summary>环境变量名：指到任意位置/任意名字的资源文件夹。</summary>
        public const string ResourceRootEnvironmentVariable = "SHADOWBUS_RESOURCES";

        private static string _resourceRoot;
        private static string _resourceRootSource;
        private static DateTime _nextProbeUtc;
        private static bool _warnedMissing;
        private static bool _loggedUse;

        /// <summary>少数地方直接读 <c>_savePath</c> 字段（清理缓存之类），顺手也换过去。</summary>
        private static readonly FieldInfo SavePathField =
            AccessTools.Field(typeof(AssetManager), "_savePath");

        /// <summary>
        /// 额外资源目录；一个候选都没有时返回 null。
        /// 找不到时限流重试（玩家可能开游戏之后才把资源文件夹放进来）。
        /// </summary>
        public static string ResourceRoot
        {
            get
            {
                if (_resourceRoot != null)
                {
                    return _resourceRoot;
                }

                if (DateTime.UtcNow < _nextProbeUtc)
                {
                    return null;
                }

                string root = ResolveResourceRoot(out string source);
                if (root == null)
                {
                    _nextProbeUtc = DateTime.UtcNow.AddSeconds(5);
                    if (!_warnedMissing)
                    {
                        _warnedMissing = true;
                        LogInfo(
                            "[Resources] No extra resource folder was found; the game keeps its own " +
                            $"paths. Put one next to the game folder (Resources), set " +
                            $"{ResourceRootEnvironmentVariable}, or set [Resources] Root in the config.");
                    }

                    return null;
                }

                _resourceRoot = root;
                _resourceRootSource = source;
                _nextProbeUtc = DateTime.MaxValue;
                LogInfo($"[Resources] Game asset root -> {root}  (found via {source})");

                // 之前没找到、现在找到了：把持久化路径重定向补上（下一帧由 Plugin.Update 执行）。
                PersistentDataPathRedirect.RequestApply();
                PlayerPrefsRedirect.RequestApply();
                return _resourceRoot;
            }
        }

        /// <summary>资源目录的相对结构分隔符版本，替换游戏 <c>_savePath</c> 时用。</summary>
        public static string ResourceRootSlash =>
            ResourceRoot == null ? null : ResourceRoot.Replace('\\', '/') + "/";

        private static string ResourceRootWithSeparator =>
            ResourceRoot == null
                ? null
                : Path.GetFullPath(ResourceRoot).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                  + Path.DirectorySeparatorChar;

        /// <summary>
        /// 游戏目录。BepInEx 正常会给出来；给不出来时退回"插件所在目录往上两级"
        /// （<c>&lt;游戏目录&gt;\BepInEx\plugins</c>），保证这里永远不抛异常 ——
        /// 后面的 IL 重定向会在游戏代码里调用它。
        /// </summary>
        private static string GameRoot
        {
            get
            {
                string gameRoot = Paths.GameRootPath;
                if (!string.IsNullOrEmpty(gameRoot))
                {
                    return gameRoot;
                }

                string pluginDirectory = Path.GetDirectoryName(
                    typeof(ResourceRootPatches).Assembly.Location);
                return string.IsNullOrEmpty(pluginDirectory)
                    ? Directory.GetCurrentDirectory()
                    : Path.GetFullPath(Path.Combine(pluginDirectory, "..", ".."));
            }
        }

        private static string ResolveResourceRoot(out string source)
        {
            source = null;

            // 1) BepInEx 配置里的覆盖值（绝对路径，或相对游戏目录）
            if (TryCandidate(Plugin.ResourceRootOverride, "配置 [Resources] Root", out string candidate))
            {
                source = "config [Resources] Root";
                return candidate;
            }

            // 2) 环境变量
            string environment = Environment.GetEnvironmentVariable(ResourceRootEnvironmentVariable);
            if (TryCandidate(environment, ResourceRootEnvironmentVariable, out candidate))
            {
                source = $"environment variable {ResourceRootEnvironmentVariable}";
                return candidate;
            }

            // 3) 默认与兼容位置
            string gameRoot = GameRoot;
            if (string.IsNullOrEmpty(gameRoot))
            {
                return null;
            }

            foreach (string relative in new[]
                     {
                         Path.Combine("..", "Resources"),
                         "Resources",
                         Path.Combine("Shadowbus", "Resources"),
                     })
            {
                if (TryCandidate(Path.Combine(gameRoot, relative), relative, out candidate))
                {
                    source = $"<game>\\{relative}";
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>候选路径存在就用它；相对路径按游戏目录解析。</summary>
        private static bool TryCandidate(string candidate, string label, out string full)
        {
            full = null;

            if (string.IsNullOrEmpty(candidate))
            {
                return false;
            }

            try
            {
                string path = candidate;
                if (!Path.IsPathRooted(path))
                {
                    path = Path.Combine(GameRoot, path);
                }

                path = Path.GetFullPath(path);
                if (!Directory.Exists(path))
                {
                    return false;
                }

                full = path;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void LogInfo(string message)
        {
            try
            {
                Plugin.Logger?.LogInfo(message);
            }
            catch (Exception)
            {
                // 日志不能影响补丁。
            }
        }

        /// <summary>
        /// 是否把游戏的资源根目录整个换到额外资源目录。
        /// 一个都没找到时保持游戏原样，免得把游戏指到一个空地方去。
        /// </summary>
        public static bool UseResourceRoot => ResourceRoot != null;

        /// <summary>
        /// 给 mod 自己用：把"游戏资源目录下的相对路径"解析成真实路径。
        /// 额外资源目录里有就用它，否则用游戏实际在用的那个资源目录（已被重定向）。
        /// </summary>
        public static string ResolveResourcePath(string relativePath)
        {
            if (TryGetFallbackPath(relativePath, out string resolved))
            {
                return resolved;
            }

            return Path.Combine(
                    PersistentDataPathRedirect.Current,
                    (relativePath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar))
                .Replace('/', Path.DirectorySeparatorChar);
        }

        /// <summary>命中一次就说明真的用上了，日志里能确认目录有没有生效。</summary>
        private static void LogOnce()
        {
            if (_loggedUse)
            {
                return;
            }

            _loggedUse = true;
            LogInfo($"[Resources] Using extra resource folder: {ResourceRoot} ({_resourceRootSource}).");
        }

        /// <summary>
        /// 把资源的相对路径（形如 <c>a/card_1015111200.unity3d</c>）解析到额外目录，
        /// 只有文件真的存在、且没跳出该目录时才认。
        /// </summary>
        private static bool TryGetFallbackPath(string assetName, out string path)
        {
            path = null;

            if (string.IsNullOrEmpty(assetName))
            {
                return false;
            }

            string relative = assetName.Replace('\\', '/').TrimStart('/');
            if (relative.Length == 0 || relative.Contains(".."))
            {
                return false;
            }

            string root = ResourceRoot;
            string rootWithSeparator = ResourceRootWithSeparator;
            if (root == null || rootWithSeparator == null)
            {
                return false;
            }

            try
            {
                string candidate = Path.GetFullPath(Path.Combine(root, relative));
                if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(candidate))
                {
                    return false;
                }

                path = candidate;
                LogOnce();
                return true;
            }
            catch (Exception)
            {
                // 路径里有非法字符之类，当作没有这份资源。
                return false;
            }
        }

        /// <summary>
        /// 把游戏的资源根目录直接换成额外资源目录。这是主机制：下载、读缓存、
        /// manifest、清缓存全都跟着走，效果跟以前挂目录联接一样。
        /// </summary>
        [HarmonyPatch(typeof(AssetManager), nameof(AssetManager.GetAssetSaveRootPath))]
        [HarmonyPostfix]
        private static void AssetManager_GetAssetSaveRootPath_Postfix(ref string __result)
        {
            if (!UseResourceRoot)
            {
                return;
            }

            __result = ResourceRootSlash;

            // 少数地方直接读 _savePath 字段（清理缓存之类），顺手也换过去。
            SavePathField?.SetValue(null, ResourceRootSlash);
        }

        /// <summary>
        /// 各类型资源的目录（<c>manifest/</c>、<c>a/</c>、根目录）在游戏启动时
        /// 由 <c>_savePath</c> 拼好存进字段，这里按同样的规则换成额外资源目录。
        /// </summary>
        [HarmonyPatch(typeof(AssetManager), nameof(AssetManager.getAssetSavePath))]
        [HarmonyPostfix]
        private static void AssetManager_getAssetSavePath_Postfix(
            AssetHandle.AssetType _assetType,
            ref string __result)
        {
            if (!UseResourceRoot)
            {
                return;
            }

            switch (_assetType)
            {
                case AssetHandle.AssetType.Manifests:
                    __result = ResourceRootSlash + "manifest/";
                    break;
                case AssetHandle.AssetType.Movie:
                case AssetHandle.AssetType.Font:
                case AssetHandle.AssetType.Sound:
                case AssetHandle.AssetType.TemporarySound:
                    __result = ResourceRootSlash;
                    break;
                default:
                    // AssetBundle 以及其它都放在 a/ 下。
                    __result = ResourceRootSlash + "a/";
                    break;
            }
        }

        private static readonly HashSet<string> LoggedClearSkips = new HashSet<string>(StringComparer.Ordinal);
        private static readonly object ClearSkipLock = new object();

        /// <summary>
        /// 游戏进剧情前会 <c>ClearTemporaryVoiceFile()</c> → <c>ClearLocalCache("v/t")</c>，
        /// 把整个 <c>v/t</c> 目录递归删掉、再按需重新下载（官方那套「临时音源」语义）。
        /// 离线时下载不可能成功，删掉就是永久丢失（剧情语音会全部消失），
        /// 所以只要接了额外资源目录就一律不删 —— 这些资源只在本地，删了补不回来。
        /// </summary>
        [HarmonyPatch(typeof(AssetManager), "ClearLocalCache")]
        [HarmonyPrefix]
        private static bool AssetManager_ClearLocalCache_Prefix(string keyword)
        {
            if (!UseResourceRoot)
            {
                return true;
            }

            string key = keyword ?? string.Empty;
            bool first;
            lock (ClearSkipLock)
            {
                first = LoggedClearSkips.Add(key);
            }

            if (first)
            {
                LogInfo(
                    $"[Resources] Kept local resources: skipped AssetManager.ClearLocalCache(\"{key}\") " +
                    "(offline mode cannot download them again).");
            }

            return false;
        }

        [HarmonyPatch(typeof(AssetManager), nameof(AssetManager.AssetFileExists))]
        [HarmonyPostfix]
        public static void AssetManager_AssetFileExists_Postfix(string assetName, ref bool __result)
        {
            // 额外资源目录里有这份文件就算有 —— 优先于游戏自己的目录。
            if (TryGetFallbackPath(assetName, out _))
            {
                __result = true;
            }
        }

        [HarmonyPatch(typeof(AssetManager), nameof(AssetManager.BuildAssetLocalCachePath), new[] { typeof(string) })]
        [HarmonyPostfix]
        public static void AssetManager_BuildAssetLocalCachePath_Postfix(
            string assetName,
            ref string __result)
        {
            PreferResourceRootPath(assetName, ref __result);
        }

        [HarmonyPatch(typeof(AssetManager), nameof(AssetManager.BuildAssetLocalCachePath), new[] { typeof(string), typeof(string) })]
        [HarmonyPostfix]
        public static void AssetManager_BuildAssetLocalCachePath_Postfix(
            string directory,
            string filename,
            ref string __result)
        {
            PreferResourceRootPath((directory ?? string.Empty) + (filename ?? string.Empty), ref __result);
        }

        /// <summary>
        /// 额外资源目录里真有这份文件时，直接把路径换成它的 —— 先认新目录，不先看游戏原目录。
        /// </summary>
        private static void PreferResourceRootPath(string relativePath, ref string __result)
        {
            if (TryGetFallbackPath(relativePath, out string fallback))
            {
                __result = fallback;
            }
        }

        /// <summary>
        /// 卡图 bundle、剧情背景、语音 ACB 实际取路径走的是
        /// <see cref="AssetHandle.BuildLocalCachePath"/>（内部直接用
        /// <c>AssetManager.getAssetSavePath</c> 拼，不经过上面那两个方法），
        /// 所以这里再补一层：游戏自己的目录里没有、额外资源目录里有，就用后者。
        /// </summary>
        [HarmonyPatch(typeof(AssetHandle), nameof(AssetHandle.BuildLocalCachePath))]
        [HarmonyPostfix]
        private static void AssetHandle_BuildLocalCachePath_Postfix(ref string __result)
        {
            string relative = ToResourceRelativePath(__result);
            if (relative != null)
            {
                PreferResourceRootPath(relative, ref __result);
            }
        }

        /// <summary>
        /// 把游戏拼出来的绝对路径还原成相对路径（形如 <c>a/card_xxx.unity3d</c>、
        /// <c>v/vo_xxx.acb</c>），认不出来就返回 null。
        /// </summary>
        private static string ToResourceRelativePath(string gamePath)
        {
            if (string.IsNullOrEmpty(gamePath))
            {
                return null;
            }

            string root;
            try
            {
                root = AssetManager.GetAssetSaveRootPath();
            }
            catch (Exception)
            {
                return null;
            }

            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            string normalizedRoot = root.Replace('\\', '/');
            if (!normalizedRoot.EndsWith("/", StringComparison.Ordinal))
            {
                normalizedRoot += "/";
            }

            string normalizedPath = gamePath.Replace('\\', '/');
            if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return normalizedPath.Substring(normalizedRoot.Length);
        }

        /// <summary>
        /// 语音（剧情语音、卡牌语音）、BGM、音效的 ACB 都不走 <c>AssetManager</c>：
        /// <c>AudioManager.AddCueSheet</c> 自己拿 <c>Application.persistentDataPath</c>
        /// 拼绝对路径，所以上面两个方法拦不住它们。这里单独拦一层：
        /// 额外资源目录里有这份 ACB 时，就按原方法一样的做法注册，只是换成额外目录里的绝对路径。
        ///
        /// 注册名、ACB/AWB 的拼接方式都跟原方法保持一致，游戏自己的目录里已经有这份文件时
        /// （也就是额外目录没给它更好的路径时）一律交回原方法。
        /// </summary>
        [HarmonyPatch(typeof(AudioManager), nameof(AudioManager.AddCueSheet))]
        [HarmonyPrefix]
        private static bool AudioManager_AddCueSheet_Prefix(
            AudioManager __instance,
            string _name,
            string acbFile,
            string subFolderPath,
            string awbname,
            ref bool __result)
        {
            string sheetName = (_name ?? string.Empty).Replace(".acb", string.Empty);

            // 已经注册过的 cue sheet，原方法自己会直接返回 true。
            if (CriAtom.GetCueSheet(sheetName) != null || CriAtom.GetCueSheet(acbFile) != null)
            {
                return true;
            }

            string subFolder = subFolderPath ?? string.Empty;
            string fileName = acbFile ?? string.Empty;
            if (!TryGetFallbackPath(subFolder + fileName, out string localAcb))
            {
                return true;
            }

            // 原方法在 awbname 为空串时拼出来的是"目录"路径，这里照抄它的格式。
            string localAwb = string.Format(
                "{0}/{1}{2}",
                ResourceRoot,
                subFolder,
                awbname ?? string.Empty);

            try
            {
                CriAtomCueSheet cueSheet = CriAtom.AddCueSheet(sheetName, localAcb, localAwb);
                if (cueSheet == null || cueSheet.acb == null)
                {
                    if (cueSheet != null)
                    {
                        __instance.RemoveCueSheet(sheetName);
                    }

                    // 额外目录里这份也读不了，交回原方法按游戏自己的目录再试一次。
                    return true;
                }

                LogOnce();
                __result = true;
                return false;
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning(
                    $"[Resources] Could not register '{subFolder}{fileName}' from " +
                    $"{ResourceRoot}: {exception.Message}");
                return true;
            }
        }

        private static readonly FieldInfo MovieRootPathField =
            AccessTools.Field(typeof(MovieManager), "fileRootPath");

        /// <summary>
        /// 影片（<c>Resources/m/</c> 里的 <c>.usm</c>，剧情序章/结局那些）是
        /// <c>MovieManager</c> 自己拿 <c>Application.persistentDataPath</c> 拼
        /// <c>file:///</c> 前缀的。这里在额外资源目录里真有这个影片时，把根路径换过去。
        /// </summary>
        [HarmonyPatch(typeof(MovieManager), nameof(MovieManager.Load))]
        [HarmonyPrefix]
        private static void MovieManager_Load_Prefix(MovieManager __instance, string filename)
        {
            if (string.IsNullOrEmpty(filename) ||
                MovieRootPathField == null ||
                !TryGetFallbackPath(filename, out _))
            {
                return;
            }

            MovieRootPathField.SetValue(__instance, "file:///" + ResourceRootSlash);
        }

        /// <summary>
        /// 首页特殊称号的 BGM 检查：<c>IsBGMAvailable</c> 直接拼
        /// <c>persistentDataPath/b/bgm_title_&lt;id&gt;.awb</c>，不经过 AssetManager。
        /// 额外资源目录里有这个 awb 就算可用；没有就交回原方法看游戏自己的目录。
        /// </summary>
        [HarmonyPatch(typeof(Wizard.SpecialTitleAssetBundle), "IsBGMAvailable")]
        [HarmonyPrefix]
        private static bool SpecialTitleAssetBundle_IsBGMAvailable_Prefix(
            string id,
            ref bool __result)
        {
            if (__result || !TryGetFallbackPath("b/bgm_title_" + id + ".awb", out _))
            {
                return true;
            }

            __result = true;
            return false;
        }
    }
}
