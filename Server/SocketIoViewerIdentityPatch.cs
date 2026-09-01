using System;
using Cute;
using HarmonyLib;
using Wizard;
using Wizard.RoomMatch;

namespace Shadowbus.Server
{
    internal static class SocketIoViewerIdentity
    {
        internal static int GetViewerId()
        {
            return global::Shadowbus.ProfileOfflineData.GetOrCreateViewerId();
        }

        internal static void ApplyToOwnPlayer(PlayerControllerForOwn controller)
        {
            if (controller == null || !OnlineRuntime.IsEnabled || controller.Target == null)
                return;
            controller.Target.ViewerId = GetViewerId();
        }
    }

    /// <summary>
    /// Apply the profile identity after the offline room task has enabled the
    /// Socket.IO runtime, before the native RoomCreate/RoomEntry packet is
    /// emitted.
    /// </summary>
    [HarmonyPatch(typeof(PlayerControllerForOwn), nameof(PlayerControllerForOwn.CreateRoomBattleServer))]
    internal static class SocketIoCreateRoomIdentityPatch
    {
        [HarmonyPrefix]
        private static void Prefix(PlayerControllerForOwn __instance)
        {
            SocketIoViewerIdentity.ApplyToOwnPlayer(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerControllerForOwn), nameof(PlayerControllerForOwn.EnterRoomBattleServer))]
    internal static class SocketIoEnterRoomIdentityPatch
    {
        [HarmonyPrefix]
        private static void Prefix(PlayerControllerForOwn __instance)
        {
            SocketIoViewerIdentity.ApplyToOwnPlayer(__instance);
        }
    }

    /// <summary>
    /// Redirect the identity at its source. PlayerStaticData.UserViewerID and
    /// the native realtime agent both read Certification.ViewerId, so this
    /// single patch keeps all game-side ownership checks and payloads aligned.
    /// Outside Socket.IO room mode the original certification id is returned.
    /// </summary>
    [HarmonyPatch(typeof(Certification), nameof(Certification.ViewerId), MethodType.Getter)]
    internal static class SocketIoCertificationViewerIdPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(ref int __result)
        {
            if (!OnlineRuntime.IsEnabled)
                return true;

            // Skip the stock getter entirely. The original account id may be
            // shared by offline clients; online room identity comes from the
            // per-installation profile instead.
            __result = SocketIoViewerIdentity.GetViewerId();
            return false;
        }
    }
}
