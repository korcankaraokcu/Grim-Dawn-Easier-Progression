"""Turn Settings into modified copies of game records.

Every apply_* function takes the merged game database (dict lowercase-name -> Record) and adds
*modified copies* of the records it changes to `out`; the originals are never mutated.
"""
import re

from .arz import T_FLOAT, T_INT, T_STRING


def norm(name):
    return name.lower().replace("\\", "/") if isinstance(name, str) else ""


def _changed(out, db, name):
    """Return the working copy of a record, creating it on first touch."""
    if name not in out:
        out[name] = db[name].copy()
    return out[name]


# ---------------------------------------------------------------------------- XP
XP_RECORD = "records/game/experienceformulas.dbr"
XP_EQUATIONS = ("experienceEquation", "eliteExperienceEquation", "ultimateExperienceEquation",
                "challengeExperienceEquation")


def apply_xp(db, s, out, stats):
    if abs(s.xp_multiplier - 1.0) < 1e-9 or XP_RECORD not in db:
        return
    rec = _changed(out, db, XP_RECORD)
    for key in XP_EQUATIONS:
        eq = rec.get(key)
        if eq:
            rec.set(key, f"({eq})*{s.xp_multiplier:g}")
            stats["xp equations"] = stats.get("xp equations", 0) + 1


# ----------------------------------------------------------------------- Factions
FACTION_DIR = "records/controllers/factions/"
_BONUS = re.compile(r"^killBonus\d+(Boss|Champion|Common|Hero)$")
_PENALTY = re.compile(r"^killPenalty(Boss|Champion|Common|Hero)$")
_GAIN_RED = re.compile(r"^(common|champion)MonsterGainReduction$")


def apply_factions(db, s, out, stats):
    gain, hostile = s.faction_gain_multiplier, s.faction_hostile_multiplier
    if abs(gain - 1) < 1e-9 and abs(hostile - 1) < 1e-9 and not s.faction_no_diminishing:
        return
    n = 0
    for name, rec in db.items():
        if not name.startswith(FACTION_DIR):
            continue
        edits = {}
        for key, f in rec.fields.items():
            if f.type != T_FLOAT or not f.values or f.values[0] == 0:
                continue
            if _BONUS.match(key) and gain != 1:
                edits[key] = [v * gain for v in f.values]
            elif _PENALTY.match(key) and hostile != 1:
                edits[key] = [v * hostile for v in f.values]
            elif s.faction_no_diminishing and _GAIN_RED.match(key):
                edits[key] = [1.0] * len(f.values)
        if edits:
            c = _changed(out, db, name)
            for k, v in edits.items():
                c.set(k, v)
            n += 1
    stats["faction controllers"] = n


# --------------------------------------------------------------- Drop quality
GEAR_CLASS = re.compile(r"^(Armor|Weapon)")
MAX_LEVEL = 100
WEIGHTED_TABLE_CLASSES = {"LootItemTable_DynWeight", "LootMasterTable"}
DROPPER_CLASSES = {"Monster", "SpiritHost"}
CHEST_TEMPLATE = "database/templates/fixeditemloot.tpl"
_TABLE_ENTRY = re.compile(r"^lootName(\d+)$")
_MONSTER_LOOT = re.compile(r"^loot(\w+?)Item(\d+)$")
_CHEST_LOOT = re.compile(r"^loot(\d+)Name(\d+)$")


TIERS = ("common", "mi", "epic", "legendary")  # "common" = everything else, incl. non-gear
_ZERO = (0.0, 0.0, 0.0, 0.0)


def _item_tier(rec):
    if not GEAR_CLASS.match(rec.get("Class") or ""):
        return 0
    return {"Rare": 1, "Epic": 2, "Legendary": 3}.get(rec.get("itemClassification"), 0)


