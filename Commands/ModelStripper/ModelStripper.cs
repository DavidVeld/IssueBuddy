using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;

namespace IssueBuddy
{
    [Transaction(TransactionMode.Manual)]
    class ModelStripper : IExternalCommand
    {
        // Window is created fresh each Execute call — do NOT instantiate at field level,
        // as a class-level instance is constructed before commandData is available.
        private ModelStripperWindow _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication app = commandData.Application;
            UIDocument uidoc = app.ActiveUIDocument;
            Document doc = uidoc.Document;

            // Guard statement to prevent running the tool inside the Family Editor environment
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

            // ── Guard: read-only document ────────────────────────────────────────
            // Check both the Revit API flag and the file system — a file can be
            // marked read-only at the OS level even when Revit doesn't report it.
            if (doc.IsReadOnly)
            {
                MessageBox.Show(
                    "This model is open in read-only mode.\n\n" +
                    "Model Stripper needs to save and modify the file — it cannot run on a read-only document.\n\n" +
                    "Possible causes:\n" +
                    "  \u2022 The file is checked out or locked by another application\n" +
                    "  \u2022 The file has the read-only attribute set in Windows Explorer\n" +
                    "  \u2022 You opened the file using Open with Detach, which is read-only\n\n" +
                    "Close and re-open the file with full write access, then try again.",
                    "Read-Only Document", MessageBoxButton.OK, MessageBoxImage.Warning);
                return Result.Cancelled;
            }

