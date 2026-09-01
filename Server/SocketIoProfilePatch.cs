using System;
using System.Collections.Generic;
using HarmonyLib;
using Wizard.RoomMatch;

namespace Shadowbus.Server
{
    /// <summary>
    /// The stock RoomEntry packet does not contain the account profile. The
    /// temporary local server cannot query the closed account service, so send
    /// only that missing data in the native RoomEntry payload. This keeps one
    /// RoomEntry per player and avoids re-entering the stock room state machine.
    /// </summary>
    [HarmonyPatch(
        typeof(PlayerControllerForOwn),
        "EmitMsg",
        new[] { typeof(string), typeof(Dictionary<string, object>), typeof(Action), typeof(bool) })]
    internal static class SocketIoProfilePatch
    {
        [HarmonyPrefix]
        private static void Prefix(
            string uri,
            ref Dictionary<string, object> dataList)
        {
            try
            {
                if (!OnlineRuntime.IsEnabled ||
                    !string.Equals(uri, "RoomEntry", StringComparison.Ordinal))
                    return;

                P2PProfile profile = global::Shadowbus.ProfileOfflineData.CreateP2PProfile();
                dataList = dataList ?? new Dictionary<string, object>();
                AddIfMissing(dataList, "profileViewerId", profile.ViewerId);
                AddIfMissing(dataList, "userName", profile.UserName ?? "Player");
                AddIfMissing(dataList, "rank", profile.Rank);
                AddIfMissing(dataList, "battlePoint", profile.BattlePoint);
                AddIfMissing(dataList, "masterPoint", profile.MasterPoint);
                AddIfMissing(dataList, "degreeId", profile.DegreeId);
                AddIfMissing(dataList, "emblemId", profile.EmblemId);
                AddIfMissing(dataList, "countryCode", profile.CountryCode ?? string.Empty);
                AddIfMissing(dataList, "isOfficial", profile.IsOfficial);

                Plugin.Logger.LogInfo(
                    $"[SocketIO] Embedded account profile in RoomEntry: viewerId={profile.ViewerId}, name={profile.UserName}");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[SocketIO] Account profile embed failed: {ex.Message}");
            }
        }

        private static void AddIfMissing(
            IDictionary<string, object> data,
            string key,
            object value)
        {
            if (!data.ContainsKey(key))
            {
                data[key] = value;
            }
        }
    }
}
