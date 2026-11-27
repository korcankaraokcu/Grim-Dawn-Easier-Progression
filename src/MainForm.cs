using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EasierProgression
{
    sealed class MainForm : Form
    {
        readonly string settingsFile;
        readonly Settings s;
        readonly float dpi;
        readonly Dictionary<string, SliderRow> sliders = new Dictionary<string, SliderRow>();
        readonly Dictionary<string, CheckBox> checks = new Dictionary<string, CheckBox>();
        readonly List<TableLayoutPanel> pages = new List<TableLayoutPanel>();
        static readonly Color HintColor = Color.FromArgb(0x55, 0x55, 0x55);

        TextBox gameBox, logBox;
        ComboBox presetBox;
        Label presetNote, example;
        Button buildBtn;
        TabControl tabs;
        string basePreset; // preset the current values started from
        bool ready, busy;

        public MainForm(string settingsFile)
        {
            this.settingsFile = settingsFile;
            Text = "Grim Dawn - Easier Progression";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.None; // pixel sizes go through S() instead
            using (var g = CreateGraphics()) dpi = g.DpiX / 96f;
            StartPosition = FormStartPosition.Manual;

            s = Program.LoadSettings(settingsFile);
            BuildUi();

            basePreset = s.Preset;
            if (!Settings.IsPreset(basePreset)) basePreset = Settings.ExactPreset(CurrentValues()); // old file / custom values
            ready = true;
            UpdatePresetLabel();
            RefreshExample();

            Log(GameData.IsGameDir(s.GameDir)
                ? $"Grim Dawn found: {s.GameDir}"
                : "Could not find Grim Dawn automatically - please pick the install folder.");
            Log("Adjust the values, then press 'Build / Update mod'. Rebuild after every game patch.");
        }

        int S(int px) => (int)Math.Round(px * dpi);

        // ------------------------------------------------------------------ layout
        void BuildUi()
        {
            SuspendLayout();
            var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(S(10)) };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, S(170)));
            Controls.Add(main);

            // --- game folder + presets
            var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, Margin = Padding.Empty };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.Controls.Add(new Label { Text = "Grim Dawn folder:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            gameBox = new TextBox { Text = s.GameDir, Dock = DockStyle.Fill, Margin = new Padding(S(4), S(3), S(4), S(3)) };
            top.Controls.Add(gameBox, 1, 0);
            var browse = new Button { Text = "Browse...", AutoSize = true };
            browse.Click += (_, __) => Browse();
            top.Controls.Add(browse, 2, 0);

            top.Controls.Add(new Label { Text = "Preset:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(S(3), S(6), S(3), 0) }, 0, 1);
            var presetRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(S(4), S(6), 0, 0) };
            presetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(200), Margin = Padding.Empty };
            presetBox.Items.AddRange(Settings.Presets.Select(p => (object)p.Key).ToArray());
            presetBox.SelectionChangeCommitted += (_, __) => ApplyPreset(presetBox.SelectedItem as string);
            presetNote = new Label { AutoSize = true, ForeColor = HintColor, Anchor = AnchorStyles.Left, Margin = new Padding(S(8), S(4), 0, 0) };
            presetRow.Controls.Add(presetBox);
            presetRow.Controls.Add(presetNote);
            top.Controls.Add(presetRow, 1, 1);
            main.Controls.Add(top, 0, 0);

            tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(0, S(8), 0, S(8)) };
            main.Controls.Add(tabs, 0, 1);

            // --- XP & factions
            var p = Page("XP & Factions");
            Slider(p, "xp_multiplier", "Monster XP", 1, 10, 0.25,
                "Multiplies experience from every monster kill (all difficulties). Quest XP and level " +
                "requirements are unchanged, so skill/attribute/devotion points stay vanilla.");
            Slider(p, "faction_gain_multiplier", "Friendly rep per kill", 1, 20, 0.25,
                "Reputation gained with your factions when you kill their enemies " +
                "(e.g. beasts -> Devil's Crossing & Coven of Ugdenbog). Bounty/quest rewards are not affected.");
            Slider(p, "faction_hostile_multiplier", "Hostile rep per kill", 0.25, 5, 0.25,
                "How fast hostile factions start to hate you. Higher = Nemesis bosses appear sooner. " +
                "Keep at 1.00x on Hardcore unless you want that.");
            Check(p, "faction_no_diminishing", "Remove diminishing reputation from kills",
                "Vanilla halves (common) / thirds (champion) kill reputation once you reach Respected/Honored.");
            Check(p, "free_inventory_bags", "Sell free inventory bags at Devil's Crossing",
                "Kerrick (the Devil's Crossing merchant) has 5 Inventory Bags for 0 gold from the start (restocked when his stock rerolls). " +
                "The Devil's Crossing faction vendor also stocks them once you are Friendly with them.");

            // --- loot (quality, amount, gold)
            p = Page("Loot");
            Slider(p, "legendary_multiplier", "Legendary / Mythical", 0.5, 10, 0.25,
                "Relative chance that a drop is a Legendary (incl. Mythical) item. Number of drops is unchanged; " +
                "the extra Legendaries replace lower items. Approximate: loot tables switch by monster level, " +
                "so the real gain is usually ~90-100% of the value shown.");
            Slider(p, "epic_multiplier", "Epic", 0.5, 10, 0.25, "Relative chance that a drop is an Epic item.");
            Slider(p, "mi_multiplier", "Monster Infrequents", 0.5, 10, 0.25,
                "Relative chance of green named 'monster infrequent' base items.");
            Slider(p, "drop_amount_multiplier", "Drop rate (chance / chest items)", 1, 5, 1,
                "Monsters: multiplies the chance each loot slot drops something (e.g. 30% -> 60%). Each slot " +
                "still drops at most one item, so slots above 50% gain less and guaranteed boss/hero drops " +
                "stay the same. Chests and breakables: multiplies how many items they roll. " +
                "Whole numbers only: chests roll whole items.", "{0:0}x");
            Slider(p, "gold_multiplier", "Gold amount", 0.5, 10, 0.25,
                "Gold per pile from monsters, chests and breakables.");
            Slider(p, "rare_affix_multiplier", "Rare (green) affixes", 0.5, 10, 0.25,
                "Random-affix items: weight of rolls that include a rare affix.");
            Slider(p, "double_affix_multiplier", "Prefix + Suffix", 0.5, 5, 0.25,
                "Random-affix items: extra weight for rolls that get both a prefix and a suffix.");
            Slider(p, "plain_item_multiplier", "Plain / broken items", 0, 1, 0.05,
                "Weight of white/broken rolls without affixes. 0 removes them wherever the table has other options.");

            // --- stat rolls
            p = Page("Stat Rolls");
            AddRow(p, Hint(
                "Every random affix (magic & rare prefixes/suffixes, monster-infrequent affixes, crafting " +
                "bonuses, Ascended affixes) rolls each stat inside a range around its base value. Choose where " +
                "the new range starts and ends, as a percentage of the vanilla range:\n" +
                "   0% = vanilla minimum, 100% = vanilla maximum.\n" +
                "Min 50% / Max 100% means every roll lands in the upper half of the vanilla range. " +
                "Min = Max gives fixed values. Epic/Legendary base stats have no random roll in the game " +
                "data, so they are unchanged. Existing items in this mod's saves are re-evaluated too.",
                SystemColors.ControlText, 0));
            Slider(p, "roll_min_percent", "New minimum", 0, 100, 1, "", "{0:0}%");
            Slider(p, "roll_max_percent", "New maximum", 0, 100, 1, "", "{0:0}%");
            example = new Label { AutoSize = true, Font = new Font("Consolas", 10f), Margin = new Padding(S(3), S(14), S(3), 0) };
            AddRow(p, example);

            // --- buttons + log
            var btns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, AutoSize = true, Margin = Padding.Empty };
            for (int i = 0; i < 3; i++) btns.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            btns.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buildBtn = Btn("Build / Update mod", () => RunBg(true));
            btns.Controls.Add(buildBtn, 0, 0);
            btns.Controls.Add(Btn("Remove mod", () => RunBg(false)), 1, 0);
            btns.Controls.Add(Btn("Open mods folder", OpenMods), 2, 0);
            btns.Controls.Add(Btn("Save settings", SaveSettings), 4, 0);
            main.Controls.Add(btns, 0, 2);

            logBox = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9f), BackColor = SystemColors.Window, Margin = new Padding(0, S(8), 0, 0),
            };
            main.Controls.Add(logBox, 0, 3);
            ActiveControl = buildBtn; // not the folder box, which would start with its text selected
            ResumeLayout();
        }

        Button Btn(string text, Action onClick)
        {
            var b = new Button { Text = text, AutoSize = true, Padding = new Padding(S(6), 0, S(6), 0) };
            b.Click += (_, __) => onClick();
            return b;
        }

        TableLayoutPanel Page(string title)
        {
            var tp = new TabPage(title) { AutoScroll = true, BackColor = SystemColors.Window };
            var t = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(S(7)) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tp.Controls.Add(t);
            tabs.TabPages.Add(tp);
            pages.Add(t);
            return t;
        }

        Label Hint(string text, Color color, int topMargin) => new Label
        {
            Text = text, AutoSize = true, ForeColor = color, MaximumSize = new Size(S(660), 0), UseMnemonic = false,
            Margin = new Padding(S(3), topMargin, S(3), 0),
        };

        /// <summary>Add a control spanning all four columns as a new row.</summary>
        static void AddRow(TableLayoutPanel t, Control c)
        {
            int row = t.RowCount;
            t.RowCount = row + 1;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(c, 0, row);
            t.SetColumnSpan(c, 4);
        }

        void Slider(TableLayoutPanel t, string key, string label, double lo, double hi, double res, string hint,
                    string fmt = "{0:0.00}x")
        {
            int row = t.RowCount;
            t.RowCount = row + 1;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var top = new Padding(S(3), S(8), S(3), 0);
            var sr = new SliderRow(lo, hi, res, v => string.Format(fmt, v), S);
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = top }, 0, row);
            sr.Bar.Margin = new Padding(S(8), S(8), S(8), 0);
            t.Controls.Add(sr.Bar, 1, row);
            sr.Shown.Margin = top;
            t.Controls.Add(sr.Shown, 2, row);
            sr.Spin.Margin = top;
            t.Controls.Add(sr.Spin, 3, row);
            if (hint != "") AddRow(t, Hint(hint, HintColor, 0));

            sr.Value = (double)s[key];
            sr.Changed += () =>
            {
                if (key.StartsWith("roll_")) RefreshExample();
                UpdatePresetLabel();
            };
            sliders[key] = sr;
        }

        void Check(TableLayoutPanel t, string key, string label, string hint)
        {
            var cb = new CheckBox { Text = label, AutoSize = true, Checked = (bool)s[key], Margin = new Padding(S(3), S(10), S(3), 0) };
            cb.CheckedChanged += (_, __) => UpdatePresetLabel();
            AddRow(t, cb);
            AddRow(t, Hint(hint, HintColor, 0));
            checks[key] = cb;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Size the tabs to the tallest page, then the window to its content, centred on the desktop.
            int pageW = 0, pageH = 0;
            foreach (var t in pages)
            {
                var ps = t.GetPreferredSize(Size.Empty);
                pageW = Math.Max(pageW, ps.Width);
                pageH = Math.Max(pageH, ps.Height);
            }
            var chrome = tabs.Size - tabs.DisplayRectangle.Size;
            var tp = tabs.TabPages[0].Padding;
            tabs.MinimumSize = new Size(pageW + chrome.Width + tp.Horizontal + S(4), pageH + chrome.Height + tp.Vertical + S(4));
            var want = Controls[0].GetPreferredSize(Size.Empty);
            tabs.MinimumSize = Size.Empty;

            var work = Screen.PrimaryScreen.WorkingArea;
            var nonClient = Size - ClientSize;
            int w = Math.Min(Math.Max(want.Width + nonClient.Width, S(760)), work.Width);
            int h = Math.Min(Math.Max(want.Height + nonClient.Height, S(640)), work.Height);
            MinimumSize = new Size(Math.Min(w, S(760)), Math.Min(h, S(640)));
            Bounds = new Rectangle(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h);
        }

        // ------------------------------------------------------------------ presets
        Dictionary<string, object> CurrentValues()
        {
            var d = new Dictionary<string, object>();
            foreach (var kv in sliders) d[kv.Key] = kv.Value.Value;
            foreach (var kv in checks) d[kv.Key] = kv.Value.Checked;
            return d;
        }

        void UpdatePresetLabel()
        {
            if (!ready) return;
            var (shown, note) = Settings.PresetStatus(CurrentValues(), basePreset);
            presetBox.SelectedIndex = shown == "" ? -1 : presetBox.Items.IndexOf(shown);
            presetNote.Text = note;
        }

        void ApplyPreset(string name)
        {
            var values = Settings.PresetValues(name);
            if (values == null) return;
            basePreset = name;
            ready = false; // one label update at the end instead of one per value
            foreach (var kv in values)
            {
                if (kv.Value is bool b) checks[kv.Key].Checked = b;
                else sliders[kv.Key].Value = (double)kv.Value;
            }
            ready = true;
            RefreshExample();
            UpdatePresetLabel();
        }

        /// <summary>Settings from the widgets.</summary>
        Settings Collect()
        {
            var st = new Settings();
            var d = CurrentValues();
            foreach (var kv in d) st[kv.Key] = kv.Value;
            st.GameDir = gameBox.Text.Trim();
            st.ModName = s.ModName;
            // An exact match wins; otherwise remember the preset the values started from.
            string exact = Settings.ExactPreset(d);
            st.Preset = exact != "" ? exact : Settings.IsPreset(basePreset) ? basePreset : "";
            return st;
        }

        void RefreshExample()
        {
            if (!sliders.TryGetValue("roll_min_percent", out var mn) || !sliders.TryGetValue("roll_max_percent", out var mx))
                return;
            double a = mn.Value, b = mx.Value;
            var lines = new List<string> { "Examples (stat base value, jitter):" };
            foreach (var (bas, jit, unit) in new[] { (20.0, 20.0, "% Physical Damage"), (5.0, 20.0, " to All Attributes"),
                                                     (100.0, 50.0, " Offensive Ability (crafting bonus)") })
            {
                double lo = bas * (1 - jit / 100), hi = bas * (1 + jit / 100);
                if (a > b)
                {
                    lines.Add("  minimum is above maximum - fix the sliders");
                    break;
                }
                var (sc, nj) = Transforms.RollTransform(jit, a, b);
                double nlo = bas * sc * (1 - nj / 100), nhi = bas * sc * (1 + nj / 100);
                lines.Add($"  +{Transforms.FormatG(lo)}-{Transforms.FormatG(hi)}{unit}  ->  " +
                          $"+{Transforms.FormatG(nlo, 4)}-{Transforms.FormatG(nhi, 4)}{unit}");
            }
            example.Text = string.Join("\n", lines);
        }

        // ------------------------------------------------------------------ actions
        void Log(string msg)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => Log(msg))); }
                catch (InvalidOperationException) { } // window closed mid-build
                return;
            }
            logBox.AppendText(msg.Replace("\n", Environment.NewLine) + Environment.NewLine);
        }

        void Browse()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Pick the Grim Dawn install folder" })
            {
                dlg.SelectedPath = Directory.Exists(gameBox.Text) ? gameBox.Text : @"C:\";
                if (dlg.ShowDialog(this) == DialogResult.OK) gameBox.Text = Path.GetFullPath(dlg.SelectedPath);
            }
        }

        void RunBg(bool build)
        {
            if (busy) return;
            var st = Collect();
            var errs = st.Validate();
            if (errs.Count > 0)
            {
                MessageBox.Show(this, string.Join("\n", errs), "Invalid settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            busy = true;
            buildBtn.Enabled = false;
            Task.Run(() =>
            {
                try
                {
                    if (build)
                    {
                        Builder.Build(st, Log);
                        Log("Done. In Grim Dawn: Play -> Custom Game -> EasierProgression.");
                    }
                    else if (Builder.Remove(st, Log))
                        Log("Mod removed.");
                    st.Save(settingsFile);
                }
                catch (Exception e) when (e is ArgumentException || e is IOException
                                          || e is UnauthorizedAccessException || e is InvalidOperationException)
                {
                    Log("ERROR: " + e.Message);
                }
                catch (Exception e) // unexpected: include the stack trace
                {
                    Log("ERROR: " + e);
                }
                finally
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            busy = false;
                            buildBtn.Enabled = true;
                            // Disabling the button moved focus to "Remove mod"; don't leave Enter there.
                            if (build) buildBtn.Focus();
                        }));
                    }
                    catch (InvalidOperationException) { }
                }
            });
        }

        void OpenMods()
        {
            string d = Path.Combine(gameBox.Text.Trim(), "mods");
            if (Directory.Exists(d)) Process.Start("explorer.exe", "\"" + d + "\"");
        }

        void SaveSettings()
        {
            Collect().Save(settingsFile);
            Log("Settings saved.");
        }
    }

    /// <summary>Track bar + spin box + value label for one numeric setting, snapped to `res` steps.</summary>
    sealed class SliderRow
    {
        public readonly TrackBar Bar;
        public readonly NumericUpDown Spin;
        public readonly Label Shown;
        public event Action Changed;
        readonly double lo, hi, res;
        readonly Func<double, string> fmt;
        bool syncing;

        public SliderRow(double lo, double hi, double res, Func<double, string> fmt, Func<int, int> S)
        {
            this.lo = lo;
            this.hi = hi;
            this.res = res;
            this.fmt = fmt;
            Bar = new TrackBar
            {
                Minimum = 0, Maximum = (int)Math.Round((hi - lo) / res), TickStyle = TickStyle.None, BackColor = SystemColors.Window,
                AutoSize = false, Height = S(26), Dock = DockStyle.Fill, SmallChange = 1,
                LargeChange = Math.Max(1, (int)Math.Round(1 / res)),
            };
            Spin = new NumericUpDown
            {
                Minimum = (decimal)lo, Maximum = (decimal)hi, Increment = (decimal)res,
                DecimalPlaces = res < 1 ? 2 : 0, Width = S(64), TextAlign = HorizontalAlignment.Right,
            };
            Shown = new Label { AutoSize = false, Width = S(60), TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Right };
            Bar.Scroll += (_, __) => Value = Math.Round(lo + Bar.Value * res, 4);
            Spin.ValueChanged += (_, __) =>
            {
                if (syncing) return;
                double v = (double)Spin.Value;
                Value = Math.Round(Math.Round(v / res) * res, 4); // snap typed values to the step
            };
        }

        public double Value
        {
            get => (double)Spin.Value;
            set
            {
                double v = Math.Max(lo, Math.Min(hi, value));
                syncing = true;
                Spin.Value = (decimal)v;
                Bar.Value = Math.Max(Bar.Minimum, Math.Min(Bar.Maximum, (int)Math.Round((v - lo) / res)));
                Shown.Text = fmt(v);
                syncing = false;
                Changed?.Invoke();
            }
        }
    }
}
