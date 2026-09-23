# 剧情脚本对战覆盖（StorySpecialBattles）

剧情章节里有一类**脚本战**：开局先后手、双方初始 PP / 生命、双方附加技能、战斗日志里的名字、各种特效覆盖、结算跳过等，由服务器的 `StoryStartTask` 响应带下来（客户端字段名 `special_battle_setting`）；敌方主战者 / 职业 / 用哪套 AI、战场 3D 背景、BGM 则由 `StoryInfoTask` 的章节响应带下来。这些值客户端与资源包里都没有，所以离线时插件只能自己生成 —— 生成得不对就会出现「玩家主战者不对、敌方 AI 完全不出牌、没有 BGM、战场背景是默认的」这类问题。

这个目录就是给这些章节补官方值 / 修正离线生成值的。

## 文件名

`Mods/StorySpecialBattles/<story_id>.json`

`story_id` 规则（与插件 `StoryOfflineData.CreateStoryId` 一致）：

```
story_id = section * 100000 + class * 10000 + chapter * 100 + suffix * 10
```

- 20 篇 38 章 class 1 = **2013800**（插件日志里会直接打印这种数字）
- 20 篇 8 章 class 5 = **2050800**（已填，见下）

进这类章节时日志会给出要补的文件名：

```text
[Offlinizer] Story 2013800 has no local special battle setting; the battle runs without the
server-defined gimmick. Optional: put the official special_battle_setting object in
Mods/StorySpecialBattles/2013800.json.
```

## 文件内容

一个 JSON 对象，可包含三段（都以 `_` 开头的段不会进 `special_battle_setting`）：

| 段 | 作用 | 必填 |
| --- | --- | --- |
| 顶层字段 | 官方那份 `special_battle_setting` 本身 | 否（只想要章节修正时可以不写） |
| `_chapter` | 修正离线生成的章节级字段（玩家/敌方主战者、敌方职业与 AI、战场、BGM） | 否 |
| `_ai` | 用 `Mods/AIData` 里的本地 CSV 完全接管敌方 AI | 否 |

### 顶层：`special_battle_setting`

| 字段 | 含义 |
| --- | --- |
| `player_first_turn` | 先手：`1` = 玩家先手，`0` = 不覆盖 |
| `player_start_pp` / `enemy_start_pp` | 双方初始 PP（客户端是直接赋值给 `PpTotal`，`0` 表示和普通对战一样从 0 开始累计） |
| `player_start_life` / `enemy_start_life` | 双方初始（最大）生命 |
| `player_attach_skill` / `enemy_attach_skill` | 双方附加技能（客户端技能 DSL，脚本战的核心） |
| `id` | 特殊战斗 id |
| `id_override_in_battle_log` | 战斗日志里替换显示的名字 id |
| `banish_effect_override` | 消滅特效覆盖 |
| `token_draw_effect_override` / `special_token_draw_effect_override` | 抽 token 特效覆盖 |
| `result_skip` | 结算跳过 |
| `vs_effect_override` | VS 演出覆盖（`1` = 是） |
| `class_destroy_effect_override` | 职业破坏特效覆盖 |

完整字段骨架见 `_template.example`（这个文件名不会被读取，只作参考）。

### `_chapter`：章节级修正

