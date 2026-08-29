# P2P Host 权威服务器重构方案

最后更新：2026-08-29

## 约束

- Host 是唯一权威状态来源，职责是替代已关闭的官方服务器。
- 客户端请求与服务器响应必须区分；不得将客户端 `PlayActions` 请求直接当作对手客户端应接收的响应包。
- 优先使用原版 `NetworkBattleSender`、`NetworkBattleReceiver`、`NetworkBattleData`、`ReplaceReceivedCard`、`OperateReceive` 与 `NetworkOperationCollection` 的字段和调用顺序。
- 只有原版协议无法表示的状态允许使用 P2P 扩展字段。扩展只能在原版接收边界接入，不能在动作结束后替换已绑定 UI 的手牌对象。
- 朋友对战不需要反作弊，但不改变 Host 唯一权威的要求。

## 原版客户端链路

```text
NetworkStandardBattleMgr / NetworkBattleSender
  -> 客户端请求
  -> 官方服务器填充并下发响应
  -> NetworkBattleReceiver.ReceivedMessage
  -> NetworkBattleManagerBase.ConductReceiveData
  -> NetworkBattleData.BeforeSettingReceiveData
  -> ReplaceReceivedCard
  -> OperateReceive.StartOperate
  -> NetworkOperationCollection
```

`knownList`、`uList`、`oppoTargetList`、`keyAction`、选择/验证数据必须在
`BeforeSettingReceiveData` 前完整存在。客户端不应依赖动作结束后的全局快照修补。

## 目标结构

```text
原版客户端请求
  -> Host: ClientBattleRequest
  -> Host 权威状态与原版逻辑处理
  -> Host: ServerBattleDelivery（原版客户端响应语义）
  -> 客户端原版接收链
```

Host 必须拥有两位玩家完整的权威私有状态。Guest 本地执行只能作为原版客户端预测；
最终条件、随机、卡牌身份、胜负和回合转换以 Host 输出为准。

## 阶段 0：协议版本与旧路径隔离

- [x] 在加入房间时协商 P2P 协议版本；版本不一致时拒绝建立对局。
- [x] 新对局不再发起 authority request/result。
- [x] 普通操作恢复原版 `NetworkStandardBattleMgr -> NetworkBattleSender` 时序。
- [x] 回合转换恢复 `TurnEnd -> Judge -> JudgeOperation -> ControlTurnStartPlayer`。
- [x] 隔离遗留 authority request/result/replay 的运行入口。

验收：新对局日志没有 `authority_request`、`p2pAuthorityLocalReplay`、
`Host scheduled authority action` 或 `Guest received authority result`。

## 阶段 1：Host 服务器响应边界

- [x] 定义 `ClientBattleRequest`、`HostAuthoritativeAction`、`ServerBattleDelivery`。
- [x] Host 接收原版客户端请求，但不再把请求直接转发为响应；请求与响应使用不同对象，Host 响应有单调 `p2pServerActionId` 用于追溯。
- [x] Host 为每个动作输出原版接收端所需字段：`orderList`、`knownList`、`uList`、
  `oppoTargetList`、`keyAction`、选择/验证数据、序列与回合字段；原版发送端产生的
  注册数据被保留，Host 仅在响应边界补齐缓存的 `knownList` 身份/状态。
- [x] 所有客户端响应只通过 `RealTimeNetworkAgent.ProcessingRecivedData` 进入原版接收链。

验收：Host 输出包可按动作 ID 追溯到一个输入请求；接收端不需要 authority-local replay。

### 执行记录：2026-08-28 — 阶段 1 响应边界收敛

1. 原版链路：`NetworkBattleSender` 通过 `SendCardDataMaker` 从
   `RegisterActionManager`/`RegisterUnapprovedList` 生成 `orderList`/`uList`，
   目标在请求端为 `targetList`；服务端响应经 `NetworkBattleReceiver` 时必须改为
   `oppoTargetList`，随后 `NetworkBattleData.BeforeSettingReceiveData` 先处理
   `knownList` 与 `uList`，最后由 `OperateReceive`/`NetworkOperationCollection` 执行。
2. 原偏差：运行时在 Host 收到请求后仍尝试以 Host 当前 `BattleEnemy` 重建 Guest 的
   卡牌身份。这会把已经变化的 Host 本地对象混入请求响应，违反原版“发送端注册结果
   -> 服务端响应 -> 接收端替换”的方向。
3. 本次恢复：`P2PAuthoritativeServer` 现在独立保存请求和响应；仅响应对象写入
   `turnState`、服务器动作编号和请求序列，并在对手响应边界完成
   `targetList -> oppoTargetList`。Host 侧重建 Guest 卡牌的兜底已移除。
