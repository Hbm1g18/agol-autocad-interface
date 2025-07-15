using System.Windows;
using System.Linq;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace ArcGisAutoCAD
{
    public partial class BlockPromptDialog : Window
    {
        public bool UseBlock => BlockRadio.IsChecked == true;
        public string SelectedBlockName => BlockCombo.SelectedItem as string;

        public BlockPromptDialog(List<string> blockNames)
        {
            InitializeComponent();
            BlockCombo.ItemsSource = blockNames;
            if (blockNames.Count > 0)
                BlockCombo.SelectedIndex = 0;
        }

        private void Radio_Checked(object sender, RoutedEventArgs e)
        {
            if (BlockCombo == null)
                return;

            BlockCombo.IsEnabled = BlockRadio.IsChecked == true;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (UseBlock && string.IsNullOrEmpty(SelectedBlockName))
            {
                MessageBox.Show("Please select a block.");
                return;
            }
            DialogResult = true;
            Close();
        }
    }
}