| 键 | 映射到的响应字段 | 说明 |
| --- | --- | --- |
| `player_chara_id` | `chara_id` | 玩家主战者。**必须给**，否则玩家会用当前职业的**初始**主战者 —— 客户端 `GetCharaPrmByClassId(classId, false)` 返回的是 `classPrm.DefaultCharaData`，也就是职业初始主战者（class 5 = 露娜 = chara 5），而剧情主角是另一张（20 篇第 8 章 = 尼古拉 `2515`）。战斗里的主战者来自 `BattleSettingData.PlayerCharaId`：`battle_settings[].skin_id_override` 为 0 时用章节响应的 `chara_id`，所以这里填了就生效；`deck_skin_id_override` 只影响按皮肤查找 battle_setting |
| `enemy_chara_id` | `enemy_chara_id` | 敌方主战者 / 皮肤 |
| `battle3dfield_id` | `battle3dfield_id` | 战场 3D 背景 id |
| `bgm_id` | `bgm_id` | 战斗 BGM 的**主题名**（不带前缀），例如 `redalert`。战场实际播的 cue 是 `BackGroundBase.PlayBgm()` 里拼出来的 `bgm_field_<bgm_id>`，也就是 `b/bgm_field_redalert.acb`。不填时是 `"0"`，客户端当成 `NONE`（静音） |
| `enemy_ai_id` | `enemy_ai_id` | 直接指定官方 AI id。一般不用手填，见下 |
| `enemy_class` | `enemy_class` | 敌方职业。**以这里的值为准**——它就是服务端下发的 `story_master_list.enemy_class`，客户端直接当 `BattleSettingBaseData.EnemyClassId` 用；没写时才退回「敌方主战者自身的职业 → AI 牌组主职业」 |
| `player_emotion_variation` / `enemy_emotion_variation` | `battle_settings[].player_emotion_override` / `enemy_emotion_override` | 表情/动作/台词表变体号，钉住这一章用哪张 `emote_chara_<主战者>_<变体>.csv` |
| `enemy_deck_id` / `enemy_style_id` / `enemy_emote_id` | — | 国服捕获到的敌方 AI 参数，插件用它在本体 master 的 `StoryAISettingList` 里反查官方 AI id |

关于 `enemy_deck_id`：它就是 `ai_deck_filelist` 的第一列，也就是 `StoryAISettingData.DeckId` 本体 —— 客户端是用它去 `AIDeckFileNameList.GetFileName(DeckId)` 取文件名的。例如 `210008 → ai_deck_purge_isunia_08`、`280000 → ai_style_purge_isunia`（style）、`280000 → ai_emote_story_280000`（emote）。所以插件现在直接按 `DeckId` 相等来找匹配的 AI，找到就把它的 `EnemyAiId` 交给客户端（连同逻辑等级、风格、表情一起对上）。

敌方职业的取值顺序是：**`_chapter.enemy_class`**（官方章节表生成，就是服务端下发的值）→ 敌方主战者自身的职业 → AI 牌组的主职业。之所以让官方值排第一：剧情里大量复用同一个杂兵模型（`class_chara_master` 里固定一个职业，例如 `500007` 是 clan 1）去打别的职业，只按模型职业判会被改错；官方值则是客户端线上直接使用的那一个。职业判错的后果很严重：客户端会拿另一个职业的数据去构筑牌组，构筑结果是空牌组 —— 表现就是**敌方 AI 完全不出牌**。

> **关于玩家主战者的适用范围**：离线生成时 `chara_id` 只能填「该职业的初始主战者」，而剧情主角往往是另一张卡
> （class 5 的初始主战者是露娜 `5`，而 20 篇 8 章的主角是尼古拉 `2515`），所以**每一章只要主角 ≠ 职业初始主战者，
> 都需要 `player_chara_id`**。这个映射只存在于服务器响应里（客户端没有 section→主角 的表），现在由
> `_tools/build_story_battle_data.py` 从官方表自动生成；要自己补某一章，就是在国服进一次那一章（第 0 / 1 节的方法）。

### `_ai`：用本地 CSV 完全接管敌方 AI

当官方 AI 数据本地缺失（或想自己配）时，可以在 `Mods/AIData` 下放三份 CSV 并在这里引用：

```json
"_ai": {
  "deck": "2050800(deck).csv",
  "style": "2050800(style).csv",
  "emote": "2050800(emote).csv",
  "logic": 2,
  "max_life": 20,
  "use_inner_emote": true,
  "cards": [ { "id": 113521020, "num": 3 } ]
}
```

- `deck` / `style` / `emote`：文件名（在 `Mods/AIData/deck`、`Mods/AIData/style`、`Mods/AIData/emote` 下查找），也可以写相对 `Mods` 的路径。
- `cards`：可选。直接给牌组卡表（`{"id":卡id,"num":张数}` 或直接写卡 id），给了就不用 `deck`。
- `logic`：0 = WEAK，1 = MIDDLE，2 = STRONG。`max_life`：敌方最大生命。

