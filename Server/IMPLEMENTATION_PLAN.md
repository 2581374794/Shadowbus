# Shadowbus Socket.IO 联机实现路线图

## 1. 目标

Shadowverse 原版实时对战使用 `BestHTTP.SocketIO`，通过 Engine.IO/Socket.IO 的 WebSocket transport 连接节点服务器。项目不再维护自定义 TCP 帧协议，也不再通过 Harmony 模拟 `RealTimeNetworkAgent` 的连接状态。

最终目标：

- 房主在游戏内启动一个 Socket.IO 节点服务器。
- 房主和加入者都使用原版 `RealTimeNetworkAgent` 连接该服务器。
- 服务器实现原版需要的握手、心跳、事件、ACK 和 MessagePack 数据传输。
- 房间创建/加入仍由本地任务路由接管，因为官方 HTTP API 已经关闭。
- 战斗阶段优先采用消息转发，只有实际双开反馈证明必须由服务器计算时才增加服务器权威逻辑。

本路线图以实际游戏双开反馈为验收依据，不新增独立协议测试或模拟客户端测试。

## 2. 明确的架构决策

### 2.1 不再使用的实现

以下内容全部退出运行路径并删除：

- 自定义 TCP `GameServer`。
- TCP `ClientConnection`。
- TCP `OnlineClient`。
- 自定义长度前缀、JSON 消息帧和 `MessageCodec`。
- `MessageType`/`NetworkMessage` 组成的 TCP 控制协议。
- 通过 `RealTimeNetworkPatch` 伪造 `Connect`、`IsOpen` 和 `Update`。
- 依赖 TCP 客户端回环连接来完成创建房间。

### 2.2 保留并重用的模块

- `RoomManager`、`GameRoom`、`Player` 和 `RoomRules`。
- 房间码生成/解析，但字段改为 Socket.IO 节点地址、端口和 `BattleId`。
- 联机玩家身份使用本地 `Mods/Profile.json` 中持久化的 `viewer_id`；缺失时自动生成随机值，不使用原版任务或认证中的 `viewerId`。
- `OnlineTaskRouter`，继续生成原版房间任务所需的响应结构。
- `OnlineRoomInputPatch`，继续支持粘贴自定义 `SV-...` 房间码。
- `OnlineRuntime`，改为管理 Socket.IO 节点生命周期和当前房间，不再持有 TCP 客户端。

### 2.3 联机改动边界（必须遵守）

- 联机对战优先完整复用游戏客户端现有的 `RealTimeNetworkAgent`、房间控制器、事件和状态机逻辑。
- 默认只修改嵌入式 `SocketIoServer` 及其服务器侧房间/消息处理；不得为了修复显示或状态而擅自改动客户端房间逻辑。
- 账号资料确实无法仅由服务器获得时，客户端可以发送资料，但必须先向项目负责人说明服务器侧方案为何不可行，并得到明确同意后才能增加客户端补丁。
- 客户端补丁获准后也只能承担资料传输等必要职责，不能重复实现原版房间状态机或 UI 流程。
- 验收以实际双开游戏反馈和日志为准，不新增独立协议测试或模拟客户端测试。

当前已获批准的例外：由于临时服务器无法访问已关闭的账号资料服务，客户端在原版加入房间调用完成后发送本地 Profile 快照；服务器将其合并到原版 `RoomEntry` 封包后转发，客户端房间状态仍由原版逻辑处理。

## 3. 目标架构

```text
┌──────────────────────────────────────────────────────────┐
│ Unity/BepInEx 插件                                       │
│                                                          │
│  OpenRoomBattleCreate/EnterRoomTask                      │
│             │                                             │
│             ▼                                             │
│      OnlineTaskRouter                                     │
│  本地创建/校验房间并返回原版任务响应                        │
│             │                                             │
│             ▼                                             │
│      OnlineRuntime                                        │
│  房间码、RoomManager、SocketIoServer 生命周期               │
└─────────────┼────────────────────────────────────────────┘
              │ 原版 RealTimeNetworkAgent
              ▼
┌──────────────────────────────────────────────────────────┐
│ SocketIoServer                                            │
│  Engine.IO handshake                                      │
│  Socket.IO namespace/event/ACK                            │
│  MessagePack binary payload                                │
│  alive/heartbeat/reconnect                                 │
│  房间连接与消息广播                                        │
└──────────────────────────────────────────────────────────┘
```

