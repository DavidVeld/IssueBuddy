using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace IssueBuddy
{
    public partial class ModelStripperWindow : Window
    {
        public string selectedName { get; set; }
        public bool IsAccepted { get; private set; }

        public string newfilepath { get; private set; }
        public string newFileName { get; private set; }

        public ModelStripperWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Pre-populate the filename box with the document title
            txt_FileName.IsEnabled = true;
            txt_FileName.Text = selectedName ?? string.Empty;
        }

        private void btn_Cancel_Click(object sender, RoutedEventArgs e)
        {
            IsAccepted = false;
            Close();
        }

        private void btn_Export_Click(object sender, RoutedEventArgs e)
        {
            // Open SaveFileDialog pre-filled with whatever is in the filename box.
            // The user can change the name and path here — that is the only rename step.
            var dlg = new SaveFileDialog
            {
                FileName = txt_FileName.Text.Trim(),
                Filter = "Revit files (*.rvt)|*.rvt",
                Title = "Specify location for the stripped Revit file"
            };

            if (dlg.ShowDialog() == true)
            {
                newfilepath = Path.GetDirectoryName(dlg.FileName);
                newFileName = Path.GetFileNameWithoutExtension(dlg.FileName);
                IsAccepted = true;
                Close();
            }
            else
            {
                MessageBox.Show(
                    "Please specify a file location, or click Cancel to exit.",
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}