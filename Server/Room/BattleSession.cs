using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Shadowbus.Server.SocketIO;
using Wizard;

namespace Shadowbus.Server.Room
{
    /// <summary>
    /// Keeps the logical battle streams for one room. The game uses the
    /// sender's pubSeq for ACK/retry, while every receiver has its own
    /// playSeq. A session therefore owns one independent bridge per
    /// direction and per Socket.IO event stream.
    /// </summary>
    public sealed class BattleSession
    {
        private readonly object _sync = new object();
        private readonly Dictionary<StreamKey, SequenceBridge> _bridges =
            new Dictionary<StreamKey, SequenceBridge>();
        private readonly Dictionary<string, InitRoomBattleState> _initStates =
            new Dictionary<string, InitRoomBattleState>(StringComparer.Ordinal);
        private readonly CardIdentityRegistry _identities = new CardIdentityRegistry();
        private readonly HiddenCardStateRegistry _hiddenCardStates =
            new HiddenCardStateRegistry();
        private bool _matchedSent;
        private bool _battleStartSent;
        private bool _dealSent;
        private bool _readySent;
        private string _hostPlayerId;
        private string _guestPlayerId;
        private int _battleSeed;
        private bool _hostFirst;
        private int[] _hostCards;
        private int[] _guestCards;
        private MulliganState _hostMulligan;
        private MulliganState _guestMulligan;

        public BattleSession(string roomId)
        {
            RoomId = roomId ?? throw new ArgumentNullException(nameof(roomId));
        }

        public string RoomId { get; }

        public void SetCardMaster(CardMaster cardMaster)
        {
            _identities.SetCardMaster(cardMaster);
        }

        public bool RecordInitRoomBattle(
            string playerId,
            PlayerDeck deck,
            int deliverySequence)
        {
            if (string.IsNullOrEmpty(playerId) || deck == null)
                return false;

            lock (_sync)
            {
                if (_matchedSent && _initStates.ContainsKey(playerId))
                    return true;

                bool loaded = false;
                int loadedDeliverySequence = 0;
                if (_initStates.TryGetValue(playerId, out InitRoomBattleState previous))
                {
                    loaded = previous.Loaded;
                    loadedDeliverySequence = previous.LoadedDeliverySequence;
                }
                _initStates[playerId] = new InitRoomBattleState
                {
                    Deck = deck.Clone(),
                    DeliverySequence = deliverySequence,
                    Loaded = loaded,
                    LoadedDeliverySequence = loadedDeliverySequence
                };
                return true;
            }
        }

        public bool TryGetInitRoomBattle(
            string playerId,
            out PlayerDeck deck,
            out int deliverySequence)
        {
            lock (_sync)
            {
                if (_initStates.TryGetValue(playerId, out InitRoomBattleState state))
                {
                    deck = state.Deck.Clone();
                    deliverySequence = state.DeliverySequence;
                    return true;
                }
            }

            deck = null;
            deliverySequence = 0;
            return false;
        }

        public bool TryBeginMatched(string hostPlayerId, string guestPlayerId)
        {
            lock (_sync)
            {
                if (_matchedSent || _initStates.Count < 2)
                    return false;
                if (!_initStates.TryGetValue(hostPlayerId, out InitRoomBattleState host) ||
                    !_initStates.TryGetValue(guestPlayerId, out InitRoomBattleState guest))
                    return false;
                foreach (InitRoomBattleState state in _initStates.Values)
                {
                    if (state.Deck == null || state.Deck.CardIds == null ||
                        state.Deck.CardIds.Length < 6)
                        return false;
                }

                _hostPlayerId = hostPlayerId;
                _guestPlayerId = guestPlayerId;
                _battleSeed = ((RoomId.GetHashCode() * 397) ^
                    DateTime.UtcNow.Ticks.GetHashCode()) & int.MaxValue;
                if (_battleSeed == 0)
                    _battleSeed = 1;
                _hostFirst = new Random(_battleSeed).Next(2) == 0;
                _hostCards = Shuffle(host.Deck.CardIds, new Random(_battleSeed ^ 0x13579BDF));
                _guestCards = Shuffle(guest.Deck.CardIds, new Random(_battleSeed ^ 0x2468ACE0));
                // The dealt order is the identity authority for both sides.
                // It matches the idx = position + 1 contract that
                // SocketIoServer.CreateDeckData sends to the owning client.
                _identities.Seed(true, _hostCards);
                _identities.Seed(false, _guestCards);
                _hiddenCardStates.Reset();
                // The whole reveal mechanism rests on this mapping being the
                // same one each client received. Record it once so a mismatch
                // can be diagnosed without patching the client.
                LogDealtDeck("host", _hostCards);
                LogDealtDeck("guest", _guestCards);
                _hostMulligan = CreateMulliganState(
                    _hostCards.Length,
                    new Random(_battleSeed ^ 0x31415926));
                _guestMulligan = CreateMulliganState(
                    _guestCards.Length,
                    new Random(_battleSeed ^ 0x27182818));
                if (_hostMulligan == null || _guestMulligan == null)
                    return false;
                _matchedSent = true;
                return true;
            }
        }

