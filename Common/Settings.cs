using System;
using System.Collections.Generic;
using System.IO;

namespace IssueBuddy.Common
{
    /// <summary>
    /// Persisted application settings for IssueBuddy.
    /// Stored as a plain INI file alongside the IssueBuddy DLL, e.g.
    ///   %AppData%\Autodesk\Revit\Addins\<version>\IssueBuddy\settings.ini
    /// One place to clean up (same folder as FileNameTemplates.xml).
    ///
    /// Format:
    ///   ; comment
    ///   [Section]
    ///   Key=Value
    /// </summary>
    public class Settings
    {
        // ── Singleton ──────────────────────────────────────────────────────────
        private static Settings? _instance;
        public static Settings Instance => _instance ??= Load();

        // ── File paths ─────────────────────────────────────────────────────────
        // Sits next to IssueBuddy.dll. Assembly.Location is the DLL on disk;
        // CodeBase covers the rare shadow-copy case.
        private static string SettingsFolder
        {
            get
            {
                System.Reflection.Assembly asm = typeof(Settings).Assembly;
                string asmPath = !string.IsNullOrEmpty(asm.Location)
                    ? asm.Location
                    : new Uri(asm.CodeBase).LocalPath;

                return Path.GetDirectoryName(asmPath)!;
            }
        }

        private static string SettingsPath =>
            Path.Combine(SettingsFolder, "settings.ini");

        // ── Properties ────────────────────────────────────────────────────────

        /// <summary>Default output folder for exported print sets.</summary>
        public string DefaultOutputFolder { get; set; } =
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        /// <summary>Last used sheet set name.</summary>
        public string LastSheetSetName { get; set; } = string.Empty;

        /// <summary>Show confirmation dialogs.</summary>
        public bool ShowConfirmations { get; set; } = true;

        /// <summary>Add-in ribbon panel name.</summary>
        public string RibbonPanelName { get; set; } = "IssueBuddy";

        /// <summary>Application version – written on every save.</summary>
        public string Version { get; set; } = "1.0.0";

        // ── Load ───────────────────────────────────────────────────────────────

        /// <summary>Load settings from the INI file; returns defaults if absent.</summary>
        public static Settings Load()
        {
            var s = new Settings();

            try
            {
                if (!File.Exists(SettingsPath))
                    return s;

                var ini = ParseIni(SettingsPath);

                s.DefaultOutputFolder = ini.Get("General", "DefaultOutputFolder", s.DefaultOutputFolder);
                s.LastSheetSetName = ini.Get("General", "LastSheetSetName", s.LastSheetSetName);
                s.ShowConfirmations = ini.GetBool("General", "ShowConfirmations", s.ShowConfirmations);
                s.RibbonPanelName = ini.Get("UI", "RibbonPanelName", s.RibbonPanelName);
                s.Version = ini.Get("Meta", "Version", s.Version);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IssueBuddy] Could not load settings: {ex.Message}");
            }

            return s;
        }

        // ── Save ───────────────────────────────────────────────────────────────

        /// <summary>Persist current settings to the INI file.</summary>
        public void Save()
        {
            try
            {
                if (!Directory.Exists(SettingsFolder))
                    Directory.CreateDirectory(SettingsFolder);

                Version = "1.0.0";

                var lines = new List<string>
                {
                    "; IssueBuddy settings file",
                    "; Edited automatically – you may also change values by hand.",
                    "",
                    "[General]",
                    $"DefaultOutputFolder={DefaultOutputFolder}",
                    $"LastSheetSetName={LastSheetSetName}",
                    $"ShowConfirmations={ShowConfirmations}",
                    "",
                    "[UI]",
                    $"RibbonPanelName={RibbonPanelName}",
                    "",
                    "[Meta]",
                    $"Version={Version}",
                };

                File.WriteAllLines(SettingsPath, lines);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IssueBuddy] Could not save settings: {ex.Message}");
            }
        }

        // ── Reset (unit tests) ─────────────────────────────────────────────────
        public static void Reset() => _instance = null;

        // ── INI parser ─────────────────────────────────────────────────────────

        private static IniData ParseIni(string path)
        {
            var data = new IniData();
            string section = string.Empty;

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();

                if (string.IsNullOrEmpty(line) || line.StartsWith(";"))
                    continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                data.Set(section, key, value);
            }

            return data;
        }

        // ── Minimal INI data store ─────────────────────────────────────────────

        private class IniData
        {
            // section -> key -> value
            private readonly Dictionary<string, Dictionary<string, string>> _data =
                new(StringComparer.OrdinalIgnoreCase);

            public void Set(string section, string key, string value)
            {
                if (!_data.TryGetValue(section, out var keys))
                    _data[section] = keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                keys[key] = value;
            }

            public string Get(string section, string key, string fallback)
            {
                if (_data.TryGetValue(section, out var keys) &&
                    keys.TryGetValue(key, out string? val))
                    return val;
                return fallback;
            }

            public bool GetBool(string section, string key, bool fallback)
            {
                string raw = Get(section, key, fallback.ToString());
                if (bool.TryParse(raw, out bool result)) return result;
                // Also accept 1/0
                if (raw == "1") return true;
                if (raw == "0") return false;
                return fallback;
            }
        }
    }
}