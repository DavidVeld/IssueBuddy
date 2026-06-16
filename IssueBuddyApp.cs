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

        // ─── Cached paths (resolved once) ─────────────────────────────────────
        private static readonly string AssemblyPath =
            Assembly.GetExecutingAssembly().Location;

        private static readonly string AssemblyDir =
            Path.GetDirectoryName(AssemblyPath) ?? string.Empty;

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

                AddButton(panel, "CreatePrintSet", "CreatePrintSet",
                    "IssueBuddy.CreatePrintSet", "revset.png");

                AddButton(panel, "PDFSplitter", "PDFSplitter",
                    "IssueBuddy.PDFSplitterCommand", "pdfsplitter.png");

                AddButton(panel, "ModelStripper", "ModelStripper",
                    "IssueBuddy.ModelStripper", "packmodel.png");

                AddButton(panel, "RevisionViewer", "RevisionViewer",
    "IssueBuddy.RevisionViewerCommand", "revviewer.png");




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
        /// Adds a push button to a panel and applies its icon if one loads.
        /// Never throws on a missing/bad image — the button is still created.
        /// </summary>
        private static PushButton AddButton(
            RibbonPanel panel,
            string internalName,
            string displayText,
            string commandClass,
            string imageFile,
            string tooltip = null)
        {
            var data = new PushButtonData(internalName, displayText, AssemblyPath, commandClass);
            if (!string.IsNullOrEmpty(tooltip))
                data.ToolTip = tooltip;

            var button = panel.AddItem(data) as PushButton;

            BitmapImage image = TryLoadImage(imageFile);
            if (button != null && image != null)
                button.LargeImage = image;

            return button;
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