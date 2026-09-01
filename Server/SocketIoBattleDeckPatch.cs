using System;
using System.Collections.Generic;
using HarmonyLib;
using Wizard;
using Shadowbus.Server.Room;

namespace Shadowbus.Server
{
    /// <summary>
    /// The retired room API used to include the selected deck in its matching
    /// response. Preserve the native InitRoomBattle sender and add only that
    /// missing local snapshot so the server can author a native Matched frame.
    /// </summary>
    [HarmonyPatch(
        typeof(RealTimeNetworkAgent),
        nameof(RealTimeNetworkAgent.EmitMsgPack),
        new[]
        {
            typeof(NetworkBattleDefine.NetworkBattleURI),
            typeof(Dictionary<string, object>),
            typeof(Action),
            typeof(bool),
            typeof(int),
            typeof(bool),
            typeof(bool)
        })]
    internal static class SocketIoBattleDeckPatch
    {
        private const string SnapshotKey = "shadowbusDeck";

        [HarmonyPrefix]
        private static void Prefix(
            NetworkBattleDefine.NetworkBattleURI uri,
            ref Dictionary<string, object> info)
        {
            if (!OnlineRuntime.IsEnabled ||
                uri != NetworkBattleDefine.NetworkBattleURI.InitRoomBattle)
                return;

            PlayerDeck deck = OnlineRuntime.LocalBattleDeck;
            if (deck == null || deck.CardIds == null || deck.CardIds.Length == 0)
            {
                Plugin.Logger.LogError(
                    "[SocketIO] InitRoomBattle has no captured local deck; server will reject initialization");
                return;
            }

            info = info ?? new Dictionary<string, object>();
            if (info.ContainsKey(SnapshotKey))
                return;

            info[SnapshotKey] = new Dictionary<string, object>
            {
                ["cardIds"] = deck.CardIds,
                ["classId"] = deck.ClanId,
                ["subclassId"] = deck.SubclassId,
                ["charaId"] = deck.CharaId,
                ["skinId"] = deck.SkinId,
                ["sleeveId"] = deck.SleeveId,
                ["rotationId"] = deck.RotationId ?? string.Empty
            };
            Plugin.Logger.LogInfo(
                $"[SocketIO] Attached local deck snapshot to InitRoomBattle: cards={deck.CardIds.Length}, class={deck.ClanId}");
        }
    }
}
