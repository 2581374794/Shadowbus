# Shadowbus 卡牌 Mod 教程

适用版本 2.5.6。同目录的 `参考模板.example` 里有五张卡。第一张最小可用，照抄就能跑；第二张几乎把能写的字段全写上了，当字典翻；后三张分别是咒术、魔法阵、倒计时魔法阵。文里的卡号、路径、特效名都是占位符，抄走换成你自己的就行。

2.5.6 新增的是「借特效」：`effectBorrowCardId` / `summonEffectCardId` / `evolveEffectCardId` / `attackEffectCardId` / `skillEffectCardId` / `destroyEffectCardId`，用法和借卡图（`normalArtCardId`）一样 —— 见第 7 章「借原版卡的特效」。

## ⬤ 1. 一个文件夹一张卡

2.5.5 起，卡片改成「一个文件夹装一张卡」。`Mods/CardMaster/` 下每个含 `.json` 的一级子文件夹就是一张卡，只扫一层，不递归。

```text
Mods/CardMaster/
├─ 示例自定义卡牌/
│  ├─ 示例自定义卡牌.json     卡的数据，文件名随意，只要是 .json
│  ├─ card.png            进化前卡图
│  ├─ card_evo.png        进化后卡图
│  ├─ play.wav            登场语音
│  └─ ...
├─ 我的第二张卡/
└─ 参考卡/                参考用的 .example，不会被加载
```

卡的 json、卡图、语音全放它自己的文件夹里，插件自己认，文件名基本随便起 —— 哪个字段负责声明，下面各章会说。这套结构是 2.5.5 才定下来的：更早的时候图在一个目录、音在一个目录、json 又在另一个地方，改一张卡得跑三处对齐。下面几条是容易踩坑的地方：

- 资源只在卡自己的子文件夹里找，没有全局资源目录这回事。`Mods/CardImages/` 和 `Mods/CardVoices/` 在 2.5.5 已经彻底移除，不再创建、不再读取。
- 一个子文件夹可以放**多个 json、多张卡**。每个 json 顶层是数组，数组里每个对象就是一张卡，写多少个都会加载。所以「同一批卡放同一个文件夹、共用文件夹里的图音」这种用法完全成立。
- 但**同一张新卡别拆到两个 json 里**。每个对象都是一张独立的卡，第二份会撞上「卡号已被占用」被跳过，拆开写不会合并成一张。
- 卡图和语音可以放在卡文件夹的**子目录**里，只要在 `imageFiles` / `voiceFiles` 里写成相对路径（例如 `"normal": "图/card.png"`、`"play": "音频/登场.wav"`）。但约定名（`card.png`、`play.wav` 这些）**只认卡文件夹根目录**，塞进子目录就不认了。
- 同一个文件夹里放多张卡时，约定名 `card.png` 会让这些卡**都指到同一张图**。要各用各的图，就用 `imageFiles` 分别声明，或者用 `<资源卡号>.png` 命名。
- 单个 json 裸放在 `Mods/CardMaster/` 根目录也能被识别，但它没有自己的文件夹，所以用不了本地卡图与本地语音。要图要音，就放进子文件夹。
- 资料目录里别放 `.json`，否则它也会被当成一张卡。参考用的 json 以 `.example` 结尾就安全。
- 保存后进一次游戏里的「卡组列表」即热重载，不用重启。但已经打开的对局不会重建已生成的卡牌，测试请开新对局。

## ⬤ 2. 新人教程:五分钟做一张最小可用卡

```jsonc
[
  { "newCard": true, "cardId": 999991000, "templateCardId": 100011010,   // 建新卡，卡号不能已被占用
    "intFields": { "Cost": 3, "Atk": 2, "Life": 3, "EvoAtk": 4, "EvoLife": 5,
                   "ResourceCardId": 999991000 },   // 最后这项决定用哪个卡图文件
    "localizationFields": { "CardName": "我的第一张卡（最小可用卡）" } }
]
```

刚上手容易迷糊的地方有几处，这里提一下：

- 你只写普通版就够了。游戏里每张卡都是「普通版 + 闪卡版」两条记录，你只需要给普通版写一份，插件会照着补出闪卡版那份，数值、技能、文本、语音都一样。所以卡号要记得把 `卡号 + 1` 空出来，闪卡要占。
- 模板卡只提供底子，不是最终卡号。`templateCardId` 挑的是类型和技能结构最接近的那张原版卡；新卡的卡号由 `cardId` 决定，两者没有关系。
- 图、音都可以先不给。不给就沿用模板卡的卡面和语音，先跑通再加素材。第 4 章讲怎么放自己的图，第 5 章讲语音。
- 改完要进一次「卡组列表」才生效，不用重启游戏。已经开着的对局不会重建卡牌，测试记得开新对局。

如果想要制作更复杂和更高级的卡牌，就往下继续阅读学习吧！

## ⬤ 3. 卡牌身份与闪卡自动派生

### ■ 三层 ID

同一个数字在插件里代表什么，取决于你写进哪个字段。写混了不一定报错，只是图、闪卡、卡号对不上。先把三层 ID 分开：

| ID | 含义 |
| --- | --- |
| `CardId` | CardMaster 字典里的一条记录，这张卡本身 |
| `BaseCardId` | 普通版与闪卡版共同代表的「同一张逻辑卡」 |
| `ResourceCardId` | 卡图、材质等资源文件的查找键，决定用哪张图 |

做新卡时建议写全：

```jsonc
"intFields": {
  "BaseCardId": 999991000, "NormalCardId": 999991000,      // 普通版
  "FoilCardId": 999991001, "ResourceCardId": 999991000     // 闪卡版 / 决定用哪个 PNG
}
```

卡号必须唯一，并且要避开原版已占用的区段：`< 100`（原版识别为职业或主战者卡）、`7xxxxxxxx`（重印卡）、`9xxxxxxxx`（Token）、`910xxxxxx`、`930xxxxxx`。另有 `CardHashId`：卡组码、二维码这类外部编码用的字符串键，通过 `stringChangeFields` 改，别照抄原版卡的值。

### ■ 推荐编号方案（九位格式）

怕撞号的话，可以照教程文件夹里的 `Cardid推荐规则.png` 来排。它把「谁做的、什么职业、什么类型、第几张、普通还是闪」都编进卡号里，多个人一起做卡也不会撞号。
这套编号只是推荐，不是强制。以前用 `99999xxxx` 做的卡照旧能用，插件不管你的前缀，只要求卡号唯一、`卡号 + 1` 空着。

### ■ `newCard`、`cardId`、`templateCardId`

