using System;
using System.Collections.Generic;

namespace Shadowbus
{
    internal static class P2PPlayerHistoryPolicy
    {
        // Native PlayActions orders own card movement between these zones. Copying
        // the same lists again as player history can leave one card object in two
        // zones, or apply only half of a zone snapshot while card references are
        // still being resolved. Hidden-card snapshots synchronize the identity and
        // mutable state of cards in these zones without taking over their topology.
        internal static IReadOnlyList<string> NativeCardZoneListNames { get; } =
            Array.AsReadOnly(new[]
            {
                "HandCardList",
                "DeckCardList",
                "ClassAndInPlayCardList",
                "CemeteryList",
                "BanishList",
                "FusionIngredientList",
                "NecromanceZoneList",
                "ReservedCardList"
            });

        internal static IReadOnlyList<string> SynchronizedListNames { get; } =
            Array.AsReadOnly(new[]
            {
                "BattleStartDeckCardList",
                "DeckSkillCardList",
                "TurnFusionCards",
                "DiscardedCardList",
                "FusionIngredientAndDiscardedCardList",
                "UniteList",
                "GetOnList",
                "BlackHole",
                // These lists back the native destroyed-card and Choice Brave
                // filters. They are persistent inputs to later skills, not
                // merely view/VFX scratch lists.
                "DestroyedWhenDestroyCards",
                "ChoiceBraveCardList",
                "ChoiceBraveCards",
                // last_target filters read this nested list across the next
                // action. It is persistent battle history, not the temporary
                // target list used while an operation is executing.
                "LastTargetCardsList",
                // The native skill filter uses this persistent list for
                // conditions such as {me.evolved_card_list.count}. It is not
                // merely a VFX work list: the count must match on the peer
                // before an opponent's skill is evaluated.
                "EvolvedCards",
                "TurnPlayCardCountInfo",
                "TurnFusionCountInfo",
                "TurnEvolveCardCountInfo",
                "TurnPlayCards",
                "TurnDrawCards",
                "TurnDrawTokenCardsWithId",
                "GameDrawCards",
                "GameDrawTokenCards",
                "GameAddUpdateDeckCards",
                "GameSummonCards",
                "GameSummonMomentTribe",
                "GamePlayMomentTribe",
                "GamePlayMomentSpellChargeCards",
                "GameUpdateDeckMomentTribe",
                "GamePlayCards",
                "GameTurnPlayCards",
                "GameEnhancePlayCards",
                "GameCrystallizedPlayCards",
                "GameLeftCards",
                "GameTurnLeftCards",
                "GameReturnedCards",
                "GameSuperSkyboundArtCards",
                "GameInplayMetamorphoseCards",
                "TurnDestroyCards",
                "TurnWhenHealingCount",
                "GameBurialRiteCards",
                "TurnBurialRiteCards",
                "BurialRiteOrDiscardCardHandIndexList",
                "GameReanimatedCards",
                "AddToDeckCardList",
                "TurnStartLifeList",
                "GameSkillReturnCardCountList",
                "GameSkillDiscardCountList",
                "GameSkillBuffCountList",
                "GameSkillMetamorphoseCountList",
                "GameQuickAttackCards"
            });

        // These are the BattlePlayerBase scalar inputs read by the native
        // condition/history code. Keeping the list beside the synchronized
        // lists makes the coverage auditable and prevents a newly discovered
        // turn/PP/evolution condition from silently falling back to the local
        // mirror on the receiving peer.
        internal static IReadOnlyList<string> SynchronizedScalarNames { get; } =
            Array.AsReadOnly(new[]
            {
                "Turn",
                "IsSelfTurn",
                "Pp",
                "PpTotal",
                "Bp",
                "EpTotal",
                "CurrentEpCount",
                "EvolveWaitTurnCount",
                "NowTurnEvol",
                "IsEpEvolveThisTurn",
                "GameUsedEpCount",
                "TurnUsedEpCount",
                "IsAlreadyChoiceBraveInThisTurn",
                "IsChoiceBraveEffectTiming",
                "TurnNecromanceCount",
                "GameNecromanceCount",
                "GameUsedPpCount",
                "RallyCount",
                "DeckBanishCount",
                "GameResonanceStartCount",
                "TurnResonanceStartCount",
                "GameUsedWhiteRitualCount",
                "LastInplayWhiteRitualStack",
                "GameSkillDiscardCount",
                "IsShortageDeck",
                "IsShortageDeckLose",
                "extraTurnCount",
                "cardTotalNum",
                "_cumulativeEvolutionCount"
            });

        internal static bool ShouldAttachPreActionHistory(string uri, int turn)
        {
            return !string.Equals(uri, "TurnStart", StringComparison.Ordinal) ||
                turn > 1;
        }
    }
}
