using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Shadowbus.Server.Room
{
    /// <summary>
    /// Translates the sender-only orderList.skillConditionCheck register into
    /// the result shape consumed by the stock NetworkBattleReceiver.
    ///
    /// The original client writes skillConditionCheck but never reads it back.
    /// The receiver instead looks for an entry containing activate/count/param
    /// in knownList or uList. The server therefore bridges the native fields
    /// without forwarding the private condition expression or target data.
    /// </summary>
    internal static class HiddenConditionBridge
    {
        internal static int Inject(JObject message)
        {
            if (message == null)
                return 0;

            JArray orderList = message["orderList"] as JArray;
            if (orderList == null || orderList.Count == 0)
                return 0;

            var existing = new HashSet<string>(StringComparer.Ordinal);
            CollectConditionKeys(message["knownList"] as JArray, existing);
            CollectConditionKeys(message["uList"] as JArray, existing);

            var injected = new List<JObject>();
            for (int i = 0; i < orderList.Count; i++)
            {
                JObject order = orderList[i] as JObject;
                if (order == null)
                    continue;

                JToken value = order["skillConditionCheck"];
                foreach (JObject condition in EnumerateObjects(value))
                {
                    JObject result = CreateReceiverResult(condition);
                    if (result == null)
                        continue;

                    string key = MakeConditionKey(result);
                    if (existing.Add(key))
                        injected.Add(result);
                }
            }

            if (injected.Count == 0)
                return 0;

            // Condition entries are deliberately put in knownList. The stock
            // receiver classifies entries containing activate/count/param as
            // SkillConditionCheckList and does not treat them as card reveals.
            JArray knownList = message["knownList"] as JArray;
            if (knownList == null)
            {
                knownList = new JArray();
                message["knownList"] = knownList;
            }
            for (int i = 0; i < injected.Count; i++)
                knownList.Add(injected[i]);
            return injected.Count;
        }

        private static JObject CreateReceiverResult(JObject source)
        {
            // Index 0 is the leader, whose attached skills also report conditions.
            if (source == null || !TryGetInt(source["idx"], out int index) || index < 0)
                return null;

            if (source["skillIdx"] == null && source["skillCount"] == null)
                return null;

            var result = new JObject
            {
                ["idx"] = index,
                // The register is created from OnSkillStart, after the source
                // client has passed its hidden condition check. An explicit
                // activate value is still honored if a future client sends it.
                ["activate"] = ReadActivate(source),
                // In the sender's perspective this card belongs to self. The
                // existing perspective flip changes it to the opponent side.
                ["isSelf"] = 1
            };

            CopyScalar(source, result, "skillIdx");
            CopyScalar(source, result, "skillCount");
            CopyScalar(source, result, "isInvoke");

            // These fields are result values understood by the stock receiver
            // for related private-count registers. They do not contain the
            // private predicate or card identities.
            CopyScalar(source, result, "count");
            CopyScalar(source, result, "param");
            CopyScalar(source, result, "callCount");
            return result;
        }

        private static int ReadActivate(JObject source)
        {
            return TryGetInt(source["activate"], out int value) ? value : 1;
        }

        private static void CopyScalar(JObject source, JObject target, string name)
        {
            JToken value = source[name];
            if (value == null || value is JObject || value is JArray)
                return;
            target[name] = value.DeepClone();
        }

        private static IEnumerable<JObject> EnumerateObjects(JToken value)
        {
            JObject objectValue = value as JObject;
            if (objectValue != null)
            {
                yield return objectValue;
                yield break;
            }

            JArray arrayValue = value as JArray;
            if (arrayValue == null)
                yield break;

            for (int i = 0; i < arrayValue.Count; i++)
            {
                JObject item = arrayValue[i] as JObject;
                if (item != null)
                    yield return item;
            }
        }

        private static void CollectConditionKeys(
            JArray entries,
            HashSet<string> keys)
        {
            if (entries == null)
                return;

            for (int i = 0; i < entries.Count; i++)
            {
                JObject entry = entries[i] as JObject;
                if (entry == null || entry["activate"] == null &&
                    entry["count"] == null && entry["param"] == null &&
                    entry["callCount"] == null)
                {
                    continue;
                }

                keys.Add(MakeConditionKey(entry));
            }
        }

        private static string MakeConditionKey(JObject entry)
        {
            return string.Join("|", new[]
            {
                ScalarText(entry, "idx"),
                ScalarText(entry, "skillIdx"),
                ScalarText(entry, "skillCount"),
                ScalarText(entry, "isInvoke"),
                ScalarText(entry, "count"),
                ScalarText(entry, "param"),
                ScalarText(entry, "callCount")
            });
        }

        private static string ScalarText(JObject entry, string name)
        {
            JToken value = entry[name];
            return value == null ? string.Empty : value.ToString();
        }

        private static bool TryGetInt(JToken value, out int result)
        {
            result = 0;
            return value != null && int.TryParse(value.ToString(), out result);
        }
    }
}
