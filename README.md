# Shadowbus

**简体中文** | [English](README.en.md)

Shadowverse 国际服的单机化与卡牌 Mod 工具，基于 BepInEx 6 开发。

本仓库同时提供可部署到 GitHub Pages 的[一体化 Web 配置编辑器](WebEditor/README.md)，
支持用中文表单编辑 AIData、BossRush、CardMaster、Format 和 TwoPick 配置，并可直接
读写本地 Mods 目录或导出完整 ZIP。

## 功能

- 单机模式：支持主界面、无限制卡组编辑、CPU 对战和开包动画。
- 默认解锁全部卡牌、主战者皮肤、卡背与主界面背景，并在本地保存背景选择。
- 无限制卡组：忽略职业、卡牌数量和卡组张数限制，也可以加入衍生卡。
- 自定义练习：可指定对手卡组、职业、主战者和 AI CSV。
- 卡牌 Mod：修改或新增卡牌，并支持自定义卡图与文本。
- 卡组列表热重载：进入卡组列表时重新加载 `CardMaster` 配置。
- 主动技能：使用 `when_activate` 为场上随从添加“启动”按钮，可设置 PP 消费。
- 自定义技能：复制卡牌信息与能力，或在保留自身能力的同时获得目标能力。
- Socket.IO 房间对战：房主启动内嵌 Socket.IO 节点并生成加密连接码，另一名玩家粘贴连接码后通过原版实时网络加入房间。

目前仅支持无限制卡组，AI 强化仍在开发中。

## 安装

### 使用成品包

