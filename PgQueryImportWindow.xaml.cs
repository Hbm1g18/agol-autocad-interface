using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Threading.Tasks;
using Npgsql;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.DatabaseServices;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;
using NetTopologySuite.Geometries;
using NTSPoint = NetTopologySuite.Geometries.Point;
using NTSLineString = NetTopologySuite.Geometries.LineString;
using NTSPolygon = NetTopologySuite.Geometries.Polygon;
using Geometry = NetTopologySuite.Geometries.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace ArcGisAutoCAD
{
    public partial class PgQueryImportWindow : Window
    {
        // Table info structure (same as PgTableInfo, but local)
        public class TableInfo
        {
            public string Schema { get; set; }
            public string Table { get; set; }
            public string GeomColumn { get; set; }
            public string GeomType { get; set; }
            public int Srid { get; set; }
            public string DisplayName => $"{Schema}.{Table} ({GeomType}, SRID={Srid})";
            public override string ToString() => DisplayName;
        }

        public class ColumnInfo
        {
            public string Name { get; set; }
            public string DataType { get; set; }
            public override string ToString() => $"{Name} ({DataType})";
        }

        private List<TableInfo> _tables = new();
        private List<ColumnInfo> _columns = new();
        private string _geomColumn;
        private string _geomType;
        private int _srid;

        // Query state
        private List<CheckBox> _fieldCheckboxes = new();
        private List<QueryConditionRow> _conditionRows = new();

        public PgQueryImportWindow()
        {
            InitializeComponent();
            NpgsqlConnection.GlobalTypeMapper.UseNetTopologySuite();
            LoadTables();
        }

        private void LoadTables()
        {
            var settings = PgSettings.Load();
            try
            {
                using var conn = new NpgsqlConnection(
                    $"Host={settings.Host};Username={settings.Username};Password={settings.Password};Database={settings.Database};Port={settings.Port};");
                conn.Open();
                using var cmd = new NpgsqlCommand(
                    "SELECT f_table_schema, f_table_name, f_geometry_column, type, srid " +
                    "FROM public.geometry_columns ORDER BY f_table_schema, f_table_name;", conn);
                using var reader = cmd.ExecuteReader();
                _tables.Clear();
                while (reader.Read())
                {
                    _tables.Add(new TableInfo
                    {
                        Schema = reader.GetString(0),
                        Table = reader.GetString(1),
                        GeomColumn = reader.GetString(2),
                        GeomType = reader.GetString(3),
                        Srid = reader.GetInt32(4)
                    });
                }
                TablesCombo.ItemsSource = _tables;
                if (_tables.Any())
                    TablesCombo.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not load tables: {ex.Message}";
            }
        }

        private void SplitByAttributeCheck_Checked(object sender, RoutedEventArgs e)
        {
            SplitAttributeCombo.IsEnabled = true;
        }

        private void SplitByAttributeCheck_Unchecked(object sender, RoutedEventArgs e)
        {
            SplitAttributeCombo.IsEnabled = false;
            SplitAttributeCombo.SelectedItem = null;
        }

        private void TablesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            FieldsPanel.Children.Clear();
            _fieldCheckboxes.Clear();
            _conditionRows.Clear();
            ConditionsPanel.Items.Clear();
            QueryPreview.Text = "";
            _columns.Clear();
            TargetCrsBox.Text = "27700";

            if (TablesCombo.SelectedItem is not TableInfo table) return;
            _geomColumn = table.GeomColumn;
            _geomType = table.GeomType;
            _srid = table.Srid;

            var settings = PgSettings.Load();
            try
            {
                using var conn = new NpgsqlConnection(
                    $"Host={settings.Host};Username={settings.Username};Password={settings.Password};Database={settings.Database};Port={settings.Port};");
                conn.Open();

                // Get columns
                using var cmd = new NpgsqlCommand(
                    "SELECT column_name, data_type FROM information_schema.columns WHERE table_schema = @schema AND table_name = @table", conn);
                cmd.Parameters.AddWithValue("schema", table.Schema);
                cmd.Parameters.AddWithValue("table", table.Table);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string col = reader.GetString(0);
                    string typ = reader.GetString(1);
                    _columns.Add(new ColumnInfo { Name = col, DataType = typ });

                    // Show checkboxes for non-geometry columns
                    if (!col.Equals(_geomColumn, StringComparison.OrdinalIgnoreCase))
                    {
                        var cb = new CheckBox { Content = col, IsChecked = true, Margin = new Thickness(4, 0, 0, 0) };
                        _fieldCheckboxes.Add(cb);
                        FieldsPanel.Children.Add(cb);
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not load columns: {ex.Message}";
            }

            AddCondition_Click(null, null);
            UpdateQueryPreview();
            // Set SplitAttributeCombo to all non-geometry columns
            SplitAttributeCombo.ItemsSource = _columns
                .Where(c => !c.Name.Equals(_geomColumn, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Name)
                .ToList();
            SplitAttributeCombo.SelectedItem = null;
            SplitAttributeCombo.IsEnabled = SplitByAttributeCheck.IsChecked == true;
        }

        private void AddCondition_Click(object sender, RoutedEventArgs e)
        {
            if (_columns.Count == 0) return;
            var cond = new QueryConditionRow(_columns, (TableInfo)TablesCombo.SelectedItem);
            cond.ConditionChanged += QueryCondition_Changed;
            _conditionRows.Add(cond);
            ConditionsPanel.Items.Add(cond);
        }

        private void ManualSqlCheck_Checked(object sender, RoutedEventArgs e)
        {
            QueryPreview.IsReadOnly = false;
        }

        private void ManualSqlCheck_Unchecked(object sender, RoutedEventArgs e)
        {
            QueryPreview.IsReadOnly = true;
            UpdateQueryPreview(); // Restore builder-generated SQL
        }

        private void QueryCondition_Changed(object sender, EventArgs e)
        {
            UpdateQueryPreview();
        }

        private void UpdateQueryPreview()
        {
            if (TablesCombo.SelectedItem is not TableInfo table) return;

            // SELECT
            var selectedFields = _fieldCheckboxes.Where(cb => cb.IsChecked == true).Select(cb => $"\"{cb.Content}\"").ToList();
            selectedFields.Add($"\"{_geomColumn}\"");
            string select = string.Join(", ", selectedFields);

            // WHERE
            var whereClauses = _conditionRows.Select(row => row.ToSql()).Where(s => !string.IsNullOrEmpty(s)).ToList();
            string where = whereClauses.Any() ? " WHERE " + string.Join(" AND ", whereClauses) : "";

            string sql = $"SELECT {select} FROM \"{table.Schema}\".\"{table.Table}\"{where}";
            QueryPreview.Text = sql;
        }

        private void UpdateSplitAttributeCombo(PgTableInfo table)
        {
            // Get non-geometry columns
            var settings = PgSettings.Load();
            var columnNames = new List<string>();

            try
            {
                using var conn = new NpgsqlConnection(
                    $"Host={settings.Host};Username={settings.Username};Password={settings.Password};Database={settings.Database};Port={settings.Port};");
                conn.Open();

                using var cmd = new NpgsqlCommand(
                    "SELECT column_name FROM information_schema.columns WHERE table_schema = @schema AND table_name = @table", conn);
                cmd.Parameters.AddWithValue("schema", table.Schema);
                cmd.Parameters.AddWithValue("table", table.Table);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var col = reader.GetString(0);
                    if (!col.Equals(table.GeomColumn, StringComparison.OrdinalIgnoreCase))
                        columnNames.Add(col);
                }
            }
            catch
            {
                // fallback: nothing
            }
            SplitAttributeCombo.ItemsSource = columnNames;
            SplitAttributeCombo.SelectedItem = null;
            SplitAttributeCombo.IsEnabled = SplitByAttributeCheck.IsChecked == true;
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (TablesCombo.SelectedItem is not TableInfo table) return;

            int.TryParse(TargetCrsBox.Text, out var targetEpsg);
            if (targetEpsg == 0) targetEpsg = 27700;
            int sourceEpsg = _srid;

            string sql = QueryPreview.Text.Trim();

            if (LimitToExtentCheck.IsChecked == true)
            {
                var ed = AcApp.DocumentManager.MdiActiveDocument.Editor;
                var view = ed.GetCurrentView();
                double xmin = view.CenterPoint.X - view.Width / 2;
                double ymin = view.CenterPoint.Y - view.Height / 2;
                double xmax = view.CenterPoint.X + view.Width / 2;
                double ymax = view.CenterPoint.Y + view.Height / 2;

                // --- Reproject view bbox from targetEpsg (drawing) to sourceEpsg (table) ---
                var factory = new ProjNet.CoordinateSystems.CoordinateSystemFactory();
                var transformFactory = new ProjNet.CoordinateSystems.Transformations.CoordinateTransformationFactory();
                var sourceCrs = factory.CreateFromWkt(FoldersWindow.GetWktForEpsg(sourceEpsg));
                var targetCrs = factory.CreateFromWkt(FoldersWindow.GetWktForEpsg(targetEpsg));
                var toSource = transformFactory.CreateFromCoordinateSystems(targetCrs, sourceCrs);

                double[] minXY = toSource.MathTransform.Transform(new[] { xmin, ymin });
                double[] maxXY = toSource.MathTransform.Transform(new[] { xmax, ymax });

                double sxmin = Math.Min(minXY[0], maxXY[0]);
                double symin = Math.Min(minXY[1], maxXY[1]);
                double sxmax = Math.Max(minXY[0], maxXY[0]);
                double symax = Math.Max(minXY[1], maxXY[1]);

                string bbox = $"ST_MakeEnvelope({sxmin}, {symin}, {sxmax}, {symax}, {sourceEpsg})";
                string intersection = $"ST_Intersection(\"{_geomColumn}\", {bbox}) AS \"{_geomColumn}\"";

                // Replace geometry column in SELECT with intersection
                int selectIdx = sql.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase);
                int fromIdx = sql.IndexOf("FROM", StringComparison.OrdinalIgnoreCase);
                if (selectIdx != -1 && fromIdx != -1)
                {
                    string selectPart = sql.Substring(selectIdx, fromIdx - selectIdx);
                    string restPart = sql.Substring(fromIdx);

                    // Replace only the *first occurrence* of the geometry column in SELECT
                    int geomIdx = selectPart.IndexOf($"\"{_geomColumn}\"", StringComparison.OrdinalIgnoreCase);
                    if (geomIdx != -1)
                    {
                        selectPart = selectPart.Remove(geomIdx, _geomColumn.Length + 2)
                                            .Insert(geomIdx, intersection);
                    }
                    sql = selectPart + restPart;
                }

                // Add bbox filter to WHERE/AND
                string extentClause = $"\"{_geomColumn}\" && {bbox}";
                if (sql.ToUpper().Contains("WHERE"))
                    sql += $" AND {extentClause}";
                else
                    sql += $" WHERE {extentClause}";
            }

            if (!sql.ToLower().StartsWith("select"))
            {
                StatusText.Text = "Only SELECT statements are allowed.";
                return;
            }
            if (sql.Contains(";"))
            {
                StatusText.Text = "Multiple statements not allowed.";
                return;
            }
            if (!sql.ToLower().Contains(_geomColumn.ToLower()))
            {
                StatusText.Text = $"Your SQL must include the geometry column '{_geomColumn}'.";
                return;
            }
            var settings = PgSettings.Load();

            try
            {
                using var conn = new NpgsqlConnection(
                    $"Host={settings.Host};Username={settings.Username};Password={settings.Password};Database={settings.Database};Port={settings.Port};");
                conn.Open();
                var features = new List<Dictionary<string, object>>();

                using var cmd = new NpgsqlCommand(sql, conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var row = new Dictionary<string, object>();
                    for (int i = 0; i < reader.FieldCount; i++)
                        row[reader.GetName(i)] = reader.GetValue(i);
                    features.Add(row);
                }

                if (!features.Any())
                {
                    StatusText.Text = "No features found.";
                    return;
                }

                // SPLIT LOGIC BEGINS HERE
                if (SplitByAttributeCheck.IsChecked == true && SplitAttributeCombo.SelectedItem is string splitCol && !string.IsNullOrWhiteSpace(splitCol))
                {
                    var grouped = features.GroupBy(f => f.ContainsKey(splitCol) && f[splitCol] != null ? f[splitCol].ToString() : "NULL");
                    int total = 0;

                    foreach (var group in grouped)
                    {
                        string safeVal = string.Join("_", group.Key.Split(System.IO.Path.GetInvalidFileNameChars())).Replace(" ", "_");
                        string layerName = $"{table.Table}-{safeVal}";
                        int count = group.Count();
                        total += count;

                        switch (_geomType.ToLower())
                        {
                            case var type when type.Contains("point"):
                                DrawFeatures(group.ToList(), _geomColumn, "point", sourceEpsg, targetEpsg, layerName);
                                break;
                            case var type when type.Contains("line"):
                                DrawFeatures(group.ToList(), _geomColumn, "line", sourceEpsg, targetEpsg, layerName);
                                break;
                            case var type when type.Contains("polygon"):
                                DrawFeatures(group.ToList(), _geomColumn, "polygon", sourceEpsg, targetEpsg, layerName);
                                break;
                            default:
                                // skip
                                break;
                        }

                        // === Record metadata for this split layer ===
                        var meta = new PgLayerMeta
                        {
                            AcadLayer = layerName,
                            Host = settings.Host,
                            Database = settings.Database,
                            Username = settings.Username,
                            Schema = table.Schema,
                            Table = table.Table,
                            GeomColumn = table.GeomColumn,
                            GeomType = table.GeomType,
                            Srid = table.Srid,
                            ImportSql = sql,
                            LastImported = DateTime.Now
                        };
                        var dwgName = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument?.Name;
                        System.Diagnostics.Debug.WriteLine("Current DWG for meta: " + dwgName);
                        meta.DwgFile = dwgName;
                        PgLayerMetadata.AddOrUpdate(meta);
                    }

                    StatusText.Text = $"Imported {total} features from {table.DisplayName} (split by '{splitCol}').";
                }
                else
                {
                    switch (_geomType.ToLower())
                    {
                        case var type when type.Contains("point"):
                            DrawFeatures(features, _geomColumn, "point", sourceEpsg, targetEpsg, table.Table);
                            break;
                        case var type when type.Contains("line"):
                            DrawFeatures(features, _geomColumn, "line", sourceEpsg, targetEpsg, table.Table);
                            break;
                        case var type when type.Contains("polygon"):
                            DrawFeatures(features, _geomColumn, "polygon", sourceEpsg, targetEpsg, table.Table);
                            break;
                        default:
                            StatusText.Text = $"Unsupported geometry type: {_geomType}";
                            return;
                    }

                    // === Record metadata for this non-split layer ===
                    var meta = new PgLayerMeta
                    {
                        AcadLayer = table.Table,
                        Host = settings.Host,
                        Database = settings.Database,
                        Username = settings.Username,
                        Schema = table.Schema,
                        Table = table.Table,
                        GeomColumn = table.GeomColumn,
                        GeomType = table.GeomType,
                        Srid = table.Srid,
                        ImportSql = sql,
                        LastImported = DateTime.Now
                    };
                    var dwgName = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument?.Name;
                    System.Diagnostics.Debug.WriteLine("Current DWG for meta: " + dwgName);
                    meta.DwgFile = dwgName;
                    PgLayerMetadata.AddOrUpdate(meta);

                    StatusText.Text = $"Imported {features.Count} features from {table.DisplayName}.";
                }

                Close();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Import failed: {ex.Message}";
            }
        }


        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        // DrawFeatures is the same as in your PgImportWindow (reuse that code here)
        private void DrawFeatures(List<Dictionary<string, object>> features, string geomColumn, string type,
                          int sourceEpsg, int targetEpsg, string acadLayer)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            var db = doc.Database;
            var ed = doc.Editor;

            var factory = new CoordinateSystemFactory();
            var transformFactory = new CoordinateTransformationFactory();
            var sourceCrs = factory.CreateFromWkt(FoldersWindow.GetWktForEpsg(sourceEpsg));
            var targetCrs = factory.CreateFromWkt(FoldersWindow.GetWktForEpsg(targetEpsg));
            var transform = transformFactory.CreateFromCoordinateSystems(sourceCrs, targetCrs);

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!layerTable.Has(acadLayer))
                {
                    layerTable.UpgradeOpen();
                    var newLayer = new LayerTableRecord { Name = acadLayer };
                    layerTable.Add(newLayer);
                    tr.AddNewlyCreatedDBObject(newLayer, true);
                }

                var layerId = layerTable[acadLayer];
                var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                foreach (var feature in features)
                {
                    var ntsGeometry = feature[geomColumn] as NetTopologySuite.Geometries.Geometry;
                    if (ntsGeometry != null)
                    {
                        // Handle Point
                        var point = ntsGeometry as NetTopologySuite.Geometries.Point;
                        if (point != null)
                        {
                            var coords = transform.MathTransform.Transform(new[] { point.X, point.Y });
                            var insertPoint = new Point3d(coords[0], coords[1], point.Coordinate.Z);
                            var acadPoint = new DBPoint(insertPoint) { LayerId = layerId };
                            modelSpace.AppendEntity(acadPoint);
                            tr.AddNewlyCreatedDBObject(acadPoint, true);
                            continue;
                        }
                        // Handle LineString
                        var line = ntsGeometry as NetTopologySuite.Geometries.LineString;
                        if (line != null)
                        {
                            var pline = new Polyline();
                            for (int i = 0; i < line.NumPoints; i++)
                            {
                                var coord = line.GetCoordinateN(i);
                                var coords = transform.MathTransform.Transform(new[] { coord.X, coord.Y });
                                pline.AddVertexAt(i, new Point2d(coords[0], coords[1]), 0, 0, 0);
                            }
                            pline.LayerId = layerId;
                            modelSpace.AppendEntity(pline);
                            tr.AddNewlyCreatedDBObject(pline, true);
                            continue;
                        }
                        // Handle Polygon
                        var polygon = ntsGeometry as NetTopologySuite.Geometries.Polygon;
                        if (polygon != null)
                        {
                            var ext = polygon.ExteriorRing;
                            var pline = new Polyline();
                            for (int i = 0; i < ext.NumPoints; i++)
                            {
                                var coord = ext.GetCoordinateN(i);
                                var coords = transform.MathTransform.Transform(new[] { coord.X, coord.Y });
                                pline.AddVertexAt(i, new Point2d(coords[0], coords[1]), 0, 0, 0);
                            }
                            pline.Closed = true;
                            pline.LayerId = layerId;
                            modelSpace.AppendEntity(pline);
                            tr.AddNewlyCreatedDBObject(pline, true);
                            continue;
                        }
                        // Handle MultiPoint
                        var multipoint = ntsGeometry as NetTopologySuite.Geometries.MultiPoint;
                        if (multipoint != null)
                        {
                            foreach (NetTopologySuite.Geometries.Point pt in multipoint.Geometries)
                            {
                                var coords = transform.MathTransform.Transform(new[] { pt.X, pt.Y });
                                var insertPoint = new Point3d(coords[0], coords[1], pt.Coordinate.Z);
                                var acadPoint = new DBPoint(insertPoint) { LayerId = layerId };
                                modelSpace.AppendEntity(acadPoint);
                                tr.AddNewlyCreatedDBObject(acadPoint, true);
                            }
                            continue;
                        }
                        // Handle MultiLineString
                        var multiline = ntsGeometry as NetTopologySuite.Geometries.MultiLineString;
                        if (multiline != null)
                        {
                            foreach (NetTopologySuite.Geometries.LineString l in multiline.Geometries)
                            {
                                var pline = new Polyline();
                                for (int i = 0; i < l.NumPoints; i++)
                                {
                                    var coord = l.GetCoordinateN(i);
                                    var coords = transform.MathTransform.Transform(new[] { coord.X, coord.Y });
                                    pline.AddVertexAt(i, new Point2d(coords[0], coords[1]), 0, 0, 0);
                                }
                                pline.LayerId = layerId;
                                modelSpace.AppendEntity(pline);
                                tr.AddNewlyCreatedDBObject(pline, true);
                            }
                            continue;
                        }
                        // Handle MultiPolygon
                        var multipolygon = ntsGeometry as NetTopologySuite.Geometries.MultiPolygon;
                        if (multipolygon != null)
                        {
                            foreach (NetTopologySuite.Geometries.Polygon pol in multipolygon.Geometries)
                            {
                                var ext = pol.ExteriorRing;
                                var pline = new Polyline();
                                for (int i = 0; i < ext.NumPoints; i++)
                                {
                                    var coord = ext.GetCoordinateN(i);
                                    var coords = transform.MathTransform.Transform(new[] { coord.X, coord.Y });
                                    pline.AddVertexAt(i, new Point2d(coords[0], coords[1]), 0, 0, 0);
                                }
                                pline.Closed = true;
                                pline.LayerId = layerId;
                                modelSpace.AppendEntity(pline);
                                tr.AddNewlyCreatedDBObject(pline, true);
                            }
                            continue;
                        }
                    }
                    else
                    {
                        // For debugging: log the unexpected type
                        Console.WriteLine("Type of feature[\"geom\"]: " + (feature[geomColumn]?.GetType().FullName ?? "null"));
                    }
                }

                tr.Commit();
            }

            ed.WriteMessage($"\nLoaded {features.Count} {type} features into '{acadLayer}'.");
        }

        public List<string> GetUniqueValues(string columnName, string tableSchema, string tableName)
        {
            var list = new List<string>();
            var settings = PgSettings.Load();
            using var conn = new NpgsqlConnection(
                $"Host={settings.Host};Username={settings.Username};Password={settings.Password};Database={settings.Database};Port={settings.Port};");
            conn.Open();
            using var cmd = new NpgsqlCommand(
                $"SELECT DISTINCT \"{columnName}\" FROM \"{tableSchema}\".\"{tableName}\" ORDER BY 1 LIMIT 100", conn); // limit for UI performance
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var v = reader[0]?.ToString();
                if (!string.IsNullOrEmpty(v)) list.Add(v);
            }
            return list;
        }

        public class QueryConditionRow : StackPanel
        {
            private ComboBox ColumnCombo, OperatorCombo, ValueBox;
            private TableInfo ParentTable;
            private List<ColumnInfo> Columns;

            public event EventHandler ConditionChanged;

            public QueryConditionRow(List<ColumnInfo> columns, TableInfo parentTable)
            {
                Columns = columns;
                ParentTable = parentTable;
                Orientation = Orientation.Horizontal;
                Margin = new Thickness(0, 3, 0, 3);

                ColumnCombo = new ComboBox { Width = 130, Margin = new Thickness(0, 0, 6, 0), ItemsSource = columns, DisplayMemberPath = "Name" };
                if (columns.Count > 0) ColumnCombo.SelectedIndex = 0;

                OperatorCombo = new ComboBox { Width = 60, Margin = new Thickness(0, 0, 6, 0), ItemsSource = new[] { "=", "<>", ">", "<", ">=", "<=", "LIKE" } };
                OperatorCombo.SelectedIndex = 0;

                ValueBox = new ComboBox { Width = 100, Margin = new Thickness(0, 0, 6, 0), IsEditable = true };

                ColumnCombo.SelectionChanged += async (s, e) =>
                {
                    if (ColumnCombo.SelectedItem is ColumnInfo col)
                    {
                        var win = Window.GetWindow(this) as PgQueryImportWindow;
                        if (win != null)
                        {
                            var values = await Task.Run(() => win.GetUniqueValues(col.Name, ParentTable.Schema, ParentTable.Table));
                            ValueBox.ItemsSource = values;
                        }
                    }
                    ConditionChanged?.Invoke(this, EventArgs.Empty);
                };

                OperatorCombo.SelectionChanged += (s, e) => ConditionChanged?.Invoke(this, EventArgs.Empty);
                ValueBox.SelectionChanged += (s, e) => ConditionChanged?.Invoke(this, EventArgs.Empty);
                ValueBox.LostFocus += (s, e) => ConditionChanged?.Invoke(this, EventArgs.Empty);

                Children.Add(ColumnCombo);
                Children.Add(OperatorCombo);
                Children.Add(ValueBox);
            }

            public string ToSql()
            {
                if (ColumnCombo.SelectedItem is not ColumnInfo col || string.IsNullOrWhiteSpace(ValueBox.Text))
                    return "";

                string op = OperatorCombo.SelectedItem?.ToString() ?? "=";
                string value = ValueBox.Text.Trim().Replace("'", "''");

                bool isNumber = col.DataType.Contains("int") || col.DataType.Contains("double") || col.DataType.Contains("numeric") || col.DataType.Contains("real");
                string valStr = isNumber ? value : $"'{value}'";
                if (op == "LIKE") valStr = $"'%{value}%'";

                return $"\"{col.Name}\" {op} {valStr}";
            }
        }
    }
}
