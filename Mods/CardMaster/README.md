# CardMaster ID 设计指南

这份文档说明 CardMaster 中各类 ID 的含义，以及制作 `newCard` 时如何选择 ID。

## 一、先区分三种 ID

一张卡通常同时有三层身份：

```text
CardId           = CardMaster 字典中的一条具体记录
BaseCardId       = 普通版和闪卡版共同代表的逻辑卡牌
ResourceCardId   = 卡图、材质和资源文件使用的资源键
```

因此，`CardId`、`BaseCardId` 和 `ResourceCardId` 不应该默认认为是同一个数字。

## 二、字段说明

| 字段 | 作用 | 制作新卡时的建议 |
| --- | --- | --- |
| `cardId` | 这条 `CardParameter` 在 CardMaster 字典中的唯一键。游戏通过它取卡牌记录。 | 必须全局未占用；普通版和闪卡版使用不同的 `cardId`。 |
| `templateCardId` | Shadowbus 补丁使用的模板 ID。`newCard=true` 时，程序先克隆这张卡，再应用补丁。 | 选择类型、技能结构和资源格式接近的官方卡；它不是新卡最终 ID。 |
| `BaseCardId` | 卡牌的逻辑基础身份。许多“同名卡/同基础卡/进化前后/复制和再生”判断会使用它。 | 普通版和闪卡版通常都填写普通版的 `cardId`。不要把它随意改成资源 ID。 |
| `NormalCardId` | 指向普通版本的 `CardParameter` 记录。 | 普通版和闪卡版都指向普通版 `cardId`。 |
| `FoilCardId` | 指向闪卡版本的 `CardParameter` 记录。游戏转换闪卡、闪卡抽取和部分复制逻辑都会读取它。 | 如果有独立闪卡，必须指向真实存在的闪卡 `cardId`；不能只填写一个不存在的数字。 |
| `ResourceCardId` | 卡牌材质、卡图和战斗资源的查找键。它可以与 `cardId` 不同。 | 普通版和闪卡版共用卡图时可相同；需要不同卡图时分别填写两个资源 ID。 |
| `IsFoil` | 当前这条记录是否是闪卡。它决定闪卡标记以及 `VariantCardShader` 的使用。 | 普通记录为 `false`，闪卡记录为 `true`。它不会自动创建另一条卡牌记录。 |
| `CardHashId` | 官方卡组码/二维码等外部编码使用的字符串键。 | 不要复制官方卡的值；应使用独立字符串。当前运行时新增卡不会自动加入原版 hash 索引，二维码支持仍有限制。 |
| `CardSetId` | 卡包、轮换和部分卡牌资格判断使用的集合 ID。 | 按需要设置；它不是卡牌唯一身份。 |
| `TwoPickFoilCardId` | 双选等特殊系统使用的闪卡替代 ID。 | 普通 DIY 卡一般不需要设置。 |

### 为闪卡指定原版动态效果

`foilEffectCardId` 是 Shadowbus 补丁字段，不是 `CardParameter` 的 `intFields` 属性。它让目标闪卡复用另一张**原版已有卡**的闪卡材质参数与动态效果，同时仍显示目标自己的卡图。

```json
{
  "newCard": true,
  "cardId": 999991001,
  "templateCardId": 100011011,
  "foilEffectCardId": 100011010,
  "boolFields": {
    "IsFoil": true
  },
  "intFields": {
    "BaseCardId": 999991000,
    "NormalCardId": 999991000,
    "FoilCardId": 999991001,
    "ResourceCardId": 999991001
  }
}
```

- 来源可以填写其普通版或闪卡版 `CardId`；运行时会自动解析该卡的真实闪卡版本。
- 只有最终 `IsFoil=true` 的记录会使用该字段；普通版不会显示动态闪卡效果。
- 普通状态复用来源的普通闪卡材质，进化状态复用同一来源的进化闪卡材质；目标卡仍读取 `<ResourceCardId>.png` 和 `<ResourceCardId>_evo.png`。
- 来源与目标必须同为随从，或同为法术/护符；不能引用自制卡，也不会自动下载缺少的原版资源包。
- 这个示例把闪卡的 `ResourceCardId` 单独设为 `999991001`；如果同时制作普通版，普通版必须使用另一个资源 ID，才能保证动态效果只出现在闪卡记录上。若只制作一条独立闪卡记录，可让它的 `NormalCardId` 继续指向自身。
- 若多个不同效果来源绑定到同一个 `ResourceCardId`，插件会禁用该资源 ID 的效果覆盖并输出警告。需要不同效果时，为各闪卡使用不同的资源 ID。