        public bool TryBeginMatched(
            string hostPlayerId,
            string guestPlayerId,
            CardMaster cardMaster)
        {
            SetCardMaster(cardMaster);
            return TryBeginMatched(hostPlayerId, guestPlayerId);
        }

        public bool TryGetBattleSetup(
            string playerId,
            out PlayerDeck selfDeck,
            out int[] selfCards,
            out int battleSeed,
            out bool selfGoesFirst)
        {
            lock (_sync)
            {
                if (!_matchedSent || !_initStates.TryGetValue(playerId, out InitRoomBattleState state))
                {
                    selfDeck = null;
                    selfCards = null;
                    battleSeed = 0;
                    selfGoesFirst = false;
                    return false;
                }

                bool isHost = string.Equals(playerId, _hostPlayerId, StringComparison.Ordinal);
                selfDeck = state.Deck.Clone();
                selfCards = (int[])(isHost ? _hostCards : _guestCards).Clone();
                battleSeed = _battleSeed;
                selfGoesFirst = isHost == _hostFirst;
                return true;
            }
        }

        public bool TryGetOpponentSetup(
            string playerId,
            out PlayerDeck opponentDeck,
            out int[] opponentCards)
        {
            lock (_sync)
            {
                string opponentId = string.Equals(playerId, _hostPlayerId, StringComparison.Ordinal)
                    ? _guestPlayerId
                    : _hostPlayerId;
                if (!_matchedSent || !_initStates.TryGetValue(opponentId, out InitRoomBattleState state))
                {
                    opponentDeck = null;
                    opponentCards = null;
                    return false;
                }

                opponentDeck = state.Deck.Clone();
                opponentCards = (int[])(string.Equals(opponentId, _hostPlayerId, StringComparison.Ordinal)
                    ? _hostCards : _guestCards).Clone();
                return true;
            }
        }

        public int GetBattleSeed()
        {
            lock (_sync)
                return _battleSeed;
        }

        private static int[] Shuffle(int[] source, Random random)
        {
            int[] result = source == null ? new int[0] : (int[])source.Clone();
            for (int i = result.Length - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                int value = result[i];
                result[i] = result[j];
                result[j] = value;
            }
            return result;
        }

        private void LogDealtDeck(string role, int[] cards)
        {
            if (cards == null)
                return;

            var parts = new List<string>(cards.Length);
            for (int i = 0; i < cards.Length; i++)
                parts.Add((i + 1) + "=" + cards[i]);
            Plugin.Logger.LogInfo(
                $"[CardIdentity] {RoomId} dealt {role} deck: " +
                string.Join(" ", parts.ToArray()));
        }

        private static MulliganState CreateMulliganState(int deckSize, Random random)
        {
            // The native mulligan controller requires three opening cards and
            // up to three distinct replacements.
            if (deckSize < 6)
                return null;

            var available = new List<int>(deckSize);
            for (int i = 1; i <= deckSize; i++)
                available.Add(i);

            var selected = new int[6];
            for (int i = 0; i < selected.Length; i++)
            {
                int position = random.Next(available.Count);
                selected[i] = available[position];
                available.RemoveAt(position);
            }

            return new MulliganState
            {
                Initial = new[] { selected[0], selected[1], selected[2] },
                Replacements = new[] { selected[3], selected[4], selected[5] }
            };
        }

