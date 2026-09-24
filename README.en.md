# Shadowbus

[简体中文](README.md) | **English**

An offline-play and card-modding toolkit for the international build of Shadowverse, built on BepInEx 6.

The repository also ships an [all-in-one web configuration editor](WebEditor/README.md) that can be deployed to GitHub Pages. It edits AIData, BossRush, CardMaster, Format and TwoPick configuration through forms, and can either read and write a local `Mods` directory or export a complete ZIP.

## Features

- **Offline play** — home screen, Unlimited deck editing, CPU battles and pack-opening animations all work without a server.
- **Everything unlocked** — all cards, leader skins, sleeves and home backgrounds are available by default, and the background choice is saved locally.
- **Unlimited decks** — class, per-card and deck-size limits are ignored, and tokens can be added to a deck.
- **Custom practice** — choose the opponent's deck, class, leader and AI CSV files.
- **BossRush (event boss gauntlet)** — define local runs in `Mods/BossRush/<config>/bossrush.json`; the whole flow is answered offline and needs no server, and there is **no event period limit** (the period text is hidden from the UI as well) — see `Mods/BossRush/README.md`.
- **Trimmed UI** — the "任务" (missions) button is removed from the post-battle result screen in every mode (story, practice, ranked, two-pick, rooms), and the "解谜任务" (puzzle missions) button in the top right of the puzzle challenge screen is hidden. The deck page's "比赛精选牌组" row, the shop's buy-sleeve / buy-leader-skin / buy-item / exchange-spot-card / buy-prebuilt-deck buttons (with their appeal strips and maintenance plates), the Other page's ranking button and the contact dialog's delete-account button are **greyed out and unclickable** instead, keeping their place in the layout. The unreachable main-menu entries (gift / missions / guild / battle pass / campaign box / main and sub banners) are still hidden outright; the full list is in the header comments of `HomeMenuPatches.cs` and `MyPageOtherPatches.cs`.
- **Currency editor** — the Other page gains a settings-styled "货币修改" button that edits gold / red ether / ticket counts and persists them (see "Currency editor" below).
- **Everything lives in the game folder** — resources, logs and `PlayerPrefs` (normally the registry key `HKCU\Software\Cygames\Shadowverse`) are redirected into the extra resource folder, so the whole package can be copied to another PC as-is.
- **Card mods** — modify or add cards, including custom artwork and text.
- **Deck list hot reload** — `CardMaster` configuration is reloaded whenever you open the deck list. It only rebuilds when a file under `Mods` actually changed: the rebuild replaces every `CardParameter` with a fresh instance and clears the plugin's artwork/voice registries, which loses the artwork of cards the UI is already showing (the "change the card count and it comes back, then another card loses it" symptom) and costs hundreds of milliseconds to seconds each time.
- **Clickable card names** — names changed through `localizationFields` are registered as card keywords, so they can be clicked inside card text to open that card's details.
- **Card list ordering** — deck-edit search results and the card gallery are sorted by cost, keeping the stock order within equal costs.
- **Active abilities** — `when_activate` adds an activate button to your followers on the field, with a configurable PP cost.
- **Custom abilities** — copy a card's information and abilities, or gain a target's abilities while keeping your own.
- **Socket.IO rooms** — the host runs an embedded Socket.IO node and generates an encrypted connection code; the other player joins through the stock realtime client.

Only Unlimited decks are supported for now. AI improvements are ongoing.

## Installation

### Prebuilt package

