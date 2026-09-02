using System;
using System.Collections.Generic;
using System.Text;
using Cute;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Shadowbus.Server.SocketIO
{
    /// <summary>
    /// Codec for the payload used by the stock RealTimeNetworkAgent:
    /// MessagePack string -> CryptAES node string -> JSON.
    /// </summary>
    internal static class SocketIoPayloadCodec
    {
        public static bool TryDecode(byte[] data, out JToken json, out int sequence, out string uri)
        {
            return TryDecode(data, out json, out sequence, out uri, true);
        }

        internal static bool TryDecodeQuiet(byte[] data, out JToken json, out int sequence, out string uri)
        {
            return TryDecode(data, out json, out sequence, out uri, false);
        }

        private static bool TryDecode(
            byte[] data,
            out JToken json,
            out int sequence,
            out string uri,
            bool logErrors)
        {
            json = null;
            sequence = 0;
            uri = null;
            try
            {
                string packedString = DecodeMessagePackString(data);

                // Battle `msg` payloads contain an encrypted node string,
                // while the ordinary `hand` stream uses the same MessagePack
                // string wrapper around plain JSON.  Try JSON first so the
                // hand stream is not incorrectly passed to Base64/AES
                // decryption. Encrypted payloads begin with the 32-character
                // key prefix and therefore cannot be mistaken for JSON.
                string plain = packedString;
                try
                {
                    json = JToken.Parse(plain);
                }
                catch (JsonReaderException)
                {
                    plain = CryptAES.decryptForNode(packedString);
                    json = JToken.Parse(plain);
                }

                uri = ExtractUri(json);
                sequence = ExtractSequence(json);
                return true;
            }
            catch (Exception ex)
            {
                if (logErrors)
                    Plugin.Logger.LogWarning($"[SocketIO] Unable to decode game payload: {ex.Message}");
                return false;
            }
        }

        internal static string DescribeSequenceFields(JToken json, int fallbackSequence)
        {
            if (json == null)
                return "<none>";

            var fields = new List<string>();
            AddField(json, fields, "pubSeq");
            AddField(json, fields, "playSeq");
            AddField(json, fields, "seq");
            AddField(json, fields, "sequence");

            JObject obj = json as JObject;
            JArray hand = obj == null ? null : obj["StockHandData"] as JArray;
            if (hand != null && hand.Count > 3)
                fields.Add("StockHandData[3]=" + hand[3]);

            JArray array = json as JArray;
            if (array != null && array.Count > 3)
                fields.Add("array[3]=" + array[3]);

            return fields.Count == 0
                ? "fallback=" + fallbackSequence
                : string.Join(",", fields.ToArray());
        }

        /// <summary>
        /// Returns a privacy-preserving shape summary for a battle payload.
        /// Values are intentionally omitted: this is used to confirm that
        /// result-bearing fields such as orderList survive the relay without
        /// writing private card or hand contents to the log.
        /// </summary>
        internal static string DescribeBattleStructure(JToken json)
        {
            if (json == null)
                return "shape=<none>";

            var fields = new List<string>();
            if (json is JObject root)
            {
                var names = new List<string>();
                foreach (JProperty property in root.Properties())
                    names.Add(property.Name);
                fields.Add("top=" + FormatNames(names));

                JToken actionType = root["type"];
                if (actionType != null)
                    fields.Add("type=" + actionType);

                AddCollectionShape(root, fields, "orderList");
                AddCollectionShape(root, fields, "skillConditionCheck");
                AddCollectionShape(root, fields, "uList");
                AddCollectionShape(root, fields, "knownList");
                AddCollectionShape(root, fields, "targetList");
                AddCollectionShape(root, fields, "oppoTargetList");
                AddCollectionShape(root, fields, "randomTargetIdx");
            }
            else if (json is JArray array)
            {
                fields.Add("rootArray=" + array.Count);
                AddArrayEntryShape(array, fields, "rootArrayEntries");
            }
            else
            {
                fields.Add("rootType=" + json.Type);
            }

            return string.Join(",", fields.ToArray());
        }

        /// <summary>
        /// Describes the result-bearing parts of a battle action without
        /// exposing private card identities or hand/deck contents.  The
        /// summary is intentionally limited to the two messages that carry
        /// turn-end and play resolution data.
        /// </summary>
        internal static string DescribeHiddenConditionStructure(string uri, JToken json)
        {
            if (!string.Equals(uri, "PlayActions", StringComparison.Ordinal) &&
                !string.Equals(uri, "TurnEndActions", StringComparison.Ordinal))
            {
                return null;
            }

            JObject root = json as JObject;
            if (root == null)
                return "payload=<non-object>";

            var fields = new List<string>();
            AppendResultCollection(root, fields, "orderList", true);
            AppendResultCollection(root, fields, "uList", false);
            AppendKnownListSummary(root, fields);
            AppendPresence(root, fields, "randomTargetIdx");
            return string.Join(",", fields.ToArray());
        }

        /// <summary>
        /// Reports how many identity reveals a relayed message carries. Counts
        /// only: the card ids themselves stay out of the log.
        /// </summary>
        private static void AppendKnownListSummary(JObject root, List<string> fields)
        {
            JToken value = root["knownList"];
            if (value == null)
            {
                fields.Add("knownList=absent");
                return;
            }

            JArray array = value as JArray;
            if (array == null)
            {
                fields.Add("knownListType=" + value.Type);
                return;
            }

            int open = 0;
            for (int i = 0; i < array.Count; i++)
            {
                JObject entry = array[i] as JObject;
                if (entry != null && entry["cardId"] != null)
                    open++;
            }
            fields.Add("knownList[]=" + array.Count + ",knownListOpen=" + open);
        }

        private static void AppendResultCollection(
            JObject root,
            List<string> fields,
            string name,
            bool isOrderList)
        {
            JToken value = root[name];
            if (value == null)
            {
                fields.Add(name + "=absent");
                return;
            }

            JArray array = value as JArray;
            if (array == null)
            {
                fields.Add(name + "Type=" + value.Type);
                return;
            }

            var kindCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var conditionSummaries = new List<string>();
            var entrySummaries = new List<string>();
            var orderSummaries = new List<string>();
            for (int i = 0; i < array.Count; i++)
            {
                JObject entry = array[i] as JObject;
                if (entry == null)
                    continue;

                if (!isOrderList)
                    entrySummaries.Add(DescribeUListEntry(entry));
                else
                    orderSummaries.Add(DescribeOrderEntry(entry));

                foreach (JProperty property in entry.Properties())
                {
                    int count;
                    if (!kindCounts.TryGetValue(property.Name, out count))
                        count = 0;
                    kindCounts[property.Name] = count + 1;

                    if (isOrderList &&
                        string.Equals(property.Name, "skillConditionCheck", StringComparison.Ordinal))
                    {
                        AppendConditionSummaries(property.Value, conditionSummaries);
                    }
                }
            }

            fields.Add(name + "[]=" + array.Count + "," + name + "Kinds=" +
                FormatCounts(kindCounts));
            if (isOrderList)
            {
                if (orderSummaries.Count > 0)
                    fields.Add("orderEntries=" + string.Join(";", orderSummaries.ToArray()));
                fields.Add("conditions=" + (conditionSummaries.Count == 0
                    ? "0"
                    : conditionSummaries.Count + "[" + string.Join(";", conditionSummaries.ToArray()) + "]"));
            }
            else if (entrySummaries.Count > 0)
            {
                fields.Add("uEntries=" + string.Join(";", entrySummaries.ToArray()));
            }
        }

        private static void AppendConditionSummaries(
            JToken value,
            List<string> summaries)
        {
            JObject condition = value as JObject;
            if (condition != null)
            {
                summaries.Add(DescribeCondition(condition));
                return;
            }

            JArray conditions = value as JArray;
            if (conditions == null)
            {
                summaries.Add("type=" + value.Type);
                return;
            }

            for (int i = 0; i < conditions.Count; i++)
            {
                JObject item = conditions[i] as JObject;
                summaries.Add(item == null ? "type=" + conditions[i].Type : DescribeCondition(item));
            }
        }

        private static string DescribeCondition(JObject condition)
        {
            var fields = new List<string>();
            AppendScalar(condition, fields, "idx");
            AppendScalar(condition, fields, "skillIdx");
            AppendScalar(condition, fields, "skillCount");
            AppendScalar(condition, fields, "type");
            AppendScalar(condition, fields, "activate");
            AppendScalar(condition, fields, "count");
            AppendScalar(condition, fields, "callCount");
            AppendScalar(condition, fields, "param");
            AppendScalar(condition, fields, "isInvoke");
            AppendScalar(condition, fields, "isPreprocess");
            AppendScalar(condition, fields, "isIncludeSelf");
            AppendScalar(condition, fields, "isExcludePlayIdx");
            AppendShape(condition, fields, "target");
            AppendPresence(condition, fields, "condition");
            return fields.Count == 0 ? "empty" : string.Join("|", fields.ToArray());
        }

        private static string DescribeUListEntry(JToken value)
        {
            JObject entry = value as JObject;
            if (entry == null)
                return "type=" + value.Type;

            var fields = new List<string>();
            AppendIndexShape(entry, fields, "idxList");
            AppendScalar(entry, fields, "from");
            AppendScalar(entry, fields, "to");
            AppendScalar(entry, fields, "isSelf");
            AppendScalar(entry, fields, "skill");
            AppendScalar(entry, fields, "isInvoke");
            AppendScalar(entry, fields, "isPreprocess");
            AppendPresence(entry, fields, "cardId");
            AppendPresence(entry, fields, "count");
            AppendPresence(entry, fields, "param");
            return fields.Count == 0 ? "empty" : string.Join("|", fields.ToArray());
        }

        private static string DescribeOrderEntry(JObject entry)
        {
            if (entry == null)
                return "empty";

            var fields = new List<string>();
            foreach (JProperty property in entry.Properties())
            {
                JObject nestedObject = property.Value as JObject;
                if (nestedObject != null)
                {
                    var names = new List<string>();
                    foreach (JProperty nested in nestedObject.Properties())
                        names.Add(nested.Name);
                    fields.Add(property.Name + "Keys=" + FormatNames(names));
                    continue;
                }

                JArray nestedArray = property.Value as JArray;
                if (nestedArray != null)
                {
                    fields.Add(property.Name + "Array=" + nestedArray.Count);
                    continue;
                }

                // Scalar values are intentionally omitted here. The nested
                // key shape is enough to identify condition/result branches
                // without exposing private card or hand data.
                fields.Add(property.Name);
            }
            return fields.Count == 0 ? "empty" : string.Join("|", fields.ToArray());
        }

        private static void AppendIndexShape(JObject obj, List<string> fields, string name)
        {
            JToken value = obj[name];
            if (value == null)
                return;

            if (value is JArray array)
            {
                fields.Add(name + "Count=" + array.Count);
                fields.Add(name + "=" + FormatIndexValues(array));
                return;
            }

            fields.Add(name + "Type=" + value.Type);
        }

        private static string FormatIndexValues(JArray values)
        {
            if (values == null || values.Count == 0)
                return "[]";

            var result = new List<string>(values.Count);
            for (int i = 0; i < values.Count; i++)
                result.Add(values[i] == null ? "null" : values[i].ToString());
            return "[" + string.Join("|", result.ToArray()) + "]";
        }

        private static void AppendShape(JObject obj, List<string> fields, string name)
        {
            JToken value = obj[name];
            if (value == null)
                return;

            if (value is JArray array)
            {
                fields.Add(name + "Count=" + array.Count);
                if (array.Count > 0 && array[0] is JObject first)
                {
                    var names = new List<string>();
                    foreach (JProperty property in first.Properties())
                        names.Add(property.Name);
                    fields.Add(name + "Keys=" + FormatNames(names));
                }
                return;
            }

            if (value is JObject objValue)
            {
                var names = new List<string>();
                foreach (JProperty property in objValue.Properties())
                    names.Add(property.Name);
                fields.Add(name + "Keys=" + FormatNames(names));
                return;
            }

            fields.Add(name + "Type=" + value.Type);
        }

        private static void AppendPresence(JObject obj, List<string> fields, string name)
        {
            if (obj[name] != null)
                fields.Add(name + "=present");
        }

        private static void AppendScalar(JObject obj, List<string> fields, string name)
        {
            JToken value = obj[name];
            if (value != null && !(value is JArray) && !(value is JObject))
                fields.Add(name + "=" + value);
        }

        private static string FormatCounts(Dictionary<string, int> counts)
        {
            if (counts == null || counts.Count == 0)
                return "[]";

            var values = new List<string>();
            foreach (KeyValuePair<string, int> pair in counts)
                values.Add(pair.Key + ":" + pair.Value);
            return "[" + string.Join("|", values.ToArray()) + "]";
        }

        private static void AddCollectionShape(
            JObject root,
            List<string> fields,
            string name)
        {
            JToken value = root[name];
            if (value == null)
                return;

            if (value is JArray array)
            {
                fields.Add(name + "[]=" + array.Count);
                AddArrayEntryShape(array, fields, name + "Keys");
                return;
            }

            if (value is JObject obj)
            {
                var names = new List<string>();
                foreach (JProperty property in obj.Properties())
                    names.Add(property.Name);
                fields.Add(name + "Keys=" + FormatNames(names));
                return;
            }

            fields.Add(name + "Type=" + value.Type);
        }

        private static void AddArrayEntryShape(
            JArray array,
            List<string> fields,
            string name)
        {
            if (array == null || array.Count == 0)
                return;

            JToken first = array[0];
            if (first is JObject obj)
            {
                var names = new List<string>();
                foreach (JProperty property in obj.Properties())
                    names.Add(property.Name);
                fields.Add(name + "=" + FormatNames(names));
            }
            else if (first is JArray nested)
            {
                fields.Add(name + "=array(" + nested.Count + ")");
            }
            else
            {
                fields.Add(name + "Type=" + first.Type);
            }
        }

        private static string FormatNames(List<string> names)
        {
            if (names == null || names.Count == 0)
                return "[]";
            return "[" + string.Join("|", names.ToArray()) + "]";
        }

        private static void AddField(JToken json, List<string> fields, string name)
        {
            JObject obj = json as JObject;
            if (obj == null)
                return;

            JToken value = obj[name];
            if (value != null)
                fields.Add(name + "=" + value);
        }

        private static string ExtractUri(JToken json)
        {
            if (json == null)
                return null;

            JObject obj = json as JObject;
            if (obj != null)
                return obj["uri"]?.Value<string>();

            // Ordinary hand data is a positional JSON array. Its first item
            // is the numeric HAND_URI_TYPE value, which is useful in logs and
            // is intentionally left as-is for the native hand receiver.
            JArray array = json as JArray;
            return array != null && array.Count > 0
                ? array[0]?.ToString()
                : null;
        }

        public static byte[] Encode(JToken json)
        {
            if (json == null)
                throw new ArgumentNullException(nameof(json));
            string encrypted = CryptAES.encryptForNode(json.ToString(Formatting.None));
            return EncodeMessagePackString(encrypted);
        }

        private static int ExtractSequence(JToken json)
        {
            int sequence;
            JObject obj = json as JObject;
            if (obj != null)
            {
                JToken value = obj["pubSeq"];
                if (value != null && int.TryParse(value.ToString(), out sequence))
                    return sequence;
            }

            if (TryExtractReliableHandSequence(json, out sequence))
                return sequence;

            return 0;
        }

        /// <summary>
        /// Extracts the client pubSeq carried by a reliable hand packet.
        /// The stock client inserts it at index 3 only for hand URI types 2
        /// (SELECT_SKILL_URI) and 5 (SLIDE_OBJECT_URI). Other hand packets
        /// use index 3 as an ordinary input parameter.
        /// </summary>
        internal static bool TryExtractReliableHandSequence(
            JToken json,
            out int sequence)
        {
            sequence = 0;

            JArray array = json as JArray;
            if (array == null)
            {
                JObject wrapper = json as JObject;
                array = wrapper == null ? null : wrapper["StockHandData"] as JArray;
            }

            if (array == null || array.Count <= 3 || !IsReliableHandUri(array[0]))
                return false;

            return int.TryParse(array[3]?.ToString(), out sequence) && sequence > 0;
        }

        private static bool IsReliableHandUri(JToken value)
        {
            if (!int.TryParse(value?.ToString(), out int uri))
                return false;
            return uri == 2 || uri == 5;
        }

        private static string DecodeMessagePackString(byte[] data)
        {
            if (data == null || data.Length == 0)
                throw new InvalidOperationException("Empty MessagePack payload");

            int offset = 0;
            int length;
            byte marker = data[offset++];
            if ((marker & 0xE0) == 0xA0)
            {
                length = marker & 0x1F;
            }
            else if (marker == 0xD9)
            {
                Ensure(data, offset, 1);
                length = data[offset++];
            }
            else if (marker == 0xDA)
            {
                Ensure(data, offset, 2);
                length = (data[offset] << 8) | data[offset + 1];
                offset += 2;
            }
            else if (marker == 0xDB)
            {
                Ensure(data, offset, 4);
                length = checked((data[offset] << 24) |
                    (data[offset + 1] << 16) |
                    (data[offset + 2] << 8) |
                    data[offset + 3]);
                offset += 4;
            }
            else
            {
                throw new InvalidOperationException($"Unsupported MessagePack string marker 0x{marker:X2}");
            }

            Ensure(data, offset, length);
            return Encoding.UTF8.GetString(data, offset, length);
        }

        private static byte[] EncodeMessagePackString(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            var output = new List<byte>(bytes.Length + 5);
            if (bytes.Length < 32)
            {
                output.Add((byte)(0xA0 | bytes.Length));
            }
            else if (bytes.Length <= byte.MaxValue)
            {
                output.Add(0xD9);
                output.Add((byte)bytes.Length);
            }
            else if (bytes.Length <= ushort.MaxValue)
            {
                output.Add(0xDA);
                output.Add((byte)(bytes.Length >> 8));
                output.Add((byte)bytes.Length);
            }
            else
            {
                output.Add(0xDB);
                output.Add((byte)(bytes.Length >> 24));
                output.Add((byte)(bytes.Length >> 16));
                output.Add((byte)(bytes.Length >> 8));
                output.Add((byte)bytes.Length);
            }
            output.AddRange(bytes);
            return output.ToArray();
        }

        private static void Ensure(byte[] data, int offset, int length)
        {
            if (offset < 0 || length < 0 || offset > data.Length - length)
                throw new InvalidOperationException("Truncated MessagePack payload");
        }
    }
}
