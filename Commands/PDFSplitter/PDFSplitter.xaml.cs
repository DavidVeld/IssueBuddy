using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Path = System.IO.Path;
using View = Autodesk.Revit.DB.View;
using Visibility = System.Windows.Visibility;

namespace IssueBuddy
{
    /// <summary>
    /// Interaction logic for PDFSplitter.xaml
    ///
    /// Splits a printed PDF binder into one PDF per sheet, names each file
    /// from a user-defined parameter template, and optionally exports DWGs
    /// of the same sheets (plus their plan views) in one pass.
    ///
    /// Ported from DV_PDFSplitter.wndSplitter. Key changes:
    ///  - Template save/load rebuilt: one ObservableCollection bound once and
    ///    mutated in place (fixes the "save needs a restart" bug).
    ///  - Two-pass split: all file names are computed and validated BEFORE
    ///    anything is written (blank names, duplicates, overwrites).
    ///  - File names sanitised; Path.Combine everywhere.
    ///  - Fixed broken validation (null-check short-circuit bug in old code).
    ///  - Progress bar + UI lockout while running.
    /// </summary>
    public partial class PDFSplitter : Window
    {
        // ── Revit context ──────────────────────────────────────────────────
        private readonly UIApplication app;
        private readonly UIDocument uidoc;
        private readonly Document doc;

        // ── Parameters available for templates ─────────────────────────────
        private readonly List<ParameterClass> parameterlist;

        /// <summary>
        /// The template currently being edited.
        /// IMPORTANT: this instance is created once and only ever mutated
        /// (Clear/Add/Move/RemoveAt). It is bound to lib_name a single time
        /// in Window_Loaded. Replacing the instance was the root cause of
        /// the old save/reload bug — don't reintroduce it.
        /// </summary>
        private readonly ObservableCollection<ParameterClass> selectedNameTemplate
            = new ObservableCollection<ParameterClass>();

        /// <summary>All saved presets (in-memory source of truth).</summary>
        private FileNameTemplateCollection templateStore;

        /// <summary>Suppresses SelectionChanged while the combo is repopulated.</summary>
        private bool suppressTemplateEvents;

        // ── Sheets / sheet sets ────────────────────────────────────────────
        private IList<ViewSheetSet> availableViewSheetSets = new List<ViewSheetSet>();
        private IList<ViewSheet> selectedSheetsInSet = new List<ViewSheet>();
        private readonly ViewSheet activeViewSheetSample;

        // ── PDF input ──────────────────────────────────────────────────────
        private PdfDocument inputDocument;
        private string pdfPath = "";

        private bool isRunning;

        private const string LastUsedSetLabel = "<Last Used>";

        public PDFSplitter(List<ParameterClass> Parameterlist, ViewSheet activeViewSheet, UIApplication thisApp)
        {
            InitializeComponent();

            parameterlist = Parameterlist ?? new List<ParameterClass>();
            activeViewSheetSample = activeViewSheet;

            app = thisApp;
            uidoc = thisApp.ActiveUIDocument;
            doc = uidoc.Document;
        }

        // ════════════════════════════════════════════════════════════════════
        //  Window setup
        // ════════════════════════════════════════════════════════════════════

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                PopulatePrintSets();
                PopulateExportSetups();
                PopulateSeparators();

                // Available parameters (sorted for findability)
                lib_Params.ItemsSource = parameterlist
                    .OrderBy(p => p.TypeName)
                    .ThenBy(p => p.Name)
                    .ToList();

                // Bind the template editor ONCE; afterwards only mutate it.
                lib_name.ItemsSource = selectedNameTemplate;

                // Saved presets
                templateStore = FileNameTemplateCollection.Load();
                RepopulateTemplateCombo(templateStore.LastUsed);