### 为卡牌指定本地语音

`voiceFiles` 是 Shadowbus 的 CardMaster 补丁字段。音频文件放在游戏目录的 `Mods/CardVoices/` 下，配置中填写相对于该目录的路径：

```json
{
  "newCard": true,
  "cardId": 999991010,
  "templateCardId": 100011010,
  "voiceFiles": {
    "play": "my_card/play.mp3",
    "evolve": "my_card/evolve.wav",
    "attack": "my_card/attack.ogg",
    "evolvedAttack": "my_card/attack_evolved.ogg",
    "destroy": "my_card/destroy.mp3",
    "evolvedDestroy": "my_card/destroy_evolved.mp3",
    "skills": ["my_card/skill_1.mp3", ""],
    "evolvedSkills": ["my_card/skill_1_evolved.mp3", ""]
  }
}
```

| 字段 | 对应语音 |
| --- | --- |
| `play` | 卡牌打出或随从登场语音，对应 `PlayVoice`。 |
| `evolve` | 进化语音，对应 `EvoVoice`。 |
| `attack` / `evolvedAttack` | 进化前/后的攻击语音，共同写入 `AtkVoice` 的两种形态。 |
| `destroy` / `evolvedDestroy` | 进化前/后的破坏语音，共同写入 `DestroyVoice` 的两种形态。 |
| `skills` / `evolvedSkills` | 进化前/后的技能语音列表，共同写入 `SkillVoice`。数组下标必须与卡牌的技能下标对齐；不需要语音的位置填写空字符串。 |

- 支持 `.wav`、`.mp3`、`.ogg`、`.aif` 和 `.aiff`；FLAC、AAC 等格式需要先转换。
- 可以使用子目录，但不能使用绝对路径或 `..` 离开 `CardVoices` 目录。
- 省略某个字段时保留 `stringChangeFields` 修改后的值或模板卡原值；本地语音与这些保留的原版语音可以混用。把字段写成空字符串，或把技能列表写成空数组，则清除对应语音。
- `voiceFiles` 在普通字符串补丁之后应用，因此同一条补丁中它优先于 `stringChangeFields.PlayVoice/EvoVoice/AtkVoice/DestroyVoice/SkillVoice`。
- 修改已有卡牌时，本地语音与其他 CardMaster 字段一样同步应用到普通版和闪卡版。新增的普通版和闪卡版是两条独立记录时，应在各自条目中配置 `voiceFiles`。
- 游戏会在 CardMaster 热重载后预加载音频。卡牌详情页的语音试听、战斗中的出场/进化/攻击/破坏/技能触发都会使用本地文件，并跟随游戏的语音音量和静音设置。
- 文件不存在、格式不支持或解码失败时，对应语音保持静音，并在 BepInEx 日志中输出一次 `[CardVoice]` 警告。

## 三、官方普通版/闪卡版关系

官方数据通常有两条记录。假设普通版是 `A`，闪卡版是 `B`：

```text
普通版记录：
CardId       = A
IsFoil       = false
BaseCardId   = A
NormalCardId = A
FoilCardId   = B

闪卡记录：
CardId       = B
IsFoil       = true
BaseCardId   = A
NormalCardId = A
FoilCardId   = B
```

`FoilCardId` 只是“指针”，不会凭空生成 `B`。`B` 必须在 CardMaster 中有对应记录。

当前 Shadowbus 的 `newCard` 逻辑也只会新增一条记录；没有额外定义闪卡记录时，不会自动生成真正的闪卡版本。未填写 `BaseCardId/NormalCardId/FoilCardId` 时，new card 会按当前补丁逻辑使用新卡自身 ID 作为默认值。

## 四、推荐的自制卡 ID 方案

建议使用项目约定的自制卡区间：

```text
999990000 及以上
```

选择 ID 时遵守以下规则：

