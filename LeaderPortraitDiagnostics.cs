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
        private const int MaxPathRequests = 150;

        /// <summary>
        /// 所有资源路径请求都记下来（不只是头像类）：这是"界面要哪张图、这张图在不在本地"最直接的证据。
        /// 结果里带 <c>.unity3d</c> 的就是包名（包都在 <c>a/</c> 下），否则是包内对象路径。
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
                if (SeenPathRequests.Count >= MaxPathRequests)
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
                    file = __result.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(root, "a", __result)
                        : null;
                    exists = file != null && File.Exists(file);
                }

                Plugin.Logger.LogInfo(
                    $"[Portrait] GetAssetTypePath type={type} path='{path}' fetch={isfetch} -> '{__result}' " +
                    (file == null ? "(object path)" : $"fileExists={exists}"));
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug($"[Portrait] path diagnostics failed: {exception.Message}");
            }
        }

        /// <summary>记录取不到的素材（返回 null）：这就是"界面上空一块"的直接原因。</summary>
        [HarmonyPatch(typeof(AssetManager), nameof(AssetManager.LoadObject),
            new Type[] { typeof(string), typeof(Type), typeof(bool) })]
        [HarmonyPostfix]
        private static void AssetManager_LoadObject_Postfix(string objectName, Type type, UnityEngine.Object __result)
        {
            LogFailedLoad(objectName, type, __result);
        }

        [HarmonyPatch(typeof(AssetManager), nameof(AssetManager.LoadObject),
            new Type[] { typeof(string), typeof(string), typeof(Type) })]
        [HarmonyPostfix]
        private static void AssetManager_LoadObjectByName_Postfix(string objectName, Type type, UnityEngine.Object __result)
        {
            LogFailedLoad(objectName, type, __result);
        }

        private static readonly HashSet<string> ReportedFailedLoads = new HashSet<string>(StringComparer.Ordinal);
        private const int MaxFailedLoadLogs = 40;

        private static void LogFailedLoad(string objectName, Type type, UnityEngine.Object result)
        {
            try
            {
                if (result != null || string.IsNullOrEmpty(objectName) ||
                    ReportedFailedLoads.Count >= MaxFailedLoadLogs)
                {
                    return;
                }

                // 只关心界面/主战者相关的素材，卡面那些插件自己有日志。
                string lower = objectName.ToLowerInvariant();
                if (lower.IndexOf("class_", StringComparison.Ordinal) < 0 &&
                    lower.IndexOf("chara", StringComparison.Ordinal) < 0 &&
                    lower.IndexOf("btn_deck", StringComparison.Ordinal) < 0 &&
                    lower.IndexOf("thumbnail", StringComparison.Ordinal) < 0 &&
                    lower.IndexOf("leader", StringComparison.Ordinal) < 0)
                {
                    return;
                }

                if (!ReportedFailedLoads.Add(objectName + "|" + (type?.Name ?? "?")))
                {
                    return;
                }

                Plugin.Logger.LogWarning(
                    $"[Portrait] LoadObject returned NULL for '{objectName}' (type={type?.Name}) " +
                    "→ 这个素材取不到，界面上对应的那块就是空的。");
            }
            catch (Exception)
            {
            }
        }

        /// <summary>把卡组那块 UI 上所有贴图/精灵的状态列出来：哪一块是空的，一眼就能看出来。</summary>
        private static void DumpDeckWidgets(DeckUI deckUi, DeckData deck)
        {
            try
            {
                UIWidget[] widgets = deckUi.GetComponentsInChildren<UIWidget>(true);
                var parts = new List<string>();
                foreach (UIWidget widget in widgets)
                {
                    if (widget == null)
                    {
                        continue;
                    }

                    string state;
                    if (widget is UITexture textureWidget)
                    {
                        Texture texture = textureWidget.mainTexture;
                        state = texture == null ? "texture=NULL" : "texture=" + texture.name;
                    }
                    else if (widget is UISprite spriteWidget)
                    {
                        state = string.IsNullOrEmpty(spriteWidget.spriteName)
                            ? "spriteName=(empty)"
                            : $"sprite={spriteWidget.spriteName}" +
                              (spriteWidget.atlas == null ? " atlas=NULL" : string.Empty);
                    }
                    else
                    {
                        continue;
                    }

                    parts.Add($"{widget.name}[{state}]");
                }

                Plugin.Logger.LogWarning(
                    $"[Portrait] deck='{deck.GetDeckName()}' widgets({parts.Count}): {string.Join(" | ", parts)}");
                _widgetDumps++;
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug($"[Portrait] widget dump failed: {exception.Message}");
            }
        }

        private static int _widgetDumps;
        private static bool _screenCaptured;

        /// <summary>
        /// 顺手把当前画面存一张 PNG 到资源根目录（`_portrait_shot.png`）。
        /// 这样即使游戏窗口在后台/失焦，也能直接看到界面上到底缺了哪一块。
        /// 只在第一次进卡组界面时存一张。
        /// </summary>
        private static void CaptureScreenOnce()
        {
            if (_screenCaptured)
            {
                return;
            }

            string root = ResourceRootPatches.ResourceRoot;
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            try
            {
                string path = Path.Combine(root, "_portrait_shot.png");
                _screenCaptured = true;
                ScreenCapture.CaptureScreenshot(path);
                Plugin.Logger.LogWarning($"[Portrait] Captured the current screen to {path}");
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug($"[Portrait] screen capture failed: {exception.Message}");
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

                // 前几块卡组把整块 UI 的贴图状态也列出来。
                if (_widgetDumps < 8)
                {
                    DumpDeckWidgets(__instance, deck);
                }

                CaptureScreenOnce();
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug($"[Portrait] diagnostics failed: {exception.Message}");
            }
        }
    }
}