Download the BepInEx and plugin archives from [Baidu Netdisk](https://pan.baidu.com/s/1XFHgqPeRUskWGKOZ0Wnilg?pwd=kbga) and extract them into the Shadowverse game root.

> Extraction code: `kbga`
> If the link expires, please open an [issue](https://github.com/2581374794/Shadowbus/issues), or follow the manual steps below.

### Manual

1. Install BepInEx 6 Mono, 32-bit.
2. Put `Shadowbus.dll` and `Newtonsoft.Json.dll` into `BepInEx/plugins/`.
3. Copy the repository's `Mods` folder into the game root.

The resulting layout:

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
      ├─ <card folder>/    one folder per card: json + artwork + audio together
      └─ Reference/        exported card-name / skill reference tables
```

A typical package root (next to the game folder) looks like this:

```text
Shadowbus/
├─ Shadowverse/           the game itself (the folder name may differ)
├─ Resources/             the resource folder, see below
├─ Shadowbus启动器.bat     portable launcher (relative paths, rename-safe)
└─ 模组文件夹.bat          opens Mods/CardMaster
```


## Resource folder (Resources)

The game stores everything it downloads under the system folder `%USERPROFILE%\AppData\LocalLow\<company>\<product>` (for this game: `Cygames\Shadowverse`). Unity creates that folder automatically on startup and it has nothing to do with where the game is installed. Shadowbus redirects every resource read and write in the game to an "extra resource folder", looked up in this order — **the first one that exists wins**:

1. the BepInEx setting `[Resources] Root` (`BepInEx/config/08c8e386-….cfg`): an absolute path, or a path relative to the game folder — any drive, any folder name;
2. the environment variable `SHADOWBUS_RESOURCES` (same rules);
3. `Resources` next to the game folder (the default and the recommended layout);
4. `Resources` inside the game folder;
5. `Shadowbus\Resources` inside the game folder (an older layout).

So a player can simply drop someone else's resource folder next to the game — **no stock resource folder and no directory junction needed** — and play right away. A folder copied in after the game has already started is picked up too (the plugin applies the redirect on the next frame). If nothing is found, the plugin leaves the game completely untouched.

The game-defined subfolders inside the resource folder are `a/` (asset bundles), `b/` (BGM), `s/` (sound effects), `v/` (voice, with `v/t` for temporary story voice), `m/` (movies), `f/` (fonts), `manifest/` (resource manifests), `cardmaster/` and `recovery/`. **Official AI data exported from the client into plain local files is consolidated in `<resource root>/story_ai/{deck,style,emote}`** (see "Scripted story battles" below) — official data goes there, while `Mods` only holds player-made content.

More than the resources themselves is redirected: every `Application.persistentDataPath` use in the game (18 call sites) plus the path fields that were already filled in during the earliest startup now point at that resource folder — card art / voices / movies / manifests, the replay directories `NewReplay` and Record, the HTTP download cache, the statistics logs (`accumulate_log` and friends), `NGUITools` save data, the game's own CardMaster export folder, and the home-screen special-title BGM check. Unity's own `Player.log` / `Player-prev.log` are written natively and their path cannot be patched from managed code, so two other routes lead into the resource folder:
>
> - **`Shadowbus启动器.bat`** passes `-logFile "<resource folder>\Player.log"` (folder priority: env var `SHADOWBUS_RESOURCES` → the game-folder sibling `Resources`, the plugin's default resource root → `<GAME_DIR>\LocalLow\Cygames\Shadowverse`; everything is derived from the game folder it found, so any drive or folder name works).
> - **A junction on the system folder** (recommended — done once, it stops mattering how the game is launched): point `%USERPROFILE%\AppData\LocalLow\<company>\<product>` at the resource folder (`Resources\游戏资源链接工具.bat`, option 1). A directory junction is completely transparent to the game, so whether it is started through the launcher, Steam or the exe directly, `Player.log` / `Player-prev.log` land in the resource folder instead of the system folder. **Migrated on this machine 2026-09-23**: `C:\Users\panqiushi\AppData\LocalLow\Cygames\Shadowverse` → junction → `D:\Games\Shadowbus\Resources` (the 7 files that were in the system folder were moved into `Resources` with a `.localow-20260923-195114` suffix, and the older 9/18 `Player.log`/`Player-prev.log` were renamed to `*.old-20260923-195131`). On another machine or after a reinstall, run that tool once (or just launch through the launcher once).

The registry settings moved as well: on Windows Unity keeps `PlayerPrefs` in `HKCU\Software\Cygames\Shadowverse`, and the plugin redirects every `PlayerPrefs` read and write in the game to **`<resource root>/PlayerPrefs.txt`** (`PlayerPrefsRedirect`, since 2.5.6). Patching `Wizard.PlayerPrefsWrapper` alone is not enough — its methods are short enough that Mono inlines them into their callers and the inlined copy bypasses the patch — so the plugin redirects the **call sites** as well (415 of them in practice). Nothing is lost — the first time a key is read and not found locally, the value is read from the registry once and recorded in the local file (lazy migration); after that the plugin only reads. The only values that still appear in the registry are the couple the Unity player maintains natively (`unity.player_session_count` / `unity.player_sessionid`, and the `Screenmanager *` keys when the resolution changes), which C++ writes directly and managed code cannot intercept. Copy the `Resources` folder over and the settings, logs and replays come with it.

Optional tools (none of them needs administrator rights):

- `Resources\游戏资源链接工具.bat`: junctions the system folder to the current resource folder so that even the native paths line up. It finds the game folder by itself and reads the company/product names from `Shadowverse_Data\app.info` to compute the target, so a renamed game folder, a different drive, or even a different company name all work. Existing junctions, empty folders, and non-empty folders are each handled appropriately (a non-empty folder is renamed to a backup first — no data is ever deleted).
- `Shadowbus启动器.bat` and `模组文件夹.bat`: locate the game through `%~dp0` plus a scan of the folders next to, inside, and one level below themselves, matching `Shadowverse_Data\app.info`. **Do not ship `.lnk` shortcuts** — a shortcut stores an absolute path and breaks on another machine.

Resource completeness can be checked with `_tools/verify_resources.py` (outside this repo): it maps every manifest entry to its `a/`, `b/`, `s/`, `v/`, `m/`, ... subfolder and tests for the file. The current resource folder is **complete: 14 manifests, 40k+ entries, 0 missing**, and only 34 of the 418 characters in `class_chara_master` have no dedicated emote master (those characters simply have none).

Whether a scripted story battle is fully playable offline can be checked with `_tools/verify_story_battles.py` (per chapter: field, BGM, both leaders, enemy class, the enemy AI's three CSVs, `_ai` references, plus `story_ai/` coverage **and whether every card in those decks exists in the local card master**). All **424 chapters currently report 0 problems**; all 267 official story AI decks are exactly 40 cards and every one of the 1733 distinct base cards they use is present locally.
### Story battles with server-side gimmicks (optional)

Some story chapters are scripted battles on the official server: who goes first, both sides' starting PP and life, attached skills, effect overrides and result skipping all arrive in the `StoryStartTask` response (`special_battle_setting` on the client). Nothing of that exists inside the client or the resource folder, so offline those chapters degrade to a plain AI battle with none of the official effects.

Starting such a chapter logs the exact file to create:

```text
[Offlinizer] Story 2013800 has no local special battle setting; ... Optional: put the official
special_battle_setting object in Mods/StorySpecialBattles/2013800.json.
```

Drop the official `special_battle_setting` object into that file (an official replay stores the same fields with a `story_special_battle_` prefix — strip it to get the keys). The same file accepts two extra sections:

- `_chapter`: corrects the offline-generated chapter fields — the player leader (`player_chara_id`), the enemy leader, the enemy class, the 3D battle field, the BGM, and the emote-table variation numbers (`player_emotion_variation` / `enemy_emotion_variation`). These are now **generated from the official table** (see "Official chapter table and generation" below) and normally need no hand editing. The enemy class is taken from `_chapter.enemy_class` — that is exactly the server's `story_master_list.enemy_class`, which the client uses directly as `BattleSettingBaseData.EnemyClassId`; only when it is absent do we fall back to "the enemy leader's own class → the AI deck's dominant class". Story battles reuse the same grunt model (fixed to one class in `class_chara_master`) as many different classes, so preferring the model's class would override the official value.
- `_ai`: hands the enemy AI completely over to local CSVs under `Mods/AIData/deck|style|emote` (deck, style, emote, logic level, starting life), so a scripted battle can be reproduced offline without the official AI data.

See `Mods/StorySpecialBattles/README.md` for the field table, examples, CSV column layout and sources. Known so far: purification arc chapter 38 = `2013800`, chapter 8 = `2050800`.

The plugin also exports every local story script it reads into `Mods/StoryScenario/`. The battle BGM (`bgm_id`) and the 3D battle field (`battle3dfield_id`) now both come from the official chapter table; an official `"0"` means "keep this chapter's own BGM / no override", not a missing file. Override them with `_chapter.bgm_id` / `_chapter.battle3dfield_id`.

### Official chapter table and generation

The CN client's chapter endpoint (`main_story/info`) returns one entry per chapter with `battle_exists`, `enemy_chara_id`, `enemy_class`, `enemy_ai_id`, `battle3dfield_id`, `bgm_id`, `special_battle_setting_id` and the full `battle_settings` (`deck_class_id` / `skin_id_override` / `deck_skin_id_override` / `player_emotion_override` / `enemy_emotion_override`). The whole table is exported as plain local files:

- `<resource root>/story_ai/story_battles_official.csv` — the raw values for **424 battle chapters** across all 20 arcs (one row per playable class per battle chapter; `main_story/info` returns `battle_settings` as a **per-chapter array**, so multi-class chapters produce several entries)
- `_tools/build_official_table.py` — rebuilds that table from `_tools/yzs_api/section_*.json` (the captured responses)
- `_tools/build_story_battle_data.py` — reads that table and generates the `_chapter` section of `Mods/StorySpecialBattles/<story_id>.json` plus `<resource root>/story_ai/emotechara/_chapter_variations.csv`; `--report` analyses without writing
- local story id = `section×100000 + deck_class×10000 + chapter×100` (plus `×10` for letter-suffixed chapters), matching `Data.CreateStoryId`

Current output: **424 `_chapter` sections** (1:1 with the local `story_scenario_param_*_2` battle scripts, nothing missed) and **359 emote-variation pins** (114 player / 356 enemy). A pin is only written when the referenced `emote_chara_<leader>_<variation>.csv` really exists locally; otherwise the arc-derived value is kept — enemy emote tables are covered for 356/424, and the remaining 68 belong to the "exists on CN but not downloaded locally" group. The emote tables themselves grew from 137 to **615** (`Resources/a` holds 875 `master_emote_chara_*` bundles; the exporter collects the leaders named by the story voice prefixes and by every `_chapter`).

`skin_id_override` is the leader that actually takes the field (`BattleSettingData.GetPlayerCharaId` prefers it over the chapter's `chara_id`), while the id in the leader menu (e.g. `500701`) has no emote table at all; the generator therefore writes the official leader into `_chapter.player_chara_id`, which is what makes the player side of leader-select chapters line up.

**39 of the 42 `special_battle_setting` ids have been collected** (74 chapter files): it is server-side designer data that exists neither in the client nor in the resource bundles, so the only way to get it is the `main_story/start` response the client receives when it really enters a chapter. Two collectors live in `_tools`: `harvest_story_settings_from_captures.py` (harvests straight out of the proxy captures, so simply playing works) and `yzs_fetch_story_settings.py` (replays that endpoint exactly as the client does, deduplicated by setting id; **the server rate-limits, keep the interval at 10 s or more**). The generator only rewrites `_chapter` and **keeps any body already stored**.

> Three are still missing: `ids 40/41/42` (arc 20 chapters 32/35/38). They need chapter 24 cleared first, chapter 24 has a cutscene crash in the CN client (`CharacterManager.CreateCharacter` gets a null prefab), and the server answers `500` for those three `main_story/start` calls anyway.

> ⚠️ For a chapter with **no** body the offline response must leave `data["0"]` an **empty object**.
> `StoryStartTask.Parse` tests `jsonData.Count == 0` to mean "no server-side battle override", and only
> then indexes `player_first_turn` / `id` / `result_skip` and a dozen more keys directly. Returning
> `{"special_battle_setting": {}}` sends the client down that branch and it throws
> `KeyNotFoundException` — the chapter simply will not start. `Mods/StorySpecialBattles/README.md`
> lists every required key.

Those 73 bodies are currently **not obtainable** (verified): the server only accepts unlocked chapters — `main_story/get_deck_list` returns `result_code=1` for the unlocked `649` but `500` for one that is not unlocked, and of the 74 chapters carrying a `special_battle_setting_id` only `649` has `is_released=true`. Story progress is recorded server-side, while the offline mod lets every chapter be played directly, so the server never saw any progress. Fetching them in bulk would require faking cleared chapters with `main_story/finish`, which we do not do. `_tools/yzs_fetch_story_settings.py` is ready (deduplicated by setting id, 41 requests for 73 chapters, aborts on any non-1 result code) for whenever the account really unlocks them.

The tools used to capture and replay this API live in the sibling `_tools`: `yzs_h2proxy.cs` (HTTP/1.1+HTTP/2 reverse proxy with per-SNI certificates), `mitm_setup.sh` (installs/removes the device-side system trust store entry and hosts entry; fully reversible), `mitm_decode.py` (decrypts captured requests/responses), `yzs_param_probe.py` (verifies the signature formula `PARAM = SHA1(udid + "/index.php/<path>" + base64(msgpack(params)) + viewer_id)`), and `yzs_replay.py` / `yzs_story_scan.py` (replay read-only endpoints exactly as the client does).

Beyond that, the official story enemy AI deck / style / emote data is **exported into plain local CSVs** by `_tools/export_story_ai.py` (with `_tools/unityfs_extract.py` unpacking the UnityFS + LZ4 bundles) and lives **inside the resource folder**: `<resource root>/story_ai/{deck,style,emote}` (~590 KB, 267/82/40 files), so story battles work completely offline. This is **official game data, so it does not go into `Mods`** — `Mods/AIData` stays reserved for player-made CSV mods. When an `_ai` reference resolves a file name, `Mods/AIData` is searched first and the official copy under the resource root is the fallback, so a same-named file in `Mods` overrides it. See `Mods/AIData/README.md`.

#### Dialogue / face / motion (chapter emote tables)

The official face, motion, voice and dialogue data is split **per story arc**: `emote_chara_<skin>_<variant>.csv`. The plain `<skin>` table is a generic placeholder — its **motion column is empty** (the client falls back to `idle`, i.e. no motion) and its `text_id` points at non-existent generic ids, so the UI shows the raw id (e.g. `ET_進化2_500211`). Only the variant tables are filled in (e.g. `500211_3` is the arc-20 table: `進化2 → motion=4 + ET_ST_進化2_500211_09_01`).

The client picks the table through `BattleSettingData.GetEmotionId(charaId, variationId)`: `0` → generic table, non-zero → `<skin>_<variant>`. The plugin therefore:

- picks the variant for the **current chapter** from `story_ai/emotechara/_variants.csv` (exported by `_tools/export_story_ai_emotes.py`; the variant numbers are derived from the official tables' own `ET_ST_..._09_01` / `_20_01` suffixes, not hand-written) and writes it into the chapter data's `player_emotion_override` / `enemy_emotion_override`;
- does the same for **custom practice with a story AI**: the story AI id is mapped back to its section (the official AI id is built from section/class/chapter) and both sides get the matching emote table;
- if a chapter's official tables do not line up, `_chapter.player_emotion_variation` / `enemy_emotion_variation` can pin the variant by hand.

**A missing text id is deliberately left as the raw id** (no empty-string fallback): seeing `ET_進化2_500211` means "this is misconfigured", and the log line `emoteTable=player=2515_1, enemy=500211_3` shows which table was used. Missing ids are split into two classes so the log stays readable:

- `ET_ST_*` → an official gap (referenced by the official tables but never defined in the official text master, so the official client shows the raw id too): **counted only**;
- any other id → the generic placeholder table was loaded (a table-selection bug): **one warning per id** plus a single-line requester (`Requested by: Wizard.Emotion..ctor`), no stack dump.

Missing emote text is split into two classes, judged by **whether the official text table defines a non-empty text for the id** (not by its prefix), so the log neither floods nor hides real problems: an id the official table never defines — **or defines with an empty value** — is an **official gap** (the official client shows the raw id too, e.g. `ET_挨拶_500042`, `ET_進化1_500211` for minor characters; the Chs table holds exactly nine empty entries, `ET_心配_301..307` and `ET_心配_403/404`) and is only counted; an id that *is* defined with real text but cannot be resolved means the generic placeholder table was loaded, which is a real bug — those get a per-id warning (capped at 20 per battle) plus one `Requested by: …` line. The id set is read once from the exported official table `<resource root>/story_ai/emotetext/*.csv` (5298 ids); if it cannot be read the classifier falls back to the old `ET_ST_*` rule.

Each battle start emits one summary line: `[StoryAI] Emote text: resolved=…, official-gap=…, misconfigured=… (examples: …), late-resolve=…`, where `late-resolve` counts ids that missed first and resolved later (the signature of a "frozen before the text table was ready" load-order problem — measured as 0, so that problem does not currently exist). Whether other chapters work the same way can be checked with `_tools/verify_story_emotes.py`.


### Temporary story voice (`v/t/`)

Story voice is downloaded on demand into `v/t/*.acb` and is normally missing from an offline pack, which is why those lines are silent. To fetch it yourself (and ship it afterwards):

```ini
[Resources]
AllowTemporaryVoiceDownload = true
```

The game then asks the server and writes the ACBs into the resource folder's `v/t/`. **This is useless while the official server is down** (the request only waits and times out), so the default `false` is the better choice; otherwise copy someone's already-downloaded `v/t/` folder for the same language.

### Runtime Simplified/Traditional story text (`<resource root>/story_text/`)

Every piece of story text — dialogue, character names, chapter summaries and movie subtitles — lives in `storylang_scenario_text_*.unity3d` (dialogue / names / summary) and `storylang_movie_subtitles_*.unity3d` (subtitles), one TextAsset per bundle. The client **downloads one language at a time**.

The story bundles have been **moved out of `a/`** into one folder per language (same bundle names, **byte for byte, with no repacking**):

```text
<resource root>/story_text/chs/storylang_scenario_text_1_10_1.unity3d     ← Simplified
<resource root>/story_text/cht/storylang_scenario_text_1_10_1.unity3d     ← Traditional
<resource root>/story_text/<chs|cht>/storylang_scenario_text_name.unity3d
<resource root>/story_text/<chs|cht>/storylang_scenario_text_summary_6.unity3d
<resource root>/story_text/<chs|cht>/storylang_movie_subtitles_story_0_prologue.unity3d
...
```

Each folder holds 1317 bundles / 4.67 MB, and `a/` keeps **no copy** (it keeps only the `storylang_scenario_param_diff_*` parameter tables and the `storylang_tutorial_*` / `storylang_stt_*` bundles, which hold textures).

The game resolves a bundle file through exactly one method (`Cute.AssetHandle.BuildLocalCachePath()`, used by `AssetHandle/<_Load>` for `AssetBundle.LoadFromFile` and by `AssetManager.LoadObject` for its `File.Exists` check), so the plugin rewrites its result according to the **current text language** (`Cute.CustomPreference.GetTextLanguage()`, the setting you pick in game). The lookup order is:

1. `<resource root>/story_text/<lower-case language code>/<original bundle name>` (`Chs`→`chs`, `Cht`→`cht`, `Eng`→`eng`, …);
2. otherwise `story_text/chs/` (Simplified);
3. otherwise the game's own path is kept.

So:

- language **简体中文** → `story_text/chs/`;
- language **繁體中文** → `story_text/cht/`, and dialogue, names, summaries and subtitles all switch together;
- any other language (Eng / Kor / …) → its own folder if present, otherwise the Simplified set (same as the old `a/` behaviour);
- adding another language later is just dropping in a `<language code>` folder — no plugin change.

To switch: Other page → language switch → pick 繁體中文 for the text language; the game restarts itself (`SoftwareReset`) and the next chapter is Traditional. Two log lines confirm it hooked up: `[StoryText] Text language is 'Cht'; story text is looked up in …\story_text → cht, chs.` and `[StoryText] Served 'storylang_…' from …`.

Both sets are collected with `_tools/collect_story_text_bundles.py` (1317 bundles / 4.67 MB each; `--move-source` copies, checks the sha256 and then deletes the source — a real move):

```text
python collect_story_text_bundles.py --lang cht                                   # Traditional ← 反和谐汇总/备份_剧情文本繁体/
python collect_story_text_bundles.py --lang chs --source deployed --move-source   # Simplified  ← Resources/a/ (moved away)
```

It deliberately **excludes** `storylang_tutorial_how_to_class_*` / `storylang_stt_loop_sneak_*` (they hold textures, and are byte-identical in both languages) and `storylang_scenario_param_diff_*` (parameter tables, not text). A few chapters of the CN client (mostly sections 1–6) were shipped as Japanese development placeholders (`dummy`-prefixed stubs) — identical in every language and never displayed — and are simply copied as they are.

To go back to the old "story text also under `a/`" layout, copy the bundles from `story_text/chs/` back into `<resource root>/a/` (leaving `cht` in place is harmless — the language folder simply wins). `_tools/verify_resources.py` knows about the new location, so it still reports `storylang_assetmanifest missing=0` after the move.

### Puzzle mode (`basic_puzzle/*`)

The puzzle battles themselves **always lived in the client's own master data**: `master_puzzle_data` (113 puzzles: character skins, voices) → `master_puzzle_battle_data` (board, hand, PP, win condition), with the text in `master_puzzletext` (nine regions) and the thumbnails in `ui_puzzle_thumbnail_*`. **The server only supplied the grouping and the clear state**, through three endpoints:

| Endpoint | Client task | Purpose |
| --- | --- | --- |
| `basic_puzzle/info` | `PracticePuzzleInfoTask` | the puzzle groups (each with `puzzle_master_id`, a title text id and its puzzles) |
| `basic_puzzle/open_puzzle_dialog` | `PracticePuzzleListTask` | one group's puzzles and difficulties (request carries `puzzle_master_id`) |
| `basic_puzzle/mission` | `PracticePuzzleMissionListTask` | the puzzle missions and their rewards |

The offline data lives in **`<resource root>/puzzle/`** (official data does not go into `Mods`):

- `puzzle_info.json` — the CN server's `basic_puzzle/info` groups verbatim (25 groups / 113 puzzles)
- `puzzle_mission.json` — the puzzle missions (19 entries)

Regenerate them from captures with `_tools/build_puzzle_data.py`; the script marks **every puzzle cleared and every mission achieved** (the same idea as flipping `is_released` for the offline story), so anything can be played straight away and nothing shows as locked. The `open_puzzle_dialog` reply is derived on the fly from that group in `puzzle_info.json`, so it needs no extra file.

The plugin's `PuzzleOfflineData` takes over those five tasks by **task type name** (`PracticePuzzleInfoTask` / `ListTask` / `MissionListTask` / `BattleStartTask` / `BattleFinish`) without referencing the client's puzzle types, so a client update cannot break the plugin build. The puzzle entry (the practice menu's `_practiceBattlePazzle`) used to be hidden because the offline data was empty; it is restored now.

The "解谜任务" (puzzle missions) button in the top right of the puzzle challenge screen (`PracticePuzzleUI._missionButton`) is hidden by `HomeMenuPatches`, so although `basic_puzzle/mission` and `puzzle_mission.json` are still kept and answered offline, nothing in the UI opens them any more; delete that one patch to bring it back.

### Currency editor (the "Other" page)

The slot that used to hold "游戏指南" (game guide) on the "其他" (Other) page is now a button called "**货币修改**" (edit currencies), cloned from the "设定" (settings) button so it has the same prefab, size and material. The dialog it opens uses the **second-level "咨询 → 咨询" dialog** (`UIManager.SupportDialogPrefab`, whose rows come with underlines): its first three rows are repurposed as **gold / red ether / ticket**, each with an input field on the right (the field comes from the delete-account dialog's prefab), and everything the support dialog ships from row four onwards — the explanatory text, the tick boxes and a "New Label" placeholder — has its underline hidden along with it. Press 保存 and it takes effect immediately: gold is redrawn by `UIManager.UpDateRupyNum()` + `MyPageMenu.UpdateRupyCount()`, crystal by `UpDateCrystalNum()` / `UpdateCrystalCount()`; the values are written to `Mods/Profile.json` and survive a restart.

| Row | Game data |
| --- | --- |
| Gold (金币) | `PlayerStaticData.UserRupyCount` ← `LoadDetail._userCrystalCount.rupy` |
| Red ether (以太) | `PlayerStaticData.UserRedEtherCount` ← `LoadDetail._userCrystalCount.red_ether` |
| Ticket (入场券) | item id 1 (challenge ticket) → `LoadDetail._userItemDict[1]` |
Three more changes on that page: the game guide button moved into the ticket-list slot, the ticket list moved into the item-history slot, and the item-history button is gone entirely (it is removed from `_enableOtherButtons` rather than merely hidden, because its per-frame entrance animation calls `SetActive(true)`); the ranking button and the contact dialog's delete-account button are greyed out and unclickable.

### Local replays (Other → 回放)

The client already ships a "new replay" system: `NetworkBattleReplayOperationRecorder` records a battle as AES-encrypted JSON under **`<resource root>/NewReplay/<battleId>/`** (`replay_info.json`, `replay_turn_start.json`, `replay_battle_log.json`, …, at most 30 kept), and `ReplayDialogContent.GoReplay` looks for that folder first — **if it exists the replay is played from disk** (`ReplayController.StartPlayReplay(..., isNewReplay: true, battleId)`), and only otherwise does it ask the server for `ReplayDetailTask`.

> **`persistentDataPath` must keep Unity's separators (the decisive trap)**: both `GoReplay` and `ReplayDataHandler.NewStockDataPlayer` decide "does this battle exist locally?" with
> `Directory.GetDirectories(persistentDataPath + "/NewReplay").Select(d => d.Replace("\\", "/")).FirstOrDefault(f => f == persistentDataPath + "/" + battleId)` —
> the candidates are normalised to forward slashes while the right-hand side concatenates the **raw `Application.persistentDataPath` string**. Unity itself returns forward slashes on Windows (`C:/Users/…`), but the plugin used to hand out the resource folder with backslashes (`D:\Games\Shadowbus\Resources`), so the comparison could **never** match: a local replay was treated as a *server* replay (`StartPlayReplay(replayInfo, …)`, `isNewReplay=false` → `StockDataPlayer` waits for server data), which shows up as **playback frozen on the mulligan screen with no error in the log at all**. `PersistentDataPathRedirect.Current` now always returns forward slashes (and `ResourceRootPatches.ResolveResourcePath` re-normalises the combined path to the platform separator), so both checks match again. Lesson: when redirecting `persistentDataPath`, keep Unity's own path style instead of pasting a backslash path from elsewhere.

What `ReplayOfflineData` does today:

- **The replay list is local**: offline, `ReplayInfoTask` scans `<resource root>/NewReplay/*/replay_info.json` (newest first, at most 30 entries) and `ReplayDetailTask` reads that battle's local `replay_info.json`, so Other → 回放 never talks to the server again — which also removes the `MessagePack.ToJson` decryption error.
- **Recording offline battles**: AI battles used to get a `NullReplayRecordManager` (which records nothing); the plugin installs a real `ReplayRecordManager` and fills in the two things the client itself is missing:
  1. Before recording starts, both starting decks (`BattlePlayer/BattleEnemy.BattleStartDeckCardList`) are written into `NetworkUserInfoData._selfDeck/_oppoDeck` — `RecordBattleStartInfo()` reads the decks from there, and for AI battles they were null (that is the `Could not enable replay recording: Object reference not set…` line);
  2. the method that actually writes the files, `BattleFinishWriteJsonData`, is only hooked onto `NetworkStandardBattleMgr.OnBattleFinish`, so the plugin also hooks `AINetworkBattleManager.InitiateGameEndSequence(hasWon)`.
  Note that `BattleManagerBase.JudgeBattleResult()` (the method that looks like the natural safety net) returns a Vfx sequence and is **created again for every card played or attack**, including the very first hand setup — so the plugin only accepts it as a battle end once one of the two leaders is actually dead; otherwise a battle would be committed as an empty replay right at the start.
  Offline battles have no battle id, so the plugin generates a 12-digit numeric id to use as the folder name (under 14 characters, so the recorder's own "delete unfinished timestamp folders" cleanup will not remove it). Watching a replay or spectating does not record a second time.
- **The two opening-mulligan records (this is why playback used to freeze on the mulligan screen)**: in `replay_network.json` the player advances on op codes — `op 0` deals (`DealOperation`), the **first `op 1`** plays our own mulligan (`SwapOperation`) and the **second `op 1`** finishes it and resumes the battle (`SecondMulliganOperation` → `OnEndMulligan`). A single-player battle records neither of the two:
  1. `op 0`: the stock code only calls `CallRecordingMulliganStart` at the end of `NetworkPlayerMulliganCtrl` / `NetworkOpponentMulliganCtrl.StartMulliganVfx`, while single-player battles use their base classes `PlayerMulliganCtrl` / `OpponentMulliganCtrl`, which never call it;
  2. the enemy's `op 1`: `BattleEnemy.OnMulliganEndForReplay` is only raised by `NetworkMulliganMgr.EnemyChangeCardVfx`; the single-player `SingleMulliganMgr.EnemyChangeCardVfx` only raises `CallRecordingMulligan` (that `OnMulliganEnd` feeds disconnect recovery).
  
  As a result `isOppoMulliganEnd` never becomes true, `OnEndMulligan` never arrives and **playback stays on the mulligan screen forever**. The plugin re-raises both events on the single-player path (three postfixes: `PlayerMulliganCtrl/StartMulliganVfx`, `OpponentMulliganCtrl/StartMulliganVfx`, `SingleMulliganMgr/EnemyChangeCardVfx`, only while this battle is being recorded by us) and leaves everything else to the stock recorder's `RecordMulliganStart` / `RecordEnemyMulliganReplaceCards`. **Replays recorded before this fix lack those two records and will still freeze on the mulligan screen** (the data cannot be reconstructed) — record a new battle instead.
- **Switch**: `[Replay] RecordOfflineBattles` (default `true`, in `BepInEx/config/08c8e386-a794-442f-a98c-aec65a183898.cfg`). Turning it off skips the whole recording path, which makes it a quick A/B for a battle-start stutter or freeze. When a replay is committed the log also says where it came from: `[Replay] Replay recording finished (win=…, from <caller>)`.
- **`classId1/2` in `replay_info.json` has to be repaired**: an offline recording writes **0** there (`DataMgr.GetPlayerClassId()` and the leader card's `Clan` both return 0). The list/preview path already clamps it in `SanitizeReplayJson`, but **playback reads the file on disk**: `ReplayController.ParseReplayData` → `SettingSelfInfo` → `OnReplayReady()` calls `GetPlayerClassId()` (=0) → `Matching.FirstSetting` → `StartBattleLoad` → `DataMgr.GetClassPrm(0)` throws a **KeyNotFoundException** and the battle never starts. Both sides are fixed now: when a file is read (`NewReplayBattleMgr.ReadJson` postfix) an invalid class is inferred from the deck's dominant class; the recorder (`BuildBattleUserInfo`) applies the same rule so new recordings no longer carry 0. Log: `[Replay] Repaired the recorded class of '…': side1: class=…, chara=…`.
- **Why `charaId1/2` (the leader) turned into the class's default character (fixed)**: the recorder writes `NetworkUserInfoData.GetSelfCharaId()/GetOpponentCharaId()`, which read **`SelfBattleStartInfo`/`OppoBattleStartInfo` — not the `_selfInfo/_oppoInfo` dictionaries**. Those two objects are only built on the "recovering a watched replay" path (`SetSelfInfo(info, isWatchReplayRecovery: true)`), so an offline battle leaves them null and every getter returns 0 (the same recording proves it: `subclassId1=0` / `subclassId2=10`, exactly what a null object yields). During playback `Matching.SettingOpponentClassDataAndLoadObject` calls `GetCharaPrmByCharaId(GetOpponentCharaId())` (=0 → null) and then falls back to `GetCharaPrmByClassId(GetOpponentClassId())`, which is how **the enemy ended up on that class's default leader**. Both ends are fixed:
  1. **While recording**: `ApplyBattleStartInfo` builds those two objects from the same keys `NetworkUserInfo.SetParameter` wants (reflection writes just those two properties; it never touches `DataMgr`, unlike the stock `SetNetworkSelfInfo`, which would also overwrite the player's class/sub-class/skin);
  2. **While reading an old recording** (its 0s cannot be rewritten): `fieldId` *is* the story chapter table's `battle3dfield_id`, so it is looked up in `Mods/StorySpecialBattles/*.json` → `_chapter` for the official `player_chara_id` / `enemy_chara_id` (narrowed by both classes and de-duplicated by the leader pair; when it stays ambiguous nothing is changed and the class default is kept). **Story battles only (`deck_format == DataMgr.BattleType.Story`)** — a practice battle can also pick a story background (measured: practice replays with `field=71/6/61/7`), and matching on `fieldId` alone would put a story leader into a practice replay. Log: `[Replay] Field 76 matches a story battle: player=1908 (class=8), enemy=4528 (class=8)`.
  So story replays recorded **before** this build (`charaId=0`) now show the right leaders too; old practice / BossRush recordings have no source to look them up and still use the class default — record a new one (fresh recordings carry the real ids). One addition: at recording time `DataMgr` cannot supply the **story enemy's** skin id either (a fresh recording measured `enemy(class=8, chara=8)` — right class, but that class's default leader), so recordings made *since* this build are repaired by the same rule: **in a story battle a `charaId` that equals "that class's default leader" counts as missing** and is replaced from the chapter table (a value that is some other skin is left alone).
- **The replay-list dialog now has an "open replay folder" button**: that dialog is the one built by `ReplayDialog.Create()` with `DialogBase.SetButtonLayout(CloseBtn)` (title `OtherTop_0033`) and it only ships a Close button. The plugin's `ReplayDialog.SetupContents` postfix adds a button into the engine's own button2 slot (`ButtonType.Gray` — the very same `btn_common_01_m_off` sprite and font/size as Close) and swaps the two slots' anchors so **Close sits on the left and the new button on its right** (the engine's two-button layout puts button1 right and button2 left by default). Clicking it opens the replay folder with `explorer.exe` and **keeps the dialog open** (`isNotCloseWindowButton2 = true`); when `UseShellExecute` is unsupported it falls back to starting the process directly.
  The path is never hardcoded: `ReplayOfflineData.ReplayRoot` = the resource root resolved at runtime + `/NewReplay` (i.e. the redirected `Application.persistentDataPath`), so another machine, drive letter or folder name all follow along; the folder is created first if it does not exist. Logs: `[Replay] Added the '…' button next to Close in the replay dialog (folder: …)` and `[Replay] Opening the replay folder: …`.
  **The button label follows the game's text language** (`StoryTextLanguagePatches.ResolveUiText`): Simplified 「打开回放文件夹」 / Traditional 「開啟回放資料夾」 / anything else (Eng, Kor, …) "Open Replay Folder"; an unreadable language falls back to Simplified. Switch language, reopen the dialog, and it shows the matching one (no restart needed).
- **Temporary diagnostics (removed once the bug is closed)**: while a replay plays, `ReplayPlaybackTrace` logs `[ReplayTrace]` lines — every operation the player actually consumes (the first 40 plus all opening ones, with `type` / `isSelf` / both index-list lengths), the mulligan hand-off and phase teardown, `StartBattle`, whoever paused the replay (with a stack trace), and any exception an operation threw (those used to be silent). When playback stalls these lines show exactly which step it stopped at.
- **Null dereference in the story-replay deal step (guarded)**: `DealOperation` → `SetSkillDescriptionValueList(AllCards, CardInfoList)` → `GetBattleCardIdx` returns null when the card is missing → `UpdateSkillDescriptionValueList(null, …)` dereferences it while assigning `card.ReplaySkillDescriptionValueList`, which aborts the whole deal and leaves playback on the mulligan screen (the stock chain has no null checks, and `CardInfo` itself can be null too). The plugin only adds "skip when null"; normal arguments behave exactly as before. `charaId` in *recording* also no longer always falls back to the class's *default* leader: it prefers `DataMgr`'s current leader, then the **leader card's own id** (the protagonist's skin card in story battles) and only then the default.
### Leader-ability choice cards (技巧 / 秘术 / 奥义)

The three options of a leader's 勇气抉择 appear as hand cards during the battle; their ids live in `Mods/OfflinizedTasks/LoadTask.json` under `data/avatar_info/abilities` (`option:card_id=A:B:C`). They carry a stock data defect: **every 技巧 card has `Cost = -1`** in the card table (秘术 / 奥义 use normal costs).

The battle log's `BattleLogManager.SetUp` only builds cost buckets for 0..30, so `_AddDestLogCommonOne` looks up `Cost`, gets null back and dereferences `.LogInfoList` — a null reference raised on the card-play retry chain, i.e. **once per frame**, which is why the 技巧 card can never be played and stays translucent in hand. `BattleLogPatch` only skips that one log entry when no bucket exists and prints `[BattleLog] card <id> has cost -1 …`; every other case goes straight through the stock code (no faked costs, no card-table edits).

Use `_tools/ability_card_costs.py` to inspect these costs.

**What the `-1` actually means (settled)**: it is not "refund 1 play point (PP)" — it is **+1 hero point**. These cards belong to **Avatar format (`Wizard.Format.Avatar = 39`)**, and their `Cost` field stores the *negated* hero-point delta: for all 24 leaders `card Cost == -ability_cost` (技巧 `-1` ↔ `ability_cost=+1` → +1 hero point; 奥义 `6` ↔ `-6` → costs 6 hero points; 秘术 `0` ↔ `0`). The two payment paths differ, and both are stock code:

- **Hero-skill (choice-brave) path**: the card built by `BattleManagerBase.ReplaceChoiceBraveCard` has `IsChoiceBraveSkillCard = true`, and `BattleCardBase.Cost` **deliberately skips the `Math.Max(0, …)` clamp** for it, returning the raw `-1`; `PlayCard` pays it with `UseBp(Cost)` → `Bp - (-1)` = +1 hero point. **That is the "refund" — of hero points.**
- **Played as an ordinary hand card**: `Cost` ends in `Math.Max(0, num)`, so `-1` is clamped to `0` → `UsePp(0)`: nothing is spent and nothing comes back. In a custom/practice match (not Avatar format) 技巧 therefore really does settle as a 0-cost card. That is stock behaviour — the `cost -1` line printed by `BattleLogPatch` reads `BaseParameter.Cost`, which is a different number from the effective cost.

The whole Avatar system sits behind format 39: the first line of `BattleManagerBase.SetupAvatarBattle` is `if (Data.CurrentFormat != 39) return;` (attaching the leader abilities and initialising hero points happens after it), and the `md_card_heroskill` preload and `_choiceBraveCardFrameMaterials` use the same gate. Custom/practice matches are `deck_format=5` (MyRotation), so there is neither a hero-skill button nor any hero-point accounting — **nothing to fix**; verifying it requires an Avatar-format match.

To check for yourself: `_tools/ability_card_costs.py --bad-only` (card id ↔ table cost) compared against `ability_cost` in `LoadTask.json`; and in your own offline replays (`Resources\NewReplay\*`, `replay_network.json` after decrypting the AES envelope) `RecordPlay`'s `16` = cost actually paid, `49` = choice-brave replacement card id, `109` = whether the choice-brave path was used.

**Artwork**: these cards have no face of their own in the stock data — their `ResourceCardId` points at an unrelated original card (蛟的技巧 → 104412010 “Dragon Egg”, 蛟的秘术 → 102424030 “Dragonkin's Fist”, 雪华的技巧 → 110114011, 雪华的秘术 → 107124011), and 拉蒂卡的技巧 (930144040/041) has no artwork anywhere locally. The material name derived from their own card id (`9304440700_M` and friends) does not exist in the local resources, so showing another card's face is what the stock data itself does — not a wrong borrow by the plugin.

**The CN client behaves identically** (`_tools/audit_choice_card_art.py` checks all 71 cards one by one — the union of the json/example files under `Mods/CardMaster/**` named 技巧/秘术/奥义 and the ability table in `LoadTask.json`; the CN side comes from `_tools/check_cn_choice_card_art.py` and the full `ls` of the CN `a/` folder): **not one of the 71 has artwork of its own** (every material named after its own card id is absent from the local material index, and the CN `a/` folder contains **zero** `card_930*` artwork bundles). **70 of them** borrow a source that exists in *both* clients (the CN bundles hold exactly `1044120100_M` (Dragon Egg) and so on); the single exception is **拉蒂卡的技巧 930144040/041**, which has no source to borrow either — no artwork anywhere, left blank as requested. So the CN client can only borrow other cards' faces as well, and this behaviour is kept as-is. Changing it means naming a different source (`normalArtCardId` / `spellArtCardId`) or dropping a `card.png` into the card folder.

**In-bundle object-path correction (this is what fixes the black deck-list cards)**: a card material's object path only comes in two folders, `Card/Spell/Materials/` and `Card/Field/Materials/`, and for some cards the official data contradicts itself — the card's kind and the folder its artwork really sits in disagree (measured example: Dragon Egg is `charType=FIELD` yet its material lives in the spell folder). The plugin swaps the folder the engine asks for with the folder that was **observed** to hold the material (same bundle, so no extra bundle load), and every correction logs `[CardArt] <material id>_M is really stored under /Field|/Spell/Materials/; corrected the engine's object path …`. It deliberately does **not** infer the folder from `asset not found`: that error also fires while a bundle is simply not loaded yet, and an earlier version that inferred from it broke requests that had been correct.

**That "observed folder" may only come from a lookup that really hit (fixed 2026-09-23; this was the magenta hand card)**: the `MaterialKindByMaterialId` cache used to be filled in the `FindCardMaterial` postfix from the **caller's declared `type`**, but `FindCardMaterial` *ignores* that argument for ≥10-digit material ids and always builds `Card/Field/Materials/`. Recording the declaration therefore labelled a material that physically lives in the field folder as "spell", and a wrong record does not mean "no correction" — it means **a correct request gets rewritten into a broken one**. Measured on the 肃清 (Purge) choice cards: all eight of them (`820144031`, `820244021`, `820344031`, `820444081`, `820544071`, `820644051`, `820744051`, `820844051`) borrow from bundles (`card_121X410300.unity3d`) that contain **only `card/field/materials/`**, and for `1214410311_M` (粉碎肃清的爪牙) and `1215410311_M` (拒绝肃清的激情) the rewrite pointed at a spell path that does not exist → `LoadObject` returned null → the hand cards rendered **magenta with no artwork** (the last two lines of that Player.log are exactly those two corrections). Now there are three layers:
1. The folder is recorded only from the object path that **really resolved** (`CardMasterPatcher.RememberMaterialKindFromObjectPath`, fed by the `AssetManager.LoadObject` postfix), and a measurement **overwrites** a declaration, so one bad guess cannot stick;
2. `AssetManager.LoadObject` got a **folder fallback** (`ResolveCardMaterialObjectPath`): when the requested folder misses, the other field/spell folder is tried, and on a hit the real folder is recorded and `[CardArt] '…' is in the other folder; loaded '…' instead` is logged. A kind/folder mismatch can therefore **never blank or magenta a card again**, and no guessing is involved; the `materials`, `textures` and `header` folder pairs are all covered;
3. When preloading borrowed artwork, `AddArtSourceBundle` now resolves the bundle name through `ResolveArtBundleName` and skips bundles that are not on disk (the `<id>0.unity3d` name derived from a 10-digit material id does not exist locally; queueing it only makes the engine look for nothing).

Diagnose faces with `_tools/find_material_bundle.py` (material name → the bundle that really holds it, plus its in-bundle object path) and `_tools/_bundle_materials.py` (list the materials a card bundle actually holds under `card/field|spell/materials/` — the way to prove which folder a material is in). The log no longer contains any temporary-probe lines (`[ArtProbe]`, `FINAL NO FACE:` and friends were removed together with the probe).

The other half of “a card turns black in the deck editor and a refresh fixes it” is the material cache: textures are destroyed when their bundle unloads, which leaves a black entry behind. Cache hits now verify the texture is still alive and rebuild otherwise, and a material without a texture is never cached at all.

**Card art turning magenta mid-battle (the material was collected)**: the game calls `Resources.UnloadUnusedAssets` when moving between screens and during battles (the `Unloading N unused Assets` lines), and **any material or texture with no managed reference is destroyed outright**. The plugin hands each borrowed card face (another card's artwork, an asset bundle pulled in on demand) to the engine and then keeps no reference, so mid-battle the face disappears — a destroyed **material** shows as **magenta** (Unity's missing-material colour) while a destroyed **texture** alone shows as a black card (the deck-editor symptom above). `CardMasterPatcher.KeepArtAssetsAlive` now records every material and texture it hands out through `ResourcesManager.FindCardMaterial` (the common exit for UI *and* battle card faces) and through the borrowed-face path in a static list — a static-field reference counts as "in use", so the sweep leaves them alone.

**Transparent hand cards in battle (技巧/秘术) — fixed; the root cause was the mesh, not the artwork**:

- the face material was always resolved correctly (`FindCardMaterial(<resource id>, Spell, …, choiceBrave=true)` returns a material with a 1024×1024 texture), so **the artwork was never the problem**;
- the engine has a dedicated assembly path for choice cards inside `CardTemplate`: it swaps the card mesh to `md_card_heroskill`, uses a `CardFrame_HS_*` frame and fills the material array as `[frame, face, class icon]`;
- `md_card_heroskill` / `md_card_heroskill_low` are only loaded when `Wizard.Data.CurrentFormat == 39` (they are part of `SBattleLoad`'s `cardPrefabPathList`; the same switch also gates `_choiceBraveCardFrameMaterials`). Offline story/practice battles are not format 39, so `GetChoiceBraveCardMesh()` returns null, the engine assigns null to the face `MeshFilter` — **the material array was right all along, there was simply no mesh to draw it on**, which reads as “transparent”;
- the plugin intercepts `GetChoiceBraveCardMesh` on both `BattleResourceMgr` and `NullBattleResourceMgr` and hands out the **unconditionally preloaded** normal card meshes (`md_card_spell` / `md_card_spell_low`, whose submesh layout matches: frame / face / class icon), so the mulligan screen, the draw animation and the hand all show the face from the first frame. A safety net remains: any `md_Card_Spell*` mesh filter that is empty or still on the hero-skill mesh gets the normal mesh, and the layer/sorting are aligned to whatever the normal hand cards in the same battle actually use;
- **lessons not to repeat**: never touch the shader the engine chose (forcing `Basic_Front0_Back0_Normal` renders the card magenta), and never attach the face material to `NormalField` / `EvolField` (those two layers are supposed to stay inactive on hand cards).

### Who the story "character select" screen shows

`StoryLeaderSelectTask` used to return “the default character of that class” for every class, so every chapter showed the plain class avatar. The leader list is now taken from official data — **a character-story section's leaders are not per class but the section's protagonists** (section 15 → `500701/500732/500704`, section 12 → `500401/500402/500403/500404`), in this order:

1. `<resource root>/story_ai/story_leaders.csv` (two columns, `section_id,chara_id`, mined from the official captures by `_tools/harvest_story_leaders.py` — the most authoritative source);
2. the `chara_id` of each chapter of that section in `<resource root>/story_ai/story_battles_official.csv`, deduplicated in order (covers all eight character-story sections: 1/3/5/7/10/12/15/17);
3. only when neither exists does it fall back to “the character that class uses in the story → class default”.

This matches the official `main_story/leader_select` captures entry by entry (section 1 → `1..8`, section 15 → `500701/500732/500704`). Audit every section with `_tools/audit_story_leaders.py` (it lists whether a section is a character-story section and whether official leader data is available).

Characters with `is_usable = false` in `master_class_chara_master` (story-only NPCs) are skipped **on the fallback path only** — in official data those `500xxx` protagonist ids are exactly what the UI displays, so they must not be filtered there. The data comes from `<resource root>/story_ai/class_chara_usable.csv` (two columns, `chara_id,is_usable`, exported from `master_class_chara_master` by `_tools/export_class_chara_master.py`); when that file is absent every character counts as usable.

Verify offline with `_tools/verify_story_leaders.py` (prints who each chapter resolves to and where it came from) and `_tools/avatar_asset_gaps.py` (whether `ui_class_<id>.unity3d` exists locally / on the CN client).

## P2P rooms

Socket.IO room mode does not connect to the official servers and does not need a permanently hosted server of your own. While the game is running, the host temporarily listens on a Socket.IO WebSocket endpoint and serves the room flow and realtime message relay.

Battle execution is Host-authoritative. The guest sends only an input request
(play, evolution, fusion, attack, turn end, and selections); the host executes
that request with the stock battle manager and returns stock-shaped
`PlayActions`, turn-transition, and result packets for the guest to replay.
Both installations exchange the private card/state baseline needed by the host
at battle setup, then send compact authoritative state changes with each
result. This is intended for trusted friend matches, not anti-cheat.

1. Both players install the same version of the game, of Shadowbus and of any card mod data.
2. The host picks standard-constructed BO1 in the stock room screen and creates a room.
3. The host clicks copy room number. The clipboard receives an encrypted connection code starting with `SVP1-`, not the short room number shown on screen.
4. The guest pastes the full `SVP1-...` code into the connection-code field of the join dialog. The confirm button enables itself once the code validates.
5. Both players choose a deck and ready up, then the battle starts through the stock room flow.

The connection code carries the host's address, Socket.IO port, BattleId and an integrity check. It is the password for that room, so do not publish it. Old codes stop working once the room closes or the game exits.

### Network requirements

P2P mode provides no account service, room list, STUN hole punching or TURN relay. The two players must satisfy one of the following:

- Both are on the same LAN, and the connection code carries the host's LAN address.
- The host has an inbound-reachable public IPv4 and has opened the configured Socket.IO port in both the router and the system firewall.
- Both have mutually reachable IPv6, the host sets the bind and advertised addresses to that IPv6, and the firewall is open.
- Both join a virtual LAN such as Tailscale, ZeroTier or Radmin VPN first, and the connection code carries the host's virtual adapter address.

If the host is behind carrier-grade NAT with no usable IPv6, a virtual LAN is required; a connection code alone cannot traverse that kind of NAT.

After the first launch, the `[SocketIO]` section of this plugin's file under `BepInEx/config/` can be edited:

- `BindAddress` — the local address the host listens on. Defaults to `0.0.0.0` for IPv4.
- `AdvertisedAddress` — the address written into the connection code. When empty, an explicitly configured `BindAddress` is preferred, otherwise a local address of the same family is chosen automatically. Set it explicitly across the public internet or on a virtual LAN. When using IPv6, both entries must be IPv6 addresses.
- `Port` — the Socket.IO port the host listens on, default `29600`.

Currently supported: standard-constructed Open Room BO1 and Room Two Pick BO1 with custom rules. Not supported: HOF, Windfall, Avatar, stock Backdraft/Cube/Chaos Two Pick, BO3/BO5, spectating, rewards and anti-cheat.

Each JSON file under `Mods/TwoPick` is one two-pick mode selectable when creating a room, with `displayName` as the label. Both players draft locally; the host syncs the full ruleset to the guest, and the final decks then enter matchmaking. If the connection drops mid-battle, the player still online wins by default.

Every game installation keeps its own player ID in `Mods/P2PIdentity.json` and its edited name, title, emblem and region in `Mods/Profile.json`. Do not copy a generated identity file to another player or to a second test instance.

### Focus loss, disconnects and stalled battles (2.5.7)

**Keep the game window focused while playing online.** When the window loses focus the game itself pauses the
battle and blocks every card action (`UnityEventAgent.OnApplicationFocus(false)` → `m_battleMgr.Pause()`, and
every touch processor checks `HasFocus`), while the Socket.IO heartbeat, the server relaying and the turn timer
all keep running — so alt-tabbing can cost you the turn by timeout. Since 2.5.7 the plugin says so explicitly:

```
[Focus] 游戏窗口失去焦点：对局已暂停，牌桌不再接受任何操作，但回合计时器与网络连接仍在继续……
[Focus] 游戏窗口重新获得焦点：对局恢复，可以继续操作。
```

Disconnect handling added/strengthened in 2.5.7:

- **Half-dead connections are reaped.** When a peer vanishes silently (unplugged cable, dropped VPN, killed
  process) TCP never reports it and the socket stays established. The server now tracks the last received
  packet: a slot stops counting as live after **30 s** of silence and the connection is closed after **45 s**.
- **Slot takeover.** A reconnect whose old connection has been silent for more than **8 s** takes the slot over
  (no more endless `already connected` retries), and the superseded connection is *not* settled as a
  disconnect loss.
- **Terminal result re-delivery.** A player who happened to be offline at the moment the battle ended gets their
  own `BattleFinish` re-sent on reconnect instead of looping in a "waiting for the result" reconnect storm.
- **Honest opponent status.** The heartbeat now reports `ONLINE / TIMEOUT / OFFLINE / WAITING` from the
  opponent's last heartbeat, so a silent peer no longer looks online forever.
- **Stalled-battle warning.** If an in-game room goes **150 s** without a single battle frame, the log gets an
  `[Online]` warning with the last frame, both sides' idle time and the local window focus state.

## Enhanced logging

### The freeze: cause and fix (fixed in the plugin)

Symptom: the picture is still there, the window reacts, CPU is near zero, but nothing happens any more
(measured: entering the story screen stops right after
`[Offlinizer] Intercepted Task: StoryInfoTask. Reading local data...`), and "sometimes it recovers after a long time".

The cause is the plugin's own network busy flag: **in the whole game assembly only plugin code ever sets
`NetworkManager.isConnect` to true** (verified by decompilation — the game itself only ever sets it to **false**, in
`StopConnectCoroutine` and in the `<Connect>d__18` coroutine we replace wholesale). The plugin uses that flag as a
"previous request still running" mutex:

```csharp
while (__instance.isConnect) { yield return 0; }   // entry of all three task paths
```

The real-request path `throw`s all over the place (MessagePack decryption failure, `HandleDeserializeException`,
`NetworkManager Connect Error 2/3` …): once an exception escapes the coroutine, the trailing `isConnect = false`
never runs and the flag stays true — every later task then blocks on that `while` and the whole game "freezes", while
an occasional `StopConnectCoroutine` clears it again, which is the "it recovers after a long time" behaviour.

Fix (`FakeConnect.cs`):

1. all three entry points now use `WaitUntilNetworkIdle`: it waits at most 3 seconds, then clears the flag itself and
   continues, logging `[Offlinizer] The network busy flag was still set …` (a deadlock is now impossible);
2. `ProcessOnlineTask` is wrapped in `try/finally`, so even when an exception escapes it always runs
   `ClearLastRequestTask()` + `isConnect = false` (the root fix).

### Freeze watchdog (`[Task]` / `[Hang]`, temporary diagnostics)

When the game freezes the log usually says nothing at all (measured: entering the story screen stops right after
`[Offlinizer] Intercepted Task: StoryInfoTask. Reading local data...` — no further line — while the process still
renders frames at ~0% CPU and the window stays responsive, i.e. the main thread is **idling on some condition**,
not spinning or crashing). `TaskWatchdog` (`TaskWatchdog.cs`, temporary diagnostics, removed once the bug is closed)
watches `NetworkManager.lastRequestTask` every frame from `Plugin.Update`:

- `[Task] '<task>' finished after Ns (next: '<next>')` when the pending task changes;
- `[Hang] task '<task>' has been pending Ns (isConnect=…, isTimeOut=…, isError=…, op='<current plugin op>')`
  every 5 s while one task stays pending, at most 12 lines per battle.

Read-only, log-only. The next freeze will name the stuck task, the network flags and what the plugin is doing.

### Online freeze forensics (new in 2.5.7)

- `[Focus] …` — records that the game window lost focus, which is what makes the board accept no input while
  the heartbeat and turn timer keep running. Both reported "online freezes" turned out to be exactly this.
- `[Online] room <id> has received no battle frame for N seconds…` — fired after 150 s without any battle frame
  while in game, including the last frame (uri/seq/sender), both sides' idle time and the local focus state.
- `[SocketIO] Closing a silent connection…` / `Opponent … has been silent for Ns` — half-dead reaping and
  honest peer-timeout reporting.
- Plugin logging is now serialised through a single lock (`LockedLogSource`), so concurrent writers
  (Socket.IO threads, the watchdog, the main thread) no longer interleave half-lines in player-submitted logs.

### Verifying patches before release (`_tools/PatchCheck`)

Harmony injects parameters by name; a typo makes the whole patch silently fail to attach and the compiler
cannot see it. `_tools/PatchCheck/` loads the plugin plus `Assembly-CSharp` and checks every `[HarmonyPatch]`
target and every injected parameter / `___field`:

```powershell
dotnet build _tools/PatchCheck/PatchCheck.csproj -c Release
_tools/PatchCheck/bin/Release/net48/PatchCheck.exe `
    "<game>\BepInEx\plugins\Shadowbus.dll" `
    "<game>\Shadowverse_Data\Managed\Assembly-CSharp.dll"
# patch definition(s): 268, patched target method(s): 269, mismatch(es): 0
```

Shadowbus formats logging at BepInEx's global dispatch boundary, so ordinary modules, forwarded Unity/BepInEx messages and online logs all share one format. BepInEx's own `[Level:Source]` prefix is kept only once; level colours are applied only at the external console boundary, so text logs never contain ANSI escapes. Overlong content is wrapped according to the configuration, reserving width for the prefix.

The enhanced log is saved separately as `BepInEx/LogOutput-enhanced.log` and never replaces the original log file. A card ID that resolves in the current `CardMaster` is annotated with its name and cost; card effect text is off by default to keep ordinary logs short.

The `[Logging]` section of the config file:

| Setting | Default | Meaning |
| --- | --- | --- |
| `EnhancedEnabled` | `true` | Enable the global enhanced format and the enhanced log file. |
| `ShowCardDetails` | `true` | Annotate card IDs as "name + cost". |
| `ShowCardEffects` | `false` | Append card effect and evolved effect text. |
| `DetailedOnline` | `false` | Log detailed online battle messages; whether card effects appear is still controlled by `ShowCardEffects`. |
| `AnsiColors` | `true` | Use real console colours by log level in BepInEx's external console; `Player.log` and the enhanced log file always stay plain text. |
| `WrapWidth` | `140` | Maximum characters per line; `0` disables wrapping. |
| `EnhancedFile` | `BepInEx/LogOutput-enhanced.log` | Path of the enhanced log; relative paths resolve against the game root. |

`DetailedOnline=true` is useful for reproducing online issues; keep it off for normal play to reduce log volume and card-text lookups.

Emote-text lookups for story battles no longer flood the log: the hundreds of per-character lookups done while preloading the character select screen are only counted, and each battle start emits a single summary line (`[StoryAI] Emote text lookups since the previous battle: ...`). Only ids that actually fail to resolve **during that battle** are printed individually (up to 30, with a caller stack trace to identify the requester). In addition, `ET_*` ids that are missing from the local emote text master now resolve to an empty string (a `Master.GetEmoteWordText` fallback), so the UI no longer shows raw keys such as `ET_進化_2_500211`.

## Custom practice

Go to **Solo > Battle** and click the blue "**自定义对手**" button centred above the class buttons on the opponent class selection page. The setup page has two columns:

- **Left**
  - "我方设置" → **我方卡组**
  - "对手设置" → **选择剧情AI** (first row), **对手卡组**, **职业**, **生命上限** (slider)
  - "对手主战者皮肤" → leader portrait strip with paging (paging buttons are greyed out too while a story AI is selected)
- **Right**
  - "LLM AI" switch (top row)
  - "自定义对手 AI 数据" → **选择官方预设AI**, **AI 逻辑** (weak / middle / strong), **牌组 / 风格 / 表情 CSV**, **刷新 CSV**

Only the **opponent** AI is configurable: the player AI is permanently off, so the page has no player-AI switch, no player-AI CSV rows and no "player stock preset" — those were **deleted**, not hidden.

"**选择剧情AI**" picks one of the official scripted-story enemy AIs from the master's `story_ai_setting` table (using the CSVs exported into the resource folder's `story_ai/`). The first entry is a fixed "**无**" (none) that cancels story-AI mode. Selecting one hands the **enemy leader, class, deck, AI data and logic level** to that story AI, and pulls the life limit back to the official default of **20**. Mutual exclusion:

- while a story AI is selected, **the custom group (选择官方预设AI + the three CSVs), AI 逻辑, the class buttons, the leader strip and the LLM AI are all greyed out** — the official story AI uses `story_ai_setting`'s `logic_level` (STRONG for chapter 8 of arc 20) and its own leader, so those buttons cannot control it;
- touching any custom control picks "无" again and re-enables the greyed-out buttons.

Validation and LLM errors are shown at the **top of the page**, so they no longer cover the leader name.

Custom CSV files go into `Mods/AIData/deck/`, `style/` and `emote/` respectively (player-made data only; the official copy lives in the resource folder's `story_ai/`). Each file must keep the column layout of the corresponding stock CSV. Leaving an entry on "use stock preset" keeps the original AI data of the preset selected for that class. Files added while the setup page is open are picked up by the "refresh CSV" button.

### Leader voices for the AI

Supports custom leader voices. A ready-to-use file is provided at `Mods/AIData/emote/ai_emote_sample.csv`. See [Mods/AIData/README.md](Mods/AIData/README.md) for which voice numbers a skin actually has, and [Docs/AI_CSV_Guide.md](Docs/AI_CSV_Guide.md) for the full CSV syntax.

### AI behaviour settings

The `[AI]` section of the config file under `BepInEx/config/` controls how the AI handles cards the stock AI data never described:

| Setting | Default | Meaning |
| --- | --- | --- |
| `StallTimeoutSeconds` | `30` | Seconds the AI may make no progress before its turn is force ended. `0` disables. |
| `UnknownCardPlayBonusMin` | `0.5` | Lowest play bonus given to a card with no AI data. |
| `UnknownCardPlayBonusMax` | `1.5` | Highest such bonus. Set both to `0` to keep only the crash fix. |
| `PriceUnpricedCards` | `true` | Score spells and amulets whose tags describe an effect but never give it a value. |
| `RespectPlayLimitLocks` | `false` | Leave cards the stock data locked with a `playLimit` tag unpriced. |
| `LowLifeHealThreshold` | `10` | Leader healing only scores at this much life or less. |

## Card mods

Card patches live in `Mods/CardMaster/`:

- `.json` files are loaded; `.example` files are samples only.
- With `newCard` set to `false`, the card matching `templateCardId` is modified.
- With `newCard` set to `true`, a new card with `cardId` is created from `templateCardId`.
- `intFields` changes numeric fields.
- `intArrayFields` changes integer or enum array fields; card traits use `"Tribe": [trait enum values]`.
- `stringChangeFields` replaces string fields such as abilities.
- `stringAppendFields` appends to the original string.
- `localizationFields` changes the card name, ability text and flavour text. A changed name is registered as a card keyword, so it can be clicked inside card text to open that card's details.

Opening the deck list hot-reloads the configuration (the card table is not rebuilt when nothing under `Mods` changed). New cards should use an unused card ID. Artwork and audio live in the card's **own folder** (`Mods/CardMaster/<card folder>/`); the global `CardImages/` and `CardVoices/` folders have not been created or read since 2.5.5, and since 2.5.6 they are not even part of the hot-reload signature any more (leftover empty folders can simply be deleted). The full layout and naming rules are in `Mods/CardMaster/mod制作教程/mod卡教程.md` inside the game folder.

- Artwork defaults to `card.png` (and `card_evo.png` for the evolved face) — drop those two files in and they apply automatically. You can also name files freely through `imageFiles`, or keep using `<ResourceCardId>.png` / `<ResourceCardId>_evo.png`. When the evolved image is absent the base one is used.
- Local voice files are declared with `voiceFiles` (paths relative to the card folder, any file name), or picked up automatically under the conventional names `play.wav`, `evolve.wav`, `attack.wav`, `attack_evolved.wav`, `destroy.wav`, `destroy_evolved.wav`. 16-bit PCM WAV is recommended.
- Both artwork and audio may sit in a sub-folder of the card folder as long as the declared path is relative (`art/card.png`); the conventional names are only looked for in the card folder root.
- A single sub-folder may hold several json files and several cards; they all load (do not split one new card across two files).
- Patching an existing card applies to both its normal and animated versions while keeping each version's own identity fields.
- `stringArrayFields` replaces `string[]` fields such as `SkillEffectPath`, `SkillSe` and `EvolEffectPath`.
- `normalArtCardId` / `evolutionArtCardId` / `spellArtCardId` borrow another stock card's face (base, evolved, and the spell/amulet face respectively) and reframe it to the borrowing card. The borrowed face is not in the bundle of the borrowing card, so when that face is requested the plugin **asynchronously** asks for the source bundle; once it lands, the next lookup (for example re-opening the deck list) finds it. This path deliberately never loads synchronously: the request happens while the engine is loading assets itself, and a blocking bundle load re-entered from inside a bundle load hangs the main thread for good.
- In-game card export writes card traits to `intArrayFields.Tribe`; for example, Officer is `[2]` and Machina is `[7]`.

Deck-edit search results and the card gallery are sorted by ascending cost, keeping the stock order within equal costs. The ordering follows the effective `Cost` from `intFields`, so configuring `SortIndex` by hand is normally no longer necessary.

The project ships these extensions:

| Keyword | Purpose |
| --- | --- |
| `when_activate` | Adds an activation timing to your followers on the field. Use `use_pp=N` in `SkillPreprocess` to set the PP cost. |
| `skill_geminize` | Copies the target follower's name, type, stats, text and every ability, and clears the user's own abilities apart from this one. |
| `skill_acquire_skills` | Gains the target follower's abilities and non-stat buffs while keeping the user's own. Abilities of the same type and attack or defence modifiers are not copied. |
| `skill_mirror` | When the user is chosen as the target of a spell or single-target ability, applies that effect once more to a random follower of the caster. |

`skill_mirror` accepts `all=true/false`, `include_self=true/false` and `ability=true/false`. The first two control whether the extra effect hits one random follower or all of them, and whether the mirroring target itself can be picked. With `ability=true`, single-target abilities that explicitly name this follower also trigger it, in addition to spells; random and area effects never do. The defaults are `false`, `true` and `false`.

See the samples and existing card files under `Mods/CardMaster/` for concrete configuration, and [Mods/readme.md](Mods/readme.md) for more ability timings.

### CardMaster attack effects

`attackEffectFields` sets the normal and evolved attack presentation. Every value is `[normal, evolved]`: `effectPath`, `se`, `moveType`, `effectEnginType` (`NONE`/`SHURIKEN`/`FLATOUT`/`SOLID`) and `time`. Empty fields keep the template card's original value.

```json
"attackEffectFields": {
  "effectPath": ["btl_attack_1", "btl_attack_2"],
  "se": ["se_btl_attack_1", "se_btl_attack_2"],
  "moveType": ["DIRECT", "DIRECT"],
  "effectEnginType": ["SHURIKEN", "SHURIKEN"],
  "time": [0.5, 0.5]
}
```

### CardMaster borrowed effects (new in 2.5.6)

Instead of typing effect and SE names, borrow another card's whole presentation (official or mod card alike) — the same idea as `normalArtCardId` for artwork and `extraVoiceIds` for voice banks:

| Field | Engine fields copied from the source |
| --- | --- |
| `summonEffectCardId` | `SummonEffectPath` / `SummonSePath` / `SummonMoveType` / `SummonEffectType` / `SummonTime` |
| `destroyEffectCardId` | `DestroyEffectPath` (the destroy presentation has no SE field) |
| `evolveEffectCardId` | `EvolEffectPath` / `EvolSePath` / `EvoEffectType` / `EvolTime` |
| `attackEffectCardId` | `AtkEffectParameter` (`_effectPath` / `_se` / `_moveType` / `_effectEnginType` / `_time`) |
| `skillEffectCardId` | `SkillEffectPath` / `SkillSe` / `SkillMoveType` / `SkillEffectEnginType` / `SkillEffectTime` / `SkillEffectTargetType` plus the matching `EvoSkill*` six |
| `effectBorrowCardId` | all of the above (per-slot fields are more specific, and fields written in the json itself win over borrowed ones) |

```json
"summonEffectCardId": 125441020, "evolveEffectCardId": 125441020, "attackEffectCardId": 125441020
```

Points worth knowing:

- **Presentation only, never the ability**: `Skill` / `SkillTiming` / `SkillCondition` / `SkillTarget` / `SkillOption` / `SkillPreprocess` are untouched, so "looks the same, plays differently" is allowed.
- **Order**: `effectBorrowCardId` → per-slot fields → the json's own `stringChangeFields` / `stringArrayFields` / `attackEffectFields`; the most specific declaration is applied last.
- **The bundles are made resident**: the engine fetches `Effect/Effects/<name>` out of `effect_<name>.unity3d`, and a mod card has no such bundle of its own; the plugin appends the borrowed `effect_<name>.unity3d` (when it exists on disk) to the game's asset-group list, while the skill SE (`s/<se>.acb`) is requested by the engine from the card's own fields. Logs: `[CardEffect] card … borrows … effects from …` and `added N borrowed effect bundle(s) to the load list`.
- The copy is taken from a **deep clone** of the source card, so nothing mutable is shared with it, and the auto-derived foil companion gets the same treatment.
- Implementation: `CardParameterPatch.ApplyBorrowedEffects` / `BorrowEffectFields` / `CollectBorrowedEffectResources` in `CardMasterPatcher.cs`, called before `PatchTemplate`; the registry is cleared with `RevokeCardMasterPatches`.
- Worked example: `Mods/CardMaster/约束的正义·伊兰翠/` (a card ported from Shadowverse: Worlds Beyond, with notes on its data, ability and the asset gaps).

## Building

```powershell
dotnet build Shadowbus.sln -c Release
```

Releases use the `Release` configuration; the output is `bin/Release/net46/Shadowbus.dll`. Without `-c`, `dotnet build` defaults to `Debug` and produces `bin/Debug/net46/`, whose IL is unoptimized — do not ship it.

Building requires the .NET SDK (which provides the `dotnet` command). The NuGet sources are declared in `RestoreAdditionalProjectSources` inside `Shadowbus.csproj`, so the first build restores them automatically.

The project references the game's `Assembly-CSharp.dll`. The game folder is located in this order, and **the repository hardcodes no user name or machine-specific path**:

1. `Shadowverse.local.props` next to the project (already `.gitignore`d), containing `<Project><PropertyGroup><ShadowverseDir>game folder</ShadowverseDir></PropertyGroup></Project>`;
2. the environment variable `SHADOWVERSE_DIR`;
3. the usual Steam libraries: `<repo>\..\..\..\SteamLibrary\steamapps\common\Shadowverse` and `%ProgramFiles(x86)%\Steam\steamapps\common\Shadowverse`;
4. a `Shadowverse` folder within two levels around the repository.

If none of them exists the build stops with one clear error instead of a wall of missing `Wizard`/`Cute` errors. **The game does not have to live in a Steam library, and no directory junction is required.**

## Notes

- This project targets offline play and mod testing.
- Back up your card configuration before editing it.
- A battle already in progress does not rebuild cards that have been created, so start a new battle when testing configuration.
- Do not ship `.lnk` shortcuts (they store absolute paths); use the portable `.bat` launchers instead.