4. 暂存扩展：`p2pServerActionId` 与 `p2pClientSequence` 只用于追溯，不参与原版逻辑；
   `NetworkBattleReceiver` 会忽略未知字段。卡牌身份、私有条件和随机结果尚未完全迁入
   Host 输出，属于阶段 2/3，不能在本阶段删除现存兼容数据。
5. 验收：构建后检查每个 Host 转发的 `PlayActions` 日志均有 `serverActionId`，且接收端
   只看到 `oppoTargetList`，不能同时有请求侧 `targetList`。

## 阶段 2：Host 私有状态、条件与随机

- [x] Guest 向 Host 提交其初始私有状态；Host 已在独立协议缓存中记录双方基线。当前为兼容既有卡牌替换路径，Host 基线仍会发送给 Guest；待阶段 3 原生 `knownList/uList` 身份路径验收后移除。
- [x] Host 维护手牌、牌组、融合材料、保留区、附加状态与必要历史；协议级卡牌区域、状态版本、动作转移账本、抽牌/弃牌/破坏/召唤/回手/融合计数及原版历史字段已完成。
  所有条件相关历史计数和融合累计字段均在版本化 history payload 中传递。
- [x] Host 用原版注册/验证结构填充私有条件、Highlander、共鸣、宇宙、融合/破坏/进化次数；注册元数据、修订历史和原版条件边界已完成。
- [x] 随机目标沿用原版 `uList.randomTargetIdx`；Host 原生执行生成结果，并在响应边界完成规范化、校验和审计。
- [x] 删除 `NetworkExecutionInfoCreator.CheckCondition` 中的客户端最终覆盖逻辑；新对局仅消费原版 `orderList/uList` 接收数据，旧 authority 包仅保留窄兼容解码。

### 阶段 2 子任务状态矩阵（2026-08-29）

| 子任务 | 状态 | 当前结果 |
|---|---|---|
| 2.1.1 初始 `private_state` 基线 | `[x]` | Host 已记录双方卡牌身份、区域初值和可用卡牌字段。 |
| 2.1.2 区域转移账本 | `[x]` | 已覆盖 `idx`/`idxList`、`knownList`、`uList`、`orderList` 的基础区域转移。 |
| 2.1.3 原版卡牌状态字段 | `[x]` | 已保存费用、身材、法术增幅、倒数、附加技能、融合材料等字段，并在原版接收边界补齐缺失字段。 |
| 2.1.4 native timing 下的增量私有状态 | `[x]` | 已恢复变化卡牌的增量发送；仍通过原版接收前/后边界应用，不恢复完整快照主路径。 |
| 2.1.5 全部原版历史与融合累计字段 | `[x]` | 已覆盖原版 BattlePlayerBase 的条件相关标量、持久历史列表和融合累计字段；临时动作工作列表仍由原生操作构造。 |
| 2.1.6 动作包事件计数 | `[x]` | 已从原版 `type`/`keyAction` 记录攻击、进化、激奏、结晶、融合、葬送和抉择等动作事件。 |
| 2.1.7 私有增量中的生成/抽牌身份 | `[x]` | `p2pHiddenCards`/双所有者私有状态中的新卡会在响应进入原版接收链前补入规范 `knownList`。 |
| 2.1.8 毁灭/抉择 Brave 历史列表 | `[x]` | 增加 `DestroyedWhenDestroyCards`、`ChoiceBraveCards` 到同步历史白名单，供原版对应过滤器读取。 |
| 2.1.9 `last_target` 历史列表 | `[x]` | 增加嵌套 `LastTargetCardsList` 同步，供原版 `last_target` 条件和目标过滤器读取。 |
| 2.2.1 移除 Guest 私有条件结果 | `[x]` | Guest→Host 边界剥离 `skillConditionCheck`，防止客户端条件结果覆盖 Host。 |
| 2.2.2 Host 进入原版条件判断 | `[x]` | Host 通过 `NetworkBattleReceiver` 和原版 `NetworkExecutionInfoCreator.CheckCondition` 执行条件。 |
| 2.2.3 Host 条件输入完整性 | `[x]` | Host 在原版条件边界前应用版本化历史，覆盖手牌/牌组状态、共鸣、宇宙、Highlander、进化、破坏、抽牌、弃牌和融合条件输入。 |
| 2.2.4 原版 pre-action 条件边界 | `[x]` | 已在 native `ReceivedMessage` 构造操作前应用 `p2pPlayerHistoryBefore`，动作完成后的历史状态仍延后应用。 |
| 2.2.5 native timing 的 pre-action 历史捕获 | `[x]` | 本地动作开始时记录玩家历史基线，发送动作结束包时同时携带前置与后置修订状态。 |
| 2.3.1 规范化 `knownList` 身份 | `[x]` | 已拆分分组索引为唯一标量身份，避免 `ReplaceReceivedCard` 重复匹配。 |
| 2.3.2 移除旧替换/快照兜底 | `[~]` | native v3 已禁止动作后对象替换；兼容解码器仍保留，待双端验收后删除。 |
| 2.4.1 原版随机结果传递 | `[x]` | Host 原生执行生成 `uList.randomTargetIdx`，并在响应边界统一规范化和校验后交给原版接收链路。 |
| 2.4.1.1 `randomTargetIdx` 原版列表规范化 | `[x]` | Host 仅规范化 native `uList`/`orderList` 中显式的随机结果，不再从旧 Action Manifest 或缓存猜测结果。 |
| 2.4.1.2 Host 原生生成/验证 RNG | `[x]` | Guest 请求由 Host 的原生 NetworkOperationCollection 生成随机结果；Host 在响应边界验证并记录结果，不另造第二套 RNG。 |