        public bool RecordLoaded(string playerId, int deliverySequence)
        {
            if (string.IsNullOrEmpty(playerId))
                return false;
            lock (_sync)
            {
                if (!_initStates.TryGetValue(playerId, out InitRoomBattleState state))
                    return false;
                state.Loaded = true;
                state.LoadedDeliverySequence = deliverySequence;
                return true;
            }
        }

        public bool TryGetLoadedSequence(string playerId, out int deliverySequence)
        {
            lock (_sync)
            {
                if (_initStates.TryGetValue(playerId, out InitRoomBattleState state) &&
                    state.LoadedDeliverySequence > 0)
                {
                    deliverySequence = state.LoadedDeliverySequence;
                    return true;
                }
            }
            deliverySequence = 0;
            return false;
        }

        public bool TryBeginBattleStart()
        {
            lock (_sync)
            {
                if (_battleStartSent || !_matchedSent || _initStates.Count < 2)
                    return false;
                foreach (InitRoomBattleState state in _initStates.Values)
                {
                    if (!state.Loaded)
                        return false;
                }
                _battleStartSent = true;
                return true;
            }
        }

        public bool TryRecordDeal(string playerId, out bool firstRequest)
        {
            firstRequest = false;
            if (string.IsNullOrEmpty(playerId))
                return false;

            lock (_sync)
            {
                MulliganState state = GetMulliganState(playerId);
                if (!_battleStartSent || state == null)
                    return false;
                firstRequest = !state.DealRequested;
                state.DealRequested = true;
                return true;
            }
        }

        public bool TryBeginDeal()
        {
            lock (_sync)
            {
                if (_dealSent || !_battleStartSent ||
                    _hostMulligan == null || _guestMulligan == null ||
                    !_hostMulligan.DealRequested || !_guestMulligan.DealRequested)
                    return false;
                _dealSent = true;
                return true;
            }
        }

        public bool TryGetDeal(
            string playerId,
            out int[] selfIndexes,
            out int[] opponentIndexes)
        {
            lock (_sync)
            {
                MulliganState self = GetMulliganState(playerId);
                MulliganState opponent = GetOpponentMulliganState(playerId);
                if (!_dealSent || self == null || opponent == null)
                {
                    selfIndexes = null;
                    opponentIndexes = null;
                    return false;
                }

                selfIndexes = (int[])self.Initial.Clone();
                opponentIndexes = (int[])opponent.Initial.Clone();
                return true;
            }
        }

        public bool TryRecordSwap(
            string playerId,
            IList<int> abandonedIndexes,
            out bool firstSubmission,
            out string error)
        {
            firstSubmission = false;
            error = null;
            if (string.IsNullOrEmpty(playerId) || abandonedIndexes == null)
            {
                error = "player or idxList is missing";
                return false;
            }

            lock (_sync)
            {
                MulliganState state = GetMulliganState(playerId);
                if (!_dealSent || state == null)
                {
                    error = "Deal has not completed";
                    return false;
                }
                if (state.SwapSubmitted)
                    return true;
                if (abandonedIndexes.Count > state.Initial.Length)
                {
                    error = "idxList contains too many cards";
                    return false;
                }

                var abandoned = new HashSet<int>();
                foreach (int index in abandonedIndexes)
                {
                    if (index <= 0 || Array.IndexOf(state.Initial, index) < 0)
                    {
                        error = $"idxList contains non-opening index {index}";
                        return false;
                    }
                    if (!abandoned.Add(index))
                    {
                        error = $"idxList contains duplicate index {index}";
                        return false;
                    }
                }

                int replacement = 0;
                state.Final = (int[])state.Initial.Clone();
                for (int i = 0; i < state.Final.Length; i++)
                {
                    if (abandoned.Contains(state.Initial[i]))
                        state.Final[i] = state.Replacements[replacement++];
                }
                state.SwapSubmitted = true;
                firstSubmission = true;
                return true;
            }
        }

