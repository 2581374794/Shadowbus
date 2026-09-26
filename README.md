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
- BossRush（活动 Boss 连战）：用 `Mods/BossRush/<配置>/bossrush.json` 定义本地挑战，整条流程离线应答、不需要服务器；**没有活动期限制**，界面上也不显示期限文本（见 `Mods/BossRush/README.md`）。
- 界面精简：删掉战斗结算界面的「任务」按钮（剧情 / 练习 / 排位 / 双选 / 房间等所有模式通用），并隐藏解谜挑战界面右上角的「解谜任务」按钮。卡组页的「比赛精选牌组」一整排、商店的购买卡套 / 购买角色皮肤 / 购买道具 / 交换临时卡牌 / 购买预组卡牌（连介绍条和维护提示牌）、「其他」页的排行榜与咨询弹窗里的「删除账号」则是**变暗不可点**、保留占位。首页那些走不通的入口（礼物 / 任务 / 公会 / 通行证 / 活动框 / 主副横幅）仍然直接隐藏，完整清单见 `HomeMenuPatches.cs`、`MyPageOtherPatches.cs` 文件头的注释。
- 货币修改：「其他」页新增一个与「设定」同款式的按钮，点开可以直接改金币 / 以太 / 入场券数量并持久化（见下面「货币修改」）。
- 数据全在游戏目录：资源、日志、`PlayerPrefs`（原本在注册表 `HKCU\Software\Cygames\Shadowverse`）统一搬到额外资源目录，整个包可以原样搬去别的电脑。
- 卡牌 Mod：修改或新增卡牌，并支持自定义卡图与文本。
- 卡组列表热重载：进入卡组列表时重新加载 `CardMaster` 配置。**只有 `Mods` 下的文件真的改动过才会重建** —— 重建会把卡表里的 `CardParameter` 全部换成新实例、并清空插件的卡图与语音注册表，界面上正在显示的卡会因此掉图（表现就是「改一下卡牌数量又好了，接着又轮到另一张」），而且每次都要几百毫秒到几秒。
- 卡名可点击：`localizationFields` 改过的卡名会注册为卡牌关键字，在卡牌描述中点击即可查看该卡详情。
- 卡牌列表排序：牌组编辑的搜索框与卡牌图鉴按费用升序排列，同费用保持原版顺序。
- 主动技能：使用 `when_activate` 为场上随从添加“启动”按钮，可设置 PP 消费。
- 自定义技能：复制卡牌信息与能力，或在保留自身能力的同时获得目标能力。
- Socket.IO 房间对战：房主启动内嵌 Socket.IO 节点并生成加密连接码，另一名玩家粘贴连接码后通过原版实时网络加入房间。

目前仅支持无限制卡组，AI 强化仍在开发中。

## 安装

### 使用成品包