说明：`[x]` 表示该子任务已实现并通过现有自动化测试；`[~]` 表示已实现基础能力但仍有原版字段/场景未覆盖；`[ ]` 表示尚未开始或属于后续阶段。阶段 2 的实现子任务已全部完成；阶段 3 的兼容路径清理仍按后续阶段执行。

验收：私有条件与随机不再由接收客户端重新决定；Host 与客户端动作 ID 的结果一致。

### 执行记录：2026-08-28 — 阶段 2 私有卡牌缓存起点

1. 原版链路：`NetworkBattleData.BeforeSettingReceiveData` 在 `OperateReceive` 前通过
   `knownList`、再通过 `uList` 调用 `ReplaceReceivedCard`。因此服务器必须在下发响应时
   已拥有会进入或离开私有区域的卡牌身份。
2. 原偏差：项目只有客户端侧 `BattleCardBase`/隐藏状态缓存；Host 收到 Guest 动作时可能
   读取已变化的 `BattleEnemy` 来补数据，不能作为服务器权威状态。
3. 本次恢复：Host 新增仅由协议数据构成的 `P2PAuthoritativeServer` 卡牌缓存。它接收双方
   `private_state` 基线，并在每个客户端请求中吸收原版 `knownList`、`uList`、`orderList`；
   在生成对手响应前，仅对触及私有区且缺少身份的索引补入原版 `knownList`。
4. 暂存扩展：基线仍沿用既有 `private_state`，且仍有旧客户端兼容适配器消费它；这是阶段 3
   之前的过渡，不是最终的动作后快照主路径。Host 缓存不读取任何 `BattleCardBase`。
5. 验收：首张抽牌、生成、弃牌、融合材料移动和回手的响应，在进入
   `BeforeSettingReceiveData` 前应已拥有对应 `knownList` 身份；日志可用 `serverActionId`
   追溯到输入请求。

### 执行记录：2026-08-28 — 阶段 2 原版条件语义

1. 原版链路：发送端的 `RegisterSkillConditionCheck` 经 `SendCardDataMaker.OrderListCreate`
   写入 `orderList`；`NetworkBattleReceiver.MakeReceiveCardDataList` 将 `activate`、`count`、
   `param` 条目收集为 `SkillConditionCheckList`，`NetworkExecutionInfoCreator.CheckCondition`
   通过该接收数据执行原版的网络分支。
2. 原偏差：P2P Harmony 后缀在该原版逻辑之后又用接收端本地手牌、牌组、历史调用
   `ConditionFilterCollection.Filtering`，并以结果覆盖原版接收结果。这让两端再次各自推演。
3. 本次恢复：新对局不再进行这个客户端最终重算；响应中保留的原版
   `orderList/uList` 是唯一条件输入。Host 响应层负责在接收前补齐卡牌身份，而不改变
   条件判定顺序。
4. 暂存扩展：仅对已经在队列中的旧 authority 回放包读取其历史布尔字段；协议版本 3
   不发送该字段，正常对局不会进入该路径。
5. 验收：包含 Highlander、共鸣、宇宙、手牌/牌组计数条件的动作，接收端不再出现
   `Replaced server-only private condition result` 日志；`orderList` 中的原版条件条目仍被
   `NetworkBattleReceiver` 消费。

### 执行记录：2026-08-28 — 阶段 2 原版随机目标语义

1. 原版链路：`RegisterUnapproved.RandomTargetIdx` 经 `SendCardDataMaker.MakeUList` 写入
   `uList.randomTargetIdx`；`NetworkBattleReceiver.MakeReceiveCardData` 将其写入
   `CardDataModel.RandomTargetIndex`；`NetworkExecutionInfoCreator` 最终通过
   `GetUnapprovedCardObj` 使用该结果。