| 写法 | 行为 |
| --- | --- |
| `newCard: true` | 先深拷贝 `templateCardId`，再套用补丁，最后以 `cardId` 登记为新记录 |
| `newCard: false` | 直接修改 `templateCardId` 那张卡本身，这时 `cardId` 填 `0`，普通版和闪卡版会一起变，各自的身份字段保留 |
| `templateCardId` | 只提供类型、技能结构和资源格式的底子，不是新卡最终的 ID。挑类型和技能最接近的原版卡 |

### ■ 导出原版卡当参考

想知道某张原版卡的数据是怎么写的，不用去翻文档，直接从游戏里导出来：

1. 进游戏，打开**卡组编辑**（牌组编辑界面，能看到卡池的那个）
2. 在卡池里找到你想参考的那张卡，**点它一下**打开卡牌详情
3. 详情面板里有一个按钮写着**「导出卡牌数据」**，点它
4. 文件会写到 `Mods/CardMaster/` 下，名字就是卡名，例如 `Mods/CardMaster/斩断肃清的剑闪.example`

导出来的是这张卡的完整参数（数值、技能、卡面槽位、语音字段、演出参数全都在），可以直接照着改成你自己的卡：把 `newCard` 改成 `true`、填一个新的 `cardId`，就是一个能跑的补丁。

几点说明：

- 导出的是 **`.example`**，这个后缀不会被加载，放在那儿不会变成一张卡，放心留着当字典。
- 导出的是**那张卡本身**（`newCard: false` + `cardId: 0`），所以直接拿它去跑等于"修改原版卡"。想复制成新卡记得改那两处。
- 同一个名字重复导出会直接覆盖，不会生成副本。
- 散装的 `.example` 放在 `Mods/CardMaster/` 根目录就行；但如果你把它改成 `.json` 放根目录，那张卡就没有自己的文件夹，用不了本地卡图和语音。

### ■ 闪卡自动派生

游戏里每张卡都是两条记录：普通版和闪卡版，带 `IsFoil` 的是闪卡那条。你只写普通版就够了。

| 项 | 规则 |
| --- | --- |
| 卡号 | 本体卡号加一（`999991000` → `999991001`） |
| 垫底 | 用模板卡自己的闪卡卡（`111641020` → `111641021`），材质和卡图来自原版闪卡 |
| 补丁 | 与本体完全相同的一份，数值、技能、文本、语音、演出逐字一致，只有 `IsFoil` 和克隆来源不同 |
| 关系字段 | `BaseCardId`、`NormalCardId`、`FoilCardId` 自动写好 |
| 旁路 | `extraVoiceIds`、`foilEffectCardId` 和三个卡面槽位会同时登记到闪卡版 |

生成成功时日志打印 `added foil companion 999991001 for new card 999991000 (cloned from <模板闪卡卡号>)`。这三种情况不会生成：补丁里自己写了 `"boolFields": { "IsFoil": true }`（视为你要手工配对，插件不插手）；`卡号 + 1` 已被占用（只出普通版，日志会说明）；同批次别的记录已经声明要用这个号（手写的优先，自动生成让位）。想让普通版和闪卡版用不同卡图或不同闪卡材质，就手工配两条，闪卡那条带上 `IsFoil: true`：

```jsonc
[ { "newCard": true, "cardId": 999991000, "templateCardId": 999000000, "boolFields": { "IsFoil": false },
    "intFields": { "BaseCardId": 999991000, "NormalCardId": 999991000, "FoilCardId": 999991001, "ResourceCardId": 999991000 } },
  { "newCard": true, "cardId": 999991001, "templateCardId": 999000001, "boolFields": { "IsFoil": true },  // 有这行，自动派生才让位
    "intFields": { "BaseCardId": 999991000, "NormalCardId": 999991000, "FoilCardId": 999991001, "ResourceCardId": 999991001 } } ]
```

## ⬤ 4. 卡图

图可以一张都不给，此时的卡面会自动采用模板卡的。想用自己的图，或者想借任意一张原版卡的图，再往下看。

### ■ 自带卡图 `imageFiles`

卡图放卡自己的文件夹里，文件名随意，在 json 里声明，路径相对于卡文件夹：

```jsonc
"imageFiles": {
  "normal":  "我的卡面.png",     // 进化前
  "evolved": "我的卡面_进化.png"  // 进化后，不写就沿用上面那张
}
```

查找顺序取第一个存在的文件（`<id>` 是本卡的 `ResourceCardId`）：进化前是 `imageFiles.normal` → `card.png` → `<id>.png`；进化后是 `imageFiles.evolved` → `card_evo.png` → `<id>_evo.png`，都没有就退回进化前那张。声明式（名字随便起，两个都写还能各有各的图）和约定式（放 `card.png` / `card_evo.png`，一个字都不用写）都能用。一个 `.png` 都不放也行，沿用模板卡的图。

`imageFiles` 的值是**相对卡文件夹**的路径，所以名字随便起，也可以带子目录：`"normal": "图/我的卡面.png"` 是合法的。约定名 `card.png` / `card_evo.png` 则**只认卡文件夹根目录**，放进子目录就不认了。同一个文件夹里放多张卡时，约定名会被它们共用（都指到同一张图），要各用各的图就各自写 `imageFiles`，或者用 `<资源卡号>.png` 这种按卡号命名的写法。

### ■ 借原版卡面

不想自备图片，就直接借原版任意一张卡的卡面：

```jsonc
"normalArtCardId":    125641030,   // 进化前，借这张卡
"evolutionArtCardId": 125641031,   // 进化后，借这张卡
"spellArtCardId":     113041010    // 咒术或魔法阵，借这张卡
```

三个槽位各自独立，只借其中一个当然可以，留空或填 `0` 表示用本卡 `ResourceCardId` 对应的 PNG。借用不挑卡种：随从卡能借法术或护符的卡面，反过来也行；咒术与魔法阵共用同一套材质，最终画成哪一种由本卡自己的 `CharType` 决定。

借卡面时建议显式给本卡一个独立的 `ResourceCardId`（等于它的 `cardId` 就行）。这一条最容易忘：多张卡共用一个资源 ID 却要求不同卡面时，插件会禁用这些卡的卡面覆盖，并输出 `resource-conflict:<id>` 警告。借来的卡面在战场上的取景会一并从来源卡继承：游戏用卡牌数据里的 `NormalTilling`、`NormalOffset`、`EvolTilling`、`EvolOffset` 决定卡图在卡框里的位置与缩放，插件把这四个值从来源卡复制过来，所以借来的图在场上和来源卡长得一样，不用你配。

### ■ 跨阶段取图

