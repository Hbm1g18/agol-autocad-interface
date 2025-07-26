using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Npgsql;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace ArcGisAutoCAD
{
    public partial class PgAppendixMakerWindow : Window
    {
        private List<PgTableInfo> _tables;
        private List<AppendixTemplate> _templates;
        private List<string> _columns;

        public PgAppendixMakerWindow()
        {
            InitializeComponent();
            NpgsqlConnection.GlobalTypeMapper.UseNetTopologySuite();
            LoadTemplates();
            LoadTables();
        }

        private void LoadTemplates()
        {
            _templates = AppendixTemplateStore.Load();
            TemplateCombo.ItemsSource = _templates;
            if (_templates.Any()) TemplateCombo.SelectedIndex = 0;
        }

        private void LoadTables()
        {
            var settings = PgSettings.Load();
            _tables = new();

            using var conn = new NpgsqlConnection(
                $"Host={settings.Host};Username={settings.Username};Password={settings.Password};Database={settings.Database};Port={settings.Port};");
            conn.Open();
            using var cmd = new NpgsqlCommand("SELECT f_table_schema, f_table_name, f_geometry_column, type, srid FROM public.geometry_columns ORDER BY 1, 2", conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                _tables.Add(new PgTableInfo
                {
                    Schema = reader.GetString(0),
                    Table = reader.GetString(1),
                    GeomColumn = reader.GetString(2),
                    GeomType = reader.GetString(3),
                    Srid = reader.GetInt32(4)
                });
            }
            TableCombo.ItemsSource = _tables;
            if (_tables.Any()) TableCombo.SelectedIndex = 0;
        }

        private void TableCombo_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (TableCombo.SelectedItem is not PgTableInfo table) return;

            var settings = PgSettings.Load();
            _columns = new();
            using var conn = new NpgsqlConnection(
                $"Host={settings.Host};Username={settings.Username};Password={settings.Password};Database={settings.Database};Port={settings.Port};");
            conn.Open();

            var cmd = new NpgsqlCommand(
                "SELECT column_name FROM information_schema.columns WHERE table_schema = @schema AND table_name = @table", conn);
            cmd.Parameters.AddWithValue("schema", table.Schema);
            cmd.Parameters.AddWithValue("table", table.Table);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                _columns.Add(reader.GetString(0));
            }
            ImageFieldCombo.ItemsSource = _columns;
            CommentFieldCombo.ItemsSource = _columns;
        }

        private void Generate_Click(object sender, RoutedEventArgs e)
        {
            if (TemplateCombo.SelectedItem is not AppendixTemplate template ||
                TableCombo.SelectedItem is not PgTableInfo table ||
                ImageFieldCombo.SelectedItem is not string imageField ||
                CommentFieldCombo.SelectedItem is not string commentField)
            {
                MessageBox.Show("Please complete all selections.");
                return;
            }

            var settings = PgSettings.Load();
            var connStr = $"Host={settings.Host};Username={settings.Username};Password={settings.Password};Database={settings.Database};Port={settings.Port};";
            var sql = $"SELECT * FROM \"{table.Schema}\".\"{table.Table}\"";

            using var conn = new NpgsqlConnection(connStr);
            conn.Open();
            var cmd = new NpgsqlCommand(sql, conn);
            using var reader = cmd.ExecuteReader();
            var features = new List<Dictionary<string, object>>();

            while (reader.Read())
            {
                var row = new Dictionary<string, object>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.GetValue(i);
                features.Add(row);
            }

            foreach (var feature in features)
            {
                AppendixGenerator.CreateLayout(template, feature, imageField, commentField);
            }

            MessageBox.Show($"Generated {features.Count} appendix layouts.");
            Close();
        }
    }
}