def _tier_targets(p, s):
    """Target tier shares for a weighted choice whose vanilla tier shares are p.

    Epic + Legendary are filled first, MI next, and common items take whatever is left, so a
    higher MI multiplier can never eat into the Epic/Legendary boost. When a choice has too
    little common share to give away, the lower-priority tier is the one that gets capped.
    """
    t = [0.0, 0.0, 0.0, 0.0]
    left = 1.0
    for tiers, mults in (((2, 3), (s.epic_multiplier, s.legendary_multiplier)),
                         ((1,), (s.mi_multiplier,))):
        want = [p[i] * m for i, m in zip(tiers, mults)]
        total = sum(want)
        scale = min(1.0, left / total) if total > 0 else 0.0
        for i, w in zip(tiers, want):
            t[i] = w * scale
        left -= total * scale
    if p[0] > 0:
        t[0] = left
    elif left > 1e-12:
        # Nothing common to absorb the rest (only possible with multipliers below 1):
        # fall back to plain relative scaling of what is there.
        tot = sum(t)
        t = [v / tot for v in t] if tot else list(p)
    return t


def _weight_groups(rec):
    """Return {group: [(nameKey, weightKey), ...]} for every weighted choice in a record.

    Entries inside one group compete with each other (relative weights)."""
    cls = rec.get("Class")
    groups = {}
    if cls in WEIGHTED_TABLE_CLASSES:
        for key in rec.fields:
            m = _TABLE_ENTRY.match(key)
            if m:
                groups.setdefault("", []).append((key, "lootWeight" + m.group(1)))
    elif cls in DROPPER_CLASSES:
        for key in rec.fields:
            m = _MONSTER_LOOT.match(key)
            if m:
                groups.setdefault(m.group(1), []).append(
                    (key, f"chanceToEquip{m.group(1)}Item{m.group(2)}"))
    elif rec.get("templateName", "").lower() == CHEST_TEMPLATE:
        for key in rec.fields:
            m = _CHEST_LOOT.match(key)
            if m:
                groups.setdefault(m.group(1), []).append((key, f"loot{m.group(1)}Weight{m.group(2)}"))
    return groups


def _scaled_values(f, factor):
    if f.type == T_INT:
        return [max(1, int(round(v * factor))) if v > 0 else v for v in f.values]
    return [v * factor if v > 0 else v for v in f.values]


def rarity_plan(db, s):
    """Plan the rarity reweighting.

    Returns (node, group_factors):
      node(name) -> (vanilla tier shares, tier shares after the mod) of a loot record, or None
                    for a missing record (left out of every average).
      group_factors(rec, entries) -> {weightKey: factor} for one weighted choice.

    Each weighted choice is planned on its own: its vanilla tier shares P give target shares
    (see _tier_targets), and every entry's weight is multiplied by the target/achieved ratio
    of the tiers it leads to. Shared sub-tables are planned once and already deliver their own
    targets, so a parent only corrects what they could not do themselves (e.g. a pure-MI table).
    """
    memo, plans = {}, {}

    def node(name, depth=0):
        if name in memo:
            return memo[name]
        rec = db.get(name)
        if rec is None or depth > 25:
            return None
        memo[name] = None  # cycle guard
        cls = rec.get("Class")
        if cls == "LevelTable":
            # Picks one sub-table by level: levels[i] .. levels[i+1]-1 use records[i].
            # Weight each sub-table by the number of character levels (1..MAX_LEVEL) it covers.
            lv = rec.getall("levels")
            tot, p, q = 0.0, [0.0] * 4, [0.0] * 4
            for i, sub in enumerate(norm(r) for r in rec.getall("records")):
                lo = lv[i] if i < len(lv) else 1
                hi = lv[i + 1] if i + 1 < len(lv) else MAX_LEVEL + 1
                span = max(0, min(hi, MAX_LEVEL + 1) - max(lo, 1))
                r = node(sub, depth + 1) if (span and sub) else None
                if r is not None:
                    tot += span
                    for t in range(4):
                        p[t] += span * r[0][t]
                        q[t] += span * r[1][t]
            res = (tuple(v / tot for v in p), tuple(v / tot for v in q)) if tot else None
        elif cls in WEIGHTED_TABLE_CLASSES:
            plan = _plan(rec, _weight_groups(rec).get("", []), depth)
            plans[name] = plan[2] if plan else {}
            res = plan[:2] if plan else None
        else:
            one = [0.0] * 4
            one[_item_tier(rec)] = 1.0
            res = (tuple(one), tuple(one))
        memo[name] = res
        return res

    def _plan(rec, entries, depth):
        rows = []
        for nk, wk in entries:
            w = rec.get(wk, 0) or 0
            target = norm(rec.get(nk))
            r = node(target, depth + 1) if (w > 0 and target) else None
            if r is not None:
                rows.append((wk, w, r[0], r[1]))
        tot = sum(w for _, w, _, _ in rows)
        if not tot:
            return None
        P = [sum(w * p[t] for _, w, p, _ in rows) / tot for t in range(4)]
        Q = [sum(w * q[t] for _, w, _, q in rows) / tot for t in range(4)]
        target = _tier_targets(P, s)
        f = [target[t] / Q[t] if Q[t] > 1e-12 else 0.0 for t in range(4)]
        factors = {wk: sum(q[t] * f[t] for t in range(4)) for wk, _, _, q in rows}
        new_tot = sum(w * factors[wk] for wk, w, _, _ in rows)
        if new_tot <= 0:
            return tuple(P), tuple(Q), {}
        q_new = tuple(sum(w * factors[wk] * q[t] for wk, w, _, q in rows) / new_tot
                      for t in range(4))
        return tuple(P), q_new, factors

    def group_factors(name, rec, group, entries):
        if group == "" and rec.get("Class") in WEIGHTED_TABLE_CLASSES:
            node(name)
            return plans.get(name, {})
        plan = _plan(rec, entries, 0)
        return plan[2] if plan else {}

    return node, group_factors


