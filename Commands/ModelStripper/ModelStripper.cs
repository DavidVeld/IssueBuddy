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

            // ── Guard: model has never been saved ────────────────────────────────
            // With no path on disk there is no original for the tool to protect:
            // Revit's Save() throws ("a file path must already be set"), and the
            // live session would become the stripped copy — so the in-progress work
            // would be stripped rather than a copy of it.
            if (string.IsNullOrEmpty(doc.PathName))
            {
                MessageBox.Show(
                    doc.IsWorkshared
                        ? "This workshared model has never been saved.\n\n" +
                          "Please save (and sync) the file before running Model Stripper."
                        : "This model has never been saved.\n\n" +
                          "Model Stripper works by saving a copy of the file and stripping " +
                          "that copy, so it needs the original to exist on disk first.\n\n" +
                          "Save the model (File → Save As), then run the tool again.",
                    "Unsaved Model", MessageBoxButton.OK, MessageBoxImage.Warning);
                return Result.Cancelled;
            }

            // ── Guard: the active view must anchor a 3D view ─────────────────────
            // Revit refuses any delete that would leave the project with no open
            // views ("Deleting all open views in a project is not allowed"), and a
            // model with no views is useless to whoever receives it. So the tool
            // only runs from a 3D view, or from a sheet with a 3D view placed on
            // it — that 3D view becomes the anchor and is never deleted.
            if (!TryResolveAnchor(doc, out ElementId anchor3DId, out ElementId anchorSheetId))
            {
                MessageBox.Show(
                    "Model Stripper needs a 3D view to keep in the stripped model.\n\n" +
                    "Please make one of these the active view and run the tool again:\n" +
                    "  • a 3D view, or\n" +
                    "  • a sheet with a 3D view placed on it\n\n" +
                    "Everything you tick in the dialog is still removed — the 3D view " +
                    "(and its sheet, if you launch from one) is the only thing kept back.",
                    "Open a 3D View First", MessageBoxButton.OK, MessageBoxImage.Warning);
                return Result.Cancelled;
            }

            // UniqueIds survive SaveAs, ElementIds are only guaranteed within a
            // document — so carry the anchor across to the copy by UniqueId.
            string anchor3DUniqueId = doc.GetElement(anchor3DId).UniqueId;
            string? anchorSheetUniqueId = anchorSheetId == ElementId.InvalidElementId
                ? null
                : doc.GetElement(anchorSheetId).UniqueId;

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

            // ── Re-establish the anchor in the COPY ───────────────────────────
            // The copy comes up with whatever views Revit restores from the saved
            // state, which is not necessarily the view we validated above. Look the
            // anchor up by UniqueId, make it active, and close every other open
            // view — that way the delete set can never cover all open views.
            ElementId copyAnchor3DId = FindByUniqueId(docToStrip, anchor3DUniqueId);
            ElementId copyAnchorSheetId = FindByUniqueId(docToStrip, anchorSheetUniqueId);

            if (copyAnchor3DId == ElementId.InvalidElementId)
            {
                // Fall back to any 3D view in the copy rather than strip blind.
                copyAnchor3DId = new FilteredElementCollector(docToStrip)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .Where(v => !v.IsTemplate)
                    .Select(v => v.Id)
                    .FirstOrDefault() ?? ElementId.InvalidElementId;
                copyAnchorSheetId = ElementId.InvalidElementId;
            }

            if (copyAnchor3DId == ElementId.InvalidElementId)
            {
                MessageBox.Show(
                    "The saved copy contains no 3D view to keep, so stripping was skipped.\n\n" +
                    "An unstripped copy has been created at:\n" + newRevFilPath + "\n\n" +
                    "Nothing was deleted.",
                    "Nothing Stripped", MessageBoxButton.OK, MessageBoxImage.Warning);
                return Result.Cancelled;
            }

            IsolateAnchorView(uidocNew,
                copyAnchorSheetId != ElementId.InvalidElementId ? copyAnchorSheetId : copyAnchor3DId);

            // ── Strip the copy ────────────────────────────────────────────────
            StripAllViews(uidocNew, copyAnchor3DId, copyAnchorSheetId);

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
        // Resolve the anchor — the 3D view that must survive the strip.
        //
        // Valid starting points are a 3D view, or a sheet with a 3D view placed on
        // it. Returns false when the active view is neither, in which case the tool
        // must not run: without an anchor the strip could empty the model of views.
        // ════════════════════════════════════════════════════════════════════════
        private static bool TryResolveAnchor(Document doc, out ElementId threeDViewId, out ElementId sheetId)
        {
            threeDViewId = ElementId.InvalidElementId;
            sheetId = ElementId.InvalidElementId;

            var active = doc.ActiveView;
            if (active == null || active.IsTemplate)
                return false;

            if (active.ViewType == ViewType.ThreeD)
            {
                threeDViewId = active.Id;
                return true;
            }

            if (active.ViewType == ViewType.DrawingSheet)
            {
                var placed = new FilteredElementCollector(doc)
                    .OfClass(typeof(Viewport))
                    .Cast<Viewport>()
                    .Where(vp => vp.SheetId == active.Id)
                    .Select(vp => doc.GetElement(vp.ViewId) as Autodesk.Revit.DB.View)
                    .FirstOrDefault(v => v != null && v.ViewType == ViewType.ThreeD);

                if (placed != null)
                {
                    threeDViewId = placed.Id;
                    sheetId = active.Id;
                    return true;
                }
            }

            return false;
        }

        // Look an element up by UniqueId. Returns InvalidElementId when absent.
        private static ElementId FindByUniqueId(Document doc, string? uniqueId)
        {
            if (string.IsNullOrEmpty(uniqueId))
                return ElementId.InvalidElementId;

            try
            {
                return doc.GetElement(uniqueId)?.Id ?? ElementId.InvalidElementId;
            }
            catch
            {
                return ElementId.InvalidElementId;
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // Leave exactly one view open in the copy — the anchor.
        //
        // Revit throws "Deleting all open views in a project is not allowed" when a
        // delete would close every open view. Whatever tabs Revit restored when it
        // reopened the copy, closing them down to the anchor removes that risk.
        // ════════════════════════════════════════════════════════════════════════
        private static void IsolateAnchorView(UIDocument uidoc, ElementId anchorId)
        {
            try
            {
                if (!(uidoc.Document.GetElement(anchorId) is Autodesk.Revit.DB.View anchor))
                    return;

                if (uidoc.ActiveView == null || uidoc.ActiveView.Id != anchor.Id)
                    uidoc.ActiveView = anchor;

                foreach (var uiView in uidoc.GetOpenUIViews())
                {
                    if (uiView.ViewId != anchor.Id)
                        uiView.Close();
                }
            }
            catch
            {
                // Not fatal — the protected set in StripAllViews still keeps the
                // anchor alive, this is only here to keep Revit's own guard quiet.
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // Add a view to the protected set, walking up the dependency chain.
        // A dependent view is deleted along with its primary, so protecting a
        // dependent means protecting every primary above it too.
        // ════════════════════════════════════════════════════════════════════════
        private static void ProtectView(Document doc, ElementId? id, HashSet<ElementId> protectedIds)
        {
            if (id == null || id == ElementId.InvalidElementId) return;
            if (!protectedIds.Add(id)) return;

            if (doc.GetElement(id) is Autodesk.Revit.DB.View view)
            {
                ElementId primary = view.GetPrimaryViewId();
                if (primary != null && primary != ElementId.InvalidElementId)
                    ProtectView(doc, primary, protectedIds);
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // Strip views / sheets / legends / schedules / templates / groups
        // Operates on the COPY document passed in — never the original.
        //
        // anchor3DId is the 3D view that must survive; anchorSheetId is the sheet
        // it sits on when the tool was launched from a sheet, otherwise
        // InvalidElementId. Neither is ever deleted.
        // ════════════════════════════════════════════════════════════════════════
        private void StripAllViews(UIDocument uidoc, ElementId anchor3DId, ElementId anchorSheetId)
        {
            Document doc = uidoc.Document;

            var allViews = new FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.View))
                .Cast<Autodesk.Revit.DB.View>()
                .ToList();

            // ── Build the protected set ───────────────────────────────────────
            // Nothing in here is deleted, whatever the user ticked. The anchor 3D
            // view guarantees the stripped model always has at least one view.
            var protectedIds = new HashSet<ElementId>();
            ProtectView(doc, anchor3DId, protectedIds);
            ProtectView(doc, doc.ActiveView?.Id, protectedIds);

            if (anchorSheetId != ElementId.InvalidElementId)
            {
                // Keep the launch sheet and everything placed on it, so the sheet
                // does not come out of the strip as an empty title block.
                ProtectView(doc, anchorSheetId, protectedIds);

                var placedIds = new FilteredElementCollector(doc)
                    .OfClass(typeof(Viewport))
                    .Cast<Viewport>()
                    .Where(vp => vp.SheetId == anchorSheetId)
                    .Select(vp => vp.ViewId);

                foreach (var id in placedIds)
                    ProtectView(doc, id, protectedIds);
            }

            var toDelete = new List<ElementId>();

            foreach (var view in allViews)
            {
                if (protectedIds.Contains(view.Id)) continue;

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

            // ── Final safety net ──────────────────────────────────────────────
            // ExceptWith is the belt to the protected-set braces: even if a future
            // change adds an id twice or misses a guard above, nothing protected can
            // reach doc.Delete.
            var deleteSet = new HashSet<ElementId>(toDelete);
            deleteSet.ExceptWith(protectedIds);

            bool anchorSurvives = allViews.Any(v => v.Id == anchor3DId)
                                  && !deleteSet.Contains(anchor3DId);

            if (!anchorSurvives)
            {
                MessageBox.Show(
                    "Stripping was abandoned because the 3D view that should be kept " +
                    "could no longer be found in the copy.\n\n" +
                    "No views have been deleted — the copy has been left unstripped.",
                    "Strip Abandoned", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                using (var t = new Transaction(doc, "Strip Views"))
                {
                    t.Start();

                    if (deleteSet.Count > 0)
                        doc.Delete(deleteSet.ToList());

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