服务器以内嵌方式运行在房主进程中。由于 Unity/Mono 的 `HttpListener.AcceptWebSocketAsync` 未实现，当前使用 `TcpListener` 手动完成 HTTP Upgrade 和 RFC6455 帧处理；协议层保持原版兼容。

## 4. Socket.IO 协议范围

### 4.1 必须先确认的兼容参数

不能凭印象假设协议版本，需从游戏内置 BestHTTP 程序集和实际游戏日志确定：

- Engine.IO 版本（EIO）。
- Socket.IO 版本和 namespace。
- 默认路径是否为 `/socket.io/`。
- 文本帧和二进制帧的组合方式。
- MessagePack 编码器的具体格式。
- ACK 参数顺序和返回值。
- `RealTimeNetworkAgent` 使用的 query 参数及其加密方式。

已确认的连接参数包括：

- `BattleId`。
- 加密后的 `viewerId`。
- `User-Agent`。

### 4.2 原版事件

第一批实现以下事件，事件名保持原版大小写：

- `connect_socket`
- `synchronize`
- `hand`
- `alive`
- `opponent_lost`
- `reconnect_socket`
- `heartbeat_ping`
- `heartbeat_pong`
- `heartbeat_timeout`
- `msg`

Engine.IO ping/pong 和 Socket.IO ACK 必须独立于游戏事件处理，不能用普通房间消息替代。

## 5. 房间创建流程

1. `OpenRoomBattleCreateRoomTask` 被 `OnlineTaskRouter` 接管。
2. `OnlineRuntime.StartServer` 启动 Socket.IO listener。
3. `RoomManager` 直接创建 `GameRoom` 和房主 `Player`。
4. 生成 `BattleId` 和 `SV-...` 房间码。
5. 房间码写入可访问的节点地址、端口和 `BattleId`。
6. 任务响应中的 `node_server_url` 指向 Socket.IO 节点，而不是裸 WebSocket 或 TCP 地址。
7. 原版 `RoomConnectController.StartConnect` 继续执行，让原版 `RealTimeNetworkAgent` 建立实时连接。
8. Socket.IO 服务端收到房主连接后，将连接绑定到房间并返回 `connect_socket`/初始同步信息。

房间码同时携带房主的 Profile 快照，使 Guest 的 `oppo_info` 在进入任务阶段即可显示真实资料。

房主创建房间不再创建本地 TCP 客户端，也不再等待 TCP `CreateRoom` 响应。

## 6. 加入房间流程

1. `OpenRoomBattleEnterRoomTask` 被 `OnlineTaskRouter` 接管。
2. 客户端解析 `SV-...` 房间码，得到节点地址、端口和 `BattleId`。
3. 本地任务路由返回原版成功响应，包含 `battle_id`、规则和节点 URL。
4. 原版 `RealTimeNetworkAgent` 使用 `BattleId` 连接房主节点。
5. Socket.IO 服务端根据 `BattleId` 将新连接加入 `GameRoom`。
6. 服务端广播玩家列表和对手信息。
7. Guest 使用本地持久化 `viewer_id` 发送 Profile 快照，服务端再向房主刷新 Guest 资料。
8. 双方原生房间 UI 根据真实实时事件更新，不再由 TCP `PlayerList` 注入完成主要流程。

加入任务不应再启动第二个本地服务器，也不应创建自定义 TCP 连接。

## 7. 客户端 Patch 策略

### 7.1 立即移除

- `RealTimeNetworkPatch` 的注册和实现。
- `OnlineRoomAlivePatch` 的注册和实现。
- 阻止原版 `RoomConnectController.StartConnect` 的补丁逻辑。

移除后，原版实时 agent 必须真实运行；出现连接错误时修复 Socket.IO 服务端或任务响应，不再伪造 `IsOpen`。

### 7.2 暂时保留

