using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace IssueBuddy
{
    public partial class CreatePrintSetWindow : Window
    {
        private readonly UIApplication _app;
        private readonly Document _doc;

        public bool accepted { get; private set; }
        public string FinalPrintSetName { get; private set; }

        // Typed backing list for the revision checkboxes
        private readonly List<RevisionViewModel> _revList = new List<RevisionViewModel>();

        public CreatePrintSetWindow(ExternalCommandData commandData)
        {
            _app = commandData.Application;
            _doc = _app.ActiveUIDocument.Document;

            InitializeComponent();
        }

        // ── Startup ─────────────────────────────────────────────────────────────

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadRevisions();
        }

        private void LoadRevisions()
        {
            try
            {
                var revisions = new FilteredElementCollector(_doc)
                    .OfClass(typeof(Revision))
                    .Cast<Revision>()
                    .OrderByDescending(r => r.SequenceNumber)
                    .ToList();

                _revList.Clear();

                foreach (Revision rev in revisions)
                {
                    string display = $"{rev.SequenceNumber} – {rev.RevisionDate} – {rev.Description}";

                    _revList.Add(new RevisionViewModel
                    {
                        Revision = rev,
                        RevisionName = display,
                        IsSelected = false
                    });
                }

                lst_Revisions.ItemsSource = _revList;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", "Failed to load revisions:\n" + ex.Message);
            }
        }

        // ── Checkbox change handlers ─────────────────────────────────────────────
        // Both revision checkboxes and the date-prefix checkbox call UpdateSheetList.
        // NOTE: The XAML must bind IsChecked to {Binding IsSelected, Mode=TwoWay}
        // (not RelativeSource ListBoxItem) so that _revList.IsSelected is actually updated.

        private void CheckBox_Changed(object sender, RoutedEventArgs e) => UpdateSheetList();

        private void chx_DataYes_Checked(object sender, RoutedEventArgs e) => UpdateName();

        // ── Core update logic ────────────────────────────────────────────────────

        private void UpdateSheetList()
        {
            UpdateName();
            RefreshSheets();
        }

        private void UpdateName()
        {
            if (_revList == null || !_revList.Any(r => r.IsSelected))
            {
                txt_SheetSetName.Text = string.Empty;
                return;
            }

            Revision latest = _revList
                .Where(r => r.IsSelected)
                .Select(r => r.Revision)
                .OrderByDescending(r => r.SequenceNumber)
                .First();

            string date = FormatRevisionDate(latest.RevisionDate);
            string description = latest.Description;

            txt_SheetSetName.Text = chx_DataYes.IsChecked == true
                ? $"{date}_{description}"
                : description;
        }

        private void RefreshSheets()
        {
            var checkedRevs = _revList.Where(r => r.IsSelected).ToList();

            if (!checkedRevs.Any())
            {
                liv_Sheets.ItemsSource = null;
                UpdateSheetCountLabel(0);
                return;
            }

            HashSet<ElementId> selectedIds = checkedRevs
                .Select(r => r.Revision.Id)
                .ToHashSet();

            var rows = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder)
                .Where(s => s.GetAllRevisionIds().Any(id => selectedIds.Contains(id)))
                .OrderBy(s => s.SheetNumber)   // Improvement: sort by number
                .Select(s => new SheetRow
                {
                    Number = s.SheetNumber,
                    Name = s.Name,
                    Rev = s.LookupParameter("Current Revision")?.AsString() ?? string.Empty,
                    SheetElementId = s.Id          // Store ElementId — used in the command
                })
                .ToList();

            liv_Sheets.ItemsSource = rows;
            UpdateSheetCountLabel(rows.Count);
        }

        private void UpdateSheetCountLabel(int count)
        {
            // lbl_SheetCount is a TextBlock defined in the XAML footer
            lbl_SheetCount.Text = count == 0
                ? string.Empty
                : $"{count} sheet{(count == 1 ? "" : "s")}";
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static string FormatRevisionDate(string rawDate)
        {
            return DateTime.TryParse(rawDate, out DateTime parsed)
                ? parsed.ToString("yyyy-MM-dd")
                : rawDate;
        }

        // ── Button handlers ──────────────────────────────────────────────────────

        // Cancel
        private void Button_Click(object sender, RoutedEventArgs e)
        {
            accepted = false;
            Close();
        }

        // OK — validate then close
        private void Button_Click_1(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txt_SheetSetName.Text))
            {
                MessageBox.Show("Please provide a print set name.", "Warning",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (liv_Sheets.Items.Count == 0)
            {
                MessageBox.Show("No sheets found for the selected revision(s). Cannot create an empty print set.",
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string proposedName = txt_SheetSetName.Text.Trim();

            bool nameExists = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewSheetSet))
                .Cast<ViewSheetSet>()
                .Any(s => s.Name.Equals(proposedName, StringComparison.OrdinalIgnoreCase));

            if (nameExists)
            {
                MessageBox.Show(
                    $"A print set named \"{proposedName}\" already exists.\n\n" +
                    "Please edit the name above and try again.",
                    "Name Already Exists", MessageBoxButton.OK, MessageBoxImage.Warning);
                txt_SheetSetName.Focus();
                txt_SheetSetName.SelectAll();
                return;
            }

            FinalPrintSetName = proposedName;
            accepted = true;
            Close();
        }

        // Copy sheet list to clipboard
        private void Button_Click_Copy(object sender, RoutedEventArgs e)
        {
            if (liv_Sheets.Items.Count == 0)
            {
                MessageBox.Show("No sheets to copy.", "Info",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("Sheet Number\tSheet Name\tRevision");

            foreach (var item in liv_Sheets.Items.OfType<SheetRow>())
                sb.AppendLine($"{item.Number}\t{item.Name}\t{item.Rev}");

            Clipboard.SetText(sb.ToString());
            MessageBox.Show("Sheet list copied to clipboard.", "Success",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }


    }
}