using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace IssueBuddy
{
    /// <summary>
    /// Creates a named print set (<see cref="ViewSheetSet"/>) containing the
    /// views and sheets currently selected in the Project Browser.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CreatePrintSetCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show("Print Set From Selection", "No active document.");
                return Result.Cancelled;
            }
            var doc = uidoc.Document;

            // 1. Harvest printable views and sheets from current selection.
            var selectedViews = new List<View>();
            foreach (var id in uidoc.Selection.GetElementIds())
            {
                var elem = doc.GetElement(id);
                if (elem is ViewSheet sheet)
                    selectedViews.Add(sheet);
                else if (elem is View v && !v.IsTemplate && v.CanBePrinted)
                    selectedViews.Add(v);
            }

            if (selectedViews.Count == 0)
            {
                TaskDialog.Show("Print Set From Selection",
                    "Select one or more views or sheets in the Project Browser first.");
                return Result.Cancelled;
            }

            // 2. Prompt for a name.
            string name = NamePromptWindow.Prompt(selectedViews.Count);
            if (name == null) return Result.Cancelled; // user cancelled
            name = name.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                TaskDialog.Show("Print Set From Selection", "Name cannot be empty.");
                return Result.Cancelled;
            }

            // 3. Reject duplicates.
            bool nameTaken = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheetSet))
                .Cast<ViewSheetSet>()
                .Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

            if (nameTaken)
            {
                TaskDialog.Show("Print Set From Selection",
                    $"A print set named \"{name}\" already exists. Please choose a different name.");
                return Result.Cancelled;
            }

            // 4. Create it.
            try
            {
                using (var t = new Transaction(doc, "Create Print Set From Selection"))
                {
                    t.Start();

                    var printManager = doc.PrintManager;
                    printManager.PrintRange = PrintRange.Select;
                    var setting = printManager.ViewSheetSetting;

                    // Switch to the In-Session set so we can edit its Views collection.
                    setting.CurrentViewSheetSet = setting.InSession;

                    var viewSet = new ViewSet();
                    foreach (var v in selectedViews) viewSet.Insert(v);

                    setting.CurrentViewSheetSet.Views = viewSet;
                    setting.SaveAs(name);

                    t.Commit();
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }

            // 5. Success dialog with a list of what went in.
            ShowSuccess(name, selectedViews);
            return Result.Succeeded;
        }

        private static void ShowSuccess(string name, List<View> views)
        {
            var ordered = views
                .OrderBy(v => v is ViewSheet ? 0 : 1)
                .ThenBy(v => v is ViewSheet s ? s.SheetNumber : v.Name);

            var sb = new StringBuilder();
            foreach (var v in ordered)
            {
                if (v is ViewSheet sheet)
                    sb.AppendLine($"• [Sheet]  {sheet.SheetNumber}  –  {sheet.Name}");
                else
                    sb.AppendLine($"• [View]   {v.Name}");
            }

            var td = new TaskDialog("Print Set From Selection")
            {
                MainInstruction = "Print set created",
                MainContent = $"\"{name}\" was created with {views.Count} item(s):",
                ExpandedContent = sb.ToString(),
                CommonButtons = TaskDialogCommonButtons.Close,
                DefaultButton = TaskDialogResult.Close
            };
            td.Show();
        }
    }
}