            // Also check the file system directly — Revit doesn't always surface
            // OS-level read-only attributes through doc.IsReadOnly
            if (!string.IsNullOrEmpty(doc.PathName))
            {
                try
                {
                    var fileInfo = new System.IO.FileInfo(doc.PathName);
                    if (fileInfo.Exists && fileInfo.IsReadOnly)
                    {
                        MessageBox.Show(
                            "The file on disk is marked as read-only:\n\n" + doc.PathName + "\n\n" +
                            "Right-click the file in Windows Explorer, select Properties, " +
                            "uncheck Read-only, then close and re-open it in Revit.",
                            "File Is Read-Only", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return Result.Cancelled;
                    }
                }
                catch
                {
                    // Path may be a BIM360/ACC path that FileInfo cannot resolve — skip this check
                }
            }

            // ── Guard: family editor ─────────────────────────────────────────────
            if (doc.IsFamilyDocument)
            {
                MessageBox.Show(
                    "Model Stripper cannot run while a Family is open for editing.\n\n" +
                    "Please close the family editor and run this tool from a project document.",
                    "Not Available", MessageBoxButton.OK, MessageBoxImage.Warning);
                return Result.Cancelled;
            }

            // ── Guard: unsaved workshared model ──────────────────────────────────
            if (doc.IsWorkshared && string.IsNullOrEmpty(doc.PathName))
            {
                MessageBox.Show(
                    "This workshared model has never been saved.\n\n" +
                    "Please save (and sync) the file before running Model Stripper.",
                    "Unsaved Model", MessageBoxButton.OK, MessageBoxImage.Warning);
                return Result.Cancelled;
            }

            // ── Guard: check for elements owned by other users ───────────────────
            if (doc.IsWorkshared)
            {
                var conflicts = GetElementsOwnedByOthers(doc);
                if (conflicts.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("The following users currently own elements in this model.");
                    sb.AppendLine("They must relinquish ownership before Model Stripper can safely delete all elements.\n");

                    foreach (var kvp in conflicts)
                        sb.AppendLine($"  • {kvp.Key}  ({kvp.Value} element{(kvp.Value == 1 ? "" : "s")} owned)");

                    sb.AppendLine("\nAsk each user to Synchronize with Central and relinquish all elements, then re-run this tool.");

                    MessageBox.Show(sb.ToString(), "Elements Owned by Others",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return Result.Cancelled;
                }
            }

            // ── Show window ──────────────────────────────────────────────────────
            _window = new ModelStripperWindow();
            _window.selectedName = doc.Title;
            _window.ShowDialog();

            if (!_window.IsAccepted)
                return Result.Cancelled;

            string fileName = _window.newFileName;
            string newRevFilPath = System.IO.Path.Combine(_window.newfilepath, fileName + ".rvt");

            // ════════════════════════════════════════════════════════════════════
            // STEP 1 — Optionally sync the ORIGINAL file before we do anything
            // ════════════════════════════════════════════════════════════════════
            if (_window.chb_SaveFile.IsChecked == true)
            {
                if (doc.IsWorkshared)
                {
                    try
                    {
                        var syncOpts = new SynchronizeWithCentralOptions();
                        var relinquishOpts = new RelinquishOptions(true);
                        syncOpts.SetRelinquishOptions(relinquishOpts);
                        syncOpts.SaveLocalBefore = true;
                        syncOpts.SaveLocalAfter = true;
                        doc.SynchronizeWithCentral(new TransactWithCentralOptions(), syncOpts);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Sync failed:\n\n" + ex.Message +
                            "\n\nModel Stripper has been cancelled. Your original file is unchanged.",
                            "Sync Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return Result.Cancelled;
                    }
                }
                else
                {
                    doc.Save();
                }
            }

            // ════════════════════════════════════════════════════════════════════
            // STEP 2 — SaveAs a copy to the target path.
            //
            // WHY THIS ORDER MATTERS:
            // We SaveAs BEFORE stripping. The copy is saved while the model is still
            // clean. We then strip the COPY (by reopening it), never the live session.
            // If we stripped first then SaveAs'd, the live document would be corrupted
            // and the user would be unable to sync back to the original central.
            // ════════════════════════════════════════════════════════════════════
            try
            {
                var saveOpts = new SaveAsOptions
                {
                    OverwriteExistingFile = true,
                    Compact = true
                };

                if (doc.IsWorkshared)
                {
                    var wsOpts = new WorksharingSaveAsOptions { SaveAsCentral = true };
                    saveOpts.SetWorksharingOptions(wsOpts);
                }

                doc.SaveAs(newRevFilPath, saveOpts);
            }
            catch (Exception ex)
            {
                MessageBox.Show("SaveAs failed:\n\n" + ex.Message +
                    "\n\nModel Stripper has been cancelled. Your original file is unchanged.",
                    "SaveAs Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return Result.Cancelled;
            }

            // ════════════════════════════════════════════════════════════════════
            // STEP 3 — Open the COPY and strip it.
            //
            // Opening the copy as a new UIDocument means the original local file's
            // central pointer is never touched. When the user later syncs the
            // original, Revit finds the correct central and there is no corruption.
            // ════════════════════════════════════════════════════════════════════
            Document docToStrip;
            UIDocument uidocNew;

            try
            {
                var openOpts = new OpenOptions { Audit = true };

                if (doc.IsWorkshared && _window.chk_KeepWorkSets.IsChecked == false)
                {
                    openOpts.DetachFromCentralOption = DetachFromCentralOption.DetachAndDiscardWorksets;
                    openOpts.SetOpenWorksetsConfiguration(new WorksetConfiguration());
                }
                else if (doc.IsWorkshared)
                {
                    // Open as new local so we can make changes
                    openOpts.DetachFromCentralOption = DetachFromCentralOption.DoNotDetach;
                }

                ModelPath savedPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(newRevFilPath);
                uidocNew = app.OpenAndActivateDocument(savedPath, openOpts, false);
                docToStrip = uidocNew.Document;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the saved copy for stripping:\n\n" + ex.Message,
                    "Open Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return Result.Failed;
            }

            // ── Strip the copy ────────────────────────────────────────────────
            StripAllViews(uidocNew);

            if (_window.chk_UnlinkRvt.IsChecked == true)
                RemoveTheLinks(uidocNew);

            // ── Save the stripped copy ────────────────────────────────────────
            try
            {
                if (_window.chk_KeepWorkSets.IsChecked == false || !doc.IsWorkshared)
                {
                    // Detached or non-workshared: plain Save
                    docToStrip.Save();
                }
                else
                {
                    // Workshared copy kept as central: sync to commit the strips
                    var syncOpts = new SynchronizeWithCentralOptions();
                    var relinquishOpts = new RelinquishOptions(true);
                    syncOpts.SetRelinquishOptions(relinquishOpts);
                    syncOpts.SaveLocalAfter = true;
                    docToStrip.SynchronizeWithCentral(new TransactWithCentralOptions(), syncOpts);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("The stripped copy could not be saved:\n\n" + ex.Message,
                    "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return Result.Failed;
            }

            // ── Offer to purge ────────────────────────────────────────────────
            PurgeUnused(docToStrip);

            MessageBox.Show(
                $"Done!\n\nStripped model saved to:\n{newRevFilPath}",
                "Model Stripper — Success", MessageBoxButton.OK, MessageBoxImage.Information);

            return Result.Succeeded;
        }

        // ════════════════════════════════════════════════════════════════════════
        // Purge unused elements
        //
        // doc.GetUnusedElements() was introduced in Revit 2024.
        // For Revit 2023 we fall back to a manual prompt.
        //
        // Purging must run in multiple passes: removing a family can free up
        // its materials, which only become purgeable on the next pass. We loop
        // until nothing new is found.
        // ════════════════════════════════════════════════════════════════════════
        private void PurgeUnused(Document doc)
        {
            var result = MessageBox.Show(
                "Would you like to purge unused families and elements from the stripped model?\n\n" +
                "This can significantly reduce file size and is recommended before issuing.\n\n" +
                "Note: purging will run multiple passes until nothing remains.",
                "Purge Unused", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

#if REVIT2024 || REVIT2025 || REVIT2026 || REVIT2027
            // ── Native API purge (Revit 2024+) ───────────────────────────────
            try
            {
                int totalPurged = 0;
                int passCount = 0;
                int maxPasses = 10; // Safety cap — should never need more than ~5

                while (passCount < maxPasses)
                {
                    // Empty HashSet = check all categories
                    ISet<ElementId> unused = doc.GetUnusedElements(new HashSet<ElementId>());

                    if (unused.Count == 0) break;

                    using (var t = new Transaction(doc, $"Purge Unused (pass {passCount + 1})"))
                    {
                        t.Start();
                        ICollection<ElementId> deleted = doc.Delete(unused);
                        totalPurged += deleted.Count;
                        t.Commit();
                    }

                    passCount++;
                }

                MessageBox.Show(
                    $"Purge complete — {totalPurged} element(s) removed in {passCount} pass(es).",
                    "Purge Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Purge failed:\n\n" + ex.Message,
                    "Purge Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
#else
            // ── Revit 2023 fallback — GetUnusedElements not available ─────────
            MessageBox.Show(
                "Automatic purge requires Revit 2024 or later.\n\n" +
                "Please purge manually:\n" +
                "Manage tab → Settings panel → Purge Unused\n\n" +
                "Run it 2–3 times until no more elements are listed.",
                "Manual Purge Required", MessageBoxButton.OK, MessageBoxImage.Information);
#endif
        }

        // ════════════════════════════════════════════════════════════════════════
        // Ownership check
        // Returns a dictionary of  username → owned element count
        // for every element currently checked out by someone other than the
        // current user. An empty dictionary means it is safe to proceed.
        // ════════════════════════════════════════════════════════════════════════
        private Dictionary<string, int> GetElementsOwnedByOthers(Document doc)
        {
            string currentUser = doc.Application.Username;
            var owners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // We sample the view and workset-bearing elements — a full model scan
            // would be too slow, so we check Views, Sheets, Groups, and RevitLinkTypes
            // which are the elements this tool actually tries to delete.
            var elementsToCheck = new FilteredElementCollector(doc)
                .WherePasses(new LogicalOrFilter(new List<ElementFilter>
                {
                    new ElementClassFilter(typeof(Autodesk.Revit.DB.View)),
                    new ElementClassFilter(typeof(RevitLinkType)),
                    new ElementClassFilter(typeof(Group))
                }))
                .ToElementIds();

            foreach (ElementId id in elementsToCheck)
            {
                CheckoutStatus status = WorksharingUtils.GetCheckoutStatus(doc, id);
                if (status == CheckoutStatus.OwnedByOtherUser)
                {
                    WorksharingTooltipInfo info = WorksharingUtils.GetWorksharingTooltipInfo(doc, id);
                    string owner = info.Owner;
                    if (!string.IsNullOrEmpty(owner) &&
                        !owner.Equals(currentUser, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!owners.ContainsKey(owner))
                            owners[owner] = 0;
                        owners[owner]++;
                    }
                }
            }

            return owners;
        }

        // ════════════════════════════════════════════════════════════════════════
        // Strip views / sheets / legends / schedules / templates / groups
        // Operates on the COPY document passed in — never the original.
        // ════════════════════════════════════════════════════════════════════════
        private void StripAllViews(UIDocument uidoc)
        {
            Document doc = uidoc.Document;

            var allViews = new FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.View))
                .Cast<Autodesk.Revit.DB.View>()
                .ToList();

            long activeId = doc.ActiveView.Id.Value;

            // Protect views on the current active sheet (if launched from a sheet)
            var viewsToSave = new HashSet<ElementId>();
            if (doc.ActiveView.ViewType == ViewType.DrawingSheet)
            {
                var viewportIds = new FilteredElementCollector(doc)
                    .OfClass(typeof(Viewport))
                    .Cast<Viewport>()
                    .Where(vp => vp.SheetId == doc.ActiveView.Id)
                    .Select(vp => vp.ViewId);
                foreach (var id in viewportIds)
                    viewsToSave.Add(id);
            }

            var toDelete = new List<ElementId>();

            foreach (var view in allViews)
            {
                if (view.Id.Value == activeId) continue;
                if (viewsToSave.Contains(view.Id)) continue;

                if (!view.IsTemplate)
                {
                    if (_window.chk_StripViews.IsChecked == true &&
                        IsStrippableViewType(view.ViewType))
                        toDelete.Add(view.Id);

                    if (_window.chk_StripSheets.IsChecked == true &&
                        view.ViewType == ViewType.DrawingSheet)
                        toDelete.Add(view.Id);

                    if (_window.chk_StripLegends.IsChecked == true &&
                        view.ViewType == ViewType.Legend)
                        toDelete.Add(view.Id);

                    if (_window.chk_StripSchedules.IsChecked == true &&
                        view.ViewType == ViewType.Schedule)
                    {
                        // Revision schedules share ViewType.Schedule but belong to
                        // OST_RevisionClouds category — always keep them so the
                        // revision history is preserved in the stripped model.
                        bool isRevisionSchedule =
                            view.Category?.Id.Value ==
                            (long)BuiltInCategory.OST_RevisionClouds;

                        if (!isRevisionSchedule)
                            toDelete.Add(view.Id);
                    }
                }
                else
                {
                    if (_window.chk_StripViewTemplates.IsChecked == true)
                        toDelete.Add(view.Id);
                }
            }

            try
            {
                using (var t = new Transaction(doc, "Strip Views"))
                {
                    t.Start();

                    if (toDelete.Count > 0)
                        doc.Delete(toDelete);

                    if (_window.chk_UngroupModels.IsChecked == true)
                    {
                        var groups = new FilteredElementCollector(doc)
                            .OfClass(typeof(Group))
                            .Cast<Group>()
                            .ToList();
                        foreach (var gp in groups)
                            gp.UngroupMembers();
                    }

                    t.Commit();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Strip views error:\n\n" + ex.Message,
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static bool IsStrippableViewType(ViewType vt) =>
            vt == ViewType.AreaPlan ||
            vt == ViewType.CeilingPlan ||
            vt == ViewType.Detail ||
            vt == ViewType.DraftingView ||
            vt == ViewType.Elevation ||
            vt == ViewType.EngineeringPlan ||
            vt == ViewType.FloorPlan ||
            vt == ViewType.Rendering ||
            vt == ViewType.Section ||
            vt == ViewType.ThreeD ||
            vt == ViewType.Walkthrough;

        // ════════════════════════════════════════════════════════════════════════
        // Remove Revit links — operates on the COPY document
        // ════════════════════════════════════════════════════════════════════════
        private void RemoveTheLinks(UIDocument uidoc)
        {
            Document doc = uidoc.Document;
            try
            {
                using (var t = new Transaction(doc, "Delete Revit Links"))
                {
                    t.Start();

                    var ids = new FilteredElementCollector(doc)
                        .OfClass(typeof(RevitLinkType))
                        .Cast<RevitLinkType>()
                        .Where(r => !r.IsNestedLink)
                        .Select(r => r.Id)
                        .ToList();

                    if (ids.Count > 0)
                        doc.Delete(ids);

                    t.Commit();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Remove links error:\n\n" + ex.Message,
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}