默认同阶段对同阶段：你的进化前对应来源的进化前，进化后对应进化后。三个 `*FromEvolved` 可以把某个槽位单独切到来源的另一阶段：`normalArtFromEvolved: true` 让你的进化前用来源的进化后图（写 `false` 是来源的进化前图，这也是默认），`evolutionArtFromEvolved: false` 让你的进化后用来源的进化前图，`spellArtFromEvolved: true` 让咒术卡面取来源的进化后图；省略就是保持同阶段映射。不写和 `false` 语义不同：前者是「按默认」，后者是「明确指定那一阶段」。三个字段互相独立。

```jsonc
"normalArtCardId": 128341010, "normalArtFromEvolved": true,       // 进化前用来源的进化后图
"evolutionArtCardId": 108241030, "evolutionArtFromEvolved": false // 进化后用来源的进化前图
```

### ■ 闪卡动态效果 `foilEffectCardId`

让闪卡复用另一张原版卡的闪卡材质与动态效果，卡图仍然用自己的：`"foilEffectCardId": 125641031`。

来源填该卡的普通版或闪卡版 ID 都行，运行时会自动解析到真正的闪卡版本。只有最终 `IsFoil=true` 的记录会用到它，普通版不显示动态效果。来源与目标必须同为随从，或同为法术护符，不能引用自制卡。多个不同来源绑到同一个 `ResourceCardId` 时效果会被禁用并输出警告，要不同效果就给它们各自不同的资源 ID。卡面槽位和闪卡材质是两回事：`normalArtCardId` 这些管卡面，`foilEffectCardId` 管材质效果，同一条记录可以既借卡面又套材质。想让闪卡有独立的卡图，就手工配对（见「闪卡自动派生」），给闪卡一个独立的 `ResourceCardId`，并额外放一张对应的图。

## ⬤ 5. 语音

音频也可以一个都不给，此时的语音会自动沿用模板卡的。想放自己的音频、，或者想把别张原版卡的语音库借进来，再往下看。

### ■ 借用原版语音库 `extraVoiceIds`

游戏按卡号分库存放语音（`v/vo_<cardId>.acb`），而且只加载卡自身的库。想用别张卡的语音条目，得先声明把那个库加载进来：

```jsonc
"extraVoiceIds": ["125641030_4", "714641010"]
```

每个元素是主数据语音列里的语音 ID，例如 `"125641030_4"`。插件只取第一个 `_` 之前的部分作为库名，所以 `"125641030"` 和 `"125641030_4"` 等价；写 JSON 数字也行，会被当字符串读。重复项自动去重，每场对局每个库只加载一次。这个字段只解决「库有没有加载」，要真播出来，还得让卡自己的语音字段指向对应条目，通常配合 `stringChangeFields`：

```jsonc
"stringChangeFields": { "PlayVoice": "125641030_1", "EvoVoice": "125641030_3",
                        "AtkVoice": "125641030_2", "DestroyVoice": "125641030_4" },
"extraVoiceIds": ["125641030"]
```

### ■ 本地音频文件

一种做法是直接把文件丢进卡文件夹，什么都不用写，插件认这些约定文件名：`play.wav`（登场）、`evolve.wav`（进化）、`attack.wav` / `attack_evolved.wav`（攻击，进化前 / 后）、`destroy.wav` / `destroy_evolved.wav`（破坏，进化前 / 后）。只有存在的那几个会被替换，其余时点继续用模板卡的语音。这些约定名**只认卡文件夹根目录**，放进子目录就不认了。

另一种用 `voiceFiles` 显式指定文件名，名字随意，路径相对于卡自己的文件夹：

```jsonc
"voiceFiles": {
  "play": "登场.wav", "evolve": "进化.wav",
  "attack": "攻击.wav", "evolvedAttack": "进化攻击.wav",
  "destroy": "破坏.wav", "evolvedDestroy": "进化破坏.wav",
  "skills": ["技能1.wav", "技能2.wav"], "evolvedSkills": ["进化技能1.wav"]
}
```

`voiceFiles` 里写了哪个时点就以它为准，没写的时点如果卡文件夹里有对应的约定文件名，也照样会被用上。技能语音（`skills` / `evolvedSkills`）没有约定文件名，只能显式写。值同样是相对卡文件夹的路径，可以带子目录（`"play": "音频/登场.wav"`），但不能用 `..` 跳出卡文件夹，越界会被拒绝并告警。

| 子字段 | 对应属性 | 时机 |
| --- | --- | --- |
| `play` | `PlayVoice` | 登场 |
| `evolve` | `EvoVoice` | 进化 |
| `attack` | `AtkVoice` | 攻击（进化前） |
| `evolvedAttack` | `AtkVoice` | 攻击（进化后） |
| `destroy` | `DestroyVoice` | 破坏（进化前） |
| `evolvedDestroy` | `DestroyVoice` | 破坏（进化后） |
| `skills` | `SkillVoice` | 技能语音，按槽位顺序 |
| `evolvedSkills` | `SkillVoice` | 技能语音（进化后） |

其余几条要点：

- 只认 WAV。这游戏的项目设置把 Unity 音频关掉了（日志里能看到 `Audio system is disabled`），本地语音统一由插件自己解码后经 Windows 原生输出，MP3、OGG、AIFF 这些喂不进去，日志会直接让你重新编码。要用 16-bit PCM WAV：音量是在样本上缩放的，只有这个格式做得到；其它位深（8/24/32bit、float）能播，但音量按 100%，不跟随游戏设置。这条别嫌麻烦，改格式比回头查哑音省事。
- 路径相对于卡自己的文件夹，不能写绝对路径，也不能用 `..` 跳出去，越界会被拒绝并告警。
- 只覆盖你声明的子字段。只写 `play`，进化语音和破坏语音继续用原版。
- 有普通和进化两侧的槽位（`attack`、`destroy`、`skills`）：只写普通侧则两侧都用你的文件；只写进化侧则普通侧保留原版；两侧都写则各自对应。原版只有单形态而你只换了普通侧时，保持单形态，不会凭空造出第二形态。
- 和 `extraVoiceIds` 可以共存：被 `voiceFiles` 接管的播本地文件，其余仍走借来的库，没换掉的语音照常出声。
- 不用再手写 `stringChangeFields` 里的 `PlayVoice`、`EvoVoice`、`AtkVoice`、`DestroyVoice`；两者都写时以 `voiceFiles` 为准。不写这个字段时行为和过去完全一样，旧配置不用改。

本地语音为什么走 Windows：这游戏的项目设置把 Unity 的音频子系统关掉了（日志里会打 `Audio system is disabled`），音频全部由 CRIWARE/ADX2 原生输出，而 CRIWARE 只能播 ACB 里的 cue，喂不进裸 WAV。于是插件在预加载阶段就把 WAV 解好、按当前音量缩放好并固定住，播放时只发一次系统调用 —— 这就是它不影响出牌动画的原因。音量、静音照样跟随游戏设置，`IsVoicePlaying` 也认得。加载成功时不打任何日志，只有出问题才会在 `[CardVoice]` 下报错。