        public bool TryGetSwapResult(string playerId, out int[] selfIndexes)
        {
            lock (_sync)
            {
                MulliganState state = GetMulliganState(playerId);
                if (state == null || !state.SwapSubmitted || state.Final == null)
                {
                    selfIndexes = null;
                    return false;
                }
                selfIndexes = (int[])state.Final.Clone();
                return true;
            }
        }

        public bool TryBeginReady()
        {
            lock (_sync)
            {
                if (_readySent || !_dealSent ||
                    _hostMulligan == null || _guestMulligan == null ||
                    !_hostMulligan.SwapSubmitted || !_guestMulligan.SwapSubmitted)
                    return false;
                _identities.SetInitialHand(true, _hostMulligan.Final);
                _identities.SetInitialHand(false, _guestMulligan.Final);
                _readySent = true;
                return true;
            }
        }

        public bool TryGetReady(
            string playerId,
            out int[] selfIndexes,
            out int[] opponentIndexes)
        {
            lock (_sync)
            {
                MulliganState self = GetMulliganState(playerId);
                MulliganState opponent = GetOpponentMulliganState(playerId);
                if (!_readySent || self?.Final == null || opponent?.Final == null)
                {
                    selfIndexes = null;
                    opponentIndexes = null;
                    return false;
                }
                selfIndexes = (int[])self.Final.Clone();
                opponentIndexes = (int[])opponent.Final.Clone();
                return true;
            }
        }

        private MulliganState GetMulliganState(string playerId)
        {
            if (string.Equals(playerId, _hostPlayerId, StringComparison.Ordinal))
                return _hostMulligan;
            if (string.Equals(playerId, _guestPlayerId, StringComparison.Ordinal))
                return _guestMulligan;
            return null;
        }

        private MulliganState GetOpponentMulliganState(string playerId)
        {
            if (string.Equals(playerId, _hostPlayerId, StringComparison.Ordinal))
                return _guestMulligan;
            if (string.Equals(playerId, _guestPlayerId, StringComparison.Ordinal))
                return _hostMulligan;
            return null;
        }

        public RoutedMessage Accept(
            string sourcePlayerId,
            string targetPlayerId,
            string eventName,
            JToken message,
            byte[] payload)
        {
            if (string.IsNullOrEmpty(sourcePlayerId))
                throw new ArgumentException("Source player id cannot be empty", nameof(sourcePlayerId));
            if (string.IsNullOrEmpty(targetPlayerId))
                throw new ArgumentException("Target player id cannot be empty", nameof(targetPlayerId));

            StreamKey key = new StreamKey(sourcePlayerId, targetPlayerId, eventName);
            lock (_sync)
            {
                if (!_bridges.TryGetValue(key, out SequenceBridge bridge))
                {
                    // A single `msg` bridge carries the whole battle stream,
                    // including the setup frames that arrive before the host
                    // and guest ids are recorded. Resolve the source role at
                    // rewrite time so later battle frames still use the
                    // correct perspective.
                    bridge = new SequenceBridge(
                        eventName,
                        (data, sequence) => RewriteForReceiver(
                            data,
                            eventName,
                            sequence,
                            IsHostPlayer(sourcePlayerId)));
                    _bridges.Add(key, bridge);
                }
                return bridge.Accept(message, payload);
            }
        }

        public RoutedMessage CreateServerMessage(
            string sourcePlayerId,
            string targetPlayerId,
            string eventName,
            JToken message,
            int? deliverySequence = null)
        {
            if (string.IsNullOrEmpty(sourcePlayerId) || string.IsNullOrEmpty(targetPlayerId))
                return null;

            StreamKey key = new StreamKey(sourcePlayerId, targetPlayerId, eventName);
            lock (_sync)
            {
                if (!_bridges.TryGetValue(key, out SequenceBridge bridge))
                {
                    bridge = new SequenceBridge(
                        eventName,
                        (data, sequence) => RewriteForReceiver(
                            data,
                            eventName,
                            sequence,
                            IsHostPlayer(sourcePlayerId)));
                    _bridges.Add(key, bridge);
                }
                return bridge.CreateServerMessage(message, deliverySequence);
            }
        }

