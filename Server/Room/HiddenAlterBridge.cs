using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Shadowbus.Server.Room
{
    /// <summary>
    /// Translates the sender's orderList alter registers into knownList card
    /// fields the receiver's NetworkBattleReceiver.MakeReceiveCardData consumes.
    ///
    /// The stock relay translated state changes sent via orderList (cost, atk,
    /// life, clan, tribe, spellboost, unionburst, attachTarget) into the
    /// receiver's knownList card entries. The sender writes RegisterCostChangeCard,
    /// RegisterAttach, RegisterSpellboost, RegisterChangeUnionBurstCount to
    /// orderList, but never reads orderList back; the receiver reads discrete
    /// knownList fields (cost, addAtk, setAtk, addLife, setLife, clan, tribe,
    /// spellboost, unionburst, attachTarget) and applies them via
    /// ReplaceReceivedCard.CopyDataToActualCard.
    ///
    /// Field mappings (sender orderList key → receiver knownList field):
    ///   atk: "a+N" → addAtk (OffenseAddModifier)
    ///   atk: "s+N" → setAtk (OffenseSetModifier)
    ///   life: "a+N" → addLife, life: "s+N" → setLife
    ///   clan: "N" → clan (GiveChangeAffiliation)
    ///   tribe: "sN" → tribe (replace), tribe: "aN" → tribe (add)
    ///   spellboost: "aN" (increment) / "sN" (set) → absolute spellboost
    ///   unionburst: "a+N" → unionburst (UnionBurstCount, absolute)
    ///   other: "o"/"l" → attachTarget (CreateAndAttachSkill)
    ///
    /// For a revealed card with a supported self-targeting spellboost cost DSL,
    /// the bridge also writes the resulting absolute cost. Other cost changes
    /// remain outside this bridge until their modifier stack is represented.
    ///
    /// type=del for RegisterAttach removes a previously-attached skill.
    ///
    /// Must run AFTER HiddenCardRevealer.Inject so a card becoming public in
    /// this message receives its accumulated state. Still-hidden spellboost
    /// targets get anonymous knownList entries containing no cardId.
    /// </summary>
    internal static class HiddenAlterBridge
    {
        private static readonly HashSet<string> AlterKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            // Cost operations are persisted as raw modifiers and folded when
            // the identity is projected at reveal time.
            "atk", "life", "clan", "tribe", "spellboost", "cost", "unionburst",
            "other", "attachTarget"
        };

        /// <summary>
        /// Folds the sender's orderList alter registers into knownList card
        /// entries the receiver will consume. Returns the count of indexes
        /// touched (for diagnostics).
        ///
        /// Must run on the sender's perspective (before isSelf flip).
        /// </summary>
        internal static int Inject(
            JObject message,
            string uri,
            bool sourceIsHost,
            CardIdentityRegistry identities,
            HiddenCardStateRegistry hiddenStates)
        {
            if (message == null || identities == null || hiddenStates == null)
                return 0;

            JArray orderList = message["orderList"] as JArray;

            // Echo repeats the receiver's reconstruction of the same action.
            // It must never advance the server's hidden state a second time.
            bool persistSpellboost = !string.Equals(uri, "Echo", StringComparison.Ordinal);

            // Fold all alter registers per index into a single AlterState.
            var states = new Dictionary<int, AlterState>();
            for (int i = 0; orderList != null && i < orderList.Count; i++)
            {
                JObject order = orderList[i] as JObject;
                if (order == null)
                    continue;
                foreach (JProperty register in order.Properties())
                {
                    JObject payload = register.Value as JObject;
                    if (payload == null)
                        continue;

                    // Only consume alter registers. skillConditionCheck,
                    // openMyCards, and other registers are handled elsewhere.
                    bool isAlter = false;
                    foreach (JProperty field in payload.Properties())
                    {
                        if (AlterKeys.Contains(field.Name))
                        {
                            isAlter = true;
                            break;
                        }
                    }
                    if (!isAlter)
                        continue;

                    // Alter registers carry idx (array or scalar).
                    foreach (int index in EnumerateIndexes(payload))
                    {
                        if (!states.TryGetValue(index, out AlterState state))
                        {
                            state = new AlterState();
                            states[index] = state;
                        }
                        ApplyRegister(payload, state);
                        bool senderOwned = IsSenderOwned(payload);
                        if (persistSpellboost && senderOwned &&
                            TryGetString(payload["spellboost"], out string spellboostOperation) &&
                            hiddenStates.ApplySpellboost(
                                sourceIsHost,
                                index,
                                spellboostOperation,
                                out int absoluteSpellboost))
                        {
                            state.Spellboost = absoluteSpellboost;
                        }
                        if (persistSpellboost && senderOwned &&
                            TryGetString(payload["cost"], out string costOperation) &&
                            hiddenStates.ApplyCostOperation(
                                sourceIsHost,
                                index,
                                costOperation))
                        {
                            if (state.CostOperations == null)
                                state.CostOperations = new List<string>();
                            state.CostOperations.Add(costOperation);
                        }
                        if (persistSpellboost && senderOwned)
                        {
                            foreach (string key in new[] { "atk", "life" })
                            {
                                if (TryGetString(payload[key], out string combatOperation))
                                {
                                    hiddenStates.ApplyCombatOperation(
                                        sourceIsHost,
                                        index,
                                        key,
                                        combatOperation);
                                }
                            }
                        }
                    }
                }
            }

            // First restore state accumulated by earlier messages to cards the
            // revealer made public in this message (notably playIdx).
            JArray knownList = message["knownList"] as JArray;
            int touched = 0;
            if (knownList != null)
            {
                for (int i = 0; i < knownList.Count; i++)
                {
                    JObject entry = knownList[i] as JObject;
                    if (entry == null || !IsSenderOwned(entry))
                        continue;

                    foreach (int index in EnumerateIndexes(entry))
                    {
                        bool hasSpellboost = hiddenStates.TryGetSpellboost(
                            sourceIsHost,
                            index,
                            out int spellboost);
                        bool hasCost = hiddenStates.HasCostOperations(sourceIsHost, index);
                        bool hasCombat = hiddenStates.TryGetCombatState(
                            sourceIsHost,
                            index,
                            out int? addAtk,
                            out int? setAtk,
                            out int? addLife,
                            out int? setLife);
                        if (hasSpellboost || hasCost || hasCombat)
                        {
                            if (hasSpellboost)
                            {
                                WriteSpellboostState(
                                    entry,
                                    sourceIsHost,
                                    index,
                                    spellboost,
                                    identities,
                                    hiddenStates);
                            }
                            else
                            {
                                if (hasCost)
                                {
                                    WriteCostState(
                                        entry,
                                        sourceIsHost,
                                        index,
                                        identities,
                                        hiddenStates);
                                }
                            }
                            if (hasCombat)
                                WriteCombatState(entry, addAtk, setAtk, addLife, setLife);
                            touched++;
                        }
                    }
                }
            }

            foreach (var pair in states)
            {
                int index = pair.Key;
                AlterState state = pair.Value;

                JObject entry = FindKnownCard(knownList, index);
                if (entry == null)
                {
                    // Spellboost is allowed to cross the hidden boundary by
                    // index. CardId stays absent, so the opponent learns no
                    // identity; NetworkBattleData routes it to
                    // SetPrivateCardSpellboost on the existing dummy card.
                    if (!state.Spellboost.HasValue)
                        continue;
                    if (knownList == null)
                    {
                        knownList = new JArray();
                        message["knownList"] = knownList;
                    }
                    entry = CreatePrivateStateCard(index);
                    knownList.Add(entry);
                }

                WriteState(entry, state);
                if (state.Spellboost.HasValue)
                {
                    WriteSpellboostState(
                        entry,
                        sourceIsHost,
                        index,
                        state.Spellboost.Value,
                        identities,
                        hiddenStates);
                }
                if (state.CostOperations != null)
                    WriteCostState(entry, sourceIsHost, index, identities, hiddenStates);
                if (hiddenStates.TryGetCombatState(
                        sourceIsHost,
                        index,
                        out int? persistedAddAtk,
                        out int? persistedSetAtk,
                        out int? persistedAddLife,
                        out int? persistedSetLife))
                {
                    WriteCombatState(
                        entry,
                        persistedAddAtk,
                        persistedSetAtk,
                        persistedAddLife,
                        persistedSetLife);
                }
                touched++;
            }

            // Once a card is public, its state is now carried by the actual
            // client card object. Drop the hidden copy so a future index reuse
            // cannot inherit stale modifiers. Anonymous cardId==0 entries stay
            // alive because the card is still hidden.
            if (knownList != null)
            {
                for (int i = 0; i < knownList.Count; i++)
                {
                    JObject entry = knownList[i] as JObject;
                    if (entry == null || !IsSenderOwned(entry))
                        continue;
                    bool isOpen = TryGetInt(entry["is_open"], out int open) && open == 1;
                    bool hasIdentity = TryGetInt(entry["cardId"], out int cardId) && cardId > 0;
                    if (!isOpen && !hasIdentity)
                        continue;
                    foreach (int index in EnumerateIndexes(entry))
                        hiddenStates.Clear(sourceIsHost, index);
                }
            }

            return touched;
        }

        private static void ApplyRegister(JObject payload, AlterState state)
        {
            // atk/life: "a+N" → add, "s+N" → set
            ApplyOffense(payload, "atk", state, isAtk: true);
            ApplyOffense(payload, "life", state, isAtk: false);

            // clan: scalar override
            if (TryGetInt(payload["clan"], out int clan))
                state.Clan = clan;

            // tribe: "sN" → replace, "aN" → add
            if (TryGetString(payload["tribe"], out string tribeStr))
            {
                if (tribeStr.StartsWith("s") && TryGetInt(tribeStr.Substring(1), out int tribeReplace))
                    state.TribeReplace = tribeReplace;
                else if (tribeStr.StartsWith("a") && TryGetInt(tribeStr.Substring(1), out int tribeAdd))
                {
                    if (state.TribeAdd == null)
                        state.TribeAdd = new List<int>();
                    state.TribeAdd.Add(tribeAdd);
                }
            }

            // unionburst: "a+N" → absolute (receiver UnionBurstCount is absolute)
            if (TryGetString(payload["unionburst"], out string unionburstStr))
            {
                string raw = unionburstStr.TrimStart('a').TrimStart('+');
                if (TryGetInt(raw, out int unionburst))
                    state.Unionburst = unionburst;
            }

            // other/attachTarget: timing/skill label (o/l), type=del for removal
            if (TryGetString(payload["other"], out string other))
            {
                bool isDel = TryGetInt(payload["type"], out int type) && type != 0;
                if (isDel)
                {
                    if (state.AttachTargetDel == null)
                        state.AttachTargetDel = new List<string>();
                    state.AttachTargetDel.Add(other);
                }
                else
                {
                    if (state.AttachTarget == null)
                        state.AttachTarget = new List<string>();
                    state.AttachTarget.Add(other);
                }
            }
            if (TryGetString(payload["attachTarget"], out string attachTarget))
            {
                if (state.AttachTarget == null)
                    state.AttachTarget = new List<string>();
                state.AttachTarget.Add(attachTarget);
            }
        }

        private static void ApplyOffense(JObject payload, string key, AlterState state, bool isAtk)
        {
            if (!TryGetString(payload[key], out string raw))
                return;

            bool isAdd = raw.StartsWith("a");
            bool isSet = raw.StartsWith("s");
            if (!isAdd && !isSet)
                return;

            string numPart = raw.Substring(1).TrimStart('+');
            if (!TryGetInt(numPart, out int value))
                return;

            if (isAtk)
            {
                if (isAdd)
                    state.AddAtk = (state.AddAtk ?? 0) + value;
                else
                {
                    state.SetAtk = value;
                    state.AddAtk = null;
                }
            }
            else
            {
                if (isAdd)
                    state.AddLife = (state.AddLife ?? 0) + value;
                else
                {
                    state.SetLife = value;
                    state.AddLife = null;
                }
            }
        }

        private static void WriteState(JObject entry, AlterState state)
        {
            // Receiver expects addAtk/setAtk (not "atk"). ReplaceReceivedCard:212-213
            // calls AddSetModifier before InheritedAddParameterBuff, so set takes
            // precedence over add. We write both if present; receiver will apply
            // set first, then add.
            if (state.SetAtk.HasValue)
                entry["setAtk"] = state.SetAtk.Value;
            if (state.AddAtk.HasValue)
                entry["addAtk"] = state.AddAtk.Value;

            if (state.SetLife.HasValue)
                entry["setLife"] = state.SetLife.Value;
            if (state.AddLife.HasValue)
                entry["addLife"] = state.AddLife.Value;

            if (state.Clan.HasValue)
                entry["clan"] = state.Clan.Value;

            // tribe: receiver expects a single value. If replace is set, use it;
            // otherwise serialize adds as comma-separated (receiver parses).
            if (state.TribeReplace.HasValue)
                entry["tribe"] = state.TribeReplace.Value;
            else if (state.TribeAdd != null && state.TribeAdd.Count > 0)
            {
                // Receiver GiveChangeAffiliation may expect "aN" prefix or raw.
                // Use the last add as representative (multiple adds are rare).
                entry["tribe"] = state.TribeAdd[state.TribeAdd.Count - 1];
            }

            if (state.Spellboost.HasValue)
                entry["spellboost"] = state.Spellboost.Value;

            if (state.Unionburst.HasValue)
                entry["unionburst"] = state.Unionburst.Value;

            // attachTarget: receiver expects a single string or array. Serialize
            // as comma-separated if multiple.
            if (state.AttachTarget != null && state.AttachTarget.Count > 0)
            {
                if (state.AttachTarget.Count == 1)
                    entry["attachTarget"] = state.AttachTarget[0];
                else
                    entry["attachTarget"] = string.Join(",", state.AttachTarget);
            }
            // AttachTargetDel: no receiver field for "remove skill", so we cannot
            // express it. Log a warning if present.
            if (state.AttachTargetDel != null && state.AttachTargetDel.Count > 0)
            {
                Plugin.Logger.LogWarning(
                    $"[HiddenAlterBridge] attachTarget del not supported: " +
                    $"{string.Join(",", state.AttachTargetDel)}");
            }
        }

        private static JObject FindKnownCard(JArray knownList, int index)
        {
            if (knownList == null)
                return null;

            // knownList entries before isSelf flip have isSelf=1 (sender-owned).
            // We're running before the flip, so we look for isSelf=1.
            for (int i = 0; i < knownList.Count; i++)
            {
                JObject entry = knownList[i] as JObject;
                if (entry == null)
                    continue;

                if (!IsSenderOwned(entry))
                    continue;

                // idx can be array or scalar.
                foreach (int entryIndex in EnumerateIndexes(entry))
                {
                    if (entryIndex == index)
                        return entry;
                }
            }
            return null;
        }

        private static void WriteCombatState(
            JObject entry,
            int? addAtk,
            int? setAtk,
            int? addLife,
            int? setLife)
        {
            if (setAtk.HasValue)
                entry["setAtk"] = setAtk.Value;
            if (addAtk.HasValue)
                entry["addAtk"] = addAtk.Value;
            if (setLife.HasValue)
                entry["setLife"] = setLife.Value;
            if (addLife.HasValue)
                entry["addLife"] = addLife.Value;
        }

        private static JObject CreatePrivateStateCard(int index)
        {
            return new JObject
            {
                ["idx"] = index,
                ["isSelf"] = 1,
                ["is_open"] = 0
            };
        }

        private static void WriteSpellboostState(
            JObject entry,
            bool sourceIsHost,
            int index,
            int spellboost,
            CardIdentityRegistry identities,
            HiddenCardStateRegistry hiddenStates)
        {
            entry["spellboost"] = spellboost;

            // A cost on an anonymous card would be ignored by the stock
            // CardId==0 receive branch. Add it only when this entry is already
            // revealing the identity, which is also when play legality needs
            // the receiver's actual card cost to be correct.
            WriteCostState(entry, sourceIsHost, index, identities, hiddenStates, spellboost);
        }

        private static void WriteCostState(
            JObject entry,
            bool sourceIsHost,
            int index,
            CardIdentityRegistry identities,
            HiddenCardStateRegistry hiddenStates,
            int? explicitSpellboost = null)
        {
            if (!TryGetInt(entry["cardId"], out int cardId) || cardId <= 0)
                return;

            int spellboost;
            if (explicitSpellboost.HasValue)
                spellboost = explicitSpellboost.Value;
            else if (hiddenStates == null ||
                     !hiddenStates.TryGetSpellboost(sourceIsHost, index, out spellboost))
                spellboost = 0;

            int cost;
            if (!identities.TryGetSpellboostAdjustedCost(
                    sourceIsHost,
                    index,
                    spellboost,
                    out cost) &&
                !identities.TryGetBaseCost(sourceIsHost, index, out cost))
                return;

            if (hiddenStates != null)
            {
                foreach (string operation in hiddenStates.GetCostOperations(sourceIsHost, index))
                    cost = ApplyCostOperation(cost, operation);
            }
            entry["cost"] = cost;
        }

        private static int ApplyCostOperation(int current, string operation)
        {
            if (string.IsNullOrEmpty(operation))
                return current;
            char kind = operation[0];
            if (!int.TryParse(operation.Substring(1).TrimStart('+'), out int value))
                return current;
            switch (kind)
            {
                case 'a':
                    return Math.Max(0, current + value);
                case 's':
                    return Math.Max(0, value);
                case 'd':
                    return Math.Max(0, (current + 1) / 2);
                case 'D':
                    return Math.Max(0, current / 2);
                default:
                    return current;
            }
        }

        private static bool IsSenderOwned(JObject entry)
        {
            // isSelf=0 means opponent (receiver's own cards, which the receiver
            // already knows). We only augment sender-owned entries.
            return !TryGetInt(entry["isSelf"], out int side) || side != 0;
        }

        private static IEnumerable<int> EnumerateIndexes(JObject entry)
        {
            // Alter registers use idx (array or scalar), uList uses idxList or idx.
            JToken indexes = entry["idxList"] ?? entry["idx"];
            if (indexes == null)
                yield break;

            if (indexes is JArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    if (TryGetInt(array[i], out int index) && index > 0)
                        yield return index;
                }
            }
            else if (TryGetInt(indexes, out int single) && single > 0)
            {
                yield return single;
            }
        }

        private static bool TryGetInt(JToken value, out int result)
        {
            result = 0;
            return value != null && int.TryParse(value.ToString(), out result);
        }

        private static bool TryGetInt(string value, out int result)
        {
            result = 0;
            return value != null && int.TryParse(value, out result);
        }

        private static bool TryGetString(JToken value, out string result)
        {
            result = null;
            if (value == null || value.Type == JTokenType.Null)
                return false;
            result = value.ToString();
            return !string.IsNullOrEmpty(result);
        }

        private sealed class AlterState
        {
            // atk/life: add/set are separate (receiver applies set first, then add)
            public int? AddAtk;
            public int? SetAtk;
            public int? AddLife;
            public int? SetLife;

            public int? Clan;
            public int? TribeReplace;  // "sN"
            public List<int> TribeAdd; // "aN"

            public int? Spellboost;    // absolute
            public List<string> CostOperations;
            public int? Unionburst;    // absolute

            public List<string> AttachTarget;
            public List<string> AttachTargetDel; // type=del
        }
    }
}
