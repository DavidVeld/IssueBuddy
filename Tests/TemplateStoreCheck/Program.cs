// Self-check for FileNameTemplateCollection. Runs against the copy of
// IssueBuddy.dll in this project's own bin folder, so it only ever touches
// files there. From the repo root:
//
//   dotnet run --project Tests/TemplateStoreCheck
//
// Prints PASS/FAIL per case; exit code 1 when anything fails.
using System;
using System.IO;
using System.Linq;
using IssueBuddy;

string dll = Path.GetDirectoryName(typeof(FileNameTemplateCollection).Assembly.Location);
string local = FileNameTemplateCollection.LocalFolder;
string shared = Path.Combine(dll, "shared");
string legacy = Path.Combine(dll, "FileNameTemplates.xml");

foreach (string dir in new[] { local, shared })
    if (Directory.Exists(dir)) Directory.Delete(dir, true);

const string Sep = "<ParameterClass><Id>-999</Id><Name>-</Name><TypeName>Separator</TypeName></ParameterClass>";
const string Sheet = "<ParameterClass><Id>1</Id><Name>Sheet Number</Name><TypeName>ViewSheet</TypeName></ParameterClass>";
string Preset(string name) => $"<FileNameTemplate><Name>{name}</Name><Items>{Sheet}{Sep}{Sheet}</Items></FileNameTemplate>";

// Old combined format: two good presets and one invalid (no items).
File.WriteAllText(legacy,
    "<?xml version=\"1.0\"?><FileNameTemplates><Templates>" +
    Preset("Proj-Sheet") + Preset("Team / Std") +
    "<FileNameTemplate><Name>Empty</Name><Items/></FileNameTemplate>" +
    "</Templates><LastUsed>Proj-Sheet</LastUsed></FileNameTemplates>");

void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
    if (!ok) Environment.ExitCode = 1;
}

void Rejects(string xml, string what)
{
    string p = Path.Combine(dll, "bad.xml");
    File.WriteAllText(p, xml);
    try { FileNameTemplateCollection.ReadFile(p); Check(false, what); }
    catch (InvalidDataException) { Check(true, what); }
    catch (Exception ex) { Check(false, what + " (threw " + ex.GetType().Name + ")"); }
}

// 1. First load explodes the legacy file into one file per valid preset.
var mine = FileNameTemplateCollection.LoadLocal();
Check(mine.Count == 2 && Directory.GetFiles(local, "*.xml").Length == 2 && mine.All(t => !t.IsShared),
      "legacy file split into 2 local files, invalid preset dropped");
Check(File.Exists(Path.Combine(local, "Team _ Std.xml")), "invalid file-name characters replaced");

// 2. Migration runs once: a deleted preset stays deleted.
FileNameTemplateCollection.Delete(mine[0]);
Check(FileNameTemplateCollection.LoadLocal().Count == 1, "legacy file ignored after migration");

// 3. Publish to the shared folder and read it back.
FileNameTemplateCollection.Write(mine[1], FileNameTemplateCollection.PathFor(shared, mine[1].Name));
var team = FileNameTemplateCollection.LoadShared(shared).Single();
Check(team.IsShared && team.Name == mine[1].Name && team.Items.Count == 3, "shared round trip");

// 4. A single preset with a blank name takes the file name.
string blank = Path.Combine(dll, "Blank Name.xml");
File.WriteAllText(blank, Preset(""));
Check(FileNameTemplateCollection.ReadFile(blank).Single().Name == "Blank Name", "blank name -> file name");

// 5. Files that are not valid presets are rejected on import.
Rejects("<?xml version=\"1.0\"?><Foo/>", "foreign XML rejected");
Rejects("<?xml version=\"1.0\"?><FileNameTemplate><Name>x</Name><Items/></FileNameTemplate>", "empty preset rejected");
Rejects("<?xml version=\"1.0\"?><FileNameTemplate><Name>x</Name><Items><ParameterClass><Id>1</Id><Name>a</Name>" +
        "<TypeName>Bogus</TypeName></ParameterClass></Items></FileNameTemplate>", "unknown parameter type rejected");

// 6. Junk and combined files in a preset folder are skipped; missing folders give nothing.
File.WriteAllText(Path.Combine(local, "junk.xml"), "not xml");
File.Copy(legacy, Path.Combine(local, "combined.xml"));
Check(FileNameTemplateCollection.LoadLocal().Count == 1, "junk and combined files skipped");
Check(FileNameTemplateCollection.LoadShared("").Count == 0 &&
      FileNameTemplateCollection.LoadShared(@"\\nonexistent-server-xyz\share").Count == 0,
      "blank or unreachable shared folder -> no presets, no exception");
