using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Shadowbus
{
    /// <summary>
    /// 一段已经准备好、可以直接交给 winmm 播放的音频。
    ///
    /// 关键在于"准备好"发生在预加载阶段，而不是播放那一帧：这里先按当前音量把样本缩放好，
    /// 并用 <c>GCHandle</c> 把缓冲固定住。播放时只剩一次 <c>PlaySound(指针)</c>，
    /// 主线程上没有分配、没有拷贝、没有逐样本运算，所以不会让出牌动画顿一下。
    ///
    /// 音量变了才需要重新缩放（设置里改语音音量这种），那是偶发操作，代价可以接受。
    /// </summary>
    public sealed class NativeWavClip : IDisposable
    {
        private byte[] _source;
        private byte[] _scaled;
        private GCHandle _pinned;

        /// <summary>音频时长（秒），用来估算"还在播"。</summary>
        public float Duration { get; private set; }

        /// <summary>当前 <see cref="_scaled"/> 是按哪个音量缩放的。</summary>
        public float Volume { get; private set; }

        internal NativeWavClip(byte[] sourceBytes, float volume, float duration)
        {
            _source = sourceBytes;
            Duration = duration;
            Render(volume);
        }

        internal IntPtr Address
        {
            get { return _pinned.IsAllocated ? _pinned.AddrOfPinnedObject() : IntPtr.Zero; }
        }

        /// <summary>
        /// 保证缓冲是按 volume 缩放好的。音量没变就什么都不做。
        /// </summary>
        public void EnsureVolume(float volume)
        {
            if (Math.Abs(volume - Volume) < 0.005f && _pinned.IsAllocated)
            {
                return;
            }

            Render(volume);
        }

        private void Render(float volume)
        {
            Release();

            if (_source == null)
            {
                return;
            }

            _scaled = WavDecoder.ScalePcm16Copy(_source, volume);
            _pinned = GCHandle.Alloc(_scaled, GCHandleType.Pinned);
            Volume = volume;
        }

        private void Release()
        {
            if (_pinned.IsAllocated)
            {
                _pinned.Free();
            }

            _scaled = null;
        }

        public void Dispose()
        {
            Release();
            _source = null;
        }
    }

    /// <summary>
    /// 用 Windows 自带的 winmm 播放 WAV，绕开 Unity 的音频系统。
    ///
    /// 为什么需要它：这个游戏关掉了 Unity 音频（音频全部由 CRIWARE/ADX2 原生输出，
    /// 场景里连一个 AudioListener 都没有）。表现是 <c>AudioClip.SetData</c> 直接失败并打印
    /// "AudioClip contains no data"——也就是说 Unity 侧根本没有音频数据缓冲，
    /// 补 AudioListener、重设 AudioSettings 都没用，AudioSource 永远不会出声。
    ///
    /// 所以本地卡牌语音在 Unity 音频不可用时改走这里：把 WAV 字节交给
    /// <c>PlaySound(SND_MEMORY | SND_ASYNC)</c>，由 Windows 直接输出。
    ///
    /// 只有 Windows 平台可用；其它平台会返回 false，调用方照旧走 Unity。
    /// </summary>
    public static class NativeWavPlayer
    {
        private const uint SndAsync = 0x0001;
        private const uint SndNoDefault = 0x0002;
        private const uint SndMemory = 0x0004;

        [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool PlaySound(IntPtr data, IntPtr module, uint flags);

        private static NativeWavClip _active;
        private static float _playingUntil;
        private static bool _unsupportedLogged;
        private static bool _warmedUp;
        private static GCHandle _warmUpPinned;

        public static bool IsSupported
        {
            get
            {
                return Application.platform == RuntimePlatform.WindowsPlayer ||
                       Application.platform == RuntimePlatform.WindowsEditor;
            }
        }

        public static bool IsPlaying
        {
            get { return _active != null && Time.realtimeSinceStartup < _playingUntil; }
        }

        /// <summary>
        /// 预加载阶段调用：把音频缩放好、固定好，后面播放就只是发一次指针。
        /// </summary>
        public static NativeWavClip Prepare(byte[] wavBytes, float volume, out string error)
        {
            error = null;

            WavInfo info;
            if (!WavDecoder.TryReadInfo(wavBytes, out info, out error))
            {
                return null;
            }

            try
            {
                return new NativeWavClip(wavBytes, volume, info.Duration);
            }
            catch (Exception exception)
            {
                error = "cannot prepare the native buffer (" + exception.Message + ")";
                return null;
            }
        }

        /// <summary>
        /// 播放一段已经准备好的音频。muted 为 true 时不发声，但仍按"播过了"计时。
        /// </summary>
        public static bool Play(NativeWavClip clip, float volume, bool muted)
        {
            if (!IsSupported)
            {
                if (!_unsupportedLogged)
                {
                    _unsupportedLogged = true;
                    Plugin.Logger.LogWarning(
                        "[CardVoice] Native WAV playback is only available on Windows; " +
                        "falling back to Unity audio.");
                }

                return false;
            }

            if (clip == null)
            {
                return false;
            }

            Stop();

            _active = clip;
            _playingUntil = Time.realtimeSinceStartup + clip.Duration;

            if (muted || volume <= 0f)
            {
                // 静音：不用真的播，但要让上层知道"播过了"。
                return true;
            }

            // 音量变了才重新缩放；平时这里是空操作。
            clip.EnsureVolume(volume);

            if (clip.Address == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                if (!PlaySound(clip.Address, IntPtr.Zero, SndMemory | SndAsync | SndNoDefault))
                {
                    Plugin.Logger.LogWarning(
                        "[CardVoice] winmm PlaySound refused the local voice file.");
                    _active = null;
                    return false;
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning(
                    "[CardVoice] Native WAV playback failed: " + exception.Message);
                _active = null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// 预热 winmm：第一次 PlaySound 要初始化音频设备和工作线程，会有几十毫秒开销。
        /// 在预加载阶段放一段听不见的静音先把这一步做掉，播放时就不会顿。
        /// </summary>
        public static void WarmUp()
        {
            if (_warmedUp || !IsSupported)
            {
                return;
            }

            _warmedUp = true;

            try
            {
                byte[] warmUp = BuildSilentWav();
                _warmUpPinned = GCHandle.Alloc(warmUp, GCHandleType.Pinned);
                PlaySound(_warmUpPinned.AddrOfPinnedObject(), IntPtr.Zero, SndMemory | SndAsync | SndNoDefault);
            }
            catch (Exception)
            {
                // 预热失败无所谓，最多第一次播放慢一点。
            }
        }

        public static void Stop()
        {
            _active = null;
            _playingUntil = 0f;

            if (!IsSupported)
            {
                return;
            }

            try
            {
                // 传 null 即停止当前播放。
                PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>1 个样本的静音 16bit 单声道 WAV，用来预热 winmm。</summary>
        private static byte[] BuildSilentWav()
        {
            const int sampleRate = 8000;
            const int dataLength = 2;
            byte[] wav = new byte[44 + dataLength];

            WriteAscii(wav, 0, "RIFF");
            BitConverter.GetBytes(36 + dataLength).CopyTo(wav, 4);
            WriteAscii(wav, 8, "WAVE");
            WriteAscii(wav, 12, "fmt ");
            BitConverter.GetBytes(16).CopyTo(wav, 16);
            BitConverter.GetBytes((ushort)1).CopyTo(wav, 20);
            BitConverter.GetBytes((ushort)1).CopyTo(wav, 22);
            BitConverter.GetBytes(sampleRate).CopyTo(wav, 24);
            BitConverter.GetBytes(sampleRate * 2).CopyTo(wav, 28);
            BitConverter.GetBytes((ushort)2).CopyTo(wav, 32);
            BitConverter.GetBytes((ushort)16).CopyTo(wav, 34);
            WriteAscii(wav, 36, "data");
            BitConverter.GetBytes(dataLength).CopyTo(wav, 40);

            return wav;
        }

        private static void WriteAscii(byte[] target, int offset, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                target[offset + i] = (byte)text[i];
            }
        }
    }
}
