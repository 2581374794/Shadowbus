using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Wizard;

namespace Shadowbus.Server.Room
{
    /// <summary>
    /// Authoritative card index to cardId map for one battle session.
    ///
    /// The stock client only ever knows its own deck contents; the official
    /// relay owned both shuffles and was therefore the only party able to
    /// reveal a hidden card's identity to the opponent. This registry restores
    /// that authority: it is seeded from the shuffled decks the server itself
    /// dealt, and is consulted by <see cref="HiddenCardRevealer"/> when a card
    /// leaves a hidden zone.
    ///
    /// Index numbering follows the contract established by
    /// SocketIoServer.CreateDeckData: the card at array position i is sent to
    /// the client as idx = i + 1.
    ///
    /// Known coupling: BattlePlayerBase.AddToDeckCardIndexChange swaps card
    /// indexes when a card returns to the deck, which would invalidate this
    /// map. That path only runs once BattleMgr has an active XorShiftRandom,
    /// which is created from the idxChangeSeed / oppoIdxChangeSeed fields.
    /// The relay never sends those fields, so the mapping is stable. If they
    /// are ever added, this registry must mirror the same swap.
    /// </summary>
    internal sealed class CardIdentityRegistry
    {
        private readonly object _sync = new object();
        private readonly Dictionary<int, int> _hostCards = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _guestCards = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _hostBaseCosts = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _guestBaseCosts = new Dictionary<int, int>();
        private readonly Dictionary<int, NetworkBattleDefine.NetworkCardPlaceState> _hostZones =
            new Dictionary<int, NetworkBattleDefine.NetworkCardPlaceState>();
        private readonly Dictionary<int, NetworkBattleDefine.NetworkCardPlaceState> _guestZones =
            new Dictionary<int, NetworkBattleDefine.NetworkCardPlaceState>();
        private readonly Dictionary<int, CardParameter> _cardParameters =
            new Dictionary<int, CardParameter>();
        private readonly List<HandResidentCostRule> _handResidentCostRules =
            new List<HandResidentCostRule>();
        private readonly Dictionary<int, SpellboostCostRule> _spellboostCostRules =
            new Dictionary<int, SpellboostCostRule>();
        private readonly HashSet<int> _cardsWithoutSpellboostCostRule =
            new HashSet<int>();
        private CardMaster _cardMaster;

        /// <summary>
        /// Sets the CardMaster reference used to lazily query base costs. Must
        /// be called from the main thread before battle messages arrive.
        /// </summary>
        public void SetCardMaster(CardMaster cardMaster)
        {
            lock (_sync)
            {
                _cardMaster = cardMaster;
                _hostBaseCosts.Clear();
                _guestBaseCosts.Clear();
                _cardParameters.Clear();
                _handResidentCostRules.Clear();
                _spellboostCostRules.Clear();
                _cardsWithoutSpellboostCostRule.Clear();

                if (_cardMaster != null)
                {
                    try
                    {
                        foreach (CardParameter parameter in _cardMaster.GetAllParameters())
                        {
                            if (parameter != null && parameter.CardId > 0)
                            {
                                _cardParameters[parameter.CardId] = parameter;
                                CompileHandResidentCostRules(parameter);
                            }
                        }
                    }
                    catch
                    {
                        _cardParameters.Clear();
                        _handResidentCostRules.Clear();
                    }
                }
            }
        }

        /// <summary>
        /// Records the shuffled deck the server dealt to one side. Replaces any
        /// previous content for that side.
        /// </summary>
        public void Seed(bool isHost, int[] shuffledCards)
        {
            lock (_sync)
            {
                Dictionary<int, int> target = isHost ? _hostCards : _guestCards;
                Dictionary<int, int> costs = isHost ? _hostBaseCosts : _guestBaseCosts;
                target.Clear();
                costs.Clear();
                (isHost ? _hostZones : _guestZones).Clear();
                if (shuffledCards == null)
                    return;
                for (int i = 0; i < shuffledCards.Length; i++)
                {
                    if (shuffledCards[i] > 0)
                    {
                        target[i + 1] = shuffledCards[i];
                        (isHost ? _hostZones : _guestZones)[i + 1] =
                            NetworkBattleDefine.NetworkCardPlaceState.Deck;
                    }
                }
            }
        }

