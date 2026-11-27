using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace EasierProgression
{
    /// <summary>Build / remove the mod in &lt;Grim Dawn&gt;/mods/&lt;ModName&gt;/.</summary>
    public static class Builder
    {
        static string cacheKey;
        static Db cacheDb;
        static readonly object cacheLock = new object();

        public static string ModDir(Settings s) => Path.Combine(s.GameDir, "mods", s.ModName);

        static Db LoadDbCached(string gameDir, Action<string> log)
        {
            var stamp = GameData.Databases.Select(p => Path.Combine(gameDir, p)).Where(File.Exists)
                .Select(p => File.GetLastWriteTimeUtc(p).Ticks.ToString());
            string key = Path.GetFullPath(gameDir).ToLowerInvariant() + "|" + string.Join("|", stamp);
            lock (cacheLock)
            {
                if (key == cacheKey)
                {
                    log("  using already-loaded game database");
                    return cacheDb;
                }
                cacheDb = null; // let the old copy be collected before loading the new one
                cacheDb = GameData.LoadDatabase(gameDir, log);
                cacheKey = key;
                return cacheDb;
            }
        }

        /// <summary>Build the mod. With `outFile`, only write the .arz there (for testing).</summary>
        public static string Build(Settings s, Action<string> log, string outFile = null)
        {
            var errs = s.Validate();
            if (!GameData.IsGameDir(s.GameDir)) errs.Add($"Not a Grim Dawn folder: '{s.GameDir}'");
            if (errs.Count > 0) throw new ArgumentException(string.Join("\n", errs));

            var sw = Stopwatch.StartNew();
            log("Loading game database ...");
            var db = LoadDbCached(s.GameDir, log);
            log("Building modified records ...");
            var (records, stats) = Transforms.BuildRecords(db, s, log);
            foreach (var kv in stats) log($"    {kv.Key}: {kv.Value:N0}");
            if (records.Count == 0) log("All settings are vanilla - nothing to write.");

            string arzPath = outFile;
            if (arzPath == null)
            {
                string outDir = Path.Combine(ModDir(s), "database");
                Directory.CreateDirectory(outDir);
                arzPath = Path.Combine(outDir, s.ModName + ".arz");
            }
            string tmp = arzPath + ".tmp";
            var sorted = records.Values.OrderBy(r => r.Name.ToLowerInvariant(), StringComparer.Ordinal);
            try
            {
                Arz.Write(tmp, sorted);
                if (File.Exists(arzPath)) File.Replace(tmp, arzPath, null);
                else File.Move(tmp, arzPath);
            }
            catch
            {
                try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }

            if (outFile == null)
            {
                var info = new List<KeyValuePair<string, object>>
                {
                    new KeyValuePair<string, object>("built", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
                    new KeyValuePair<string, object>("stats",
                        stats.Select(kv => new KeyValuePair<string, object>(kv.Key, kv.Value)).ToList()),
                    new KeyValuePair<string, object>("settings", s.ToPairs()),
                };
                File.WriteAllText(Path.Combine(ModDir(s), "settings_used.json"), Json.Write(info),
                                  new UTF8Encoding(false));
            }
            log($"Wrote {records.Count:N0} records to {arzPath} ({sw.Elapsed.TotalSeconds:F1}s)");
            return arzPath;
        }

        public static bool Remove(Settings s, Action<string> log)
        {
            string d = ModDir(s);
            if (!Directory.Exists(d))
            {
                log("Mod folder does not exist - nothing to remove.");
                return false;
            }
            // Safety: only delete folders this tool created.
            if (!File.Exists(Path.Combine(d, "settings_used.json")))
                throw new InvalidOperationException($"{d} was not created by this tool; refusing to delete it.");
            Directory.Delete(d, true);
            log($"Removed {d}");
            return true;
        }
    }
}