- `OnlineTaskRouter`：替换已经关闭的官方房间 HTTP API。
- `OnlineRoomInputPatch`：房间码输入清洗和校验。
- 必要的房间 UI 辅助代码：只显示状态，不直接承载网络协议。

### 7.3 最终目标

实时连接、心跳、对手发现、准备状态和战斗消息全部由原版 `RealTimeNetworkAgent` 与 Socket.IO 服务端完成，Harmony 只保留官方 HTTP 房间任务的离线化适配。

## 8. 服务端模块规划

```text
Server/
├── SocketIO/
│   ├── SocketIoServer.cs          # TcpListener + WebSocket handshake
│   ├── SocketIoConnection.cs      # 单连接生命周期和发送队列
│   ├── EngineIoProtocol.cs        # open/ping/pong/close/upgrade
│   ├── SocketIoProtocol.cs        # namespace/event/ACK/packet ID
│   ├── MessagePackCodec.cs        # 原版 payload 编解码
│   └── RealtimeMessageRouter.cs   # 原版事件到房间服务的路由
├── Room/
│   ├── RoomManager.cs
│   ├── GameRoom.cs
│   ├── Player.cs
│   └── RoomRules.cs
├── Network/
│   ├── ConnectionCode.cs          # 仅保留房间码，不包含 TCP 协议
│   └── RealtimeEndpoint.cs        # 节点 URL 和 BattleId
└── OnlineRuntime.cs               # 生命周期、任务层和主线程状态
```

旧 TCP 文件不应在最终工程中保留：

- `Server/Core/GameServer.cs`
- `Server/Core/ClientConnection.cs`
- `Server/Core/MessageHandler.cs`
- `Server/Client/OnlineClient.cs`
- `Server/Network/MessageCodec.cs`
- `Server/Network/NetworkProtocol.cs`
- 仅服务于上述协议的测试工具和测试文档

## 9. 实现阶段

### Phase 0：清理 TCP（已完成）

- 删除 TCP server/client/codec/message handler。
- 移除 Plugin 中的 TCP 配置项和 Patch 注册。
- 将 `OnlineRuntime` 改为 Socket.IO 生命周期接口。
- 保留房间模型和房间码数据结构。
- 重写本文件。

完成标准：工程不再引用 `TcpClient`、`TcpListener`、`NetworkStream`、`MessageCodec` 或 `NetworkMessage`。

### Phase 1：Socket.IO listener（已完成）

- 在房主进程启动 HTTP listener。
- 实现 WebSocket upgrade。
- 实现 Engine.IO open、ping、pong、close。
- 实现 Socket.IO connect/disconnect 和基础 ACK。
- 输出连接 query、BattleId、viewerId 解析日志。

完成标准：真实游戏双开时，双方原版 `RealTimeNetworkAgent` 能连接到同一节点，日志不再出现模拟连接或 `RoomConnectChecker` 空引用。

### Phase 2：房间绑定和等待大厅（已完成）

- 根据 `BattleId` 查找 `GameRoom`。
- 绑定房主/加入者连接。
- 实现 `connect_socket`、`hand`、`synchronize`、`alive`。
- 玩家加入、离开和重连时广播对手信息。
- 处理 `opponent_lost` 和 `heartbeat_timeout`。

 完成标准：创建和加入房间后，双方原生房间界面都能看到对方，准备状态能够互相更新。

### Phase 3：`msg` 消息转发

- 解码或保留原版 MessagePack payload。
- 按房间转发 `msg`，保留 ACK。
- 记录事件名、方向、payload 长度和 ACK 结果，不记录敏感内容，除非实际排查需要。
- 处理乱序、重复、断线重连和发送失败。

完成标准：双方进入对战流程，至少能完成原版握手、选手确认、初始同步和一个完整回合的消息往返。

### Phase 4：战斗同步策略

优先采用透明转发：

- 服务端不解析每个卡牌效果。
- 服务器只负责房间隔离、顺序、ACK、断线和广播。
- 以实际双开反馈判断原版客户端是否能自行完成战斗状态。

只有出现以下问题时，才增加服务端状态：

- 客户端必须依赖服务器生成的初始手牌或随机结果。
- 某些动作必须由服务器确认后客户端才继续。
- 双方出现不可恢复的状态分歧。