        internal void SetInitialHand(bool isHost, IList<int> indexes)
        {
            lock (_sync)
            {
                Dictionary<int, NetworkBattleDefine.NetworkCardPlaceState> zones =
                    isHost ? _hostZones : _guestZones;
                foreach (int index in new List<int>(zones.Keys))
                    zones[index] = NetworkBattleDefine.NetworkCardPlaceState.Deck;
                if (indexes == null)
                    return;
                for (int i = 0; i < indexes.Count; i++)
                {
                    int index = indexes[i];
                    if (index > 0 && zones.ContainsKey(index))
                        zones[index] = NetworkBattleDefine.NetworkCardPlaceState.Hand;
                }
            }
        }

        private void SetZone(
            bool isHost,
            int index,
            NetworkBattleDefine.NetworkCardPlaceState zone)
        {
            if (index <= 0)
                return;
            lock (_sync)
            {
                Dictionary<int, NetworkBattleDefine.NetworkCardPlaceState> zones =
                    isHost ? _hostZones : _guestZones;
                zones[index] = zone;
            }
        }

        internal int ApplyHandResidentCostRules(
            bool isHost,
            int playedIndex,
            HiddenCardStateRegistry hiddenStates)
        {
            if (playedIndex <= 0 || hiddenStates == null)
                return 0;

            lock (_sync)
            {
                Dictionary<int, int> identities = isHost ? _hostCards : _guestCards;
                if (!identities.TryGetValue(playedIndex, out int playedCardId) ||
                    !_cardParameters.TryGetValue(playedCardId, out CardParameter playedCard))
                    return 0;

                Dictionary<int, NetworkBattleDefine.NetworkCardPlaceState> zones =
                    isHost ? _hostZones : _guestZones;
                var handIndexes = new List<int>();
                foreach (KeyValuePair<int, NetworkBattleDefine.NetworkCardPlaceState> pair in zones)
                {
                    if (pair.Value == NetworkBattleDefine.NetworkCardPlaceState.Hand &&
                        pair.Key != playedIndex)
                    {
                        handIndexes.Add(pair.Key);
                    }
                }

                int applied = 0;
                for (int i = 0; i < _handResidentCostRules.Count; i++)
                {
                    HandResidentCostRule rule = _handResidentCostRules[i];
                    if (!_cardParameters.TryGetValue(rule.OwnerCardId, out CardParameter ownerCard) ||
                        !MatchesTargetCardType(rule.Target, ownerCard) ||
                        !MatchesPlayedCard(rule.Condition, playedCard) ||
                        !MatchesCondition(rule.Condition, ownerCard, handIndexes, isHost))
                    {
                        continue;
                    }

                    for (int j = 0; j < handIndexes.Count; j++)
                    {
                        int targetIndex = handIndexes[j];
                        if (!identities.TryGetValue(targetIndex, out int targetCardId) ||
                            targetCardId != rule.OwnerCardId)
                        {
                            continue;
                        }

                        int delta;
                        if (!TryEvaluateCostOption(rule.Option, out delta))
                        {
                            continue;
                        }

                        string operation = rule.Option.StartsWith("set=", StringComparison.Ordinal)
                            ? "s" + delta
                            : "a" + delta;
                        if (hiddenStates.ApplyCostOperation(isHost, targetIndex, operation))
                            applied++;
                    }
                }
                return applied;
            }
        }

        internal void ApplyMessageZones(JObject message, bool sourceIsHost)
        {
            if (message == null)
                return;

            ApplyMoveList(message["orderList"] as JArray, sourceIsHost, false);
            ApplyMoveList(message["uList"] as JArray, sourceIsHost, true);
            if (string.Equals(message["uri"]?.Value<string>(), "PlayActions", StringComparison.Ordinal) &&
                TryGetInt(message["playIdx"], out int playIndex) && playIndex > 0)
            {
                SetZone(sourceIsHost, playIndex, NetworkBattleDefine.NetworkCardPlaceState.Field);
            }
        }

