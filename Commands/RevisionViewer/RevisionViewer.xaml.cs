using Binding = System.Windows.Data.Binding; // Explicitly map to WPF system bindings up front
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;

namespace IssueBuddy
{
    public partial class RevisionViewer : Window
    {
        private DataTable _internalDataTable;

        public RevisionViewer()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Reads Revit sheet revision metadata and builds a pivot matrix matching the target layout.
        /// </summary>
        public void PopulateData(Document doc)
        {
            // Detect global project revision numbering settings up front
            RevisionSettings revSettings = RevisionSettings.GetRevisionSettings(doc);
            bool isPerSheet = revSettings.RevisionNumbering == RevisionNumbering.PerSheet;

            // Gather all printable sheets that are actively participating in documentation sets
            List<ViewSheet> sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder)
                .Where(s => {
                    // SHEET_SCHEDULED controls the "Appears in Sheet List" property
                    Parameter p = s.get_Parameter(BuiltInParameter.SHEET_SCHEDULED);
                    return p != null && p.AsInteger() == 1;
                })
                .OrderBy(s => s.SheetNumber)
                .ToList();

            List<SheetRevisionRowData> rowRecords = new List<SheetRevisionRowData>();
            HashSet<string> globalRevisionDates = new HashSet<string>();

            // Step 1: Collect parameter values and iterate historic sequence streams
            foreach (ViewSheet sheet in sheets)
            {
                SheetRevisionRowData record = new SheetRevisionRowData
                {
                    SheetNumber = sheet.SheetNumber, // String storage retains alpha-prefixes and leading zeros safely
                    SheetTitle = sheet.Name,
                    LastRevision = sheet.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString() ?? "",
                    LastRevisionDate = sheet.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION_DATE)?.AsString() ?? "",
                    IssuedBy = sheet.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION_ISSUED_BY)?.AsString() ?? "",
                    IssuedTo = sheet.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION_ISSUED_TO)?.AsString() ?? ""
                };

                ICollection<ElementId> historicalRevisionIds = sheet.GetAllRevisionIds();
                foreach (ElementId revId in historicalRevisionIds)
                {
                    if (doc.GetElement(revId) is Revision rev)
                    {
                        string dateStr = rev.RevisionDate;

                        // Safely retrieve numbering matching the project's setup scheme (Per Sheet vs Per Project)
                        string revNum = isPerSheet ? sheet.GetRevisionNumberOnSheet(revId) : rev.RevisionNumber;

                        // Fallback processing if revision remains unissued
                        if (string.IsNullOrWhiteSpace(revNum))
                        {
                            revNum = rev.SequenceNumber.ToString();
                        }

                        if (!string.IsNullOrWhiteSpace(dateStr))
                        {
                            globalRevisionDates.Add(dateStr);

                            if (!record.HistoryMatrix.ContainsKey(dateStr))
                            {
                                record.HistoryMatrix[dateStr] = new List<string>();
                            }
                            if (!record.HistoryMatrix[dateStr].Contains(revNum))
                            {
                                record.HistoryMatrix[dateStr].Add(revNum);
                            }
                        }
                    }
                }
                rowRecords.Add(record);
            }

            // Step 2: Organize global transaction date strings chronologically
            List<string> sortedTimelineColumns = globalRevisionDates.OrderBy(d => {
                return DateTime.TryParse(d, out DateTime parsedDate) ? parsedDate : DateTime.MinValue;
            }).ThenBy(d => d).ToList();

            // Step 3: Map out data structure framework using generalized Revit standard terms
            _internalDataTable = new DataTable();
            _internalDataTable.Columns.Add("Sheet Number", typeof(string));
            _internalDataTable.Columns.Add("Sheet Title", typeof(string));
            _internalDataTable.Columns.Add("Last Revision", typeof(string));
            _internalDataTable.Columns.Add("Date", typeof(string));

            foreach (string dateCol in sortedTimelineColumns)
            {
                _internalDataTable.Columns.Add(dateCol, typeof(string));
            }

            _internalDataTable.Columns.Add("Issued By", typeof(string));
            _internalDataTable.Columns.Add("Issued To", typeof(string));

            // Step 4: Populate transactional cells
            foreach (SheetRevisionRowData record in rowRecords)
            {
                DataRow row = _internalDataTable.NewRow();
                row["Sheet Number"] = record.SheetNumber;
                row["Sheet Title"] = record.SheetTitle;
                row["Last Revision"] = record.LastRevision;
                row["Date"] = record.LastRevisionDate;

                foreach (string dateCol in sortedTimelineColumns)
                {
                    if (record.HistoryMatrix.ContainsKey(dateCol))
                    {
                        // Safely combine simultaneous revisions issued on the same day
                        row[dateCol] = string.Join(", ", record.HistoryMatrix[dateCol]);
                    }
                    else
                    {
                        row[dateCol] = string.Empty;
                    }
                }

                row["Issued By"] = record.IssuedBy;
                row["Issued To"] = record.IssuedTo;
                _internalDataTable.Rows.Add(row);
            }

            // Step 5: Render explicit styled columns to DataGrid
            dg_RevisionHistory.Columns.Clear();

            // Establish a simple text styling override to force specific text block content columns to render Bold
            Style boldTextElementStyle = new Style(typeof(TextBlock));
            boldTextElementStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));

            dg_RevisionHistory.Columns.Add(new DataGridTextColumn { Header = "Sheet Number", Binding = new Binding("Sheet Number"), Width = new DataGridLength(110), ElementStyle = boldTextElementStyle });
            dg_RevisionHistory.Columns.Add(new DataGridTextColumn { Header = "Sheet Title", Binding = new Binding("Sheet Title"), Width = new DataGridLength(220), ElementStyle = boldTextElementStyle });

            // Applied the bold style to the Last Revision Column elements
            dg_RevisionHistory.Columns.Add(new DataGridTextColumn { Header = "Last Revision", Binding = new Binding("Last Revision"), Width = new DataGridLength(95), ElementStyle = boldTextElementStyle });

            // Disabled sorting on the static summary Date column to prevent dynamic evaluation errors
            dg_RevisionHistory.Columns.Add(new DataGridTextColumn { Header = "Date", Binding = new Binding("Date"), Width = new DataGridLength(100), CanUserSort = false });

            // Generate timeline columns and explicitly disable sorting to prevent runtime evaluation crashes
            foreach (string dateCol in sortedTimelineColumns)
            {
                dg_RevisionHistory.Columns.Add(new DataGridTextColumn
                {
                    Header = dateCol,
                    Binding = new Binding($"[{dateCol}]"), // Bracket escaping safely targets structural data indices
                    Width = new DataGridLength(90),
                    CanUserSort = false
                });
            }

            // Standardized generalized headers mapped to their tracking nodes
            dg_RevisionHistory.Columns.Add(new DataGridTextColumn { Header = "Issued By", Binding = new Binding("Issued By"), Width = new DataGridLength(95) });
            dg_RevisionHistory.Columns.Add(new DataGridTextColumn { Header = "Issued To", Binding = new Binding("Issued To"), Width = new DataGridLength(95) });

            dg_RevisionHistory.ItemsSource = _internalDataTable.DefaultView;
        }

        /// <summary>
        /// Native parser converting runtime DataTable strings into clean CSV documents.
        /// </summary>
        private void Button_Click_Export(object sender, RoutedEventArgs e)
        {
            if (_internalDataTable == null || _internalDataTable.Rows.Count == 0)
            {
                MessageBox.Show("There is no revision data available to export.", "Export Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Microsoft.Win32.SaveFileDialog saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV Files (*.csv)|*.csv",
                Title = "Export Revision Matrix Table",
                FileName = "IssueViewerExport.csv"
            };

            if (saveDialog.ShowDialog() == true)
            {
                try
                {
                    StringBuilder csvBuilder = new StringBuilder();

                    // Process Header Fields
                    string[] columnNames = _internalDataTable.Columns.Cast<DataColumn>()
                                                .Select(column => EscapeForCsv(column.ColumnName))
                                                .ToArray();
                    csvBuilder.AppendLine(string.Join(",", columnNames));

                    // Process Data Row Fields
                    foreach (DataRow row in _internalDataTable.Rows)
                    {
                        string[] fields = row.ItemArray
                                            .Select(field => EscapeForCsv(field?.ToString()))
                                            .ToArray();
                        csvBuilder.AppendLine(string.Join(",", fields));
                    }

                    File.WriteAllText(saveDialog.FileName, csvBuilder.ToString(), Encoding.UTF8);
                    MessageBox.Show("Revision table successfully exported!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to export data document:\n{ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// Encapsulates values containing commas or quotes to preserve structure within Excel or other parsers.
        /// </summary>
        private string EscapeForCsv(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            // If the cell text contains commas, double quotes, or newline breaks, wrap the string block
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\r") || value.Contains("\n"))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }
            return value;
        }

        private void Button_Click_Close(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
            this.Close();
        }
    }

    /// <summary>
    /// Lightweight storage node tracking individual parameters mapped per sheet element.
    /// </summary>
    internal class SheetRevisionRowData
    {
        public string SheetNumber { get; set; }
        public string SheetTitle { get; set; }
        public string LastRevision { get; set; }
        public string LastRevisionDate { get; set; }
        public string IssuedBy { get; set; }
        public string IssuedTo { get; set; }
        public Dictionary<string, List<string>> HistoryMatrix { get; set; } = new Dictionary<string, List<string>>();
    }
}