## ⬤ 6. 数值与文本

键名写错是最常见的一类问题。其实很简单，规则只有一条：一律照抄 `CardParameter` 上的属性名。`intFields` 收整数，`CardParameter` 共 18 个 Int32 属性，常用的这些：

```text
BaseCardId / NormalCardId / FoilCardId / ResourceCardId / CardSetId / TwoPickFoilCardId
Cost / Atk / Life / EvoAtk / EvoLife / ChantCount / Rarity
GetRedEther / UseRedEther
```

其他属性（`CharType`、`Clan`、`SortIndex`、`SummonMoveType`、`SummonEffectType`、`EvoEffectType` 等）不在这 18 个之内，`intFields` 一样能改。

```jsonc
"intFields": { "CharType": 1, "Clan": 2, "Cost": 3, "Atk": 2, "Life": 3 }
```

`floatFields` 只有两个属性，正好全覆盖：`SummonTime`（召唤、入场特效时长，秒）和 `EvolTime`（进化特效时长，秒）。

```jsonc
"floatFields": { "SummonTime": 0.8, "EvolTime": 0.8 }
```

它非要单独一个字段，是因为 `intFields` 只收整数，装不下 `0.8`；用 `stringChangeFields` 写字符串会抛 `Object of type 'System.String' cannot be converted to type 'System.Single'`。这两个属性游戏只在构造函数里写一次，运行期不会覆盖。`boolFields` 最常用的是 `IsFoil`，表示当前这条记录是不是闪卡。卡牌类型写在 `intArrayFields`：

```jsonc
"intArrayFields": { "Tribe": [2] }   // 2 = 士兵，7 = 机械
```

### ■ 数值：`intFields`、`floatFields`、`boolFields`、`intArrayFields`

键名一律写 `CardParameter` 上的属性名。`intFields` 收整数，`CardParameter` 共 18 个 Int32 属性，常用的这些：

```text
BaseCardId / NormalCardId / FoilCardId / ResourceCardId / CardSetId / TwoPickFoilCardId
Cost / Atk / Life / EvoAtk / EvoLife / ChantCount / Rarity
GetRedEther / UseRedEther
```

其他属性（`CharType`、`Clan`、`SortIndex`、`SummonMoveType`、`SummonEffectType`、`EvoEffectType` 等）不在这 18 个之内，`intFields` 一样能改。

```jsonc
"intFields": { "CharType": 1, "Clan": 2, "Cost": 3, "Atk": 2, "Life": 3 }
```

`floatFields` 只有两个属性，正好全覆盖：`SummonTime`（召唤、入场特效时长，秒）和 `EvolTime`（进化特效时长，秒）。

```jsonc
"floatFields": { "SummonTime": 0.8, "EvolTime": 0.8 }
```

它非要单独一个字段，是因为 `intFields` 只收整数，装不下 `0.8`；用 `stringChangeFields` 写字符串会抛 `Object of type 'System.String' cannot be converted to type 'System.Single'`。这两个属性游戏只在构造函数里写一次，运行期不会覆盖。

`boolFields` 最常用的是 `IsFoil`，表示当前这条记录是不是闪卡。卡牌类型写在 `intArrayFields`：

```jsonc
"intArrayFields": { "Tribe": [2] }   // 2 = 士兵，7 = 机械
```

### ■ 字符串：`stringChangeFields`、`stringAppendFields`、`stringArrayFields`

| 字段 | 作用 |
| --- | --- |
| `stringChangeFields` | 整体替换字符串字段 |
| `stringAppendFields` | 在原值后追加，以 `,` 开头表示续接一段新技能 |
| `stringArrayFields` | 整体替换 `string[]` 字段 |

可改的技能字段名举例：

```text
Skill / SkillTiming / SkillCondition / SkillTarget / SkillOption / SkillPreprocess
SkillEffectPath / SkillSe / EvolEffectPath ...
```

两个字段写了同一个键时，先替换再追加。同时写容易出错，用一个就行。

### ■ 卡名与描述 `localizationFields`

键是字段名，不带卡号前缀，值为空会被忽略。可写 `CardName`、`Description`、`EvoDescription`、`SkillDescription`、`EvoSkillDescription`、`FlavorText`、`TribeName`。

```jsonc
"localizationFields": {
  "CardName": "皇棋",
  "TribeName": "皇棋",                                 // 卡面类型行
  "SkillDescription": "……[u][ffcd45]皇棋[-][/u]……"      // 这里的「皇棋」会变成可点击词条
}
```

改过的卡名会变成可点击词条。描述里的黄色下划线文字必须存在于游戏的关键字字典 `BattleKeyWordDic` 才会成为词条，而这个字典只含原版卡名；凡是用 `localizationFields.CardName` 改过名的卡，插件会把新卡名补注册进去，于是你在能力文本里写这个卡名，它就变成可点击的黄色词条，点开是这张卡的完整详情。该字典每次进入对局都会整本重建，补注册会在重建之后再补一次，局外的卡组编辑、图鉴和局内都能点。

卡面类型行得靠 `TribeName`。`CardParameter.TribeName` 是只读属性，没有 setter，文字由游戏在构造时按模板的 `_tribeNameId` 查本地化表得到，新卡号在表里没有条目，所以这行默认空白。类型判定和显示是两条独立路径：`tribe=N` 的过滤走技能筛选器解析枚举数字，与这行文字无关，类型名写不写都不影响卡的过滤行为。

## ⬤ 7. 咒术、魔法阵与技能

### ■ 咒术与魔法阵

咒术和魔法阵都不是单独的卡种，就是把随从卡的类型改掉，改的是 `CharType`：

| `CharType` | 类型 |
| --- | --- |
| `1` | 随从 |
| `2` | 魔法阵 |
| `3` | 倒计时魔法阵 |
| `4` | 咒术 |

改造时注意这几处：