        private bool IsHostPlayer(string playerId)
        {
            return !string.IsNullOrEmpty(playerId) &&
                !string.IsNullOrEmpty(_hostPlayerId) &&
                string.Equals(playerId, _hostPlayerId, StringComparison.Ordinal);
        }

        private JToken RewriteForReceiver(
            JToken message,
            string eventName,
            int deliverySequence,
            bool sourceIsHost)
        {
            // `hand` and matching/setup messages have their own native view
            // semantics. Only battle messages contain relative `isSelf`
            // fields and need the server-known card identities.
            if (!string.Equals(eventName, "msg", StringComparison.Ordinal) ||
                !(message is JObject data))
            {
                return message;
            }

            JObject clone = (JObject)data.DeepClone();
            string uri = clone["uri"]?.Value<string>();
            if (IsBattleViewMessage(uri))
            {
                // The client sends keyAction selections in a request-only
                // envelope. Flatten it to the official response shape before
                // the stock opponent receiver parses the action.
                KeyActionBridge.NormalizeForReceiver(clone);

                // Reveal before flipping: the injected entries are written in
                // the sender's perspective so the flip below turns them into
                // the receiver's opponent-card form.
                int revealed = HiddenCardRevealer.Inject(clone, uri, sourceIsHost, _identities);
                if (revealed > 0)
                {
                    Plugin.Logger.LogInfo(
                        $"[BattleSession] {RoomId} {uri}: revealed {revealed} hidden card(s) " +
                        $"from {(sourceIsHost ? "host" : "guest")}");
                }

                // A hidden card's own when_play_other cost change is omitted
                // by NetworkSkill_cost_change.IsSend. Reconstruct that one
                // official server responsibility while the played card and
                // the resident cards are still in their pre-move hand state.
                if (string.Equals(uri, "PlayActions", StringComparison.Ordinal) &&
                    TryGetInt(clone["playIdx"], out int playedIndex) && playedIndex > 0)
                {
                    int residentAlters = _identities.ApplyHandResidentCostRules(
                        sourceIsHost,
                        playedIndex,
                        _hiddenCardStates);
                    if (residentAlters > 0)
                    {
                        Plugin.Logger.LogInfo(
                            $"[BattleSession] {RoomId} {uri}: applied " +
                            $"{residentAlters} hidden hand resident cost change(s) " +
                            $"from {(sourceIsHost ? "host" : "guest")}");
                    }
                }

                // PlayActions is emitted after the local fusion/transform
                // sequence completes, but its playIdx still identifies the
                // original card that was played. Keep the reveal above on the
                // old identity, then advance the server registry for future
                // actions. Other battle messages do not use playIdx as a
                // first-use reveal, so applying the same update here is safe.
                int metamorphosed = HiddenCardRevealer.ApplyMetamorphoses(
                    clone,
                    sourceIsHost,
                    _identities);
                if (metamorphosed > 0)
                {
                    Plugin.Logger.LogInfo(
                        $"[BattleSession] {RoomId} {uri}: updated " +
                        $"{metamorphosed} transformed card identity(ies) " +
                        $"from {(sourceIsHost ? "host" : "guest")}");
                }

                int bridgedConditions = HiddenConditionBridge.Inject(clone);
                if (bridgedConditions > 0)
                {
                    Plugin.Logger.LogInfo(
                        $"[BattleSession] {RoomId} {uri}: bridged " +
                        $"{bridgedConditions} hidden condition result(s) " +
                        $"from {(sourceIsHost ? "host" : "guest")}");
                }

                int bridgedAlters = HiddenAlterBridge.Inject(
                    clone,
                    uri,
                    sourceIsHost,
                    _identities,
                    _hiddenCardStates);
                if (bridgedAlters > 0)
                {
                    Plugin.Logger.LogInfo(
                        $"[BattleSession] {RoomId} {uri}: bridged " +
                        $"{bridgedAlters} hidden state alter(s) " +
                        $"from {(sourceIsHost ? "host" : "guest")}");
                }

                // Both uList and orderList.move describe authoritative zone
                // transitions. Apply them only after the resident trigger has
                // observed the hand state at the instant the card was played.
                _identities.ApplyMessageZones(clone, sourceIsHost);

                // The active client encodes targets from its own view. The
                // stock opponent receiver consumes this result as
                // `oppoTargetList`; preserve the native target entries but
                // move the envelope to the response-side name.
                if (clone["targetList"] != null && clone["oppoTargetList"] == null)
                {
                    clone["oppoTargetList"] = clone["targetList"];
                    clone.Remove("targetList");
                }

                FlipBattlePerspective(clone);
            }

            // The source pubSeq is used for ACK/retry. The receiving stock
            // agent consumes its own contiguous playSeq stream.
            clone["playSeq"] = deliverySequence;
            return clone;
        }