CSV 列（与客户端 `AICardDataAsset` / `AIPolicyDataAsset` / `AIEmoteDataAsset` 一致，带表头行）：

```text
deck : card_id,use_common,card_name,card_num,battleBonus,playBonus,priority,tag1_type,tag1_arg,tag1_cond,...(每 3 列一组 tag),note
       use_common 写 "〇" 表示用 ally-common 数据，其它值表示不用
style: id,category,priority,type,arg,cond
emote: id,category,face_id,motion_id,voice_id,text_id
```

官方剧情敌方 AI 的数据**已经导出到资源目录的 `story_ai/{deck,style,emote}`**
（`<资源根>/story_ai`，约 590 KB，267 个 deck / 82 个 style / 40 个 emote），可以直接按文件名引用，
不用改名 —— 导出工具是仓库外的 `_tools/export_story_ai.py`：

```json
"_ai": { "deck": "ai_deck_purge_isunia_08.csv", "style": "ai_style_purge_isunia.csv", "emote": "ai_emote_story_280000.csv" }
```

查找顺序是「`Mods/AIData/<类型>/`（玩家的）→ `<资源根>/story_ai/<类型>/`（官方的）」，
所以想改官方数据时把同名文件放到 `Mods/AIData` 就能覆盖，官方那份不用动。

那张官方表（`a/master_story_ai_setting.unity3d`）也可以直接导出查看，用来确认某一章的
官方 AI 参数；例如 20 篇第 8 章：

```text
enemy_ai_id,deck_id,style_id,emote_id,logic_level,use_inner_emote
290008,210008,280000,280000,2,0
```

```powershell
python D:\Games\Shadowbus\_tools\unityfs_extract.py --textasset `
  D:\Games\Shadowbus\Resources\a\master_story_ai_setting.unity3d D:\temp\extracted
```


### 2.5 战斗恢复记录（**推荐**：一次 adb 拷贝就能拿到全部官方值）

国服客户端开打时会写一份战斗恢复记录，里面就有章节响应里那几个服务器独有字段：

```text
/sdcard/Android/data/com.netease.yzs/files/recovery/<udid>_recovery_single.json
```

它是 AES-256-CBC 加密的，但**密钥明文写在文件开头**（前 32 字符是密钥、前 16 字符兼作 IV、
第 32 字符之后是 base64 密文，见客户端 `Cute.CryptAES.DecryptRJ256ForNode`），所以不需要任何
设备信息。仓库外的工具 `_tools/cn_recovery_extract.ps1` 会把这一整套走完：

```powershell
# 先在国服客户端里把那一章打到进战斗，然后：
powershell -NoProfile -ExecutionPolicy Bypass -File `
  D:\Games\Shadowbus\_tools\cn_recovery_extract.ps1 -WriteChapter
```

输出（20 篇第 8 章的真实结果）：

```text
  battle_type      = 4   (4 = Story)
  story_id         = 2050800   (section 20, class 5, chapter 8)
  battle3dfield_id = 73   -> a/bg_3dfield73.unity3d
  bgm_id           = 'redalert'  -> b/bgm_field_redalert.acb
  player           = clan 5, chara 2515, deck 40 cards
  enemy            = clan 8, chara 500211, deck 40 cards
  enemy AI         = deck 210008, style 280000, emote 280000, logic 2, maxLife 20, innerEmote False
  special battle   = id '37', pp 0/0, life 20/20, skipResult 0
  player first     = True
```

把最后打印的 `_chapter` 段贴进对应的 json 即可。**这条路线比内存 dump 简单得多**，
而且顺带给出双方完整的 40 张卡表（可以拿来核对 `Resources/story_ai/deck/` 里的官方牌组 CSV）。