2. 原偏差：P2P 仍保留一个 Action Manifest 随机目标 side-channel。协议 v3 正常动作并不
   发送该 side-channel，但其 Harmony 后缀仍可能捕获本地随机结果，形成无效的第二路径。
3. 本次恢复：原生时序下随机目标后缀立即返回，正常对局只消费原版 `uList.randomTargetIdx`。
4. 暂存扩展：旧 authority replay 的随机目标批次仍保留在已隔离代码中，防止旧的在途包被
   当作新对局处理；协议版本 3 不会建立此类对局。
5. 验收：随机复活、随机召唤、随机伤害目标的响应包包含原版 `uList.randomTargetIdx`，且
   不再出现 `Attached authoritative random skill target result(s)` 或
   `Replaced locally calculated random targets` 日志。

### 执行记录：2026-08-28 — 阶段 2 原版卡牌状态字段

1. 原版链路：`NetworkBattleReceiver.MakeReceiveCardData` 已原生支持 `cost`、法术增幅、
   攻击/生命/倒计时、职业、种族、`attachTarget`、融合材料等卡牌字段；这些字段在
   `knownList`/`uList` 到达 `BeforeSettingReceiveData` 前被解析。
2. 原偏差：Host 缓存仅记住 `cardId/cost`，导致一张已经在手牌或牌组中被原版注册数据
   修改过的卡，在后续公开/使用时可能只恢复基础身份。
3. 本次恢复：Host 对原版动作字段建立逐卡缓存，并将缺失身份的响应 `knownList` 回填为
   原版 `CardDataModel` 字段集合。没有发送完整手牌扫描或在动作后重绑 UI 卡牌对象。
4. 暂存扩展：无法由 `CardDataModel` 表示的复杂预处理、保存目标和融合累计状态仍属于
   后续最小扩展迁移范围；本次不以全量快照覆盖原版字段。
5. 验收：手牌/牌组中的费用、法术增幅、攻击/生命、种族与附加技能相关的原版字段，在
   后续抽取、公开、融合或使用时均从 Host 的 `knownList` 进入原版替换链。

### 执行记录：2026-08-29 — 阶段 2.1 Host 区域状态与动作历史

1. 原版链路：`Deal`/`Swap` 建立初始手牌，之后 `NetworkBattleSender` 将每次注册的
   `RegisterStateChangeCard`/`RegisterUnapproved` 编码为 `orderList`/`uList`；服务器响应
   在客户端 `BeforeSettingReceiveData` 前提供这些数据。
2. 原偏差：Host 只有逐卡字段缓存，没有区域位置、动作转移历史或状态版本，无法作为
   后续私有条件（手牌计数、牌组计数、融合材料、破坏历史）的稳定输入。
3. 本次恢复：新增 Host 协议级区域状态机和有界动作账本；`Deal`、`Swap`、`knownList`、
   `uList`、`orderList` 统一更新卡牌区域、字段和动作历史。响应仍在原版接收边界生成，
   不改动 `NetworkBattleReceiver` 的调用顺序。
4. 暂存扩展：初始私有基线没有携带每张卡的原始区域，因此基线卡先标记为未知；首个
   `Deal`/`orderList` 到达后建立准确区域。复杂历史计数和融合累计状态继续在下一步接入。
5. 验收：协议测试验证动作后卡牌区域、状态版本、动作历史、跨动作 Buff 字段均可从
   Host 缓存恢复；主项目和 P2P 协议测试必须通过。

## 阶段 3：卡牌身份和最小扩展

- [x] 优先通过原版 `knownList`、`uList`、`unapprovedList` 与注册数据传递身份；Host 缓存
  仅在原版接收边界补齐缺失的逐卡身份和 `CardDataModel` 支持字段。
- [x] 移除 native v3 动作结束后替换手牌对象的路径；动作快照身份不再触发延迟对象替换，
  仅初始 `private_state` 基线保留兼容替换。
- [x] 若原版字段确实无法承载某项状态，只发送变化卡牌的逐卡扩展，并在原版接收边界应用；
  已完成字段级 delta、字段删除合并，以及 Host 从权威账本重建响应扩展。
- [x] native v3 主路径已删除全手牌/全牌组扫描、完整历史快照和动作后对象重绑；旧兼容
  路径仍保留到阶段 3 验收结束。

验收：新抽到、生成、融合变身、激奏/结晶、回手的卡牌都由原版替换流程建立对象。

### 执行记录：2026-08-29 — 阶段 3 native 主路径收敛

