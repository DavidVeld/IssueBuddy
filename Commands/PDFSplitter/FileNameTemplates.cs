using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;

namespace IssueBuddy
{
    /// <summary>
    /// One saved file-name preset. Each preset is its own XML file, so presets
    /// can be shared, exported and imported without touching anyone else's.
    /// </summary>
    [Serializable]
    public class FileNameTemplate
    {
        public string Name { get; set; }

        public List<ParameterClass> Items { get; set; }

        /// <summary>File this preset was read from (not serialised).</summary>
        [XmlIgnore]
        public string FilePath { get; set; } = "";

        /// <summary>True when it lives in the shared (server) folder.</summary>
        [XmlIgnore]
        public bool IsShared { get; set; }

        public FileNameTemplate()
        {
            Name = "";
            Items = new List<ParameterClass>();
        }

        // The editable ComboBox shows and matches on this.
        public override string ToString() => Name;
    }

    /// <summary>
    /// Preset storage.
    ///
    ///   Local:  &lt;IssueBuddy.dll folder&gt;\Templates\&lt;name&gt;.xml
    ///   Shared: any folder set in settings.ini  [Templates] SharedFolder=
    ///
    /// The class itself is the OLD combined format (root &lt;FileNameTemplates&gt;),
    /// kept only so legacy FileNameTemplates.xml files can be imported and
    /// exploded into single files. The legacy file next to the DLL is migrated
    /// once — when the Templates folder does not exist yet — and ignored after.
    /// </summary>
    [Serializable]
    [XmlRoot("FileNameTemplates")]
    public class FileNameTemplateCollection
    {
        public List<FileNameTemplate> Templates { get; set; } = new List<FileNameTemplate>();

        // ── Locations ──────────────────────────────────────────────────────

        private static string DllFolder
        {
            get
            {
                System.Reflection.Assembly asm = typeof(FileNameTemplateCollection).Assembly;
                string asmPath = !string.IsNullOrEmpty(asm.Location)
                    ? asm.Location
                    : new Uri(asm.CodeBase).LocalPath;

                return Path.GetDirectoryName(asmPath);
            }
        }

        public static string LocalFolder => Path.Combine(DllFolder, "Templates");

        private static string LegacyPath => Path.Combine(DllFolder, "FileNameTemplates.xml");

        private static readonly string[] ValidTypes = { "ProjectInfo", "ViewSheet", "Separator" };

        /// <summary>File path a preset called <paramref name="name"/> gets in <paramref name="folder"/>.</summary>
        public static string PathFor(string folder, string name)
        {
            string safe = string.Concat(name.Trim().Select(
                c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            return Path.Combine(folder, safe + ".xml");
        }

        // ── Load ───────────────────────────────────────────────────────────

        /// <summary>
        /// Local presets, migrating the old combined file first if needed.
        /// Never throws — unreadable files are skipped so the tool always opens.
        /// </summary>
        public static List<FileNameTemplate> LoadLocal()
        {
            MigrateLegacy();
            return ReadFolder(LocalFolder, false);
        }

        /// <summary>
        /// Shared presets. Never throws, but an unreachable server blocks for the
        /// network timeout (~40 s measured) — call it off the UI thread.
        /// </summary>
        public static List<FileNameTemplate> LoadShared(string folder)
        {
            return string.IsNullOrWhiteSpace(folder)
                ? new List<FileNameTemplate>()
                : ReadFolder(folder, true);
        }

        private static List<FileNameTemplate> ReadFolder(string folder, bool isShared)
        {
            var result = new List<FileNameTemplate>();
            try
            {
                if (!Directory.Exists(folder))
                    return result;

                foreach (string file in Directory.GetFiles(folder, "*.xml"))
                {
                    try
                    {
                        // Only single-preset files count; a stray combined file is not a preset.
                        FileNameTemplate t = ReadFile(file).SingleOrDefault();
                        if (t == null) continue;
                        t.FilePath = file;
                        t.IsShared = isShared;
                        result.Add(t);
                    }
                    catch { /* not a preset — skip */ }
                }
            }
            catch { /* folder unreachable (e.g. server offline) */ }

            return result.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Reads a single-preset file or a legacy combined file. Throws
        /// <see cref="InvalidDataException"/> when the file is neither, or
        /// holds no valid preset.
        /// </summary>
        public static List<FileNameTemplate> ReadFile(string path)
        {
            List<FileNameTemplate> list;

            // XmlReader.Create prohibits DTDs by default (no XXE from imported files).
            using (XmlReader reader = XmlReader.Create(path))
            {
                var single = new XmlSerializer(typeof(FileNameTemplate));
                var combined = new XmlSerializer(typeof(FileNameTemplateCollection));

                if (single.CanDeserialize(reader))
                {
                    var t = (FileNameTemplate)single.Deserialize(reader);
                    if (string.IsNullOrWhiteSpace(t.Name))
                        t.Name = Path.GetFileNameWithoutExtension(path);
                    list = new List<FileNameTemplate> { t };
                }
                else if (combined.CanDeserialize(reader))
                {
                    list = ((FileNameTemplateCollection)combined.Deserialize(reader)).Templates
                           ?? new List<FileNameTemplate>();
                }
                else
                {
                    throw new InvalidDataException("Not an IssueBuddy file-name template.");
                }
            }

            list = list.Where(IsValid).ToList();
            if (list.Count == 0)
                throw new InvalidDataException("The file holds no valid file-name template.");

            foreach (FileNameTemplate t in list)
                t.Name = t.Name.Trim();
            return list;
        }

        private static bool IsValid(FileNameTemplate t)
        {
            return t != null
                && !string.IsNullOrWhiteSpace(t.Name)
                && t.Items != null && t.Items.Count > 0
                && t.Items.All(i => i != null && ValidTypes.Contains(i.TypeName));
        }

        /// <summary>
        /// First run of the per-file storage: explode the old combined file
        /// into the new Templates folder. After that the old file is ignored.
        /// </summary>
        private static void MigrateLegacy()
        {
            try
            {
                if (Directory.Exists(LocalFolder))
                    return;

                Directory.CreateDirectory(LocalFolder);
                if (File.Exists(LegacyPath))
                    foreach (FileNameTemplate t in ReadFile(LegacyPath))
                        Write(t, PathFor(LocalFolder, t.Name));
            }
            catch
            {
                // Legacy file unreadable — the user can still Import it by hand.
            }
        }

        // ── Write / delete ─────────────────────────────────────────────────

        /// <summary>Writes one preset as a single-preset XML file.</summary>
        public static void Write(FileNameTemplate template, string path)
        {
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            var copy = new FileNameTemplate
            {
                Name = template.Name.Trim(),
                Items = template.Items.Select(i => i.Clone()).ToList()
            };

            var serializer = new XmlSerializer(typeof(FileNameTemplate));
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                serializer.Serialize(fs, copy);
        }

        public static void Delete(FileNameTemplate template)
        {
            if (File.Exists(template.FilePath))
                File.Delete(template.FilePath);
        }
    }
}
