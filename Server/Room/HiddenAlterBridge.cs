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
    ///   spellboost: "aN"/"sN" → spellboost (SetSpellChargeCount, absolute)
    ///   unionburst: "a+N" → unionburst (UnionBurstCount, absolute)
    ///   other: "o"/"l" → attachTarget (CreateAndAttachSkill)
    ///
    /// cost is EXCLUDED: it requires base cost (cardId → CardMaster lookup),
    /// which is unsafe from the socket thread without a main-thread hook. Will
    /// be added when a safe base-cost cache is available.
    ///
    /// type=del for RegisterAttach removes a previously-attached skill.
    ///
    /// Must run AFTER HiddenCardRevealer.Inject: the revealer decides which
    /// indexes enter the known domain (creates knownList entries), this bridge
    /// only adds state fields to those existing entries.
    /// </summary>
    internal static class HiddenAlterBridge
    {
        private static readonly HashSet<string> AlterKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            // cost EXCLUDED (needs base cost from CardMaster, unsafe on socket thread)
            "atk", "life", "clan", "tribe", "spellboost", "unionburst",
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
            bool sourceIsHost,
            CardIdentityRegistry identities)
        {
            if (message == null || identities == null)
                return 0;

            JArray orderList = message["orderList"] as JArray;
            if (orderList == null || orderList.Count == 0)
                return 0;

            // Fold all alter registers per index into a single AlterState,
            // then write the state to that index's knownList entry (if present).
            var states = new Dictionary<int, AlterState>();
            for (int i = 0; i < orderList.Count; i++)
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
                    }
                }
            }

            if (states.Count == 0)
                return 0;

            // Now write each index's folded state to its knownList entry.
            // Only write if the entry exists (HiddenCardRevealer created it);
            // never invent a card.
            JArray knownList = message["knownList"] as JArray;
            if (knownList == null)
                return 0;

            int touched = 0;
            foreach (var pair in states)
            {
                int index = pair.Key;
                AlterState state = pair.Value;

                JObject entry = FindKnownCard(knownList, index, sourceIsHost);
                if (entry == null)
                {
                    // The card is not revealed, so the receiver should not see
                    // its state. Skip silently.
                    continue;
                }

                WriteState(entry, state);
                touched++;
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

            // spellboost: "aN"/"sN" → absolute (receiver uses SetSpellChargeCount)
            if (TryGetString(payload["spellboost"], out string spellboostStr))
            {
                string raw = spellboostStr.TrimStart('a', 's');
                if (TryGetInt(raw, out int spellboost))
                    state.Spellboost = spellboost;
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
                    state.SetAtk = value;
            }
            else
            {
                if (isAdd)
                    state.AddLife = (state.AddLife ?? 0) + value;
                else
                    state.SetLife = value;
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

        private static JObject FindKnownCard(JArray knownList, int index, bool sourceIsHost)
        {
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
            public int? Unionburst;    // absolute

            public List<string> AttachTarget;
            public List<string> AttachTargetDel; // type=del
        }
    }
}
