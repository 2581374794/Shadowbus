namespace Shadowbus.Server.Core
{
    /// <summary>
    /// Socket.IO 节点配置
    /// </summary>
    public class ServerConfig
    {
        /// <summary>
        /// Socket.IO listener 绑定地址
        /// </summary>
        public string BindAddress { get; set; } = "0.0.0.0";

        /// <summary>
        /// Socket.IO listener 端口
        /// </summary>
        public int Port { get; set; } = 29600;

        /// <summary>
        /// Engine.IO/Socket.IO HTTP 路径。
        /// </summary>
        public string SocketPath { get; set; } = "/socket.io/";

        /// <summary>
        /// Engine.IO 版本。auto 表示由兼容层选择。
        /// </summary>
        public string EngineIoVersion { get; set; } = "auto";

        /// <summary>
        /// Socket.IO namespace。
        /// </summary>
        public string Namespace { get; set; } = "/";

        /// <summary>
        /// 公布地址（写入房间码）
        /// </summary>
        public string AdvertisedAddress { get; set; } = string.Empty;

        /// <summary>
        /// 最大连接数
        /// </summary>
        public int MaxConnections { get; set; } = 100;

        /// <summary>
        /// 心跳间隔（秒）
        /// </summary>
        public int HeartbeatInterval { get; set; } = 5;

        /// <summary>
        /// 连接超时（秒）
        /// </summary>
        public int ConnectionTimeout { get; set; } = 30;

        /// <summary>
        /// 房间超时（分钟）
        /// </summary>
        public int RoomTimeout { get; set; } = 60;

        /// <summary>
        /// 详细日志
        /// </summary>
        public bool Verbose { get; set; } = false;

        /// <summary>
        /// 记录所有消息
        /// </summary>
        public bool LogMessages { get; set; } = false;

        /// <summary>
        /// 记录状态变化
        /// </summary>
        public bool LogStateChanges { get; set; } = false;
    }
}
