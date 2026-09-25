using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cute;
using HarmonyLib;
using UnityEngine;
using Wizard;

namespace Shadowbus
{
    /// <summary>
    /// 主战者皮肤素材兜底。
    ///
    /// 本地资源库里有一部分皮肤**官方就没发** UI 素材包（国际服和国服一样缺，不是加载失败）：
    /// 401 个皮肤里缺缩略图的 115 个、缺主战者选择按钮图的 100 个、缺卡组立绘的 113 个。例如
    /// 月影的 500405（本体）只有全身立绘和卡组立绘，没有 <c>ui_class_skin_500405.unity3d</c>；
    /// 但同一个角色的 2505（月影2）<c>ui_class_skin_2505.unity3d</c> 是有的。
    ///
    /// 游戏取这些素材的路子是：
    ///   <c>GetAssetTypePath(id, type, false)</c> → 包名 → <c>LoadAssetGroupAsync</c> 预加载
    ///   <c>GetAssetTypePath(id, type, true)</c> → 对象路径 → <c>LoadObject</c> 在**已加载的包**里按对象名找
    /// 包不存在时后一步必然返回 null，界面上就空一块。所以这里在 <c>GetAssetTypePath</c> 出口处
    /// 把"包不存在"的请求整条换掉——包名和对象路径一起换，调用方预加载的包和最后取的对象仍然对得上。
    ///
    /// 替代顺序（越靠前越像原图）：
    ///   1. **同角色的另一个皮肤**的同类型素材：官方素材，尺寸/构图天然正确（月影 500405 → 2505）；
    ///   2. 同一皮肤的另一类素材：拿到的图不是这个规格，会按官方的像素尺寸做**等比例居中裁切**再给出去
    ///      （只裁不变形，尺寸与官方一致：缩略图 512×512、按钮图 256×256、卡组立绘 1024×256）；
    ///   3. 同角色其它皮肤 × 其它类型素材，同样做等比例裁切；
    ///   4. 一个都没有（600070/600080/600090 这类占位号本地一张图都没有）时原样不动，只记一条日志。
    /// </summary>
    public static class LeaderSkinAssetFallback
    {
        /// <summary>替换后生成的贴图最多留几份（超出就丢引用，交给 Resources.UnloadUnusedAssets 回收）。</summary>
        private const int MaxConvertedTextures = 64;

        /// <summary>只有这几种"缺了就直接空一块"的类型才兜底；战斗立绘、模型等不动。</summary>
        private static readonly HashSet<ResourcesManager.AssetLoadPathType> Supported =
            new HashSet<ResourcesManager.AssetLoadPathType>
            {
                ResourcesManager.AssetLoadPathType.ClassCharaSkinThumbnail,
                ResourcesManager.AssetLoadPathType.ClassCharaButton,
                ResourcesManager.AssetLoadPathType.DeckListTexture,
            };

        /// <summary>同一皮肤内可换用的素材类型（不含请求的那一类本身），顺序即优先级。</summary>
        private static readonly Dictionary<ResourcesManager.AssetLoadPathType, ResourcesManager.AssetLoadPathType[]> SubstituteOrder =
            new Dictionary<ResourcesManager.AssetLoadPathType, ResourcesManager.AssetLoadPathType[]>
            {
                {
                    ResourcesManager.AssetLoadPathType.ClassCharaSkinThumbnail, new[]
                    {
                        ResourcesManager.AssetLoadPathType.ClassCharaButton,
                        ResourcesManager.AssetLoadPathType.DeckListTexture,
                        ResourcesManager.AssetLoadPathType.ClassCharaBase,
                    }
                },
                {
                    ResourcesManager.AssetLoadPathType.ClassCharaButton, new[]
                    {
                        ResourcesManager.AssetLoadPathType.ClassCharaSkinThumbnail,
                        ResourcesManager.AssetLoadPathType.DeckListTexture,
                        ResourcesManager.AssetLoadPathType.ClassCharaBase,
                    }
                },
                {
                    ResourcesManager.AssetLoadPathType.DeckListTexture, new[]
                    {
                        ResourcesManager.AssetLoadPathType.ClassCharaSkinThumbnail,
                        ResourcesManager.AssetLoadPathType.ClassCharaButton,
                        ResourcesManager.AssetLoadPathType.ClassCharaBase,
                    }
                },
            };