        private static bool IsBattleViewMessage(string uri)
        {
            if (string.IsNullOrEmpty(uri))
                return false;

            switch (uri)
            {
                case "TurnStart":
                case "PlayActions":
                case "TurnEndActions":
                case "TurnEnd":
                case "TurnEndFinal":
                case "Judge":
                case "Echo":
                case "BattleFinish":
                case "Retire":
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryGetInt(JToken value, out int result)
        {
            result = 0;
            return value != null && int.TryParse(value.ToString(), out result);
        }

        private static void FlipBattlePerspective(JToken value)
        {
            JObject objectValue = value as JObject;
            if (objectValue != null)
            {
                foreach (JProperty property in objectValue.Properties())
                {
                    // targetList/oppoTargetList entries keep the acting
                    // player's target-relative `isSelf` flag.
                    if (string.Equals(property.Name, "targetList", StringComparison.Ordinal) ||
                        string.Equals(property.Name, "oppoTargetList", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (string.Equals(property.Name, "isSelf", StringComparison.Ordinal))
                    {
                        if (TryGetInt(property.Value, out int side) && (side == 0 || side == 1))
                            property.Value = side == 0 ? 1 : 0;
                        continue;
                    }
                    FlipBattlePerspective(property.Value);
                }
                return;
            }

            JArray arrayValue = value as JArray;
            if (arrayValue == null)
                return;
            for (int i = 0; i < arrayValue.Count; i++)
                FlipBattlePerspective(arrayValue[i]);
        }

        public void MarkDelivered(RoutedMessage message)
        {
            if (message == null)
                return;
            lock (_sync)
            {
                foreach (SequenceBridge bridge in _bridges.Values)
                    if (bridge.MarkDelivered(message))
                        return;
            }
        }

        public IList<RoutedMessage> TakePending(
            string sourcePlayerId,
            string targetPlayerId,
            string eventName)
        {
            StreamKey key = new StreamKey(sourcePlayerId, targetPlayerId, eventName);
            lock (_sync)
            {
                return _bridges.TryGetValue(key, out SequenceBridge bridge)
                    ? bridge.TakePending()
                    : new List<RoutedMessage>();
            }
        }

        public IList<RoutedMessage> TakePendingForTarget(string targetPlayerId)
        {
            var result = new List<RoutedMessage>();
            if (string.IsNullOrEmpty(targetPlayerId))
                return result;

            lock (_sync)
            {
                foreach (KeyValuePair<StreamKey, SequenceBridge> pair in _bridges)
                {
                    if (pair.Key.TargetPlayerId == targetPlayerId)
                        result.AddRange(pair.Value.TakePending());
                }
            }

            result.Sort((left, right) => left.Order.CompareTo(right.Order));
            return result;
        }

        public void MarkDelivered(
            string sourcePlayerId,
            string targetPlayerId,
            string eventName,
            int sourceSequence)
        {
            if (sourceSequence <= 0)
                return;

            StreamKey key = new StreamKey(sourcePlayerId, targetPlayerId, eventName);
            lock (_sync)
            {
                if (_bridges.TryGetValue(key, out SequenceBridge bridge))
                    bridge.MarkDelivered(sourceSequence);
            }
        }

        public void MarkDeliveredForTarget(
            string targetPlayerId,
            string eventName,
            int sourceSequence)
        {
            if (string.IsNullOrEmpty(targetPlayerId) || sourceSequence <= 0)
                return;

            lock (_sync)
            {
                foreach (KeyValuePair<StreamKey, SequenceBridge> pair in _bridges)
                {
                    if (pair.Key.TargetPlayerId == targetPlayerId &&
                        pair.Key.EventName == (eventName ?? string.Empty))
                    {
                        pair.Value.MarkDelivered(sourceSequence);
                    }
                }
            }
        }

        private readonly struct StreamKey : IEquatable<StreamKey>
        {
            public StreamKey(string sourcePlayerId, string targetPlayerId, string eventName)
            {
                SourcePlayerId = sourcePlayerId ?? string.Empty;
                TargetPlayerId = targetPlayerId ?? string.Empty;
                EventName = eventName ?? string.Empty;
            }

            public readonly string SourcePlayerId;
            public readonly string TargetPlayerId;
            public readonly string EventName;

            public bool Equals(StreamKey other)
            {
                return string.Equals(SourcePlayerId, other.SourcePlayerId, StringComparison.Ordinal) &&
                       string.Equals(TargetPlayerId, other.TargetPlayerId, StringComparison.Ordinal) &&
                       string.Equals(EventName, other.EventName, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is StreamKey && Equals((StreamKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = SourcePlayerId.GetHashCode();
                    hash = (hash * 397) ^ TargetPlayerId.GetHashCode();
                    return (hash * 397) ^ EventName.GetHashCode();
                }
            }
        }

        private sealed class InitRoomBattleState
        {
            public PlayerDeck Deck;
            public int DeliverySequence;
            public bool Loaded;
            public int LoadedDeliverySequence;
        }

        private sealed class MulliganState
        {
            public int[] Initial;
            public int[] Replacements;
            public int[] Final;
            public bool DealRequested;
            public bool SwapSubmitted;
        }
    }

    public sealed class RoutedMessage
    {
        internal RoutedMessage(
            string eventName,
            JToken message,
            byte[] payload,
            int sourceSequence,
            int deliverySequence,
            bool hasSequence,
            bool duplicate,
            long order)
        {
            EventName = eventName;
            Message = message;
            Payload = payload;
            SourceSequence = sourceSequence;
            DeliverySequence = deliverySequence;
            HasSequence = hasSequence;
            IsDuplicate = duplicate;
            Order = order;
        }

        public string EventName { get; }
        public JToken Message { get; }
        public byte[] Payload { get; }
        public int SourceSequence { get; }
        public int DeliverySequence { get; }
        public bool HasSequence { get; }
        public bool IsDuplicate { get; }
        internal long Order { get; }
    }

    internal sealed class SequenceBridge
    {
        private const int MaxHistory = 256;

        private readonly string _eventName;
        private readonly Func<JToken, int, JToken> _rewrite;
        private readonly Dictionary<int, RoutedMessage> _history =
            new Dictionary<int, RoutedMessage>();
        private readonly Queue<RoutedMessage> _pending =
            new Queue<RoutedMessage>();
        private int _lastSourceSequence;
        private int _nextDeliverySequence = 1;
        private long _order;

        public SequenceBridge(
            string eventName,
            Func<JToken, int, JToken> rewrite = null)
        {
            _eventName = eventName ?? string.Empty;
            _rewrite = rewrite;
        }

        public RoutedMessage Accept(JToken message, byte[] payload)
        {
            int sourceSequence = ExtractSourceSequence(message, _eventName);
            bool hasSequence = sourceSequence > 0;
            if (!hasSequence)
            {
                return new RoutedMessage(
                    _eventName,
                    message,
                    payload,
                    0,
                    0,
                    false,
                    false,
                    ++_order);
            }

            if (_history.TryGetValue(sourceSequence, out RoutedMessage previous))
            {
                return new RoutedMessage(
                    previous.EventName,
                    previous.Message,
                    previous.Payload,
                    previous.SourceSequence,
                    previous.DeliverySequence,
                    true,
                    true,
                    previous.Order);
            }

            // TCP preserves order, but retries can arrive after a reconnect.
            // Keep accepting a forward gap and make it visible in logs; the
            // destination still receives a contiguous server-owned sequence.
            int deliverySequence = _nextDeliverySequence++;
            JToken routedMessage = _rewrite == null
                ? RewriteForReceiver(message, _eventName, deliverySequence)
                : _rewrite(message, deliverySequence);
            byte[] routedPayload = routedMessage == message
                ? payload
                : SocketIoPayloadCodec.Encode(routedMessage);
            var routed = new RoutedMessage(
                _eventName,
                routedMessage,
                routedPayload,
                sourceSequence,
                deliverySequence,
                true,
                false,
                ++_order);

            _history[sourceSequence] = routed;
            _pending.Enqueue(routed);
            _lastSourceSequence = Math.Max(_lastSourceSequence, sourceSequence);
            TrimHistory();
            return routed;
        }

        public RoutedMessage CreateServerMessage(JToken message, int? forcedDeliverySequence)
        {
            int deliverySequence;
            if (forcedDeliverySequence.HasValue && forcedDeliverySequence.Value > 0)
            {
                deliverySequence = forcedDeliverySequence.Value;
                _nextDeliverySequence = Math.Max(_nextDeliverySequence, deliverySequence + 1);
            }
            else
            {
                deliverySequence = _nextDeliverySequence++;
            }

            JToken routedMessage = _rewrite == null
                ? RewriteForReceiver(message, _eventName, deliverySequence)
                : _rewrite(message, deliverySequence);
            byte[] routedPayload = SocketIoPayloadCodec.Encode(routedMessage);
            var routed = new RoutedMessage(
                _eventName,
                routedMessage,
                routedPayload,
                0,
                deliverySequence,
                true,
                false,
                ++_order);
            _pending.Enqueue(routed);
            return routed;
        }

        public IList<RoutedMessage> TakePending()
        {
            // Return a snapshot. The caller removes a frame only after the
            // transport accepted it, so a failed send remains available for
            // the next reconnect.
            return new List<RoutedMessage>(_pending);
        }

        public void MarkDelivered(int sourceSequence)
        {
            if (sourceSequence <= 0 || _pending.Count == 0)
                return;

            int count = _pending.Count;
            for (int i = 0; i < count; i++)
            {
                RoutedMessage message = _pending.Dequeue();
                if (message.SourceSequence != sourceSequence)
                    _pending.Enqueue(message);
            }
        }

        public bool MarkDelivered(RoutedMessage target)
        {
            if (target == null || _pending.Count == 0)
                return false;

            bool removed = false;
            int count = _pending.Count;
            for (int i = 0; i < count; i++)
            {
                RoutedMessage message = _pending.Dequeue();
                if (object.ReferenceEquals(message, target))
                    removed = true;
                else
                    _pending.Enqueue(message);
            }
            return removed;
        }

        private void TrimHistory()
        {
            if (_history.Count <= MaxHistory)
                return;

            int oldest = int.MaxValue;
            foreach (int sequence in _history.Keys)
                oldest = Math.Min(oldest, sequence);
            _history.Remove(oldest);
        }

        private static int ExtractSourceSequence(JToken message, string eventName)
        {
            if (message == null)
                return 0;

            JObject wrapper = message as JObject;
            JToken value = wrapper == null ? null : wrapper["pubSeq"];
            int sequence;
            if (value != null && int.TryParse(value.ToString(), out sequence) && sequence > 0)
                return sequence;

            // Depending on the decoder path, hand data may be wrapped in the
            // StockHandData property instead of arriving as the root array.
            // Reliable hand URI types 2 and 5 carry pubSeq at index 3;
            // ordinary hand types use that position as an input parameter.
            if (string.Equals(eventName, "hand", StringComparison.Ordinal) &&
                SocketIoPayloadCodec.TryExtractReliableHandSequence(message, out sequence))
            {
                return sequence;
            }

            return 0;
        }

        private static JToken RewriteForReceiver(JToken message, string eventName, int deliverySequence)
        {
            // hand's sequence is an entry in the positional list and is not
            // consumed by the stock OnHandReceived path. Keep it untouched;
            // its stream is still bridged and deduplicated independently.
            if (!string.Equals(eventName, "msg", StringComparison.Ordinal) ||
                !(message is JObject data))
            {
                return message;
            }

            JObject clone = (JObject)data.DeepClone();
            clone["playSeq"] = deliverySequence;
            return clone;
        }
    }
}
