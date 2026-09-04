using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using IssueBuddy.Common;

namespace IssueBuddy
{
    [Transaction(TransactionMode.Manual)]
    class CreatePrintSet : IExternalCommand
    {
        private const string Title = "Create Print Set";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // ── Entry conditions ─────────────────────────────────────────────
            // requireModifiable: the command saves a print set, which needs a
            // transaction — better to say so now than after the user has filled
            // in the dialog.
            UIDocument? uidoc = CommandGuards.GetProjectUIDocument(
                commandData, Title, requireModifiable: true);

            if (uidoc == null)
                return Result.Cancelled;

            Document doc = uidoc.Document;

            // The window lists revisions and the sheets carrying them, so both
            // have to exist for it to be of any use.
            if (!CommandGuards.RequireSheets(doc, Title)) return Result.Cancelled;
            if (!CommandGuards.RequireRevisions(doc, Title)) return Result.Cancelled;

            // Show the window — blocks Revit until closed.
            // The window validates name uniqueness before accepting,
            // so by the time we get here the name is guaranteed to be free.
            CreatePrintSetWindow window = new CreatePrintSetWindow(commandData);
            window.ShowDialog();

            if (!window.accepted)
                return Result.Cancelled;

            // Retrieve the sheet rows the window prepared
            var sheetRows = window.liv_Sheets.ItemsSource as List<SheetRow>;

            if (sheetRows == null || sheetRows.Count == 0)
            {
                TaskDialog.Show("Warning", "No sheets were found for the selected revision(s). Print set not created.");
                return Result.Cancelled;
            }

            HashSet<ElementId> sheetIds = sheetRows
                .Select(r => r.SheetElementId)
                .Where(id => id != null && id != ElementId.InvalidElementId)
                .ToHashSet();

            string finalSetName = window.FinalPrintSetName;

            using (Transaction tr = new Transaction(doc, "Create Print Set"))
            {
                tr.Start();
                try
                {
                    PrintManager printManager = doc.PrintManager;

                    // 1. Switch to Select mode so ViewSheetSetting is editable
                    printManager.PrintRange = PrintRange.Select;

                    ViewSheetSetting viewSheetSetting = printManager.ViewSheetSetting;

                    // 2. Fetch sheets by stored ElementId, sorted by sheet number.
                    //    NaturalStringComparer handles mixed alphanumeric correctly:
                    //    A-2 < A-10 < A-11 rather than the broken A-10 < A-11 < A-2
                    //    you get from a plain alphabetic sort.
                    List<ViewSheet> sheetsToAdd = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .Where(s => !s.IsPlaceholder && sheetIds.Contains(s.Id))
                        .OrderBy(s => s.SheetNumber, new NaturalStringComparer())
                        .ToList();

                    // 3. Build a brand-new ViewSet and populate it in sorted order.
                    //    Never call .Clear()/.Insert() on the existing .Views collection
                    //    — Revit exposes it read-only and throws "Collection is read only".
                    ViewSet viewSet = new ViewSet();
                    foreach (ViewSheet sheet in sheetsToAdd)
                        viewSet.Insert(sheet);

                    // 4. Point at the blank in-session set, assign views, save under the chosen name.
                    viewSheetSetting.CurrentViewSheetSet = viewSheetSetting.InSession;
                    viewSheetSetting.CurrentViewSheetSet.Views = viewSet;
                    viewSheetSetting.SaveAs(finalSetName);

                    tr.Commit();

                    TaskDialog.Show("Success",
                        $"Print set \"{finalSetName}\" was created successfully " +
                        $"with {sheetsToAdd.Count} sheet(s), sorted by sheet number.");

                    return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    tr.RollBack();
                    TaskDialog.Show("Error", "Failed to create print set:\n" + ex.Message);
                    return Result.Failed;
                }
            }
        }
    }

    /// <summary>
    /// Compares strings the way a human would read them:
    /// "A-2" comes before "A-10" because 2 &lt; 10 numerically.
    /// Standard string sort would put "A-10" before "A-2" (char '1' &lt; '2').
    /// </summary>
    internal class NaturalStringComparer : IComparer<string>
    {
        public int Compare(string x, string y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            int ix = 0, iy = 0;
            while (ix < x.Length && iy < y.Length)
            {
                if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
                {
                    long nx = 0, ny = 0;
                    while (ix < x.Length && char.IsDigit(x[ix])) nx = nx * 10 + (x[ix++] - '0');
                    while (iy < y.Length && char.IsDigit(y[iy])) ny = ny * 10 + (y[iy++] - '0');
                    if (nx != ny) return nx.CompareTo(ny);
                }
                else
                {
                    int c = char.ToUpperInvariant(x[ix]).CompareTo(char.ToUpperInvariant(y[iy]));
                    if (c != 0) return c;
                    ix++; iy++;
                }
            }
            return x.Length.CompareTo(y.Length);
        }
    }
}