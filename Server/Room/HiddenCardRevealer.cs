using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Shadowbus.Server.Room
{
    /// <summary>
    /// Restores the identity reveals the official relay injected into battle
    /// messages.
    ///
    /// No client code produces a knownList - grep the stock assembly and only
    /// the receive path (NetworkBattleReceiver) and the enum definition exist.
    /// The server owned both shuffles, so it was the only party able to tell a
    /// player which of the opponent's face-down cards just became visible.
    /// Without those reveals NetworkBattleData.ReplaceReceivedCards skips the
    /// card (its unapproved entry carries cardId 0), the opponent's deck slot
    /// stays a dummy, and effects such as a deck-self summon never render.
    ///
    /// The rule is mechanical rather than per-card: a card needs revealing
    /// exactly when its identity cannot be derived from the skill itself, i.e.
    /// when it already existed in a hidden zone. Cards created by the skill
    /// (tokens) are generated identically on both sides and need no reveal.
    /// </summary>
    internal static class HiddenCardRevealer
    {
        // Zones whose contents the opponent cannot see. Only a card leaving
        // one of these carries an identity the receiver does not have.
        private static readonly HashSet<int> HiddenSourcePlaces = new HashSet<int>
        {
            (int)NetworkBattleDefine.NetworkCardPlaceState.Deck,
            (int)NetworkBattleDefine.NetworkCardPlaceState.Hand
        };

        // Zones the receiver has to render. Moves into Deck/Hand/None are
        // deliberately absent: the card stays hidden and revealing it would
        // leak information the official server never disclosed.
        private static readonly HashSet<int> RevealedTargetPlaces = new HashSet<int>
        {
            (int)NetworkBattleDefine.NetworkCardPlaceState.Field,
            (int)NetworkBattleDefine.NetworkCardPlaceState.Cemetery,
            (int)NetworkBattleDefine.NetworkCardPlaceState.Banish,
            (int)NetworkBattleDefine.NetworkCardPlaceState.FusionIngredient,
            (int)NetworkBattleDefine.NetworkCardPlaceState.Riding,
            (int)NetworkBattleDefine.NetworkCardPlaceState.Reservation,
            (int)NetworkBattleDefine.NetworkCardPlaceState.Unite
        };

        /// <summary>
        /// Adds the identities this message exposes to its knownList and
        /// returns how many entries were injected.
        ///
        /// Must run on the sender's perspective, before isSelf is flipped: the
        /// injected entries use isSelf = 1 so that the flip turns them into the
        /// receiver's "opponent card" form, which is what ReplaceReceivedCards
        /// consumes.
        ///
        /// The unapproved list is never modified. The stock receiver already
        /// reconciles uList against knownList (see
        /// NetworkBattleData.BeforeSettingReceiveData, which drops deck-to-
        /// banish unapproved entries whose knownList entry is open), and that
        /// reconciliation only holds while uList keeps its native content.
        /// </summary>
        internal static int Inject(
            JObject message,
            string uri,
            bool sourceIsHost,
            CardIdentityRegistry identities)
        {
            if (message == null || identities == null)
                return 0;

            // Entries the sender revealed itself (banish, discard, open token
            // draw) teach the registry indexes that were never part of the
            // dealt deck, so later moves of the same card stay resolvable.
            LearnDeclaredIdentities(message, sourceIsHost, identities);

            JArray knownList = message["knownList"] as JArray;
            var resolvedIndexes = new HashSet<int>();
            CollectSenderIndexes(knownList, resolvedIndexes);

            var injected = new List<JObject>();
            // playIdx is the sender's acting card in any zone, so it is only a
            // safe reveal on PlayActions, where the card is being played and
            // therefore becomes public. Other messages carry it for cards that
            // may still be hidden in hand; those go through the uList rule
            // below or stay hidden.
            if (string.Equals(uri, "PlayActions", StringComparison.Ordinal) &&
                TryGetInt(message["playIdx"], out int playIndex) && playIndex > 0 &&
                resolvedIndexes.Add(playIndex))
            {
                // Keep a hidden placeholder when the identity is unknown:
                // GetPlayCard() resolves the opponent card object by index
                // alone, so dropping the entry would drop the whole action.
                identities.TryResolve(sourceIsHost, playIndex, out int playCardId);
                injected.Add(CreateKnownCard(playIndex, playCardId));
            }

            foreach (int index in EnumerateHiddenMoveIndexes(message))
            {
                if (!resolvedIndexes.Add(index))
                    continue;
                // Never invent an identity. An index the server cannot resolve
                // stays hidden, exactly as it is today.
                if (identities.TryResolve(sourceIsHost, index, out int cardId))
                    injected.Add(CreateKnownCard(index, cardId));
            }

            if (injected.Count == 0)
                return 0;

            if (knownList == null)
            {
                knownList = new JArray();
                message["knownList"] = knownList;
            }
            for (int i = 0; i < injected.Count; i++)
                knownList.Add(injected[i]);
            return injected.Count;
        }

        private static void LearnDeclaredIdentities(
            JObject message,
            bool sourceIsHost,
            CardIdentityRegistry identities)
        {
            JArray unapprovedList = message["uList"] as JArray;
            if (unapprovedList == null)
                return;

            for (int i = 0; i < unapprovedList.Count; i++)
            {
                JObject entry = unapprovedList[i] as JObject;
                if (entry == null || !IsSenderOwned(entry) ||
                    !TryGetInt(entry["cardId"], out int cardId) || cardId <= 0)
                {
                    continue;
                }

                // MakeUList merges moves that share an identity, so one entry
                // can list several indexes of the same card.
                foreach (int index in EnumerateIndexes(entry))
                    identities.Learn(sourceIsHost, index, cardId);
            }
        }

        private static IEnumerable<int> EnumerateHiddenMoveIndexes(JObject message)
        {
            JArray unapprovedList = message["uList"] as JArray;
            if (unapprovedList == null)
                yield break;

            for (int i = 0; i < unapprovedList.Count; i++)
            {
                JObject entry = unapprovedList[i] as JObject;
                if (entry == null || !IsSenderOwned(entry) || !IsHiddenReveal(entry))
                    continue;
                foreach (int index in EnumerateIndexes(entry))
                    yield return index;
            }
        }

        private static bool IsHiddenReveal(JObject entry)
        {
            if (!TryGetInt(entry["from"], out int fromPlace) ||
                !HiddenSourcePlaces.Contains(fromPlace))
            {
                return false;
            }

            JToken destination = entry["to"];
            if (destination is JArray destinations)
            {
                for (int i = 0; i < destinations.Count; i++)
                {
                    if (TryGetInt(destinations[i], out int place) &&
                        RevealedTargetPlaces.Contains(place))
                    {
                        return true;
                    }
                }
                return false;
            }

            return TryGetInt(destination, out int toPlace) &&
                RevealedTargetPlaces.Contains(toPlace);
        }

        private static IEnumerable<int> EnumerateIndexes(JObject entry)
        {
            JArray indexes = entry["idxList"] as JArray;
            if (indexes != null)
            {
                for (int i = 0; i < indexes.Count; i++)
                {
                    // MakeUList appends a -99 sentinel when the deck ran short.
                    if (TryGetInt(indexes[i], out int index) && index > 0)
                        yield return index;
                }
                yield break;
            }

            if (TryGetInt(entry["idx"], out int single) && single > 0)
                yield return single;
        }

        private static void CollectSenderIndexes(JArray knownList, HashSet<int> target)
        {
            if (knownList == null)
                return;

            for (int i = 0; i < knownList.Count; i++)
            {
                JObject entry = knownList[i] as JObject;
                if (entry == null || !IsSenderOwned(entry))
                    continue;
                foreach (int index in EnumerateIndexes(entry))
                    target.Add(index);
            }
        }

        private static JObject CreateKnownCard(int index, int cardId)
        {
            var result = new JObject
            {
                ["idx"] = index,
                ["isSelf"] = 1,
                ["is_open"] = cardId > 0 ? 1 : 0
            };
            if (cardId > 0)
                result["cardId"] = cardId;
            return result;
        }

        private static bool IsSenderOwned(JObject entry)
        {
            // Entries flagged as the opponent's describe the receiver's own
            // cards, which the receiver already knows.
            return !TryGetInt(entry["isSelf"], out int side) || side != 0;
        }

        private static bool TryGetInt(JToken value, out int result)
        {
            result = 0;
            return value != null && int.TryParse(value.ToString(), out result);
        }
    }
}