### Phase 5：网络部署和稳定性

- `BindAddress` 默认监听 `0.0.0.0`。
- `AdvertisedAddress` 必须是加入者可访问的局域网、公网或虚拟网地址。
- 房间码不能写入 `127.0.0.1`，除非明确是同机双开。
- 支持端口占用提示、连接超时、房主退出和房间销毁。
- 必要时提供独立 Socket.IO server 进程，避免 Unity 进程承载 listener 的生命周期问题。

## 10. 配置目标

```ini
[SocketIO]
Enabled = true
BindAddress = 0.0.0.0
AdvertisedAddress =
Port = 29600
Path = /socket.io/
EngineIoVersion = auto
Namespace = /
ConnectionTimeoutSeconds = 15
HeartbeatTimeoutSeconds = 30
Verbose = false

[Online]
RoomCodePrefix = SV-
AllowSpectators = false
RelayBattleMessages = true
```

`EngineIoVersion = auto` 只表示启动时允许兼容层根据已确认的原版实现选择版本，不代表同时实现所有版本。

## 11. 实际游戏验收清单

每个阶段只使用实际游戏双开反馈和日志验收：

- Host 创建房间后不无限 loading。
- Guest 使用完整房间码加入成功。
- 双方日志显示真实 Socket.IO 连接，而非 `RealTimeNetworkPatch` 模拟成功。
- 双方都能看到对手姓名、ViewerId 和房间状态。
- 心跳持续正常，无误报 timeout。
- 双方准备后能够进入原版对战流程。
- 断开一方后另一方收到原版对手离线状态。

不再维护独立的 TCP 协议测试、伪造客户端测试或与实际游戏行为无关的协议快照测试。

## 12. 当前优先级

1. 删除 TCP 源码和引用。
2. 完成 `SocketIoServer` 的 Engine.IO/Socket.IO 基础握手。
3. 让原版 `RealTimeNetworkAgent` 真实连接本地节点。
4. 实现房间绑定、`hand`、`synchronize`、`alive`。
5. 用实际双开反馈修正 MessagePack、ACK 和事件顺序。
6. 实现 `msg` 转发，再决定是否需要服务器权威战斗状态。

## 13. 自定义房间赛制（已实现）

- 房主创建普通房间时，原版赛制按钮改为显示 `Mods/Format/*.json` 中的赛制列表，
  包含内置 Unlimited（任意卡牌、任意数量）。
- 原版 `BattleParameter.DeckFormat` 始终使用 `Format.Unlimited` 承载，避免触发
  Rotation、HOF 等官方格式限制；BO1/BO3/BO5 等房间流程仍由原版逻辑处理。
- 原版房间响应使用 API 格式编号：`2` 才是 Unlimited（`1` 是 Rotation），
  所有创建/加入响应均通过 `Data.FormatConvertApi(Format.Unlimited)` 生成。
- 房间码携带完整 `CustomFormatDefinition` 快照。Guest 解析房间码后安装该定义，
  不依赖本地是否存在同名文件。
- 房间选卡复用原版 `DeckSelectUIDialog` / `DeckUI`，不合规卡组置灰且不能确认；
  `PlayerControllerForOwn.SelectDeck` 也会做一次本地拦截。
- `OnlineTaskRouter` 对普通单卡组和多卡组选择任务再次按当前房间定义校验本地卡组。
- 房间实时 `RoomEntry` 采用单次、有序快照：先发送自身结果，再发送对手快照，
  避免重复 `RoomEntry` 触发原版 `InitializeOpponentPlayer()` 重置对手状态。

## 14. 当前优先级

1. 用实际双开游戏确认房主选择 Unlimited/自定义 JSON 后，双方显示同一赛制。
2. 用包含禁卡或超限 Token 的卡组确认双方列表置灰、确认被阻止，并检查日志中的规则 ID。
3. 确认 BO1/BO3/BO5 和退出房间流程不受自定义赛制接入影响。

## 15. 当前代码状态

已完成：

