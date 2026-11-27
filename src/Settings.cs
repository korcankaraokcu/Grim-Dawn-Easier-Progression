using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace EasierProgression
{
    /// <summary>Mod settings. Property names map to snake_case keys in settings.json
    /// (XpMultiplier -> xp_multiplier).</summary>
    public sealed class Settings
    {
        public const string DefaultModName = "EasierProgression";

        // --- Experience
        public double XpMultiplier { get; set; } = 2.0;            // monster-kill XP x

        // --- Faction reputation (per kill)
        public double FactionGainMultiplier { get; set; } = 3.0;   // rep gained with friendly factions per kill x
        public double FactionHostileMultiplier { get; set; } = 1.0; // rep lost with hostile factions per kill x (nemesis!)
        public bool FactionNoDiminishing { get; set; } = false;    // disable the 0.5x / 0.33x reductions at higher standing
        public bool FreeInventoryBags { get; set; } = true;        // Kerrick + Devil's Crossing faction vendor sell bags for 0 gold

        // --- Drop quality: which item gets picked when something drops
        public double MiMultiplier { get; set; } = 1.5;            // Monster Infrequents (green named base items) x
        public double EpicMultiplier { get; set; } = 2.0;          // Epic (blue) x
        public double LegendaryMultiplier { get; set; } = 2.0;     // Legendary + Mythical (purple) x

        // --- Drop amount
        public double DropAmountMultiplier { get; set; } = 2.0;    // whole number: monster slot chances (cap 100%) and chest item counts x
        public double GoldMultiplier { get; set; } = 2.0;          // gold per pile x

        // --- Drop quality: affixes on random-affix items
        public double RareAffixMultiplier { get; set; } = 2.0;     // weight of rolls containing a rare (green) affix x
        public double DoubleAffixMultiplier { get; set; } = 1.5;   // extra x for prefix+suffix rolls
        public double PlainItemMultiplier { get; set; } = 0.5;     // broken / no-affix rolls x

        // --- Affix stat rolls: positions inside the vanilla roll range (0 = vanilla min, 100 = vanilla max)
        public double RollMinPercent { get; set; } = 50.0;
        public double RollMaxPercent { get; set; } = 100.0;

        // --- UI state: the preset the values were last loaded from ("" = none)
        public string Preset { get; set; } = "";

        // --- Output
        public string GameDir { get; set; } = "";
        public string ModName { get; set; } = DefaultModName;

        // ------------------------------------------------------------------ key access
        static readonly PropertyInfo[] Props = typeof(Settings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0) // not the this[key] indexer
            .OrderBy(p => p.MetadataToken) // declaration order
            .ToArray();

        static readonly Dictionary<string, PropertyInfo> ByKey = Props.ToDictionary(p => KeyOf(p.Name));

        static string KeyOf(string pascal) => Regex.Replace(pascal, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();

        public static IEnumerable<string> Keys => Props.Select(p => KeyOf(p.Name));

        public object this[string key]
        {
            get => ByKey[key].GetValue(this);
            set
            {
                var p = ByKey[key];
                p.SetValue(this, p.PropertyType == typeof(double) ? Convert.ToDouble(value) : value);
            }
        }

        public List<KeyValuePair<string, object>> ToPairs() =>
            Keys.Select(k => new KeyValuePair<string, object>(k, this[k])).ToList();

        /// <summary>Known keys with a value of the right type are taken; anything else keeps its default.</summary>
        public static Settings FromPairs(IEnumerable<KeyValuePair<string, object>> pairs)
        {
            var s = new Settings();
            foreach (var kv in pairs)
            {
                if (!ByKey.TryGetValue(kv.Key, out var p)) continue;
                var t = p.PropertyType;
                if ((t == typeof(double) && kv.Value is double) || (t == typeof(bool) && kv.Value is bool)
                    || (t == typeof(string) && kv.Value is string))
                    p.SetValue(s, kv.Value);
            }
            return s;
        }

        public List<string> Validate()
        {
            var errs = new List<string>();
            foreach (var k in new[] { "xp_multiplier", "faction_gain_multiplier", "faction_hostile_multiplier",
                                      "mi_multiplier", "epic_multiplier", "legendary_multiplier",
                                      "rare_affix_multiplier", "double_affix_multiplier",
                                      "drop_amount_multiplier", "gold_multiplier" })
            {
                double v = (double)this[k];
                if (!(v > 0) || double.IsInfinity(v)) errs.Add($"{k} must be > 0");
            }
            if (!new[] { 1.0, 2, 3, 4, 5 }.Contains(DropAmountMultiplier))
                errs.Add("drop_amount_multiplier must be a whole number from 1 to 5");
            if (!(PlainItemMultiplier >= 0))
                errs.Add("plain_item_multiplier must be >= 0");
            if (!(RollMinPercent >= 0 && RollMinPercent <= 100 && RollMaxPercent >= 0 && RollMaxPercent <= 100))
                errs.Add("roll percentages must be within 0..100");
            if (RollMinPercent > RollMaxPercent)
                errs.Add("roll_min_percent must be <= roll_max_percent");
            if (ModName == null || !Regex.IsMatch(ModName, "^[A-Za-z_][A-Za-z0-9_]*$"))
                errs.Add("mod_name must be letters/digits/underscore only");
            return errs;
        }

        // ------------------------------------------------------------------ presets
        static Dictionary<string, object> P(double xp, double gain, bool noDim, bool bags, double mi, double epic,
            double leg, double drop, double gold, double rare, double dbl, double plain, double rollMin) =>
            new Dictionary<string, object>
            {
                ["xp_multiplier"] = xp, ["faction_gain_multiplier"] = gain, ["faction_hostile_multiplier"] = 1.0,
                ["faction_no_diminishing"] = noDim, ["free_inventory_bags"] = bags,
                ["mi_multiplier"] = mi, ["epic_multiplier"] = epic, ["legendary_multiplier"] = leg,
                ["drop_amount_multiplier"] = drop, ["gold_multiplier"] = gold,
                ["rare_affix_multiplier"] = rare, ["double_affix_multiplier"] = dbl, ["plain_item_multiplier"] = plain,
                ["roll_min_percent"] = rollMin, ["roll_max_percent"] = 100.0,
            };

        /// <summary>Preset name -> values, in display order.</summary>
        public static readonly List<KeyValuePair<string, Dictionary<string, object>>> Presets =
            new List<KeyValuePair<string, Dictionary<string, object>>>
            {
                new KeyValuePair<string, Dictionary<string, object>>("Vanilla (no changes)",
                    P(1.0, 1.0, false, false, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.0)),
                new KeyValuePair<string, Dictionary<string, object>>("Light",
                    P(1.5, 2.0, false, true, 1.25, 1.5, 1.5, 2.0, 1.5, 1.5, 1.25, 0.75, 25.0)),
                new KeyValuePair<string, Dictionary<string, object>>("Medium",
                    P(2.0, 3.0, false, true, 1.5, 2.0, 2.0, 2.0, 2.0, 2.0, 1.5, 0.5, 50.0)),
                new KeyValuePair<string, Dictionary<string, object>>("Generous",
                    P(3.0, 5.0, true, true, 2.0, 3.0, 4.0, 3.0, 3.0, 3.0, 2.0, 0.25, 75.0)),
            };

        public static Dictionary<string, object> PresetValues(string name) =>
            Presets.FirstOrDefault(p => p.Key == name).Value;

        public static bool IsPreset(string name) => PresetValues(name) != null;

        /// <summary>True if every value the preset defines equals `values` (numbers within 1e-6).</summary>
        public static bool MatchesPreset(IDictionary<string, object> values, string name)
        {
            foreach (var kv in PresetValues(name))
            {
                values.TryGetValue(kv.Key, out var cur);
                if (kv.Value is bool b)
                {
                    if (!(cur is bool cb) || cb != b) return false;
                }
                else if (!(cur is double d) || Math.Abs(d - (double)kv.Value) > 1e-6) return false;
            }
            return true;
        }

        /// <summary>Name of the first preset that equals the values exactly, or "".</summary>
        public static string ExactPreset(IDictionary<string, object> values) =>
            Presets.Select(p => p.Key).FirstOrDefault(n => MatchesPreset(values, n)) ?? "";

        /// <summary>(preset shown in the selector, indicator text next to it).
        /// `basePreset` = preset the values were last loaded from. An exact match with any preset wins,
        /// so tweaking values back to a preset shows it without an indicator.</summary>
        public static (string Shown, string Note) PresetStatus(IDictionary<string, object> values, string basePreset)
        {
            if (IsPreset(basePreset) && MatchesPreset(values, basePreset)) return (basePreset, "");
            string exact = ExactPreset(values);
            if (exact != "") return (exact, "");
            if (IsPreset(basePreset)) return (basePreset, "(modified)");
            return ("", "Custom values");
        }

        // ------------------------------------------------------------------ files
        public static Settings Load(string path)
        {
            try
            {
                if (Json.Parse(File.ReadAllText(path, Encoding.UTF8)) is List<KeyValuePair<string, object>> obj)
                    return FromPairs(obj);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (FormatException) { } // corrupt or hand-edited file: fall back to defaults
            catch (OverflowException) { }
            return new Settings();
        }

        public void Save(string path) => File.WriteAllText(path, Json.Write(ToPairs()), new UTF8Encoding(false));
    }
}
