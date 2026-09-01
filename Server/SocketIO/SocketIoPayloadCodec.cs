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
                string encrypted = DecodeMessagePackString(data);
                string plain = CryptAES.decryptForNode(encrypted);
                json = JToken.Parse(plain);
                uri = json["uri"]?.Value<string>();
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

            JArray hand = json["StockHandData"] as JArray;
            if (hand != null && hand.Count > 3)
                fields.Add("StockHandData[3]=" + hand[3]);

            JArray array = json as JArray;
            if (array != null && array.Count > 3)
                fields.Add("array[3]=" + array[3]);

            return fields.Count == 0
                ? "fallback=" + fallbackSequence
                : string.Join(",", fields.ToArray());
        }

        private static void AddField(JToken json, List<string> fields, string name)
        {
            JToken value = json[name];
            if (value != null)
                fields.Add(name + "=" + value);
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
            JToken value = json["pubSeq"];
            if (value != null && int.TryParse(value.ToString(), out sequence))
                return sequence;

            JArray hand = json["StockHandData"] as JArray;
            if (hand != null && hand.Count > 3 && int.TryParse(hand[3].ToString(), out sequence))
                return sequence;

            JArray array = json as JArray;
            if (array != null && array.Count > 3 && int.TryParse(array[3].ToString(), out sequence))
                return sequence;

            return 0;
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