- 咒术和魔法阵没有攻防。`Atk`、`Life` 留 `0`，`EvoAtk`、`EvoLife` 也留 `0`。
- 结算时机不一样。随从常用 `when_activate`（挂在场上等着按「启动」），咒术是打出去就结算，写 `when_play`。
- 魔法阵要长期留场，用的是「每当……」那类时机，比如 `when_summon_other`（自己的随从登场时）、`when_destroy_other`、`self_turn_end`。语法和随从技能完全一样，只是 `SkillTiming` 换成留场触发的关键词。
- 倒计时魔法阵多一个 `ChantCount`，填要等几个自己的回合。倒计时归零时护符被破坏，所以效果挂在 `when_destroy` 上 —— 原版的「荣光的炽天使·拉庇斯」就是这么写的（`CharType: 3`、`ChantCount: 1`、`SkillTiming: when_destroy`）。
- 卡面可以借原版咒术/魔法阵的图，用 `spellArtCardId`。想取来源的进化后图就加 `spellArtFromEvolved: true`。
- 咒术和魔法阵共用同一套材质，最终画成哪一种，由本卡自己的 `CharType` 决定，你不用挑材质。

下面三张卡在 `参考模板.example` 里也有，可以直接照着改。它们都用 `stringChangeFields` 整体替换技能串；想在模板卡原有技能后面追加，就改用 `stringAppendFields`，值前面加个逗号。

咒术，打出时对对方随机 1 个随从打 3 点：

```jsonc
{ "newCard": true, "cardId": 999991020,
  "templateCardId": 100011010,     // 模板只提供资源格式，技能串会被整段换掉
  "intFields": { "CharType": 4, "Cost": 3, "ChantCount": 0,          // 4 = 咒术
                 "Atk": 0, "Life": 0, "EvoAtk": 0, "EvoLife": 0, "ResourceCardId": 999991020 },
  "stringChangeFields": { "Skill": "damage", "SkillTiming": "when_play", "SkillCondition": "none",
    "SkillTarget": "character=op&target=inplay&card_type=unit&random_count=1",
    "SkillOption": "damage=3", "SkillPreprocess": "none" },
  "spellArtCardId": 113041010,
  "localizationFields": { "CardName": "示例咒术", "Description": "对对方随机 1 个随从造成 3 点伤害。" } }
```

魔法阵，自己的随从登场时触发：

```jsonc
{ "newCard": true, "cardId": 999991030, "templateCardId": 100011010,
  "intFields": { "CharType": 2, "Cost": 2, "ChantCount": 0,          // 2 = 魔法阵
                 "Atk": 0, "Life": 0, "EvoAtk": 0, "EvoLife": 0, "ResourceCardId": 999991030 },
  "stringChangeFields": { "Skill": "damage",
    "SkillTiming": "when_summon_other",   // 留场，每当自己的随从登场
    "SkillCondition": "character=me&target=summoned_card&card_type=unit",
    "SkillTarget": "character=op&target=inplay&card_type=unit&random_count=1",
    "SkillOption": "damage=1", "SkillPreprocess": "none", "SkillIcon": "induction" },
  "spellArtCardId": 113041010,
  "localizationFields": { "CardName": "示例魔法阵",
                          "Description": "每当你的随从登场，对对方随机 1 个随从造成 1 点伤害。" } }
```

倒计时魔法阵，等 2 个回合后消失并抽 2 张：

```jsonc
{ "newCard": true, "cardId": 999991040, "templateCardId": 100011010,
  "intFields": { "CharType": 3, "Cost": 4, "ChantCount": 2,          // 3 = 倒计时魔法阵，2 回合后归零
                 "Atk": 0, "Life": 0, "EvoAtk": 0, "EvoLife": 0, "ResourceCardId": 999991040 },
  "stringChangeFields": { "Skill": "draw",
    "SkillTiming": "when_destroy",        // 倒计时归零＝护符被破坏
    "SkillCondition": "none",
    "SkillTarget": "character=me&target=deck&card_type=all&random_count=2",
    "SkillOption": "none", "SkillPreprocess": "none" },
  "spellArtCardId": 113041010,
  "localizationFields": { "CardName": "示例倒计时魔法阵", "Description": "倒计时 2：谢幕曲 抽 2 张牌。" } }
```

改完之后别留着随从那套尾巴：进化数值清零，卡面槽位从 `normalArtCardId` / `evolutionArtCardId` 换成 `spellArtCardId`，`attackEffectFields` 不写就行（咒术和魔法阵一般不攻击）。

### ■ 攻击演出 `attackEffectFields`

每个值都是 `[普通, 进化]` 两套，留空的字段保留模板卡原值。

```jsonc
"attackEffectFields": {
  "effectPath": ["btl_holy_shot_1", "btl_holy_shot_1"],   // 特效路径，下面是音效路径
  "se": ["se_btl_holy_shot_1", "se_btl_holy_shot_1"],
  "moveType": ["ARC", "ARC"],                             // 移动类型，例如 DIRECT、ARC
  "effectEnginType": ["SHURIKEN", "SHURIKEN"],            // 引擎类型
  "time": [0.3, 0.3]                                      // 时长
}
```

`effectEnginType` 可选 `NONE`、`SHURIKEN`、`FLATOUT`、`SOLID`。

### ■ 借原版卡的特效（2.5.6 新增）

上面 `attackEffectFields` 要你自己填特效名和 SE 名。**不想记这些名字，就直接借另一张卡（原版或自制都行）的整套演出**，写法和借卡图 `normalArtCardId` 一样：

```jsonc
"summonEffectCardId": 125441020,   // 登场 + 破坏演出
"evolveEffectCardId": 125441020,   // 进化演出
"attackEffectCardId": 125441020,   // 攻击演出（普通 + 进化两套）
"skillEffectCardId":  125441020,   // 技能发动演出（含进化后技能）
"destroyEffectCardId": 125441020,  // 只借破坏演出
"effectBorrowCardId": 125441020    // 上面全部一次借完
```

各字段抄过来的引擎字段（都是整套抄，不会只抄一半）：

| 字段 | 抄的内容 |
| --- | --- |
| `summonEffectCardId` | `SummonEffectPath` / `SummonSePath` / `SummonMoveType` / `SummonEffectType` / `SummonTime` |
| `destroyEffectCardId` | `DestroyEffectPath`（破坏演出没有 SE 字段） |
| `evolveEffectCardId` | `EvolEffectPath` / `EvolSePath` / `EvoEffectType` / `EvolTime` |
| `attackEffectCardId` | 整块攻击演出参数（特效名、SE、移动方式、引擎类型、时长） |
| `skillEffectCardId` | `SkillEffectPath` / `SkillSe` / `SkillMoveType` / `SkillEffectEnginType` / `SkillEffectTime` / `SkillEffectTargetType` 以及对应的 `EvoSkill*` 六项 |

几条要点：

