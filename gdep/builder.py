"""Build / remove the mod in <Grim Dawn>/mods/<ModName>/."""
import json
import os
import shutil
import time

from . import gamedata, transforms
from .arz import write_arz

_DB_CACHE = {}


def mod_dir(settings):
    return os.path.join(settings.game_dir, "mods", settings.mod_name)


def load_db_cached(game_dir, log=print):
    stamp = tuple(os.path.getmtime(os.path.join(game_dir, p))
                  for p in gamedata.DATABASES if os.path.isfile(os.path.join(game_dir, p)))
    key = (os.path.normcase(game_dir), stamp)
    if key not in _DB_CACHE:
        _DB_CACHE.clear()
        _DB_CACHE[key] = gamedata.load_database(game_dir, log)
    else:
        log("  using already-loaded game database")
    return _DB_CACHE[key]


def build(settings, log=print):
    errs = settings.validate()
    if not gamedata.is_game_dir(settings.game_dir):
        errs.append(f"Not a Grim Dawn folder: {settings.game_dir!r}")
    if errs:
        raise ValueError("\n".join(errs))

    t0 = time.time()
    log("Loading game database ...")
    db = load_db_cached(settings.game_dir, log)
    log("Building modified records ...")
    records, stats = transforms.build_records(db, settings, log)
    for k, v in stats.items():
        log(f"    {k}: {v:,}")
    if not records:
        log("All settings are vanilla - nothing to write.")

    out_dir = os.path.join(mod_dir(settings), "database")
    os.makedirs(out_dir, exist_ok=True)
    arz_path = os.path.join(out_dir, settings.mod_name + ".arz")
    tmp = arz_path + ".tmp"
    write_arz(tmp, sorted(records.values(), key=lambda r: r.name.lower()))
    os.replace(tmp, arz_path)
    with open(os.path.join(mod_dir(settings), "settings_used.json"), "w", encoding="utf-8") as fh:
        json.dump({"built": time.strftime("%Y-%m-%d %H:%M:%S"), "stats": stats,
                   "settings": settings.to_dict()}, fh, indent=2)
    log(f"Wrote {len(records):,} records to {arz_path} ({time.time() - t0:.1f}s)")
    return arz_path, stats


def remove(settings, log=print):
    d = mod_dir(settings)
    if not os.path.isdir(d):
        log("Mod folder does not exist - nothing to remove.")
        return False
    # Safety: only delete folders this tool created.
    if not os.path.isfile(os.path.join(d, "settings_used.json")):
        raise RuntimeError(f"{d} was not created by this tool; refusing to delete it.")
    shutil.rmtree(d)
    log(f"Removed {d}")
    return True
