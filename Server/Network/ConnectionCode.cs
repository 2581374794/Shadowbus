using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Shadowbus.Server.Room;

namespace Shadowbus.Server.Network
{
    /// <summary>
    /// 房间连接码生成和解析
    /// </summary>
    public static class ConnectionCode
    {
        private const string Prefix = "SV-";
        private const int ChecksumLength = 4;

        /// <summary>
        /// 生成房间码
        /// </summary>
        /// <param name="serverIp">服务器 IP 地址</param>
        /// <param name="port">服务器端口</param>
        /// <param name="roomId">房间 ID</param>
        /// <returns>房间码，格式：SV-XXXXXXXX-XXXX</returns>
        public static string Generate(string serverIp, int port, string roomId)
        {
            return Generate(serverIp, port, roomId, null);
        }

        /// <summary>
        /// Generates a room code and embeds the host profile so a guest can
        /// populate the original enter-room response before its WebSocket is
        /// established. Older codes without this field remain valid.
        /// </summary>
        public static string Generate(string serverIp, int port, string roomId, global::Shadowbus.P2PProfile hostProfile)
        {
            return Generate(serverIp, port, roomId, hostProfile, null);
        }

        public static string Generate(
            string serverIp,
            int port,
            string roomId,
            global::Shadowbus.P2PProfile hostProfile,
            RoomRules roomRules)
        {
            if (string.IsNullOrEmpty(serverIp))
                throw new ArgumentException("Server IP cannot be empty", nameof(serverIp));
            if (port <= 0 || port > 65535)
                throw new ArgumentException("Invalid port number", nameof(port));
            if (string.IsNullOrEmpty(roomId))
                throw new ArgumentException("Room ID cannot be empty", nameof(roomId));

            try
            {
                // 创建连接数据
                var data = new ConnectionData
                {
                    ServerIp = serverIp,
                    Port = port,
                    RoomId = roomId,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    HostProfile = hostProfile,
                    RoomRules = roomRules
                };

                // 序列化为 JSON
                string json = JsonConvert.SerializeObject(data);
                byte[] jsonBytes = Encoding.UTF8.GetBytes(json);

                // 压缩
                byte[] compressed = Compress(jsonBytes);

                // Base32 编码（比 Base64 更适合手动输入）
                string encoded = Base32Encode(compressed);

                // 计算校验和
                string checksum = CalculateChecksum(encoded);

                // 生成最终房间码
                return $"{Prefix}{encoded}-{checksum}";
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[ConnectionCode] Generate error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 解析房间码
        /// </summary>
        public static bool TryParse(string code, out ConnectionData data, out string error)
        {
            data = null;
            error = null;

            if (string.IsNullOrEmpty(code))
            {
                error = "Connection code is empty";
                return false;
            }

            try
            {
                // 去除空白字符
                code = code.Trim();

                // 验证前缀
                if (!code.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    error = "Invalid connection code format (missing prefix)";
                    return false;
                }

                // 移除前缀
                code = code.Substring(Prefix.Length);

                // 分离编码和校验和
                string[] parts = code.Split('-');
                if (parts.Length != 2)
                {
                    error = "Invalid connection code format (missing checksum)";
                    return false;
                }

                string encoded = parts[0];
                string checksum = parts[1];

                // 验证校验和
                string calculatedChecksum = CalculateChecksum(encoded);
                if (!checksum.Equals(calculatedChecksum, StringComparison.OrdinalIgnoreCase))
                {
                    error = "Connection code checksum verification failed";
                    return false;
                }

                // Base32 解码
                byte[] compressed = Base32Decode(encoded);

                // 解压缩
                byte[] jsonBytes = Decompress(compressed);

                // 反序列化
                string json = Encoding.UTF8.GetString(jsonBytes);
                data = JsonConvert.DeserializeObject<ConnectionData>(json);

                // 验证数据完整性
                if (data == null || string.IsNullOrEmpty(data.ServerIp) ||
                    data.Port <= 0 || string.IsNullOrEmpty(data.RoomId))
                {
                    error = "Connection code contains invalid data";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to parse connection code: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// 压缩数据
        /// </summary>
        private static byte[] Compress(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionMode.Compress))
                {
                    gzip.Write(data, 0, data.Length);
                }
                return output.ToArray();
            }
        }

        /// <summary>
        /// 解压缩数据
        /// </summary>
        private static byte[] Decompress(byte[] data)
        {
            using (var input = new MemoryStream(data))
            using (var output = new MemoryStream())
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            {
                gzip.CopyTo(output);
                return output.ToArray();
            }
        }

        /// <summary>
        /// Base32 编码（使用 RFC 4648 标准字母表，不含 0、1、O、I 以避免混淆）
        /// </summary>
        private static string Base32Encode(byte[] data)
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            StringBuilder result = new StringBuilder();

            int bits = 0;
            int bitCount = 0;

            foreach (byte b in data)
            {
                bits = (bits << 8) | b;
                bitCount += 8;

                while (bitCount >= 5)
                {
                    bitCount -= 5;
                    int index = (bits >> bitCount) & 0x1F;
                    result.Append(alphabet[index]);
                }
            }

            if (bitCount > 0)
            {
                int index = (bits << (5 - bitCount)) & 0x1F;
                result.Append(alphabet[index]);
            }

            return result.ToString();
        }

        /// <summary>
        /// Base32 解码
        /// </summary>
        private static byte[] Base32Decode(string encoded)
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            encoded = encoded.ToUpperInvariant();

            using (var output = new MemoryStream())
            {
                int bits = 0;
                int bitCount = 0;

                foreach (char c in encoded)
                {
                    int index = alphabet.IndexOf(c);
                    if (index < 0)
                        throw new ArgumentException($"Invalid Base32 character: {c}");

                    bits = (bits << 5) | index;
                    bitCount += 5;

                    if (bitCount >= 8)
                    {
                        bitCount -= 8;
                        output.WriteByte((byte)(bits >> bitCount));
                        bits &= (1 << bitCount) - 1;
                    }
                }

                return output.ToArray();
            }
        }

        /// <summary>
        /// 计算校验和（使用 CRC32）
        /// </summary>
        private static string CalculateChecksum(string data)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(data);
            uint crc = CalculateCrc32(bytes);
            return crc.ToString("X4"); // 4 位十六进制
        }

        /// <summary>
        /// CRC32 计算
        /// </summary>
        private static uint CalculateCrc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;

            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    if ((crc & 1) != 0)
                        crc = (crc >> 1) ^ 0xEDB88320;
                    else
                        crc >>= 1;
                }
            }

            return ~crc;
        }
    }

    /// <summary>
    /// 连接数据
    /// </summary>
    public class ConnectionData
    {
        public string ServerIp { get; set; }
        public int Port { get; set; }
        public string RoomId { get; set; }
        public long Timestamp { get; set; }
        public global::Shadowbus.P2PProfile HostProfile { get; set; }
        public RoomRules RoomRules { get; set; }
    }
}