- **优先级**：先抄 `effectBorrowCardId`，再抄各槽位，最后才应用 json 里你自己写的 `stringChangeFields` / `stringArrayFields` / `attackEffectFields`。所以**明确写的永远赢过借来的**，同一条记录里可以「借登场、自己写技能演出」。
- **只借演出，不借效果**。技能本身（`Skill` / `SkillTiming` / `SkillCondition` / `SkillTarget` / `SkillOption` / `SkillPreprocess`）不受影响 —— 想让卡「长一样但打不一样」，这正是你要的。
- **技能槽位数**：借来的 `SkillEffectPath` 等数组是按技能下标取的，长度最好和第 7 章你自己写的技能条数一致。数量不一致时插件只记一行日志提醒，不报错（原版本身也有 9 个技能配 7 条演出的情况）。
- **特效包会自动加载**：战斗里引擎是「按名字要 `effect_<名字>.unity3d` 这个包、取里面的 `Effect/Effects/<名字>`」；自制卡的这些包本来不存在，借来的名字又没人请求过，结果会是**静默无特效**（引擎只是取不到，不报错）。插件在预载清单里补上借来的 `effect_<名字>.unity3d`（磁盘上没有的会跳过），技能 SE 的 `s/<se>.acb` 由引擎自己按卡面字段请求。日志里能看到 `[CardEffect] card … borrows … effects from …`。
- 借来的特效会连同**闪卡版**一起生效（自动派生的闪卡记录也走同一套抄写）。
- 特效名 / SE 名想手写也行：`SummonEffectPath`、`DestroyEffectPath` 是 `stringChangeFields` 的键；`SkillEffectPath`、`SkillSe`、`SkillEffectTime`、`EvolEffectPath`、`EvolSePath` 是 `stringArrayFields` 的键；`SummonMoveType`、`SummonEffectType`、`EvoEffectType` 走 `intFields`；`SkillMoveType`、`SkillEffectEnginType`、`SkillEffectTargetType` 走 `intArrayFields`。

### ■ 技能

### ■ 主动技能 `when_activate`

给场上随从加一个「启动」按钮。六个 `Skill*` 字段必须成组补齐，追加写法以 `,` 开头：

```jsonc
"stringAppendFields": {
  "Skill": ",draw", "SkillTiming": ",when_activate", "SkillCondition": ",character=me",
  "SkillTarget": ",character=me&target=deck&card_type=all&random_count=1",
  "SkillOption": ",none", "SkillPreprocess": ",use_pp=1"   // use_pp=N 设置 PP 消费
},
"localizationFields": { "SkillDescription": "启动（1PP）：抽 1 张牌。" }
```

### ■ 三个扩展技能

写在 `Skill` 串里：

| 关键字 | 作用 |
| --- | --- |
| `skill_geminize` | 复制目标随从的名称、类型、身材、文本与全部技能，并清除自身除该技能外的技能 |
| `skill_acquire_skills` | 获得目标随从的全部技能与非身材 Buff，保留自身技能；不复制同类型技能，也不复制攻击力和生命值修正 |
| `skill_mirror` | 成为法术或单体能力的指定目标时，让该效果对使用者的随机随从再生效一次 |

`skill_mirror` 支持三个参数：`all=true/false`（追加到一个随机随从还是全部随从，默认 `false`）、`include_self=true/false`（镜像目标自身能否成为追加目标，默认 `true`）、`ability=true/false`（除法术外，明确指定本随从的单体能力也能触发，默认 `false`）。

把模板卡改成「启动：复制对面一个随从」：

```jsonc
{ "newCard": false, "cardId": 0, "templateCardId": 999000000,
  "stringAppendFields": { "Skill": ",skill_geminize", "SkillTiming": ",when_activate",
    "SkillCondition": ",character=me", "SkillOption": ",none", "SkillPreprocess": ",use_pp=1",
    "SkillTarget": ",character=both&target=inplay&card_type=unit&select_count=1" },
  "localizationFields": { "SkillDescription": "启动（1PP）：复制一个随从。" } }
```

## ⬤ 8. 排查

**卡图不显示，或显示成原版卡图**：图得放在**卡自己的文件夹**里（跟 json 同目录），不是 `Mods/CardMaster/` 根目录；用 `imageFiles` 声明时文件名要和文件完全一致（含扩展名），用约定名就放 `card.png`（或 `card_evo.png`）；借用卡面时确认没有多张卡共用同一个 `ResourceCardId`，共用会触发 `resource-conflict` 并禁用覆盖。日志里出现 `loading borrowed artwork bundle on demand` 是正常的，首次显示会按需加载。

**卡组列表里卡图空白**：首次进卡组编辑可能空白，稍等或重进即可，借用卡面的来源资源包需要按需加载。一直空白就查日志里的 `material-unavailable` 和 `artwork source ... is unavailable`。

**自己在卡文件夹里放的 PNG 在场上被放大裁切**：这个坑我自己踩过。卡图的取景参数（`NormalTilling`、`NormalOffset`）来自模板卡，不是为你的图准备的。借构图相近的原版卡面可以规避，也可以把这张图单独挂到构图匹配的原版卡上再借。

**卡名在描述里点不开**：确认卡名是用 `localizationFields.CardName` 改的，并且描述里写的字与它完全一致，包括空格。

**闪卡没有动态效果**：查日志有没有 `foil effect ... is disabled because its ... share ResourceCardId`，普通版和闪卡版必须用不同的 `ResourceCardId`；再看来源和目标是否同为随从或同为法术护符；最后看这条记录是不是 `IsFoil=true`。

**闪卡没生成**：日志搜 `gets no foil companion`，说明 `卡号 + 1` 已被占用，成对预留卡号即可。

**本地语音不响**：日志搜 `[CardVoice]`。加载成功是不打日志的，有 `[CardVoice]` 报警就是真出问题了，目前只会是这三种：

| 日志 | 原因 |
| --- | --- |
| `'<文件>' is not a WAV. This game has Unity audio disabled, so only WAV can be played — re-encode it as 16-bit PCM WAV.` | 不是插件能播的 WAV。转成 16-bit PCM WAV，MP3/OGG/AIFF 在这里没用 |
| `Failed to read '<文件>': <原因>` | 读不出来：文件不在卡自己的文件夹里、名字写错、权限或文件名里的特殊字符 |
| `Failed to decode '<文件>': <原因>` | WAV 文件本身有问题：损坏、压缩 WAV、A-law/μ-law 之类 |

**加了 `voiceFiles` 之后，没被替换的语音变哑**：正常不会发生，插件不屏蔽卡牌原有的语音库。真遇到了把日志发出来。

**打出「牌库耗尽获胜 / 死神」这类卡牌时，日志出现 `[BattleVfx] Deck slot 40 is out of bounds (…); using slot N instead`**：这不是错误，是插件在修游戏自己的越界。游戏硬取牌库容器的第 40 个子物体，容器不够 41 个时 `GetChild` 抛异常、整条出牌流程断掉（表现就是按下卡死）。插件把这次取值收敛到合法槽位，所以特效照常播、对局不再卡死。单人对局、双选、联机、房间对战、解谜、回放走的是同一套修复。

