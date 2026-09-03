using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Shadowbus;

/// <summary>
/// Read-only diagnostics for the receive side of hidden-card resolution.
///
/// The relay has been shown to emit a correct knownList - the injected
/// index/cardId pairs match the dealt deck, and knownListOpen counts the
/// entries that carry an identity. Yet a deck-self summon (119631020) still
/// fails to render on the opponent intermittently, which places the fault
/// after the message arrives.
///
/// The prime suspect is ReplaceReceivedCard.SearchForDummyCardInHandAndDeck.
/// It resolves the placeholder with SingleOrDefault over DeckCardList, so a
/// duplicated index throws and a missing index yields null; ReplaceCard then
/// returns null and the whole reveal is dropped without a word. These hooks
/// record which of those happened, and what the receiver's zones looked like
/// at that moment.
///
/// This class must never change battle state or control flow. Every hook is
/// a postfix or a prefix with no return value, and every access is guarded.
/// </summary>
public static class BattleHiddenCardDiagnostics
{
    /// <summary>
    /// Reports what the receiver did with each knownList entry.
    ///
    /// A null result is the silent-failure case: the card kept its dummy and
    /// no reveal happened. The zone census tells which branch of
    /// SearchForDummyCardInHandAndDeck was responsible - a count of 0 means
    /// the index had already left every searched zone, while a count above 1
    /// means SingleOrDefault would have thrown before returning.
    /// </summary>
    [HarmonyPatch(typeof(ReplaceReceivedCard), nameof(ReplaceReceivedCard.ReplaceCard))]
    [HarmonyPostfix]
    private static void ReplaceCardPostfix(
        ReplaceReceivedCard __instance,
        BattlePlayerBase battlePlayer,
        BattleCardBase __result)
    {
        try
        {
            if (!IsNetworkBattle())
                return;

            int index = __instance.CardIdx;
            Log(
                $"[HiddenDiag] ReplaceCard idx={index}, cardId={__instance.CardId}, " +
                $"result={DescribeCard(__result)}, " +
                $"zones={DescribeZoneMatches(battlePlayer, index)}, " +
                $"player={DescribePlayer(battlePlayer)}");
        }
        catch (Exception exception)
        {
            Log($"[HiddenDiag] ReplaceCard failed: {exception}");
        }
    }

    /// <summary>
    /// Records the knownList as a whole before any entry is processed, so a
    /// dropped entry can be told apart from an entry that was never sent.
    /// ReplaceReceivedCards runs once per list; a cardId of 0 is the stock
    /// "identity withheld" placeholder and is expected.
    /// </summary>
    [HarmonyPatch(typeof(NetworkBattleData), nameof(NetworkBattleData.BeforeSettingReceiveData))]
    [HarmonyPrefix]
    private static void BeforeSettingReceiveDataPrefix(NetworkBattleData __instance)
    {
        try
        {
            if (!IsNetworkBattle())
                return;

            var receiveData = __instance?.GetReceiveData();
            if (receiveData == null)
                return;

            List<CardDataModel> known = receiveData.knownCardList;
            List<CardDataModel> unapproved = receiveData.unapprovedList;
            if ((known == null || known.Count == 0) &&
                (unapproved == null || unapproved.Count == 0))
            {
                return;
            }

            Log(
                $"[HiddenDiag] receive uri={receiveData.dataUri}, " +
                $"known={DescribeDataModels(known)}, " +
                $"unapproved={DescribeDataModels(unapproved)}");
        }
        catch (Exception exception)
        {
            Log($"[HiddenDiag] BeforeSettingReceiveData failed: {exception}");
        }
    }

    /// <summary>
    /// The condition gate a deck-self summon must clear on the receiver.
    /// CheckCondition returning false here is what makes the effect silently
    /// not happen, and its verdict depends on the knownList reaching
    /// GetReceiveCardList intact.
    /// </summary>
    [HarmonyPatch(typeof(NetworkExecutionInfoCreator), nameof(NetworkExecutionInfoCreator.CheckCondition))]
    [HarmonyPostfix]
    private static void CheckConditionPostfix(
        NetworkExecutionInfoCreator __instance,
        bool __result)
    {
        try
        {
            if (!IsNetworkBattle())
                return;

            SkillBase skill = __instance?._skill;
            if (skill == null || !(skill.ConditionTargetFilter is SkillTargetDeckSelfFilter))
                return;

            BattleCardBase owner = skill.SkillPrm?.ownerCard;
            if (owner == null || owner.IsPlayer)
                return;

            Log(
                $"[HiddenDiag] CheckCondition deckSelf owner={DescribeCard(owner)}, " +
                $"result={__result}, " +
                $"receiveSkillConditionCheck={__instance._isReceiveSkillConditionCheck}, " +
                $"unapprovedSkill={__instance._isUnapprovedSkill}, " +
                $"playSkill={__instance._playSkill}, notPlaySkill={__instance._notPlaySkill}, " +
                $"receiveCards={DescribeReceiveCardList(owner)}");
        }
        catch (Exception exception)
        {
            Log($"[HiddenDiag] CheckCondition failed: {exception}");
        }
    }

