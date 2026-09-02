using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Shadowbus;

/// <summary>
/// Read-only diagnostics for hidden-card network resolution.
/// This class must never change the original battle state or control flow.
/// </summary>
public static class BattleHiddenCardDiagnostics
{
    [HarmonyPatch(typeof(NetworkBattleManagerBase), nameof(NetworkBattleManagerBase.GetUnapprovedCardObj))]
    [HarmonyPostfix]
    private static void GetUnapprovedCardObjPostfix(
        NetworkBattleManagerBase __instance,
        BattlePlayerBase player,
        int skillCardIndex,
        int publishedActiveCount,
        int movement,
        SkillBase skill,
        List<BattleCardBase> __result)
    {
        try
        {
            if (!IsNetworkBattle() || !(skill is Skill_summon_card))
            {
                return;
            }

            var receiveData = __instance?.networkBattleData?.GetReceiveData();
            var unapprovedList = receiveData?.unapprovedList;
            if (unapprovedList == null)
            {
                return;
            }

            var hiddenSummonEntries = unapprovedList
                .Where(IsHiddenDeckToFieldEntry)
                .ToList();
            if (hiddenSummonEntries.Count == 0)
            {
                return;
            }

            var owner = skill.SkillPrm?.ownerCard;
            Log(
                $"[HiddenDiag] GetUnapprovedCardObj summon: " +
                $"skillCardIndex={skillCardIndex}, published={publishedActiveCount}, " +
                $"movement={movement}, invoked={skill.IsInvoked}, " +
                $"owner={DescribeCard(owner)}, lookupPlayer={DescribePlayer(player)}, " +
                $"hiddenEntries={hiddenSummonEntries.Count}");

            foreach (var data in hiddenSummonEntries)
            {
                BattleCardBase resolved = null;
                string resolvedState = "none";
                try
                {
                    if (player != null)
                    {
                        resolved = NetworkBattleGenericTool.GetIndexToCardBase(
                            __instance,
                            player,
                            data.Index);
                        if (resolved != null)
                        {
                            resolvedState = NetworkBattleGenericTool
                                .GetCardPlaceState(player, data.Index)
                                .ToString();
                        }
                    }
                }
                catch (Exception exception)
                {
                    resolvedState = $"lookup-error:{exception.GetType().Name}:{exception.Message}";
                }

                Log(
                    $"[HiddenDiag] unapproved index={data.Index}, cardId={data.CardId}, " +
                    $"from={data.fromState}, to={FormatStates(data.ToStateList)}, " +
                    $"skillCardIndex={data.skillCardIndex}, " +
                    $"published={data.publishedActiveSkillCount}, " +
                    $"movement={data.skillMovementNum}, invoked={data.IsInvoked}, " +
                    $"gotUnapproved={data.IsGotUnapproved}, " +
                    $"resolved={DescribeCard(resolved)}, resolvedState={resolvedState}");
            }

            Log(
                $"[HiddenDiag] GetUnapprovedCardObj resultCount={__result?.Count ?? 0}, " +
                $"result={FormatCards(__result)}");
        }
        catch (Exception exception)
        {
            // Diagnostics must not interfere with the original network battle.
            Log($"[HiddenDiag] GetUnapprovedCardObj failed: {exception}");
        }
    }

    [HarmonyPatch(typeof(Skill_summon_card), nameof(Skill_summon_card.Start))]
    [HarmonyPrefix]
    private static void SummonStartPrefix(Skill_summon_card __instance, SkillBase.CallParameter parameter)
    {
        try
        {
            if (!IsNetworkBattle())
            {
                return;
            }

            var owner = __instance?.SkillPrm?.ownerCard;
            var targets = parameter?.targetCards as ICollection<BattleCardBase>;
            if (targets == null)
            {
                Log(
                    $"[HiddenDiag] Skill_summon_card.Start: " +
                    $"owner={DescribeCard(owner)}, published={__instance?.PublishedActiveSkillCount}, " +
                    $"invoked={__instance?.IsInvoked}, targets=non-collection");
                return;
            }

            Log(
                $"[HiddenDiag] Skill_summon_card.Start: owner={DescribeCard(owner)}, " +
                $"published={__instance.PublishedActiveSkillCount}, invoked={__instance.IsInvoked}, " +
                $"targets={targets.Count}");
            foreach (var target in targets)
            {
                Log(
                    $"[HiddenDiag] summon target: {DescribeCard(target)}, " +
                    $"isInDeck={SafeCardFlag(target, card => card.IsInDeck)}, " +
                    $"isInplay={SafeCardFlag(target, card => card.IsInplay)}, " +
                    $"isDead={SafeCardFlag(target, card => card.IsDead)}");
            }
        }
        catch (Exception exception)
        {
            // Diagnostics must not prevent Skill_summon_card.Start from running.
            Log($"[HiddenDiag] Skill_summon_card.Start failed: {exception}");
        }
    }

    private static bool IsHiddenDeckToFieldEntry(CardDataModel data)
    {
        return data != null &&
               data.fromState == NetworkBattleDefine.NetworkCardPlaceState.Deck &&
               data.ToStateList != null &&
               data.ToStateList.Contains(NetworkBattleDefine.NetworkCardPlaceState.Field);
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
        {
            return "null";
        }

        try
        {
            return $"isPlayer={player.IsPlayer}, hand={player.HandCardList?.Count ?? -1}, " +
                   $"deck={player.DeckCardList?.Count ?? -1}, field={player.ClassAndInPlayCardList?.Count ?? -1}, " +
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
        {
            return "null";
        }

        try
        {
            return $"index={card.Index},cardId={card.CardId},isPlayer={card.IsPlayer}";
        }
        catch (Exception exception)
        {
            return $"error:{exception.GetType().Name}:{exception.Message}";
        }
    }

    private static string FormatStates(IEnumerable<NetworkBattleDefine.NetworkCardPlaceState> states)
    {
        if (states == null)
        {
            return "null";
        }

        try
        {
            return string.Join(",", states.Select(state => state.ToString()).ToArray());
        }
        catch (Exception exception)
        {
            return $"error:{exception.GetType().Name}:{exception.Message}";
        }
    }

    private static string FormatCards(IEnumerable<BattleCardBase> cards)
    {
        if (cards == null)
        {
            return "null";
        }

        try
        {
            return string.Join(";", cards.Select(DescribeCard).ToArray());
        }
        catch (Exception exception)
        {
            return $"error:{exception.GetType().Name}:{exception.Message}";
        }
    }

    private static string SafeCardFlag(BattleCardBase card, Func<BattleCardBase, bool> read)
    {
        if (card == null)
        {
            return "false";
        }

        try
        {
            return read(card).ToString();
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