        private void ApplyMoveList(JArray entries, bool sourceIsHost, bool isUnapproved)
        {
            if (entries == null)
                return;

            for (int i = 0; i < entries.Count; i++)
            {
                JObject entry = entries[i] as JObject;
                if (entry == null)
                    continue;

                JObject move = isUnapproved ? entry : entry["move"] as JObject;
                if (move == null)
                    continue;

                if (!TryGetInt(move["to"], out int destination))
                    continue;
                bool self = !TryGetInt(move["isSelf"], out int isSelf) || isSelf != 0;
                bool ownerIsHost = self ? sourceIsHost : !sourceIsHost;
                foreach (int index in EnumerateIndexes(move))
                    SetZone(ownerIsHost, index,
                        (NetworkBattleDefine.NetworkCardPlaceState)destination);
            }
        }

        private static IEnumerable<int> EnumerateIndexes(JObject entry)
        {
            JToken indexes = entry["idxList"] ?? entry["idx"];
            if (indexes is JArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    if (TryGetInt(array[i], out int index) && index > 0)
                        yield return index;
                }
                yield break;
            }

            if (TryGetInt(indexes, out int single) && single > 0)
                yield return single;
        }

        private static bool TryGetInt(JToken value, out int result)
        {
            result = 0;
            return value != null && int.TryParse(value.ToString(), out result);
        }

        private void CompileHandResidentCostRules(CardParameter card)
        {
            string[] skills = SplitNormalSkills(card.Skill);
            string[] timings = SplitNormalSkills(card.SkillTiming);
            string[] conditions = SplitNormalSkills(card.SkillCondition);
            string[] targets = SplitNormalSkills(card.SkillTarget);
            string[] options = SplitNormalSkills(card.SkillOption);
            int count = Math.Min(
                Math.Min(skills.Length, timings.Length),
                Math.Min(conditions.Length, Math.Min(targets.Length, options.Length)));

            for (int i = 0; i < count; i++)
            {
                if (!string.Equals(skills[i], "cost_change", StringComparison.Ordinal) ||
                    !string.Equals(timings[i], "when_play_other", StringComparison.Ordinal) ||
                    !ContainsHandResidentTarget(targets[i]) ||
                    !TryGetOptionExpression(options[i], "add=", out _) &&
                    !TryGetOptionExpression(options[i], "set=", out _))
                {
                    continue;
                }

                _handResidentCostRules.Add(new HandResidentCostRule
                {
                    OwnerCardId = card.CardId,
                    Condition = conditions[i],
                    Target = targets[i],
                    Option = options[i]
                });
            }
        }

        private static bool MatchesTargetCardType(string target, CardParameter card)
        {
            string cardType = GetFilterValue(target, "card_type");
            return string.IsNullOrEmpty(cardType) || MatchesCardType(cardType, card);
        }

        private static bool MatchesPlayedCard(string condition, CardParameter playedCard)
        {
            if (playedCard == null)
                return false;

            string cardType = GetFilterValue(condition, "card_type");
            if (!string.IsNullOrEmpty(cardType) && !MatchesCardType(cardType, playedCard))
                return false;

            string tribe = GetFilterValue(condition, "tribe");
            if (!string.IsNullOrEmpty(tribe) && !MatchesTribe(tribe, playedCard))
                return false;

            string clan = GetFilterValue(condition, "clan");
            if (!string.IsNullOrEmpty(clan) && !MatchesClan(clan, playedCard))
                return false;

            return true;
        }