def apply_item_rarity(db, s, out, stats):
    """Multiply the chance of each Epic/Legendary/MI item by its multiplier.

    The number of drops stays the same; common items give up the share (see _tier_targets).
    """
    if all(abs(m - 1) < 1e-9 for m in (s.mi_multiplier, s.epic_multiplier, s.legendary_multiplier)):
        return
    _, group_factors = rarity_plan(db, s)

    counts = {"tables": 0, "droppers": 0, "chests": 0}
    for name, rec in db.items():
        groups = _weight_groups(rec)
        if not groups:
            continue
        edits = {}
        for group, entries in groups.items():
            factors = {wk: g for wk, g in group_factors(name, rec, group, entries).items()
                       if rec.fields.get(wk) is not None}
            pos = [g for g in factors.values() if g > 0]
            # Weights are relative: scaling a whole group by one factor changes nothing.
            if not pos or max(pos) - min(pos) < 1e-6 * max(pos):
                continue
            # Rescale so no weight shrinks (keeps precision for small integer weights).
            k = 1.0 / min(pos)
            peak = max(max(rec.fields[wk].values) * g * k for wk, g in factors.items())
            if peak > 1e9:
                k *= 1e9 / peak
            for wk, g in factors.items():
                f = rec.fields[wk]
                new = _scaled_values(f, g * k)
                if new != f.values:
                    edits[wk] = new
        if edits:
            c = _changed(out, db, name)
            for k_, v in edits.items():
                c.fields[k_].values = v
            cls = rec.get("Class")
            counts["tables" if cls in WEIGHTED_TABLE_CLASSES else
                   "droppers" if cls in DROPPER_CLASSES else "chests"] += 1
    stats["loot tables reweighted"] = counts["tables"]
    stats["monsters with reweighted loot slots"] = counts["droppers"]
    stats["chest loot tables reweighted"] = counts["chests"]


AFFIX_WEIGHTS_RARE = ("rarePrefixOnly", "rareSuffixOnly", "normalPrefixRareSuffix",
                      "rarePrefixNormalSuffix", "rareBothPrefixSuffix")
AFFIX_WEIGHTS_DOUBLE = ("bothPrefixSuffix", "normalPrefixRareSuffix", "rarePrefixNormalSuffix",
                        "rareBothPrefixSuffix")
AFFIX_WEIGHTS_PLAIN = ("brokenOnly", "noPrefixNoSuffix")