从[百度网盘](https://pan.baidu.com/s/1iNJ7HMVR2cbV1aKvLzI2AA?pwd=7ejh)下载 BepInEx 和插件压缩包，解压到 Shadowverse 游戏根目录。

### 手动安装

1. 安装 BepInEx 6 Mono 32 位版本。
2. 将 `Shadowbus.dll` 和 `Newtonsoft.Json.dll` 放入 `BepInEx/plugins/`。
3. 将项目中的 `Mods` 文件夹复制到游戏根目录。

安装后的主要目录如下：

```text
Shadowverse/
├─ BepInEx/plugins/Shadowbus.dll
└─ Mods/
   ├─ AIData/
   │  ├─ deck/
   │  ├─ style/
   │  └─ emote/
   ├─ UnlimitedDecks/
   ├─ CardMaster/
   └─ CardImages/
```

## 联机对战

联机对战功能正在开发中。实时传输已从自定义 TCP 切换为原版兼容的 Socket.IO over WebSocket；创建/加入房间仍由本地任务路由适配官方 HTTP API。

## 增强日志

Shadowbus 会在 BepInEx 的全局日志分发边界统一格式化日志，因此普通模块、Unity/BepInEx 转发日志和联机日志都会使用同一套格式。BepInEx 原有的 `[Level:Source]` 前缀只保留一次；等级颜色只在外部控制台输出边界应用，文本日志不会出现 ANSI 控制符。过长内容会按配置换行，并为前缀预留宽度。

增强日志默认另存为 `BepInEx/LogOutput-enhanced.log`，不会替换原始日志文件。卡牌 ID 如果能在当前 `CardMaster` 中解析，会附加卡名和费用；卡牌效果文本默认关闭，以避免普通日志过长。

配置文件中的 `[Logging]` 段：

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `EnhancedEnabled` | `true` | 启用全局增强格式和增强日志文件 |
| `ShowCardDetails` | `true` | 将卡牌 ID 附加为“卡名 + 费用” |
| `ShowCardEffects` | `false` | 附加卡牌效果和进化效果文本 |
| `DetailedOnline` | `false` | 记录联机对战的详细消息；卡牌效果是否显示仍由 `ShowCardEffects` 控制 |
| `AnsiColors` | `true` | 在 BepInEx 外部控制台中按日志等级使用真实控制台颜色；`Player.log` 和增强日志文件始终保持纯文本 |
| `WrapWidth` | `140` | 单行最大字符数，设置为 `0` 关闭自动换行 |
| `EnhancedFile` | `BepInEx/LogOutput-enhanced.log` | 增强日志路径；相对路径以游戏根目录为基准 |

`DetailedOnline=true` 适合复现联机问题；常规游玩建议关闭，以减少日志量和卡牌文本查询开销。

## 自定义练习

进入“单人 > 对战”，在对手职业选择页点击第九个“自定义卡组”图标。配置页可同时选择：

- 对手的本地无限制卡组、职业和已拥有主战者。
- 原作 AI 预设、逻辑等级和生命上限。
- 自定义 Deck、Style、Emote CSV。

自定义 CSV 分别放入 `Mods/AIData/deck/`、`style/` 和 `emote/`。文件需要保持原作对应 CSV 的列格式；配置项留为“使用原作预设”时，会使用当前职业所选预设的原始 AI 数据。配置页打开后新增文件，可点击“刷新 CSV”重新扫描。

### AI 主战者语音

支持自定义主战者语音，`Mods/AIData/emote/ai_emote_sample.csv` 是一份可直接使用的现成文件。皮肤实际拥有哪些语音序号见 [Mods/AIData/README.md](Mods/AIData/README.md)，完整语法见 [Docs/AI_CSV_Guide.md](Docs/AI_CSV_Guide.md)。

### AI 行为配置

`BepInEx/config/` 下插件配置文件的 `[AI]` 段，控制 AI 如何处理原作 AI 数据没有描述的卡牌：

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `StallTimeoutSeconds` | `30` | AI 无进展多少秒后强制结束回合，`0` 关闭 |
| `UnknownCardPlayBonusMin` | `0.5` | 无 AI 数据卡牌的最低出牌加分 |
| `UnknownCardPlayBonusMax` | `1.5` | 最高出牌加分，两项都填 `0` 则只保留防崩溃 |
| `PriceUnpricedCards` | `true` | 是否按标签为「有模拟标签、无计分标签」的法术和护符折价 |
| `RespectPlayLimitLocks` | `false` | 开启后，原作用 `playLimit` 锁定的卡牌跳过定价 |
| `LowLifeHealThreshold` | `10` | 主战者回复只在该生命值及以下计分 |

## 卡牌 Mod

卡牌补丁位于 `Mods/CardMaster/`：

- `.json` 文件会被加载，`.example` 文件仅作为示例。
- `newCard` 为 `false` 时修改 `templateCardId` 对应的卡牌。
- `newCard` 为 `true` 时以 `templateCardId` 为模板创建 `cardId` 对应的新卡。
- `intFields` 修改数值字段。
- `intArrayFields` 修改整数或枚举数组字段；卡牌类型使用 `"Tribe": [类型枚举值]`。
- `stringChangeFields` 替换技能等字符串字段。
- `stringAppendFields` 在原字符串后追加内容。
- `localizationFields` 修改卡名、能力文本和背景文本。

修改配置后进入卡组列表即可热重载。新增卡牌应使用未占用的卡牌 ID；卡图放在 `Mods/CardImages/`，并通过 `ResourceCardId` 引用。

- 进化前卡图命名为 `<ResourceCardId>.png`。
- 进化后卡图命名为 `<ResourceCardId>_evo.png`；未提供时自动使用进化前卡图。
- 修改已有卡牌时，补丁会同步应用到其普通版和闪卡版，同时保留两个版本各自的身份字段。
- `stringArrayFields` 可用于替换 `SkillEffectPath`、`SkillSe`、`EvolEffectPath` 等 `string[]` 字段。
- `foilEffectCardId` 可为闪卡指定原版卡牌的动态材质效果。可填写该来源卡的普通或闪卡 `CardId`；仅目标记录为 `IsFoil=true` 时生效，卡图仍使用目标卡自己的 `ResourceCardId` 和 PNG。来源与目标必须同为随从或同为法术/护符，且来源必须是原版已有卡。若同时制作普通版和闪卡版，为了让普通版不误用闪卡材质，两者必须使用不同的 `ResourceCardId`；共享资源 ID 时该覆盖会被禁用。只有单独制作一条闪卡记录时可以继续使用它自己的 `NormalCardId`。
- 游戏内导出卡牌数据时会把卡牌类型写入 `intArrayFields.Tribe`；例如士兵为 `[2]`、机械为 `[7]`。

项目已提供以下扩展：

| 关键词 | 用途 |
| --- | --- |
| `when_activate` | 为己方场上随从提供主动发动时点；在 `SkillPreprocess` 中使用 `use_pp=N` 设置 PP 消费。 |
| `skill_geminize` | 复制目标随从的名称、类型、身材、文本与全部技能，并清除自身除该技能外的技能。 |
| `skill_acquire_skills` | 获得目标随从的全部技能与非身材 Buff，并保留自身技能；不会复制同类型技能或攻击力、生命值修正。 |
| `skill_mirror` | 成为法术或单体能力的指定目标时，使对应效果对使用者的随机随从再生效一次。 |

`skill_mirror` 支持 `all=true/false`、`include_self=true/false` 和 `ability=true/false`。前两项控制追加效果应用于随机一个或全部随从，以及镜像目标自身能否成为追加目标；`ability=true` 时，除法术外，其他明确指定本随从的单体能力也能触发，随机和群体效果不会触发。默认值依次为 `false`、`true` 和 `false`。

具体配置可参考 `Mods/CardMaster/` 下的示例和现有卡牌文件。更多技能时点说明见 [Mods/readme.md](Mods/readme.md)。

### CardMaster 攻击特效

`attackEffectFields` 可设置卡牌攻击时的普通/进化两套演出数据。字段值均为 `[普通, 进化]`：`effectPath`（特效路径）、`se`（音效路径）、`moveType`（移动类型）、`effectEnginType`（引擎类型 `NONE`/`SHURIKEN`/`FLATOUT`/`SOLID`）和 `time`（时长）。字段留空时保留模板卡牌原值。

```json
"attackEffectFields": {
  "effectPath": ["btl_attack_1", "btl_attack_2"],
  "se": ["se_btl_attack_1", "se_btl_attack_2"],
  "moveType": ["DIRECT", "DIRECT"],
  "effectEnginType": ["SHURIKEN", "SHURIKEN"],
  "time": [0.5, 0.5]
}
```

## 构建

```powershell
dotnet build Shadowbus.sln
```

构建产物位于 `bin/Debug/net46/Shadowbus.dll`。项目需要引用游戏的 `Assembly-CSharp.dll`；如果游戏安装位置不同，请调整 `Shadowbus.csproj` 中的 `HintPath`。

## 注意事项

- 本项目面向单机和 Mod 测试用途。
- 修改卡牌配置前建议保留备份。
- 正在进行的对局不会自动重建已生成的卡牌，测试配置时建议开始新对局。