        private bool MatchesCondition(
            string condition,
            CardParameter ownerCard,
            IList<int> handIndexes,
            bool isHost)
        {
            if (string.IsNullOrEmpty(condition))
                return true;

            string[] terms = condition.Split('&');
            for (int i = 0; i < terms.Length; i++)
            {
                string term = terms[i].Trim();
                if (term.Length == 0 ||
                    term.StartsWith("character=", StringComparison.Ordinal) ||
                    term.StartsWith("target=", StringComparison.Ordinal) ||
                    term.StartsWith("card_type=", StringComparison.Ordinal) ||
                    term.StartsWith("tribe=", StringComparison.Ordinal) ||
                    term.StartsWith("clan=", StringComparison.Ordinal))
                {
                    continue;
                }

                int open = term.IndexOf('}');
                if (open < 0)
                    return false;
                string variable = term.Substring(1, open - 1);
                string comparator = term.Substring(open + 1).Trim();
                if (!TryParseComparison(comparator, out char operation, out int expected))
                    return false;

                int actual;
                if (variable.IndexOf("hand_self.unit.count", StringComparison.Ordinal) >= 0)
                {
                    actual = CountCardsInHand(handIndexes, isHost, true);
                }
                else if (variable.IndexOf("hand_self.count", StringComparison.Ordinal) >= 0)
                {
                    actual = handIndexes.Count;
                }
                else
                {
                    // Other private counters are outside this first rule set.
                    // Leave the native client as the source of truth for them.
                    return false;
                }

                if (!Compare(actual, expected, operation))
                    return false;
            }
            return true;
        }

        private static bool TryEvaluateCostOption(
            string option,
            out int value)
        {
            value = 0;
            string expression;
            bool isAdd = TryGetOptionExpression(option, "add=", out expression);
            if (!isAdd && !TryGetOptionExpression(option, "set=", out expression))
                return false;

            if (int.TryParse(expression.Trim(), out value))
                return true;
            return false;
        }

        private int CountCardsInHand(
            IList<int> handIndexes,
            bool isHost,
            bool unitsOnly)
        {
            int count = 0;
            Dictionary<int, int> identities = isHost ? _hostCards : _guestCards;
            for (int i = 0; i < handIndexes.Count; i++)
            {
                if (!identities.TryGetValue(handIndexes[i], out int cardId) ||
                    !_cardParameters.TryGetValue(cardId, out CardParameter card))
                    continue;
                if (!unitsOnly || card.CharType == CardBasePrm.CharaType.NORMAL)
                    count++;
            }
            return count;
        }

