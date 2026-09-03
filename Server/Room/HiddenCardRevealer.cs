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
    ///
    /// Three signals feed the reveal:
    ///   1. a uList move out of a hidden zone into a visible one, and
    ///   2. an orderList move whose is_open positions explicitly mark a draw
    ///      (or other state change) as public, and
    ///   3. an orderList register in which the sender declares cards public
    ///      without moving them (a card opened while staying in hand).
    /// Both end up in the same knownList, because that is the only receive-side
    /// field able to express "the opponent's card at index N is face up now".
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

        // orderList registers whose payload names cards the sender is making
        // public without moving them. RegisterTool.OrderListParameter defines
        // 19 others; those are either re-derived by the receiver's own skill
        // engine or already reach it through uList / targetList, which is why
        // dropping them costs nothing. openMyCards is the one whose
        // information exists nowhere else.
        //
        // It is produced by RegisterOpenMyCards, which covers every mechanism
        // that opens a card in place: RegisterValidate.IsSendOpenMyCardsSkill
        // (self-turn-end open_card, on-banish hand-self), IsOpenMyHandSkill
        // (reveals the whole hand), Skill_update_deck.IsOpen, and
        // Skill_token_draw with an invisible target.
        private static readonly HashSet<string> RevealRegisters =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "openMyCards"
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
                injected.Add(CreateKnownCard(playIndex, playCardId, playCardId > 0));
            }

            // FusionMove() resolves the selected ingredients from the
            // receiver's OpponentTargetDataList. Those cards are normally
            // hidden hand placeholders, so the relay must disclose their
            // identities before the targetList envelope is renamed and the
            // sender perspective is flipped. Do not apply this to ordinary
            // target selection: only fusion consumes targetList as card
            // ingredients.
            foreach (int index in EnumerateFusionMaterialIndexes(message))
            {
                if (!resolvedIndexes.Add(index))
                    continue;
                if (identities.TryResolve(sourceIsHost, index, out int cardId))
                    injected.Add(CreateKnownCard(index, cardId, true));
            }

            foreach (int index in EnumerateHiddenMoveIndexes(message))
            {
                if (!resolvedIndexes.Add(index))
                    continue;
                // Never invent an identity. An index the server cannot resolve
                // stays hidden, exactly as it is today.
                if (identities.TryResolve(sourceIsHost, index, out int cardId))
                    injected.Add(CreateKnownCard(index, cardId, true));
            }

            foreach (int index in EnumerateOpenMoveIndexes(message))
            {
                if (!resolvedIndexes.Add(index))
                    continue;
                // is_open is an explicit sender declaration. Keep a
                // placeholder when the identity is unavailable so the stock
                // receiver can still pass its open-card condition gate.
                identities.TryResolve(sourceIsHost, index, out int cardId);
                if (cardId <= 0)
                {
                    Plugin.Logger.LogWarning(
                        $"[HiddenCardReveal] unresolved open move index {index} " +
                        $"from {(sourceIsHost ? "host" : "guest")}");
                }
                injected.Add(CreateKnownCard(index, cardId, true));
            }

            foreach (int index in EnumerateDeclaredOpenIndexes(message))
            {
                if (!resolvedIndexes.Add(index))
                    continue;
                // Unlike a zone move, the sender has explicitly declared this
                // card public, so it stays open even when the identity cannot
                // be resolved: NetworkExecutionInfoCreator gates the skill on
                // the index alone, and letting the effect resolve matters more
                // than showing the right card face.
                identities.TryResolve(sourceIsHost, index, out int cardId);
                if (cardId <= 0)
                {
                    Plugin.Logger.LogWarning(
                        $"[HiddenCardReveal] unresolved open card index {index} " +
                        $"from {(sourceIsHost ? "host" : "guest")}");
                }
                injected.Add(CreateKnownCard(index, cardId, true));
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

            // The identity is the one thing the server cannot verify on its
            // own: a wrong index -> cardId mapping produces a message that
            // looks correct here and fails silently inside the receiver's
            // ReplaceReceivedCard. Log the pairs so they can be compared
            // against what the opponent client actually renders.
            Plugin.Logger.LogInfo(
                $"[HiddenCardReveal] {uri} from {(sourceIsHost ? "host" : "guest")}: " +
                DescribeInjected(injected));
            return injected.Count;
        }

        private static string DescribeInjected(List<JObject> injected)
        {
            var parts = new List<string>(injected.Count);
            for (int i = 0; i < injected.Count; i++)
            {
                JObject entry = injected[i];
                TryGetInt(entry["idx"], out int index);
                string identity = TryGetInt(entry["cardId"], out int cardId)
                    ? cardId.ToString()
                    : "hidden";
                bool isOpen = TryGetInt(entry["is_open"], out int open) && open == 1;
                parts.Add($"{index}=>{identity}{(isOpen ? "" : "(closed)")}");
            }
            return string.Join(" ", parts.ToArray());
        }

        /// <summary>
        /// Applies the current card identities declared by metamorphose
        /// registers. The register is a sender-side orderList entry and is
        /// therefore authoritative for the sender's index space. Callers
        /// intentionally invoke this after PlayActions reveals so that the
        /// action's playIdx still resolves to the card that was played before
        /// its fusion transformation.
        /// </summary>
        internal static int ApplyMetamorphoses(
            JObject message,
            bool sourceIsHost,
            CardIdentityRegistry identities)
        {
            if (message == null || identities == null)
                return 0;

            JArray orderList = message["orderList"] as JArray;
            if (orderList == null)
                return 0;

            int applied = 0;
            for (int i = 0; i < orderList.Count; i++)
            {
                JObject order = orderList[i] as JObject;
                JObject metamorphose = order == null
                    ? null
                    : order["metamorphose"] as JObject;
                if (metamorphose == null || !IsSenderOwned(metamorphose))
                    continue;

                JObject after = metamorphose["after"] as JObject;
                if (after == null ||
                    !TryGetInt(after["cardId"], out int cardId) || cardId <= 0)
                {
                    continue;
                }

                foreach (int index in EnumerateIndexes(metamorphose))
                {
                    if (identities.Transform(sourceIsHost, index, cardId))
                        applied++;
                }
            }
            return applied;
        }

        private static IEnumerable<int> EnumerateFusionMaterialIndexes(JObject message)
        {
            if (!TryGetInt(message["type"], out int actionType) ||
                actionType != (int)NetworkBattleDefine.PlayActionType.FUSION)
            {
                yield break;
            }

            JArray targets = message["targetList"] as JArray;
            if (targets == null)
                yield break;

            for (int i = 0; i < targets.Count; i++)
            {
                JObject target = targets[i] as JObject;
                if (target == null || !IsSenderOwned(target))
                    continue;

                if (TryGetInt(target["targetIdx"], out int index) && index > 0)
                    yield return index;
            }
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

        /// <summary>
        /// Yields indexes explicitly marked public by RegisterStateChangeCard.
        /// Its is_open value is a list of positions into the move's idx array,
        /// not a list of card indexes. Ordinary hidden draws have no is_open
        /// field and therefore remain hidden.
        /// </summary>
        private static IEnumerable<int> EnumerateOpenMoveIndexes(JObject message)
        {
            JArray orderList = message["orderList"] as JArray;
            if (orderList == null)
                yield break;

            for (int i = 0; i < orderList.Count; i++)
            {
                JObject order = orderList[i] as JObject;
                if (order == null)
                    continue;

                JObject move = order["move"] as JObject;
                if (move == null || !IsSenderOwned(move))
                    continue;

                JToken openToken = move["is_open"];
                JArray indexes = move["idx"] as JArray;
                if (indexes == null)
                    indexes = move["idxList"] as JArray;
                JArray openPositions = openToken as JArray;
                if (indexes == null || openPositions == null)
                    continue;

                for (int j = 0; j < openPositions.Count; j++)
                {
                    if (!TryGetInt(openPositions[j], out int position) ||
                        position < 0 || position >= indexes.Count)
                    {
                        continue;
                    }

                    if (TryGetInt(indexes[position], out int index) && index > 0)
                        yield return index;
                }
            }
        }

        /// <summary>
        /// Yields the indexes the sender declared public through an orderList
        /// reveal register.
        ///
        /// orderList is a send-only channel: SendCardDataMaker writes it and no
        /// client code reads it back, so the official server was the party that
        /// translated these registers into the receiver's fields. A card opened
        /// in place never appears in uList - it does not change zone - which is
        /// why the move rule above cannot see it.
        /// </summary>
        private static IEnumerable<int> EnumerateDeclaredOpenIndexes(JObject message)
        {
            JArray orderList = message["orderList"] as JArray;
            if (orderList == null)
                yield break;

            for (int i = 0; i < orderList.Count; i++)
            {
                // OrderListCreate emits one register per entry, keyed by
                // RegisterActionBase.GetUriMsg().
                JObject order = orderList[i] as JObject;
                if (order == null)
                    continue;

                foreach (JProperty register in order.Properties())
                {
                    if (!RevealRegisters.Contains(register.Name))
                        continue;
                    // RegisterOpenMyCards strips isSelf and carries no cardId,
                    // so the index list is the whole payload.
                    JObject payload = register.Value as JObject;
                    if (payload == null)
                        continue;
                    foreach (int index in EnumerateIndexes(payload))
                        yield return index;
                }
            }
        }

        private static IEnumerable<int> EnumerateIndexes(JObject entry)
        {
            // MakeUList writes idxList, while RegisterActionBase.MakeSendData
            // puts the whole index list under idx, so either key can hold an
            // array. idxList wins when both are present.
            return EnumerateIndexToken(entry["idxList"] ?? entry["idx"]);
        }

        private static IEnumerable<int> EnumerateIndexToken(JToken indexes)
        {
            if (indexes == null)
                yield break;

            if (indexes is JArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    // MakeUList appends a -99 sentinel when the deck ran short.
                    if (TryGetInt(array[i], out int index) && index > 0)
                        yield return index;
                }
                yield break;
            }

            // A register can also send idx as a grouped string; those carry no
            // resolvable index, and TryGetInt rejects them.
            if (TryGetInt(indexes, out int single) && single > 0)
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

        private static JObject CreateKnownCard(int index, int cardId, bool isOpen)
        {
            var result = new JObject
            {
                ["idx"] = index,
                ["isSelf"] = 1,
                ["is_open"] = isOpen ? 1 : 0
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
