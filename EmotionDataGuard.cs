using System;
using System.Collections.Generic;
using Cute;
using HarmonyLib;
using Wizard;

namespace Shadowbus
{
    /// <summary>
    /// 主战者表情/语音数据的兜底。
    ///
    /// 游戏在开场、结算、表情按钮这些地方都会直接取 <c>_emotionDic[皮肤号]</c>：
    ///   <c>DataMgr.GetPlayerEmotionData() / GetEnemyEmotionData() / GetEmotionDataBySkinId()</c>
    /// 表里没有这个皮肤号就 <c>KeyNotFoundException</c>。而调用点常常在协程里
    /// （<c>OpeningPhase.Setup → OpeningVoiceVfx → GetPlayerEmotionData</c>），
    /// **异常一抛协程就断了，对局卡在开场** —— 实测就是这个把移植过来的主战者卡死的。
    ///
    /// 平时这些数据由 <see cref="ImportedLeaders"/> 补进表里；这里只做最后一道保险：
    /// 查不到就返回一份"每个表情都有、但语音/台词为空"的表，让流程照常走完（顶多没声音），
    /// 而不是把整个对局卡死。
    /// </summary>
    public static class EmotionDataGuard
    {
        private static Dictionary<ClassCharaPrm.EmotionType, Emotion> _fallback;
        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);

        private static Dictionary<ClassCharaPrm.EmotionType, Emotion> Fallback()
        {
            if (_fallback != null)
            {
                return _fallback;
            }

            var table = new Dictionary<ClassCharaPrm.EmotionType, Emotion>();
            foreach (ClassCharaPrm.EmotionType type in Enum.GetValues(typeof(ClassCharaPrm.EmotionType)))
            {
                try
                {
                    // 空 voice_id / text_id：Voice.Play 会当成"没有这句语音"，不会播错别人的。
                    table[type] = new Emotion(new[]
                    {
                        ((int)type).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                    });
                }
                catch (Exception exception)
                {
                    Plugin.Logger.LogDebug($"[Import] Could not build a fallback emotion ({type}): {exception.Message}");
                }
            }

            _fallback = table;
            return _fallback;
        }

        private static Dictionary<ClassCharaPrm.EmotionType, Emotion> Resolve(string id)
        {
            try
            {
                Dictionary<string, Dictionary<ClassCharaPrm.EmotionType, Emotion>> table = Data.Master?._emotionDic;
                if (table != null && !string.IsNullOrEmpty(id) && table.ContainsKey(id))
                {
                    return null;
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogDebug($"[Import] Could not inspect the emotion table for '{id}': {exception.Message}");
                return null;
            }

            if (Reported.Add(id ?? string.Empty))
            {
                Plugin.Logger.LogWarning(
                    $"[Import] Leader '{id}' has no emotion/voice table; using the silent fallback " +
                    "(otherwise this throws inside a battle coroutine and freezes the match).");
            }

            return Fallback();
        }

        [HarmonyPatch(typeof(DataMgr), nameof(DataMgr.GetPlayerEmotionData))]
        [HarmonyPrefix]
        private static bool DataMgr_GetPlayerEmotionData_Prefix(
            DataMgr __instance,
            ref Dictionary<ClassCharaPrm.EmotionType, Emotion> __result)
        {
            Dictionary<ClassCharaPrm.EmotionType, Emotion> fallback = Resolve(__instance?.GetPlayerEmotionId());
            if (fallback == null)
            {
                return true;
            }

            __result = fallback;
            return false;
        }

        [HarmonyPatch(typeof(DataMgr), nameof(DataMgr.GetEnemyEmotionData))]
        [HarmonyPrefix]
        private static bool DataMgr_GetEnemyEmotionData_Prefix(
            DataMgr __instance,
            ref Dictionary<ClassCharaPrm.EmotionType, Emotion> __result)
        {
            Dictionary<ClassCharaPrm.EmotionType, Emotion> fallback = Resolve(__instance?.GetEnemyEmotionId());
            if (fallback == null)
            {
                return true;
            }

            __result = fallback;
            return false;
        }

        [HarmonyPatch(typeof(DataMgr), nameof(DataMgr.GetEmotionDataBySkinId))]
        [HarmonyPrefix]
        private static bool DataMgr_GetEmotionDataBySkinId_Prefix(
            string skinId,
            ref Dictionary<ClassCharaPrm.EmotionType, Emotion> __result)
        {
            Dictionary<ClassCharaPrm.EmotionType, Emotion> fallback = Resolve(skinId);
            if (fallback == null)
            {
                return true;
            }

            __result = fallback;
            return false;
        }
    }
}