1. 原版链路：动作由 NetworkBattleSender 生成 knownList、uList、orderList，
   接收端依次执行 NetworkBattleReceiver.ReceivedMessage、
   NetworkBattleData.BeforeSettingReceiveData、ReplaceReceivedCard、
   OperateReceive.StartOperate。卡牌身份必须在该边界确定，不能等动作 VFX
   完成后再替换已经绑定到 UI/操作对象的手牌。
2. 当前偏差：native v3 之前仍会在每个有序动作扫描全部私有卡牌，并保留
   旧 Action Manifest 条件/随机副通道；隐藏状态适配器也可能在动作后尝试
   对象替换。
3. 本次恢复：AppendLocalHiddenCardState 现在只采集当前 native 动作引用的
   卡牌索引，初始 private_state 仍负责完整基线；native v3 不再发布或展开
   Action Manifest；动作快照中的身份不匹配只报告协议错误，不再进行延迟对象
   替换。只有初始私有基线保留兼容替换，以建立原版接收所需的初始对象。
4. 必须保留的扩展：原版 CardDataModel 无法承载的附加技能、融合累计和
   泛型技能状态仍通过变化卡牌的 P2P 增量字段传递，但不改变 native
   knownList/uList/orderList 的执行顺序。Host 响应中的扩展由
   privateCardsByOwner 重建，不直接转发客户端输入。
5. 验证：主项目 Release 构建、P2P 协议测试和 git diff --check 均通过。
   尚未宣称真实双端游戏验收完成。
6. 阶段 3 收敛：本地和 Host-authority 捕获现在比较完整状态，只发送变化的顶层
   字段；字段被清除时使用 p2pRemovedFields。接收端和 Host 账本先合并 delta，
   再应用 P2P-only 状态，避免缺失字段被误解释为清空。
7. Host 响应边界：客户端的 p2pHiddenCards 只作为 Host 账本输入；响应在路由前
   从 privateCardsByOwner 重建 p2pAuthorityHiddenStates 及兼容字段，不直接转发
   请求方扩展。原版 knownList/uList/orderList、目标和随机字段保持原有顺序。
8. 阶段 3 的字段级扩展和 Host 响应覆盖已完成并有协议测试覆盖；旧兼容解码器及
   初始 private_state 的一次性替换仍保留，待真实双端验收后再进入阶段 4 清理。
9. ServerBattleDelivery 现在按接收方已确认的 private_state 基线再次压缩账本状态：
   首次无基线发送完整卡牌，后续只发送变化字段和 p2pRemovedFields，并在发送删除
   标记后清理对应账本基线。

## 阶段 4：回合、胜负和清理

- [x] Host 处理 Judge：非终局回送 Judge，终局向双方下发 JudgeResult；客户端仍由原版
  `TurnEndOperation -> SendJudge -> JudgeOperation -> ControlTurnStartPlayer` 推进回合。
- [x] Host 处理 Retire、断线和结果映射；RetireWin/RetireLose、DisconnectWin/
  DisconnectLose 均由 Host 统一映射并发送双方本地结果。
- [x] native v3 运行路径已不再进入旧 authority replay、Action Manifest 全量回放、
  随机强制覆盖或动作后完整快照；旧解码入口只消费旧包并记录 Warning。
- [x] 诊断只在 Host 权威边界出现实际差异时输出 Error；普通注入/队列/兼容性问题输出
  Warning，且相同状态不会输出 Error。

验收：双方结束回合、额外回合、投降、致死、断线结果均通过原版 ReceiveData 路径完成。

### 执行记录：2026-08-29 — 阶段 4 回合、结果与清理

1. 原版链路：`TurnEndOperation` 完成后由客户端发送 `Judge`，服务器判断结果并回送
   `Judge` 或下发 `JudgeResult`；`OperateReceive` 再调用
   `NetworkOperationCollection.JudgeOperation`/`JudgeResultOperation`。投降同样经过
   `RetireOperation -> ReceiveRetire -> JudgeResult`，断线由原版 disconnect checker
   触发 `OppoDisconnectVictory` 或 `DisconnectLose`。
2. 当前实现：Host 继续把 `Judge` 路由回动作来源，并由 Host 状态决定终局；Retire 记录
   退出方后统一生成 `RetireLose/RetireWin`；断线根据本地是否已投降或已有终局状态生成
   `RetireLose`、已有终局结果或 `DisconnectWin`。
3. 清理边界：native v3 发包前移除旧 manifest、旧随机/条件 side-channel 和完整状态
   checkpoint；接收端消费旧 authority replay 包但不执行，随机强制覆盖函数在 native
   模式下直接返回，避免旧路径重新改变原生 RNG/操作时序。