                UpdateReadiness();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "PDF Splitter");
            }
        }

        private void PopulatePrintSets()
        {
            availableViewSheetSets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheetSet))
                .Cast<ViewSheetSet>()
                .OrderBy(s => s.Name)
                .ToList();

            cbb_PrintSet.Items.Add(LastUsedSetLabel);
            foreach (ViewSheetSet set in availableViewSheetSets)
                cbb_PrintSet.Items.Add(set.Name);
        }

        private void PopulateExportSetups()
        {
            IList<string> setupNames = BaseExportOptions.GetPredefinedSetupNames(doc);
            foreach (string name in setupNames)
                cbb_ExportSettings.Items.Add(name);

            if (setupNames.Count > 0)
            {
                cbb_ExportSettings.SelectedIndex = 0;
            }
            else
            {
                // No predefined DWG export setup → DWG export cannot work.
                // Disable the whole DWG section and tell the user how to fix it.
                chx_GenerateDWGS.IsChecked = false;
                chx_GenerateDWGS.IsEnabled = false;
                chx_GenerateDWGS.Content = "Export DWGs  (no DWG export setup found)";

                cbb_ExportSettings.IsEnabled = false;
                cbb_ExportSettings.ToolTip =
                    "No predefined DWG export setup exists in this project.";

                // Defer slightly so the window is fully shown before the dialog.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    System.Windows.MessageBox.Show(this,
                        "This project has no predefined DWG export setups, so the " +
                        "PDF Splitter cannot export DWGs.\n\n" +
                        "If you need DWGs:\n" +
                        "  1. Close this window.\n" +
                        "  2. In Revit:  File  →  Export  →  CAD Formats  →  DWG.\n" +
                        "  3. Click the '…' next to Select Export Setup, add and save a setup, then Cancel out.\n" +
                        "  4. Re-open the PDF Splitter.\n\n" +
                        "You can still split PDFs without exporting DWGs.",
                        "PDF Splitter — no DWG export setup",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }), DispatcherPriority.ContextIdle);
            }
        }

        private void PopulateSeparators()
        {
            cbb_Separator.ItemsSource = new List<string>
            {
                "-", "_", "x", " ", " - ", " _ ", " x "
            };
            cbb_Separator.Text = "-";
        }

        // ════════════════════════════════════════════════════════════════════
        //  Template presets — save / load / delete
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Rebuilds the preset dropdown from the in-memory store and
        /// optionally selects (and loads) one preset — all without firing
        /// selection events halfway through.
        /// </summary>
        private void RepopulateTemplateCombo(string selectName)
        {
            suppressTemplateEvents = true;
            try
            {
                cbb_Templates.Items.Clear();
                foreach (FileNameTemplate t in templateStore.Templates.OrderBy(t => t.Name))
                    cbb_Templates.Items.Add(t.Name);

                if (!string.IsNullOrWhiteSpace(selectName) && templateStore.Exists(selectName))
                {
                    cbb_Templates.SelectedItem = templateStore.Get(selectName).Name;
                    LoadTemplateIntoEditor(selectName);
                }
                else
                {
                    cbb_Templates.SelectedIndex = -1;
                    cbb_Templates.Text = "";
                }
            }
            finally
            {
                suppressTemplateEvents = false;
            }
        }

        /// <summary>
        /// Copies a saved preset into the live editor collection
        /// (in place — the bound instance is never replaced).
        /// </summary>
        private void LoadTemplateIntoEditor(string name)
        {
            FileNameTemplate template = templateStore.Get(name);

            selectedNameTemplate.Clear();
            if (template != null)
            {
                foreach (ParameterClass item in template.Items)
                    selectedNameTemplate.Add(item.Clone());
            }

            RefreshSample();
        }

        private void cbb_Templates_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressTemplateEvents) return;

            string name = cbb_Templates.SelectedItem as string;
            if (!string.IsNullOrWhiteSpace(name))
            {
                LoadTemplateIntoEditor(name);
                templateStore.LastUsed = name;
            }
        }

        private void btn_SaveTemplate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string name = cbb_Templates.Text != null ? cbb_Templates.Text.Trim() : "";

                if (name == "")
                {
                    System.Windows.MessageBox.Show(
                        "Type a name for the preset in the box first.",
                        "Save Preset", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (selectedNameTemplate.Count == 0)
                {
                    System.Windows.MessageBox.Show(
                        "The template is empty — add at least one parameter before saving.",
                        "Save Preset", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (templateStore.Exists(name))
                {
                    MessageBoxResult overwrite = System.Windows.MessageBox.Show(
                        "A preset called '" + name + "' already exists.\n\nOverwrite it?",
                        "Save Preset", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (overwrite != MessageBoxResult.Yes) return;
                }

                string error;
                if (templateStore.AddOrUpdate(name, selectedNameTemplate, out error))
                {
                    // The in-memory store is already up to date — just refresh
                    // the dropdown and reselect. No file re-read, no restart.
                    RepopulateTemplateCombo(name);
                    SetStatus("Preset '" + name + "' saved.");
                }
                else
                {
                    System.Windows.MessageBox.Show(
                        "Could not save the preset:\n" + error,
                        "Save Preset", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "Save Preset");
            }
        }

        private void btn_Delete_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string name = cbb_Templates.Text != null ? cbb_Templates.Text.Trim() : "";
                if (name == "" || !templateStore.Exists(name)) return;

                MessageBoxResult confirm = System.Windows.MessageBox.Show(
                    "Delete preset '" + name + "'?",
                    "Delete Preset", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes) return;

                string error;
                if (templateStore.Delete(name, out error))
                {
                    selectedNameTemplate.Clear();
                    RepopulateTemplateCombo(null);
                    RefreshSample();
                    SetStatus("Preset '" + name + "' deleted.");
                }
                else
                {
                    System.Windows.MessageBox.Show(error, "Delete Preset");
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "Delete Preset");
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  Template editing
        // ════════════════════════════════════════════════════════════════════

        private void btn_AddParameter_Click(object sender, RoutedEventArgs e)
        {
            AddSelectedParameter();
        }

        private void btn_AddParameter_Click(object sender, MouseButtonEventArgs e)
        {
            AddSelectedParameter();
        }

        private void AddSelectedParameter()
        {
            ParameterClass selectedParam = lib_Params.SelectedItem as ParameterClass;
            if (selectedParam != null)
            {
                // Clone — the template must own its items.
                selectedNameTemplate.Add(selectedParam.Clone());
                RefreshSample();
            }
        }

        private void btn_AddSeparator_Click(object sender, RoutedEventArgs e)
        {
            string sepValue = cbb_Separator.Text;
            if (string.IsNullOrEmpty(sepValue)) return;

            selectedNameTemplate.Add(new ParameterClass(sepValue, "Separator"));
            RefreshSample();
        }

        private void btn_ItemUp_Click(object sender, RoutedEventArgs e)
        {
            int idx = lib_name.SelectedIndex;
            if (idx > 0)
            {
                selectedNameTemplate.Move(idx, idx - 1);
                lib_name.SelectedIndex = idx - 1;   // keep the moved item selected
            }
            RefreshSample();
        }

        private void btn_ItemDown_Click(object sender, RoutedEventArgs e)
        {
            int idx = lib_name.SelectedIndex;
            if (idx >= 0 && idx < selectedNameTemplate.Count - 1)
            {
                selectedNameTemplate.Move(idx, idx + 1);
                lib_name.SelectedIndex = idx + 1;
            }
            RefreshSample();
        }

        private void btn_ItemDel_Click(object sender, RoutedEventArgs e)
        {
            int idx = lib_name.SelectedIndex;
            if (idx >= 0)
            {
                selectedNameTemplate.RemoveAt(idx);
                if (selectedNameTemplate.Count > 0)
                    lib_name.SelectedIndex = Math.Min(idx, selectedNameTemplate.Count - 1);
            }
            RefreshSample();
        }

        private void RefreshSample()
        {
            ViewSheet sampleSheet = selectedSheetsInSet.FirstOrDefault() ?? activeViewSheetSample;

            string fileName = "";
            if (sampleSheet != null && selectedNameTemplate.Count > 0)
            {
                fileName = ParameterHelperClass.getFileName(
                    new List<ParameterClass>(selectedNameTemplate), sampleSheet, app);
            }

            lbl_sample.Text = fileName == "" ? "(add parameters to preview)" : fileName + ".pdf";
        }

        // ════════════════════════════════════════════════════════════════════
        //  Inputs — PDF, DWG path, print set
        // ════════════════════════════════════════════════════════════════════

        private void btn_getPDF_Click(object sender, RoutedEventArgs e)
        {
            BrowseForPdf();
        }

        private void brd_DropZone_Click(object sender, MouseButtonEventArgs e)
        {
            BrowseForPdf();
        }

        private void BrowseForPdf()
        {
            try
            {
                Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select the PDF binder to split",
                    Filter = "PDF files (*.pdf)|*.pdf",
                    CheckFileExists = true
                };

                if (dialog.ShowDialog(this) == true)
                    AcceptPDFFile(dialog.FileName);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "PDF Splitter");
            }
        }

        private void img_PDF_Drop(object sender, System.Windows.DragEventArgs e)
        {
            SetDropZoneState(false);

            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                    AcceptPDFFile(files[0]);
            }
        }

        private void brd_DropZone_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            bool isPdf = false;
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
                isPdf = files != null && files.Length > 0 &&
                        string.Equals(Path.GetExtension(files[0]), ".pdf", StringComparison.OrdinalIgnoreCase);
            }

            e.Effects = isPdf ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
            e.Handled = true;
            SetDropZoneState(isPdf);
        }

        private void brd_DropZone_DragLeave(object sender, System.Windows.DragEventArgs e)
        {
            SetDropZoneState(false);
        }

        private void SetDropZoneState(bool highlight)
        {
            brd_DropZone.BorderBrush = highlight
                ? (Brush)FindResource("PrimaryAccentBrush")
                : (Brush)FindResource(inputDocument != null ? "BorderPinkBrush" : "ControlBorderBrush");
        }

        private void AcceptPDFFile(string fileName)
        {
            try
            {
                if (!string.Equals(Path.GetExtension(fileName), ".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    System.Windows.MessageBox.Show(
                        "That doesn't look like a PDF file — please drop or select a .pdf.",
                        "PDF Splitter", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (!File.Exists(fileName))
                {
                    System.Windows.MessageBox.Show("File not found:\n" + fileName, "PDF Splitter");
                    return;
                }

                PdfDocument document = PdfReader.Open(fileName, PdfDocumentOpenMode.Import);

                // Only commit once the file opened cleanly.
                inputDocument = document;
                pdfPath = fileName;

                txt_PDFPath.Text = pdfPath;
                txt_PDFPath.ToolTip = pdfPath;
                txt_PDFCount.Text = inputDocument.PageCount + " pg";

                txt_DropIcon.Foreground = (Brush)FindResource("PrimaryAccentBrush");
                txt_DropHint.Text = Path.GetFileName(pdfPath);
                SetDropZoneState(false);

                UpdateReadiness();
            }
            catch (Exception ex)
            {
                // Typically: password protected / corrupt / unsupported PDF.
                System.Windows.MessageBox.Show(
                    "Could not open the PDF:\n" + ex.Message +
                    "\n\nIf the file is password protected or corrupt, re-print it from Revit and try again.",
                    "PDF Splitter", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void btn_getDWG_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string initial = "";
                if (!string.IsNullOrEmpty(pdfPath))
                    initial = Path.GetDirectoryName(pdfPath);

                string folder = FolderPicker.Show(this, "Select the DWG export folder", initial);

                if (!string.IsNullOrWhiteSpace(folder))
                {
                    txt_DWGPath.Text = folder;
                    txt_DWGPath.ToolTip = folder;
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "PDF Splitter");
            }
        }

        private void cbb_PrintSet_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string name = cbb_PrintSet.SelectedItem as string;
            if (string.IsNullOrEmpty(name)) return;

            try
            {
                ViewSheetSet selectedSet = GetSelectedViewSheetSet(name);

                selectedSheetsInSet = new List<ViewSheet>();
                if (selectedSet != null)
                {
                    foreach (View v in selectedSet.OrderedViewList)
                    {
                        ViewSheet vs = v as ViewSheet;
                        if (vs != null)
                            selectedSheetsInSet.Add(vs);
                    }
                }

                txt_PrintSetCount.Text = selectedSheetsInSet.Count + " sh";
                RefreshSample();
                UpdateReadiness();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "PDF Splitter");
            }
        }

        private ViewSheetSet GetSelectedViewSheetSet(string name)
        {
            if (name == LastUsedSetLabel)
            {
                PrintManager printManager = doc.PrintManager;
                return printManager.ViewSheetSetting.CurrentViewSheetSet as ViewSheetSet;
            }

            return availableViewSheetSets.FirstOrDefault(s => s.Name == name);
        }

        private void chx_GenerateDWGS_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;   // fires during InitializeComponent

            bool enabled = chx_GenerateDWGS.IsChecked == true;
            chx_GenerateDWGViews.IsEnabled = enabled;
            chx_NoXrefs.IsEnabled = enabled;
            chx_TrueColourExport.IsEnabled = enabled;
            chx_DeletePcp.IsEnabled = enabled;
            // chx_SeparateFolders applies to PDFs too — leave it always enabled.
            txt_DWGPath.IsEnabled = enabled;
            btn_getDWGs.IsEnabled = enabled;
            cbb_ExportSettings.IsEnabled = enabled;
        }

        // ════════════════════════════════════════════════════════════════════
        //  Readiness / status
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Central gate: Split is only enabled when a PDF is loaded, a print
        /// set is selected, and the page count matches the sheet count.
        /// The status line always says what is still missing.
        /// </summary>
        private void UpdateReadiness()
        {
            bool hasPdf = inputDocument != null;
            bool hasSet = selectedSheetsInSet.Count > 0;
            bool countsMatch = hasPdf && hasSet &&
                               inputDocument.PageCount == selectedSheetsInSet.Count;

            // Colour-code the two counters so a mismatch is obvious at a glance.
            Brush ok = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x32));   // green
            Brush bad = (Brush)FindResource("PrimaryAccentBrush");

            if (hasPdf && hasSet)
            {
                txt_PDFCount.Foreground = countsMatch ? ok : bad;
                txt_PrintSetCount.Foreground = countsMatch ? ok : bad;
            }

            btn_Split.IsEnabled = countsMatch && !isRunning;

            if (!hasPdf)
                SetStatus("Select or drop the PDF binder to split.");
            else if (!hasSet)
                SetStatus("Select the Print Set that was used to print the PDF.");
            else if (!countsMatch)
                SetStatus("Mismatch: PDF has " + inputDocument.PageCount +
                          " pages but the set has " + selectedSheetsInSet.Count +
                          " sheets. Pick the matching set or re-print the binder.");
            else
                SetStatus("Ready — " + inputDocument.PageCount + " sheets will be created.");
        }

        private void SetStatus(string message)
        {
            lbl_Status.Text = message;
        }

        /// <summary>WPF-native replacement for WinForms DoEvents.</summary>
        private static void DoEvents()
        {
            DispatcherFrame frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        // ════════════════════════════════════════════════════════════════════
        //  Split
        // ════════════════════════════════════════════════════════════════════

        private void btn_Split_Click(object sender, RoutedEventArgs e)
        {
            if (isRunning) return;

            // ── Guards ──────────────────────────────────────────────────────
            if (inputDocument == null || selectedSheetsInSet.Count == 0) return;

            if (inputDocument.PageCount != selectedSheetsInSet.Count)
            {
                System.Windows.MessageBox.Show(
                    "The PDF page count does not match the selected sheet set.",
                    "PDF Splitter", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (selectedNameTemplate.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    "Set up a File Naming Template first (add at least one parameter).",
                    "PDF Splitter", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string pdfSourceDir = Path.GetDirectoryName(pdfPath);

            // DWG target: user choice, else alongside the PDF.
            bool exportDwgs = chx_GenerateDWGS.IsChecked == true;
            string dwgBasePath = txt_DWGPath.Text;
            if (string.IsNullOrWhiteSpace(dwgBasePath))
                dwgBasePath = pdfSourceDir;

            if (exportDwgs && !Directory.Exists(dwgBasePath))
            {
                System.Windows.MessageBox.Show(
                    "The DWG export folder does not exist:\n" + dwgBasePath,
                    "PDF Splitter", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string setupName = cbb_ExportSettings.Text;
            if (exportDwgs && string.IsNullOrWhiteSpace(setupName))
            {
                System.Windows.MessageBox.Show(
                    "Select a DWG Export Settings setup, or untick 'Export DWGs'.",
                    "PDF Splitter", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // ── Resolve final PDF and DWG output directories ────────────────
            // "Separate folders" mode drops PDFs into <pdfSourceDir>\PDF and
            // DWGs into <dwgBasePath>\DWG. Created on demand.
            bool separateFolders = chx_SeparateFolders.IsChecked == true;
            string pdfOutputDir = pdfSourceDir;
            string dwgOutputDir = dwgBasePath;

            if (separateFolders)
            {
                pdfOutputDir = Path.Combine(pdfSourceDir, "PDF");
                dwgOutputDir = Path.Combine(dwgBasePath, "DWG");

                try
                {
                    Directory.CreateDirectory(pdfOutputDir);
                    if (exportDwgs)
                        Directory.CreateDirectory(dwgOutputDir);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(
                        "Could not create output sub-folders:\n" + ex.Message,
                        "PDF Splitter", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            // ── Pass 1: compute & validate ALL file names up front ──────────
            List<ParameterClass> template = new List<ParameterClass>(selectedNameTemplate);
            List<string> fileNames = new List<string>();
            List<string> blankSheets = new List<string>();

            for (int idx = 0; idx < selectedSheetsInSet.Count; idx++)
            {
                ViewSheet sheet = selectedSheetsInSet[idx];
                string name = ParameterHelperClass.getFileName(template, sheet, app);

                if (string.IsNullOrWhiteSpace(name))
                    blankSheets.Add(sheet.SheetNumber + " - " + sheet.Name);

                fileNames.Add(name);
            }

            if (blankSheets.Count > 0)
            {
                System.Windows.MessageBox.Show(
                    "The template produced an empty file name for these sheets " +
                    "(parameters missing or blank):\n\n" + Summarise(blankSheets) +
                    "\n\nFix the parameter values or the template, then try again.",
                    "PDF Splitter", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Duplicate names within the batch → later sheets would silently
            // overwrite earlier ones. Old version did exactly that; we stop.
            List<string> duplicates = fileNames
                .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicates.Count > 0)
            {
                System.Windows.MessageBox.Show(
                    "The template produces the same file name for more than one sheet:\n\n" +
                    Summarise(duplicates) +
                    "\n\nAdd a unique parameter (e.g. Sheet Number) to the template.",
                    "PDF Splitter", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Existing files → one confirmation up front instead of a surprise
            // at the end.
            List<string> existing = fileNames
                .Where(n => File.Exists(Path.Combine(pdfOutputDir, n + ".pdf")) ||
                            (exportDwgs && File.Exists(Path.Combine(dwgOutputDir, n + ".dwg"))))
                .ToList();

            if (existing.Count > 0)
            {
                MessageBoxResult overwrite = System.Windows.MessageBox.Show(
                    existing.Count + " file(s) already exist and will be overwritten, e.g.:\n\n" +
                    Summarise(existing) + "\n\nContinue?",
                    "PDF Splitter", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (overwrite != MessageBoxResult.Yes) return;
            }

            // ── Pass 2: do the work ─────────────────────────────────────────
            RunSplit(pdfOutputDir, fileNames, exportDwgs, dwgOutputDir, setupName);
        }

        private void RunSplit(string pdfDirectory, List<string> fileNames,
                              bool exportDwgs, string dwgTargetPath, string setupName)
        {
            isRunning = true;
            SetUiLocked(true);
            Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;

            int pdfCount = 0;
            int dwgCount = 0;
            List<string> failures = new List<string>();

            try
            {
                int total = selectedSheetsInSet.Count;
                prg_Progress.Maximum = total;

                for (int idx = 0; idx < total; idx++)
                {
                    ViewSheet sheet = selectedSheetsInSet[idx];
                    string fileName = fileNames[idx];

                    SetStatus("Processing " + (idx + 1) + " of " + total + ":  " + fileName);
                    prg_Progress.Value = idx;
                    DoEvents();

                    // ── PDF page ────────────────────────────────────────────
                    try
                    {
                        PdfDocument outputDocument = new PdfDocument();
                        outputDocument.Version = inputDocument.Version;
                        outputDocument.Info.Title = fileName;
                        outputDocument.Info.Creator = inputDocument.Info.Creator;
                        outputDocument.AddPage(inputDocument.Pages[idx]);
                        outputDocument.Save(Path.Combine(pdfDirectory, fileName + ".pdf"));
                        pdfCount++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add(fileName + ".pdf — " + ex.Message);
                        continue;   // no point exporting DWGs for a failed sheet name
                    }

                    // ── DWGs ────────────────────────────────────────────────
                    if (exportDwgs)
                    {
                        if (ExportDWG(sheet, setupName, dwgTargetPath, fileName, false, failures))
                            dwgCount++;

                        if (chx_GenerateDWGViews.IsChecked == true)
                        {
                            foreach (ElementId viewId in sheet.GetAllPlacedViews())
                            {
                                ViewPlan plan = doc.GetElement(viewId) as ViewPlan;
                                if (plan == null) continue;

                                string planFileName = ParameterHelperClass.SanitizeFileName(
                                    fileName + "_" + plan.Name);

                                if (ExportDWG(plan, setupName, dwgTargetPath, planFileName, true, failures))
                                    dwgCount++;
                            }
                        }
                    }
                }

                prg_Progress.Value = total;

                // ── PCP cleanup — once, top folder only ─────────────────────
                if (exportDwgs && chx_DeletePcp.IsChecked == true)
                {
                    SetStatus("Cleaning up PCP files…");
                    DoEvents();
                    try
                    {
                        foreach (string pcp in Directory.EnumerateFiles(dwgTargetPath, "*.pcp",
                                     SearchOption.TopDirectoryOnly))
                        {
                            File.Delete(pcp);
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add("PCP cleanup — " + ex.Message);
                    }
                }

                // ── Summary ─────────────────────────────────────────────────
                string summary = pdfCount + " PDFs created" +
                                 (exportDwgs ? ", " + dwgCount + " DWGs exported" : "") + ".";
                SetStatus("Done — " + summary);

                if (failures.Count > 0)
                {
                    System.Windows.MessageBox.Show(
                        summary + "\n\n" + failures.Count + " item(s) failed:\n\n" + Summarise(failures),
                        "PDF Splitter — finished with warnings",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    System.Windows.MessageBox.Show(
                        summary + "\n\nOutput folder:\n" + pdfDirectory,
                        "PDF Splitter — finished",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "PDF Splitter");
            }
            finally
            {
                isRunning = false;
                Mouse.OverrideCursor = null;
                SetUiLocked(false);
                UpdateReadiness();
            }
        }

        private void SetUiLocked(bool locked)
        {
            bool enabled = !locked;
            btn_Split.IsEnabled = enabled;
            btn_Cancel.IsEnabled = enabled;
            grp_Settings.IsEnabled = enabled;
            btn_getPDF.IsEnabled = enabled;
            btn_getDWGs.IsEnabled = enabled;
            cbb_PrintSet.IsEnabled = enabled;
            cbb_ExportSettings.IsEnabled = enabled;
            chx_GenerateDWGS.IsEnabled = enabled;
            chx_GenerateDWGViews.IsEnabled = enabled;
            chx_NoXrefs.IsEnabled = enabled;
            chx_TrueColourExport.IsEnabled = enabled;
            chx_DeletePcp.IsEnabled = enabled;
            chx_SeparateFolders.IsEnabled = enabled;
            brd_DropZone.IsEnabled = enabled;
        }

        private static string Summarise(IList<string> items, int max = 12)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < items.Count && i < max; i++)
                sb.AppendLine("  • " + items[i]);

            if (items.Count > max)
                sb.AppendLine("  … and " + (items.Count - max) + " more");

            return sb.ToString().TrimEnd();
        }

        // ════════════════════════════════════════════════════════════════════
        //  DWG export
        // ════════════════════════════════════════════════════════════════════

        private bool ExportDWG(View view, string setupName, string path, string filename,
                               bool useSharedCoords, List<string> failures)
        {
            try
            {
                DWGExportOptions dwgOptions = DWGExportOptions.GetPredefinedOptions(doc, setupName);
                if (dwgOptions == null)
                {
                    failures.Add(filename + ".dwg — export setup '" + setupName + "' not found.");
                    return false;
                }

                // "DON'T export views as Xrefs" → merge everything into one file.
                dwgOptions.MergedViews = chx_NoXrefs.IsChecked == true;
                dwgOptions.FileVersion = ACADVersion.R2007;

                if (chx_TrueColourExport.IsChecked == true)
                    dwgOptions.Colors = ExportColorMode.TrueColorPerView;

                // Plan-view copies are exported on shared coordinates so they
                // drop straight into a federated CAD environment.
                if (useSharedCoords)
                    dwgOptions.SharedCoords = true;

                ICollection<ElementId> views = new List<ElementId> { view.Id };
                bool exported = doc.Export(path, filename, views, dwgOptions);

                if (!exported)
                    failures.Add(filename + ".dwg — Revit reported the export failed.");

                return exported;
            }
            catch (Exception ex)
            {
                failures.Add(filename + ".dwg — " + ex.Message);
                return false;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  Close
        // ════════════════════════════════════════════════════════════════════

        private void Btn_Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            // Remember the last used preset for next time.
            try
            {
                if (templateStore != null)
                {
                    string ignore;
                    templateStore.Save(out ignore);
                }
            }
            catch { /* never block close */ }

            base.OnClosed(e);
        }
    }
}