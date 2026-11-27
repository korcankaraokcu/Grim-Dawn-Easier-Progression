using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace EasierProgression
{
    /// <summary>Turn Settings into modified copies of game records.
    ///
    /// Every Apply* step reads the merged game database and puts *modified copies* of the records
    /// it changes into Ctx.Out; the originals are never mutated.</summary>
    public static class Transforms
    {
        sealed class Ctx
        {
            public Db Db;
            public Settings S;
            public Dictionary<string, Record> Out = new Dictionary<string, Record>(StringComparer.Ordinal);
            public List<KeyValuePair<string, long>> Stats = new List<KeyValuePair<string, long>>();

            /// <summary>The working copy of a record, created on first touch.</summary>
            public Record Changed(string name)
            {
                if (!Out.TryGetValue(name, out var r)) Out[name] = r = Db.Get(name).Copy();
                return r;
            }

            public void Stat(string key, long value)
            {
                int i = Stats.FindIndex(kv => kv.Key == key);
                if (i >= 0) Stats[i] = new KeyValuePair<string, long>(key, value);
                else Stats.Add(new KeyValuePair<string, long>(key, value));
            }

            public long StatOr0(string key) => Stats.FirstOrDefault(kv => kv.Key == key).Value;
        }

        static string Norm(string name) => Arz.NormName(name);

        static bool Near1(double v) => Math.Abs(v - 1.0) < 1e-9;

        static Regex Re(string p) => new Regex(p, RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>`digits` significant digits, shortest form (2, 1.5, 0.333333, 1e+06).</summary>
        public static string FormatG(double v, int digits = 6)
        {
            string s = v.ToString("G" + digits, CultureInfo.InvariantCulture);
            int e = s.IndexOf('E');
            if (e < 0) return s;
            string exp = s.Substring(e + 1);
            char sign = exp[0] == '-' ? '-' : '+';
            string d = exp.TrimStart('+', '-').TrimStart('0').PadLeft(2, '0');
            return s.Substring(0, e) + "e" + sign + d;
        }

        static string Times(string eq, double m) => $"({eq})*{FormatG(m)}";

        /// <summary>Compensated (Neumaier) sum: much less rounding error than a plain loop.</summary>
        static double Sum(IEnumerable<double> xs)
        {
            double s = 0, c = 0;
            foreach (double x in xs)
            {
                double t = s + x;
                c += Math.Abs(s) >= Math.Abs(x) ? (s - t) + x : (x - t) + s;
                s = t;
            }
            return c != 0 && !double.IsInfinity(c) && !double.IsNaN(c) ? s + c : s;
        }

        // ------------------------------------------------------------------------- XP
        const string XpRecord = "records/game/experienceformulas.dbr";
        static readonly string[] XpEquations = { "experienceEquation", "eliteExperienceEquation",
                                                 "ultimateExperienceEquation", "challengeExperienceEquation" };

        static void ApplyXp(Ctx c)
        {
            if (Near1(c.S.XpMultiplier) || !c.Db.Contains(XpRecord)) return;
            var rec = c.Changed(XpRecord);
            foreach (var key in XpEquations)
            {
                string eq = rec.Str(key);
                if (string.IsNullOrEmpty(eq)) continue;
                rec.SetStr(key, Times(eq, c.S.XpMultiplier));
                c.Stat("xp equations", c.StatOr0("xp equations") + 1);
            }
        }

        // -------------------------------------------------------------------- Factions
        const string FactionDir = "records/controllers/factions/";
        static readonly Regex Bonus = Re(@"^killBonus\d+(Boss|Champion|Common|Hero)$");
        static readonly Regex Penalty = Re(@"^killPenalty(Boss|Champion|Common|Hero)$");
        static readonly Regex GainRed = Re(@"^(common|champion)MonsterGainReduction$");

        static void ApplyFactions(Ctx c)
        {
            double gain = c.S.FactionGainMultiplier, hostile = c.S.FactionHostileMultiplier;
            bool noDim = c.S.FactionNoDiminishing;
            if (Near1(gain) && Near1(hostile) && !noDim) return;
            int n = 0;
            foreach (var kv in c.Db)
            {
                if (!kv.Key.StartsWith(FactionDir, StringComparison.Ordinal)) continue;
                var rec = kv.Value;
                var edits = new List<KeyValuePair<string, List<double>>>();
                for (int i = 0; i < rec.Count; i++)
                {
                    string key = rec.KeyAt(i);
                    var f = rec.FieldAt(i);
                    if (f.Type != FieldType.Float || f.Nums.Count == 0 || f.Nums[0] == 0) continue;
                    if (Bonus.IsMatch(key) && !Near1(gain))
                        edits.Add(Edit(key, f.Nums.Select(v => v * gain)));
                    else if (Penalty.IsMatch(key) && !Near1(hostile))
                        edits.Add(Edit(key, f.Nums.Select(v => v * hostile)));
                    else if (noDim && GainRed.IsMatch(key))
                        edits.Add(Edit(key, f.Nums.Select(_ => 1.0)));
                }
                if (edits.Count == 0) continue;
                var copy = c.Changed(kv.Key);
                foreach (var e in edits) copy.SetNums(e.Key, e.Value, FieldType.Float);
                n++;
            }
            c.Stat("faction controllers", n);
        }

        static KeyValuePair<string, List<double>> Edit(string key, IEnumerable<double> values) =>
            new KeyValuePair<string, List<double>>(key, values.ToList());

        // ---------------------------------------------------------------- Drop quality
        static readonly Regex GearClass = Re("^(Armor|Weapon)");
        const int MaxLevel = 100;
        static readonly HashSet<string> WeightedTableClasses = new HashSet<string> { "LootItemTable_DynWeight", "LootMasterTable" };
        static readonly HashSet<string> DropperClasses = new HashSet<string> { "Monster", "SpiritHost" };
        const string ChestTemplate = "database/templates/fixeditemloot.tpl";
        static readonly Regex TableEntry = Re(@"^lootName(\d+)$");
        static readonly Regex MonsterLoot = Re(@"^loot(\w+?)Item(\d+)$");
        static readonly Regex ChestLoot = Re(@"^loot(\d+)Name(\d+)$");

        // Tier index: 0 = common gear, 1 = MI, 2 = Epic, 3 = Legendary, 4 = not gear (crafting
        // materials, blueprints, components, potions ...). Non-gear keeps its vanilla share.
        const int Tiers = 5, NonGear = 4;

        static int ItemTier(Record rec)
        {
            if (!GearClass.IsMatch(rec.Str("Class") ?? "")) return NonGear;
            switch (rec.Str("itemClassification"))
            {
                case "Rare": return 1;
                case "Epic": return 2;
                case "Legendary": return 3;
                default: return 0;
            }
        }

        /// <summary>Target tier shares for a weighted choice whose vanilla tier shares are p.
        ///
        /// Non-gear drops (crafting materials, blueprints ...) keep their vanilla share. Of the rest,
        /// Epic + Legendary are filled first, MI next, and common gear takes whatever is left, so a
        /// higher MI multiplier can never eat into the Epic/Legendary boost. When a choice has too
        /// little common gear to give away, the lower-priority tier is the one that gets capped.</summary>
        static double[] TierTargets(double[] p, Settings s)
        {
            var t = new double[Tiers];
            t[NonGear] = p[NonGear];
            double left = 1.0 - p[NonGear];
            if (left < 1e-12) left = 0;
            var steps = new[]
            {
                (new[] { 2, 3 }, new[] { s.EpicMultiplier, s.LegendaryMultiplier }),
                (new[] { 1 }, new[] { s.MiMultiplier }),
            };
            foreach (var (tiers, mults) in steps)
            {
                var want = new double[tiers.Length];
                for (int i = 0; i < tiers.Length; i++) want[i] = p[tiers[i]] * mults[i];
                double total = Sum(want);
                double scale = total > 0 ? Math.Min(1.0, left / total) : 0.0;
                for (int i = 0; i < tiers.Length; i++) t[tiers[i]] = want[i] * scale;
                left -= total * scale;
                // When a step takes all that is left, the subtraction leaves rounding noise (~1e-16)
                // instead of 0. Snap it, or the next tier gets a meaningless sliver of share.
                if (left < 1e-12) left = 0;
            }
            if (p[0] > 0)
                t[0] = left;
            else if (left > 1e-12)
            {
                // No common gear to absorb the rest (only possible with multipliers below 1):
                // scale the gear tiers up to their vanilla total instead.
                double gear = Sum(t.Take(NonGear)), want = 1.0 - p[NonGear];
                if (gear > 0)
                    for (int i = 0; i < NonGear; i++) t[i] *= want / gear;
                else
                    t = (double[])p.Clone();
            }
            return t;
        }

        /// <summary>group -> [(nameKey, weightKey)] for every weighted choice in a record.
        /// Entries inside one group compete with each other (relative weights).</summary>
        static List<KeyValuePair<string, List<(string Name, string Weight)>>> WeightGroups(Record rec)
        {
            string cls = rec.Str("Class");
            var groups = new List<KeyValuePair<string, List<(string, string)>>>();
            void Add(string g, string nk, string wk)
            {
                int i = groups.FindIndex(x => x.Key == g);
                if (i < 0)
                {
                    groups.Add(new KeyValuePair<string, List<(string, string)>>(g, new List<(string, string)>()));
                    i = groups.Count - 1;
                }
                groups[i].Value.Add((nk, wk));
            }

            if (cls != null && WeightedTableClasses.Contains(cls))
            {
                foreach (var key in rec.Keys)
                {
                    var m = TableEntry.Match(key);
                    if (m.Success) Add("", key, "lootWeight" + m.Groups[1].Value);
                }
            }
            else if (cls != null && DropperClasses.Contains(cls))
            {
                foreach (var key in rec.Keys)
                {
                    var m = MonsterLoot.Match(key);
                    if (m.Success)
                        Add(m.Groups[1].Value, key, $"chanceToEquip{m.Groups[1].Value}Item{m.Groups[2].Value}");
                }
            }
            else if ((rec.Str("templateName") ?? "").ToLowerInvariant() == ChestTemplate)
            {
                foreach (var key in rec.Keys)
                {
                    var m = ChestLoot.Match(key);
                    if (m.Success) Add(m.Groups[1].Value, key, $"loot{m.Groups[1].Value}Weight{m.Groups[2].Value}");
                }
            }
            return groups;
        }

        static List<double> ScaledValues(Field f, double factor)
        {
            // A positive weight never rounds down to 0; only a factor of exactly 0 removes an entry.
            if (f.Type == FieldType.Int)
                return f.Nums.Select(v => v > 0 ? (factor > 0 ? Math.Max(1, Math.Round(v * factor)) : 0) : v).ToList();
            return f.Nums.Select(v => v > 0 ? v * factor : v).ToList();
        }

        /// <summary>Vanilla (P) and after-mod (Q) tier shares of a loot record at each character level
        /// 1..MaxLevel (index level-1). An entry is null where the record gives nothing at that level.
        /// Levels with the same shares share one array.</summary>
        sealed class Shares
        {
            public double[][] P = new double[MaxLevel][], Q = new double[MaxLevel][];
        }

        /// <summary>One stretch of levels inside a weighted choice where nothing underneath changes.</summary>
        sealed class Band
        {
            public List<int> Levels = new List<int>();
            public double[][] Mix;     // per entry: after-mod tier shares it leads to, null if absent here
            public double[] Vanilla;   // the choice's vanilla tier shares in this band
            public double[] Target;    // what they should become
        }

        static readonly Shares[] LeafShares = Enumerable.Range(0, Tiers).Select(t =>
        {
            var one = new double[Tiers];
            one[t] = 1.0;
            var sh = new Shares();
            for (int l = 0; l < MaxLevel; l++) sh.P[l] = sh.Q[l] = one;
            return sh;
        }).ToArray();

        /// <summary>Plans the rarity reweighting.
        ///
        /// Each weighted choice is planned on its own, per level: its vanilla tier shares at each
        /// level give target shares (see TierTargets), and one set of entry weights is fitted to hit
        /// the targets of every level as closely as possible (see FitWeights). Shared sub-tables are
        /// planned once and already deliver their own targets, so a parent only corrects what they
        /// could not do themselves (e.g. a pure-MI table).</summary>
        sealed class RarityPlan
        {
            readonly Db db;
            readonly Settings s;
            // A key with a null value = being computed (cycle guard) or no result.
            readonly Dictionary<string, Shares> memo = new Dictionary<string, Shares>(StringComparer.Ordinal);
            readonly Dictionary<string, Dictionary<string, double>> plans =
                new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);

            public RarityPlan(Db db, Settings s)
            {
                this.db = db;
                this.s = s;
            }

            /// <summary>Tier shares of a loot record, or null if it never gives anything.</summary>
            public Shares Node(string name, int depth = 0)
            {
                if (memo.TryGetValue(name, out var cached)) return cached;
                var rec = db.Get(name);
                if (rec == null || depth > 25) return null;
                memo[name] = null; // cycle guard
                string cls = rec.Str("Class");
                Shares res;
                if (cls == "LevelTable")
                {
                    // Picks one sub-table by level: levels[i] .. levels[i+1]-1 use records[i].
                    var lv = rec.AllNum("levels");
                    var subs = rec.AllStr("records");
                    res = new Shares();
                    bool any = false;
                    for (int i = 0; i < subs.Count; i++)
                    {
                        string sub = Norm(subs[i]);
                        double lo = i < lv.Count ? lv[i] : 1;
                        double hi = i + 1 < lv.Count ? lv[i + 1] : MaxLevel + 1;
                        if (sub == "" || Math.Min(hi, MaxLevel + 1) <= Math.Max(lo, 1)) continue;
                        var r = Node(sub, depth + 1);
                        if (r == null) continue;
                        for (int l = Math.Max(1, (int)Math.Ceiling(lo)); l < hi && l <= MaxLevel; l++)
                        {
                            if (res.P[l - 1] != null || r.P[l - 1] == null) continue; // first match wins
                            res.P[l - 1] = r.P[l - 1];
                            res.Q[l - 1] = r.Q[l - 1];
                            any = true;
                        }
                    }
                    if (!any) res = null;
                }
                else if (cls != null && WeightedTableClasses.Contains(cls))
                {
                    var entries = WeightGroups(rec).FirstOrDefault(g => g.Key == "").Value
                                  ?? new List<(string, string)>();
                    var plan = Plan(rec, entries, depth);
                    plans[name] = plan.Factors ?? new Dictionary<string, double>();
                    res = plan.Shares;
                }
                else
                    res = LeafShares[ItemTier(rec)];
                memo[name] = res;
                return res;
            }

            /// <summary>Plan one weighted choice: (vanilla/new shares, weightKey -> factor).
            /// Both are null if nothing in it leads anywhere.</summary>
            (Shares Shares, Dictionary<string, double> Factors) Plan(Record rec, List<(string Name, string Weight)> entries, int depth)
            {
                var rows = new List<(string Wk, double W, Shares R)>();
                foreach (var (nk, wk) in entries)
                {
                    double w = rec.Num(wk, 0);
                    string target = Norm(rec.Str(nk));
                    var r = w > 0 && target != "" ? Node(target, depth + 1) : null;
                    if (r != null) rows.Add((wk, w, r));
                }
                if (rows.Count == 0) return (null, null);

                // Group the levels into bands where every entry gives the same thing.
                var bands = new List<Band>();
                for (int l = 0; l < MaxLevel; l++)
                {
                    if (rows.All(row => row.R.Q[l] == null)) continue;
                    Band band = bands.FirstOrDefault(b =>
                    {
                        int rep = b.Levels[0];
                        return rows.All(row => ReferenceEquals(row.R.Q[rep], row.R.Q[l]) && ReferenceEquals(row.R.P[rep], row.R.P[l]));
                    });
                    if (band == null)
                    {
                        band = new Band { Mix = rows.Select(row => row.R.Q[l]).ToArray() };
                        double tot = Sum(rows.Where(row => row.R.P[l] != null).Select(row => row.W));
                        band.Vanilla = new double[Tiers];
                        for (int t = 0; t < Tiers; t++)
                            band.Vanilla[t] = Sum(rows.Where(row => row.R.P[l] != null).Select(row => row.W * row.R.P[l][t])) / tot;
                        band.Target = TierTargets(band.Vanilla, s);
                        bands.Add(band);
                    }
                    band.Levels.Add(l);
                }
                if (bands.Count == 0) return (null, null);

                var g = FitWeights(rows.Select(row => row.W).ToArray(), bands);

                var factors = new Dictionary<string, double>(StringComparer.Ordinal);
                for (int i = 0; i < rows.Count; i++) factors[rows[i].Wk] = g[i];

                var res = new Shares();
                foreach (var band in bands)
                {
                    var q = new double[Tiers];
                    double newTot = 0;
                    for (int i = 0; i < rows.Count; i++)
                    {
                        if (band.Mix[i] == null) continue;
                        double wi = rows[i].W * factors[rows[i].Wk];
                        newTot += wi;
                        for (int t = 0; t < Tiers; t++) q[t] += wi * band.Mix[i][t];
                    }
                    bool keep = newTot > 0;
                    for (int t = 0; t < Tiers; t++) q[t] = keep ? q[t] / newTot : 0;
                    foreach (int l in band.Levels)
                    {
                        res.P[l] = band.Vanilla;
                        res.Q[l] = keep ? q : null;
                    }
                }
                return (res, factors);
            }

            /// <summary>weightKey -> factor for one weighted choice of a record.</summary>
            public Dictionary<string, double> GroupFactors(string name, Record rec, string group,
                                                           List<(string Name, string Weight)> entries)
            {
                string cls = rec.Str("Class");
                if (group == "" && cls != null && WeightedTableClasses.Contains(cls))
                {
                    Node(name);
                    return plans.TryGetValue(name, out var pf) ? pf : new Dictionary<string, double>();
                }
                return Plan(rec, entries, 0).Factors ?? new Dictionary<string, double>();
            }
        }

        /// <summary>Weight factors that make a weighted choice deliver the target tier shares.
        ///
        /// w[i] = vanilla weight of entry i. Each band (a stretch of levels) says what every entry
        /// leads to there, plus the choice's vanilla and target shares. One set of factors has to
        /// serve all bands, weighted by how many levels each covers.
        ///
        /// Entries that only ever lead to tiers whose target is 0 get factor 0 (unless a band would be
        /// left with nothing). The others are grouped by what they lead to at every level; each group
        /// gets one factor exp(theta), so entries that give the same thing keep their relative
        /// weights. The thetas are fitted by damped Gauss-Newton to the targets of every band.
        ///
        /// Errors are log ratios (twice too much costs the same as half too little), so a 0.005%
        /// Legendary share counts as much as a 30% MI share. Common gear only takes what's left and
        /// isn't scored. When the targets can't all be met (an entry that brings a wanted tier also
        /// brings an unwanted one, or levels pull in different directions) the fit settles between
        /// them, but a tier that leaves the range between its vanilla share and its target costs
        /// WrongWay times as much. Non-gear may rise (only its plain error counts then) but not fall.
        /// The same is scored for every entry on its own: what it brings of a tier should change by
        /// that tier's factor, so items inside a tier keep their vanilla proportions (one Legendary
        /// can't crowd out another, crafting materials can't stand in for blueprints). A small pull
        /// towards the vanilla weights picks the smallest change among equally good answers.</summary>
        static double[] FitWeights(double[] w, List<Band> bands)
        {
            const double WrongWay = 10;  // residual weight (squared: 100x)
            const double Pull = 1e-2;    // residual weight of each log-factor (towards vanilla)
            const double LogLimit = 10;  // factors within e^-10 .. e^10 of vanilla
            int n = w.Length, nb = bands.Count;
            double levelsTotal = bands.Sum(b => b.Levels.Count);

            var wanted = new bool[n];
            for (int i = 0; i < n; i++)
                foreach (var b in bands)
                {
                    var m = b.Mix[i];
                    if (m == null) continue;
                    for (int t = 0; t < Tiers; t++)
                        if (b.Target[t] > 0 && m[t] > 1e-12) wanted[i] = true;
                }
            // Never leave a band with only factor-0 entries: the slot would give nothing there.
            foreach (var b in bands)
                if (!Enumerable.Range(0, n).Any(i => b.Mix[i] != null && wanted[i]))
                    for (int i = 0; i < n; i++) if (b.Mix[i] != null) wanted[i] = true;
            var result = new double[n];
            var live = Enumerable.Range(0, n).Where(i => wanted[i]).ToArray();
            if (live.Length == 0) return Enumerable.Repeat(1.0, n).ToArray();

            // Group the live entries by what they lead to at every level.
            var groupOf = new int[n];
            var keys = new Dictionary<string, int>(StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder();
            foreach (int i in live)
            {
                sb.Clear();
                foreach (var b in bands)
                {
                    var m = b.Mix[i];
                    if (m == null) sb.Append('-');
                    else foreach (double v in m) sb.Append(v.ToString("R", CultureInfo.InvariantCulture)).Append(',');
                    sb.Append('|');
                }
                string key = sb.ToString();
                if (!keys.TryGetValue(key, out int gi)) keys[key] = gi = keys.Count;
                groupOf[i] = gi;
            }
            int G = keys.Count;
            if (G == 1)
            {
                foreach (int i in live) result[i] = 1.0;
                return result;
            }
            var gw = new double[nb][];   // vanilla weight of each group in each band (0 = absent)
            var gm = new double[nb][][]; // what each group leads to in each band
            for (int k = 0; k < nb; k++)
            {
                gw[k] = new double[G];
                gm[k] = new double[G][];
                foreach (int i in live)
                {
                    if (bands[k].Mix[i] == null) continue;
                    gw[k][groupOf[i]] += w[i];
                    gm[k][groupOf[i]] = bands[k].Mix[i];
                }
            }

            var bw = new double[nb]; // residual weight of a band: sqrt(share of levels)
            for (int k = 0; k < nb; k++) bw[k] = Math.Sqrt(bands[k].Levels.Count / levelsTotal);

            // Vanilla share of each group in each band.
            var p0 = new double[nb][];
            for (int k = 0; k < nb; k++)
            {
                double s0 = gw[k].Sum();
                p0[k] = gw[k].Select(x => s0 > 0 ? x / s0 : 0).ToArray();
            }

            // Per band: share error and wrong-way move per tier, then the same per entry and tier;
            // then the pull.
            int perBand = 2 * Tiers + 2 * G * Tiers;
            int R = nb * perBand + G;
            var res = new double[R];
            var jr = new double[R, G];
            var p = new double[G];
            var q = new double[Tiers];

            // Residuals (and their Jacobian) for log-factors th; returns the squared error.
            double Eval(double[] th, bool jacobian)
            {
                double e = 0;
                Array.Clear(res, 0, R);
                if (jacobian) Array.Clear(jr, 0, jr.Length);
                for (int k = 0; k < nb; k++)
                {
                    var b = bands[k];
                    int r0 = k * perBand;
                    double mx = double.NegativeInfinity;
                    for (int c = 0; c < G; c++)
                        if (gw[k][c] > 0) mx = Math.Max(mx, Math.Log(gw[k][c]) + th[c]);
                    if (double.IsNegativeInfinity(mx)) continue;
                    double tot = 0;
                    for (int c = 0; c < G; c++)
                        tot += p[c] = gw[k][c] > 0 ? Math.Exp(Math.Log(gw[k][c]) + th[c] - mx) : 0;
                    Array.Clear(q, 0, Tiers);
                    for (int c = 0; c < G; c++)
                    {
                        p[c] /= tot;
                        if (p[c] > 0) for (int t = 0; t < Tiers; t++) q[t] += p[c] * gm[k][c][t];
                    }
                    // Common gear only takes what's left, so it isn't scored itself.
                    for (int t = 1; t < Tiers; t++)
                    {
                        double vt = b.Vanilla[t], tt = b.Target[t], qt = q[t];
                        if (qt <= 0) continue; // nothing here brings this tier
                        // Errors are log ratios: twice too much costs the same as half too little.
                        double r1, d1; // residual and d residual / d q
                        if (tt > 0) { r1 = Math.Log(qt / tt); d1 = 1 / qt; }
                        else if (vt > 0) { r1 = qt / vt; d1 = 1 / vt; }
                        else continue;
                        // Outside [vanilla, target]: moved the wrong way, or past the target.
                        double lo = Math.Min(vt, tt);
                        double hi = t == NonGear ? double.PositiveInfinity : Math.Max(vt, tt);
                        double r2 = 0, d2 = 0;
                        if (lo > 0 && qt < lo) { r2 = Math.Log(lo / qt); d2 = -1 / qt; }
                        else if (hi > 0 && qt > hi) { r2 = Math.Log(qt / hi); d2 = 1 / qt; }
                        res[r0 + t] = bw[k] * r1;
                        res[r0 + Tiers + t] = bw[k] * WrongWay * r2;
                        if (jacobian)
                            for (int c = 0; c < G; c++)
                            {
                                if (p[c] == 0) continue;
                                // d q[t] / d th[c] = p_c (mix_c[t] - q[t])
                                double dq = p[c] * (gm[k][c][t] - qt);
                                jr[r0 + t, c] = bw[k] * d1 * dq;
                                if (r2 > 0) jr[r0 + Tiers + t, c] = bw[k] * WrongWay * d2 * dq;
                            }
                        e += res[r0 + t] * res[r0 + t] + res[r0 + Tiers + t] * res[r0 + Tiers + t];
                    }
                    // Every entry on its own: what it brings of a tier should change by that tier's
                    // factor (target / vanilla), so the items inside a tier keep their vanilla
                    // proportions (one boosted Legendary must not crowd out another). Weighted by
                    // how much of the tier the entry brings; falling below vanilla when the tier is
                    // boosted (or past the factor) is a wrong-way move, whatever its size.
                    for (int g = 0; g < G; g++)
                    {
                        if (p0[k][g] <= 0 || p[g] <= 0) continue;
                        double lx = Math.Log(p[g] / p0[k][g]); // log of this entry's own change
                        for (int t = 1; t < Tiers; t++)
                        {
                            double m = gm[k][g][t], vt = b.Vanilla[t], tt = b.Target[t];
                            if (m <= 0 || vt <= 0 || tt <= 0) continue;
                            double lr = Math.Log(tt / vt);
                            int ri = r0 + 2 * Tiers + 2 * (g * Tiers + t);
                            double wgt = Math.Sqrt(p0[k][g] * m / vt);
                            res[ri] = bw[k] * wgt * (lx - lr);
                            double lo = Math.Min(0, lr), hi = t == NonGear ? double.PositiveInfinity : Math.Max(0, lr);
                            double r2 = 0, sg = 0;
                            if (lx < lo) { r2 = lo - lx; sg = -1; }
                            else if (lx > hi) { r2 = lx - hi; sg = 1; }
                            res[ri + 1] = bw[k] * WrongWay * r2;
                            if (jacobian)
                                for (int c = 0; c < G; c++)
                                {
                                    if (p[c] <= 0) continue;
                                    double dl = (c == g ? 1 : 0) - p[c]; // d log p_g / d th_c
                                    jr[ri, c] = bw[k] * wgt * dl;
                                    if (r2 > 0) jr[ri + 1, c] = bw[k] * WrongWay * sg * dl;
                                }
                            e += res[ri] * res[ri] + res[ri + 1] * res[ri + 1];
                        }
                    }
                }
                for (int c = 0; c < G; c++)
                {
                    res[nb * perBand + c] = Pull * th[c];
                    if (jacobian) jr[nb * perBand + c, c] = Pull;
                    e += res[nb * perBand + c] * res[nb * perBand + c];
                }
                return e;
            }

            var theta = new double[G];
            double err = Eval(theta, true), mu = 1e-3;
            var A = new double[G, G];
            var rhs = new double[G];
            for (int iter = 0; iter < 200; iter++)
            {
                // Normal equations (Jr'Jr + mu) d = -Jr' res
                Array.Clear(A, 0, A.Length);
                Array.Clear(rhs, 0, G);
                for (int k = 0; k < R; k++)
                {
                    double rk = res[k];
                    for (int a = 0; a < G; a++)
                    {
                        double ja = jr[k, a];
                        if (ja == 0) continue;
                        rhs[a] -= ja * rk;
                        for (int c = 0; c < G; c++) A[a, c] += ja * jr[k, c];
                    }
                }
                double scale = 0;
                for (int a = 0; a < G; a++) scale = Math.Max(scale, A[a, a]);

                bool improved = false;
                double before = err;
                for (int tries = 0; tries < 40 && !improved; tries++)
                {
                    var M = (double[,])A.Clone();
                    for (int a = 0; a < G; a++) M[a, a] += mu * scale;
                    var d = SolveLinear(M, (double[])rhs.Clone());
                    if (d == null) { mu *= 4; continue; }
                    var next = new double[G];
                    for (int c = 0; c < G; c++)
                        next[c] = Math.Max(-LogLimit, Math.Min(LogLimit, theta[c] + d[c]));
                    double e = Eval(next, false);
                    if (e < err)
                    {
                        theta = next;
                        err = Eval(theta, true);
                        mu = Math.Max(1e-12, mu / 3);
                        improved = true;
                    }
                    else
                        mu *= 4;
                }
                if (!improved || before - err < 1e-12 * before) break;
            }

            // Factors relative to the vanilla weights, largest = 1.
            double best = theta.Max();
            foreach (int i in live) result[i] = Math.Exp(theta[groupOf[i]] - best);
            return result;
        }

        /// <summary>Solve M x = b by Gaussian elimination with partial pivoting; null if singular.</summary>
        static double[] SolveLinear(double[,] M, double[] b)
        {
            int n = b.Length;
            for (int c = 0; c < n; c++)
            {
                int piv = c;
                for (int r = c + 1; r < n; r++) if (Math.Abs(M[r, c]) > Math.Abs(M[piv, c])) piv = r;
                if (Math.Abs(M[piv, c]) < 1e-300) return null;
                if (piv != c)
                {
                    for (int k = 0; k < n; k++) { var tmp = M[c, k]; M[c, k] = M[piv, k]; M[piv, k] = tmp; }
                    var tb = b[c]; b[c] = b[piv]; b[piv] = tb;
                }
                for (int r = c + 1; r < n; r++)
                {
                    double f = M[r, c] / M[c, c];
                    if (f == 0) continue;
                    for (int k = c; k < n; k++) M[r, k] -= f * M[c, k];
                    b[r] -= f * b[c];
                }
            }
            var x = new double[n];
            for (int r = n - 1; r >= 0; r--)
            {
                double sum = b[r];
                for (int k = r + 1; k < n; k++) sum -= M[r, k] * x[k];
                x[r] = sum / M[r, r];
            }
            return x;
        }

        /// <summary>Multiply the chance of each Epic/Legendary/MI item by its multiplier.
        /// The number of drops stays the same; common items give up the share (see TierTargets).</summary>
        static void ApplyItemRarity(Ctx c)
        {
            var s = c.S;
            if (Near1(s.MiMultiplier) && Near1(s.EpicMultiplier) && Near1(s.LegendaryMultiplier)) return;
            var plan = new RarityPlan(c.Db, s);
            int tables = 0, droppers = 0, chests = 0;
            foreach (var kv in c.Db)
            {
                string name = kv.Key;
                var rec = kv.Value;
                var groups = WeightGroups(rec);
                if (groups.Count == 0) continue;
                var edits = new List<KeyValuePair<string, List<double>>>();
                foreach (var g in groups)
                {
                    var factors = plan.GroupFactors(name, rec, g.Key, g.Value)
                        .Where(x => rec[x.Key] != null).ToList();
                    var pos = factors.Select(x => x.Value).Where(v => v > 0).ToList();
                    // Weights are relative: scaling a whole group by one factor changes nothing.
                    // (A factor of 0 does change it: that entry's share goes to the others.)
                    if (pos.Count == 0) continue;
                    bool anyZero = factors.Any(x => x.Value <= 0 && rec[x.Key].Nums.Any(v => v > 0));
                    if (!anyZero && pos.Max() - pos.Min() < 1e-6 * pos.Max()) continue;
                    // Rescale so no weight shrinks (keeps precision for small integer weights).
                    double k = 1.0 / pos.Min();
                    double peak = factors.Max(x => (rec[x.Key].Nums.Count > 0 ? rec[x.Key].Nums.Max() : 0) * x.Value * k);
                    if (peak > 1e9) k *= 1e9 / peak;
                    foreach (var x in factors)
                    {
                        var f = rec[x.Key];
                        var nv = ScaledValues(f, x.Value * k);
                        if (!nv.SequenceEqual(f.Nums)) edits.Add(new KeyValuePair<string, List<double>>(x.Key, nv));
                    }
                }
                if (edits.Count == 0) continue;
                var copy = c.Changed(name);
                foreach (var e in edits) copy.SetNums(e.Key, e.Value, FieldType.Float);
                string cls = rec.Str("Class");
                if (cls != null && WeightedTableClasses.Contains(cls)) tables++;
                else if (cls != null && DropperClasses.Contains(cls)) droppers++;
                else chests++;
            }
            c.Stat("loot tables reweighted", tables);
            c.Stat("monsters with reweighted loot slots", droppers);
            c.Stat("chest loot tables reweighted", chests);
        }

        static readonly string[] AffixWeightsRare = { "rarePrefixOnly", "rareSuffixOnly", "normalPrefixRareSuffix",
                                                      "rarePrefixNormalSuffix", "rareBothPrefixSuffix" };
        static readonly string[] AffixWeightsDouble = { "bothPrefixSuffix", "normalPrefixRareSuffix",
                                                        "rarePrefixNormalSuffix", "rareBothPrefixSuffix" };
        static readonly string[] AffixWeightsPlain = { "brokenOnly", "noPrefixNoSuffix" };

        static void ApplyAffixQuality(Ctx c)
        {
            var s = c.S;
            if (Near1(s.RareAffixMultiplier) && Near1(s.DoubleAffixMultiplier) && Near1(s.PlainItemMultiplier)) return;

            // Same factor table for every record.
            var order = new List<string>();
            var factors = new Dictionary<string, double>(StringComparer.Ordinal);
            void Mul(string k, double m)
            {
                if (!factors.ContainsKey(k)) { order.Add(k); factors[k] = 1.0; }
                factors[k] *= m;
            }
            foreach (var k in AffixWeightsRare) Mul(k, s.RareAffixMultiplier);
            foreach (var k in AffixWeightsDouble) Mul(k, s.DoubleAffixMultiplier);
            foreach (var k in AffixWeightsPlain)
            {
                if (!factors.ContainsKey(k)) order.Add(k);
                factors[k] = s.PlainItemMultiplier;
            }

            int n = 0;
            foreach (var kv in c.Db)
            {
                var rec = kv.Value;
                if (rec.Str("Class") != "LootItemTable_DynWeight") continue;
                // Only weights that are non-zero matter; a table whose only outcome is a single
                // category is unaffected by scaling, so skip it to keep the mod small.
                var active = order.Where(k => rec.Num(k, 0) > 0).ToList();
                if (active.Count < 2) continue;
                var nv = new List<KeyValuePair<string, double>>();
                foreach (var k in active)
                {
                    var f = rec[k];
                    double v = f.Nums[0] * factors[k];
                    if (f.Type == FieldType.Int)
                    {
                        v = Math.Round(v);
                        if (v == 0 && factors[k] > 0) v = 1;
                    }
                    nv.Add(new KeyValuePair<string, double>(k, v));
                }
                // Never let every outcome reach zero (e.g. plain multiplier 0 on a plain-only table).
                if (nv.All(x => x.Value <= 0)) continue;
                var copy = c.Changed(kv.Key);
                foreach (var x in nv) copy.SetNums(x.Key, new[] { x.Value }, FieldType.Float);
                n++;
            }
            c.Stat("affix weight tables", n);
        }

        // ----------------------------------------------------------- Affix stat rolls
        // Fields on an affix that are NOT stat magnitudes (or that we deliberately keep fixed).
        static readonly Regex RollSkip = Re(
            @"(Chance$|Duration|^levelRequirement$|^lootRandomizer|^marketAdjustmentPercent$" +
            @"|^conversionPercentage|^augment|^itemSkill|^racialBonusRace)");

        /// <summary>(scale, new jitter %) for an affix.
        ///
        /// Vanilla roll range is value * (1 +- J). We want the new range to start at minPos and end
        /// at maxPos (percent 0..100) of that vanilla range. A centre scale `s` and a new jitter J'
        /// satisfy: value*s*(1-J') = new_min and value*s*(1+J') = new_max.</summary>
        public static (double Scale, double NewJitter) RollTransform(double jitterPct, double minPos, double maxPos)
        {
            double j = jitterPct / 100.0;
            double a = minPos / 100.0, b = maxPos / 100.0;
            double scale = 1.0 + j * (a + b - 1.0);
            double newJ = scale != 0 ? j * (b - a) / scale : 0.0;
            return (scale, newJ * 100.0);
        }

        static void ApplyRolls(Ctx c)
        {
            var s = c.S;
            if (s.RollMinPercent <= 0 && s.RollMaxPercent >= 100) return;
            int n = 0;
            var skip = new Dictionary<string, bool>(StringComparer.Ordinal); // few distinct keys, millions of fields
            foreach (var kv in c.Db)
            {
                var rec = kv.Value;
                if (rec.Str("Class") != "LootRandomizer") continue;
                double jit = rec.Num("lootRandomizerJitter", 0);
                if (jit <= 0) continue;
                var (scale, newJ) = RollTransform(jit, s.RollMinPercent, s.RollMaxPercent);
                var copy = c.Changed(kv.Key);
                for (int i = 0; i < copy.Count; i++)
                {
                    var f = copy.FieldAt(i);
                    if (f.Type != FieldType.Float) continue;
                    string key = copy.KeyAt(i);
                    if (!skip.TryGetValue(key, out bool sk)) skip[key] = sk = RollSkip.IsMatch(key);
                    if (!sk && f.Nums.Any(v => v != 0))
                        copy.SetNumsAt(i, f.Nums.Select(v => v * scale));
                }
                copy.SetNums("lootRandomizerJitter", new[] { newJ }, FieldType.Float);
                n++;
            }
            c.Stat("affixes re-ranged", n);
        }

        // ---------------------------------------------------------------- Drop amount
        static readonly string[] ChestCountEqs = { "numSpawnMinEquation", "numSpawnMaxEquation" };

        /// <summary>Check(name) is true if the record can produce an item a player can pick up
        /// (false for cosmetic monster gear such as zombie rags, which has cannotPickUp=1).</summary>
        sealed class Droppable
        {
            readonly Db db;
            readonly Dictionary<string, bool> memo = new Dictionary<string, bool>(StringComparer.Ordinal);

            public Droppable(Db db) => this.db = db;

            public bool Check(string name, int depth = 0)
            {
                if (memo.TryGetValue(name, out bool cached)) return cached;
                var rec = db.Get(name);
                if (rec == null || depth > 25) return false;
                memo[name] = false; // cycle guard
                string cls = rec.Str("Class");
                bool res;
                if (cls == "LevelTable")
                    res = rec.AllStr("records").Where(r => r != "").Any(r => Check(Norm(r), depth + 1));
                else if (cls != null && WeightedTableClasses.Contains(cls))
                {
                    var entries = WeightGroups(rec).FirstOrDefault(g => g.Key == "").Value ?? new List<(string, string)>();
                    res = entries.Where(e => rec.Num(e.Weight, 0) > 0).Any(e => Check(Norm(rec.Str(e.Name)), depth + 1));
                }
                else
                    res = !rec.Truthy("cannotPickUp");
                memo[name] = res;
                return res;
            }
        }

        /// <summary>More drops, like GDModdingTool's AdjustLootAmount: monster slot chances x (capped at
        /// 100%) and chest/breakable item counts x. What a slot drops is unchanged, so every item - Epics
        /// and Legendaries included - drops proportionally more often. Guaranteed (100%) slots can't rise.</summary>
        static void ApplyLootAmount(Ctx c)
        {
            double m = c.S.DropAmountMultiplier;
            if (Near1(m)) return;
            var droppable = new Droppable(c.Db);
            int slots = 0, capped = 0, monsters = 0, chests = 0;
            foreach (var kv in c.Db)
            {
                var rec = kv.Value;
                string cls = rec.Str("Class");
                if (cls != null && DropperClasses.Contains(cls))
                {
                    if (rec["dropItems"]?.Count > 0 && !rec.Truthy("dropItems")) continue; // dropItems = 0
                    var edits = new List<KeyValuePair<string, List<double>>>();
                    foreach (var g in WeightGroups(rec))
                    {
                        string key = "chanceToEquip" + g.Key;
                        var f = rec[key];
                        if (f == null || f.IsString || f.Nums.Count == 0 || f.Nums[0] <= 0) continue;
                        if (!g.Value.Where(e => rec.Num(e.Weight, 0) > 0).Any(e => droppable.Check(Norm(rec.Str(e.Name)))))
                            continue; // cosmetic-only slot: more of it just changes how monsters look
                        if (m > 1 && f.Nums[0] >= 100)
                        {
                            capped++;
                            continue;
                        }
                        edits.Add(Edit(key, f.Nums.Select(v => Math.Min(100.0, v * m))));
                    }
                    if (edits.Count == 0) continue;
                    var copy = c.Changed(kv.Key);
                    foreach (var e in edits) copy.SetNums(e.Key, e.Value, FieldType.Float);
                    slots += edits.Count;
                    monsters++;
                }
                else if ((rec.Str("templateName") ?? "").ToLowerInvariant() == ChestTemplate)
                {
                    var eqs = ChestCountEqs.Where(k => rec.Truthy(k)).ToList();
                    if (eqs.Count == 0) continue;
                    var copy = c.Changed(kv.Key);
                    foreach (var k in eqs) copy.SetStr(k, Times(rec.Str(k), m));
                    chests++;
                }
            }
            c.Stat("monsters with more drops", monsters);
            c.Stat("monster loot slots raised", slots);
            c.Stat("guaranteed slots left at 100%", capped);
            c.Stat("chests/breakables with more items", chests);
        }

        static readonly string[] GoldEqs = { "goldAmountEquation", "goldAmountEquation2" };
        static readonly string[] GoldLimits = { "goldValueMin", "goldValueMax" };

        /// <summary>Gold per drop x: all gold comes from a handful of shared gold-generator records.</summary>
        static void ApplyGold(Ctx c)
        {
            double m = c.S.GoldMultiplier;
            if (Near1(m)) return;
            int n = 0;
            foreach (var kv in c.Db)
            {
                var rec = kv.Value;
                if (!GoldEqs.Any(rec.Truthy)) continue;
                var copy = c.Changed(kv.Key);
                foreach (var k in GoldEqs)
                    if (rec.Truthy(k)) copy.SetStr(k, Times(rec.Str(k), m));
                // Scale the clamps too, or big piles would silently stop growing.
                foreach (var k in GoldLimits)
                {
                    var f = copy[k];
                    if (f != null && !f.IsString && f.Nums.Count > 0)
                        copy.SetNums(k, f.Nums.Select(v => f.Type == FieldType.Int ? Math.Round(v * m) : v * m), f.Type);
                }
                n++;
            }
            c.Stat("gold generators", n);
        }

        // -------------------------------------------------------------- Inventory bags
        const string BagItem = "records/items/misc/inventorybag.dbr";
        const string BagVendorTier = "records/creatures/npcs/merchants/factiontables/devilscrossing_friendly_01.dbr";
        const string KerrickMarket = "records/creatures/npcs/merchants/merchanttbl_a01.dbr";
        const double KerrickBags = 5;  // per stock roll; regular merchants have no unlimited stock

        /// <summary>Sell the (otherwise quest-only) inventory bag for 0 gold:
        /// - at Kerrick, the first Devil's Crossing merchant, from the start. His stock is rolled per
        ///   category (table + min/max count); his Potion category is unused (0-0, no table), so it
        ///   gets the bag itself with a count of 5-5 (bought bags come back when his stock rerolls).
        ///   Vanilla sells a quest item the same way (Benevald, merchanttbl_b01b's Chest category).
        /// - at the Devil's Crossing faction vendor once Friendly, as a fallback: faction tiers have a
        ///   fixed always-in-stock list (marketStaticItems).</summary>
        static void ApplyBags(Ctx c)
        {
            if (!c.S.FreeInventoryBags || !c.Db.Contains(BagItem)) return;
            var bag = c.Changed(BagItem);
            bag.SetNums("itemCost", new[] { 0.0 }, FieldType.Int);
            var market = c.Db.Get(KerrickMarket);
            if (market != null && !market.Truthy("marketPotionTable") && market.Num("marketNumPotionMax", 0) <= 0)
            {
                var m = c.Changed(KerrickMarket);
                m.SetStr("marketPotionTable", BagItem);
                m.SetNums("marketNumPotionMin", new[] { KerrickBags }, FieldType.Int);
                m.SetNums("marketNumPotionMax", new[] { KerrickBags }, FieldType.Int);
                c.Stat("inventory bag at Kerrick", 1);
            }
            if (c.Db.Contains(BagVendorTier))
            {
                var tier = c.Changed(BagVendorTier);
                var items = tier.AllStr("marketStaticItems").Where(v => v != "").ToList();
                if (!items.Select(Norm).Contains(BagItem))
                    tier.SetStrs("marketStaticItems", items.Concat(new[] { BagItem }));
                c.Stat("inventory bag at Devil's Crossing faction vendor", 1);
            }
        }

        public static (Dictionary<string, Record> Records, List<KeyValuePair<string, long>> Stats)
            BuildRecords(Db db, Settings s, Action<string> log)
        {
            var c = new Ctx { Db = db, S = s };
            var steps = new (string, Action<Ctx>)[]
            {
                ("XP", ApplyXp), ("factions", ApplyFactions), ("item rarity", ApplyItemRarity),
                ("affix quality", ApplyAffixQuality), ("stat rolls", ApplyRolls), ("drop amount", ApplyLootAmount),
                ("gold", ApplyGold), ("inventory bags", ApplyBags),
            };
            foreach (var (step, fn) in steps)
            {
                log($"  applying {step} ...");
                fn(c);
            }
            return (c.Out, c.Stats);
        }
    }
}
