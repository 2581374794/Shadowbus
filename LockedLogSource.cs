using System.Threading;
using BepInEx.Logging;

namespace Shadowbus
{
    /// <summary>
    /// 给插件日志加一把锁，让每条日志**整行**写出去。
    ///
    /// 为什么要这个：插件里同时有多个线程在写日志 —— Socket.IO 服务器的接收/发送线程、
    /// 任务看门狗、Unity 主线程。BepInEx 的监听器（磁盘 + Unity 日志）本身不是按行原子的，
    /// 于是玩家反馈的日志里出现了"半截行互相插入"的情况，例如：
    ///
    ///   [SocketIO] Text send failed (f2ec5050ceb1[Warning:…] [SocketIO] Text send failed…
    ///   …一个现有的连接。
    ///
    /// 结果是玩家贴过来的日志我们经常读不通。这里把 `Plugin.Logger` 换成这个包装：
    /// 所有日志调用（`LogInfo/LogWarning/LogError/LogDebug/LogMessage/Log`）都经过同一把锁，
    /// 再转给真正的 <see cref="ManualLogSource"/> —— 调用点一个字都不用改。
    /// </summary>
    public sealed class LockedLogSource
    {
        private readonly ManualLogSource _source;
        private readonly object _sync = new object();

        public LockedLogSource(ManualLogSource source)
        {
            _source = source;
        }

        /// <summary>真正的 BepInEx 日志源（需要它的场合，比如交给别的组件）。</summary>
        public ManualLogSource Source => _source;

        public void Log(LogLevel level, object data)
        {
            lock (_sync)
            {
                _source.Log(level, data);
            }
        }

        public void LogDebug(object data)
        {
            lock (_sync)
            {
                _source.LogDebug(data);
            }
        }

        public void LogInfo(object data)
        {
            lock (_sync)
            {
                _source.LogInfo(data);
            }
        }

        public void LogMessage(object data)
        {
            lock (_sync)
            {
                _source.LogMessage(data);
            }
        }

        public void LogWarning(object data)
        {
            lock (_sync)
            {
                _source.LogWarning(data);
            }
        }

        public void LogError(object data)
        {
            lock (_sync)
            {
                _source.LogError(data);
            }
        }
    }
}
