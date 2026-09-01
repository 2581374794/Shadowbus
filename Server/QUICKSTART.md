# Socket.IO 联机快速开始

当前联机层已经切换到 Socket.IO，旧的自定义 TCP server/client 不再存在。

## 房主

1. 启动游戏并进入房间界面。
2. 点击“创建房间”。
3. 插件启动 Socket.IO listener，并创建 `GameRoom`。
4. 复制生成的 `SV-...` 房间码。
5. 将房间码发送给另一名玩家。

默认监听端口为 `29600`，默认绑定地址为 `0.0.0.0`。跨机器联机时，房间码中的地址必须是加入者可以访问的局域网、公网或虚拟网地址，不能使用 `127.0.0.1`。

## 加入者

1. 启动游戏并进入房间界面。
2. 粘贴完整的 `SV-...` 房间码。
3. 点击“加入房间”。
4. 原版 `RealTimeNetworkAgent` 会根据任务返回的 `node_server_url` 连接房主的 Socket.IO 节点。

## 关键日志

- `[OnlineRuntime] Socket.IO runtime initialized`
- `[SocketIO] Listener started`
- `[OnlineRuntime] Socket.IO room created`
- `[OnlineRuntime] Socket.IO endpoint accepted`
- `[SocketIO] Client connected`

如果日志仍出现 `TcpClient`、`GameServer`、`OnlineClient` 或 `RealTimeNetworkPatch`，说明加载的插件不是当前版本。

## 当前状态

Socket.IO listener 已提供基础 Engine.IO open、ping/pong 和 Socket.IO 事件入口。原版 MessagePack、ACK、房间绑定和 `msg` 战斗转发按 `IMPLEMENTATION_PLAN.md` 的阶段继续实现。

实际游戏双开反馈是唯一验收依据，不使用独立协议测试工具。
