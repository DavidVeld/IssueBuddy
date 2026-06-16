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
    /// Launches the Sheets Issues & Revisions History window.
    /// Restricted to execution only within a project document context.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class RevisionViewerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIApplication uiapp = commandData.Application;
                UIDocument uidoc = uiapp.ActiveUIDocument;

                // If there is no active document open at all, abort gracefully
                if (uidoc == null) return Result.Cancelled;

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
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}