- TCP server/client/codec/message handler 已删除。
- `RealTimeNetworkPatch`、`OnlineRoomConnectPatch`、`OnlineRoomAlivePatch` 已删除并取消注册。
- `OnlineRuntime` 已改为直接创建房间和管理 Socket.IO listener。
- 基础 WebSocket listener、Engine.IO open、ping/pong、Socket.IO event/ACK 入口已加入。
- 任务响应已改为返回 Socket.IO 节点地址。

下一步主线：根据真实游戏双开反馈继续修正 Engine.IO/Socket.IO 事件顺序与战斗消息转发；
自定义房间赛制已接入，按第 14 节验收。

## 16. 真正房间对战实施方案

创建/加入房间、玩家资料、赛制和卡组选择已经完成。下一阶段目标是让双方在不重写原版战斗状态机的前提下，真正进入并完成一段对局。本阶段仍以实际双开游戏反馈为唯一验收依据，不新增独立协议测试或模拟客户端。

### 16.1 实施原则和边界

1. **客户端优先复用原版逻辑。** `RoomBase`、`RoomConnectController`、`RealTimeNetworkAgent`、`RealTimeNetworkBattleAgent`、战斗管理器和原版 UI 继续负责连接状态、房间状态、战斗状态、卡牌效果和界面更新。
2. **服务器只承担实时节点职责。** 默认只实现握手、心跳、房间隔离、消息顺序、ACK、重连和透明转发，不在服务器重写卡牌规则或客户端状态机。
3. **客户端补丁必须先报告并获准。** 如果真实游戏日志证明仅修改服务器无法让原版继续推进，先说明缺失的原版输入、拟修改的方法和影响范围，得到批准后再增加最小客户端补丁。补丁不得替代原版房间/战斗状态机。
4. **先观察再增加权威状态。** 只有透明转发无法完成初始化、随机结果或动作确认时，才在服务器保存必要的战斗状态；优先同步原版结果，不实现完整卡牌引擎。

### 16.2 Phase A：进入战斗前基线（只记录，不改行为）

先用两个真实游戏进程完成以下流程：Host 创建房间、Guest 加入、双方选择合法卡组、双方准备。为每个连接记录：

- Socket.IO/Engine.IO 握手、连接和断开时刻；
- 原版事件名和方向（Host -> Server、Guest -> Server、Server -> Host、Server -> Guest）；
- `msg` 和 `hand` 的原始帧类型、长度、ACK 编号；
- 每个方向的发送序号、接收序号和客户端 `MatchingStatus`；
- 从 `RoomReady` 到战斗场景加载、首个战斗 URI、首个战斗数据包之间的顺序；
- 是否仍有任务访问官方 HTTP 服务，是否出现 `reconnect`、`opponent_lost` 或超时。

本阶段的产物是第一处真实阻塞点，而不是新的协议实现。若双方尚未离开房间大厅，不进入后续阶段。

### 16.3 Phase B：双向序号和 ACK 桥接

原版 `RealTimeNetworkAgent` 使用两个独立序号：发送侧 `pubSeq`，接收侧 `playSeq`。服务器不得把发送方的 `pubSeq` 直接转发为对端的 `playSeq`。

新增服务器侧实时路由（名称可按现有代码调整）：

```text
Server/SocketIO/RealtimeMessageRouter.cs
Server/SocketIO/SequenceBridge.cs
Server/Room/BattleSession.cs
```

职责如下：

- Host -> Guest、Guest -> Host 分别维护独立的服务端投递序号；
- 每个客户端保留自己的发送 ACK 关联，收到 Socket.IO ACK 后只确认该客户端的 `pubSeq`；
- 接收端按 `playSeq` 严格递增投递，重复包去重，乱序包暂存或请求重发；
- `msg` 与 `hand` 使用两套独立序号、ACK 和待发送队列，不能混流；
- 转发层记录 URI、方向、序号、长度和结果，默认不记录卡牌 payload 内容；
- 连接关闭时保留尚未确认的消息和最后序号，为后续重连恢复提供依据。

完成标准：双方原版代理能够稳定完成战斗前握手/确认，不出现序号断裂、ACK 错配、重复 `RoomEntry` 或误判心跳超时。