    /// <summary>
    /// Counts index matches in each zone SearchForDummyCardInHandAndDeck
    /// consults, in the same order. Deck and hand are the SingleOrDefault
    /// lookups, so anything other than 0 or 1 there is a defect.
    /// </summary>
    private static string DescribeZoneMatches(BattlePlayerBase player, int index)
    {
        if (player == null)
            return "player=null";

        try
        {
            return
                $"deck={CountMatches(player.DeckCardList, index)}, " +
                $"hand={CountMatches(player.HandCardList, index)}, " +
                $"cemetery={CountMatches(player.CemeteryList, index)}, " +
                $"necromance={CountMatches(player.NecromanceZoneList, index)}, " +
                $"reserved={CountMatches(player.ReservedCardList, index)}";
        }
        catch (Exception exception)
        {
            return $"error:{exception.GetType().Name}:{exception.Message}";
        }
    }

    private static string CountMatches(IEnumerable<BattleCardBase> cards, int index)
    {
        if (cards == null)
            return "null";

        try
        {
            return cards.Count(card => card != null && card.Index == index)
                .ToString();
        }
        catch (Exception exception)
        {
            return $"error:{exception.GetType().Name}";
        }
    }

    private static string DescribeReceiveCardList(BattleCardBase owner)
    {
        try
        {
            var battleMgr = owner.SelfBattlePlayer?.BattleMgr as NetworkBattleManagerBase;
            var receiveData = battleMgr?.networkBattleData?.GetReceiveData();
            List<CardDataModel> cards = receiveData?.GetReceiveCardList();
            if (cards == null)
                return "null";

            var parts = new List<string>(cards.Count);
            for (int i = 0; i < cards.Count; i++)
            {
                CardDataModel card = cards[i];
                if (card == null)
                    continue;
                parts.Add($"{card.Index}:{card.CardId}{(card.IsOpen ? "*" : "")}");
            }
            return "[" + string.Join(" ", parts.ToArray()) + "]";
        }
        catch (Exception exception)
        {
            return $"error:{exception.GetType().Name}:{exception.Message}";
        }
    }

    private static string DescribeDataModels(List<CardDataModel> cards)
    {
        if (cards == null)
            return "null";

        try
        {
            var parts = new List<string>(cards.Count);
            for (int i = 0; i < cards.Count; i++)
            {
                CardDataModel card = cards[i];
                if (card == null)
                    continue;
                // An asterisk marks an open entry; from/to describe the move
                // the sender declared, which is what the uList reconciliation
                // in BeforeSettingReceiveData keys on.
                parts.Add(
                    $"{card.Index}:{card.CardId}{(card.IsOpen ? "*" : "")}" +
                    $"({card.fromState}->{FormatStates(card.ToStateList)})");
            }
            return "[" + string.Join(" ", parts.ToArray()) + "]";
        }
        catch (Exception exception)
        {
            return $"error:{exception.GetType().Name}:{exception.Message}";
        }
    }

    private static bool IsNetworkBattle()
    {
        try
        {
            return GameMgr.GetIns() != null && GameMgr.GetIns().IsNetworkBattle;
        }
        catch
        {
            return false;
        }
    }

    private static string DescribePlayer(BattlePlayerBase player)
    {
        if (player == null)
            return "null";

        try
        {
            return $"isPlayer={player.IsPlayer}, hand={player.HandCardList?.Count ?? -1}, " +
                   $"deck={player.DeckCardList?.Count ?? -1}, " +
                   $"field={player.ClassAndInPlayCardList?.Count ?? -1}, " +
                   $"cemetery={player.CemeteryList?.Count ?? -1}";
        }
        catch (Exception exception)
        {
            return $"error:{exception.GetType().Name}:{exception.Message}";
        }
    }

    private static string DescribeCard(BattleCardBase card)
    {
        if (card == null)
            return "null";

        try
        {
            // NullBattleCard is the decline sentinel CreateActualCard returns
            // for a when-draw banish; it is not an ordinary card.
            if (card is NullBattleCard)
                return "NullBattleCard";
            return $"index={card.Index},cardId={card.CardId},isPlayer={card.IsPlayer}";
        }
        catch (Exception exception)
        {
            return $"error:{exception.GetType().Name}:{exception.Message}";
        }
    }

    private static string FormatStates(
        IEnumerable<NetworkBattleDefine.NetworkCardPlaceState> states)
    {
        if (states == null)
            return "null";

        try
        {
            return string.Join(",", states.Select(state => state.ToString()).ToArray());
        }
        catch (Exception exception)
        {
            return $"error:{exception.GetType().Name}:{exception.Message}";
        }
    }

    private static void Log(string message)
    {
        try
        {
            Plugin.Logger?.LogInfo(message);
        }
        catch
        {
            // Logging must never affect battle execution.
        }
    }
}
