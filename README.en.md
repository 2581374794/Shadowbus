# Shadowbus

[简体中文](README.md) | **English**

An offline-play and card-modding toolkit for the international build of Shadowverse, built on BepInEx 6.

The repository also ships an [all-in-one web configuration editor](WebEditor/README.md) that can be deployed to GitHub Pages. It edits AIData, BossRush, CardMaster, Format and TwoPick configuration through forms, and can either read and write a local `Mods` directory or export a complete ZIP.

## Features

- **Offline play** — home screen, Unlimited deck editing, CPU battles and pack-opening animations all work without a server.
- **Everything unlocked** — all cards, leader skins, sleeves and home backgrounds are available by default, and the background choice is saved locally.
- **Unlimited decks** — class, per-card and deck-size limits are ignored, and tokens can be added to a deck.
- **Custom practice** — choose the opponent's deck, class, leader and AI CSV files.
- **Card mods** — modify or add cards, including custom artwork and text.
- **Deck list hot reload** — `CardMaster` configuration is reloaded whenever you open the deck list.
- **Clickable card names** — names changed through `localizationFields` are registered as card keywords, so they can be clicked inside card text to open that card's details.
- **Card list ordering** — deck-edit search results and the card gallery are sorted by cost, keeping the stock order within equal costs.
- **Active abilities** — `when_activate` adds an activate button to your followers on the field, with a configurable PP cost.
- **Custom abilities** — copy a card's information and abilities, or gain a target's abilities while keeping your own.
- **Socket.IO rooms** — the host runs an embedded Socket.IO node and generates an encrypted connection code; the other player joins through the stock realtime client.

Only Unlimited decks are supported for now. AI improvements are ongoing.

## Installation

### Prebuilt package

Download the BepInEx and plugin archives from [Baidu Netdisk](https://pan.baidu.com/s/1iNJ7HMVR2cbV1aKvLzI2AA?pwd=7ejh) and extract them into the Shadowverse game root.

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
   ├─ CardMaster/
   └─ CardImages/
```

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

Currently supported: standard-constructed Open Room BO1 and Room Two Pick BO1 with custom rules. Not supported: HOF, Windfall, Avatar, stock Backdraft/Cube/Chaos Two Pick, BO3/BO5, spectating, reconnection, rewards and anti-cheat.

Each JSON file under `Mods/TwoPick` is one two-pick mode selectable when creating a room, with `displayName` as the label. Both players draft locally; the host syncs the full ruleset to the guest, and the final decks then enter matchmaking. If the connection drops mid-battle, the player still online wins by default.

Every game installation keeps its own player ID in `Mods/P2PIdentity.json` and its edited name, title, emblem and region in `Mods/Profile.json`. Do not copy a generated identity file to another player or to a second test instance.

## Enhanced logging

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

## Custom practice

Go to **Solo > Battle** and click the ninth "custom deck" icon on the opponent class selection page. The setup page lets you choose:

- The opponent's local Unlimited deck, class and any leader you own.
- The stock AI preset, logic level and life total.
- Custom Deck, Style and Emote CSV files.

Custom CSV files go into `Mods/AIData/deck/`, `style/` and `emote/` respectively. Each file must keep the column layout of the corresponding stock CSV. Leaving an entry on "use stock preset" keeps the original AI data of the preset selected for that class. Files added while the setup page is open are picked up by the "refresh CSV" button.

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

Opening the deck list hot-reloads the configuration. New cards should use an unused card ID. Artwork goes into `Mods/CardImages/` and is referenced through `ResourceCardId`.

- Base artwork is named `<ResourceCardId>.png`.
- Evolved artwork is named `<ResourceCardId>_evo.png`; the base image is used when it is absent.
- Patching an existing card applies to both its normal and animated versions while keeping each version's own identity fields.
- `stringArrayFields` replaces `string[]` fields such as `SkillEffectPath`, `SkillSe` and `EvolEffectPath`.
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

## Building

```powershell
dotnet build Shadowbus.sln -c Release
```

Releases use the `Release` configuration; the output is `bin/Release/net46/Shadowbus.dll`. Without `-c`, `dotnet build` defaults to `Debug` and produces `bin/Debug/net46/`, whose IL is unoptimized — do not ship it.

Building requires the .NET SDK (which provides the `dotnet` command). The NuGet sources are declared in `RestoreAdditionalProjectSources` inside `Shadowbus.csproj`, so the first build restores them automatically.

The project references the game's `Assembly-CSharp.dll`; adjust the `HintPath` entries in `Shadowbus.csproj` if the game is installed elsewhere.

## Notes

- This project targets offline play and mod testing.
- Back up your card configuration before editing it.
- A battle already in progress does not rebuild cards that have been created, so start a new battle when testing configuration.
