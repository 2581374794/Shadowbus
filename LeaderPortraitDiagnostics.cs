using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Cute;
using HarmonyLib;
using UnityEngine;
using Wizard;

namespace Shadowbus
{
    /// <summary>
    /// 主战者头像诊断（只打日志，不改行为）。
    ///
    /// 卡组界面上每张卡组的头像来自：
    ///
    ///   DeckUI.SetDisplayByDeckData
    ///     → skinId = deck.GetSkinId()                       // 卡组 JSON 里的 leader_skin_id
    ///     → GetAssetTypePath(skinId, DeckListTexture, isfetch: true) → "ui_btn_deck_&lt;skinId&gt;.unity3d"
    ///     → LoadObject&lt;Texture&gt;("Resources/a/ui_btn_deck_&lt;skinId&gt;.unity3d")
    ///
    /// 头像不显示时，可能是①卡组里的 skin id 不对（数据问题）、②资源包不在本地（包不完整）、
    /// ③包在但取不到贴图（加载问题）。这三种在外面看起来一模一样，所以这里把每一步都写进日志。
    ///
    /// 排查完可以连同本文件一起删掉。
    /// </summary>
    public static class LeaderPortraitDiagnostics
    {
        private static readonly FieldInfo ClassTextureField = AccessTools.Field(typeof(DeckUI), "_classTexture");
        private static int _logged;
        private static int _problems;
        private const int MaxNormalLogs = 12;
        private const int MaxProblemLogs = 25;

        /// <summary>已记录过的「资源类型 + 输入 + 是否 fetch」组合，避免刷屏。</summary>
        private static readonly HashSet<string> SeenPathRequests = new HashSet<string>(StringComparer.Ordinal);
        private const int MaxPathRequests = 80;

        /// <summary>
        /// 主战者头像相关的资源路径请求全部记下来：这是"界面要哪张图、这张图在不在本地"最直接的证据。
        /// 皮肤选择、卡组编辑大图、卡组列表小图走的都是这几个类型。
        /// </summary>
        [HarmonyPatch(typeof(ResourcesManager), nameof(ResourcesManager.GetAssetTypePath))]
        [HarmonyPostfix]
        private static void ResourcesManager_GetAssetTypePath_Postfix(
            string path,
            ResourcesManager.AssetLoadPathType type,
            bool isfetch,
            ref string __result)
        {
            try
            {
                if (!IsPortraitAssetType(type) || SeenPathRequests.Count >= MaxPathRequests)
                {
                    return;
                }

                string key = $"{type}|{path}|{isfetch}";
                if (!SeenPathRequests.Add(key))
                {
                    return;
                }

                string root = ResourceRootPatches.ResourceRoot;
                string file = null;
                bool exists = false;
                if (!string.IsNullOrEmpty(root) && !string.IsNullOrEmpty(__result))
                {
                    // isfetch 时结果是"包文件名"，包都在 a/ 下；否则是包内对象路径，不查文件。
                    file = isfetch
                        ? Path.Combine(root, "a", __result)
                        : Path.Combine(root, "a", __result + ".unity3d");
                    exists = File.Exists(file);
                }

                Plugin.Logger.LogInfo(
                    $"[Portrait] GetAssetTypePath type={type} path='{path}' fetch={isfetch} -> '{__result}' " +
                    $"fileExists={exists}");
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug($"[Portrait] path diagnostics failed: {exception.Message}");
            }
        }

        private static bool IsPortraitAssetType(ResourcesManager.AssetLoadPathType type)
        {
            switch (type)
            {
                case ResourcesManager.AssetLoadPathType.DeckListTexture:
                case ResourcesManager.AssetLoadPathType.DeckEditBGTexture:
                case ResourcesManager.AssetLoadPathType.ClassCharaSkinThumbnail:
                case ResourcesManager.AssetLoadPathType.ClassCharaWideThumbnail:
                case ResourcesManager.AssetLoadPathType.ClassCharaButton:
                case ResourcesManager.AssetLoadPathType.ClassCharaBase:
                    return true;
                default:
                    return false;
            }
        }

        [HarmonyPatch(typeof(DeckUI), "SetDisplayByDeckData")]
        [HarmonyPostfix]
        private static void DeckUI_SetDisplayByDeckData_Postfix(DeckUI __instance, DeckData deck)
        {
            try
            {
                if (deck == null)
                {
                    return;
                }

                int skinId = deck.GetSkinId();
                string bundle = $"ui_btn_deck_{skinId:D2}.unity3d";
                string root = ResourceRootPatches.ResourceRoot;
                string file = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "a", bundle);
                bool exists = file != null && File.Exists(file);

                UITexture texture = ClassTextureField?.GetValue(__instance) as UITexture;
                bool missing = texture == null || texture.mainTexture == null;

                if (!missing && _logged >= MaxNormalLogs)
                {
                    return;
                }

                if (missing)
                {
                    if (_problems >= MaxProblemLogs)
                    {
                        return;
                    }

                    _problems++;
                }
                else
                {
                    _logged++;
                }

                Plugin.Logger.LogWarning(
                    $"[Portrait] deck='{deck.GetDeckName()}' class={deck.GetDeckClassID()} " +
                    $"rawSkin={deck.GetRawSkinId()} skin={skinId} bundle={bundle} " +
                    $"fileExists={exists}{(file == null ? string.Empty : " (" + file + ")")} " +
                    $"texture={(texture == null ? "missing-widget" : (texture.mainTexture == null ? "NULL" : texture.mainTexture.name))} " +
                    $"random={deck.IsSkinRandom} pool=[{string.Join(",", deck.SelectRandomSkinIdList ?? new List<int>())}]");
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug($"[Portrait] diagnostics failed: {exception.Message}");
            }
        }
    }
}