### 16.4 Phase C：战斗初始化的透明转发

在序号桥接稳定后，继续让原版客户端自行生成或处理：

- Battle ID 和双方身份；
- 随机种子、先手和初始生命值；
- 牌库洗牌、初始手牌和起手调整；
- 战斗场景加载、战斗开始和首个回合。

服务器先不解释 MessagePack 内部卡牌字段，只保证原版初始化消息按正确方向、正确序号和正确 ACK 送达。若日志证明某项数据必须由服务器生成，才在 `BattleSession` 增加最小状态迁移：

```text
Waiting -> DeckReady -> BattleInitializing -> BattleStarted
```

服务器生成的数据必须保持原版字段和事件边界，客户端仍通过原版接收器完成状态更新。

完成标准：双方都进入战斗场景，看到正确对手身份，并收到一致的初始生命值、先手和手牌状态。

### 16.5 Phase D：最小可玩回合

按以下固定顺序逐步验收，每次只推进一个动作：

1. 双方进入战斗场景并完成起手；
2. 当前回合方抽一张牌；
3. 打出一张不涉及随机目标的普通卡；
4. 对手收到并完成原版动画、费用和场面更新；
5. 当前回合结束，另一方收到回合切换；
6. 另一方完成一次抽牌或普通卡牌动作。

每一步都检查双方场面、生命值、费用、手牌数量、回合号和日志序号是否一致。出现分歧时，先定位是消息未送达、顺序错误、ACK 错误还是原版状态拒绝；不直接在服务器实现卡牌效果。

完成标准：双方至少完成一个完整回合，且无需客户端战斗状态机补丁。

### 16.6 Phase E：随机数、隐藏信息和结果同步

只有 Phase D 的实际反馈表明客户端无法保持一致时，才增加服务器辅助：

- 统一随机种子、先手和洗牌结果；
- 只向拥有者发送抽牌和手牌内容，向对手发送原版允许公开的摘要；
- 对随机目标、随机伤害或随机生成牌同步最终结果，而不是在服务器重放完整卡牌逻辑；
- 保存必要的回合号、动作号和结果哈希，用于检测双方状态分歧。

服务器不得把隐藏手牌广播给另一方，也不得以自定义字段替代原版战斗事件。若必须增加客户端接收补丁，先向用户报告并获得批准。

### 16.7 Phase F：重连、掉线和结束流程

单回合稳定后再处理异常流程：

- Socket.IO 断线、心跳超时和 `opponent_lost`；
- 原版 `Reenter`/`resume`，恢复双方各自的 `playSeq` 和未确认消息；
- 对手掉线后的等待、判负或返回房间；
- `Release`、`Leave`、战斗结束和返回房间；
- Host 退出时销毁 `BattleSession`、关闭房间和停止内嵌 Socket.IO listener。

结束流程继续由原版客户端任务和 UI 处理，服务器只返回原版需要的结束事件和房间生命周期结果。

### 16.8 验收门槛和停止条件

每个阶段必须满足当前门槛后才能进入下一阶段：

| 阶段 | 必须观察到的结果 |
| --- | --- |
| A | 找到 `RoomReady` 后的首个真实阻塞点，且日志包含完整方向/序号/ACK 链路 |
| B | `msg`、`hand` 两条流均无序号断裂、重复投递或 ACK 错配 |
| C | 双方进入战斗并拥有一致的初始化状态 |
| D | 完成一个完整回合，双方状态一致 |
| E | 随机效果和隐藏信息不会造成状态分歧或泄露 |
| F | 断线、重连、结束和 Host 退出均能回到可用房间状态 |

如果某阶段发现必须修改客户端，立即停止该阶段的代码实现，先提交“问题证据、原版调用链、服务器侧不可行原因、最小补丁范围和回滚方式”供批准。

### 16.9 本阶段明确不做的事项

- 不重写原版战斗 UI、房间状态机或 `RealTimeNetworkAgent`；
- 不在没有真实日志证据前实现服务器版卡牌规则引擎；
- 不把 `msg` 和 `hand` 合并为一条自定义消息流；
- 不使用发送方 `pubSeq` 充当接收方 `playSeq`；
- 不增加独立协议测试、模拟客户端或与实际游戏行为无关的快照测试；
- 不擅自修改客户端逻辑。

