"""Grim Dawn - Easier Progression: settings UI + mod builder.

Run:  python gd_easier_progression.py           (GUI)
      python gd_easier_progression.py --build   (build with saved settings, no GUI)
"""
import os
import queue
import sys
import threading
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from gdep import builder, gamedata  # noqa: E402
from gdep import settings as cfg  # noqa: E402
from gdep.transforms import roll_transform  # noqa: E402

SETTINGS_FILE = os.path.join(HERE, "settings.json")


def load_settings():
    s = cfg.load(SETTINGS_FILE)
    if not gamedata.is_game_dir(s.game_dir):
        s.game_dir = gamedata.find_game_dir()
    return s


def cli_build():
    s = load_settings()
    builder.build(s)
    cfg.save(s, SETTINGS_FILE)


# --------------------------------------------------------------------------- GUI
def run_gui():
    import tkinter as tk
    from tkinter import filedialog, messagebox, ttk

    s = load_settings()
    root = tk.Tk()
    root.title("Grim Dawn - Easier Progression")
    root.minsize(760, 640)
    style = ttk.Style(root)
    if "vista" in style.theme_names():
        style.theme_use("vista")
    style.configure("Hint.TLabel", foreground="#555")
    style.configure("Head.TLabelframe.Label", font=("Segoe UI", 10, "bold"))

    vars_ = {}
    outer = ttk.Frame(root, padding=10)
    outer.pack(fill="both", expand=True)

    # --- game folder + presets
    top = ttk.Frame(outer)
    top.pack(fill="x")
    ttk.Label(top, text="Grim Dawn folder:").grid(row=0, column=0, sticky="w")
    game_var = tk.StringVar(value=s.game_dir)
    ttk.Entry(top, textvariable=game_var, width=70).grid(row=0, column=1, sticky="we", padx=4)

    def browse():
        d = filedialog.askdirectory(initialdir=game_var.get() or "C:\\")
        if d:
            game_var.set(os.path.normpath(d))

    ttk.Button(top, text="Browse...", command=browse).grid(row=0, column=2)
    ttk.Label(top, text="Preset:").grid(row=1, column=0, sticky="w", pady=(6, 0))
    preset_var = tk.StringVar(value="")
    base_preset = {"name": s.preset}  # preset the current values started from
    preset_row = ttk.Frame(top)
    preset_row.grid(row=1, column=1, sticky="w", padx=4, pady=(6, 0))
    preset_box = ttk.Combobox(preset_row, textvariable=preset_var, values=list(cfg.PRESETS),
                              state="readonly", width=25)
    preset_box.pack(side="left")
    preset_note = ttk.Label(preset_row, text="", style="Hint.TLabel")
    preset_note.pack(side="left", padx=(8, 0))
    top.columnconfigure(1, weight=1)

    nb = ttk.Notebook(outer)
    nb.pack(fill="both", expand=True, pady=8)

    def page(title):
        f = ttk.Frame(nb, padding=10)
        nb.add(f, text=title)
        f.columnconfigure(1, weight=1)
        return f

    def slider(parent, row, key, label, lo, hi, res, hint, fmt="{:.2f}x"):
        var = tk.DoubleVar(value=getattr(s, key))
        vars_[key] = var
        ttk.Label(parent, text=label).grid(row=row, column=0, sticky="w", pady=(8, 0))
        shown = ttk.Label(parent, width=9, anchor="e")
        shown.grid(row=row, column=2, sticky="e", pady=(8, 0))

        def on_move(_=None):
            v = round(round(var.get() / res) * res, 4)
            var.set(v)
            shown.config(text=fmt.format(v))
            refresh_example()

        sc = ttk.Scale(parent, from_=lo, to=hi, variable=var, command=on_move)
        sc.grid(row=row, column=1, sticky="we", padx=8, pady=(8, 0))
        ent = ttk.Spinbox(parent, from_=lo, to=hi, increment=res, textvariable=var, width=7, command=on_move)
        ent.grid(row=row, column=3, pady=(8, 0))
        ent.bind("<FocusOut>", on_move)
        ent.bind("<Return>", on_move)
        ttk.Label(parent, text=hint, style="Hint.TLabel", wraplength=640, justify="left").grid(
            row=row + 1, column=0, columnspan=4, sticky="w")
        var.trace_add("write", lambda *_: shown.config(text=_safe_fmt(fmt, var)))
        shown.config(text=fmt.format(var.get()))

    def check(parent, row, key, label, hint):
        var = tk.BooleanVar(value=getattr(s, key))
        vars_[key] = var
        ttk.Checkbutton(parent, text=label, variable=var).grid(row=row, column=0, columnspan=4, sticky="w", pady=(10, 0))
        ttk.Label(parent, text=hint, style="Hint.TLabel", wraplength=640, justify="left").grid(
            row=row + 1, column=0, columnspan=4, sticky="w")

    # --- XP & factions
    p = page("XP & Factions")
    slider(p, 0, "xp_multiplier", "Monster XP", 1, 10, 0.25,
           "Multiplies experience from every monster kill (all difficulties). Quest XP and level "
           "requirements are unchanged, so skill/attribute/devotion points stay vanilla.")
    slider(p, 2, "faction_gain_multiplier", "Friendly rep per kill", 1, 20, 0.25,
           "Reputation gained with your factions when you kill their enemies "
           "(e.g. beasts -> Devil's Crossing & Coven of Ugdenbog). Bounty/quest rewards are not affected.")
    slider(p, 4, "faction_hostile_multiplier", "Hostile rep per kill", 0.25, 5, 0.25,
           "How fast hostile factions start to hate you. Higher = Nemesis bosses appear sooner. "
           "Keep at 1.00x on Hardcore unless you want that.")
    check(p, 6, "faction_no_diminishing", "Remove diminishing reputation from kills",
          "Vanilla halves (common) / thirds (champion) kill reputation once you reach Respected/Honored.")
    check(p, 8, "free_inventory_bags", "Sell free inventory bags at Devil's Crossing",
          "The Devil's Crossing faction vendor always stocks Inventory Bags for 0 gold once you are "
          "Friendly with them.")

    # --- loot (quality, amount, gold)
    p = page("Loot")
    slider(p, 0, "legendary_multiplier", "Legendary / Mythical", 0.5, 10, 0.25,
           "Relative chance that a drop is a Legendary (incl. Mythical) item. Number of drops is unchanged; "
           "the extra Legendaries replace lower items. Approximate: loot tables switch by monster level, "
           "so the real gain is usually ~90-100% of the value shown.")
    slider(p, 2, "epic_multiplier", "Epic", 0.5, 10, 0.25, "Relative chance that a drop is an Epic item.")
    slider(p, 4, "mi_multiplier", "Monster Infrequents", 0.5, 10, 0.25,
           "Relative chance of green named 'monster infrequent' base items.")
    slider(p, 6, "drop_amount_multiplier", "Drop amount", 1, 5, 1,
           "How often monster loot slots drop something (capped at 100%, so guaranteed boss/hero drops "
           "stay the same) and how many items chests and breakables roll. "
           "Whole numbers only: chests roll whole items.", fmt="{:.0f}x")
    slider(p, 8, "gold_multiplier", "Gold amount", 0.5, 10, 0.25,
           "Gold per pile from monsters, chests and breakables.")
    slider(p, 10, "rare_affix_multiplier", "Rare (green) affixes", 0.5, 10, 0.25,
           "Random-affix items: weight of rolls that include a rare affix.")
    slider(p, 12, "double_affix_multiplier", "Prefix + Suffix", 0.5, 5, 0.25,
           "Random-affix items: extra weight for rolls that get both a prefix and a suffix.")
    slider(p, 14, "plain_item_multiplier", "Plain / broken items", 0, 1, 0.05,
           "Weight of white/broken rolls without affixes. 0 removes them wherever the table has other options.")

    # --- stat rolls
    p = page("Stat Rolls")
    ttk.Label(p, wraplength=660, justify="left", text=(
        "Every random affix (magic & rare prefixes/suffixes, monster-infrequent affixes, crafting "
        "bonuses, Ascended affixes) rolls each stat inside a range around its base value. Choose where "
        "the new range starts and ends, as a percentage of the vanilla range:\n"
        "   0% = vanilla minimum, 100% = vanilla maximum.\n"
        "Min 50% / Max 100% means every roll lands in the upper half of the vanilla range. "
        "Min = Max gives fixed values. Epic/Legendary base stats have no random roll in the game "
        "data, so they are unchanged. Existing items in this mod's saves are re-evaluated too.")
              ).grid(row=0, column=0, columnspan=4, sticky="w")
    slider(p, 1, "roll_min_percent", "New minimum", 0, 100, 1, "", fmt="{:.0f}%")
    slider(p, 3, "roll_max_percent", "New maximum", 0, 100, 1, "", fmt="{:.0f}%")
    example = ttk.Label(p, justify="left", font=("Consolas", 10))
    example.grid(row=5, column=0, columnspan=4, sticky="w", pady=(14, 0))

    def refresh_example():
        try:
            a, b = vars_["roll_min_percent"].get(), vars_["roll_max_percent"].get()
        except (KeyError, tk.TclError):
            return
        lines = ["Examples (stat base value, jitter):"]
        for base, jit, unit in ((20, 20, "% Physical Damage"), (5, 20, " to All Attributes"),
                                (100, 50, " Offensive Ability (crafting bonus)")):
            lo, hi = base * (1 - jit / 100), base * (1 + jit / 100)
            if a > b:
                lines.append("  minimum is above maximum - fix the sliders")
                break
            sc, nj = roll_transform(jit, a, b)
            nlo, nhi = base * sc * (1 - nj / 100), base * sc * (1 + nj / 100)
            lines.append(f"  +{lo:g}-{hi:g}{unit}  ->  +{nlo:.4g}-{nhi:.4g}{unit}")
        example.config(text="\n".join(lines))

    refresh_example()

    # --- buttons + log
    btns = ttk.Frame(outer)
    btns.pack(fill="x")
    log_box = tk.Text(outer, height=11, wrap="word", state="disabled", font=("Consolas", 9))
    log_box.pack(fill="both", expand=False, pady=(8, 0))
    msgs = queue.Queue()

    def log(msg):
        msgs.put(msg)

    def pump():
        while not msgs.empty():
            m = msgs.get()
            if callable(m):  # UI action queued by a worker thread
                m()
                continue
            log_box.config(state="normal")
            log_box.insert("end", m + "\n")
            log_box.see("end")
            log_box.config(state="disabled")
        root.after(100, pump)

    pump()

    def current_values():
        d = {}
        for k, v in vars_.items():
            try:
                d[k] = v.get()
            except tk.TclError:  # half-typed spinbox value
                d[k] = None
        return d

    def update_preset_label(*_):
        shown, note = cfg.preset_status(current_values(), base_preset["name"])
        preset_var.set(shown)
        preset_note.config(text=note)

    def collect():
        """Settings from the widgets, or None (after showing an error) if a field is invalid."""
        d = current_values()
        bad = [k for k, v in d.items() if v is None]
        if bad:
            messagebox.showerror("Invalid settings", "Not a valid number: " + ", ".join(bad))
            return None
        d["game_dir"] = game_var.get().strip()
        d["mod_name"] = s.mod_name
        # An exact match wins; otherwise remember the preset the values started from.
        d["preset"] = cfg.exact_preset(d) or (
            base_preset["name"] if base_preset["name"] in cfg.PRESETS else "")
        return cfg.Settings.from_dict(d)

    def apply_preset(_=None):
        name = preset_var.get()
        if name not in cfg.PRESETS:
            return
        base_preset["name"] = name
        for k, v in cfg.PRESETS[name].items():
            vars_[k].set(v)
        refresh_example()
        update_preset_label()

    preset_box.bind("<<ComboboxSelected>>", apply_preset)
    if base_preset["name"] not in cfg.PRESETS:  # old settings file / custom values
        base_preset["name"] = cfg.exact_preset(current_values())
    for v in vars_.values():
        v.trace_add("write", update_preset_label)
    update_preset_label()

    busy = {"on": False}

    def run_bg(fn, done_msg):
        if busy["on"]:
            return
        st = collect()
        if st is None:
            return
        errs = st.validate()
        if errs:
            messagebox.showerror("Invalid settings", "\n".join(errs))
            return
        busy["on"] = True
        build_btn.config(state="disabled")

        def work():
            try:
                fn(st, log)
                cfg.save(st, SETTINGS_FILE)
                log(done_msg)
            except Exception as e:  # report everything in the log window
                log("ERROR: " + str(e))
                log(traceback.format_exc())
            finally:
                busy["on"] = False
                msgs.put(lambda: build_btn.config(state="normal"))

        threading.Thread(target=work, daemon=True).start()

    build_btn = ttk.Button(btns, text="Build / Update mod", command=lambda: run_bg(
        builder.build, "Done. In Grim Dawn: Play -> Custom Game -> EasierProgression."))
    build_btn.pack(side="left")
    ttk.Button(btns, text="Remove mod", command=lambda: run_bg(builder.remove, "Mod removed.")).pack(side="left", padx=6)

    def open_mods():
        d = os.path.join(game_var.get(), "mods")
        if os.path.isdir(d):
            os.startfile(d)

    ttk.Button(btns, text="Open mods folder", command=open_mods).pack(side="left")
    def save_settings():
        st = collect()
        if st is not None:
            cfg.save(st, SETTINGS_FILE)
            log("Settings saved.")

    ttk.Button(btns, text="Save settings", command=save_settings).pack(side="right")

    if not gamedata.is_game_dir(s.game_dir):
        log("Could not find Grim Dawn automatically - please pick the install folder.")
    else:
        log(f"Grim Dawn found: {s.game_dir}")
    log("Adjust the values, then press 'Build / Update mod'. Rebuild after every game patch.")
    center_window(root)
    root.mainloop()


