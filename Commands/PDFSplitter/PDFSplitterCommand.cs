using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using IssueBuddy.Common;

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
        private const string Title = "PDF Splitter";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // ── Entry conditions ─────────────────────────────────────────
                // Read-only is fine: the splitter writes PDFs and DWGs to disk,
                // it does not modify the model.
                UIDocument? uidoc = CommandGuards.GetProjectUIDocument(commandData, Title);
                if (uidoc == null)
                    return Result.Cancelled;

                UIApplication uiapp = commandData.Application;
                Document doc = uidoc.Document;

                if (!CommandGuards.RequireSheets(doc, Title))
                    return Result.Cancelled;

                // ── Find a sample sheet for parameter discovery + preview ───
                // Null whenever the active view is anything other than a sheet —
                // a schedule, a 3D view, a legend — so fall back to the first
                // sheet in the project rather than failing.
                ViewSheet sampleSheet = uidoc.ActiveView as ViewSheet;

                if (sampleSheet == null)
                {
                    sampleSheet = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .Where(s => !s.IsPlaceholder)
                        .OrderBy(s => s.SheetNumber)
                        .FirstOrDefault();
                }

                // Unreachable in practice — RequireSheets applies the same filter.
                // Kept so a future change to either query cannot produce a silent
                // failure further down.
                if (sampleSheet == null)
                {
                    TaskDialog.Show(Title,
                        "No usable sheet could be found in this project, so the file " +
                        "name templates cannot be built.");
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
                // Handled here rather than via Result.Failed so the user sees an
                // explanation instead of Revit's raw "Command Failure" box.
                CommandGuards.ReportUnexpected(Title, ex);
                return Result.Cancelled;
            }
        }
    }
}
