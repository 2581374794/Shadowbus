using System;
using System.Collections.Generic;

namespace Shadowbus.Server.Room
{
    /// <summary>
    /// Stores receiver-visible state for cards whose identity is still hidden.
    /// The official relay addressed these cards by index, so no card identity
    /// needs to be disclosed while the state is accumulated.
    /// </summary>
    internal sealed class HiddenCardStateRegistry
    {
        private readonly object _sync = new object();
        private readonly Dictionary<int, int> _hostSpellboost =
            new Dictionary<int, int>();
        private readonly Dictionary<int, int> _guestSpellboost =
            new Dictionary<int, int>();
        private readonly Dictionary<int, List<string>> _hostCostOperations =
            new Dictionary<int, List<string>>();
        private readonly Dictionary<int, List<string>> _guestCostOperations =
            new Dictionary<int, List<string>>();
        private readonly Dictionary<int, CombatState> _hostCombat =
            new Dictionary<int, CombatState>();
        private readonly Dictionary<int, CombatState> _guestCombat =
            new Dictionary<int, CombatState>();

        internal void Reset()
        {
            lock (_sync)
            {
                _hostSpellboost.Clear();
                _guestSpellboost.Clear();
                _hostCostOperations.Clear();
                _guestCostOperations.Clear();
                _hostCombat.Clear();
                _guestCombat.Clear();
            }
        }

        internal bool ApplySpellboost(
            bool isHost,
            int index,
            string operation,
            out int absoluteValue)
        {
            absoluteValue = 0;
            if (index <= 0 || string.IsNullOrEmpty(operation))
                return false;

            char kind = operation[0];
            if ((kind != 'a' && kind != 's') ||
                !int.TryParse(operation.Substring(1), out int value))
            {
                return false;
            }

            lock (_sync)
            {
                Dictionary<int, int> states = isHost
                    ? _hostSpellboost
                    : _guestSpellboost;
                states.TryGetValue(index, out int current);
                absoluteValue = kind == 'a' ? current + value : value;
                if (absoluteValue < 0)
                    absoluteValue = 0;
                states[index] = absoluteValue;
                return true;
            }
        }

        internal bool TryGetSpellboost(bool isHost, int index, out int value)
        {
            value = 0;
            if (index <= 0)
                return false;

            lock (_sync)
            {
                Dictionary<int, int> states = isHost
                    ? _hostSpellboost
                    : _guestSpellboost;
                return states.TryGetValue(index, out value);
            }
        }

        /// <summary>
        /// Persists a raw cost modifier in sender order. Keeping the operation
        /// instead of guessing an absolute value lets the reveal projection
        /// apply the same ordered modifier stack once the card identity is
        /// known.
        /// </summary>
        internal bool ApplyCostOperation(bool isHost, int index, string operation)
        {
            if (index <= 0 || string.IsNullOrEmpty(operation))
                return false;

            char kind = operation[0];
            if (kind != 'a' && kind != 's' && kind != 'd' && kind != 'D')
                return false;
            if (!int.TryParse(operation.Substring(1).TrimStart('+'), out _))
                return false;

            lock (_sync)
            {
                Dictionary<int, List<string>> states = isHost
                    ? _hostCostOperations
                    : _guestCostOperations;
                if (!states.TryGetValue(index, out List<string> operations))
                {
                    operations = new List<string>();
                    states[index] = operations;
                }
                operations.Add(operation);
                return true;
            }
        }

        internal IList<string> GetCostOperations(bool isHost, int index)
        {
            lock (_sync)
            {
                Dictionary<int, List<string>> states = isHost
                    ? _hostCostOperations
                    : _guestCostOperations;
                if (!states.TryGetValue(index, out List<string> operations))
                    return Array.Empty<string>();
                return new List<string>(operations);
            }
        }

        internal bool HasCostOperations(bool isHost, int index)
        {
            lock (_sync)
            {
                Dictionary<int, List<string>> states = isHost
                    ? _hostCostOperations
                    : _guestCostOperations;
                return states.TryGetValue(index, out List<string> operations) &&
                    operations.Count > 0;
            }
        }

        /// <summary>
        /// Records an ordered attack/life modifier without exposing the card.
        /// A set supersedes prior additions; additions after the set remain
        /// representable by the stock receiver's set-then-add fields.
        /// </summary>
        internal bool ApplyCombatOperation(
            bool isHost,
            int index,
            string key,
            string operation)
        {
            if (index <= 0 ||
                (key != "atk" && key != "life") ||
                string.IsNullOrEmpty(operation))
            {
                return false;
            }

            char kind = operation[0];
            if ((kind != 'a' && kind != 's') ||
                !int.TryParse(operation.Substring(1).TrimStart('+'), out int value))
            {
                return false;
            }

            lock (_sync)
            {
                Dictionary<int, CombatState> states = isHost
                    ? _hostCombat
                    : _guestCombat;
                if (!states.TryGetValue(index, out CombatState state))
                {
                    state = new CombatState();
                    states[index] = state;
                }

                if (key == "atk")
                {
                    if (kind == 's')
                    {
                        state.SetAtk = value;
                        state.AddAtk = null;
                    }
                    else
                    {
                        state.AddAtk = (state.AddAtk ?? 0) + value;
                    }
                }
                else if (kind == 's')
                {
                    state.SetLife = value;
                    state.AddLife = null;
                }
                else
                {
                    state.AddLife = (state.AddLife ?? 0) + value;
                }
                return true;
            }
        }

        internal bool TryGetCombatState(
            bool isHost,
            int index,
            out int? addAtk,
            out int? setAtk,
            out int? addLife,
            out int? setLife)
        {
            addAtk = null;
            setAtk = null;
            addLife = null;
            setLife = null;
            lock (_sync)
            {
                Dictionary<int, CombatState> states = isHost
                    ? _hostCombat
                    : _guestCombat;
                if (!states.TryGetValue(index, out CombatState state))
                    return false;
                addAtk = state.AddAtk;
                setAtk = state.SetAtk;
                addLife = state.AddLife;
                setLife = state.SetLife;
                return true;
            }
        }

        /// <summary>
        /// Drops all hidden modifiers after a card leaves the hidden lifetime
        /// represented by this index. The caller performs this only after the
        /// current message has been projected, so a reveal still carries its
        /// final state.
        /// </summary>
        internal void Clear(bool isHost, int index)
        {
            if (index <= 0)
                return;

            lock (_sync)
            {
                Dictionary<int, int> spellboost = isHost
                    ? _hostSpellboost
                    : _guestSpellboost;
                Dictionary<int, List<string>> costs = isHost
                    ? _hostCostOperations
                    : _guestCostOperations;
                Dictionary<int, CombatState> combat = isHost
                    ? _hostCombat
                    : _guestCombat;
                spellboost.Remove(index);
                costs.Remove(index);
                combat.Remove(index);
            }
        }

        private sealed class CombatState
        {
            internal int? AddAtk;
            internal int? SetAtk;
            internal int? AddLife;
            internal int? SetLife;
        }
    }
}