def _work_area(root):
    """(left, top, right, bottom) of the desktop work area (screen minus taskbar)."""
    if sys.platform == "win32":
        try:
            import ctypes
            from ctypes import wintypes
            rect = wintypes.RECT()
            if ctypes.windll.user32.SystemParametersInfoW(0x0030, 0, ctypes.byref(rect), 0):  # SPI_GETWORKAREA
                return rect.left, rect.top, rect.right, rect.bottom
        except Exception:
            pass
    return 0, 0, root.winfo_screenwidth(), root.winfo_screenheight()


def center_window(root):
    """Place the window in the middle of the desktop work area."""
    root.update_idletasks()
    min_w, min_h = root.minsize()
    w = max(root.winfo_reqwidth(), min_w)
    h = max(root.winfo_reqheight(), min_h)
    left, top, right, bottom = _work_area(root)
    # Account for the title bar / borders, which are not part of the requested size.
    frame_w = root.winfo_rootx() - root.winfo_x()
    title_h = root.winfo_rooty() - root.winfo_y()
    x = left + max(0, (right - left - w - 2 * frame_w) // 2)
    y = top + max(0, (bottom - top - h - title_h - frame_w) // 2)
    root.geometry(f"{w}x{h}+{x}+{y}")


def _safe_fmt(fmt, var):
    try:
        return fmt.format(var.get())
    except Exception:
        return ""


if __name__ == "__main__":
    if "--build" in sys.argv:
        cli_build()
    else:
        run_gui()
