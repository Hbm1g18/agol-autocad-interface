using System.Collections.Generic;
using System.Windows;

namespace ArcGisAutoCAD
{
    public partial class AttributeFieldPickerDialog : Window
    {
        public string SelectedField => FieldCombo.SelectedItem as string;

        public AttributeFieldPickerDialog(List<string> fieldNames)
        {
            InitializeComponent();
            FieldCombo.ItemsSource = fieldNames;
            if (fieldNames.Count > 0)
                FieldCombo.SelectedIndex = 0;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedField == null)
            {
                MessageBox.Show("Please select a field.");
                return;
            }
            DialogResult = true;
            Close();
        }
    }
}