## 17. 真正对战阶段当前任务

当前进入 **Phase C：原版战斗初始化实机验收**。真实日志已经确认此前的第一处阻塞点：双方都发送了 `InitRoomBattle`，但临时节点没有生成官方节点负责的 `Matched`，因此客户端一直停留在寻找对手界面。

Phase A 的服务器侧观测已经加入：`SocketIoServer` 记录收到的事件，`SocketIoConnection` 记录发出的二进制事件和 ACK，日志包含连接角色、方向、URI、序号字段、ACK 编号和 payload 长度，不记录卡牌或账号敏感内容。

已批准并实现的客户端例外：`OnlineTaskRouter` 接管 `RoomBattleDoMatchingTask` 及其变体，返回原版匹配响应（Host=`3007`、Guest=`3004`、`battle_state=0`、当前 `battle_id` 和本地 `card_master_id`）。该适配只替代已关闭的 HTTP 任务，不重写 `Matching_Room` 或战斗状态机。

本轮新增并已获批准的唯一客户端实时补丁：在联网模式下，原版 `RealTimeNetworkAgent.EmitMsgPack(InitRoomBattle)` 发包前附加当前已选牌组的 `shadowbusDeck` 快照。原因是原版 `SetupComplete` 和 `InitRoomBattle` 均不包含牌组，而 `Matched` 的 `selfDeck` 必须由节点返回；临时节点无法读取另一进程的客户端内存。补丁不接管发送、接收、场景加载或战斗状态机。

服务端已实现：

- 等待双方 `InitRoomBattle` 并校验各自的牌组快照；
- 生成数字 `BattleId`，避免原版 `GetBattleId()` 的 `long.Parse` 失败；
- 为双方确定一致的随机种子、先后手和各自洗牌后的牌库；
- 按各自视角生成原版 `Matched`，只向客户端发送其自己的真实 `selfDeck`；
- 等待双方原版场景加载完成并发送 `Loaded`；
- 按各自视角生成原版 `BattleStart`；
- `Matched` / `BattleStart` 复用对应方向的连续 `playSeq` 和 pending 重连队列，不转发包含私有牌组快照的 `InitRoomBattle`。

继续核对原版战斗初始化链路后，已确认 `BattleStart` 之后不能透明转发空的
`Deal` 请求：`NetworkMulliganMgr` 会由双方各发送一次 `Deal`，但
`NetworkBattleReceiver -> DealOperation -> MulliganPhaseBase.StartDeal` 必须收到
各三项 `self` / `oppo` 索引才能创建起手牌。该缺失可完全由服务器补齐，不需要
新的客户端补丁。

服务端现已继续实现：

- 为双方各生成六个不重复的牌库索引，前三个作为起手，后三个作为最多三张换牌；
- 等待双方原版 `Deal` 请求后，按各自视角下发原版 `Deal`；
- 校验原版 `Swap.idxList` 只能引用该玩家的三张起手牌，并生成换牌后的三张索引；
- 分别向提交者下发原版 `Swap`，双方提交后按各自视角下发原版 `Ready`；
- 所有响应继续走 `synchronize -> RealTimeNetworkBattleAgent -> NetworkBattleReceiver`
  原版接收链，不向对手泄露牌面，也不增加客户端战斗补丁；
- `Deal` / `Swap` / `Ready` 使用接收端连续的服务端 `playSeq`，客户端请求本身只做
  ACK 和服务器状态输入，不作为空消息转发给对端。

下一验收点使用真实双开游戏。预期日志依次出现 `InitRoomBattle accepted`、
`OUT synchronize Matched`、双方 `IN msg Loaded`、`OUT synchronize BattleStart`、
双方 `IN msg Deal`、`OUT synchronize Deal`，以及换牌后双方的
`OUT synchronize Swap` / `OUT synchronize Ready`。确认完成起手交换并进入第一回合后，
再依据真实 `TurnStart` / `PlayActions` / `Echo` 日志收紧 Host 权威转换；不提前增加
客户端战斗补丁。
