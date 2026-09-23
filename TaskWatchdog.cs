using System;
using Cute;
using UnityEngine;

namespace Shadowbus
{
    /// <summary>
    /// 最小看门狗（临时诊断，定位完就删）：游戏「卡死」时日志里往往什么都没有，
    /// 这次实测的卡死（进剧情页）就是停在
    ///
    ///   [Offlinizer] Intercepted Task: StoryInfoTask. Reading local data...   ← 之后一行都没有
    ///
    /// 进程却还在跑帧、CPU 接近 0、窗口也能响应 —— 也就是主线程在**空转等某个条件**，
    /// 而不是死循环或崩溃。这里每隔几秒把「当前挂着哪个任务、挂多久、网络状态、插件正在做什么」
    /// 打一行，下次卡死就能直接从日志读出卡在哪一步。
    ///
    /// 只读状态、只打日志，不改变任何行为。
    /// </summary>
    internal static class TaskWatchdog
    {
        /// <summary>同一个任务挂满这么久就报一次。</summary>
        private const float ReportAfterSeconds = 5f;

        /// <summary>同一局最多报这么多行，避免刷屏。</summary>
        private const int MaxReports = 12;

        private static string _currentTask;

        private static float _pendingSince;

        private static float _lastReport;

        private static int _reports;

        internal static void Tick()
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                NetworkManager manager = Toolbox.NetworkManager;
                NetworkTask task = manager != null ? manager.lastRequestTask : null;
                string name = task != null ? task.GetType().Name : "(none)";

                if (!string.Equals(name, _currentTask, StringComparison.Ordinal))
                {
                    if (_currentTask != null && _currentTask != "(none)")
                    {
                        Plugin.Logger.LogInfo(
                            $"[Task] '{_currentTask}' finished after {now - _pendingSince:F1}s " +
                            $"(next: '{name}')");
                    }

                    _currentTask = name;
                    _pendingSince = now;
                    _lastReport = 0f;
                    _reports = 0;
                    return;
                }

                if (task == null || now - _pendingSince < ReportAfterSeconds || now - _lastReport < ReportAfterSeconds)
                {
                    return;
                }

                _lastReport = now;
                if (_reports++ >= MaxReports)
                {
                    return;
                }

                Plugin.Logger.LogWarning(
                    $"[Hang] task '{name}' has been pending {now - _pendingSince:F1}s " +
                    $"(isConnect={manager.isConnect}, isTimeOut={manager.isTimeOut}, isError={manager.isError}, " +
                    $"op='{PerfTrace.CurrentOperation ?? "-"}')");
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Task] watchdog failed: {exception.Message}");
            }
        }
    }
}
