using Autodesk.Revit.UI;
using IssueBuddy.Common;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace IssueBuddy
{
    /// <summary>
    /// IssueBuddy – Revit External Application entry point.
    ///
    /// Registered in IssueBuddy.addin via:
    ///   &lt;FullClassName&gt;IssueBuddy.IssueBuddy&lt;/FullClassName&gt;
    ///
    /// Design note: OnStartup is deliberately a thin orchestrator. Anything that
    /// can fail for non-fatal reasons (missing icons, context-menu registration)
    /// is isolated so it logs and continues instead of aborting the whole add-in.
    /// </summary>
    public class IssueBuddyApp : Autodesk.Revit.UI.IExternalApplication
    {
        // ─── Constants ────────────────────────────────────────────────────────
        private const string PanelName = "Issue Buddy";
        private const string ImageSubFolder = "img";

        // The F1 help document, shipped alongside the assembly. The name is fixed
        // by the Autodesk App Store listing — see the csproj Content entry.
        private const string HelpFileName = "ReadMe - issueTools.pdf";

        // ─── Cached paths (resolved once) ─────────────────────────────────────
        private static readonly string AssemblyPath =
            Assembly.GetExecutingAssembly().Location;

        private static readonly string AssemblyDir =
            Path.GetDirectoryName(AssemblyPath) ?? string.Empty;

        private static readonly string HelpFilePath =
            Path.Combine(AssemblyDir, HelpFileName);

        // ─────────────────────────────────────────────────────────────────────
        // OnStartup
        // ─────────────────────────────────────────────────────────────────────
        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                RibbonPanel panel = GetOrCreatePanel(application, PanelName);

                // Core buttons. Each loads its icon safely — a missing image
                // leaves the button without an icon rather than failing startup.
                // Every button gets a tooltip, a long description, and F1
                // contextual help pointing at the shipped ReadMe PDF.

                AddButton(panel, "CreatePrintSet", "CreatePrintSet",
                    "IssueBuddy.CreatePrintSet", "revset.png",
                    "Create a print set from the sheets on selected revisions.",
                    "Builds a named Revit print set (view/sheet set) from every sheet " +
                    "carrying the revision(s) you pick, so an issue can be printed or " +
                    "exported without hand-picking sheets.");

                AddButton(panel, "PDFSplitter", "PDFSplitter",
                    "IssueBuddy.PDFSplitterCommand", "pdfsplitter.png",
                    "Split a multi-page PDF into one file per sheet.",
                    "Splits a printed PDF into single-sheet files and names each one " +
                    "from a template you build out of Revit sheet and Project " +
                    "Information parameters.");

                AddButton(panel, "ModelStripper", "ModelStripper",
                    "IssueBuddy.ModelStripper", "packmodel.png",
                    "Save a stripped copy of the model for issue.",
                    "Saves a copy of the current model, then removes the views, sheets, " +
                    "legends, schedules and view templates you tick, optionally " +
                    "unloading Revit links and ungrouping model groups. The original " +
                    "model is never modified. Run it from a 3D view, or from a sheet " +
                    "with a 3D view on it — that view is kept in the stripped copy.");

                AddButton(panel, "RevisionViewer", "RevisionViewer",
                    "IssueBuddy.RevisionViewerCommand", "revviewer.png",
                    "Review the sheet issue and revision history.",
                    "Lists every sheet against its revisions so the issue history can " +
                    "be checked in one place, and exports the table to CSV.");


#if REVIT2025 || REVIT2026 || REVIT2027
                // Optional 2025+ feature. Failures here must NOT fail the add-in.
                TryRegisterContextMenuFeature(application, panel);
#endif

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                // Only genuinely fatal problems (e.g. panel creation) land here.
                TaskDialog.Show("IssueBuddy – Startup Error", ex.Message);
                return Result.Failed;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // OnShutdown
        // ─────────────────────────────────────────────────────────────────────
        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                // Persist any settings changes made during the session
                Settings.Instance.Save();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[IssueBuddy] Shutdown error: {ex.Message}");
                return Result.Failed;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the named panel on the Add-Ins tab, creating it if absent.
        /// Avoids the "panel already exists" exception and keeps every button
        /// (including the 2025+ context-menu button) on a single panel.
        /// </summary>
        private static RibbonPanel GetOrCreatePanel(
            UIControlledApplication application, string panelName)
        {
            RibbonPanel existing = application
                .GetRibbonPanels()                       // Add-Ins tab
                .FirstOrDefault(p => p.Name == panelName);

            return existing ?? application.CreateRibbonPanel(panelName);
        }

        /// <summary>
        /// Adds a push button to a panel, applies its icon if one loads, and wires
        /// up the tooltip, long description and F1 contextual help.
        /// Never throws on a missing/bad image — the button is still created.
        /// </summary>
        private static PushButton AddButton(
            RibbonPanel panel,
            string internalName,
            string displayText,
            string commandClass,
            string imageFile,
            string? tooltip = null,
            string? longDescription = null)
        {
            var data = new PushButtonData(internalName, displayText, AssemblyPath, commandClass);
            if (!string.IsNullOrEmpty(tooltip))
                data.ToolTip = tooltip;

            // Shown in the expanded tooltip, below the separator.
            if (!string.IsNullOrEmpty(longDescription))
                data.LongDescription = longDescription;

            var button = panel.AddItem(data) as PushButton;

            BitmapImage image = TryLoadImage(imageFile);
            if (button != null && image != null)
                button.LargeImage = image;

            if (button != null)
                TrySetContextualHelp(button);

            return button;
        }

        /// <summary>
        /// Attaches F1 contextual help to a button, opening the ReadMe PDF that
        /// ships beside the assembly. Revit then shows the "Press F1 for more help"
        /// line on the button's tooltip and launches the file when F1 is pressed.
        ///
        /// Optional feature: a missing PDF or an older API must not fail startup,
        /// so failures are logged and the button is left without help rather than
        /// advertising help that cannot open.
        /// </summary>
        private static void TrySetContextualHelp(PushButton button)
        {
            try
            {
                if (!File.Exists(HelpFilePath))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[IssueBuddy] Help file not found: {HelpFilePath}");
                    return;
                }

                // ContextualHelpType.Url covers local documents as well as web
                // URLs — Revit shell-launches the path, so the PDF opens in the
                // user's default reader.
                button.SetContextualHelp(
                    new ContextualHelp(ContextualHelpType.Url, HelpFilePath));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[IssueBuddy] Failed to set contextual help on " +
                    $"'{button.Name}': {ex.Message}");
            }
        }

        /// <summary>
        /// Loads an icon from the add-in's "img" folder. Returns null (and logs)
        /// if the file is missing or invalid, so the caller can carry on.
        /// Uses OnLoad + Freeze to release the file handle and stay thread-safe.
        /// </summary>
        private static BitmapImage TryLoadImage(string fileName)
        {
            try
            {
                string fullPath = Path.Combine(AssemblyDir, ImageSubFolder, fileName);

                if (!File.Exists(fullPath))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[IssueBuddy] Image not found: {fullPath}");
                    return null;
                }

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(fullPath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[IssueBuddy] Failed to load image '{fileName}': {ex.Message}");
                return null;
            }
        }

#if REVIT2025 || REVIT2026 || REVIT2027
        /// <summary>
        /// Registers the right-click context menu (views/sheets, incl. Project
        /// Browser) and the always-available "Create Print Set From Selection"
        /// button. Optional feature: any failure is logged, not propagated.
        /// </summary>
        private static void TryRegisterContextMenuFeature(
            UIControlledApplication application, RibbonPanel panel)
        {
            try
            {
                application.RegisterContextMenu(
                    Assembly.GetExecutingAssembly().GetName().Name,
                    new PrintSetContextMenuCreator());
                /*
                var data = new PushButtonData(
                    "CreatePrintSetFromSelection",
                    "Create Print Set\nFrom Selection",
                    AssemblyPath,
                    typeof(CreatePrintSetCommand).FullName)
                {
                    ToolTip = "Creates a print set from selection."
                };

                panel.AddItem(data);

                BitmapImage image = TryLoadImage("revset.png");
                if (data != null && image != null)
                    data.LargeImage = image;
                */
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[IssueBuddy] Context menu registration failed: {ex.Message}");
            }
        }
#endif
    }
}