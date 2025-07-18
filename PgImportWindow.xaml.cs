using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Npgsql;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.DatabaseServices;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;
using NetTopologySuite.Geometries;
using Geometry = NetTopologySuite.Geometries.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace ArcGisAutoCAD
{
    public class PgTableInfo
    {
        public string Schema { get; set; }
        public string Table { get; set; }
        public string GeomColumn { get; set; }
        public string GeomType { get; set; }
        public int Srid { get; set; }
        public string DisplayName => $"{Schema}.{Table} ({GeomType}, SRID={Srid})";
    }

    public partial class PgImportWindow : Window
    {
        private readonly List<PgTableInfo> _geometryTables = new();

        public PgImportWindow()
        {
            InitializeComponent();
            TablesCombo.SelectionChanged += TablesCombo_SelectionChanged;
            NpgsqlConnection.GlobalTypeMapper.UseNetTopologySuite();

            var settings = PgSettings.Load();
            SourceCrsBox.Text = settings.SourceEpsg.ToString();
            TargetCrsBox.Text = settings.TargetEpsg.ToString();

            LoadGeometryTables(settings);
        }

        private void LoadGeometryTables(PgSettings settings)
        {
            try
            {
                _geometryTables.Clear();

                using var conn = new NpgsqlConnection(
                    $"Host={settings.Host};Username={settings.Username};Password={settings.Password};Database={settings.Database};Port={settings.Port};");
                conn.Open();

                var cmd = new NpgsqlCommand(
                    "SELECT f_table_schema, f_table_name, f_geometry_column, type, srid " +
                    "FROM public.geometry_columns ORDER BY f_table_schema, f_table_name;", conn);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    _geometryTables.Add(new PgTableInfo
                    {
                        Schema = reader.GetString(0),
                        Table = reader.GetString(1),
                        GeomColumn = reader.GetString(2),
                        GeomType = reader.GetString(3),
                        Srid = reader.GetInt32(4)
                    });
                }

                TablesCombo.ItemsSource = _geometryTables;
                if (_geometryTables.Any())
                {
                    TablesCombo.SelectedIndex = 0;
                    SourceCrsBox.Text = _geometryTables[0].Srid.ToString();
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error loading tables: {ex.Message}";
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

        private void TablesCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (TablesCombo.SelectedItem is PgTableInfo selectedTable)
            {
                UpdateSplitAttributeCombo(selectedTable);
            }
        }



        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (TablesCombo.SelectedItem is not PgTableInfo table)
            {
                StatusText.Text = "Please select a table.";
                return;
            }

            int.TryParse(SourceCrsBox.Text, out var sourceEpsg);
            int.TryParse(TargetCrsBox.Text, out var targetEpsg);

            if (sourceEpsg == 0) sourceEpsg = table.Srid;
            if (targetEpsg == 0) targetEpsg = 27700;

            var settings = PgSettings.Load();

            try
            {
                using var conn = new NpgsqlConnection(
                    $"Host={settings.Host};Username={settings.Username};Password={settings.Password};Database={settings.Database};Port={settings.Port};");
                conn.Open();

                string sql = $"SELECT *, (\"{table.GeomColumn}\") AS geom FROM \"{table.Schema}\".\"{table.Table}\"";
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

                        switch (table.GeomType.ToLower())
                        {
                            case var type when type.Contains("point"):
                                ImportPoints(group.ToList(), table, sourceEpsg, targetEpsg, layerName);
                                break;

                            case var type when type.Contains("line"):
                                DrawFeatures(group.ToList(), table.GeomColumn, "line", sourceEpsg, targetEpsg, layerName);
                                break;

                            case var type when type.Contains("polygon"):
                                DrawFeatures(group.ToList(), table.GeomColumn, "polygon", sourceEpsg, targetEpsg, layerName);
                                break;

                            default:
                                // just skip unknown types
                                break;
                        }
                    }

                    StatusText.Text = $"Imported {total} features from {table.DisplayName} (split by '{splitCol}').";
                }
                else
                {
                    switch (table.GeomType.ToLower())
                    {
                        case var type when type.Contains("point"):
                            ImportPoints(features, table, sourceEpsg, targetEpsg);
                            break;

                        case var type when type.Contains("line"):
                            DrawFeatures(features, table.GeomColumn, "line", sourceEpsg, targetEpsg, table.Table);
                            break;

                        case var type when type.Contains("polygon"):
                            DrawFeatures(features, table.GeomColumn, "polygon", sourceEpsg, targetEpsg, table.Table);
                            break;

                        default:
                            StatusText.Text = $"Unsupported geometry type: {table.GeomType}";
                            return;
                    }

                    StatusText.Text = $"Imported {features.Count} features from {table.DisplayName}.";
                }

                Close();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Import failed: {ex.Message}";
            }
        }

        // Overload ImportPoints so you can pass a custom layer name:
        private void ImportPoints(List<Dictionary<string, object>> features, PgTableInfo table, int sourceEpsg, int targetEpsg, string customLayerName = null)
        {
            string chosenBlock = null;
            string attrField = null;
            bool useBlock = false;

            var blockNames = FoldersWindow.GetBlockNames();
            var dialog = new BlockPromptDialog(blockNames) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                useBlock = dialog.UseBlock;
                if (useBlock)
                {
                    chosenBlock = dialog.SelectedBlockName;

                    var attrTags = FoldersWindow.GetBlockAttributeTags(AcApp.DocumentManager.MdiActiveDocument.Database, chosenBlock);
                    var featureFields = features[0].Keys.Where(k => !k.Equals(table.GeomColumn, StringComparison.OrdinalIgnoreCase)).ToList();

                    if (attrTags.Any() && featureFields.Any())
                    {
                        var attrDialog = new AttributeFieldPickerDialog(featureFields) { Owner = this };
                        if (attrDialog.ShowDialog() == true)
                            attrField = attrDialog.SelectedField;
                    }
                }
            }

            DrawFeatures(features, table.GeomColumn, "point", sourceEpsg, targetEpsg, customLayerName ?? table.Table, chosenBlock, attrField);
        }


        private void DrawFeatures(List<Dictionary<string, object>> features, string geomColumn, string type,
                          int sourceEpsg, int targetEpsg, string acadLayer, string blockName = null, string attrField = null)
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
                    var ntsGeometry = feature["geom"] as NetTopologySuite.Geometries.Geometry;
                    if (ntsGeometry != null)
                    {
                        var point = ntsGeometry as NetTopologySuite.Geometries.Point;
                        if (point != null)
                        {
                            InsertPoint(point, transform, modelSpace, layerId, tr, feature, blockTable, blockName, attrField);
                            continue;
                        }
                        var line = ntsGeometry as NetTopologySuite.Geometries.LineString;
                        if (line != null)
                        {
                            InsertLineString(line, transform, modelSpace, layerId, tr);
                            continue;
                        }
                        var polygon = ntsGeometry as NetTopologySuite.Geometries.Polygon;
                        if (polygon != null)
                        {
                            InsertPolygon(polygon, transform, modelSpace, layerId, tr);
                            continue;
                        }
                        var multipoint = ntsGeometry as NetTopologySuite.Geometries.MultiPoint;
                        if (multipoint != null)
                        {
                            foreach (NetTopologySuite.Geometries.Point pt in multipoint.Geometries)
                                InsertPoint(pt, transform, modelSpace, layerId, tr, feature, blockTable, blockName, attrField);
                            continue;
                        }
                        var multiline = ntsGeometry as NetTopologySuite.Geometries.MultiLineString;
                        if (multiline != null)
                        {
                            foreach (NetTopologySuite.Geometries.LineString l in multiline.Geometries)
                                InsertLineString(l, transform, modelSpace, layerId, tr);
                            continue;
                        }
                        var multipolygon = ntsGeometry as NetTopologySuite.Geometries.MultiPolygon;
                        if (multipolygon != null)
                        {
                            foreach (NetTopologySuite.Geometries.Polygon pol in multipolygon.Geometries)
                                InsertPolygon(pol, transform, modelSpace, layerId, tr);
                            continue;
                        }
                    }
                    else
                    {
                        // For debugging: what type is it?
                        Console.WriteLine("Type of feature[\"geom\"]: " + (feature["geom"]?.GetType().FullName ?? "null"));
                    }
                }


                tr.Commit();
            }

            ed.WriteMessage($"\nLoaded {features.Count} {type} features into '{acadLayer}'.");
        }


        private void InsertPoint(NetTopologySuite.Geometries.Point pt, ICoordinateTransformation transform, BlockTableRecord btr, ObjectId layerId,
                                 Transaction tr, Dictionary<string, object> feature, BlockTable bt, string blockName, string attrField)
        {
            var coords = transform.MathTransform.Transform(new[] { pt.X, pt.Y });
            var insertPoint = new Point3d(coords[0], coords[1], 0);

            if (!string.IsNullOrWhiteSpace(blockName) && bt.Has(blockName))
            {
                var blkId = bt[blockName];
                var br = new BlockReference(insertPoint, blkId) { LayerId = layerId };
                btr.AppendEntity(br);
                tr.AddNewlyCreatedDBObject(br, true);

                var blkDef = (BlockTableRecord)tr.GetObject(blkId, OpenMode.ForRead);
                foreach (ObjectId id in blkDef)
                {
                    if (id.ObjectClass.DxfName != "ATTDEF") continue;

                    var attDef = (AttributeDefinition)tr.GetObject(id, OpenMode.ForRead);
                    var attRef = new AttributeReference();
                    attRef.SetAttributeFromBlock(attDef, br.BlockTransform);
                    attRef.TextString = !string.IsNullOrWhiteSpace(attrField) && feature.TryGetValue(attrField, out var val)
                        ? val?.ToString()
                        : attDef.TextString;

                    br.AttributeCollection.AppendAttribute(attRef);
                    tr.AddNewlyCreatedDBObject(attRef, true);
                }
            }
            else
            {
                var acadPoint = new DBPoint(insertPoint) { LayerId = layerId };
                btr.AppendEntity(acadPoint);
                tr.AddNewlyCreatedDBObject(acadPoint, true);
            }
        }

        private void InsertLineString(NetTopologySuite.Geometries.LineString ls, ICoordinateTransformation transform, BlockTableRecord btr, ObjectId layerId, Transaction tr)
        {
            var pline = new Polyline();
            for (int i = 0; i < ls.NumPoints; i++)
            {
                var coord = ls.GetCoordinateN(i); // Fix for CS1061
                var coords = transform.MathTransform.Transform(new[] { coord.X, coord.Y });
                pline.AddVertexAt(i, new Point2d(coords[0], coords[1]), 0, 0, 0);
            }
            pline.LayerId = layerId;
            btr.AppendEntity(pline);
            tr.AddNewlyCreatedDBObject(pline, true);
        }

        private void InsertPolygon(NetTopologySuite.Geometries.Polygon poly, ICoordinateTransformation transform, BlockTableRecord btr, ObjectId layerId, Transaction tr)
        {
            InsertLineString(poly.ExteriorRing, transform, btr, layerId, tr);
        }
    }
}