1. 在所有 CardMaster 数据中搜索，确认普通版和闪卡版都没有冲突。
2. 普通版和闪卡版可以使用相邻 ID，例如 `999991000` 和 `999991001`，但必须显式填写关系字段，不要只依赖“末位加一”的猜测。
3. 不要使用小于 `100` 的 ID；原版会把它们识别为职业/主战者卡。
4. 避免使用原版特殊区间，例如 `910xxxxxx`、`930xxxxxx` 以及其他已经被游戏特殊逻辑占用的区间。
5. 不要复用官方 `CardHashId`、`ResourceCardId` 或已存在的 CardId。

## 五、普通版 + 闪卡版示例

下面示例创建一张普通版 `999991000` 和一张闪卡版 `999991001`。两者共用同一张卡图，但闪卡使用闪卡 Shader：

```json
[
  {
    "newCard": true,
    "cardId": 999991000,
    "templateCardId": 100011010,
    "boolFields": {
      "IsFoil": false
    },
    "intFields": {
      "BaseCardId": 999991000,
      "NormalCardId": 999991000,
      "FoilCardId": 999991001,
      "ResourceCardId": 999991000
    },
    "localizationFields": {
      "CardName": "我的自制卡"
    }
  },
  {
    "newCard": true,
    "cardId": 999991001,
    "templateCardId": 100011011,
    "boolFields": {
      "IsFoil": true
    },
    "intFields": {
      "BaseCardId": 999991000,
      "NormalCardId": 999991000,
      "FoilCardId": 999991001,
      "ResourceCardId": 999991000
    },
    "localizationFields": {
      "CardName": "我的自制卡"
    }
  }
]
```

如果闪卡需要独立卡图，把第二条记录的 `ResourceCardId` 改为另一个资源键，例如 `999991001`，并放置：

```text
Mods/CardImages/999991000.png
Mods/CardImages/999991000_evo.png
Mods/CardImages/999991001.png
Mods/CardImages/999991001_evo.png
```

如果没有 `_evo.png`，进化状态会回退使用普通卡图。

## 六、只有普通版、不制作独立闪卡

CardMaster 的数据结构默认每条卡都有 `FoilCardId`。如果不制作独立闪卡，可以让它自指，但不要把它误认为真正的闪卡：

```json
{
  "newCard": true,
  "cardId": 999991010,
  "templateCardId": 100011010,
  "boolFields": {
    "IsFoil": false
  },
  "intFields": {
    "BaseCardId": 999991010,
    "NormalCardId": 999991010,
    "FoilCardId": 999991010,
    "ResourceCardId": 999991010
  }
}
```

这种写法可能仍让原版 UI 显示“闪卡/转换”入口，因为原版只看到 `FoilCardId` 有数值；但它不会产生独立的闪卡 `CardParameter`，也不会因为这个字段自动启用闪卡 Shader。

不要把 `FoilCardId` 设为 `0`。原版会把小于 `100` 的 ID 当作职业卡处理，容易产生错误。

## 七、如何解析 CardId 对应的卡名

Shadowbus 会在游戏加载 CardMaster 后导出：

```text
Mods/CardMaster/Reference/card_names.csv
```

其中包含：

```text
card_id,card_name,clan,char_type,cost,atk,life,base_card_id,...
```

制作 Mod 时可以按 `card_id` 搜索这个文件确认卡名、类型和基础 ID。例如 PowerShell：

```powershell
rg "^999991000," Mods/CardMaster/Reference/card_names.csv
```

WebEditor 也会使用内置卡表解析官方 CardId；打开当前 CardMaster 文件时，自制卡会按 `localizationFields.CardName` 显示。对于刚新增的卡，先让游戏加载一次并重新导出 Reference，再用导出的 CSV 核对最终关系。

## 八、排查清单

遇到卡图、闪卡或卡牌复制异常时，按以下顺序检查：

1. `cardId` 是否和其他卡冲突。
2. `NormalCardId` 和 `FoilCardId` 是否都指向真实存在的记录。
3. 闪卡记录是否真的设置了 `IsFoil=true`。
4. 普通版和闪卡版的 `BaseCardId` 是否一致。
5. `ResourceCardId` 是否与 `Mods/CardImages` 文件名一致。
6. 进化图是否使用 `<ResourceCardId>_evo.png` 命名。
7. 是否误把 `templateCardId` 当成了新卡的最终 `CardId`。
8. 是否复制了官方 `CardHashId`，导致外部编码冲突。