def apply_affix_quality(db, s, out, stats):
    if abs(s.rare_affix_multiplier - 1) < 1e-9 and abs(s.double_affix_multiplier - 1) < 1e-9 \
            and abs(s.plain_item_multiplier - 1) < 1e-9:
        return
    n = 0
    for name, rec in db.items():
        if rec.get("Class") != "LootItemTable_DynWeight":
            continue
        factors = {}
        for k in AFFIX_WEIGHTS_RARE:
            factors[k] = factors.get(k, 1.0) * s.rare_affix_multiplier
        for k in AFFIX_WEIGHTS_DOUBLE:
            factors[k] = factors.get(k, 1.0) * s.double_affix_multiplier
        for k in AFFIX_WEIGHTS_PLAIN:
            factors[k] = s.plain_item_multiplier
        # Only weights that are non-zero matter; a table whose only outcome is a single
        # category is unaffected by scaling, so skip it to keep the mod small.
        active = [k for k in factors if (rec.get(k) or 0) > 0]
        if len(active) < 2:
            continue
        new = {}
        for k in active:
            f = rec.fields[k]
            v = f.values[0] * factors[k]
            if f.type == T_INT:
                v = int(round(v))
                if v == 0 and factors[k] > 0:
                    v = 1
            new[k] = v
        # Never let every outcome reach zero (e.g. plain multiplier 0 on a plain-only table).
        if all(v <= 0 for v in new.values()):
            continue
        c = _changed(out, db, name)
        for k, v in new.items():
            c.fields[k].values = [v]
        n += 1
    stats["affix weight tables"] = n


# -------------------------------------------------------------- Affix stat rolls
# Fields on an affix that are NOT stat magnitudes (or that we deliberately keep fixed).
_ROLL_SKIP = re.compile(
    r"(Chance$|Duration|^levelRequirement$|^lootRandomizer|^marketAdjustmentPercent$"
    r"|^conversionPercentage|^augment|^itemSkill|^racialBonusRace)")


def roll_transform(jitter_pct, min_pos, max_pos):
    """Return (scale, new_jitter_pct) for an affix.

    Vanilla roll range is value * (1 +- J). We want the new range to start at min_pos and end
    at max_pos (fractions 0..1) of that vanilla range. A centre scale `s` and a new jitter J'
    satisfy: value*s*(1-J') = new_min and value*s*(1+J') = new_max.
    """
    j = jitter_pct / 100.0
    a, b = min_pos / 100.0, max_pos / 100.0
    scale = 1.0 + j * (a + b - 1.0)
    new_j = j * (b - a) / scale if scale else 0.0
    return scale, new_j * 100.0


def apply_rolls(db, s, out, stats):
    if s.roll_min_percent <= 0 and s.roll_max_percent >= 100:
        return
    n = 0
    for name, rec in db.items():
        if rec.get("Class") != "LootRandomizer":
            continue
        jit = rec.get("lootRandomizerJitter") or 0
        if jit <= 0:
            continue
        scale, new_j = roll_transform(jit, s.roll_min_percent, s.roll_max_percent)
        c = _changed(out, db, name)
        for key, f in c.fields.items():
            if f.type == T_FLOAT and not _ROLL_SKIP.search(key) and any(f.values):
                f.values = [v * scale for v in f.values]
        c.set("lootRandomizerJitter", new_j, T_FLOAT)
        n += 1
    stats["affixes re-ranged"] = n


# -------------------------------------------------------------------- Drop amount
_CHEST_COUNT_EQS = ("numSpawnMinEquation", "numSpawnMaxEquation")


def _droppable_fn(db):
    """droppable(name) -> True if the record can produce an item a player can pick up
    (False for cosmetic monster gear such as zombie rags, which has cannotPickUp=1)."""
    memo = {}

    def droppable(name, depth=0):
        if name in memo:
            return memo[name]
        rec = db.get(name)
        if rec is None or depth > 25:
            return False
        memo[name] = False  # cycle guard
        cls = rec.get("Class")
        if cls == "LevelTable":
            res = any(droppable(norm(r), depth + 1) for r in rec.getall("records") if r)
        elif cls in WEIGHTED_TABLE_CLASSES:
            res = any(droppable(norm(rec.get(nk)), depth + 1)
                      for nk, wk in _weight_groups(rec).get("", []) if (rec.get(wk) or 0) > 0)
        else:
            res = not rec.get("cannotPickUp")
        memo[name] = res
        return res

    return droppable


