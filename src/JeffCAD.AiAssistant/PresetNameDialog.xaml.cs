using System.Windows;

namespace JeffCAD.AiAssistant
{
    /// <summary>
    /// A one-field prompt for naming a preset. WPF has no built-in input box, and
    /// pulling in the VisualBasic assembly for <c>Microsoft.VisualBasic.Interaction.InputBox</c>
    /// would drag a dependency into an AutoCAD-managed add-in for one dialog.
    /// </summary>
    public partial class PresetNameDialog : Window
    {
        public PresetNameDialog(string title, string defaultName)
        {
            InitializeComponent();

            Title = title;
            NameBox.Text = defaultName ?? string.Empty;

            // Preselect so the user can type a replacement immediately, and focus
            // the field so the dialog is usable without touching the mouse.
            Loaded += (_, __) =>
            {
                NameBox.Focus();
                NameBox.SelectAll();
            };
        }

        /// <summary>The entered name. Only meaningful when the dialog returned true.</summary>
        public string PresetName { get; private set; } = string.Empty;

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var name = NameBox.Text?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                HintText.Text = "名称不能为空。";
                HintText.Foreground = System.Windows.Media.Brushes.Firebrick;
                NameBox.Focus();
                return;
            }

            PresetName = name;
            DialogResult = true;
            Close();
        }
    }
}