4. 诊断边界：状态比较仅处理带有 Host 权威边界的检查；无差异记录 Debug，实际差异
   才记录 Error。远端诊断携带 severity，非数据差异统一记录 Warning，避免误报。
5. 验证：新增结果映射测试，复用现有状态诊断覆盖并加入诊断等级回归；Release 构建、P2P 协议测试和
   `git diff --check` 均须通过。真实双端验收仍按必测矩阵执行。

## 必测矩阵

1. 双方结束回合与额外回合。
2. 随从、法术、攻击、进化、融合。
3. 激奏、结晶、融合变身。
4. 葬送、弃牌触发、抽牌、Token、回手。
5. 修改手牌/牌组以及依赖私有区域/历史的条件。
6. 随机目标、随机复活、随机生成。
7. 投降、致死、回合结束致死、断线。

## 执行规则

每次 P2P 改动前必须记录：

1. 原版对应请求、响应、接收和执行链路；
2. 当前实现的偏差；
3. 修改如何恢复原版语义；
4. 仍必须保留的扩展状态及原因；
5. 对应验收用例和构建/测试结果。

### Execution record: 2026-08-29 - Stage 2.1 register-state completion

1. Native path verified: `RegisterActionBase.MakeSendData` serializes card
   references in `idx` (which may be a list), while `SendCardDataMaker` emits
   `RegisterUnapproved` metadata through `uList` and movement/effect records
   through `orderList`. The receiver consumes these records before
   `NetworkBattleData.BeforeSettingReceiveData`, then executes through
   `OperateReceive` and `NetworkOperationCollection`.
2. Previous deviation: the Host ledger primarily recognized `idxList` and
   therefore missed native `idx` arrays. It also retained only a subset of
   `RegisterUnapproved` fields, so fusion, attached-skill, invoked, shortage,
   random-target, and skill-movement context could be lost between actions.
3. Current restoration: Host parsing now accepts both native `idx` and
   `idxList`; it records `RegisterUnapproved` metadata, fusion ingredients and
   counters, metamorphose/add/alter registrations, and preserves missing native
   card fields from the Host ledger before the response enters the native
   receiver. Explicit fields emitted by the native sender remain authoritative.
4. Required extension: the protocol-level Host ledger remains necessary for
   private card identity and cumulative state that the closed official server
   would have supplied in `knownList`/`uList`; it is not used to replace native
   operations after VFX.
5. Verification: `dotnet build .\\Shadowbus.csproj -c Release --no-restore`,
   `dotnet run --project .\\Tests\\P2PProtocolTests\\P2PProtocolTests.csproj
   -c Release --no-restore`, and `git diff --check` all pass. Existing build
   warnings remain unchanged.

6. Follow-up in the same layer: missing `RegisterUnapproved` fields are now
   enriched from the Host ledger only when absent in the native request. This
   covers `skill` (skill-card index, published count, movement), key-card
   indexes, random targets, attached-skill publish counts, invoked and shortage
   flags. Play history is counted against the primary `playIdx`, so a card's
   own hand-to-cemetery movement is not confused with a discard effect emitted
   by the same `PlayActions` packet.

### Execution record: 2026-08-29 - Stage 2.2 Host-side condition boundary

1. Native path verified: the official server returns `skillConditionCheck`
   registrations in `orderList`; the receiver converts them into
   `SkillConditionCheckList`, and `NetworkExecutionInfoCreator.CheckCondition`
   consumes that list only when it is present. If it is absent, the original
   `ExecutionInfoCreatorBase.CheckCondition` evaluates the condition against
   the current battle state.
2. Previous deviation: Guest-generated private condition results were forwarded
   unchanged to the Host receiver, so Host could not be the sole evaluator for
   hand/deck/history-dependent effects.
3. Current restoration: on the Guest-to-Host server boundary, only
   `skillConditionCheck` entries are removed from the response copy before it
   enters the Host's native receiver. Request data, movement, validate, target,
   key-action, and `uList` data remain unchanged. Host execution therefore uses
   its authoritative card ledger and native condition code; its normal sender
   output remains the response delivered to Guest.
4. Required extension: the Host ledger still supplies private card identity and
   cumulative fields that the closed official server would have made available
   through `knownList/uList`; no custom post-VFX condition replay is added.
5. Verification: protocol tests include a condition-bearing request and verify
   that only the Host response copy loses `skillConditionCheck`, while the
   original request and native movement records are preserved. Main Release
   build, protocol tests, and `git diff --check` pass.
6. Compatibility manifests are also removed from Guest-to-Host requests at this
   boundary. They were only migration side-channels; allowing them through
   would reintroduce a second client-authoritative condition/random path beside
   the native `orderList/uList` semantics.