        /// <summary>官方各类素材的像素尺寸（从本地官方素材量出来的，全部皮肤一致）。换素材时按这个尺寸裁切。</summary>
        private static readonly Dictionary<ResourcesManager.AssetLoadPathType, Vector2Int> OfficialSize =
            new Dictionary<ResourcesManager.AssetLoadPathType, Vector2Int>
            {
                { ResourcesManager.AssetLoadPathType.ClassCharaSkinThumbnail, new Vector2Int(512, 512) },
                { ResourcesManager.AssetLoadPathType.ClassCharaButton, new Vector2Int(256, 256) },
                { ResourcesManager.AssetLoadPathType.DeckListTexture, new Vector2Int(1024, 256) },
            };

        private sealed class Substitution
        {
            public int SkinId;
            public ResourcesManager.AssetLoadPathType Requested;
            public ResourcesManager.AssetLoadPathType Used;
        }

        private static readonly HashSet<string> ExistingBundles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Substitution> Substitutions = new Dictionary<string, Substitution>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, List<int>> SiblingCache = new Dictionary<int, List<int>>();
        private static readonly Dictionary<string, Texture> ConvertedTextures = new Dictionary<string, Texture>(StringComparer.Ordinal);
        private static readonly List<string> ConvertedOrder = new List<string>();
        private static readonly HashSet<string> Logged = new HashSet<string>(StringComparer.Ordinal);
        private static int _substitutionCount;

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
                if (BundleExists(root, __instance.GetAssetTypePath(path, type, false)))
                {
                    return;
                }

                foreach (KeyValuePair<int, ResourcesManager.AssetLoadPathType> candidate in ResolveCandidates(skinId, type))
                {
                    string bundle = __instance.GetAssetTypePath(candidate.Key.ToString(), candidate.Value, false);
                    if (!BundleExists(root, bundle))
                    {
                        continue;
                    }

                    string objectPath = __instance.GetAssetTypePath(candidate.Key.ToString(), candidate.Value, true);
                    if (string.IsNullOrEmpty(objectPath))
                    {
                        continue;
                    }

                    __result = isfetch ? objectPath : bundle;
                    if (candidate.Value != type)
                    {
                        // 换了素材类型：取到后用官方尺寸等比例裁切，避免被界面拉变形。
                        Substitutions[objectPath] = new Substitution
                        {
                            SkinId = skinId,
                            Requested = type,
                            Used = candidate.Value,
                        };
                    }

                    _substitutionCount++;
                    LogOnce(skinId, type, candidate.Key, candidate.Value, isfetch);
                    return;
                }

                LogOnce(skinId, type, 0, type, isfetch);
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

        /// <summary>
        /// 候选素材：先"同角色的另一个皮肤 + 同类型"（官方素材，最合适），再"同皮肤另一类"，
        /// 最后"同角色其它皮肤 + 另一类"。
        /// </summary>
        private static IEnumerable<KeyValuePair<int, ResourcesManager.AssetLoadPathType>> ResolveCandidates(
            int skinId,
            ResourcesManager.AssetLoadPathType type)
        {
            foreach (int sibling in SiblingsOf(skinId))
            {
                yield return new KeyValuePair<int, ResourcesManager.AssetLoadPathType>(sibling, type);
            }

            foreach (ResourcesManager.AssetLoadPathType other in SubstituteOrder[type])
            {
                yield return new KeyValuePair<int, ResourcesManager.AssetLoadPathType>(skinId, other);
            }

            foreach (int sibling in SiblingsOf(skinId))
            {
                foreach (ResourcesManager.AssetLoadPathType other in SubstituteOrder[type])
                {
                    yield return new KeyValuePair<int, ResourcesManager.AssetLoadPathType>(sibling, other);
                }
            }
        }

