using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace IssueBuddy
{
    /// <summary>
    /// Lightweight code-only WPF dialog used in place of a TaskDialog,
    /// since Revit's native TaskDialog cannot host a text input.
    /// Styled to feel native: simple, no chrome surprises, Enter / Esc work.
    /// </summary>
    internal class NamePromptWindow : Window
    {
        private readonly TextBox _input;
        public string ResultName { get; private set; }

        public static string Prompt(int selectionCount)
        {
            var win = new NamePromptWindow(selectionCount);

            // Parent to Revit's main window so the dialog centers correctly and is modal to Revit.
            var helper = new System.Windows.Interop.WindowInteropHelper(win)
            {
                Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle
            };

            return win.ShowDialog() == true ? win.ResultName : null;
        }

        private NamePromptWindow(int selectionCount)
        {
            Title = "Create Print Set From Selection";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            Background = SystemColors.WindowBrush;

            var root = new Grid { Margin = new Thickness(20, 18, 20, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Heading
            var heading = new TextBlock
            {
                Text = "Specify a name",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(178, 22, 22))
            };
            Grid.SetRow(heading, 0);
            root.Children.Add(heading);

            // Subtitle
            var subtitle = new TextBlock
            {
                Text = $"Name for the new print set containing {selectionCount} selected item(s).",
                Foreground = SystemColors.GrayTextBrush,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(subtitle, 2);
            root.Children.Add(subtitle);

            // Input
            _input = new TextBox
            {
                Padding = new Thickness(6, 4, 6, 4),
                MinHeight = 26,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(_input, 4);
            root.Children.Add(_input);

            // Buttons
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetRow(buttons, 6);

            var ok = new Button
            {
                Content = "OK",
                Width = 90,
                Height = 28,
                IsDefault = true,
                Margin = new Thickness(0, 0, 8, 0)
            };
            ok.Click += (s, e) =>
            {
                ResultName = _input.Text;
                DialogResult = true;
            };

            var cancel = new Button
            {
                Content = "Cancel",
                Width = 90,
                Height = 28,
                IsCancel = true
            };

            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            Content = root;

            Loaded += (s, e) => { _input.Focus(); Keyboard.Focus(_input); };
        }
    }
}