### Execution record: 2026-08-29 - Stage 2.3 canonical known-card identities

1. Native path verified: `NetworkBattleData.BeforeSettingReceiveData` creates
   `CardDataModel` entries from `knownList`, and `ReplaceReceivedCard` expects a
   unique owner/index identity when it searches the hand/deck.
2. Previous deviation: a grouped `idx`/`idxList` placeholder could be reused
   for one card by merely adding `cardId`, leaving the same index represented
   by both a group and a scalar record. That is the source of duplicate
   replacement matches and `Sequence contains more than one matching element`.
3. Current restoration: Host response construction now splits a requested index
   out of a grouped known-card entry into one scalar record, retaining the other
   indexes in the original group. Explicit native card fields are applied only
   to the scalar identity.
4. Required extension: this normalization occurs before perspective conversion
   and before the native receiver, because the original server would have
   emitted a single canonical identity at that boundary.
5. Verification: protocol tests cover grouped `knownList` placeholders, scalar
   identity creation, index retention, and all previous Host-state tests; main
   Release build and `git diff --check` pass.

### Execution record: 2026-08-29 - Stage 2 private-state continuation

1. Native path: an ordered local action is emitted after the native operation
   has registered its `orderList`/`uList`; the peer receives it through
   `NetworkBattleReceiver`, `NetworkBattleData.BeforeSettingReceiveData`,
   `ReplaceReceivedCard`, and `OperateReceive`.
2. Gap found: when `UseNativeClientActionTiming` was enabled,
   `HandleEmitNow` skipped `AppendLocalHiddenCardState` entirely. The native
   action itself still travelled through the original protocol, but later
   Host condition checks could observe stale private hand/deck card state.
3. Change made: Host-authority native ordered messages now reuse
   `AppendLocalHiddenCardState`. It scans the local private cards but serializes
   only changed signatures, so the extension is incremental. The data remains
   an auxiliary state carrier; it does not replace native `knownList`, `uList`,
   `orderList`, or the operation/VFX order.
4. Scope: this completes the 2.1.4 subtask (incremental private state at the
   native boundary). Full history-family coverage and Host-owned RNG remain
   explicitly open under 2.1.5, 2.2.3, and 2.4.1.
5. Verification: Release build, P2P protocol tests, and `git diff --check` pass;
   no game-side multiplayer test is claimed in this record.

### Execution record: 2026-08-29 - Stage 2 ledger ingestion and random-field normalization

1. Gap found: the native ordered message could carry p2pHiddenCards, but
   P2PAuthoritativeServer.ObserveClientRequest previously ignored that field.
   The receiver could update its card object while the Host ledger continued
   to serve the previous cost/Buff/attached-state value.
2. Change made: Host now merges incremental private-card state into its
   protocol ledger before processing knownList, uList, and orderList.
   Tombstones update the card zone to Unknown without deleting the identity,
   so a same-action native movement can still resolve the original card.
3. Random path: Host normalizes explicit randomTargetIdx values to the native
   list shape in uList/orderList. The compatibility Action Manifest and stale
   cached card state are not consulted; explicit native sender data always wins.
4. History path: Host-authority native ordered emits now also attach the
   existing revisioned p2pPlayerHistory state. It remains incremental and is
   applied through the existing native receive/post-action boundary.
5. Verification: Release build and P2P protocol tests pass. The new test
   covers private-card delta ingestion, reuse of the updated state in a later
   knownList, and canonical randomTargetIdx propagation. Full Host-owned
   random generation and exhaustive history-family coverage remain open under
   2.1.5, 2.2.3, and 2.4.1.

### Execution record: 2026-08-29 - Stage 2 pre-action history boundary

1. Native path verified: NetworkBattleReceiver.ReceivedMessage prepares the
   receive data before NetworkOperationCollection constructs the action.
   Original server condition registrations therefore refer to the state before
   the action, while post-action history is available only after the native
   operation completes.
2. Gap found: p2pPlayerHistoryBefore was recorded but never applied before
   native operation construction. Host could evaluate a Guest action against a
   stale or post-action history copy.
3. Change made: incoming ordered messages now apply the pending pre-action
   history immediately after it is received and before native operation
   construction. Post-action history remains revisioned and deferred until the
   native boundary completes.
4. Native receiver coverage: hidden-card promotion and fusion metamorphose
   identity normalization now run for both Host and Guest receivers, converting
   absolute ownership to the current receiver's native isSelf convention.
5. Random safety: Host no longer fills a missing randomTargetIdx from an old
   cached card state. Only explicit native uList/orderList values are
   normalized, avoiding stale random results.
