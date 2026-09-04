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
    /// Launches the Sheets Issues & Revisions History window.
    /// Restricted to execution only within a project document context.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class RevisionViewerCommand : IExternalCommand
    {
        private const string Title = "Revision Viewer";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // ── Entry conditions ─────────────────────────────────────────
                // Read-only is fine: the viewer only reads the model and exports
                // to CSV, so it never opens a transaction.
                UIDocument? uidoc = CommandGuards.GetProjectUIDocument(commandData, Title);
                if (uidoc == null)
                    return Result.Cancelled;

                UIApplication uiapp = commandData.Application;
                Document doc = uidoc.Document;

                // The window is a sheet-by-revision matrix — with no sheets there
                // is nothing to draw.
                if (!CommandGuards.RequireSheets(doc, Title))
                    return Result.Cancelled;

                RevisionViewer window = new RevisionViewer();

                // Set the Revit window as owner of the WPF window, so that they
                // behave modally (e.g. Revit's "Activate" command brings the
                // viewer to the front instead of leaving it behind).
                WindowInteropHelper helper = new WindowInteropHelper(window);
                helper.Owner = uiapp.MainWindowHandle;

                // Build out and map data onto the form control components
                window.PopulateData(doc);

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