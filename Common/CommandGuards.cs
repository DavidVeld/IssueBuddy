using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Linq;

namespace IssueBuddy.Common
{
    /// <summary>
    /// Shared entry-condition checks for the IssueBuddy external commands.
    ///
    /// Every method here is silent on success and shows exactly one explanatory
    /// dialog on failure — nothing is displayed just to confirm a check passed.
    ///
    /// Why this exists: without an up-front check, a command started in the wrong
    /// context fails deep inside the Revit API, and the user gets Revit's raw
    /// "Command Failure for External Command" box (or a NullReferenceException)
    /// instead of being told what to do. The states worth catching are:
    ///   • no document open at all — ActiveUIDocument is null
    ///   • the Family Editor — most of the API these tools use does not apply
    ///   • a read-only file — any transaction will throw
    ///   • a project with nothing to work on — no sheets, no revisions
    /// </summary>
    internal static class CommandGuards
    {
        /// <summary>
        /// Resolves the active project document, rejecting the states that make
        /// every IssueBuddy command impossible. Returns null after showing a
        /// message, so callers can simply return Result.Cancelled.
        ///
        /// Pass requireModifiable for commands that write to the model.
        /// </summary>
        internal static UIDocument? GetProjectUIDocument(
            ExternalCommandData commandData,
            string title,
            bool requireModifiable = false)
        {
            UIApplication? app = commandData?.Application;

            if (app == null)
            {
                Show(title,
                    "Revit did not supply an application context for this command.\n\n" +
                    "Please restart Revit and try again.");
                return null;
            }

            // Null whenever Revit has no open document — the start page, or after
            // the last project is closed while the ribbon stays clickable.
            UIDocument uidoc = app.ActiveUIDocument;
            if (uidoc?.Document == null)
            {
                Show(title,
                    "No project is open.\n\n" +
                    "Open a Revit project, then run this tool again.");
                return null;
            }

            Document doc = uidoc.Document;

            if (doc.IsFamilyDocument)
            {
                Show(title,
                    "This tool only works in a project.\n\n" +
                    "It cannot run in the Family Editor. Close the family, switch to " +
                    "a project document, then try again.");
                return null;
            }

            if (requireModifiable && !RequireModifiable(doc, title))
                return null;

            return uidoc;
        }

        /// <summary>
        /// Rejects documents that cannot be edited. Checks the Revit flag and the
        /// file system separately — a file can carry the Windows read-only
        /// attribute while doc.IsReadOnly still reports false.
        /// </summary>
        internal static bool RequireModifiable(Document doc, string title)
        {
            if (doc.IsReadOnly)
            {
                Show(title,
                    "This model is open in read-only mode, so it cannot be changed.\n\n" +
                    "This usually means the file is locked by another application, or " +
                    "was opened with Detach from Central.\n\n" +
                    "Re-open it with write access, then try again.");
                return false;
            }

            // A model that has never been saved has no path to inspect — that is
            // fine here, since an in-memory document is still modifiable.
            if (!string.IsNullOrEmpty(doc.PathName))
            {
                try
                {
                    var fileInfo = new System.IO.FileInfo(doc.PathName);
                    if (fileInfo.Exists && fileInfo.IsReadOnly)
                    {
                        Show(title,
                            "The file on disk is marked as read-only:\n\n" + doc.PathName + "\n\n" +
                            "Right-click the file in Windows Explorer, select Properties, " +
                            "uncheck Read-only, then re-open it in Revit.");
                        return false;
                    }
                }
                catch
                {
                    // BIM 360 / ACC paths cannot be resolved by FileInfo — skip the
                    // check rather than blocking a cloud model that is fine.
                }
            }

            return true;
        }

        /// <summary>
        /// Rejects projects with no real sheets, so sheet-driven tools explain the
        /// situation instead of opening an empty window.
        /// </summary>
        internal static bool RequireSheets(Document doc, string title)
        {
            bool anySheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Any(s => !s.IsPlaceholder);

            if (!anySheets)
            {
                Show(title,
                    "This project contains no sheets.\n\n" +
                    "Add at least one sheet, then run this tool again.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Rejects projects with no revisions, so revision-driven tools explain the
        /// situation instead of opening a window with an empty list.
        /// </summary>
        internal static bool RequireRevisions(Document doc, string title)
        {
            bool anyRevisions = new FilteredElementCollector(doc)
                .OfClass(typeof(Revision))
                .Any();

            if (!anyRevisions)
            {
                Show(title,
                    "This project has no revisions.\n\n" +
                    "Add a revision under View → Sheet Composition → Revisions, " +
                    "then run this tool again.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Last-resort handler for an unexpected exception. Shows the detail in a
        /// collapsed section so the dialog stays readable but the message is still
        /// available when reporting a problem.
        /// </summary>
        internal static void ReportUnexpected(string title, Exception ex)
        {
            try
            {
                var td = new TaskDialog(title)
                {
                    MainInstruction = "Something went wrong",
                    MainContent =
                        "This command stopped because of an unexpected error. " +
                        "Your model has not been changed.",
                    ExpandedContent = ex.ToString(),
                    CommonButtons = TaskDialogCommonButtons.Close,
                    DefaultButton = TaskDialogResult.Close
                };
                td.Show();
            }
            catch
            {
                // Never let the error reporter itself throw out of a command.
            }
        }

        /// <summary>
        /// Single place the guard messages are displayed, so they all look alike.
        /// </summary>
        private static void Show(string title, string content)
        {
            try
            {
                var td = new TaskDialog(title)
                {
                    MainContent = content,
                    CommonButtons = TaskDialogCommonButtons.Close,
                    DefaultButton = TaskDialogResult.Close
                };
                td.Show();
            }
            catch
            {
                // A failed dialog must not mask the guard's decision.
            }
        }
    }
}