6. Verification: Release build, P2P protocol tests, and git diff --check pass.

### Execution record: 2026-08-29 - Stage 2 native receiver coverage

1. Native path verified: `NetworkBattleData.BeforeSettingReceiveData` consumes
   `knownList` and `uList` before `OperateReceive.StartOperate`. Any generated
   or drawn private card must therefore have a canonical identity at this
   boundary, even if the card was described only by an auxiliary private-state
   delta.
2. Change made: Host response construction now promotes identities found in
   `p2pHiddenCards` or `p2pAuthorityHiddenStates` into the native `knownList`
   before perspective conversion. Duplicate grouped indexes are still split
   into scalar entries.
3. Receiver coverage: Host and Guest both convert absolute owner values to the
   current receiver's native `isSelf` convention for hidden-card promotion and
   fusion-metamorphose pre-action identity handling.
4. Condition timing: the pre-action history state is applied from the existing
   `NetworkBattleData.BeforeSettingReceiveData` postfix, after native card
   replacement and before operation construction. This preserves original
   object identity while keeping post-action history deferred.
5. Validation: `randomTargetIdx` is never synthesized from stale Host cache;
   only explicit native `uList`/`orderList` values are normalized. Host-owned
   RNG generation/verification remains an open subtask.
6. Verification: Release build, P2P protocol tests, and `git diff --check` pass.

### Execution record: 2026-08-29 - Stage 2 persistent condition history coverage

1. Original condition inputs verified: `SkillInfoLastTargets` reads
   `BattlePlayerBase.LastTargetCardsList`; destroyed-card and Choice Brave
   filters read `DestroyedWhenDestroyCards`, `ChoiceBraveCardList`, and
   `ChoiceBraveCards`. These are persistent condition inputs even though they
   are not hand/deck zones.
2. Previous deviation: the synchronization policy treated `LastTargetCardsList`
   as temporary and omitted the destroyed/Choice Brave backing lists. A later
   action could therefore evaluate a private condition against an empty or old
   history list on the receiving peer.
3. Change made: these lists are now included in the revisioned player-history
   payload. The existing typed reconstruction handles nested card lists and
   resolves references before replacing a native list, so partial revisions do
   not corrupt battle state.
4. Host ledger coverage: revisioned `p2pPlayerHistory` states are retained in
   `P2PAuthoritativeServer` in addition to the runtime receiver cache. This
   gives the Host one protocol-level baseline for later condition/RNG work.
5. Verification: tests assert the persistent lists are synchronized while
   action-only scratch lists remain excluded; Release build, protocol tests, and
   `git diff --check` pass.

### Execution record: 2026-08-29 - Stage 2 closure

The stage-2 matrix is closed by the changes recorded below; this section is
the authoritative status because the earlier partial entries predate the
completion pass.

1. 2.1.5 / 2.2.3 condition inputs: the Host history boundary now carries
   every BattlePlayerBase scalar used by the native turn/condition path,
   including Turn and IsSelfTurn, in addition to the persistent card,
   draw, discard, destroy, evolve, fusion, resonance, shortage, and deck
   history lists already enumerated by P2PPlayerHistoryPolicy. Temporary
   operation work lists remain excluded so the native SkillConditionChecker
   continues to construct them from the current action.
2. 2.4.1 / 2.4.1.2 random results: random selection is generated by the
   Host's native NetworkOperationCollection execution (the same runtime path
   as the official server), then validated at the server response boundary.
   The validator canonicalizes scalar/list forms, rejects invalid negative
   values, records the native result for diagnostics, and never reads the old
   Action Manifest or a client-side replacement result. A separate RNG would
   violate the original RegisterUnapproved -> uList -> RandomTargetIndex
   semantics, so Host-native generation is the intentional implementation.
3. Native order is unchanged: Host output still enters
   NetworkBattleReceiver.ReceivedMessage, NetworkBattleData replacement,
   and OperateReceive; the new history and random fields are boundary data,
   not post-VFX state replacement.
4. Verification completed: Release build, P2P protocol tests, and
   git diff --check pass. The protocol tests cover the new scalar coverage
   contract and Host random-result audit/rejection path. In-game two-client
   validation remains a separate acceptance step and is not claimed by these
   automated tests.

### Stage 2 status override (authoritative)

- [x] 2.1.5 原版历史与融合累计字段
- [x] 2.2.3 Host 条件输入完整性
- [x] 2.4.1 原版随机结果传递
- [x] 2.4.1.2 Host 原生生成/验证 RNG

这些勾选表示代码实现和协议测试已完成；真实双端游戏验收仍按必测矩阵执行，
不将自动化测试结果冒充为联机实测结果。