**一个坏 json 拖垮整批卡**：2.5.5 已经不这样了，坏文件会点名跳过，其余照常加载。**卡牌列表怎么排序**：按 `Cost` 升序，同费用按 `SortIndex`。改 `intFields.Cost` 就会自动影响排序，通常不用再手配 `SortIndex`。排序覆盖制作卡池、牌组列表、编辑牌组搜索框和卡牌分解四处。

### ■ 其他 `Mods` 目录

Shadowbus 所有可自定义的内容都在游戏根目录的 `Mods/` 下，插件启动时会自动建好这些目录：

| 路径 | 内容 |
| --- | --- |
| `Mods/CardMaster/<卡文件夹>/` | 一张卡的全部资源：`*.json` + 卡图 + 音频，文件名随意 |
| `Mods/CardMaster/*.json` | 直接放根目录的卡：没有自己的文件夹，用不了本地卡图与本地语音 |
| `Mods/CardMaster/Reference/` | 卡名与技能参考表（`card_names.csv`、`card_skills.csv`），由导出功能生成 |
| `Mods/Format/*.json` | 自定义赛制 |
| `Mods/TwoPick/*.json` | 双选（2Pick）配置 |
| `Mods/UnlimitedDecks/` | 无限赛制牌组数据 |
| `Mods/AIData/deck\|style\|emote/` | 电脑对手的牌组、打法风格、表情 |
| `Mods/BossRush/` | Boss Rush 模式配置与存档（`State/`、`Reference/`） |
| `Mods/OfflinizedTasks/*.json` | 离线任务响应缓存，不建议手改 |

`Mods/Profile.json`、`Mods/MyPageBackground.json`、`Mods/P2PIdentity.json`（联机身份标识，勿复制给他人）是几个散装配置文件。本教程只讲卡牌，其余目录（赛制、2Pick、AI、Boss Rush）各自独立、互不影响，只做卡不动它们时，游戏表现和官方一致。自定义赛制写在 `Mods/Format/*.json`：

```jsonc
{
  "id": "示例赛制ID", "displayName": "示例赛制名",
  "deckSizeLimit": 40,          // 卡组张数上限，null = 无限制
  "sameCardLimit": 3,           // 同名卡上限，null = 无限制
  "tokenCardTotalLimit": 5,     // Token 卡总数上限，null = 无限制
  "tokenSameCardLimit": 2,      // 同名 Token 上限，null = 无限制
  "cardLimits": { "999991000": 1 }   // 特定卡单独限张：卡号 -> 上限
}
```

### ■ 版本变化

2.5.6 相比 2.5.5

| 变化 | 说明 |
| --- | --- |
| 新增「借特效」 | `effectBorrowCardId` / `summonEffectCardId` / `destroyEffectCardId` / `evolveEffectCardId` / `attackEffectCardId` / `skillEffectCardId`，借另一张卡的整套演出（特效名 + SE + 移动方式 + 引擎类型 + 时长），并自动把 `effect_<名字>.unity3d` 挂进预载清单 |
| 借来的特效同时作用于闪卡版 | 自动派生的闪卡记录走同一套抄写 |

2.5.4 相比 2.5.3

| 变化 | 说明 |
| --- | --- |
| 新增 `voiceFiles` | 用本地音频文件当卡牌语音 |
| 新增跨阶段取图 | `normalArtFromEvolved`、`evolutionArtFromEvolved`、`spellArtFromEvolved` |
| `localizationFields.TribeName` 生效 | 卡面类型行不再是空白 |
| 借图的场上取景修正 | 自动继承来源卡的 `NormalTilling`、`NormalOffset`、`EvolTilling`、`EvolOffset` |
| 卡牌排序修正 | 牌组编辑里自制卡不再乱序 |

2.5.5 相比 2.5.4

| 变化 | 说明 |
| --- | --- |
| 一张卡一个文件夹 | json、卡图、语音都放进 `Mods/CardMaster/<卡文件夹>/`，只扫一层 |
| 新增 `imageFiles` | 卡图文件名随意，在 json 里声明 |
| 语音文件名随意 | `voiceFiles` 声明；不写则认 `play.wav` 这类约定名 |
| `CardImages` / `CardVoices` 移除 | 不再创建、不再读取，资源只在自己文件夹里找 |
| 本地语音改走 Windows 原生输出 | Unity 音频在本工程里是关掉的，插件直接准备原生缓冲；音量/静音仍跟随游戏设置 |
| 死神 / 牌库耗尽类卡牌全模式修复 | 单人对局、双选、联机、房间对战、解谜、回放的同一个越界崩溃 |
| 一个坏 json 不再拖垮整批卡 | 坏文件点名跳过，其余照常加载 |
| 主界面按钮与横幅清理 | 礼物/任务/公会/通行证/横幅等入口隐藏，本文之外 |

旧配置不用改：这些字段不写就是 2.5.4 的行为。

## ⬤ 9. 字段总表（速查）

前面各章讲完的字段，这里汇总成一张表。找某个字段是干什么的、该看哪一节，翻这里；具体怎么填回对应章节看。