def apply_loot_amount(db, s, out, stats):
    """More drops, like GDModdingTool's AdjustLootAmount: monster slot chances x (capped at 100%)
    and chest/breakable item counts x. What a slot drops is unchanged, so every item - Epics and
    Legendaries included - drops proportionally more often. Guaranteed (100%) slots can't rise."""
    m = s.drop_amount_multiplier
    if abs(m - 1) < 1e-9:
        return
    droppable = _droppable_fn(db)
    slots = capped = monsters = chests = 0
    for name, rec in db.items():
        cls = rec.get("Class")
        if cls in DROPPER_CLASSES:
            if not rec.get("dropItems", 1):
                continue
            edits = {}
            for group, entries in _weight_groups(rec).items():
                key = f"chanceToEquip{group}"
                f = rec.fields.get(key)
                if f is None or not f.values or f.values[0] <= 0:
                    continue
                if not any(droppable(norm(rec.get(nk))) for nk, wk in entries
                           if (rec.get(wk) or 0) > 0):
                    continue  # cosmetic-only slot: more of it just changes how monsters look
                if m > 1 and f.values[0] >= 100:
                    capped += 1
                    continue
                edits[key] = [min(100.0, v * m) for v in f.values]
            if edits:
                c = _changed(out, db, name)
                for k, v in edits.items():
                    c.fields[k].values = v
                slots += len(edits)
                monsters += 1
        elif (rec.get("templateName") or "").lower() == CHEST_TEMPLATE:
            eqs = {k: rec.get(k) for k in _CHEST_COUNT_EQS if rec.get(k)}
            if eqs:
                c = _changed(out, db, name)
                for k, eq in eqs.items():
                    c.set(k, f"({eq})*{m:g}")
                chests += 1
    stats["monsters with more drops"] = monsters
    stats["monster loot slots raised"] = slots
    stats["guaranteed slots left at 100%"] = capped
    stats["chests/breakables with more items"] = chests


GOLD_EQS = ("goldAmountEquation", "goldAmountEquation2")
GOLD_LIMITS = ("goldValueMin", "goldValueMax")


def apply_gold(db, s, out, stats):
    """Gold per drop x: all gold comes from a handful of shared gold-generator records."""
    m = s.gold_multiplier
    if abs(m - 1) < 1e-9:
        return
    n = 0
    for name, rec in db.items():
        if not any(rec.get(k) for k in GOLD_EQS):
            continue
        c = _changed(out, db, name)
        for k in GOLD_EQS:
            eq = rec.get(k)
            if eq:
                c.set(k, f"({eq})*{m:g}")
        # Scale the clamps too, or big piles would silently stop growing.
        for k in GOLD_LIMITS:
            f = c.fields.get(k)
            if f is not None and f.values:
                f.values = [int(round(v * m)) if f.type == T_INT else v * m for v in f.values]
        n += 1
    stats["gold generators"] = n


# ------------------------------------------------------------------- Inventory bags
BAG_ITEM = "records/items/misc/inventorybag.dbr"
BAG_VENDOR_TIER = "records/creatures/npcs/merchants/factiontables/devilscrossing_friendly_01.dbr"


def apply_bags(db, s, out, stats):
    """Sell the (otherwise quest-only) inventory bag for 0 gold at the Devil's Crossing faction
    vendor. Faction tiers have a fixed always-in-stock list (marketStaticItems); regular
    merchants only roll random stock, so they can't guarantee the bag."""
    if not s.free_inventory_bags or BAG_ITEM not in db or BAG_VENDOR_TIER not in db:
        return
    bag = _changed(out, db, BAG_ITEM)
    f = bag.fields.get("itemCost")
    bag.set("itemCost", 0 if f is None or f.type == T_INT else 0.0, T_INT if f is None else f.type)
    tier = _changed(out, db, BAG_VENDOR_TIER)
    items = [v for v in tier.getall("marketStaticItems") if v]
    if BAG_ITEM not in (norm(v) for v in items):
        tier.set("marketStaticItems", items + [BAG_ITEM], T_STRING)
    stats["inventory bag at Devil's Crossing vendor"] = 1


def build_records(db, s, log=print):
    out, stats = {}, {}
    for step, fn in (("XP", apply_xp), ("factions", apply_factions),
                     ("item rarity", apply_item_rarity), ("affix quality", apply_affix_quality),
                     ("stat rolls", apply_rolls), ("drop amount", apply_loot_amount),
                     ("gold", apply_gold), ("inventory bags", apply_bags)):
        log(f"  applying {step} ...")
        fn(db, s, out, stats)
    return out, stats
