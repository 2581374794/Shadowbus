namespace Shadowbus
{
    internal readonly struct P2PBattleResultPair
    {
        internal P2PBattleResultPair(int host, int guest)
        {
            Host = host;
            Guest = guest;
        }

        internal int Host { get; }
        internal int Guest { get; }
    }

    internal static class P2PBattleResult
    {
        internal const int RetireWin = 105;
        internal const int RetireLose = 106;
        internal const int DisconnectWin = 201;
        internal const int DisconnectLose = 202;

        internal static P2PBattleResultPair FromHostLocalResult(int hostLocalResult)
        {
            return FromLocalResult(true, hostLocalResult);
        }

        internal static P2PBattleResultPair FromLocalResult(
            bool localIsHost,
            int localResult)
        {
            int opponentResult = Invert(localResult);
            return localIsHost
                ? new P2PBattleResultPair(localResult, opponentResult)
                : new P2PBattleResultPair(opponentResult, localResult);
        }

        internal static bool IsPairedResult(int result)
        {
            return (result >= 101 && result <= 108) ||
                (result >= 201 && result <= 208);
        }

        // RESULT_CODE.MaxTurnLose (210) is intentionally not a win/lose
        // pair.  The stock NetworkBattleManagerBase gives that same code to
        // both clients and renders its special max-turn result locally.  Do
        // not run it through Invert(), otherwise one client would receive the
        // unrelated reserved value 209.
        internal static bool IsTerminalResult(int result)
        {
            return IsPairedResult(result) || result == 210;
        }

        internal static bool TryCreateResultPair(
            bool localIsHost,
            int localResult,
            out P2PBattleResultPair results)
        {
            if (IsPairedResult(localResult))
            {
                results = FromLocalResult(localIsHost, localResult);
                return true;
            }

            if (localResult == 210)
            {
                results = new P2PBattleResultPair(localResult, localResult);
                return true;
            }

            results = default;
            return false;
        }

        internal static P2PBattleResultPair FromRetirement(
            bool retiringSideIsHost)
        {
            return retiringSideIsHost
                ? new P2PBattleResultPair(RetireLose, RetireWin)
                : new P2PBattleResultPair(RetireWin, RetireLose);
        }

        internal static int ResolveLocalResultAfterDisconnect(
            bool localRetired,
            int currentLocalResult)
        {
            if (localRetired)
            {
                return RetireLose;
            }
            return IsTerminalResult(currentLocalResult)
                ? currentLocalResult
                : DisconnectWin;
        }

        internal static int Invert(int result)
        {
            if (IsPairedResult(result))
            {
                return (result & 1) == 1 ? result + 1 : result - 1;
            }
            return result;
        }
    }
}
