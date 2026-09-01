using HarmonyLib;
using Shadowbus.Server.Network;

namespace Shadowbus.Server
{
    /// <summary>
    /// The original room dialog only accepts five digits. Socket.IO connection codes
    /// contain an address and port, so a valid code pasted from the clipboard
    /// is stored directly while retaining the game's existing dialog.
    /// </summary>
    [HarmonyPatch(typeof(IDInput), "Paste")]
    public static class OnlineRoomInputPatch
    {
        [HarmonyPrefix]
        public static bool PastePrefix(IDInput __instance)
        {
            string roomCode = ClipboardHelper.Clipboard?.Trim();
            if (!ConnectionCode.TryParse(roomCode, out _, out _))
                return true;

            __instance.InputID = roomCode;
            __instance.CurrentDialogBase?.SetButtonDisable(false, false, false, false);

            IDInput.LayoutSet layout = AccessTools.Field(typeof(IDInput), "_currentLayout")
                ?.GetValue(__instance) as IDInput.LayoutSet;
            if (layout?._label != null)
            {
                const string marker = "SIOOK";
                for (int i = 0; i < layout._label.Length && i < marker.Length; i++)
                    layout._label[i].text = marker[i].ToString();
            }

            Plugin.Logger.LogInfo("[OnlineRoomInput] Socket.IO room code pasted");
            return false;
        }
    }
}
