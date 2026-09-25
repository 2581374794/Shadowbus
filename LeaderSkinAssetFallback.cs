using System;
using System.Collections.Generic;
using System.IO;
using Cute;
using HarmonyLib;

namespace Shadowbus
{
    /// <summary>
    /// 主战者皮肤素材兜底。
    ///
    /// 本地资源库里有一部分皮肤的 UI 素材包本身就是缺的（不是加载失败，是包不在），例如月影
    /// 500405 只有 <c>ui_class_500405_base.unity3d</c> / <c>ui_btn_deck_500405.unity3d</c>，
    /// 却没有 <c>ui_class_skin_500405.unity3d</c>（皮肤缩略图）和
    /// <c>ui_class_select_thumbnail_500405.unity3d</c>（主战者选择按钮图）。全量排查（401 个皮肤，
    /// 以 <c>Resources/manifest.db</c> 为准）缺缩略图的 115 个、缺按钮图的 100 个、缺卡组立绘的 113 个。
    ///
    /// 游戏取这些素材的路子是：
    ///   <c>GetAssetTypePath(id, type, false)</c> → 包名 → <c>LoadAssetGroupAsync</c> 预加载
    ///   <c>GetAssetTypePath(id, type, true)</c> → 对象路径 → <c>LoadObject</c> 在**已加载的包**里按对象名找
    /// 包不存在时后一步必然返回 null（界面上空一块）。所以这里在 <c>GetAssetTypePath</c> 出口处，
    /// 把"包不存在"的请求整条换成同一皮肤的另一份存在素材——包名和对象路径一起换，
    /// 调用方预加载的包和最后取的对象仍然对得上。
    ///
    /// 替代顺序（同一皮肤）：皮肤缩略图 → 选择按钮图 → 卡组立绘 → 全身立绘；
    /// 请求的那一类本身排在第一位，缺了才往后找。一个替代都没有（600070/600080/600090
    /// 这类占位号本地一张图都没有）时不动它，保持原样。
    /// </summary>
    public static class LeaderSkinAssetFallback
    {
        /// <summary>可替代的素材类型，顺序即优先级。</summary>
        private static readonly ResourcesManager.AssetLoadPathType[] Candidates =
        {
            ResourcesManager.AssetLoadPathType.ClassCharaSkinThumbnail,
            ResourcesManager.AssetLoadPathType.ClassCharaButton,
            ResourcesManager.AssetLoadPathType.DeckListTexture,
            ResourcesManager.AssetLoadPathType.ClassCharaBase,
        };

        /// <summary>只对这几种"缺了就直接空一块"的类型兜底；战斗立绘/模型等不动。</summary>
        private static readonly HashSet<ResourcesManager.AssetLoadPathType> Supported =
            new HashSet<ResourcesManager.AssetLoadPathType>(Candidates);

        /// <summary>包文件是否存在：只缓存"存在"的结果，不存在的一律现查（万一后来补上了包）。</summary>
        private static readonly HashSet<string> ExistingBundles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> Logged = new HashSet<string>(StringComparer.Ordinal);
        private static int _fallbackCount;

        private static bool _resolving;

        [HarmonyPatch(typeof(ResourcesManager), nameof(ResourcesManager.GetAssetTypePath))]
        [HarmonyPostfix]
        private static void ResourcesManager_GetAssetTypePath_Postfix(
            ResourcesManager __instance,
            string path,
            ResourcesManager.AssetLoadPathType type,
            bool isfetch,
            ref string __result)
        {
            if (_resolving || __instance == null || !Supported.Contains(type))
            {
                return;
            }

            if (!int.TryParse(path, out int skinId) || skinId <= 0)
            {
                return;
            }

            string root = ResourceRootPatches.ResourceRoot;
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            _resolving = true;
            try
            {
                // 自己那一类在本地就取得到：什么都不用做。
                string ownBundle = __instance.GetAssetTypePath(path, type, false);
                if (string.IsNullOrEmpty(ownBundle) || BundleExists(root, ownBundle))
                {
                    return;
                }

                foreach (ResourcesManager.AssetLoadPathType candidate in Candidates)
                {
                    if (candidate == type)
                    {
                        continue;
                    }

                    string bundle = __instance.GetAssetTypePath(path, candidate, false);
                    if (string.IsNullOrEmpty(bundle) || !BundleExists(root, bundle))
                    {
                        continue;
                    }

                    string objectPath = __instance.GetAssetTypePath(path, candidate, true);
                    if (string.IsNullOrEmpty(objectPath))
                    {
                        continue;
                    }

                    __result = isfetch ? objectPath : bundle;
                    _fallbackCount++;
                    LogOnce(skinId, type, candidate, isfetch);
                    return;
                }

                // 同一皮肤一张图都没有（占位号），保持原样。
                LogOnce(skinId, type, null, isfetch);
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug(
                    $"[SkinFallback] Could not substitute the '{type}' asset of skin {path}: {exception.Message}");
            }
            finally
            {
                _resolving = false;
            }
        }

        private static bool BundleExists(string root, string bundle)
        {
            if (ExistingBundles.Contains(bundle))
            {
                return true;
            }

            if (!File.Exists(Path.Combine(root, "a", bundle)))
            {
                return false;
            }

            ExistingBundles.Add(bundle);
            return true;
        }

        private static void LogOnce(
            int skinId,
            ResourcesManager.AssetLoadPathType requested,
            ResourcesManager.AssetLoadPathType? substitute,
            bool isfetch)
        {
            if (!Logged.Add($"{skinId}|{requested}|{substitute}"))
            {
                return;
            }

            if (substitute == null)
            {
                Plugin.Logger.LogWarning(
                    $"[SkinFallback] Skin {skinId}: no local '{requested}' asset and nothing to substitute " +
                    "(nothing of this skin is installed locally).");
                return;
            }

            Plugin.Logger.LogInfo(
                $"[SkinFallback] Skin {skinId}: local '{requested}' bundle is missing, " +
                $"{(isfetch ? "using" : "preloading")} '{substitute}' instead " +
                $"({_fallbackCount} substitution(s) so far).");
        }
    }
}