加 `-EmitJson` 还会**直接生成插件读的那一整份 json**（顶层 `special_battle_setting` + `_chapter`），
可以拿来和我们手写的那份逐字段比对：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  D:\Games\Shadowbus\_tools\cn_recovery_extract.ps1 -EmitJson
python D:\Games\Shadowbus\_tools\compare_story_json.py      # 生成 <OutDir>/story_<id>.json 后比对
```

`2050800` 的比对结果是 **16 个字段 0 差异** —— 包括两段 2290 / 3146 字符的附加技能 DSL：

```text
_chapter                OK    {'player_chara_id': 2515, 'enemy_chara_id': 500211, 'enemy_class': 8,
                              'enemy_deck_id': 210008, 'enemy_style_id': 280000, 'enemy_emote_id': 280000,
                              'battle3dfield_id': 73, 'bgm_id': 'redalert'}
enemy_attach_skill      OK    (len 3146 vs 3146)
player_attach_skill     OK    (len 2290 vs 2290)
... fields compared: 16, differences: 0
```

> 另外已验证：恢复记录里敌方那 40 张卡与 `Resources/story_ai/deck/ai_deck_purge_isunia_08.csv`
> 逐张一致（14 种、张数全同）。

## 自检：这一章是否已经完全离线可用

仓库外的 `_tools/verify_story_battles.py` 会把本目录下每个 `<story_id>.json` 引用的资源逐个核对：
战场 3D 场地、`bgm_field_<bgm_id>`、双方主战者是否在 `class_chara_master` 里、**敌方职业与敌方主战者的
职业是否一致**、敌方 deck/style/emote 的 CSV 有没有导出、`_ai` 引用的文件能否找到、双方附加技能是否非空，
最后再统计 `story_ai/` 目录对 master 全表的覆盖率：

```powershell
python D:\Games\Shadowbus\_tools\verify_story_battles.py
```

当前结果（0 问题）：

```text
[OK  ] story 2050800  field 73; bgm redalert; 玩家主战者 2515(clan 5); 敌方主战者 500211(clan 8);
      deck ai_deck_purge_isunia_08; style ai_style_purge_isunia; emote ai_emote_story_280000;
      附加技能 player_attach_skill,enemy_attach_skill

=== story_ai 目录覆盖率 ===
deck   需要 267 个，已导出 267/267
style  需要 85 个，已导出 82/82，文件名表里没映射 [8003, 8004, 8005]
emote  需要 40 个，已导出 40/40

=== 剧情 AI 牌组的卡表覆盖 ===
  卡表 11895 张；剧情 AI 牌组 267 套，用到 1733 张不同的基础卡（另外 11 行是带版本号的卡）
  每套牌组张数：最少 40，中位 40，最多 40
  所有引用到的卡都在本地卡表里（0 缺失）
