using System.Windows;

namespace ArcGisAutoCAD
{
    public partial class SimplePromptWindow : Window
    {
        public string ResponseText => InputBox.Text;

        public SimplePromptWindow(string prompt)
        {
            InitializeComponent();
            PromptText.Text = prompt;
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
