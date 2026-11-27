# Grim Dawn – Easier Progression

A settings UI that builds a personal Grim Dawn mod (`mods/EasierProgression`) with adjustable:

| Setting | What it changes in the game database |
|---|---|
| **Monster XP** | Wraps the four kill-XP equations in `records/game/experienceformulas.dbr` with `(...)*x`. Level curve, skill/attribute/devotion points are untouched. |
| **Friendly rep per kill** | Scales `killBonus*` on faction controllers (e.g. killing beasts → Devil's Crossing / Coven). |
| **Hostile rep per kill** | Scales `killPenalty*` (how fast enemies hate you → Nemesis spawns). |
| **Remove diminishing rep** | Sets `common/championMonsterGainReduction` to 1.0. |
| **Legendary / Epic / MI chance** | Reweights loot tables, monster loot slots and chests so each tier's chance becomes roughly `vanilla × multiplier`. The number of drops is unchanged: common gear gives up the share. Crafting materials, blueprints and other non-gear drops keep about their vanilla chance (they can rise where they share a table with boosted items). Tables keep one set of weights for every level, so where the items underneath change with character level the boost is a compromise across levels: e.g. a boss slot whose Epic table gives common items below level 6 gets its full Epic boost later at the cost of a few percent fewer MIs at levels 1–5. Epic/Legendary are filled first, then MI, so raising MI never reduces the Epic/Legendary boost. In guaranteed boss slots with no common items (MI *or* Epic), more Epics means slightly fewer MIs. |
| **Drop rate (chance / chest items)** | **Monsters:** multiplies each loot slot's drop *chance* (`chanceToEquip*`, capped at 100%), not the number of items. A slot still drops at most one item per kill, so 30% becomes 60% (×2 on average) but 70% can only become 100% (×1.43). **Chests and breakables:** multiplies how many items they roll. Technically it multiplies monster slot chances and wraps chest/breakable item counts (`numSpawnMin/MaxEquation`) with `(...)*x`, like GDModdingTool's `AdjustLootAmount`. What a slot drops is unchanged, so Epics/Legendaries scale with it. Guaranteed (100%) boss/hero slots and cosmetic-only slots (e.g. zombie rags) stay as they are. |
| **Gold amount** | Wraps the gold formulas in the 18 shared gold-generator records with `(...)*x` and scales their min/max clamps to match. |
| **Free inventory bags** | Kerrick, the Devil's Crossing merchant, has 5 Inventory Bags for 0 gold from the start, restocked whenever his stock rerolls: his unused Potion stock category (`merchanttbl_a01`) is pointed at the bag with a count of 5. The Devil's Crossing faction vendor also always stocks them once you're Friendly (`marketStaticItems` on `devilscrossing_friendly_01`). |
| **Rare affixes / prefix+suffix / plain items** | Scales the affix-combination weights in `LootItemTable_DynWeight` tables. |
| **Stat rolls (min/max %)** | Moves each affix's roll range inside its vanilla range (see below). |

## Use

1. Run `GrimDawnEasierProgression.exe`. It needs nothing installed: it runs on .NET Framework 4.8,
   which ships with Windows 10 and 11.
2. Pick a preset or move the sliders, then click **Build / Update mod** (~8 s).
3. In Grim Dawn: **Play → Custom Game → EasierProgression**. Create a new character or bring one in.
4. Rebuild after every game patch so the mod always copies current game data.

Settings are saved in `settings.json` next to the exe. Headless rebuild with them:
`GrimDawnEasierProgression.exe --build` (optionally `--settings other.json`).

## Building from source

Needs the .NET SDK (8 or newer; `winget install Microsoft.DotNet.SDK.8`):

```
dotnet build src -c Release
```

The exe is written to `src/bin/Release/net48/`.

## How the stat-roll setting works

Every affix record has `lootRandomizerJitter` = J (0–50). A stat with base value V rolls in
`V·(1−J%) … V·(1+J%)`. For a new minimum at *a*% and a new maximum at *b*% of that range, the
builder scales the affix's stat fields by `s = 1 + J·(a+b−1)` and sets the jitter to
`J' = J·(b−a)/s`, so the new range is exactly `[min + a·(max−min), min + b·(max−min)]`.

Example: Min 50 %, Max 100 % turns `+16–24% Physical Damage` into `+20–24%`.

Applies to magic/rare prefixes and suffixes, MI affixes, blacksmith crafting bonuses and Ascended
affixes. Epic/Legendary base stats have no jitter field in the data, so they stay as they are.
Proc chances, durations, conversions and skill bonuses are not scaled.

## Notes

- Kerrick's stock is rolled per category (a loot table plus a min/max count). The bag gets his
  unused Potion category with a count of 5, so they're always there and he loses no regular stock.
  Vanilla uses the same trick to sell a quest item (Benevald's Chest category). The faction vendor
  (fixed, always-in-stock list) is a second source.
- The bags are not handed out through a quest. That is possible (GDModdingTool's optional mod makes
  the first quest give all five), but quests are compiled binary files in `resources/Quests.arc`
  with an undocumented format, edited only with Crate's QuestEditor/Asset Manager. The mod would
  have to ship a hand-edited copy of the whole quest, which a game patch could silently undo or
  break. This builder only writes database records, copied fresh from the game on every build.
- Each mod has its own saves, separate from vanilla. Back up `Documents\My Games\Grim Dawn\save` first.
- Items only store their affix references and a seed, so changing the roll settings also re-evaluates
  items you already own in this mod.
- Bounty and quest reputation and quest XP live in quest files, not the database, so they're unchanged.
- The ±J reading of the jitter value is inferred from the data and templates, not from Crate docs.
  It was checked in game with Min 100 / Max 100: affixes rolled at the top of their vanilla range.