从[百度网盘](https://pan.baidu.com/s/1XFHgqPeRUskWGKOZ0Wnilg?pwd=kbga)下载 BepInEx 和插件压缩包，解压到 Shadowverse 游戏根目录。

> 提取码：`kbga`
> 网盘链接如失效，请到 [Issues](https://github.com/2581374794/Shadowbus/issues) 反馈，或按下文「手动安装」自行搭建。

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
   └─ CardMaster/
      ├─ <卡文件夹>/        一张卡一个文件夹：json + 卡图 + 语音都在里面
      └─ Reference/         卡名与技能参考表（自动导出）
```

整合包根目录（游戏文件夹旁边）通常长这样：

```text
Shadowbus/
├─ Shadowverse/           游戏本体（文件夹名可以改）
├─ Resources/             资源文件夹，见下文
├─ Shadowbus启动器.bat     便携启动器（相对定位，改名换盘都能用）
└─ 模组文件夹.bat          打开 Mods/CardMaster
```


## 资源目录（Resources）

游戏原本把下载下来的资源写在系统目录 `%USERPROFILE%\AppData\LocalLow\<公司名>\<产品名>`（本游戏是 `Cygames\Shadowverse`）。这个目录由 Unity 在启动时自动创建，和游戏装在哪个盘无关。Shadowbus 会把游戏里所有读写资源的位置都改到「额外资源目录」，按下面的顺序查找，**第一个存在的就用它**：

1. BepInEx 配置 `[Resources] Root`（`BepInEx/config/08c8e386-….cfg`）：绝对路径，或相对游戏目录的路径，放哪个盘、叫什么名字都行；
2. 环境变量 `SHADOWBUS_RESOURCES`（同上）；
3. 游戏目录同级的 `Resources`（默认，也是最推荐的放法）；
4. 游戏目录下的 `Resources`；
5. 游戏目录下的 `Shadowbus\Resources`（早期版本的放法）。

玩家只要把别人的资源文件夹整个复制过来放在游戏目录旁边，**不需要原版资源目录、也不需要做任何目录联接**，直接就能玩；游戏启动之后才把资源文件夹放进来也能认（插件会在下一帧补上重定向）。一个都没找到时，插件完全不动游戏行为。

资源目录里由游戏决定的子目录是 `a/`（资源包）、`b/`（BGM）、`s/`（音效）、`v/`（语音，`v/t` 是剧情临时语音）、`m/`（影片）、`f/`（字体）、`manifest/`（各资源清单）、`cardmaster/`、`recovery/`。**移植进来的官方素材也统一放在资源目录里**，按语言分开、各自独立：

| 目录 | 内容 |
| --- | --- |
| `<资源根>/story_text/{chs,cht}/` | 剧情正文 / 人名 / 章节概要 / 影片字幕包（各 1317 个） |
| `<资源根>/text/{chs,cht}/` | 卡名 / 卡牌说明 / 卡面记述 / 卡牌效果 / 关键词 / 表情台词等 50 张文本表 |
| `<资源根>/LeaderSkins/<皮肤号>/` | 国服独占主战者的补图（PNG）与表情 CSV、`imported_leaders.json` |
| `<资源根>/story_ai/{deck,style,emote}/` | 从本体导出成纯本地文件的官方 AI 数据（见下面「剧情特殊战斗」） |

游戏自己按当前**文本语言**从对应的语言目录取，两套互不影响；`Mods` 只放玩家自制的模组内容（官方数据不进 `Mods`）。

被接管的不只是资源本身：游戏里所有 `Application.persistentDataPath` 的取值（一共 18 处调用）以及启动最早期就已经存进静态字段的路径，一律指向这个资源目录，包括卡图/语音/影片/manifest、回放目录 `NewReplay` 与 Record、HTTP 下载缓存、统计日志 `accumulate_log` 等、`NGUITools` 存档、游戏自带的 CardMaster 导出目录、首页特殊称号的 BGM 检查。Unity 原生写的 `Player.log` / `Player-prev.log` 走引擎的 C++ 层，托管代码改不了它的路径，所以另外两条路都通到资源目录：
>
> - **`Shadowbus启动器.bat`**：启动时加 `-logFile "<资源目录>\Player.log"`（目录优先级：环境变量 `SHADOWBUS_RESOURCES` → 游戏目录同级的 `Resources`（插件的默认资源根）→ 游戏目录下的 `LocalLow\Cygames\Shadowverse`；路径全从找到的游戏目录推导，任何盘符/文件夹名都适用）。
> - **系统目录联接**（推荐，一次做完就与启动方式无关）：把 `%USERPROFILE%\AppData\LocalLow\<公司名>\<产品名>` 整个联接到资源目录（`Resources\游戏资源链接工具.bat` 选 1）。Windows 上的目录联接对游戏完全透明，所以**不管用启动器、Steam 还是直接双击 exe**，`Player.log` / `Player-prev.log` 都落在资源目录里，不会再回到系统目录。**2026-09-23 已在本机迁移**：`C:\Users\panqiushi\AppData\LocalLow\Cygames\Shadowverse` → 联接 → `D:\Games\Shadowbus\Resources`（迁移前系统目录里的 7 个文件都搬进 Resources，名字带 `.localow-20260923-195114` 后缀；更早那份 9/18 的 Player.log/Player-prev.log 改名成 `*.old-20260923-195131`）。换一台电脑/重装后跑一次那个工具、或者用启动器启动一次即可。

> 系统目录里原先会出现的 `accumulate_log` / `inquiry_log` / `last_accumulate_log1` / `last_accumulate_log2` / `setting_info_log` 这 5 个文件已经被插件重定向到资源目录，只剩上面两个引擎日志（联接之后这两个也在资源目录里）。

注册表里的设置也一起搬了：Windows 上 Unity 的 `PlayerPrefs` 存在 `HKCU\Software\Cygames\Shadowverse`，插件把游戏里所有 `PlayerPrefs` 读写重定向到 **`<资源根>/PlayerPrefs.txt`**（`PlayerPrefsRedirect`，2.5.6 起）。注意不能只给 `Wizard.PlayerPrefsWrapper` 打补丁——它太短，Mono 会把方法体内联进调用方，绕开补丁；插件是连**调用它的地方**一起换掉的（实测拦下 415 处）。注册表里的旧值不会丢——第一次读到某个键时先回落到注册表读一次、顺手记进本地文件（惰性搬家），之后插件只读不写。唯一还会出现在注册表里的是 Unity 播放器原生层自己维护的几个键（`unity.player_session_count` / `unity.player_sessionid`，以及改分辨率时的 `Screenmanager *`），那是 C++ 层直接写注册表的，托管代码拦不住。也就是说：把 `Resources` 整个复制过去，设置 / 日志 / 回放就都在包里了。

可选工具（都不需要管理员权限）：

- `Resources\游戏资源链接工具.bat`：把系统目录整个联接（junction）到当前资源文件夹，让系统层看到的路径也统一。它会自己找游戏目录，并从 `Shadowverse_Data\app.info` 读出公司名/产品名来算出目标路径，所以游戏文件夹改名、换盘、公司名不同都能用；已有联接、空文件夹、非空文件夹分别处理（非空会先改名备份，绝不删数据）。
- `Shadowbus启动器.bat`、`模组文件夹.bat`：用 `%~dp0` 相对定位，并在自己所在目录、子目录、再下一层子目录里按 `Shadowverse_Data\app.info` 找游戏，改名换盘都能用。**不要用 `.lnk` 快捷方式随包分发**——快捷方式内部存的是绝对路径，换机器就失效。

资源目录完整性可以用仓库外的 `_tools/verify_resources.py` 核对：它把每张 manifest 逐条映射到 `a/`、`b/`、`s/`、`v/`、`m/` 等子目录检查文件是否存在。当前这套资源 14 张 manifest 共 4 万余条**全部齐全（0 缺失）**，并且 `class_chara_master` 的 418 个角色里只有 34 个没有独立表情表（这些角色本来就没有）。

剧情战斗是否「完全离线可用」用 `_tools/verify_story_battles.py` 核对（逐章检查战场/BGM/双方主战者/敌方职业/敌方 AI 三份 CSV/`_ai` 引用，并统计 `story_ai/` 覆盖率与**牌组里的卡是否都在本地卡表里**）：当前 **424 章全部 0 问题**；267 套官方剧情 AI 牌组每套正好 40 张，用到的 1733 张基础卡本地一张不缺。
### 剧情特殊战斗（可选）

有些章节的战斗在官方是脚本战（开局先后手、双方初始 PP/生命、附加技能、特效覆盖、结算跳过等），这些值只存在于服务器的 `StoryStartTask` 响应里（客户端字段 `special_battle_setting`），客户端与资源包里都没有。离线时插件返回空表，表现就是「能打，但只是普通 AI 战，官方那些特殊效果不生效」。

进这类章节时日志会直接给出需要补的文件名：

```text
[Offlinizer] Story 2013800 has no local special battle setting; ... Optional: put the official
special_battle_setting object in Mods/StorySpecialBattles/2013800.json.
```

把官方那份 `special_battle_setting` 放进该文件即可（官方回放里存着同一套字段，前缀 `story_special_battle_` 去掉就是键名）。同一个文件还能带两段修正：

- `_chapter`：修正离线生成的章节级字段 —— 玩家主战者（`player_chara_id`）、敌方主战者、敌方职业、战场 3D 背景、BGM、以及表情表变体号（`player_emotion_variation` / `enemy_emotion_variation`）。这些值现在**由官方表自动生成**（见下面「官方章节表与自动生成」），一般不需要手填。敌方职业**以 `_chapter.enemy_class` 为准**——它就是服务端下发的 `story_master_list.enemy_class`，客户端直接拿来当 `BattleSettingBaseData.EnemyClassId`；只有在没写的时候才退回「敌方主战者自身的职业 → AI 牌组主职业」的顺序。剧情里大量复用同一个杂兵模型（`class_chara_master` 里固定一个职业）去打别的职业，让模型职业优先反而会改错官方值。
- `_ai`：用 `Mods/AIData/deck|style|emote` 下的本地 CSV 完全接管敌方 AI（牌组 / 风格 / 表情 / 逻辑等级 / 初始生命），本地没有官方 AI 数据也能离线还原脚本战。

字段表、示例、CSV 列格式和来源见 `Mods/StorySpecialBattles/README.md`。已知：肃清篇 38 章 = `2013800`，肃清篇 8 章 = `2050800`。

插件同时会把实际读到的本地剧情脚本导出到 `Mods/StoryScenario/`。战斗 BGM（`bgm_id`）与战场 3D 场地（`battle3dfield_id`）现在都来自官方章节表；官方值里的 `"0"` 表示「沿用本章自己的 BGM / 不覆盖」，不是文件缺失。想换就写 `_chapter.bgm_id` / `_chapter.battle3dfield_id`。

### 官方章节表与自动生成

国服客户端的章节数据（`main_story/info`）每章一条，含 `battle_exists`、`enemy_chara_id`、`enemy_class`、`enemy_ai_id`、`battle3dfield_id`、`bgm_id`、`special_battle_setting_id`，以及完整的 `battle_settings`（`deck_class_id` / `skin_id_override` / `deck_skin_id_override` / `player_emotion_override` / `enemy_emotion_override`）。整张表已导出成纯本地文件：

- `<资源根>/story_ai/story_battles_official.csv` —— 官方 20 个篇章、**424 章战斗**的原始值（每个战斗章每个可玩职业一行；`main_story/info` 的 `battle_settings` 本来就是**一章一个数组**，多职业章节会有多条）
- `_tools/build_official_table.py` —— 从 `_tools/yzs_api/section_*.json`（抓包结果）重建上面那张表
- `_tools/build_story_battle_data.py` —— 读那张表，生成 `Mods/StorySpecialBattles/<story_id>.json` 的 `_chapter` 段和 `<资源根>/story_ai/emotechara/_chapter_variations.csv`；`--report` 只分析不写盘
- 本地 story id = `section×100000 + deck_class×10000 + chapter×100`（章节带字母后缀时再加 `×10`），与 `Data.CreateStoryId` 一致

生成结果：**424 份 `_chapter`**（与本地 `story_scenario_param_*_2` 战斗脚本数量 1:1 对齐，0 遗漏）、**359 条表情表变体钉住**（玩家 114 / 敌方 356）。钉住只在对应的 `emote_chara_<主战者>_<变体>.csv` 本地确实存在时才写，其余保留按篇章推导的值——当前敌方表情表覆盖 356/424，剩下 68 章的表属于「国服有、本地资源包没下到」。顺带把表情表从 137 个补到了 **615 个**（`Resources/a` 里有 875 个 `master_emote_chara_*`，导出脚本按「剧情语音前缀 + `_chapter` 里出现的主战者」收集）。

`skin_id_override` 是官方里真正上场的主战者（`BattleSettingData.GetPlayerCharaId` 优先用它，覆盖章节的 `chara_id`），选人菜单里的那个 id（比如 `500701`）本身没有表情表；所以生成器把官方主战者写进 `_chapter.player_chara_id`，选择角色型篇章的玩家侧表情/动作才能对上。

**`special_battle_setting` 已收到 39 / 42 个设置**（74 份章节文件）：它是服务端策划数据，客户端与资源包里都没有，只能由客户端真正进入那一章时从 `main_story/start` 的响应里拿到。两条取法都在 `_tools` 里：`harvest_story_settings_from_captures.py`（从抓包里直接收割，玩家正常游玩即可）和 `yzs_fetch_story_settings.py`（按客户端原样重放该接口，按设置 id 去重；**注意服务端有限流，间隔别低于 10 秒**）。生成脚本只覆盖 `_chapter`，**原样保留**已有的正文。

> 还差 3 个：`设置 40/41/42` = 第 20 篇第 32/35/38 章。要先通关第 24 章，而第 24 章在国服客户端里有一场演出崩溃（`CharacterManager.CreateCharacter` 拿到 null prefab），服务端对这三个 `main_story/start` 也一律回 `500`（进度没到）。

> ⚠️ 离线响应里，**没有正文的章节必须返回空对象** `data["0"] = {}`。客户端
> `StoryStartTask.Parse` 用 `jsonData.Count == 0` 判断"这章没有服务端特殊战斗覆盖"，
> 之后才直接索引 `player_first_turn` / `id` / `result_skip` 等十几个键。若写成
> `{"special_battle_setting": {}}`，客户端会走进取值分支并抛 `KeyNotFoundException`，
> 表现就是**这一章点了进不去战斗**。`Mods/StorySpecialBattles/README.md` 里有完整键表。其余 73 章带 `special_battle_setting_id` 的战斗暂时按「无服务端特殊覆盖」运行，行为与之前一致。

这 73 章的正文**目前取不到**（已实测）：服务端只接受已解锁章节——`main_story/get_deck_list` 对已解锁的 `649` 返回 `result_code=1`，对未解锁章节返回 `500`；而官方表里 677 章只有 62 章 `is_released=true`，带 `special_battle_setting_id` 的 74 章中**只有 `649` 是已解锁的**。原因是剧情进度由服务端记录，而离线 mod 让所有章节都能直接玩，服务端因此没记录过进度。要批量拿就得先伪造通关（`main_story/finish`），不做。工具 `_tools/yzs_fetch_story_settings.py` 已备好（按设置 id 去重，41 个请求覆盖 73 章，遇到非 1 结果码立即停止），账号真解锁后可直接 `--apply`。

抓包/复现这套接口的工具在同级 `_tools`：`yzs_h2proxy.cs`（HTTP/1.1+HTTP/2 反向代理，按 SNI 动态签证书）、`mitm_setup.sh`（设备侧装/卸系统信任库与 hosts，可一键还原）、`mitm_decode.py`（解密抓到的请求/响应）、`yzs_param_probe.py`（校验签名公式：`PARAM = SHA1(udid + "/index.php/<路径>" + base64(msgpack(params)) + viewer_id)`）、`yzs_replay.py` / `yzs_story_scan.py`（按客户端原样重放只读接口并批量取表）。

另外，官方剧情敌方 AI 的牌组 / 风格 / 表情已经用 `_tools/export_story_ai.py`（配套 `_tools/unityfs_extract.py` 解 UnityFS + LZ4 资源包）**导出成纯本地 CSV**，统一放在**资源目录**里：`<资源根>/story_ai/{deck,style,emote}`（约 590 KB，267/82/40 个文件），剧情战斗因此完全离线可用。这是**官方资源**，所以不进 `Mods` —— `Mods/AIData` 继续只放玩家自己写的模组 CSV；`_ai` 引用文件时先查 `Mods/AIData`，再查资源目录的官方那份，同名可用 Mods 覆盖。详见 `Mods/AIData/README.md`。

#### 台词 / 表情 / 动作（章节表情表）

官方的表情、动作、语音、台词是按**篇章**分开做的：`emote_chara_<皮肤>_<变体>.csv`。`<皮肤>`（不带后缀）那张是通用占位表 —— **动作列为空**（客户端回落 `idle`，等于没动作）、`text_id` 指向不存在的通用 id（界面把原始 id 当台词显示，例如 `ET_進化2_500211`）。变体表才把脸/动作/语音/ST 台词填齐（例如 `500211_3` 的第 20 篇表里 `進化2 → motion=4 + ET_ST_進化2_500211_09_01`）。

客户端的选择方式是 `BattleSettingData.GetEmotionId(charaId, variationId)`：`variationId=0` → 通用表，非 0 → `<皮肤>_<变体>`。所以插件现在会：

- 从 `_tools/export_story_ai_emotes.py` 导出的 `story_ai/emotechara/_variants.csv` 里按**当前篇章**选变体（变体号是从官方表自身的 ST 台词后缀 `_09_01` / `_20_01` 推导出来的，不是手填），写进章节数据的 `player_emotion_override` / `enemy_emotion_override`；
- 自定义练习里选「剧情 AI」时同样处理：用剧情 AI id 反查它属于哪一篇（官方 AI id 就是 section/class/chapter 编出来的），再给双方设对应的表情表；
- 万一某章的官方表和篇章对不上，可以在 `_chapter` 里用 `player_emotion_variation` / `enemy_emotion_variation` 手工钉住。

**查不到文本时故意保留原始 id 显示**（不作"空串兜底"）：`ET_進化2_500211` 这种字样就是"这里有问题"的可视提示，配合日志里的 `emoteTable=player=2515_1, enemy=500211_3` 一眼能看出用的是哪张表。日志把缺失分成两类，**判据是官方文本表里到底有没有这个 id**（不是靠 id 前缀猜），避免刷屏也避免漏报：

- **官方表里没有（或值为空串）** → 官方缺口（官方客户端同样显示原文，例如 `ET_挨拶_500042`、`ET_進化1_500211` 这类杂兵/小角色的表情文本），**只计数**；
- **官方表里有非空文本却解析不出来** → 说明读到了通用占位表（选表出问题），**逐条警告**（同一次战斗上限 20 条）并附一行请求方（`Requested by: …`），不再打整段堆栈。

判定用的 id 集合是资源目录里导出的官方文本表 `<资源根>/story_ai/emotetext/*.csv`（五个语区合计 5298 个 id），首次用到时读一次并打一行 `Loaded N official emote text id(s)…`；**只有值为非空的行才算"官方定义过"**——官方表里值为空串的条目客户端查到空值会当成没有、原样返回 key，那同样是官方缺口（Chs 表 5298 条里正好 9 条：`ET_心配_301..307`、`ET_心配_403/404`）。表读不到时退回"`ET_ST_*` 算官方缺口"的旧规则，不会把真问题吞掉。

进战斗时汇总成一行：`[StoryAI] Emote text: resolved=…, official-gap=…, misconfigured=… (examples: …), late-resolve=…`（`late-resolve` = 先查失败、之后又查成功的条目数，用来判定"文本表还没就绪就被冻结"的加载时序问题；实测为 0，当前不存在该问题）。其他章节是否也能按同一流程修好，用 `_tools/verify_story_emotes.py` 核对。

### 剧情临时语音（`v/t/`）

剧情语音是按需下载的临时音源（资源目录的 `v/t/*.acb`），离线包一般没有，所以那几章静音。想自己下下来随时打包分发，就把配置里的开关打开：

```ini
[Resources]
AllowTemporaryVoiceDownload = true
```

打开后游戏会像官方客户端一样向服务器要清单并下载，ACB 落在资源目录的 `v/t/` 下。**官方服务器已关闭时这个开关没有用**（只会白等/超时），保持默认的 `false` 更合适；缺的语音只能从别人已下载好的同语言 `v/t/` 目录拷过来。

### 剧情文本简繁切换（`<资源根>/story_text/`）

剧情正文、角色名、章节概要、影片字幕全在 `storylang_scenario_text_*.unity3d`（正文 / 人名 / 概要）和 `storylang_movie_subtitles_*.unity3d`（影片字幕）里，每个包里一份 TextAsset。客户端**一次只下载一种语言**的版本。

剧情文本包**已经从 `a/` 里挪出来**，改成按语言分目录放（同一批包名，**字节原样，不用重打包**）：

```text
<资源根>/story_text/chs/storylang_scenario_text_1_10_1.unity3d     ← 简体
<资源根>/story_text/cht/storylang_scenario_text_1_10_1.unity3d     ← 繁体
<资源根>/story_text/<chs|cht>/storylang_scenario_text_name.unity3d
<资源根>/story_text/<chs|cht>/storylang_scenario_text_summary_6.unity3d
<资源根>/story_text/<chs|cht>/storylang_movie_subtitles_story_0_prologue.unity3d
...
```

两个文件夹各 1317 个包 / 4.65 MB，`a/` 下**不再保留副本**（`a/` 里只剩 `storylang_scenario_param_diff_*` 参数表和 `storylang_tutorial_*`、`storylang_stt_*` 这两组图）。

**`chs/` 里装的是国服（网易）的简体译文**：原来的简体是国际服自己那套翻译（还混着繁体字形），和国服差别很大——逐包比对 1317 个包里有 **1160 个**内容不同（例如 `亞里莎`→`亚里莎`、`此處為次元的夾縫之間`→`此处乃是次元的狭缝`）。现在 `chs/` 的 1160 个包已经用国服客户端的文本重建（`_tools/build_story_text_bundles.py`），`cht/` 保持原来的繁体那套不动。国服客户端本身就是 2022 格式、引擎读不了，所以做法是把国服文本**灌回**原来那个 2020 包壳里（跟主战者 spine 一样的一次性手术），每个包仍然带自己独立的内部 archive 名。

游戏算剧情包路径只有一条路（`Cute.AssetHandle.BuildLocalCachePath()`，`AssetHandle/<_Load>` 用它 `AssetBundle.LoadFromFile`、`AssetManager.LoadObject` 用它 `File.Exists`），插件就在这条路的出口按**当前文本语言**（`Cute.CustomPreference.GetTextLanguage()`，也就是设置里切的那一项）改路径，查找顺序是：

1. `<资源根>/story_text/<语言码小写>/<原包名>`（`Chs`→`chs`、`Cht`→`cht`、`Eng`→`eng`……）；
2. 没有就退回 `story_text/chs/`（简体）；
3. 都没有就交回游戏原路径。

所以：

- 语言选**简体中文** → `story_text/chs/`；
- 语言选**繁体中文** → `story_text/cht/`，**正文 / 人名 / 概要 / 影片字幕一起切**；
- 其他语言（Eng / Kor / …）→ 先看有没有同名目录，没有就用简体那份（和以前 `a/` 里的行为一致）；
- 想再加别的语言，照着放一个 `<语言码小写>` 目录即可，不用改插件。

切换方式：「其他」页 → 语言切换 → 文本语言选「繁體中文」→ 游戏自己重启（`SoftwareReset`），重启后进剧情就是繁体；日志里会有 `[StoryText] Text language is 'Cht'; story text is looked up in …\story_text → cht, chs.` 和 `[StoryText] Served 'storylang_…' from …` 两行可以确认接上了。

两套文本用 `_tools/collect_story_text_bundles.py` 收集（各 1317 个包、4.67 MB；`--move-source` = 复制后核对 sha256 一致再删源文件，也就是**真转移**）：

```text
python collect_story_text_bundles.py --lang cht                                     # 繁体 ← 反和谐汇总/备份_剧情文本繁体/
python collect_story_text_bundles.py --lang chs --source deployed --move-source     # 简体 ← Resources/a/（挪走）
```

**不收** `storylang_tutorial_how_to_class_*`、`storylang_stt_loop_sneak_*`——它们里面装的是图（Texture2D），两套逐字节一致；也不收 `storylang_scenario_param_diff_*`（参数表，不是文本）。国服客户端有一部分章节（第 1～6 篇为主）的原文包本来就是日文开发占位（正文以 `dummy` 开头的空壳），这些包各语言下一样、实际也不显示，脚本照原样收集，不影响。

想退回「剧情文本也放 `a/`」的老样子：把 `story_text/chs/` 里的包复制回 `<资源根>/a/` 即可（`cht` 留着也不影响，插件只是优先用语言目录）。`_tools/verify_resources.py` 已经认识这个新位置，所以搬走之后它照样报 `storylang_assetmanifest missing=0`。

### 文本表：简体 / 繁体两套独立存放（`<资源根>/text/`）

剧情以外的所有文本表都走同一条入口：`Master.LoadLocalizeJsonAndParseWithRegion(dic, region, fileName, isTrimKey)` —— 把某个语言区段的 JSON 灌进一张字典，区段名就是当前文本语言（`Data.SystemText.RegionCode = CustomPreference.GetTextLanguage()`，见 `Master.StartLoadCardNameText` 等 50 处调用）。

插件的 `CnTextOverrides` 就挂在这条路上：解析完之后把**当前语言那一份**盖上去。两套数据在资源目录里并列存放、各自独立，和 `story_text/` 一个路子：

```text
<资源根>/text/chs/cardnametext.json     ← 国服（网易）译文，来自国服客户端
<资源根>/text/chs/skilldesctext.json    ← 卡牌说明 / 效果（9442 条）
<资源根>/text/chs/flavourtext.json      ← 卡面记述（8927 条）
<资源根>/text/chs/emotetext.json        ← 表情台词（5288 条）
<资源根>/text/chs/battlekeyword.json    ← 能力关键词（6290 条）
… 共 50 张表 51802 条
<资源根>/text/cht/<同名表>.json          ← 国际服原有繁体（49 张表 51205 条，从游戏本体导出）
```

目录名就是语言目录小写（`Chs` → `chs`、`Cht` → `cht`），**只有玩家在设置里选了简体中文时才会覆盖**：

- **文本语言 = 简体中文** → 用 `text/chs/`（国服译文）；
- **文本语言 = 繁体中文或其它** → 一个字都不覆盖，游戏显示自己的原文（繁体保持原版单机繁体）；
- 判断依据是存档里的 `LANG_SETTING`（语言切换时写进去的那一项）。**不能**用游戏正在解析的语言区段来判断：这个客户端切到繁体之后，文本表仍在解析 `Chs` 区段，照区段判断就会把繁体界面里的文本换成国服译文（「先谋」变成「激奏」就是这么来的）。

`text/cht/` 那份原版繁体照样留着、可以单独改（`_tools/build_text_data.py --lang cht --region Cht --source pc` 重新导出），只是默认不参与覆盖。改哪套就只动哪个目录；想让某张表回到游戏原样，删掉那个 json 即可。数据生成：

```text
python build_text_data.py --lang chs --region Chs --source cn   # 简体 ← 国服客户端
python build_text_data.py --lang cht --region Cht --source pc   # 繁体 ← 游戏本体 a/
```

两套的差别很大：卡名 3936/5356、说明 8107/9442、记述 8246/8927、表情台词 3323、关键词 2356/6290 与国服不同。**繁体的那套就是游戏原本的繁体**，一个字节都没改（例如关键词 `Battle_keyword_Title_0527`：国服简体与繁体都是「激奏」，而国际服简体原作是「先谋」—— 所以简体换成国服译法后，这个键和繁体看着一样是正常的，不是繁体被改了）。

> 两套文本表都只在装了资源文件夹时生效；`text/` 下没有对应语言目录时插件什么都不做。

表情表包 `a/master_emote_chara_*.unity3d`（每个角色一张：情绪 → 脸/动作/语音/台词 id，875 个）是**结构性**数据，不带正文——台词正文在 `emotetext` 表里按语言取，所以它只需要一份，仍然放在 `a/`。国服客户端那 875 张的结构与本地同源（文本 id 有 96% 在本体表里存在），因此这份包直接用国服的 CSV 重建（`_tools/rebuild_emote_bundles.py`），语音 id 与 `text/chs/emotetext.json` 对得上。重建时要改三样东西：TextAsset 的名字与内容、`m_Container` 路径、以及 `m_Name` **和** `m_AssetBundleName`——引擎认包看的是后者，只改前者的话 875 个包全都自称是那个壳包，日志会刷满 `AssetBundle '壳包名' was already unloaded.`（主战者 spine 那 7 个包同理）。

### 解谜（`basic_puzzle/*`）

解谜的战斗内容**本来就全在客户端自己的 master 里**：`master_puzzle_data`（113 题的角色皮肤/语音等）→ `master_puzzle_battle_data`（棋盘、手牌、PP、胜利条件），文本在 `master_puzzletext`（九语区），缩略图在 `ui_puzzle_thumbnail_*`。**服务端只提供分组和通关状态**，一共三个接口：

| 接口 | 客户端任务 | 作用 |
| --- | --- | --- |
| `basic_puzzle/info` | `PracticePuzzleInfoTask` | 谜题分组列表（每组含 `puzzle_master_id` / 标题文本 id / 组内谜题） |
| `basic_puzzle/open_puzzle_dialog` | `PracticePuzzleListTask` | 某一组的谜题与难度（请求带 `puzzle_master_id`） |
| `basic_puzzle/mission` | `PracticePuzzleMissionListTask` | 解谜任务与奖励 |

离线数据放在 **`<资源根>/puzzle/`**（官方数据不进 `Mods`）：

- `puzzle_info.json` —— 国服 `basic_puzzle/info` 的分组原样数据（25 组 / 113 题）
- `puzzle_mission.json` —— 解谜任务（19 条）

用 `_tools/build_puzzle_data.py` 从抓包结果生成；脚本会把**所有谜题标成已通关、所有任务标成已达成**（与离线剧情把 `is_released` 置真同一个思路），所以离线进去就能直接打任意一题，不会再有"未解锁"。`open_puzzle_dialog` 的响应由 `puzzle_info.json` 里对应那组现算，不需要额外文件。

插件的 `PuzzleOfflineData` 按**任务类型名**接管这 5 个任务（`PracticePuzzleInfoTask` / `ListTask` / `MissionListTask` / `BattleStartTask` / `BattleFinish`），不引用客户端的解谜类型，客户端换版本也不会把插件编译带崩。以前解谜入口（练习菜单里的 `_practiceBattlePazzle`）因为离线数据是空的被隐藏，现在已恢复。

解谜挑战界面右上角的「解谜任务」按钮（`PracticePuzzleUI._missionButton`）由 `HomeMenuPatches` 隐藏，所以 `basic_puzzle/mission` 与 `puzzle_mission.json` 虽然仍然保留并会被离线应答，界面上没有入口再打开它；想恢复删掉那条补丁即可。

### 货币修改（「其他」页）

「其他」页原来「游戏指南」的位置现在是一个克隆自「设定」的按钮「**货币修改**」（同 prefab、同大小同材质）。点开的弹窗用的是**「咨询 → 咨询」那个二级弹窗**（`UIManager.SupportDialogPrefab`，行与行之间自带下划线）：取它前三行改成 **金币 / 以太 / 入场券**，每行右侧一个输入框（输入框来自「删除账号」弹窗里的 prefab），第 4 行起自带的说明文字、勾选项和「New Label」占位连同下划线一起收起来。填好点「保存」立刻生效——金币靠 `UIManager.UpDateRupyNum()` + `MyPageMenu.UpdateRupyCount()` 当场重画顶栏，水晶用 `UpDateCrystalNum()` / `UpdateCrystalCount()`——并写进 `Mods/Profile.json`，重启后仍然是这个数。

| 行 | 游戏内数据 |
| --- | --- |
| 金币 | `PlayerStaticData.UserRupyCount` ← `LoadDetail._userCrystalCount.rupy` |
| 以太 | `PlayerStaticData.UserRedEtherCount` ← `LoadDetail._userCrystalCount.red_ether` |
| 入场券 | 道具 id 1（挑战券）→ `LoadDetail._userItemDict[1]` |

同一页还做了三件事：「游戏指南」挪到原「入场券兑换券一览」的位置、「入场券兑换券一览」挪到原「道具获得履历」的位置、「道具获得履历」整个删掉（它每帧的入场动画会 `SetActive(true)`，所以是从 `_enableOtherButtons` 里摘掉而不只是隐藏）；排行榜和咨询弹窗里的「删除账号」变暗不可点。

### 本地回放（「其他 → 回放」）

游戏自带一套「新回放」机制：`NetworkBattleReplayOperationRecorder` 把一局录成 AES 加密的 JSON 放进 **`<资源根>/NewReplay/<battleId>/`**（`replay_info.json` / `replay_turn_start.json` / `replay_battle_log.json` …，最多保留 30 局）；`ReplayDialogContent.GoReplay` 先找这个目录，**找到就本地播放**（`ReplayController.StartPlayReplay(..., isNewReplay: true, battleId)`），找不到才去服务器要 `ReplayDetailTask`。

> **`persistentDataPath` 的分隔符必须和 Unity 一致（关键坑）**：`GoReplay` 与 `ReplayDataHandler.NewStockDataPlayer` 判定「本地有没有这一局」用的是
> `Directory.GetDirectories(persistentDataPath + "/NewReplay").Select(d => d.Replace("\\", "/")).FirstOrDefault(f => f == persistentDataPath + "/" + battleId)` ——
> 候选路径被规范化成正斜杠，右值却直接拼 **`Application.persistentDataPath` 的原样字符串**。Unity 在 Windows 上给的本来就是正斜杠（`C:/Users/…`），
> 而插件早期把资源目录（反斜杠，`D:\Games\Shadowbus\Resources`）直接交出去，于是这个比较**永远不成立**：本地回放被当成「服务器回放」播
> （`StartPlayReplay(replayInfo, …)`，`isNewReplay=false` → `StockDataPlayer` 等服务器数据），表现就是**卡在换牌界面什么都不播、日志也没有任何报错**。
> `PersistentDataPathRedirect.Current` 现在统一返回正斜杠（`ResourceRootPatches.ResolveResourcePath` 用 `Path.Combine` 后再归一化回平台分隔符），
> 两个判定点都能命中了。教训：重定向 `persistentDataPath` 时要保持 Unity 原生的路径写法，别把别处的反斜杠路径直接塞进去。

插件（`ReplayOfflineData`）目前做了这些：

- **回放列表走本地**：`ReplayInfoTask` 离线时扫 `<资源根>/NewReplay/*/replay_info.json` 拼出列表（最多 30 条、按时间倒序），`ReplayDetailTask` 也改成从本地那一局的 `replay_info.json` 读 —— 所以「其他 → 回放」不会再往服务器发请求，那段 `MessagePack.ToJson` 解密报错也就没有了。
- **离线战斗录像**：AI 战原本拿的是 `NullReplayRecordManager`（什么都不写），插件换成真正的 `ReplayRecordManager`，并补上游戏自己缺的两处：
  1. 开录前先把双方的开局牌组（`BattlePlayer/BattleEnemy.BattleStartDeckCardList`）塞进 `NetworkUserInfoData._selfDeck/_oppoDeck` —— `RecordBattleStartInfo()` 就是从这里取牌组的，AI 战原本是空的（日志里那句 `Could not enable replay recording: Object reference not set…` 就是它）；
  2. 真正写文件的 `BattleFinishWriteJsonData` 只挂在 `NetworkStandardBattleMgr.OnBattleFinish` 上，插件另外挂到 `AINetworkBattleManager.InitiateGameEndSequence(hasWon)` 上。
  另外注意 `BattleManagerBase.JudgeBattleResult()`（原本当成兜底结算用的那个）返回的是个 Vfx 序列，**每次出牌 / 攻击都会新建一次**，战斗刚开始摆完手牌就会触发第一次 —— 所以插件只在「有一方主战者已经倒下」时才认它，否则一局会在开局就被写成空录像。
  离线局没有 battle_id，插件会生成一个 12 位数字 id 当目录名（少于 14 位，不会被录像器自己的“清理没录完的时间戳目录”逻辑删掉）。看回放和观战时不会重复录。
- **开局换牌那两条记录（回放卡在换牌界面的根因，已修）**：录像的 `replay_network.json` 里，回放播放器靠 op 推进 —— `op 0` 发牌（`DealOperation`）、**第一个 `op 1`** 播我方换牌（`SwapOperation`）、**第二个 `op 1`** 收尾并继续对局（`SecondMulliganOperation` → `OnEndMulligan`）。而单机局这两条原版都不录：
  1. `op 0`：原版只在 `NetworkPlayerMulliganCtrl` / `NetworkOpponentMulliganCtrl` 的 `StartMulliganVfx` 末尾调 `CallRecordingMulliganStart`，单机用的是它们的基类 `PlayerMulliganCtrl` / `OpponentMulliganCtrl`，一次都不调；
  2. 敌方 `op 1`：`BattleEnemy.OnMulliganEndForReplay` 只在 `NetworkMulliganMgr.EnemyChangeCardVfx` 里发，单机的 `SingleMulliganMgr.EnemyChangeCardVfx` 只发 `CallRecordingMulligan`（那条 `OnMulliganEnd` 是断线恢复用的）。
  
  结果就是 `isOppoMulliganEnd` 永远是 false、`OnEndMulligan` 永不到达，画面**停在换牌界面不再往下播**。插件把这两个事件在单机路径上补发（`PlayerMulliganCtrl/StartMulliganVfx`、`OpponentMulliganCtrl/StartMulliganVfx`、`SingleMulliganMgr/EnemyChangeCardVfx` 三个 postfix，只在「这一局正被我们录像」时动手），其余照旧交给原版录像器的 `RecordMulliganStart` / `RecordEnemyMulliganReplaceCards`。**修好前录下的录像缺这两条数据，仍然会停在换牌界面**（数据无法补出），重新录一局即可。
- **开关**：配置 `[Replay] RecordOfflineBattles`（默认 `true`，在 `BepInEx/config/08c8e386-a794-442f-a98c-aec65a183898.cfg` 里）。关掉之后整条录像链路不跑，用来排查「战斗加载卡死 / 卡顿」时做 A/B；录像落盘时会打一行 `[Replay] Replay recording finished (win=…, from <触发点>)`，`from` 说明是哪个补丁触发的收尾。
- **`replay_info.json` 里的职业号（`classId1/2`）必须补**：离线局录出来的这份文件里 `classId1/classId2` 是 **0**（`DataMgr.GetPlayerClassId()` 和主战者卡的 `Clan` 都给 0）。列表/预览那一路 `SanitizeReplayJson` 已经夹过，但**播放读的是磁盘上的文件**：`ReplayController.ParseReplayData` → `SettingSelfInfo` → `OnReplayReady()` 取 `GetPlayerClassId()`（=0）→ `Matching.FirstSetting` → `StartBattleLoad` → `DataMgr.GetClassPrm(0)` 直接 **KeyNotFoundException**，对局起不来。现在两处都补：读文件时（`NewReplayBattleMgr.ReadJson` 的 postfix）职业非法就从牌组里数量最多的职业推；录的时候（`BuildBattleUserInfo`）也按同样规则补一次，新录像不再带 0。日志 `[Replay] Repaired the recorded class of '…': side1: class=…, chara=…`。
- **`charaId1/2`（主战者）为什么会变成「职业默认那位」（已修）**：录像器写的是 `NetworkUserInfoData.GetSelfCharaId()/GetOpponentCharaId()`，它们读的**不是** `_selfInfo/_oppoInfo` 两个字典，而是 `SelfBattleStartInfo`/`OppoBattleStartInfo` —— 这两个对象只有「看录像恢复」那条路会建（`SetSelfInfo(info, isWatchReplayRecovery: true)`），离线自己打的对局一直是 null，取出来全是 0（同一局录出来的 `subclassId1=0` / `subclassId2=10` 就是证据：null 时 `GetSelfSubClassId()` 返回 0、`GetOpponentSubClassId()` 返回 10）。回放播放时 `Matching.SettingOpponentClassDataAndLoadObject` 走 `GetCharaPrmByCharaId(GetOpponentCharaId())`（=0 → null）→ 再退 `GetCharaPrmByClassId(GetOpponentClassId())`，于是**敌方变成该职业的默认主战者**。现在两层都修：
  1. **录的时候**：`ApplyBattleStartInfo` 按 `NetworkUserInfo.SetParameter` 要的键把这两个对象直接建出来（只反射写这两个属性，不碰 `DataMgr`，避免像原版 `SetNetworkSelfInfo` 那样顺带改玩家职业/副职业/皮肤）；
  2. **读老录像的时候**（录的时候已经是 0，改不回来）：`fieldId` 就是剧情章节表的 `battle3dfield_id`，按它去 `Mods/StorySpecialBattles/*.json` 的 `_chapter` 里反查官方配的 `player_chara_id` / `enemy_chara_id`（还要用双方职业卡一下，并按「双方主战者」去重；分不清就不动，退回职业默认）。**只对剧情局（`deck_format == DataMgr.BattleType.Story`）查**——练习局也能把战场选成剧情用过的背景（实测 `field=71/6/61/7` 的练习回放），光按 `fieldId` 查会把剧情主战者安到练习回放上。日志 `[Replay] Field 76 matches a story battle: player=1908 (class=8), enemy=4528 (class=8)`。
  所以：**这一版之前录的剧情回放**（`charaId=0` 那种）现在打开也能显示正确的敌我主战者；练习 / BossRush 的老录像没有可反查的来源，仍是职业默认主战者，重新录一局即可（新录像直接带真值）。
  补一句：录制那一刻`DataMgr` 也拿不到**剧情敌方**的皮肤号（实测新录像里是 `enemy(class=8, chara=8)` —— 职业对、主战者是该职业默认那位），所以「已经录好的新录像」同样按上面的规则修：**剧情局里 `charaId` 只要等于「该职业默认主战者」就当作待补**，被章节表的值覆盖（只有录到的是别的皮肤时才尊重录像里的值）。
- **回放列表弹窗多了个「打开回放文件夹」按钮**：列表弹窗就是 `ReplayDialog.Create()` 用 `DialogBase.SetButtonLayout(CloseBtn)` 建的那个（标题 `OtherTop_0033`），只带一个「关闭」。插件在 `ReplayDialog.SetupContents` 的 postfix 里往引擎自己的 button2 槽位加一个按钮（`ButtonType.Gray` —— 和「关闭」同一个 `btn_common_01_m_off` 贴图、同一套字号/尺寸），并把两个槽位的锚点对调，让**「关闭」在左、新按钮在它右侧**（引擎的两按钮布局本来就是 button1 在右、button2 在左）。新按钮点一下用 `explorer.exe` 打开录像目录，**不关弹窗**（`isNotCloseWindowButton2 = true`）；`UseShellExecute` 不支持时退回直接起进程。
  路径不写死：`ReplayOfflineData.ReplayRoot` = 运行时解析出来的资源根 + `/NewReplay`（也就是被重定向的 `Application.persistentDataPath`），换电脑/换盘符/换文件夹名都跟着走；目录不存在会先 `Directory.CreateDirectory` 再打开。日志 `[Replay] Added the '…' button next to Close in the replay dialog (folder: …)` 与 `[Replay] Opening the replay folder: …`。
  **按钮名字跟着游戏的文字语言走**（`StoryTextLanguagePatches.ResolveUiText`）：简中「打开回放文件夹」/ 繁中「開啟回放資料夾」/ 其它语言（Eng、Kor…）「Open Replay Folder」；语言读不出来时按简中。换语言后重开这个弹窗就是对应的一版（不用重启）。
- **临时诊断（定位完就删）**：播放回放时 `ReplayPlaybackTrace` 会打 `[ReplayTrace]` 行 —— 播放器实际消费的每一条 op（开头 40 条 + 所有开局 op，带 `type` / `isSelf` / 两个下标表长度）、换牌收尾与阶段拆除、`StartBattle`、谁把回放置成暂停（带调用栈）、以及某条 op 抛出的异常（原先会被静默吞掉）。回放卡住时先看这几行就能确定停在哪一步。
- **剧情回放发牌那步的空引用（已挡）**：`DealOperation` → `SetSkillDescriptionValueList(AllCards, CardInfoList)` → `GetBattleCardIdx` 找不到卡时返回 null → `UpdateSkillDescriptionValueList(null, …)` 里给 `card.ReplaySkillDescriptionValueList` 赋值就 NullReference，整条发牌流程断掉、播放器停在换牌界面（原版这条链没有任何空判断，CardInfo 本身也可能是 null）。插件只补「拿到 null 就跳过」，正常时行为不变。另外录的时候 `charaId` 也不再一律退回「职业默认主战者」：先取 `DataMgr` 的当前主战者，再取**主战者卡自己的卡号**（剧情局就是那位主角的皮肤卡号），最后才是默认。
### 勇气抉择卡（技巧 / 秘术 / 奥义）

剧情里 leader 的「勇气抉择」三个选项在战斗中是当手牌出的，它们的卡号写在 `Mods/OfflinizedTasks/LoadTask.json` 的 `data/avatar_info/abilities` 里（`option:card_id=A:B:C`）。这批卡有一个原版数据缺陷：**每一个「XX的技巧」在卡表里 `Cost = -1`**（秘术 / 奥义都是正常费用）。

战斗日志 `BattleLogManager.SetUp` 只给费用 0..30 建桶，`_AddDestLogCommonOne` 拿 `Cost` 去桶里找，-1 找不到就返回 null 再取 `.LogInfoList` —— 空引用，而且发生在出牌重试链上，**每帧抛一次**，技巧卡因此打不出去、一直半透明挂在手上。`BattleLogPatch` 只在「这个费用没有桶」时跳过那条日志并打一行 `[BattleLog] card <id> has cost -1 …`，其余情况完全走原版（不伪造费用、不改卡表）。

查这批卡的费用可以用 `_tools/ability_card_costs.py`。

**「-1」到底是什么（已查清）**：不是「回 1 费」，而是**回 1 点英雄点**。这批卡是**主战者模式（`Wizard.Format.Avatar = 39`）**的英雄技能卡，卡表 `Cost` 存的是「英雄点变化的相反数」——24 位主战者逐条核对都是 `卡表 Cost == -ability_cost`（技巧 `-1` ↔ `ability_cost=+1` → 英雄点 +1；奥义 `6` ↔ `-6` → 花 6 点英雄点；秘术 `0` ↔ `0`）。两条支付路径行为不同，而且都是原版代码：

- **英雄技能（抉择）路径**：`BattleManagerBase.ReplaceChoiceBraveCard` 造出来的卡 `IsChoiceBraveSkillCard = true`，而 `BattleCardBase.Cost` 对这种卡**故意跳过 `Math.Max(0, …)` 的夹取**，原样返回 `-1`；`PlayCard` 对它走 `UseBp(Cost)` → `Bp - (-1)` = 英雄点 +1。**官方的那个「回」就在这里，回的是英雄点。**
- **当普通手牌打**：`Cost` 最后一行是 `Math.Max(0, num)`，`-1` 被夹成 `0` → `UsePp(0)`，既不扣也不回。所以在自定义/练习这种非主战者对局里，技巧的**实际结算就是 0 费**，这是原版行为（`BattleLogPatch` 打的那行 `cost -1` 读的是 `BaseParameter.Cost`，与实际结算费用本来就是两回事）。

整条主战者系统都被 format 39 挡着：`BattleManagerBase.SetupAvatarBattle` 第一行就是 `if (Data.CurrentFormat != 39) return;`（挂主战者能力、初始化英雄点都在它后面），`md_card_heroskill` 的预载与 `_choiceBraveCardFrameMaterials` 是同一个开关。自定义/练习局是 `deck_format=5`（MyRotation），既没有英雄技能按钮也没有英雄点收支 —— **不需要修**，要验证得把对局开在主战者模式。

核对方法：`_tools/ability_card_costs.py --bad-only`（卡号 ↔ 卡表费用），`LoadTask.json` 的 `ability_cost` 逐条对比即得上表；你自己的离线录像（`Resources\NewReplay\*`，AES 目录里 `replay_network.json` 解密后）里 `RecordPlay` 的 `16`=实际支付费用、`49`=抉择替换卡号、`109`=是否走抉择路径，可以直接确认某局到底走的是哪条路。

**卡图**：这批卡在原版数据里没有自己的卡面，`ResourceCardId` 指向的是别的原版卡 —— 蛟的技巧 → 104412010「龙蛋」、蛟的秘术 → 102424030「龙人的怒拳」、雪华的技巧 → 110114011「精灵环绕」、雪华的秘术 → 107124011「释放本能」，拉蒂卡的技巧（930144040/041）本地则一份卡图都没有。按它们自己的卡号算出来的材质名（`9304440700_M` 之类）在本地资源里**不存在**，所以「显示的是别的原版卡的图」就是原版数据本身的行为，不是插件借错了。

**国服实测一致**（`_tools/audit_choice_card_art.py` 逐张核对：卡号取 `Mods/CardMaster/**` 文件名带技巧/秘术/奥义的 json/example 与 `LoadTask.json` 抉择表的并集，共 71 张；国服一侧用 `_tools/check_cn_choice_card_art.py` / `ls` 国服 `a/` 的完整列表）：**71 张里没有任何一张拥有自己的卡图**（按自己卡号算出的材质名在本地材质索引里全部缺失，国服 `a/` 里 `card_930*` 卡图包总数为 **0**）；其中 **70 张**借用的来源图本地与国服**都**存在（国服包内材质正是 `1044120100_M`（龙蛋）等），唯一例外是**拉蒂卡的技巧 930144040/041** —— 它连借用的来源都没有，两边都没有图（按要求留空白）。也就是说国服只能借用别人的卡图，两边行为一致，**保持现状**。要换成别的图只能另外指定来源（`normalArtCardId` / `spellArtCardId`，或往卡文件夹里放 `card.png`）。

**包内对象路径纠正（卡组列表黑卡用的就是它）**：卡面材质的对象路径只有 `Card/Spell/Materials/` 与 `Card/Field/Materials/` 两种目录，而有些卡「卡的种类」和「图实际所在的目录」在官方数据里就是矛盾的（实测反例：龙蛋 `charType=FIELD`，材质却在 spell 目录）。插件会把「引擎要的目录」换成**实测取到材质的那个目录**（同一个包内换目录，不会多加载包），纠正时打一行 `[CardArt] <材质号>_M is really stored under /Field|/Spell/Materials/; corrected the engine's object path …`。**不要**从 `asset not found` 反推目录 —— 那个报错也会因为「包还没加载」而出现，早期版本这么做把本来正确的请求改坏过。

**这个「实测目录」只能来自真正命中的那次查找（2026-09-23 修，手牌紫红的根因）**：缓存 `MaterialKindByMaterialId` 以前是在 `FindCardMaterial` 的 postfix 里按**调用方声明的 `type`** 记的，而 `FindCardMaterial` 对 ≥10 位的材质号**无视传进来的 type**、一律拼 `Card/Field/Materials/`；于是「按声明记」会把明明躺在 field 目录的材质记成 spell。记错的后果不是"没纠正"，而是**把本来正确的请求改坏**：实测「肃清」系列抉择卡（`820144031`/`820244021`/`820344031`/`820444081`/`820544071`/`820644051`/`820744051`/`820844051`）8 张的来源包（`card_121X410300.unity3d`）里**只有 `card/field/materials/`**，其中 `1214410311_M`（粉碎肃清的爪牙）与 `1215410311_M`（拒绝肃清的激情）被改去 spell 目录后 `LoadObject` 找不到 → 手牌**紫红、卡图丢失**（Player.log 里正好最后两行就是这两条的 `corrected the engine's object path`）。
现在改成三层：
1. 目录只由**真正命中的那条对象路径**记录（`CardMasterPatcher.RememberMaterialKindFromObjectPath`，在 `AssetManager.LoadObject` 的 postfix 里看这次查到的是 field 还是 spell），**实测覆盖声明**，记错一次不会一直错；
2. `AssetManager.LoadObject` 加了**换目录兜底**（`ResolveCardMaterialObjectPath`）：按 caller 给的目录没找到时，换 field/spell 另一个目录再找一次，命中就记下真正的目录并打 `[CardArt] '…' is in the other folder; loaded '…' instead`。这样「卡型与目录不一致」**再也不可能让卡面变紫红**，也不再依赖任何猜测；材质/贴图/卡名横幅（`materials`/`textures`/`header`）三对目录都覆盖；
3. `AddArtSourceBundle` 预载借来的卡面时，包名先用 `ResolveArtBundleName` 纠正、磁盘上没有就不加进加载列表（10 位材质号按 `<id>0.unity3d` 算出来的包名本地并不存在，塞进去只会让引擎白跑一趟）。

排查卡面问题的工具：`_tools/find_material_bundle.py`（材质名 → 真正装着它的包 + 包里的对象路径）、`_tools/_bundle_materials.py`（直接列出某个卡面包里 `card/field|spell/materials/` 各有哪些材质，用来验证「材质到底在哪个目录」）。日志里**不再有**临时探针的行（`[ArtProbe]` / `FINAL NO FACE:` 等已随探针一起移除）。

**战斗里手牌透明（技巧/秘术）—— 已修，根因是网格不是卡图**：

- 卡面材质一直取得到（`FindCardMaterial(<资源号>, Spell, …, choiceBrave=true)` 返回带 1024×1024 贴图的材质），**卡图从来不是问题**；
- 引擎在 `CardTemplate` 里对抉择卡有一段专用装配：把卡面网格换成 `md_card_heroskill`、卡框换成 `CardFrame_HS_*`、材质数组填 `[卡框, 卡面, 职业图标]`；
- 而 `md_card_heroskill` / `md_card_heroskill_low` **只在 `Wizard.Data.CurrentFormat == 39` 时才随预载清单加载**（`SBattleLoad` 的 `cardPrefabPathList`，同一开关也管着 `_choiceBraveCardFrameMaterials`）。离线剧情/练习战不是 39 → `GetChoiceBraveCardMesh()` 返回 null → 引擎把卡面 `MeshFilter` 设成 null → **材质数组明明是对的，却没有网格可画** → 看着就是透明；
- 插件的修法：拦 `BattleResourceMgr` / `NullBattleResourceMgr` 的 `GetChoiceBraveCardMesh`，改交**无条件预载**的普通卡网格（`md_card_spell` / `md_card_spell_low`，两者子网格布局一致：卡框/卡面/职业图标）；改完换牌界面、抽牌动画、手牌全程都有卡面。留了一条兜底：卡视图上若还有空掉/仍是 heroskill 的 `md_Card_Spell*` 网格，就补回普通卡网格，并把图层与排序对齐到同一场战斗里普通手牌实测到的值；
- **教训（别再犯）**：不要动引擎选好的 shader（把它换成 `Basic_Front0_Back0_Normal` 会直接渲染成紫红），也不要把卡面材质挂到 `NormalField` / `EvolField`（那两层在手牌里本来就该是未激活的）。

卡组编辑里「卡图变黑、刷新一下又好」的另一半原因在材质缓存：贴图会随资源包卸载被 Unity 销毁，缓存里那条就成了黑卡。现在缓存条目每次命中都会检查贴图还在不在，不在就丢掉重建，而且没有贴图的材质根本不进缓存。

**战斗中途卡面变紫红（材质被回收）**：游戏在进出界面/战斗里会调 `Resources.UnloadUnusedAssets`
（日志里的 `Unloading N unused Assets`），**没有任何托管引用的材质/贴图会被直接销毁**。插件把
「借来的卡面」（另一张卡的素材、按需异步拉进来的素材包）交给引擎之后自己不再引用它们，
于是战斗打到一半卡面消失 —— 材质被销毁的表现就是**紫红**（Unity 缺材质＝品红），
只有贴图被销毁则是一张黑卡（就是上面卡组编辑那个现象）。现在
`CardMasterPatcher.KeepArtAssetsAlive` 把从 `ResourcesManager.FindCardMaterial`
（UI 与战斗取卡面的公共出口）以及借用卡面路径交出去的每一份材质和它的贴图都记在一个静态列表里 ——
静态字段引用算「在用」，回收扫不掉它们。日志里出现 `[CardArt] kept N card-face asset(s) alive`
说明这条链路在跑。

### 剧情「角色选择」界面显示谁

`StoryLeaderSelectTask`（角色剧情选择）原来对每个职业一律返回「该职业默认主战者」，所以所有篇章都显示成默认职业头像。现在按官方数据解析该篇的 leader 列表 —— **角色剧情篇的 leader 不是按职业分的，而是该篇的主角们**（如第 15 篇是 `500701/500732/500704` 三位主角，第 12 篇是 `500401/500402/500403/500404` 四位），优先级：

1. `<资源根>/story_ai/story_leaders.csv`（两列 `section_id,chara_id`，由 `_tools/harvest_story_leaders.py` 从官方抓包里挖出来 —— 最权威）；
2. `<资源根>/story_ai/story_battles_official.csv` 里该篇各章的 `chara_id`，按出现顺序去重（官方关卡表，覆盖 1/3/5/7/10/12/15/17 全部角色剧情篇）；
3. 都没有时才回退「每职业取剧情里的那个人 → 职业默认角色」。

实测与官方 `main_story/leader_select` 抓包逐项一致（第 1 篇 `1..8`、第 15 篇 `500701/500732/500704`）。逐篇核对用 `_tools/audit_story_leaders.py`，它会列出每一篇是不是角色剧情篇、以及手上有没有官方 leader 数据。

另外会跳过 `master_class_chara_master` 里 `is_usable = false` 的角色（剧情专属 NPC）—— 但**只在回退路径上**：官方数据里这些 500xxx 主角号本来就是要显示的，不能过滤。判据来自 `<资源根>/story_ai/class_chara_usable.csv`（两列 `chara_id,is_usable`，用 `_tools/export_class_chara_master.py` 从 `master_class_chara_master` 导出；文件不存在时一律当作可用）。

离线核对用 `_tools/verify_story_leaders.py`（打印每个 section 各章最终会显示谁，以及来源是覆盖/官方表/默认），资源齐不齐用 `_tools/avatar_asset_gaps.py`（`ui_class_<角色号>.unity3d` 在本地/国服各有没有）。

## 联机对战

Socket.IO 房间模式不连接官方服务器，也不需要自己长期架设服务器。游戏运行期间，房主临时监听一个 Socket.IO WebSocket 端点，负责房间流程与实时消息转发。

战斗由房主权威执行。访客只发送输入请求（出牌、进化、融合、攻击、结束回合与各类选择），由房主用原版战斗管理器执行，并回传原版格式的 `PlayActions`、回合切换与结果封包供访客重放。双方在对战初始化时交换房主所需的私有卡牌/状态基线，之后随每次结果发送精简的权威状态变更。该模式面向互相信任的好友对战，不具备反作弊能力。

1. 双方安装相同版本的游戏、Shadowbus 以及卡牌 Mod 数据。
2. 房主在原版房间界面选择标准构筑 BO1 并创建房间。
3. 房主点击复制房间号。剪贴板得到的是以 `SVP1-` 开头的加密连接码，而不是界面上显示的短房间号。
4. 访客把完整的 `SVP1-...` 连接码粘贴到加入对话框的连接码输入框。校验通过后确认按钮才会启用。
5. 双方各自选择卡组并准备，随后通过对战房间流程开始对战。

连接码包含房主地址、Socket.IO 端口、BattleId 与完整性校验。它相当于该房间的密码，请勿公开。房间关闭或游戏退出后，旧连接码即失效。

### 网络要求

P2P 模式不提供账号服务、房间列表、STUN 打洞或 TURN 中继。双方必须满足以下条件之一：

- 双方处于同一局域网，且连接码携带房主的局域网地址。
- 房主拥有可被外部访问的公网 IPv4，并在路由器和系统防火墙中放行所配置的 Socket.IO 端口。
- 双方具备可互相访问的 IPv6，房主把绑定地址与对外公布地址都设为该 IPv6，并放行防火墙。
- 双方先加入 Tailscale、ZeroTier 或 Radmin VPN 等虚拟局域网，连接码携带房主的虚拟网卡地址。

如果房主处于运营商级 NAT 之后且没有可用的 IPv6，则必须使用虚拟局域网；仅凭连接码无法穿透这类 NAT。

首次启动后，可在 `BepInEx/config/` 下本插件的配置文件中编辑 `[SocketIO]` 段：

- `BindAddress` — 房主监听的本地地址，IPv4 下默认为 `0.0.0.0`。
- `AdvertisedAddress` — 写入连接码的地址。留空时优先使用显式配置的 `BindAddress`，否则自动选择一个同地址族的本机地址。跨公网或在虚拟局域网中使用时建议显式设置。使用 IPv6 时两项都必须是 IPv6 地址。
- `Port` — 房主监听的 Socket.IO 端口，默认 `29600`。

目前支持：标准构筑的 Open Room BO1，以及带自定义规则的 Room Two Pick BO1。不支持：HOF、Windfall、Avatar、原版 Backdraft/Cube/Chaos Two Pick、BO3/BO5、观战、奖励与反作弊。

`Mods/TwoPick` 下的每个 JSON 文件对应一种创建房间时可选的二选一模式，以 `displayName` 作为显示名。双方在本地各自选牌，房主把完整规则同步给访客，最终卡组再进入匹配。对战中掉线时，仍在线的一方默认获胜。

每个游戏安装各自在 `Mods/P2PIdentity.json` 保存玩家 ID，并在 `Mods/Profile.json` 保存修改后的名称、称号、徽章与地区。请勿把生成的身份文件复制给其他玩家或第二个测试实例。

### 断线、失焦与对局停滞（2.5.7）

**打联机时请让游戏窗口保持焦点。** 游戏自身在窗口失去焦点时会暂停对局并**屏蔽全部出牌操作**
（`UnityEventAgent.OnApplicationFocus(false)` → `m_battleMgr.Pause()`，所有触摸处理器都判 `HasFocus`），
但 **Socket.IO 心跳、服务器转发、回合计时器都照常运行** —— 切出去不仅不能出牌，还可能被判回合超时输掉。
2.5.7 起插件会在日志里明确写出这件事：

```
[Focus] 游戏窗口失去焦点：对局已暂停，牌桌不再接受任何操作，但回合计时器与网络连接仍在继续……
[Focus] 游戏窗口重新获得焦点：对局恢复，可以继续操作。
```

断线处理（2.5.7 新增/加强）：

- **半死连接回收**：对端悄悄消失（拔网线、掉虚拟局域网、杀进程）时 TCP 不会报错，socket 会一直挂着。
  现在按"最后收到数据的时间"判断：静默超过 **30 秒**不再算该槽位活跃，超过 **45 秒**直接回收并释放槽位。
- **槽位接管**：重连时若旧连接已静默超过 **8 秒**，新连接直接接管同一槽位（不再回 `already connected`），
  并且**不会**把这条被顶掉的旧连接按掉线结算而判你负。
- **结果补发**：结算那一刻正好断线的玩家，重连时会自动补发他那一份结算结果，不再卡在"等结算"里反复重连。
- **对手状态**：心跳回包按对手最后心跳时间上报 `ONLINE / TIMEOUT / OFFLINE / WAITING`，对端哑掉时不再
  一直显示在线让你干等。
- **对局停滞告警**：房间处于对战中且超过 **150 秒**没有任何战斗帧时，日志里会打一条 `[Online]` 警告，
  附带最后一条帧、双方各自静默时长与本机窗口焦点状态，便于远程定位。

## 增强日志

### 卡死：根因与修复（已在插件里修掉）

症状：画面还在、能点、CPU 接近 0，但什么都不再发生（实测：进剧情页停在
`[Offlinizer] Intercepted Task: StoryInfoTask. Reading local data...` 之后一行都没有），
「有时候过很久会自己恢复」。

根因在插件自己的网络忙标志上：**全程序集里只有插件代码把 `NetworkManager.isConnect` 置 true**
（反编译确认：游戏自己只会在 `StopConnectCoroutine` 和它那个被我们整个替换掉的
`<Connect>d__18` 里置 **false**）。插件把 `isConnect` 当「上一笔请求还没完」的互斥用：

```csharp
while (__instance.isConnect) { yield return 0; }   // 三个任务路径的入口
```

而**真实请求那条路到处都是 `throw`**（MessagePack 解密失败、`HandleDeserializeException`、
`NetworkCheck Error 2/3`…）：异常一逃出协程，末尾那句 `isConnect = false` 就不执行，
标志永远留着 true —— 之后**每一个**任务都卡在那句 `while` 上，于是整局游戏「卡死」；
游戏偶尔调用 `StopConnectCoroutine` 时又会被清掉，正好对应「过很久自己好了」。

修复（`FakeConnect.cs`）：

1. 三个入口统一改用 `WaitUntilNetworkIdle`：最多等 3 秒，超时就自己清掉标志并继续，
   同时打一行 `[Offlinizer] The network busy flag was still set …`（死锁从此不可能发生）；
2. `ProcessOnlineTask` 整段用 `try/finally` 兜住，异常逃出时也一定执行
   `ClearLastRequestTask()` + `isConnect = false`（根因修复）。

### 卡死看门狗（`[Task]` / `[Hang]`，临时诊断）

游戏「卡死」时日志往往什么都没有（实测：进剧情页时停在
`[Offlinizer] Intercepted Task: StoryInfoTask. Reading local data...` 之后就再也没有一行，
而进程仍在跑帧、CPU 接近 0、窗口也响应 —— 也就是主线程在**空转等某个条件**，不是死循环也不是崩溃）。
`TaskWatchdog`（`TaskWatchdog.cs`，临时诊断，定位完就删）在 `Plugin.Update` 里每帧检查
`NetworkManager.lastRequestTask`：

- 任务换人时打 `[Task] '<任务>' finished after Ns (next: '<下一个>')`；
- 同一个任务挂满 5 秒打一次 `[Hang] task '<任务>' has been pending Ns (isConnect=…, isTimeOut=…, isError=…, op='<当前插件操作>')`，
  同一局最多 12 行。

只读状态、只打日志。下次卡死时这几行会直接指出「卡在哪个任务、网络标志是什么、插件正在做什么」。

### 联机卡死现场（2.5.7 新增的诊断）

- `[Focus] 游戏窗口失去焦点… / 重新获得焦点…`：把"失焦导致牌桌不接受操作"这件事写进日志。
  实测的两次「联机卡死」都是这个原因 —— 玩家切到别的窗口，游戏按自己的规则暂停了输入，
  而心跳与回合计时器照跑，服务器侧只看到"这个玩家一直不发操作"。
- `[Online] 房间 <id> 已 N 秒没有收到任何战斗帧…`：对局中超过 150 秒没有任何战斗帧时告警，
  附最后一条帧（uri/seq/谁发的）、双方连接各自静默多久、本机窗口焦点状态。
- `[SocketIO] Closing a silent connection…` / `Opponent … has been silent for Ns`：
  半死连接被回收、以及对手心跳超时被如实上报时的记录。
- 插件自身日志改为**加锁整行输出**，Socket.IO 线程 / 看门狗 / 主线程并发写日志时不会再出现
  "半截行互相插入"，玩家贴过来的日志可以直接读。

### 发布前校验补丁（`_tools/PatchCheck`）

Harmony 是**按参数名**注入的：名字写错时补丁会静默挂不上，编译期发现不了。
`_tools/PatchCheck/` 里有一个校验器，加载插件与 `Assembly-CSharp`，逐个核对每个 `[HarmonyPatch]`
目标是否存在、注入参数名与 `___字段` 是否对得上：

```powershell
dotnet build _tools/PatchCheck/PatchCheck.csproj -c Release
_tools/PatchCheck/bin/Release/net48/PatchCheck.exe `
    "<游戏目录>\BepInEx\plugins\Shadowbus.dll" `
    "<游戏目录>\Shadowverse_Data\Managed\Assembly-CSharp.dll"
# patch definition(s): 268, patched target method(s): 269, mismatch(es): 0
```

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

剧情表情文本的查询不再逐条刷屏：选人界面按角色批量预载的那几百次查询只统计，每场战斗开始时汇总成一行 `[StoryAI] Emote text lookups since the previous battle: ...`；只有在**该场战斗**里真的查不到台词的表情 id 才会逐条输出（最多 30 条，并附调用堆栈，便于定位请求方）。另外，本地表情文本表里查不到的 `ET_*` id 现在返回空串（`Master.GetEmoteWordText` 兜底），界面不会再显示 `ET_進化_2_500211` 这种原始键。

## 自定义练习

进入“单人 > 对战”，在对手职业选择页有**两个**蓝色按钮（原来那个「自定义对手」在左，新增的「斗蛐蛐」在它右边一格）：

| 按钮 | 用途 |
| --- | --- |
| **自定义对手** | 配置**对手** AI（卡组 / 官方 AI 预设 / 三份 CSV / 剧情 AI / 逻辑 / 血量 / LLM），我方照常手动出牌 |
| **斗蛐蛐** | 打开**同一套**设置界面，但进对局后**我方也交给 AI 打**（AI 对 AI，看 AI 互殴） |

两个按钮的文字都会跟随游戏的文本语言：`自定义对手 / 自訂對手 / Custom Opponent`、`斗蛐蛐 / 鬥蛐蛐 / AI vs AI`。

配置页分两栏：

- **左栏**
  - 「我方设置」→ **我方卡组**
  - 「对手设置」→ **选择剧情AI**（第一栏）、**对手卡组**、**职业**、**生命上限**（滑条）
  - 「对手主战者皮肤」→ 主战者头像列表 + 左右翻页（选中剧情 AI 时连同左右翻页按钮一起变暗）
- **右栏**
  - 「LLM AI」开关（最上面）
  - 「自定义对手 AI 数据」→ **选择官方预设AI**、**AI 逻辑**（弱 / 中 / 强）、**牌组 / 风格 / 表情 CSV**、**刷新 CSV**

只需要配置**对手** AI；「斗蛐蛐」按钮走的是同一套界面，只是额外把**我方**也交给 AI（用我方职业对应的官方预设 / 本地 CSV），所以配置页本身没有多出按钮。

第一次点这两个按钮不会再卡顿：官方练习 AI 的 master、AI 牌组包与各牌组卡表在**练习页一显示按钮时就开始后台分批预热**，点击时直接取结果（最多等 0.5 秒兜底）；牌组或卡表被热重载后会重新预热。

「**选择剧情AI**」从 master 的 `story_ai_setting` 里挑一个**官方剧情敌方 AI**（用资源目录 `story_ai/` 里导出的那套 CSV）。列表第一项是固定的「**无**」，选它即可取消剧情 AI。选中后**敌方主战者、职业、卡组、AI 数据、逻辑等级全部由剧情 AI 接管**，生命上限同时拉回官方的默认值 **20**。互斥关系：

- 选中剧情 AI 后，**自定义那一组（选择官方预设AI + 三份 CSV）、AI 逻辑、职业按钮、主战者栏和 LLM AI 都会变暗**——剧情 AI 用的是 `story_ai_setting` 里的 `logic_level`（例如 20 篇第 8 章是 STRONG）和它自带的主战者，这些按钮控制不了它；
- 动了自定义的任意一项（选卡组 / 选预设 / 选 CSV）就自动退回「无」，变暗的按钮恢复。

配置页的校验 / LLM 报错统一显示在**页面最上方**，不再压住主战者名字。

自定义 CSV 分别放入 `Mods/AIData/deck/`、`style/` 和 `emote/`（**只放玩家自己的**；官方那份在资源目录的 `story_ai/`，由「剧情 AI」按钮使用）。文件需要保持原作对应 CSV 的列格式；配置项留为“使用原作预设”时，会使用当前职业所选预设的原始 AI 数据。配置页打开后新增文件，可点击“刷新 CSV”重新扫描。

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
- `localizationFields` 修改卡名、能力文本和背景文本。改过的卡名会注册为卡牌关键字，因此在卡牌描述中可以直接点击查看该卡详情。

修改配置后进入卡组列表即可热重载（文件没改动时不会再重建卡表）。新增卡牌应使用未占用的卡牌 ID。卡图和语音都放在**这张卡自己的文件夹**里（`Mods/CardMaster/<卡文件夹>/`）；全局的 `CardImages/`、`CardVoices/` 目录自 2.5.5 起不再创建、不再读取，2.5.6 起连热重载判定也不再引用它们（遗留的空目录可以直接删掉）。完整的目录与命名规则见游戏目录里的 `Mods/CardMaster/mod制作教程/mod卡教程.md`。

- 卡图默认叫 `card.png`（进化后 `card_evo.png`），只写这两个文件就自动生效；也可以在 `imageFiles` 里指定任意文件名，或沿用 `<ResourceCardId>.png` / `<ResourceCardId>_evo.png` 的命名。进化图没提供时自动使用进化前那张。
- 本地语音用 `voiceFiles` 声明（相对卡文件夹，名字随意），不写则认 `play.wav`、`evolve.wav`、`attack.wav`、`attack_evolved.wav`、`destroy.wav`、`destroy_evolved.wav` 这些约定名。推荐 16-bit PCM WAV。
- 卡图和语音都可以放在卡文件夹的子目录里，只要路径写成相对路径（`图/card.png`）；约定名只认卡文件夹根目录。
- 一个子文件夹里可以放多个 json、多张卡，它们会一起加载（同一张新卡不要拆到两个 json 里）。
- 修改已有卡牌时，补丁会同步应用到其普通版和闪卡版，同时保留两个版本各自的身份字段。
- `stringArrayFields` 可用于替换 `SkillEffectPath`、`SkillSe`、`EvolEffectPath` 等 `string[]` 字段。
- `foilEffectCardId` 可为闪卡指定原版卡牌的动态材质效果。可填写该来源卡的普通或闪卡 `CardId`；仅目标记录为 `IsFoil=true` 时生效，卡图仍使用目标卡自己的 `ResourceCardId` 和 PNG。来源与目标必须同为随从或同为法术/护符，且来源必须是原版已有卡。若同时制作普通版和闪卡版，为了让普通版不误用闪卡材质，两者必须使用不同的 `ResourceCardId`；共享资源 ID 时该覆盖会被禁用。只有单独制作一条闪卡记录时可以继续使用它自己的 `NormalCardId`。
- 游戏内导出卡牌数据时会把卡牌类型写入 `intArrayFields.Tribe`；例如士兵为 `[2]`、机械为 `[7]`。
- `normalArtCardId` / `evolutionArtCardId` / `spellArtCardId` 可以「借」另一张原版卡的卡面（分别是进化前、进化后、以及法术/护符的卡面），并按本卡的比例重新裁切。借来的图不在本卡自己的包里：插件会在请求这张卡面时**异步**补拉来源包，包到位之后下一次查询（例如重新打开卡组列表）就能看到图。这一支刻意不用同步加载 —— 请求发生在游戏自己加载资源的过程中，在包加载里再阻塞地拉包会把主线程彻底挂住。

牌组编辑的搜索框和卡牌图鉴按费用升序排列，同费用的卡牌保持原版顺序。排序依据是 `intFields` 中生效的 `Cost`，因此通常不需要再手工配置 `SortIndex`。

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

### CardMaster 借特效（2.5.6 新增）

不想手填特效名/SE 名，就直接借另一张卡（原版或自制都行）的整套演出 —— 和 `normalArtCardId` 借卡图、`extraVoiceIds` 借语音库同一个思路：

| 字段 | 借到的引擎字段 |
| --- | --- |
| `summonEffectCardId` | `SummonEffectPath` / `SummonSePath` / `SummonMoveType` / `SummonEffectType` / `SummonTime` |
| `destroyEffectCardId` | `DestroyEffectPath`（破坏演出没有 SE 字段） |
| `evolveEffectCardId` | `EvolEffectPath` / `EvolSePath` / `EvoEffectType` / `EvolTime` |
| `attackEffectCardId` | `AtkEffectParameter`（`_effectPath` / `_se` / `_moveType` / `_effectEnginType` / `_time`） |
| `skillEffectCardId` | `SkillEffectPath` / `SkillSe` / `SkillMoveType` / `SkillEffectEnginType` / `SkillEffectTime` / `SkillEffectTargetType` 与对应 `EvoSkill*` 六项 |
| `effectBorrowCardId` | 上面全部（各槽位字段比它更具体，json 自己写的字段又比借来的更优先） |

```json
"summonEffectCardId": 125441020, "evolveEffectCardId": 125441020, "attackEffectCardId": 125441020
```

要点：

- **只借演出，不借效果**：`Skill` / `SkillTiming` / `SkillCondition` / `SkillTarget` / `SkillOption` / `SkillPreprocess` 一律不动，「长得一样、打起来不一样」是允许的。
- **顺序**：`effectBorrowCardId` → 各槽位 → json 自己的 `stringChangeFields` / `stringArrayFields` / `attackEffectFields`，越明确的越晚应用。
- **包会自动到位**：引擎按名字取 `effect_<名字>.unity3d` 里的 `Effect/Effects/<名字>`，自制卡自己的包不存在、借来的名字本来也没人请求 —— 插件会把借来的 `effect_<名字>.unity3d`（磁盘上存在的）挂进预载清单，技能 SE 的 `s/<se>.acb` 由引擎按卡面字段自行请求。日志 `[CardEffect] card … borrows … effects from …` / `added N borrowed effect bundle(s) to the load list`。
- 抄写用的是来源卡的**深拷贝**，不会和来源卡共享可变数据；闪卡版（自动派生）同样生效。
- 实现：`CardParameterPatch.ApplyBorrowedEffects` / `BorrowEffectFields` / `CollectBorrowedEffectResources`（`CardMasterPatcher.cs`），在 `PatchTemplate` 之前调用，注册表随 `RevokeCardMasterPatches` 一起清空。
- 实战例子：`Mods/CardMaster/约束的正义·伊兰翠/`（二代卡搬运，含数值、能力、演出来源与素材缺口的说明）。

## 构建

```powershell
dotnet build Shadowbus.sln -c Release
```

发布版本使用 `Release` 配置，构建产物位于 `bin/Release/net46/Shadowbus.dll`。不带 `-c` 时 `dotnet build` 默认为 `Debug`，产物在 `bin/Debug/net46/`，其 IL 未经优化，请勿用于发布。

构建需要 .NET SDK（提供 `dotnet` 命令）。依赖的 NuGet 源已写在 `Shadowbus.csproj` 的 `RestoreAdditionalProjectSources` 中，首次构建会自动还原。

项目需要引用游戏的 `Assembly-CSharp.dll`。游戏目录按下面的顺序确定，仓库里**没有写死任何用户名或机器专属路径**：

1. 仓库目录下的 `Shadowverse.local.props`（已被 `.gitignore` 忽略），内容形如 `<Project><PropertyGroup><ShadowverseDir>游戏目录</ShadowverseDir></PropertyGroup></Project>`；
2. 环境变量 `SHADOWVERSE_DIR`；
3. Steam 默认库：`<仓库>\..\..\..\SteamLibrary\steamapps\common\Shadowverse`、`%ProgramFiles(x86)%\Steam\steamapps\common\Shadowverse`；
4. 仓库附近两层目录里的 `Shadowverse`。

都找不到时构建会直接报一条清楚的中文错误，而不是抛一大堆找不到 `Wizard`/`Cute` 的提示。**游戏目录不需要在 Steam 库里，也不需要任何目录联接。**

## 注意事项

- 本项目面向单机和 Mod 测试用途。
- 修改卡牌配置前建议保留备份。
- 正在进行的对局不会自动重建已生成的卡牌，测试配置时建议开始新对局。
- 随包分发时不要带 `.lnk` 快捷方式（内部存绝对路径），用仓库/整合包里的便携 `.bat` 启动器。
