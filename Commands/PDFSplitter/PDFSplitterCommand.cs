using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace IssueBuddy
{
    /// <summary>
    /// Launches the PDF Splitter window.
    ///
    /// Gathers everything the window needs up front:
    ///  - all sheet (ViewSheet) parameters, read off a sample sheet
    ///  - all Project Information parameters
    /// and passes them in as ParameterClass items for building file name
    /// templates.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class PDFSplitterCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIApplication uiapp = commandData.Application;
                UIDocument uidoc = uiapp.ActiveUIDocument;
                Document doc = uidoc.Document;

                // FIX: Guard statement to prevent running the tool inside the Family Editor environment
                if (doc.IsFamilyDocument)
                {
                    TaskDialog.Show(
                        "Invalid Environment",
                        "This command can only be run within a Project environment, not within the Family Editor.",
                        TaskDialogCommonButtons.Ok,
                        TaskDialogResult.Ok
                    );
                    return Result.Cancelled;
                }

                // ── Find a sample sheet for parameter discovery + preview ───
                ViewSheet sampleSheet = uidoc.ActiveView as ViewSheet;

                if (sampleSheet == null)
                {
                    sampleSheet = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .Where(s => !s.IsTemplate)
                        .OrderBy(s => s.SheetNumber)
                        .FirstOrDefault();
                }

                if (sampleSheet == null)
                {
                    TaskDialog.Show("PDF Splitter",
                        "This project contains no sheets — there is nothing to split against.");
                    return Result.Cancelled;
                }

                // ── Collect available parameters ────────────────────────────
                List<ParameterClass> parameterList = new List<ParameterClass>();

                // Sheet parameters (instance parameters on the sample sheet)
                foreach (Parameter p in sampleSheet.Parameters)
                {
                    if (p == null || p.Definition == null) continue;

                    // Only types the name builder can render as text.
                    StorageType st = p.StorageType;
                    if (st != StorageType.String &&
                        st != StorageType.Double &&
                        st != StorageType.Integer &&
                        st != StorageType.ElementId)
                        continue;

                    parameterList.Add(new ParameterClass(p.Definition.Name, "ViewSheet", p.Id.GetHashCode()));
                }

                // Project Information parameters
                ProjectInfo info = doc.ProjectInformation;
                if (info != null)
                {
                    foreach (Parameter p in info.Parameters)
                    {
                        if (p == null || p.Definition == null) continue;

                        StorageType st = p.StorageType;
                        if (st != StorageType.String &&
                            st != StorageType.Double &&
                            st != StorageType.Integer)
                            continue;

                        parameterList.Add(new ParameterClass(p.Definition.Name, "ProjectInfo", p.Id.GetHashCode()));
                    }
                }

                // De-duplicate by name + type
                parameterList = parameterList
                    .GroupBy(p => p.TypeName + "::" + p.Name)
                    .Select(g => g.First())
                    .ToList();

                // ── Show the window, parented to Revit ──────────────────────
                PDFSplitter window = new PDFSplitter(parameterList, sampleSheet, uiapp);

                WindowInteropHelper helper = new WindowInteropHelper(window);
                helper.Owner = uiapp.MainWindowHandle;

                window.ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