        /// <summary>
        /// 同一角色的其它皮肤号（角色表里 <c>chara_name</c> 是本地化显示名，同名即同一角色，
        /// 例：月影本体 500405 与月影2 的 2505）。按皮肤号距离排序，先试更接近的。
        /// </summary>
        private static List<int> SiblingsOf(int skinId)
        {
            if (SiblingCache.TryGetValue(skinId, out List<int> cached))
            {
                return cached;
            }

            var siblings = new List<int>();
            try
            {
                List<ClassCharacterMasterData> leaders = Data.Master?.ClassCharacterList;
                string name = leaders?.FirstOrDefault(item => item != null && item.skin_id == skinId)?.chara_name;
                if (!string.IsNullOrEmpty(name))
                {
                    siblings.AddRange(leaders
                        .Where(item => item != null && item.skin_id > 0 && item.skin_id != skinId &&
                                       string.Equals(item.chara_name, name, StringComparison.Ordinal))
                        .Select(item => item.skin_id)
                        .Distinct()
                        .OrderBy(id => Math.Abs(id - skinId)));
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug($"[SkinFallback] Could not list the sibling skins of {skinId}: {exception.Message}");
            }

            SiblingCache[skinId] = siblings;
            return siblings;
        }

        /// <summary>
        /// 换过类型的素材：取到之后按官方尺寸等比例裁切（只裁不拉伸），这样界面上尺寸是对的。
        /// 同类型换皮肤（官方素材）不用处理。
        /// </summary>
        [HarmonyPatch(typeof(AssetManager), nameof(AssetManager.LoadObject),
            new Type[] { typeof(string), typeof(Type), typeof(bool) })]
        [HarmonyPostfix]
        private static void AssetManager_LoadObject_Postfix(string objectName, ref UnityEngine.Object __result)
        {
            __result = ConvertIfNeeded(objectName, __result);
        }

        [HarmonyPatch(typeof(AssetManager), nameof(AssetManager.LoadObject),
            new Type[] { typeof(string), typeof(string), typeof(Type) })]
        [HarmonyPostfix]
        private static void AssetManager_LoadObjectByBundle_Postfix(string objectName, ref UnityEngine.Object __result)
        {
            __result = ConvertIfNeeded(objectName, __result);
        }

        private static UnityEngine.Object ConvertIfNeeded(string objectName, UnityEngine.Object result)
        {
            try
            {
                if (result == null || string.IsNullOrEmpty(objectName) ||
                    !Substitutions.TryGetValue(objectName, out Substitution substitution))
                {
                    return result;
                }

                if (!(result is Texture source) || !OfficialSize.TryGetValue(substitution.Requested, out Vector2Int size))
                {
                    return result;
                }

                string key = $"{substitution.SkinId}|{(int)substitution.Requested}";
                if (ConvertedTextures.TryGetValue(key, out Texture cached) && cached != null)
                {
                    return cached;
                }

                Texture converted = FitToSize(source, size.x, size.y);
                if (converted == null)
                {
                    return result;
                }

                converted.name = $"{source.name}_fit{size.x}x{size.y}";
                StoreConverted(key, converted);
                return converted;
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug($"[SkinFallback] Could not fit the substituted texture: {exception.Message}");
                return result;
            }
        }

        private static void StoreConverted(string key, Texture texture)
        {
            if (ConvertedTextures.Count >= MaxConvertedTextures && ConvertedOrder.Count > 0)
            {
                // 不显式 Destroy：界面可能还引用着；丢引用即可，Unity 之后会自己回收。
                ConvertedTextures.Remove(ConvertedOrder[0]);
                ConvertedOrder.RemoveAt(0);
            }

            ConvertedTextures[key] = texture;
            ConvertedOrder.Add(key);
        }

        /// <summary>把源贴图按等比例居中裁切后缩放到目标尺寸（只裁不拉伸，所以不会变形）。</summary>
        private static Texture FitToSize(Texture source, int width, int height)
        {
            if (source.width <= 0 || source.height <= 0 || width <= 0 || height <= 0)
            {
                return null;
            }

            float sourceAspect = (float)source.width / source.height;
            float targetAspect = (float)width / height;
            Vector2 scale = Vector2.one;
            Vector2 offset = Vector2.zero;
            if (sourceAspect > targetAspect)
            {
                scale.x = targetAspect / sourceAspect;
                offset.x = (1f - scale.x) * 0.5f;
            }
            else if (sourceAspect < targetAspect)
            {
                scale.y = sourceAspect / targetAspect;
                offset.y = (1f - scale.y) * 0.5f;
            }

            RenderTexture renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, renderTexture, scale, offset);
                RenderTexture.active = renderTexture;
                var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                texture.Apply(false, false);
                return texture;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        private static bool BundleExists(string root, string bundle)
        {
            if (string.IsNullOrEmpty(bundle))
            {
                return false;
            }

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
            int sourceSkinId,
            ResourcesManager.AssetLoadPathType used,
            bool isfetch)
        {
            if (!Logged.Add($"{skinId}|{requested}|{sourceSkinId}|{used}"))
            {
                return;
            }

            if (sourceSkinId == 0)
            {
                Plugin.Logger.LogWarning(
                    $"[SkinFallback] Skin {skinId}: no local '{requested}' asset and nothing to substitute " +
                    "(neither the skin nor its siblings have any art installed).");
                return;
            }

            Plugin.Logger.LogInfo(
                $"[SkinFallback] Skin {skinId} '{requested}': {(isfetch ? "using" : "preloading")} " +
                $"{used} of skin {sourceSkinId}" +
                $"{(used == requested ? " (same character, official asset)" : " (fitted to the official size)")}" +
                $" — {_substitutionCount} substitution(s) so far.");
        }
    }
}