| 字段 | 类型 | 作用 | 详见 |
| --- | --- | --- | --- |
| `newCard` | bool | `true` 建新卡，`false` 改模板卡本身 | 卡牌身份与闪卡 |
| `cardId` | int | 新卡卡号（`newCard=false` 时填 `0`） | 卡牌身份与闪卡 |
| `templateCardId` | int | 模板卡卡号 | 卡牌身份与闪卡 |
| `normalArtCardId` | int? | 借原版卡的进化前卡面 | 卡图 · 借原版卡面 |
| `evolutionArtCardId` | int? | 借原版卡的进化后卡面 | 卡图 · 借原版卡面 |
| `spellArtCardId` | int? | 借原版卡的咒术或魔法阵卡面 | 卡图 · 借原版卡面 |
| `normalArtFromEvolved` | bool? | 普通槽位改取来源的进化后图 | 卡图 · 跨阶段取图 |
| `evolutionArtFromEvolved` | bool? | 进化槽位改取来源的进化前图 | 卡图 · 跨阶段取图 |
| `spellArtFromEvolved` | bool? | 咒术槽位的阶段选择 | 卡图 · 跨阶段取图 |
| `foilEffectCardId` | int? | 闪卡动态材质来源 | 卡图 · 闪卡动态效果 |
| `intFields` | 字典 | 整数属性（`Cost`、`Atk`、`Life` 等） | 数值与文本 · 数值 |
| `floatFields` | 字典 | 浮点属性（`SummonTime`、`EvolTime`） | 数值与文本 · 数值 |
| `boolFields` | 字典 | bool 属性（`IsFoil`） | 数值与文本 · 数值 |
| `intArrayFields` | 字典 | 整数或枚举数组（`Tribe`） | 数值与文本 · 数值 |
| `stringChangeFields` | 字典 | 整体替换字符串字段 | 数值与文本 · 字符串 |
| `stringAppendFields` | 字典 | 在原字符串后追加 | 数值与文本 · 字符串 |
| `stringArrayFields` | 字典 | 替换 `string[]` 字段 | 数值与文本 · 字符串 |
| `localizationFields` | 字典 | 卡名、描述、类型名等文本 | 数值与文本 · 卡名与描述 |
| `extraVoiceIds` | string[] | 借用原版语音库 | 语音 · 借用原版语音库 |
| `imageFiles` | 对象 | 用卡文件夹里的本地卡图，文件名随意 | 卡图 · 自带卡图 |
| `voiceFiles` | 对象 | 用卡文件夹里的本地音频当卡牌语音 | 语音 · 本地音频文件 |
| `attackEffectFields` | 对象 | 攻击演出参数 | 咒术、魔法阵与技能 · 攻击演出 |
| `summonEffectCardId` | int? | 借另一张卡的登场/破坏演出 | 咒术、魔法阵与技能 · 借原版卡的特效 |
| `destroyEffectCardId` | int? | 只借破坏演出 | 同上 |
| `evolveEffectCardId` | int? | 借进化演出 | 同上 |
| `attackEffectCardId` | int? | 借攻击演出 | 同上 |
| `skillEffectCardId` | int? | 借技能发动演出（含进化后） | 同上 |
| `effectBorrowCardId` | int? | 一次借完上面全部演出 | 同上 |

表里没有「闪卡记录」这种字段，`newCard` 会自动派生出闪卡版，见「闪卡自动派生」。

## ⬤ 10. 附：从二代（Worlds Beyond）搬一张卡过来

这一节是实战记录：把二代的「约束的《正义》·伊兰翠」做成了一张自制卡，放在
`Mods/CardMaster/约束的正义·伊兰翠/`（连同 `说明.md`）。二代客户端装在
`%LOCALAPPDATA%\sevo\ShadowverseWB`，数据流程和一代**完全不同**，先把结论说清楚，免得白忙：

| 想搬的东西 | 能不能搬 | 怎么搬 / 为什么不能 |
| --- | --- | --- |
| **数值与文本**（费用/身材/卡名/能力文本/风味文本） | ✅ 能 | 二代 master 数据是 `mastermemory.bytes`（MessagePack + LZ4，**没加密**）：`BaseCardMaster` 有费用/攻击/生命/稀有度，`MasterTextLabel` 有全部文案。参考工具 `_tools/_sv2_master.py`、`_tools/_sv2_card.py` |
| **效果（能力逻辑）** | ✅ 能 | 照着官方的技能串翻译成一代语法。二代文本里的【威慢/威慑】＝一代的「不会被攻击」（`not_be_attacked`）；「进化前//进化后」的分支在一代同样用 `//` 写 |
| **特效（VFX）** | ⚠️ 只能近似 | 二代特效包是**新版 Unity 序列化**的，一代（Unity 2020.3）引擎读不了，必须用**同版本编辑器重新打包**。所以先借本家现成特效（见「借原版卡的特效」）。二代特效路径可从 `CardResourceMaster` 查到，例：`btl_10544110_1..6`、`smn_10544110_1` |
| **音效（SE/语音）** | ⚠️ 拿到数据了，缺解码器 | `sound/Windows/d/<语言>/dx_<卡号>.pck` 是 **Wwise** 包（magic `AKPK`），里面是 10 个 WEM（Wwise Vorbis，48 kHz 单声道），**没加密**，能整包拆出来（`_tools/_sv2_wwise.py`）。但要转成一代能播的 16-bit PCM WAV，本机得有 vgmstream（或 ffmpeg + vgmstream）；没有就只能先借本家 SE |
| **卡图** | ❌ 暂时不能 | 二代**下载到本地**的卡图包（`Persistent\dat\**`）从**文件偏移 256 到结尾是加密的**（预装包 `StreamingAssets/HybridBundles/*.ab` 没加密，但卡面不在里面）。所以卡图先沿用模板卡的；把 `card.png` / `card_evo.png` 放进卡文件夹就能换上（json 不用改），或者用社区解包工具（如 SVGWBTools/Wizard2AssetsUnpacker）先把包解开再导出 |

搬运的实操顺序（本例就是这么做的）：

1. **认卡号**：`python _tools/_sv2_master.py find 伊兰翠`（按卡名找 `MasterTextLabel`），拿到 8 位卡号
   `10544110`；卡面资源号是 9 位（`105441100` 普通 / `105441101` 进化 / `105441102` 异画）。
2. **抄数值**：`python _tools/_sv2_card.py`（默认就查这张卡）会把 `BaseCardMaster` / `CardText` /
   `CardResourceMaster` / 技能表全 dump 成 JSON，写进 `_sv2_out/`。
3. **写效果**：把官方技能文本翻成一代的六个 `Skill*` 字段；一代支持用 `//` 在同一个槽位里写
   「进化前//进化后」，例如本卡：
   ```jsonc
   "Skill":        "guard//attach_skill,damage//damage,heal//none",
   "SkillTiming":  "when_change_inplay//when_evolve,self_turn_end//self_turn_end,self_turn_end//none",
   "SkillOption":  "none//skill=(skill:not_be_attacked)(timing:when_change_inplay)(condition:character=me)(target:character=me)(option:none)(preprocess:none),damage=8//damage=8,healing=8//none"
   ```
   守护只在进化前那一侧，进化后自然没有；「进化时获得不会被攻击」用 `attach_skill` + 内嵌技能串
   （照抄 `黑翼霸主·芙洛迪.example`）。
4. **接演出**：`summonEffectCardId` / `evolveEffectCardId` / `attackEffectCardId` 借一张本家卡
   （本例借 `125441020`），技能演出自己指定 `btl_unique_lucifer_2` 这类现成特效名。
5. **补素材**：有 PNG/WAV 就丢进卡文件夹（`card.png` / `card_evo.png` / `play.wav` …），没有就先借。

> 二代的资产索引在 `Persistent/meta`（SQLite，表 `a`：`n` = 资源路径，`h` = `Persistent/dat/<前两位>/<h>` 文件名），
> 查路径用 `_tools/_svwb_store.py`（`--like` / `--get` / `--prefixes`）。