        private static string GetFilterValue(string text, string key)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            string[] terms = text.Split('&');
            string prefix = key + "=";
            for (int i = 0; i < terms.Length; i++)
            {
                string term = terms[i].Trim();
                if (term.StartsWith(prefix, StringComparison.Ordinal))
                    return term.Substring(prefix.Length);
            }
            return null;
        }

        private static bool MatchesCardType(string cardType, CardParameter card)
        {
            if (string.Equals(cardType, "all", StringComparison.Ordinal))
                return true;
            if (string.Equals(cardType, "unit", StringComparison.Ordinal) ||
                string.Equals(cardType, "unit_and_allfield", StringComparison.Ordinal))
                return card.CharType == CardBasePrm.CharaType.NORMAL;
            if (string.Equals(cardType, "spell", StringComparison.Ordinal))
                return card.CharType == CardBasePrm.CharaType.SPELL;
            if (string.Equals(cardType, "field", StringComparison.Ordinal) ||
                string.Equals(cardType, "spell_and_field", StringComparison.Ordinal))
                return card.CharType == CardBasePrm.CharaType.FIELD ||
                    card.CharType == CardBasePrm.CharaType.CHANT_FIELD;
            return true;
        }

        private static bool MatchesTribe(string tribe, CardParameter card)
        {
            // The stock parser maps any_tribe to SkillTribeFilter(ALL, "=").
            // This is a wildcard condition, including cards whose parameter
            // list does not explicitly contain the synthetic ALL entry.
            if (string.Equals(tribe, "all", StringComparison.Ordinal) ||
                string.Equals(tribe, "any_tribe", StringComparison.Ordinal))
                return true;
            if (!Enum.TryParse(tribe.ToUpperInvariant(), out CardBasePrm.TribeType expected))
                return false;
            return card.Tribe != null && card.Tribe.Contains(expected);
        }

        private static bool MatchesClan(string clan, CardParameter card)
        {
            if (string.Equals(clan, "all", StringComparison.Ordinal))
                return true;
            if (!int.TryParse(clan, out int numeric))
                numeric = (int)CardBasePrm.GetClanType(clan);
            return (int)card.Clan == numeric;
        }

        private static bool TryParseComparison(
            string text,
            out char operation,
            out int expected)
        {
            operation = '=';
            expected = 0;
            if (string.IsNullOrEmpty(text))
                return false;
            if (text.StartsWith(">=", StringComparison.Ordinal) ||
                text.StartsWith("<=", StringComparison.Ordinal))
            {
                operation = text[0] == '>' ? 'G' : 'L';
                return int.TryParse(text.Substring(2), out expected);
            }
            if (text[0] == '>' || text[0] == '<' || text[0] == '=')
            {
                operation = text[0];
                return int.TryParse(text.Substring(1), out expected);
            }
            return false;
        }

        private static bool Compare(int actual, int expected, char operation)
        {
            switch (operation)
            {
                case '>': return actual > expected;
                case '<': return actual < expected;
                case 'G': return actual >= expected;
                case 'L': return actual <= expected;
                default: return actual == expected;
            }
        }

        private sealed class HandResidentCostRule
        {
            internal int OwnerCardId;
            internal string Condition;
            internal string Target;
            internal string Option;
        }

        /// <summary>
        /// Records the base cost of a card index. Call from the main thread
        /// when the deck is dealt, so that a later socket-thread fold of a
        /// cost change does not need to touch CardMaster.
        /// </summary>
        public void CacheBaseCost(bool isHost, int index, int baseCost)
        {
            if (index <= 0)
                return;

            lock (_sync)
            {
                Dictionary<int, int> target = isHost ? _hostBaseCosts : _guestBaseCosts;
                target[index] = baseCost;
            }
        }

        /// <summary>
        /// Returns the base cost for an index. Lazily queries CardMaster on first
        /// access if the cost is not cached. False when the card identity is
        /// unknown or CardMaster is unavailable.
        /// </summary>
        public bool TryGetBaseCost(bool isHost, int index, out int baseCost)
        {
            baseCost = 0;
            if (index <= 0)
                return false;

            lock (_sync)
            {
                Dictionary<int, int> costCache = isHost ? _hostBaseCosts : _guestBaseCosts;
                if (costCache.TryGetValue(index, out baseCost))
                    return true;

                Dictionary<int, int> identities = isHost ? _hostCards : _guestCards;
                if (!identities.TryGetValue(index, out int cardId) || cardId <= 0)
                    return false;

                if (_cardMaster == null)
                    return false;

                try
                {
                    var cardParam = _cardMaster.GetCardParameterFromId(cardId);
                    if (cardParam != null)
                    {
                        baseCost = cardParam.Cost;
                        costCache[index] = baseCost;
                        return true;
                    }
                }
                catch
                {
                }

                return false;
            }
        }

        /// <summary>
        /// Calculates the cost produced by the card's own spellboost resident
        /// cost rule. Only the stock DSL forms whose target is the card itself
        /// are accepted; unknown expressions are deliberately left untouched.
        /// </summary>
        public bool TryGetSpellboostAdjustedCost(
            bool isHost,
            int index,
            int spellboost,
            out int cost)
        {
            cost = 0;
            if (index <= 0 || spellboost < 0)
                return false;

            lock (_sync)
            {
                Dictionary<int, int> identities = isHost ? _hostCards : _guestCards;
                if (!identities.TryGetValue(index, out int cardId) || cardId <= 0 ||
                    _cardMaster == null)
                {
                    return false;
                }

                if (_cardsWithoutSpellboostCostRule.Contains(cardId))
                    return false;

                if (!_spellboostCostRules.TryGetValue(cardId, out SpellboostCostRule rule))
                {
                    CardParameter card;
                    try
                    {
                        card = _cardMaster.GetCardParameterFromId(cardId);
                    }
                    catch
                    {
                        return false;
                    }

                    if (card == null || !TryCreateSpellboostCostRule(card, out rule))
                    {
                        _cardsWithoutSpellboostCostRule.Add(cardId);
                        return false;
                    }
                    _spellboostCostRules[cardId] = rule;
                }

                cost = rule.Calculate(spellboost);
                Dictionary<int, int> costs = isHost ? _hostBaseCosts : _guestBaseCosts;
                costs[index] = rule.BaseCost;
                return true;
            }
        }

        /// <summary>
        /// Resolves an index to its card identity. Returns false for unknown
        /// indexes; the caller must then leave the card hidden rather than
        /// guessing an identity.
        /// </summary>
        public bool TryResolve(bool isHost, int index, out int cardId)
        {
            cardId = 0;
            if (index <= 0)
                return false;

            lock (_sync)
            {
                Dictionary<int, int> source = isHost ? _hostCards : _guestCards;
                return source.TryGetValue(index, out cardId) && cardId > 0;
            }
        }

        /// <summary>
        /// Absorbs an identity the sending client revealed on its own. Some
        /// skills (banish, discard, open token draw) already carry a cardId in
        /// the unapproved list; learning those keeps mid-battle indexes that
        /// were never part of the opening deck resolvable later.
        /// </summary>
        public void Learn(bool isHost, int index, int cardId)
        {
            if (index <= 0 || cardId <= 0)
                return;

            lock (_sync)
            {
                Dictionary<int, int> target = isHost ? _hostCards : _guestCards;
                if (target.TryGetValue(index, out int existing) && existing == cardId)
                    return;
                target[index] = cardId;
                Dictionary<int, int> costs = isHost ? _hostBaseCosts : _guestBaseCosts;
                costs.Remove(index);
                if (existing > 0)
                {
                    Plugin.Logger.LogWarning(
                        $"[CardIdentity] {(isHost ? "host" : "guest")} index {index} " +
                        $"changed identity {existing} -> {cardId}");
                }
            }
        }

        /// <summary>
        /// Applies an explicit metamorphose result to the current identity of
        /// a card index. Unlike Learn, this is an authoritative state change,
        /// so replacing the initial deck identity is expected and must not be
        /// reported as index drift.
        /// </summary>
        public bool Transform(bool isHost, int index, int cardId)
        {
            if (index <= 0 || cardId <= 0)
                return false;

            lock (_sync)
            {
                Dictionary<int, int> target = isHost ? _hostCards : _guestCards;
                if (target.TryGetValue(index, out int existing) && existing == cardId)
                    return false;
                target[index] = cardId;
                Dictionary<int, int> costs = isHost ? _hostBaseCosts : _guestBaseCosts;
                costs.Remove(index);
                return true;
            }
        }

        private static bool TryCreateSpellboostCostRule(
            CardParameter card,
            out SpellboostCostRule rule)
        {
            rule = null;
            string[] skills = SplitNormalSkills(card.Skill);
            string[] timings = SplitNormalSkills(card.SkillTiming);
            string[] targets = SplitNormalSkills(card.SkillTarget);
            string[] options = SplitNormalSkills(card.SkillOption);
            int count = Math.Min(
                Math.Min(skills.Length, timings.Length),
                Math.Min(targets.Length, options.Length));

            var addExpressions = new List<string>();
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                if (!string.Equals(skills[i], "cost_change", StringComparison.Ordinal) ||
                    !string.Equals(timings[i], "when_spell_charge", StringComparison.Ordinal) ||
                    !ContainsSelfTarget(targets[i]))
                {
                    continue;
                }

                if (TryGetOptionExpression(options[i], "add=", out string expression))
                {
                    addExpressions.Add(expression);
                    found = true;
                }
            }

            if (!found)
                return false;

            rule = new SpellboostCostRule(
                card.Cost,
                addExpressions);
            return true;
        }

        private static string[] SplitNormalSkills(string value)
        {
            if (string.IsNullOrEmpty(value))
                return new[] { "none" };

            string compact = value.Replace(" ", string.Empty);
            int evolved = compact.IndexOf("//", StringComparison.Ordinal);
            if (evolved >= 0)
                compact = compact.Substring(0, evolved);
            return compact.Split(new[] { ',' }, StringSplitOptions.None);
        }

        private static bool ContainsSelfTarget(string target)
        {
            if (string.IsNullOrEmpty(target))
                return false;

            string[] parts = target.Split('&');
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.Equals(parts[i], "target=self", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool ContainsHandResidentTarget(string target)
        {
            if (string.IsNullOrEmpty(target))
                return false;

            string[] parts = target.Split('&');
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.Equals(parts[i], "target=self", StringComparison.Ordinal) ||
                    string.Equals(parts[i], "target=hand_self", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool TryGetOptionExpression(
            string option,
            string prefix,
            out string expression)
        {
            expression = null;
            if (string.IsNullOrEmpty(option) ||
                !option.StartsWith(prefix, StringComparison.Ordinal))
                return false;
            expression = option.Substring(prefix.Length);
            if (expression.Length == 0)
                return false;
            return true;
        }

        private sealed class SpellboostCostRule
        {
            internal SpellboostCostRule(
                int baseCost,
                List<string> addExpressions)
            {
                BaseCost = baseCost;
                _addExpressions = addExpressions ?? new List<string>();
            }

            internal int BaseCost { get; }

            internal int Calculate(int spellboost)
            {
                int result = BaseCost;
                for (int i = 0; i < _addExpressions.Count; i++)
                {
                    if (TryEvaluateExpression(
                            _addExpressions[i],
                            spellboost,
                            out int delta))
                    {
                        result += delta;
                    }
                }
                return Math.Max(0, result);
            }

            private readonly List<string> _addExpressions;
        }

        /// <summary>
        /// Evaluates the arithmetic subset used by CardMaster option values.
        /// The charge variables are resolved from the current spellboost count;
        /// constants and +, -, * operators are handled without card-specific
        /// rules or string-prefix matching.
        /// </summary>
        private static bool TryEvaluateExpression(
            string expression,
            int spellboost,
            out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(expression))
                return false;

            int position = 0;
            if (!TryParseExpression(expression, ref position, spellboost, out value))
                return false;
            SkipExpressionWhitespace(expression, ref position);
            return position == expression.Length;
        }

        private static bool TryParseExpression(
            string text,
            ref int position,
            int spellboost,
            out int value)
        {
            value = 0;
            if (!TryParseProduct(text, ref position, spellboost, out value))
                return false;

            while (true)
            {
                SkipExpressionWhitespace(text, ref position);
                if (position >= text.Length ||
                    (text[position] != '+' && text[position] != '-'))
                    return true;

                char op = text[position++];
                if (!TryParseProduct(text, ref position, spellboost, out int rhs))
                    return false;
                value = op == '+' ? value + rhs : value - rhs;
            }
        }

        private static bool TryParseProduct(
            string text,
            ref int position,
            int spellboost,
            out int value)
        {
            value = 0;
            if (!TryParseFactor(text, ref position, spellboost, out value))
                return false;

            while (true)
            {
                SkipExpressionWhitespace(text, ref position);
                if (position >= text.Length || text[position] != '*')
                    return true;
                position++;
                if (!TryParseFactor(text, ref position, spellboost, out int rhs))
                    return false;
                value *= rhs;
            }
        }

        private static bool TryParseFactor(
            string text,
            ref int position,
            int spellboost,
            out int value)
        {
            value = 0;
            SkipExpressionWhitespace(text, ref position);
            if (position >= text.Length)
                return false;

            int sign = 1;
            if (text[position] == '+' || text[position] == '-')
            {
                if (text[position++] == '-')
                    sign = -1;
                SkipExpressionWhitespace(text, ref position);
            }

            if (position < text.Length && text[position] == '(')
            {
                position++;
                if (!TryParseExpression(text, ref position, spellboost, out value))
                    return false;
                SkipExpressionWhitespace(text, ref position);
                if (position >= text.Length || text[position++] != ')')
                    return false;
                value *= sign;
                return true;
            }

            int start = position;
            while (position < text.Length &&
                (char.IsLetterOrDigit(text[position]) || text[position] == '_'))
            {
                position++;
            }
            if (start == position)
                return false;

            string token = text.Substring(start, position - start);
            if (int.TryParse(token, out value))
            {
                value *= sign;
                return true;
            }

            switch (token)
            {
                case "ADD_CHARGE_COUNT":
                    value = spellboost;
                    break;
                case "ADD_ODD_CHARGE_COUNT":
                    value = (spellboost + 1) / 2;
                    break;
                case "ADD_EVEN_CHARGE_COUNT":
                    value = spellboost / 2;
                    break;
                default:
                    return false;
            }
            value *= sign;
            return true;
        }

        private static void SkipExpressionWhitespace(string text, ref int position)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
                position++;
        }
    }
}
