using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using Spine;
using Spine.Unity;
using UnityEngine;

namespace Shadowbus
{
    /// <summary>
    /// 一次性诊断：主战者模型（spine）到底卡在哪一步。
    ///
    /// 素材包已经能被 Unity 读出来了（<c>bundle=True prefab=True</c>），所以"不显示"只可能是
    /// 这三类之一，全部由这里打出来：
    /// 1. 骨架数据没解析成功（<c>SkeletonDataAsset.GetSkeletonData</c> 抛异常或返回 null）；
    /// 2. 解析成功但渲染器没建起来（<c>SkeletonMecanim.valid=false</c>、没有 mesh）；
    /// 3. 建起来了但看不见（材质/着色器 null、物体失活、位置/缩放到画面外、层不对）。
    /// </summary>
    internal static class SpineLoadDiagnostics
    {
        private static readonly string[] Watched =
        {
            "class_1110", "class_1520", "class_1530",
            "class_99002", "class_99003", "class_99004", "class_99007",
        };

        private static readonly HashSet<string> DataReported = new HashSet<string>();
        private static readonly HashSet<int> MeshReported = new HashSet<int>();

        /// <summary>防止前缀里调用原方法时又回到自己身上。</summary>
        private static bool _inside;

        [HarmonyPatch(typeof(SkeletonDataAsset), nameof(SkeletonDataAsset.GetSkeletonData))]
        [HarmonyPrefix]
        private static bool SkeletonDataAsset_GetSkeletonData_Prefix(
            SkeletonDataAsset __instance, bool quiet, ref SkeletonData __result)
        {
            if (_inside || __instance == null)
            {
                return true;
            }

            _inside = true;
            try
            {
                __result = __instance.GetSkeletonData(quiet);
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning(
                    $"[SpineDiag] '{__instance.name}' GetSkeletonData threw: {exception}");
                throw;
            }
            finally
            {
                _inside = false;
            }

            if (DataReported.Add(__instance.name))
            {
                SkeletonData data = __result;
                string summary = data == null
                    ? "data=null"
                    : string.Format(
                        CultureInfo.InvariantCulture,
                        "version={0} animations={1} skins={2} bones={3}",
                        data.Version, data.Animations.Count, data.Skins.Count, data.Bones.Count);
                Plugin.Logger.LogInfo(
                    $"[SpineDiag] '{__instance.name}' parsed: {summary} " +
                    $"atlasPages={DescribeAtlas(__instance)} jsonAsset={__instance.skeletonJSON != null}");
            }

            return false;
        }

        private static string DescribeAtlas(SkeletonDataAsset asset)
        {
            try
            {
                Array atlases = AccessTools.Field(typeof(SkeletonDataAsset), "atlasAssets")?.GetValue(asset) as Array;
                if (atlases == null || atlases.Length == 0)
                {
                    return "none";
                }

                var builder = new StringBuilder();
                foreach (object item in atlases)
                {
                    if (builder.Length > 0)
                    {
                        builder.Append('+');
                    }

                    if (item == null)
                    {
                        builder.Append("null");
                        continue;
                    }

                    var getAtlas = AccessTools.Method(item.GetType(), "GetAtlas");
                    var atlas = getAtlas == null ? null : getAtlas.Invoke(item, null) as Atlas;
                    if (atlas == null)
                    {
                        builder.Append("null");
                        continue;
                    }

                    builder.Append((atlas.Pages == null ? 0 : atlas.Pages.Count).ToString(CultureInfo.InvariantCulture));
                    builder.Append('/');
                    builder.Append((atlas.Regions == null ? 0 : atlas.Regions.Count).ToString(CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
            catch (Exception exception)
            {
                return "<" + exception.Message + ">";
            }
        }

        [HarmonyPatch(typeof(SkeletonMecanim), nameof(SkeletonMecanim.LateUpdate))]
        [HarmonyPostfix]
        private static void SkeletonMecanim_LateUpdate_Postfix(SkeletonMecanim __instance)
        {
            try
            {
                if (__instance == null)
                {
                    return;
                }

                GameObject go = __instance.gameObject;
                if (!IsWatched(go.name))
                {
                    return;
                }

                int id = __instance.GetInstanceID();
                if (MeshReported.Count >= 24 || !MeshReported.Add(id))
                {
                    return;
                }

                MeshRenderer renderer = __instance.GetComponent<MeshRenderer>();
                MeshFilter filter = __instance.GetComponent<MeshFilter>();
                Material material = renderer != null ? renderer.sharedMaterial : null;
                int vertices = -1;
                try
                {
                    if (filter != null && filter.sharedMesh != null)
                    {
                        vertices = filter.sharedMesh.vertexCount;
                    }
                }
                catch
                {
                }

                SkeletonDataAsset asset = __instance.skeletonDataAsset;
                Plugin.Logger.LogInfo(
                    $"[SpineDiag] '{go.name}' render: valid={__instance.valid} " +
                    $"skeleton={__instance.skeleton != null} dataAsset={asset != null} " +
                    $"json={(asset != null && asset.skeletonJSON != null)} " +
                    $"renderer={renderer != null} enabled={(renderer != null && renderer.enabled)} " +
                    $"material='{(material == null ? "null" : material.name)}' " +
                    $"shader='{(material == null || material.shader == null ? "null" : material.shader.name)}' " +
                    $"vertices={vertices} sorting={(renderer == null ? 0 : renderer.sortingOrder)} " +
                    $"active={go.activeInHierarchy} layer={go.layer} " +
                    $"pos={go.transform.position} scale={go.transform.lossyScale} " +
                    $"parent='{(go.transform.parent == null ? "-" : go.transform.parent.name)}' " +
                    $"root='{go.transform.root.name}'");
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug($"[SpineDiag] render probe failed: {exception.Message}");
            }
        }

        private static bool IsWatched(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            foreach (string watched in Watched)
            {
                if (name.IndexOf(watched, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
