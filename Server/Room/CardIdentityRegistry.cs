using System.Collections.Generic;

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
        // Base cost for each dealt card, keyed by index. Populated when the
        // deck is dealt so a hidden card's cost change (which the receiver
        // settles as an absolute value) can be folded correctly later. Kept
        // separate from the identity map because a card's identity can change
        // (metamorphose) without its base cost being relearned.
        private readonly Dictionary<int, int> _hostBaseCosts = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _guestBaseCosts = new Dictionary<int, int>();

        /// <summary>
        /// Records the shuffled deck the server dealt to one side. Replaces any
        /// previous content for that side.
        /// </summary>
        public void Seed(bool isHost, int[] shuffledCards)
        {
            lock (_sync)
            {
                Dictionary<int, int> target = isHost ? _hostCards : _guestCards;
                target.Clear();
                if (shuffledCards == null)
                    return;
                for (int i = 0; i < shuffledCards.Length; i++)
                {
                    if (shuffledCards[i] > 0)
                        target[i + 1] = shuffledCards[i];
                }
            }
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
        /// Returns the cached base cost for an index. False when the card is
        /// not one the server dealt, or the cost was never cached.
        /// </summary>
        public bool TryGetBaseCost(bool isHost, int index, out int baseCost)
        {
            baseCost = 0;
            if (index <= 0)
                return false;

            lock (_sync)
            {
                Dictionary<int, int> source = isHost ? _hostBaseCosts : _guestBaseCosts;
                return source.TryGetValue(index, out baseCost);
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
                if (existing > 0)
                {
                    // A changed identity means the index mapping drifted from
                    // the dealt deck. See the class remarks on index swapping.
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
                return true;
            }
        }
    }
}
