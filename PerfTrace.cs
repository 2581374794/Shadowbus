using System;
using System.Diagnostics;
using UnityEngine;

namespace Shadowbus
{
    /// <summary>
    /// 卡死定位用的小工具：给插件里可能吃时间的补丁包一层跑表。
    ///
    /// 两个用途：
    /// <list type="number">
    ///   <item><c>Enter(name)</c> 在慢操作进行中把名字挂到 <see cref="CurrentOperation"/> 上，
    ///         卡死看门狗报 stall 时就能打出来「当时插件正在做什么」。没有插件操作在执行时
    ///         它是 null —— 那说明时间是被游戏自己吃掉的，不用再往插件这边找。</item>
    ///   <item>单次耗时超过 <see cref="SlowMilliseconds"/> 就写一行 <c>[Perf]</c> 日志，
    ///         不用等卡死就能看到是哪个补丁慢。</item>
    /// </list>
    ///
    /// 只在主线程的补丁里用；除日志外不改任何游戏状态。
    /// </summary>
    internal static class PerfTrace
    {
        /// <summary>单次超过这个毫秒数才记一行。</summary>
        internal const long SlowMilliseconds = 200;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>最内层正在执行的插件操作名；没有插件操作在执行时为 null。</summary>
        internal static string CurrentOperation;

        /// <summary>最近一次被记下来的慢操作。</summary>
        internal static string LastOperation;
        internal static long LastOperationMilliseconds;

        /// <summary>
        /// 和探针日志里的 <c>t=</c> 对齐的秒数。用 <c>Time.realtimeSinceStartup</c>，
        /// 拿不到时退回插件自己的 Stopwatch，保证跑表本身不会抛。
        /// </summary>
        internal static double Now
        {
            get
            {
                try
                {
                    return Time.realtimeSinceStartup;
                }
                catch (Exception)
                {
                    return Clock.Elapsed.TotalSeconds;
                }
            }
        }

        internal static Scope Enter(string name)
        {
            return new Scope(name, CurrentOperation);
        }

        internal static void Note(string name, long milliseconds)
        {
            if (milliseconds < SlowMilliseconds)
            {
                return;
            }

            LastOperation = name;
            LastOperationMilliseconds = milliseconds;
            try
            {
                Plugin.Logger?.LogInfo($"[Perf] {name} took {milliseconds} ms at t={Now:F2}s");
            }
            catch (Exception)
            {
                // 跑表不能影响游戏。
            }
        }

        internal readonly struct Scope : IDisposable
        {
            private readonly string name;
            private readonly string previous;
            private readonly long startedAt;

            internal Scope(string name, string previous)
            {
                this.name = name;
                this.previous = previous;
                startedAt = Clock.ElapsedMilliseconds;
                CurrentOperation = name;
            }

            public void Dispose()
            {
                long elapsed = Clock.ElapsedMilliseconds - startedAt;
                CurrentOperation = previous;
                Note(name, elapsed);
            }
        }
    }
}
