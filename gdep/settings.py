"""Mod settings: defaults, presets, load/save."""
import json
from dataclasses import asdict, dataclass, fields

MOD_NAME = "EasierProgression"


@dataclass
class Settings:
    # --- Experience -------------------------------------------------------
    xp_multiplier: float = 2.0            # monster-kill XP x

    # --- Faction reputation (per kill) -------------------------------------
    faction_gain_multiplier: float = 3.0   # rep gained with friendly factions per kill x
    faction_hostile_multiplier: float = 1.0  # rep lost with hostile factions per kill x (nemesis!)
    faction_no_diminishing: bool = False   # disable the 0.5x / 0.33x reductions at higher standing
    free_inventory_bags: bool = True       # Devil's Crossing faction vendor sells bags for 0 gold

    # --- Drop quality: which item gets picked when something drops ----------
    mi_multiplier: float = 1.5             # Monster Infrequents (green named base items) x
    epic_multiplier: float = 2.0           # Epic (blue) x
    legendary_multiplier: float = 2.0      # Legendary + Mythical (purple) x

    # --- Drop amount -----------------------------------------------------------
    drop_amount_multiplier: float = 2.0    # whole number: monster slot chances (cap 100%) and chest item counts x
    gold_multiplier: float = 2.0           # gold per pile x

    # --- Drop quality: affixes on random-affix items ------------------------
    rare_affix_multiplier: float = 2.0     # weight of rolls containing a rare (green) affix x
    double_affix_multiplier: float = 1.5   # extra x for prefix+suffix rolls
    plain_item_multiplier: float = 0.5     # broken / no-affix rolls x

    # --- Affix stat rolls ----------------------------------------------------
    # Positions inside the vanilla roll range: 0 = vanilla minimum, 100 = vanilla maximum.
    roll_min_percent: float = 50.0
    roll_max_percent: float = 100.0

    # --- UI state: the preset the values were last loaded from ("" = none) ----
    preset: str = ""

    # --- Output ----------------------------------------------------------------
    game_dir: str = ""
    mod_name: str = MOD_NAME

    @classmethod
    def from_dict(cls, d):
        known = {f.name for f in fields(cls)}
        return cls(**{k: v for k, v in d.items() if k in known})

    def to_dict(self):
        return asdict(self)

    def validate(self):
        errs = []
        numeric = [f.name for f in fields(self) if f.type in (float, "float")]
        bad = [n for n in numeric if isinstance(getattr(self, n), bool)
               or not isinstance(getattr(self, n), (int, float))]
        if bad:
            return [f"{n} must be a number" for n in bad]
        for name in ("xp_multiplier", "faction_gain_multiplier", "faction_hostile_multiplier",
                     "mi_multiplier", "epic_multiplier", "legendary_multiplier",
                     "rare_affix_multiplier", "double_affix_multiplier",
                     "drop_amount_multiplier", "gold_multiplier"):
            if getattr(self, name) <= 0:
                errs.append(f"{name} must be > 0")
        if self.drop_amount_multiplier not in (1, 2, 3, 4, 5):
            errs.append("drop_amount_multiplier must be a whole number from 1 to 5")
        if self.plain_item_multiplier < 0:
            errs.append("plain_item_multiplier must be >= 0")
        if not (0 <= self.roll_min_percent <= 100 and 0 <= self.roll_max_percent <= 100):
            errs.append("roll percentages must be within 0..100")
        if self.roll_min_percent > self.roll_max_percent:
            errs.append("roll_min_percent must be <= roll_max_percent")
        if not self.mod_name.isidentifier():
            errs.append("mod_name must be letters/digits/underscore only")
        return errs


PRESETS = {
    "Vanilla (no changes)": dict(
        xp_multiplier=1.0, faction_gain_multiplier=1.0, faction_hostile_multiplier=1.0,
        faction_no_diminishing=False, free_inventory_bags=False, mi_multiplier=1.0, epic_multiplier=1.0, legendary_multiplier=1.0,
        drop_amount_multiplier=1.0, gold_multiplier=1.0,
        rare_affix_multiplier=1.0, double_affix_multiplier=1.0, plain_item_multiplier=1.0,
        roll_min_percent=0.0, roll_max_percent=100.0),
    "Light": dict(
        xp_multiplier=1.5, faction_gain_multiplier=2.0, faction_hostile_multiplier=1.0,
        faction_no_diminishing=False, free_inventory_bags=True, mi_multiplier=1.25, epic_multiplier=1.5, legendary_multiplier=1.5,
        drop_amount_multiplier=2.0, gold_multiplier=1.5,
        rare_affix_multiplier=1.5, double_affix_multiplier=1.25, plain_item_multiplier=0.75,
        roll_min_percent=25.0, roll_max_percent=100.0),
    "Medium": dict(
        xp_multiplier=2.0, faction_gain_multiplier=3.0, faction_hostile_multiplier=1.0,
        faction_no_diminishing=False, free_inventory_bags=True, mi_multiplier=1.5, epic_multiplier=2.0, legendary_multiplier=2.0,
        drop_amount_multiplier=2.0, gold_multiplier=2.0,
        rare_affix_multiplier=2.0, double_affix_multiplier=1.5, plain_item_multiplier=0.5,
        roll_min_percent=50.0, roll_max_percent=100.0),
    "Generous": dict(
        xp_multiplier=3.0, faction_gain_multiplier=5.0, faction_hostile_multiplier=1.0,
        faction_no_diminishing=True, free_inventory_bags=True, mi_multiplier=2.0, epic_multiplier=3.0, legendary_multiplier=4.0,
        drop_amount_multiplier=3.0, gold_multiplier=3.0,
        rare_affix_multiplier=3.0, double_affix_multiplier=2.0, plain_item_multiplier=0.25,
        roll_min_percent=75.0, roll_max_percent=100.0),
}


def matches_preset(values, name):
    """True if every value the preset defines equals the given values (floats within 1e-6)."""
    for k, v in PRESETS[name].items():
        cur = values.get(k)
        if isinstance(v, bool) or not isinstance(v, (int, float)):
            if cur != v:
                return False
        elif cur is None or abs(float(cur) - v) > 1e-6:
            return False
    return True


def exact_preset(values):
    """Name of the first preset that equals the values exactly, or ''."""
    return next((n for n in PRESETS if matches_preset(values, n)), "")


def preset_status(values, base):
    """Return (preset shown in the selector, indicator text next to it).

    base = preset the values were last loaded from. An exact match with any preset wins, so
    tweaking values back to a preset shows it without an indicator."""
    if base in PRESETS and matches_preset(values, base):
        return base, ""
    exact = exact_preset(values)
    if exact:
        return exact, ""
    if base in PRESETS:
        return base, "(modified)"
    return "", "Custom values"


def load(path):
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return Settings.from_dict(json.load(fh))
    except FileNotFoundError:
        return Settings()
    except (ValueError, TypeError):  # corrupt or hand-edited file: fall back to defaults
        return Settings()


def save(settings, path):
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(settings.to_dict(), fh, indent=2)
