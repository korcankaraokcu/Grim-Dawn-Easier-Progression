"""Locate a Grim Dawn install and load its (merged) database."""
import os
import re

from .arz import read_arz

GAME_EXE = "Grim Dawn.exe"
DATABASES = [
    os.path.join("database", "database.arz"),
    os.path.join("gdx1", "database", "GDX1.arz"),   # Ashes of Malmouth
    os.path.join("gdx2", "database", "GDX2.arz"),   # Forgotten Gods
    os.path.join("gdx3", "database", "GDX3.arz"),   # Fangs of Asterkarn
]

# Only these parts of the database are ever touched/read by the builder.
RELEVANT_PREFIXES = (
    "records/game/",
    "records/items/",
    "records/creatures/",
    "records/controllers/factions/",
    "records/endlessdungeon/",
)


def is_game_dir(path):
    return bool(path) and os.path.isfile(os.path.join(path, GAME_EXE)) and \
        os.path.isfile(os.path.join(path, DATABASES[0]))


def find_game_dir():
    """Best-effort Steam/GOG lookup."""
    candidates = []
    steam_roots = [r"C:\Program Files (x86)\Steam", r"C:\Program Files\Steam"]
    try:
        import winreg
        for hive, key in ((winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam"),
                          (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam")):
            try:
                with winreg.OpenKey(hive, key) as k:
                    for name in ("SteamPath", "InstallPath"):
                        try:
                            steam_roots.append(winreg.QueryValueEx(k, name)[0])
                        except OSError:
                            pass
            except OSError:
                pass
    except ImportError:
        pass
    libs = []
    for root in steam_roots:
        vdf = os.path.join(root, "steamapps", "libraryfolders.vdf")
        libs.append(root)
        if os.path.isfile(vdf):
            with open(vdf, encoding="utf-8", errors="ignore") as fh:
                libs += [p.replace("\\\\", "\\") for p in re.findall(r'"path"\s+"([^"]+)"', fh.read())]
    for lib in libs:
        candidates.append(os.path.join(lib, "steamapps", "common", "Grim Dawn"))
    candidates += [r"C:\GOG Games\Grim Dawn", r"C:\Program Files (x86)\GOG Galaxy\Games\Grim Dawn"]
    for c in candidates:
        if is_game_dir(c):
            return os.path.normpath(c)
    return ""


def load_database(game_dir, log=print):
    """Load base game + expansions; later archives override earlier ones."""
    db = {}
    keep = lambda name: name.startswith(RELEVANT_PREFIXES)
    for rel in DATABASES:
        path = os.path.join(game_dir, rel)
        if not os.path.isfile(path):
            log(f"  (skipping missing {rel})")
            continue
        log(f"  reading {rel} ...")
        db.update(read_arz(path, keep))
    log(f"  {len(db):,} relevant records loaded")
    return db