```

两点说明：

- 没映射的 `8003/8004/8005` 属于 AI 8003/8004/8005，用的是 `9101/9301/9501` 这套练习场 stock 牌组、
  也没有表情，是遗留行，与剧情无关。
- 卡 id 超过 9 位时**后两位是版本号**（`10332401007` = 基础卡 `103324010` + 版本 07），
  客户端 `AICardDataAssetSet` 就是这么拆的，工具里做了同样的处理。
- 267 套牌组**每一套都正好 40 张**，这本身就说明导出的 CSV 与官方完全一致。

（另外 `_tools/verify_resources.py` 核对的是资源目录本身：14 张 manifest 共 4 万余条全部存在。）

## 已经填好的

| 内容 | 数量 | 说明 |
| --- | --- | --- |
| `_chapter`（官方章节表生成） | **424** | 由 `_tools/build_story_battle_data.py` 从 `<资源根>/story_ai/story_battles_official.csv`（国服 `main_story/info` 原始值）生成：敌方主战者/职业/AI、战场 3D、BGM、玩家主战者、双方表情表变体号。与本地 `story_scenario_param_*_2` 战斗脚本数量 **1:1 对齐（424/424，0 遗漏）**。跑 `--report` 只分析不写盘 |
| `special_battle_setting` 正文 | **39 / 42** | 已收 39 个设置（74 份章节文件），含 `2050800`(id37)、`2011700`(id38，敌方 99 血)、`2072400`(id39)、`110200`(id8)、`110400`(id9)。**服务端独有数据**，只能由客户端进那一章时从 `main_story/start` 响应里取（或用 `_tools/yzs_fetch_story_settings.py` 按客户端原样重放该接口；也可以用 `_tools/harvest_story_settings_from_captures.py` 直接从抓包里收）。生成脚本会原样保留正文，只覆盖 `_chapter` |

> **还差 3 个**：`设置 40/41/42` = 第 20 篇第 32/35/38 章。它们要先通关第 24 章，而第 24 章在国服客户端里有一场演出崩溃（`CharacterManager.CreateCharacter` 拿到 null prefab，`ArgumentException: The Object you want to instantiate is null`），演出播不下去就进不了那一关。服务端对这三个 `main_story/start` 一律回 `500`（进度没到），所以只能等客户端那边能过关之后再收。

> ⚠️ **没有正文的文件里，这一章必须什么都不返回。**
> `StoryStartTask.Parse` 的判断是 `if (jsonData.Count == 0) { 重置跳过标记; return; }`，
> 之后再 `jsonData["special_battle_setting"]["player_first_turn"]` 这样**直接索引**十几个键。
> 所以"文件存在但只有 `_chapter`"时，离线响应里的 `data["0"]` 必须是**空对象**；
> 一旦写成 `{"special_battle_setting": {}}`，客户端就会走进取值分支并抛
> `KeyNotFoundException`，表现是这一章**点了进不去战斗**
> （`Player.log`: `[Offlinizer] Error processing local data for StoryStartTask`）。
> 同理，手写正文时下面这些键一个都不能少：`player_first_turn`、`player_start_pp`、
> `enemy_start_pp`、`player_start_life`、`enemy_start_life`、`player_attach_skill`、
> `enemy_attach_skill`、`id`、`id_override_in_battle_log`、`banish_effect_override`、
> `result_skip`、`vs_effect_override`、`class_destroy_effect_override`
> （插件会给缺失的键补默认值兜底）。

> `main_story/info` 里一个战斗章的 `battle_settings` 是**一个数组**——一章可以在多个职业下开打，每个职业一条。建表时必须逐条展开（`_tools/build_official_table.py` 就是这么做的）；按 `story_id` 去重会把多职业的行整批丢掉。

`2050800` 的 `_chapter`：玩家主战者 `2515`（尼古拉，clan 5）、敌方主战者 `500211`（clan 8）、敌方 AI `290008`、战场 `73`、BGM `redalert`、表情变体 1/3 —— 与官方表逐字段一致。

另外 73 章带 `special_battle_setting_id` 但**没有正文**，它们按「无服务端特殊覆盖」运行（和之前一样）。

> **为什么这 73 章取不到**（已实测，别再试一遍）：服务端只接受**已解锁**的章节。
> 用 `main_story/get_deck_list {story_id:649}` 能返回 `result_code=1`，换成一个未解锁章节就返回 `result_code=500`。
> 官方 `main_story/info` 里 677 章只有 62 章 `is_released=true`，而带 `special_battle_setting_id` 的 74 章里
> **只有 `649` 这一章是已解锁的** —— 也就是唯一有正文的那个。原因是剧情的线上进度记录来自服务端，
> 而离线 mod 让所有章节都能直接玩，服务端因此从没记录过进度，自然几乎全是「未解锁」。
> 要批量拿就得先发 `main_story/finish` 伪造成已通关，那是伪造进度，不做。
> 工具已备好（`_tools/yzs_fetch_story_settings.py`，按设置 id 去重，41 个请求覆盖 73 章，遇到非 1 的结果码立刻停），
> 哪天账号真的解锁了那些章节，直接 `--apply` 跑一遍即可。

## 这些值从哪里来

### 0. 官方章节表（**推荐**：一次批量取全，不用进战斗）

国服客户端的 `main_story/info` 每章返回一条完整记录，包含 `battle_exists`、`enemy_chara_id`、`enemy_class`、`enemy_ai_id`、`battle3dfield_id`、`bgm_id`、`special_battle_setting_id` 和 `battle_settings` 七字段。这张表已经落到 `<资源根>/story_ai/story_battles_official.csv`，`_tools/build_story_battle_data.py` 负责生成 `_chapter`。

要自己重新取一遍（只读接口，务必放慢），同目录 `_tools` 里有完整链路：

```powershell
dotnet run _tools\yzs_h2proxy.cs -- 443 D:\Games\Shadowbus\_tools\mitm   # 宿主机反向代理
adb push _tools\mitm_setup.sh /data/local/tmp/ ; su -c "sh /data/local/tmp/mitm_setup.sh install"
python _tools\mitm_decode.py                       # 解密抓到的请求/响应
python _tools\yzs_story_scan.py --sections 1-20 --charas 0 --template <最新抓包号> --interval 5
su -c "sh /data/local/tmp/mitm_setup.sh uninstall" ; adb shell pm clear  # 收尾还原
```

协议要点：请求体是**裸二进制** `AES-CBC(msgpack(params), IV=udid[0:16]) + 32 字节 ASCII 密钥`；`PARAM = SHA1(真UDID + "/index.php/<路径>" + base64(msgpack(params)) + viewer_id)`；请求头的 `SID` 要**原样复制客户端请求里的那个**（响应 `data_headers.sid` 是另一个值）。签名公式可用 `_tools/yzs_param_probe.py` 对任意抓包逐条验证。

> 注意：接口有频率风控。同一账号短时间内打太多请求会让**整个会话**对所有接口返回 `result_code=201`；重新登录即可恢复，但**务必按人类节奏**（例如每 5 秒 1 个请求、单批几十个）。

### 1. 战斗 setup（`special_battle_setting`）—— 国服内存

官方服务器已经关闭，但**国服（网易）客户端还在运营**，而且它把整场战斗的 setup JSON（含上面全部字段）放在内存里。做法（模拟器已 root）：

1. 在国服客户端里**进那一章的脚本战斗**（不用打赢，进战斗即可）；
2. 取进程内存：`pid=$(pidof com.netease.yzs)`，从 `/proc/$pid/maps` 里挑 `rw-p` 匿名区（含 `heap`/`ashmem`/`anon`），逐个
   `su -c "dd if=/proc/$pid/mem of=/sdcard/memdumps/rN.bin bs=4096 skip=<start/4096> count=<len/4096>"`
3. `grep -a -l story_special_battle_player_attach_skill /sdcard/memdumps/*`，命中那份就是；
4. 转换（脚本已放好，仓库外的 `_tools/yzs_battle_extract.py`）：

   ```powershell
   python yzs_battle_extract.py 命中的dump.bin 2050800 `
     "D:\Github\Shadowbus\Mods\StorySpecialBattles\2050800.json" `
     "D:\Games\Shadowbus\Shadowverse\Mods\StorySpecialBattles\2050800.json"
   ```

5. 重启游戏（或重开那一章），日志会打印：

```text
[Offlinizer] Applied local special battle setting '2050800.json' for story 2050800.
```

> 注意：内存里的 setup JSON 只在战斗期间存在，战斗结束/被 GC 后就没了 —— 所以要「进战斗 → 立刻 dump」。
> 另一条更省事的路是抓 `main_story/start` 的响应：`_tools/yzs_h2proxy.cs` + 客户机 hosts 指向宿主机 + 系统 CA，
> 用 `_tools/mitm_decode.py` 解密即可（国服是 gzip + base64 + AES-256-CBC，密钥拼在密文尾部，IV 是 UDID 前 16 位，
> 这些都已经验证并写在解码脚本里）。MITM 只劫持一个域名、响应原样转发，收尾用
> `su -c "sh /data/local/tmp/mitm_setup.sh uninstall"` 一条命令还原。

### 2. 本地剧情脚本（核对 BGM / 背景 / 演出）

章节响应里的 `bgm_id`、`battle3dfield_id` 离线拿不到时，还有一个本地来源：`story_scenario_param_*` 资源（路径 `Story/Param/scenario_param_*`）里带着这一章的演出脚本，BGM、背景、战斗指令都在里面。

插件会把实际读到的脚本导出到 `Mods/StoryScenario/<资源名>.csv`，所以只要在游戏里把那一章正常播一次，就能对照着把 `bgm_id` / `battle3dfield_id` 补进 `_chapter`：

```text
[Offlinizer] Exported local story scenario 'story_scenario_param_20_5_8_1.csv'.
```

也可以不进游戏直接读（仓库外的 `_tools/unityfs_extract.py`）：

```powershell
python D:\Games\Shadowbus\_tools\unityfs_extract.py --strings `
  D:\Games\Shadowbus\Resources\a\story_scenario_param_20_5_8_1.unity3d
```

> 已确认的边界：**剧情脚本里只有 2D 剧情背景和剧情 BGM**（例如 20-8 的
> `scn_bg_isunia_fountain_1`、`"1","3","BGM_2","bgm_st_isunia02"(,"BGM_3","bgm_st_lastbattle","BGM_4","bgm_st_deadlock")`），
> **没有战斗 3D 场地**。客户端里 `BattleManagerBase.CreateBackgroundId()` 对 Story 直接返回
> `GetSoroPlay3DFieldID()`（来自章节响应），`CreateBgmId()` 直接返回 `GetStoryBgmID()`
> （同样来自章节响应，`"0"` 会被 `BattleSettingBaseData` 转成 `NONE`）。场景结束处理
> `GlobalVariableSetter.ClearBattleParameter()` 只会在战斗结束后把 BGM 重置回 `NONE`，
> 不会设置它。
>
> 所以这两个值只存在于服务器响应里。插件现在的处理是：
>
> - **BGM**：`_chapter.bgm_id` 原样传给客户端，战场播 `bgm_field_<bgm_id>`。没填就是客户端的
>   `NONE`（静音）。**不要**拿剧情脚本里注册的 BGM 去兜底：那里写的是完整 cue 名
>   （如 `bgm_st_isunia02`），拼出来是 `bgm_field_bgm_st_isunia02`，根本不存在。
> - **战场 3D 场地**：`_chapter.battle3dfield_id`，没填是默认的 `1`。可用 id 见下。
> - 官方值用下面第 2.5 节的办法拿，两个值都能拿到。

### 3. 可用战场 id（`battle3dfield_id`）

官方值拿不到时只能自己挑。客户端 `BattleManagerBase.CreateManager()` 按 id 建场地，下表是
「代码支持 **且** 本地有 `a/bg_3dfield<id>.unity3d` 资源」的 id：

| id | 场地 | id | 场地 | id | 场地 | id | 场地 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | Forest | 2 | Castle | 3 | Volcano | 4 | RoyalPalace |
| 5 | Temple | 6 | Chateau | 7 | Laboratory | 8 | Gate |
| 9 | Arena | 10 | Plaz | 14 | RoyalPalaceNight | 15 | TempleNight |
| 17 | LaboratoryNight | 18 | Yuwan | 20 | PlazRioting | 21 | Hill |
| 22 | Alley | 23 | HillRioting | 30 | Iron | 31 | Nate |
| 32 | Nat2 | 33 | Nat3 | 34 | Nat4 | 41 | Rivayle |
| 42 | RivayleBackalley | 43 | VellsarDesert | 51 | Field51 | 52 | Field52 |
| 61 | Field61 | 62 | Field62 | 71 | Field71 | 72 | Field72 |
| 73 | Field73 | 74 | Field74 | 75 | Field75 | 76 | Field76 |
| 1001 | SpecialArena | 1004 | Stage | 1008 | Field1008 | 1009 | Field1009 |

代码里还有 `11 / 1002 / 1003 / 1005 / 1006 / 1007 / 1010 / 1011 / 1012`，但本地没有对应资源，
填了会加载失败，别用。

BGM 名字直接对应资源目录里的 `b/`，要挑的话列一下：

```powershell
Get-ChildItem "D:\Games\Shadowbus\Resources\b\bgm_*.acb" | Select-Object -ExpandProperty BaseName
```

剧情脚本里用到的章节 BGM 也在 `Mods/StoryScenario/<章节>.csv` 里能搜到（搜 `BGM_`）。

> `_chapter` 是按文件写入时间缓存的：改完 `Mods/StorySpecialBattles/<story_id>.json` 直接重进那一章
> 就生效，不用重启游戏 —— 战场和 BGM 这类值本来就要靠试。
