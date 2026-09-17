using System;
using System.IO;

namespace Shadowbus
{
    /// <summary>WAV 的格式信息，供解码与原生播放共用。</summary>
    public struct WavInfo
    {
        public int DataOffset;
        public int DataLength;
        public int Channels;
        public int SampleRate;
        public int BitsPerSample;
        public ushort FormatTag;
        /// <summary>每声道样本数，用来算时长。</summary>
        public int Frames;

        public float Duration
        {
            get { return SampleRate > 0 ? (float)Frames / SampleRate : 0f; }
        }
    }

    /// <summary>
    /// 解析 WAV 的格式信息，并支持把 16 位样本按音量缩放。
    ///
    /// 为什么不交给 Unity：<c>UnityWebRequestMultimedia.GetAudioClip</c> 在这些卡牌语音上会
    /// 返回一个「非 null 但 0 采样」的空 AudioClip——请求成功、也不报错，播放代码就以为
    /// 加载好了；而这个工程的 Unity 音频在项目设置里本来就关着（音频全走 CRIWARE/ADX2），
    /// <c>AudioClip.SetData</c> 也会直接失败。所以本地音频自己解析、自己播放，
    /// 见 <see cref="NativeWavPlayer"/>。
    ///
    /// 认 PCM 8/16/24/32 位、IEEE float 32 位，以及 WAVE_FORMAT_EXTENSIBLE 包装的这几种；
    /// A-law / μ-law 与压缩 WAV 会被拒绝并给出原因。
    /// </summary>
    public static class WavDecoder
    {
        private const ushort FormatPcm = 0x0001;
        private const ushort FormatFloat = 0x0003;
        private const ushort FormatExtensible = 0xFFFE;
        private const ushort FormatAlaw = 0x0006;
        private const ushort FormatMulaw = 0x0007;

        /// <summary>读取文件字节。失败时返回 null 并给出原因。</summary>
        public static byte[] ReadAllBytes(string fullPath, out string error)
        {
            error = null;
            try
            {
                return File.ReadAllBytes(fullPath);
            }
            catch (Exception exception)
            {
                error = "cannot read the file (" + exception.Message + ")";
                return null;
            }
        }

        /// <summary>
        /// 解析 RIFF 结构。不创建任何 Unity 对象，所以即使 Unity 音频不可用也能拿到样本信息。
        /// </summary>
        public static bool TryReadInfo(byte[] bytes, out WavInfo info, out string error)
        {
            info = default(WavInfo);
            error = null;

            if (bytes == null || bytes.Length < 44 ||
                !IsTag(bytes, 0, "RIFF") || !IsTag(bytes, 8, "WAVE"))
            {
                error = "not a RIFF/WAVE file";
                return false;
            }

            ushort formatTag = 0;
            int channelCount = 0;
            int sampleRate = 0;
            int bitsPerSample = 0;
            int dataOffset = -1;
            int dataLength = 0;
            bool sawFormat = false;

            // 逐个 chunk 找 fmt / data。chunk 大小是奇数时后面会补一个填充字节。
            int position = 12;
            while (position + 8 <= bytes.Length)
            {
                int body = position + 8;
                int chunkSize = BitConverter.ToInt32(bytes, position + 4);
                if (chunkSize < 0)
                {
                    error = "corrupted chunk size";
                    return false;
                }

                if (IsTag(bytes, position, "fmt ") && chunkSize >= 16 && body + 16 <= bytes.Length)
                {
                    formatTag = BitConverter.ToUInt16(bytes, body);
                    channelCount = BitConverter.ToUInt16(bytes, body + 2);
                    sampleRate = BitConverter.ToInt32(bytes, body + 4);
                    bitsPerSample = BitConverter.ToUInt16(bytes, body + 14);
                    sawFormat = true;

                    // WAVE_FORMAT_EXTENSIBLE：真正的格式在 SubFormat GUID 的头两个字节。
                    if (formatTag == FormatExtensible && chunkSize >= 26 && body + 26 <= bytes.Length)
                    {
                        formatTag = BitConverter.ToUInt16(bytes, body + 24);
                    }
                }
                else if (IsTag(bytes, position, "data"))
                {
                    dataOffset = body;
                    dataLength = Math.Min(chunkSize, bytes.Length - body);
                    if (dataLength < 0)
                    {
                        dataLength = 0;
                    }
                }

                position = body + chunkSize + (chunkSize & 1);
            }

            if (!sawFormat)
            {
                error = "no fmt chunk";
                return false;
            }

            if (dataOffset < 0)
            {
                error = "no data chunk";
                return false;
            }

            if (channelCount <= 0 || sampleRate <= 0)
            {
                error = string.Concat(
                    "bad format (channels=", channelCount, ", sample rate=", sampleRate, ")");
                return false;
            }

            if (formatTag == FormatAlaw || formatTag == FormatMulaw)
            {
                error = "A-law / mu-law WAV is not supported; save as 16-bit PCM";
                return false;
            }

            if (formatTag != FormatPcm && formatTag != FormatFloat)
            {
                error = string.Concat("compressed WAV (format 0x", formatTag.ToString("X4"),
                    ") is not supported; save as 16-bit PCM");
                return false;
            }

            int bytesPerSample = bitsPerSample / 8;
            if (bytesPerSample <= 0)
            {
                error = string.Concat("unsupported bit depth ", bitsPerSample);
                return false;
            }

            int totalSamples = dataLength / bytesPerSample;
            if (totalSamples <= 0)
            {
                error = "the data chunk carries no samples";
                return false;
            }

            info = new WavInfo
            {
                DataOffset = dataOffset,
                DataLength = dataLength,
                Channels = channelCount,
                SampleRate = sampleRate,
                BitsPerSample = bitsPerSample,
                FormatTag = formatTag,
                Frames = totalSamples / channelCount
            };

            return true;
        }

        /// <summary>
        /// 复制一份 WAV 字节并把 16 位 PCM 样本按 volume 缩放（0~1）。
        /// 原生播放没有音量通道，只能在数据上做。非 16 位原样返回（音量按 100% 播）。
        /// </summary>
        public static byte[] ScalePcm16Copy(byte[] source, float volume)
        {
            byte[] copy = (byte[])source.Clone();

            WavInfo info;
            string ignored;
            if (!TryReadInfo(copy, out info, out ignored) ||
                info.FormatTag != FormatPcm ||
                info.BitsPerSample != 16 ||
                volume >= 0.999f)
            {
                return copy;
            }

            if (volume < 0f)
            {
                volume = 0f;
            }

            int count = info.DataLength / 2;
            for (int i = 0; i < count; i++)
            {
                int offset = info.DataOffset + i * 2;
                short value = BitConverter.ToInt16(copy, offset);
                int scaled = (int)(value * volume);
                if (scaled > short.MaxValue)
                {
                    scaled = short.MaxValue;
                }
                else if (scaled < short.MinValue)
                {
                    scaled = short.MinValue;
                }

                copy[offset] = (byte)(scaled & 0xFF);
                copy[offset + 1] = (byte)((scaled >> 8) & 0xFF);
            }

            return copy;
        }

        private static bool IsTag(byte[] bytes, int offset, string tag)
        {
            if (offset + tag.Length > bytes.Length)
            {
                return false;
            }

            for (int i = 0; i < tag.Length; i++)
            {
                if (bytes[offset + i] != tag[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
