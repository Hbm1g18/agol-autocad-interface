using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace ArcGisAutoCAD
{
    public partial class PgLayerRefreshDialog : Window
    {
        public List<PgLayerMeta> SelectedLayers { get; private set; } = new();

        public PgLayerRefreshDialog(List<PgLayerMeta> allLayers)
        {
            InitializeComponent();

            // Only show layers for current DWG, or those not associated with a DWG
            string currentDwg = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument?.Name;
            var forCurrentDwg = allLayers
                .Where(x =>
                    (string.IsNullOrEmpty(x.DwgFile)) // not associated with any DWG
                    || string.Equals(x.DwgFile, currentDwg, System.StringComparison.OrdinalIgnoreCase) // matches current DWG
                )
                .ToList();

            LayersListBox.ItemsSource = forCurrentDwg;
            LayersListBox.SelectionMode = System.Windows.Controls.SelectionMode.Multiple;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            SelectedLayers = LayersListBox.SelectedItems.Cast<PgLayerMeta>().ToList();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
