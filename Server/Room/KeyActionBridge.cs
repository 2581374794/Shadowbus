using Newtonsoft.Json.Linq;

namespace Shadowbus.Server.Room
{
    /// <summary>
    /// Converts the stock client's keyAction request shape into the response
    /// shape consumed by NetworkBattleReceiver.
    ///
    /// SendKeyActionDataManager nests the selected values under selectCard:
    ///   selectCard = { cardId/cardIdx: [...], open: 0/1 }
    /// The receiver instead reads choice ids directly from selectCard and
    /// burial-rite indexes directly from the keyAction entry's cardIdx field.
    /// The official server therefore flattened this envelope while relaying.
    /// </summary>
    internal static class KeyActionBridge
    {
        internal static int NormalizeForReceiver(JObject message)
        {
            if (message == null || !(message["keyAction"] is JArray actions))
                return 0;

            int normalized = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                if (!(actions[i] is JObject action) ||
                    !(action["selectCard"] is JObject selection))
                {
                    continue;
                }

                // BurialRate is the only stock key action whose selection is
                // addressed by battle-card index rather than card identity.
                JToken cardIndexes = selection["cardIdx"];
                if (cardIndexes != null)
                {
                    action["cardIdx"] = cardIndexes.DeepClone();
                    action.Remove("selectCard");
                    normalized++;
                    continue;
                }

                JToken cardIds = selection["cardId"];
                if (cardIds == null)
                    continue;

                // Choice, HaveBeforeSkillChoice, ChoiceEvolution and
                // ChoiceBrave all consume selectCard as List<object>. Fusion
                // does not consume the value, but using the same stock
                // response shape is harmless and keeps this transform
                // protocol-based rather than card- or action-specific.
                action["selectCard"] = cardIds.DeepClone();
                normalized++;
            }

            return normalized;
        }
    }
}
