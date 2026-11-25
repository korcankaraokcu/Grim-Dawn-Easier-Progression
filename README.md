# Grim Dawn – Easier Progression

A settings UI that builds a personal Grim Dawn mod (`mods/EasierProgression`) with adjustable:

| Setting | What it changes in the game database |
|---|---|
| **Monster XP** | Wraps the four kill-XP equations in `records/game/experienceformulas.dbr` with `(...)*x`. Level curve, skill/attribute/devotion points are untouched. |
| **Friendly rep per kill** | Scales `killBonus*` on faction controllers (e.g. killing beasts → Devil's Crossing / Coven). |
| **Hostile rep per kill** | Scales `killPenalty*` (how fast enemies hate you → Nemesis spawns). |
| **Remove diminishing rep** | Sets `common/championMonsterGainReduction` to 1.0. |
| **Legendary / Epic / MI chance** | Reweights loot tables, monster loot slots and chests so each tier's chance becomes roughly `vanilla × multiplier`. The number of drops is unchanged: common items give up the share. Epic/Legendary are filled first, then MI, so raising MI never reduces the Epic/Legendary boost. In guaranteed boss slots with no common items (MI *or* Epic), more Epics means slightly fewer MIs. |
| **Drop amount** | Multiplies each monster loot slot's drop chance (`chanceToEquip*`, capped at 100%) and wraps chest/breakable item counts (`numSpawnMin/MaxEquation`) with `(...)*x`, like GDModdingTool's `AdjustLootAmount`. What a slot drops is unchanged, so Epics/Legendaries scale with it. Guaranteed (100%) boss/hero slots and cosmetic-only slots (e.g. zombie rags) stay as they are. |
| **Gold amount** | Wraps the gold formulas in the 18 shared gold-generator records with `(...)*x` and scales their min/max clamps to match. |
| **Free inventory bags** | The Devil's Crossing faction vendor always stocks Inventory Bags for 0 gold once you're Friendly with them (`marketStaticItems` on `devilscrossing_friendly_01`). |
| **Rare affixes / prefix+suffix / plain items** | Scales the affix-combination weights in `LootItemTable_DynWeight` tables. |
| **Stat rolls (min/max %)** | Moves each affix's roll range inside its vanilla range (see below). |

## Use

1. `run_gui.bat` (installs `lz4` once if it's missing), or `python gd_easier_progression.py`.
2. Pick a preset or move the sliders, then click **Build / Update mod** (~30–50 s).
3. In Grim Dawn: **Play → Custom Game → EasierProgression**. Create a new character or bring one in.
4. Rebuild after every game patch so the mod always copies current game data.

Headless rebuild using the saved `settings.json`: `python gd_easier_progression.py --build`.

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

- The free bags are sold by the Devil's Crossing faction vendor, not by Kerrick (the regular
  Devil's Crossing merchant). Kerrick's whole stock is rolled at random from loot tables, so a bag
  there would only appear sometimes. Faction vendors have a fixed, always-in-stock list.
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