## 九、CardId 的常见数字规则（非强制约定）

下面这些规律来自反编译代码和官方 CardMaster 数据，**不是完整的官方公开规范**。游戏真正依赖的是字段关系和特殊判断函数；不要因为某个数字看起来符合规律，就省略 `BaseCardId/NormalCardId/FoilCardId` 等字段。

### 1. 末位 `0/1`：普通版和闪卡版

官方卡牌最常见的形式是：

```text
普通版：xxxxxxxx0
闪卡版：xxxxxxxx1
```

例如：

```text
100011010 -> 100011011
120341020 -> 120341021
```

WebEditor 的辅助解析也把“末位为 `1`”作为常见闪卡形式，解析为前一个 ID。不过这只是便利规则；真正可靠的判断仍然是：

```text
IsFoil
NormalCardId
FoilCardId
```

不要因为某个 ID 末位是 `1`，就直接假设它一定是闪卡。

### 2. 前三位：通常代表卡牌家族、版本或内部来源

官方数据中可以观察到以下常见段：

| 前缀 | 常见含义 | 备注 |
| --- | --- | --- |
| `100`～`132` | 普通卡池、扩展包中的主体卡牌 | 通常与 `CardSetId`、卡包批次一起变化，不代表职业。 |
| `7xx` | 重印、复刻或特殊版本 | 原版 `CardParameter.IsReprintedCard` 会把 `7xxxxxxxx` 判断为重印卡。 |
| `8xx` | 特殊卡、转换卡、部分内部衍生卡 | 不能只凭 `8` 判断具体类型，必须查看原始记录。 |
| `9xx` | Token、特殊生成卡或系统卡 | 详情页的 `IsTokenId` 会把 `9xxxxxxxx` 视为 Token 区域。普通自制卡不建议使用。 |

前三位更像“分配区间”而不是一个简单的职业编号。比如 `100`、`120` 并不分别代表某个职业。

### 3. 特殊前缀是游戏逻辑判断，不是普通卡 ID

以下前缀会被原版代码直接识别，制作普通 DIY 卡时应避开：

| 区间 | 原版判断 | 风险 |
| --- | --- | --- |
| `< 100` | `CardMaster.IsClass()` 判断为职业/主战者卡 | 普通卡会被当成职业卡。 |
| `7xxxxxxxx` | `IsReprintedCard` 为真 | 会进入重印卡相关逻辑。 |
| `9xxxxxxxx` | 详情页 `IsTokenId` 为真 | 可能被当作 Token，持有数量、详情和生成逻辑不同。 |
| `910xxxxxx` | `IsEvolveChoiceCard` / `IsChoiceEvolutionCard` 为真 | 会进入选择进化卡逻辑。 |
| `930xxxxxx` | `IsChoiceBraveCardCheck` 为真 | 会进入 Choice Brave/英雄技能卡逻辑。 |
| `800xxxxxx` 的部分范围 | `IsMutationCardCheck()` 可能命中 | 会改变法术/随从资源类型和材质路径。 |

另外，原版资源加载对超过十亿的资源 ID 有特殊处理。除非你明确知道自己在制作特殊资源卡，否则不要使用十位数且以 `1` 开头的资源 ID。

### 4. 中间位和末三位没有稳定的公开含义

官方 ID 中经常可以看到类似：

```text
100011010
101021020
120341030
```

这些数字可能体现卡包批次、卡牌序号、职业/来源分组和内部资源版本，但不同卡池、Token、重印卡和特殊卡的编码方式并不完全一致。尤其不能把：

```text
第 4 位 = 职业
末三位 = 卡牌类型
```

当成通用公式。制作 Mod 时，应以实际的 `CardParameter` 字段为准，而不是自行拆分数字推断。

### 5. 推荐的非强制分配方式

为了让自制卡一眼可识别，可以使用自己的连续区间，例如：

```text
普通卡：999991000、999991010、999991020 ...
闪卡：  999991001、999991011、999991021 ...
```

这种分配方式的好处是便于人工阅读，但它只是 Mod 作者自己的命名约定。最终仍要显式填写：

```text
BaseCardId   = 普通版 CardId
NormalCardId = 普通版 CardId
FoilCardId   = 闪卡 CardId
```

并确认每个被引用的 `CardId` 都确实在 CardMaster 中存在。
