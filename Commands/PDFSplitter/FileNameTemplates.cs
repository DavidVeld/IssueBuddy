using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace IssueBuddy
{
    /// <summary>
    /// One saved file-name preset.
    /// </summary>
    [Serializable]
    public class FileNameTemplate
    {
        public string Name { get; set; }

        public List<ParameterClass> Items { get; set; }

        public FileNameTemplate()
        {
            Name = "";
            Items = new List<ParameterClass>();
        }
    }

    /// <summary>
    /// All saved file-name presets, persisted as readable XML at:
    ///   %AppData%\IssueBuddy\FileNameTemplates.xml
    ///
    /// Design note — why this fixes the old "save needs a reload" bug:
    /// The old implementation re-deserialised the XML file immediately after
    /// saving, and the window then replaced its bound ObservableCollection
    /// with a brand-new (empty) one without rebinding the ListView. The UI
    /// kept showing the orphaned old collection while edits went into the
    /// hidden new one — hence everything only looked right after a restart.
    ///
    /// Here the in-memory collection is the single source of truth:
    /// Save() writes the current state to disk and never re-reads it, and
    /// the window mutates one ObservableCollection in place instead of
    /// swapping instances.
    /// </summary>
    [Serializable]
    [XmlRoot("FileNameTemplates")]
    public class FileNameTemplateCollection
    {
        public List<FileNameTemplate> Templates { get; set; }

        /// <summary>Name of the preset last used, restored on next launch.</summary>
        public string LastUsed { get; set; }

        public FileNameTemplateCollection()
        {
            Templates = new List<FileNameTemplate>();
            LastUsed = "";
        }

        // ── Storage location ───────────────────────────────────────────────
        //
        // Stored next to the IssueBuddy DLL itself, e.g.
        //   %AppData%\Autodesk\Revit\Addins\<version>\IssueBuddy\FileNameTemplates.xml
        // Per-machine, per-Revit-version. Standard Revit addins live under
        // the user's AppData so writes are always permitted.

        private static string StorageFolder
        {
            get
            {
                // Assembly.Location is the DLL on disk; CodeBase handles the
                // shadow-copy case some hosts use. Either way → DLL directory.
                System.Reflection.Assembly asm = typeof(FileNameTemplateCollection).Assembly;
                string asmPath = !string.IsNullOrEmpty(asm.Location)
                    ? asm.Location
                    : new Uri(asm.CodeBase).LocalPath;

                return Path.GetDirectoryName(asmPath);
            }
        }

        private static string StoragePath
        {
            get { return Path.Combine(StorageFolder, "FileNameTemplates.xml"); }
        }

        // ── Load / Save ────────────────────────────────────────────────────

        /// <summary>
        /// Loads the collection from disk. Never throws: a missing or corrupt
        /// file returns an empty collection so the tool always opens.
        /// </summary>
        public static FileNameTemplateCollection Load()
        {
            try
            {
                if (!File.Exists(StoragePath))
                    return new FileNameTemplateCollection();

                XmlSerializer serializer = new XmlSerializer(typeof(FileNameTemplateCollection));
                using (FileStream fs = new FileStream(StoragePath, FileMode.Open, FileAccess.Read))
                {
                    FileNameTemplateCollection result = serializer.Deserialize(fs) as FileNameTemplateCollection;
                    if (result == null)
                        return new FileNameTemplateCollection();

                    if (result.Templates == null)
                        result.Templates = new List<FileNameTemplate>();

                    return result;
                }
            }
            catch
            {
                return new FileNameTemplateCollection();
            }
        }

        /// <summary>
        /// Persists THIS instance to disk. The caller's in-memory state is
        /// already correct — nothing needs to be re-read afterwards.
        /// </summary>
        public bool Save(out string error)
        {
            error = "";
            try
            {
                if (!Directory.Exists(StorageFolder))
                    Directory.CreateDirectory(StorageFolder);

                XmlSerializer serializer = new XmlSerializer(typeof(FileNameTemplateCollection));
                using (FileStream fs = new FileStream(StoragePath, FileMode.Create, FileAccess.Write))
                {
                    serializer.Serialize(fs, this);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ── Operations ─────────────────────────────────────────────────────

        public FileNameTemplate Get(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            return Templates.FirstOrDefault(
                t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public bool Exists(string name)
        {
            return Get(name) != null;
        }

        /// <summary>
        /// Adds a new preset or overwrites an existing one with deep copies
        /// of the supplied items, then persists.
        /// </summary>
        public bool AddOrUpdate(string name, IEnumerable<ParameterClass> items, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "The preset needs a name.";
                return false;
            }

            string trimmed = name.Trim();
            List<ParameterClass> copy = items != null
                ? items.Select(i => i.Clone()).ToList()
                : new List<ParameterClass>();

            FileNameTemplate existing = Get(trimmed);
            if (existing != null)
            {
                existing.Items = copy;
            }
            else
            {
                Templates.Add(new FileNameTemplate { Name = trimmed, Items = copy });
            }

            LastUsed = trimmed;
            return Save(out error);
        }

        public bool Delete(string name, out string error)
        {
            error = "";
            FileNameTemplate existing = Get(name);
            if (existing == null)
            {
                error = "Preset '" + name + "' was not found.";
                return false;
            }

            Templates.Remove(existing);
            if (string.Equals(LastUsed, existing.Name, StringComparison.OrdinalIgnoreCase))
                LastUsed = "";

            return Save(out error);
        }
    }
}