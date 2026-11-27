using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace EasierProgression
{
    /// <summary>Locate a Grim Dawn install and load its (merged) database.</summary>
    public static class GameData
    {
        public const string GameExe = "Grim Dawn.exe";

        public static readonly string[] Databases =
        {
            Path.Combine("database", "database.arz"),
            Path.Combine("gdx1", "database", "GDX1.arz"), // Ashes of Malmouth
            Path.Combine("gdx2", "database", "GDX2.arz"), // Forgotten Gods
            Path.Combine("gdx3", "database", "GDX3.arz"), // Fangs of Asterkarn
        };

        // Only these parts of the database are ever touched/read by the builder.
        static readonly string[] RelevantPrefixes =
        {
            "records/game/",
            "records/items/",
            "records/creatures/",
            "records/controllers/factions/",
            "records/endlessdungeon/",
        };

        public static bool IsGameDir(string path) =>
            !string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, GameExe))
            && File.Exists(Path.Combine(path, Databases[0]));

        /// <summary>Best-effort Steam/GOG lookup.</summary>
        public static string FindGameDir()
        {
            var steamRoots = new List<string> { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" };
            foreach (var (hive, key) in new[] { (Registry.CurrentUser, @"Software\Valve\Steam"),
                                                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam") })
            {
                try
                {
                    using (var k = hive.OpenSubKey(key))
                        foreach (var name in new[] { "SteamPath", "InstallPath" })
                            if (k?.GetValue(name) is string v)
                                steamRoots.Add(v);
                }
                catch (Exception e) when (e is System.Security.SecurityException || e is IOException
                                          || e is UnauthorizedAccessException) { }
            }

            var libs = new List<string>();
            foreach (var root in steamRoots)
            {
                libs.Add(root);
                string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                try
                {
                    libs.AddRange(Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"")
                        .Cast<Match>().Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
                }
                catch (IOException) { }
            }

            var candidates = libs.Select(l => Path.Combine(l, "steamapps", "common", "Grim Dawn")).ToList();
            candidates.Add(@"C:\GOG Games\Grim Dawn");
            candidates.Add(@"C:\Program Files (x86)\GOG Galaxy\Games\Grim Dawn");
            foreach (var c in candidates)
            {
                try
                {
                    if (IsGameDir(c)) return Path.GetFullPath(c);
                }
                catch (ArgumentException) { } // malformed path from a registry/vdf entry
            }
            return "";
        }

        /// <summary>Load base game + expansions; later archives override earlier ones.</summary>
        public static Db LoadDatabase(string gameDir, Action<string> log)
        {
            var db = new Db();
            Func<string, bool> keep = name => RelevantPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));
            foreach (var rel in Databases)
            {
                string path = Path.Combine(gameDir, rel);
                if (!File.Exists(path))
                {
                    log($"  (skipping missing {rel})");
                    continue;
                }
                log($"  reading {rel} ...");
                Arz.Read(path, keep, db);
            }
            log($"  {db.Count:N0} relevant records loaded");
            return db;
        }
    }
}
