using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.DatabaseServices;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using System.Reflection;
using System.IO;

namespace ArcGisAutoCAD
{
    public class LayerItem
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public bool IsSelected { get; set; }
    }

    public partial class FoldersWindow : Window
    {
        private readonly List<Folder> _folders;
        private List<LayerItem> _layerItems = new();
        private readonly ArcGisServiceFinder _client;

        private static bool standardBlocksImported = false;

        public FoldersWindow(List<Folder> folders)
        {
            InitializeComponent();
            _folders = folders;
            FoldersDropdown.ItemsSource = _folders;
            FoldersDropdown.DisplayMemberPath = "Title";

            var settings = ArcSettings.Load();
            _client = new ArcGisServiceFinder(settings.Username, settings.Password);
        }

        private async void FoldersDropdown_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            LayersListBox.ItemsSource = null;
            if (FoldersDropdown.SelectedItem is Folder selectedFolder)
            {
                await _client.GenerateTokenAsync();
                var services = await _client.GetFeatureServicesAsync(selectedFolder.Id);

                _layerItems = services.Select(s => new LayerItem { Id = s.Id, Title = s.Title, IsSelected = false }).ToList();
                LayersListBox.ItemsSource = _layerItems;
            }
        }

        private async void LoadSelectedLayers_Click(object sender, RoutedEventArgs e)
        {
            var selectedLayers = _layerItems.Where(l => l.IsSelected).ToList();
            if (!selectedLayers.Any())
            {
                MessageBox.Show("Please select at least one layer.");
                return;
            }

            LoadButton.IsEnabled = false;

            int epsg = ArcSettings.Load().TargetEpsg;

            
            foreach (var layer in selectedLayers)
            {
                try
                {
                    await _client.GenerateTokenAsync();
                    var service = await _client.GetServiceInfoAsync(layer.Id);
                    if (service == null)
                    {
                        MessageBox.Show($"Service info for '{layer.Title}' could not be loaded.");
                        continue;
                    }
                    if (service.Layers == null || !service.Layers.Any())
                    {
                        MessageBox.Show($"No sublayers found for '{layer.Title}'.");
                        continue;
                    }
                    string url = service.Url;

                    foreach (var sublayer in service.Layers)
                    {
                        int sublayerId = sublayer.Id;
                        string sublayerName = sublayer.Name ?? $"{layer.Title}_{sublayerId}";
                        var result = await _client.QueryLayerDataAsync(url, sublayerId);
                        var features = result?.Features;
                        if (features == null || features.Length == 0)
                            continue;

                        string geometryType = sublayer.GeometryType?.ToLower() ?? "";

                        bool isPointLayer = geometryType.Contains("point") || features.Any(f => f.Geometry?.X.HasValue == true && f.Geometry?.Y.HasValue == true);
                        bool isLineLayer = geometryType.Contains("line") || features.Any(f => f.Geometry?.Paths != null);
                        bool isPolygonLayer = geometryType.Contains("polygon") || features.Any(f => f.Geometry?.Rings != null);

                        string acadLayerName = sublayerName;

                        string chosenBlockName = null;
                        bool useBlock = false;
                        string blockAttributeField = null;

                        if (isPointLayer)
                        {
                            if (!standardBlocksImported)
                            {
                                string tempDxfPath = ExtractResourceToTempFile("StandardBlocks2.dxf");
                                ImportBlocksFromResource(AcApp.DocumentManager.MdiActiveDocument.Database, tempDxfPath);
                                standardBlocksImported = true;
                            }

                            var blockNames = GetBlockNames();
                            var dialog = new BlockPromptDialog(blockNames) { Owner = this };
                            if (dialog.ShowDialog() == true)
                            {
                                useBlock = dialog.UseBlock;
                                if (useBlock)
                                {
                                    chosenBlockName = dialog.SelectedBlockName;
                                    if (string.IsNullOrEmpty(chosenBlockName) || !blockNames.Contains(chosenBlockName))
                                    {
                                        MessageBox.Show("Invalid block selected.");
                                        continue;
                                    }
                                    var blockAttrTags = GetBlockAttributeTags(AcApp.DocumentManager.MdiActiveDocument.Database, chosenBlockName);
                                    if (blockAttrTags.Count > 0)
                                    {
                                        var sampleAttributes = features.First().Attributes;
                                        var fieldNames = sampleAttributes.Keys
                                            .Where(field => !field.StartsWith("ESRI", StringComparison.OrdinalIgnoreCase))
                                            .ToList();

                                        if (fieldNames.Count == 0)
                                        {
                                            MessageBox.Show("No suitable attribute fields found for block attribute mapping.");
                                            continue;
                                        }

                                        var fieldDialog = new AttributeFieldPickerDialog(fieldNames) { Owner = this };
                                        if (fieldDialog.ShowDialog() == true)
                                            blockAttributeField = fieldDialog.SelectedField;
                                        else
                                            continue;
                                    }
                                }
                            }
                            else
                            {
                                continue;
                            }
                            DrawFeaturesOnCanvas(features, epsg, acadLayerName, chosenBlockName, blockAttributeField);
                        }
                        else if (isLineLayer)
                        {
                            DrawFeaturesOnCanvas(features, epsg, acadLayerName);
                        }
                        else if (isPolygonLayer)
                        {
                            DrawFeaturesOnCanvas(features, epsg, acadLayerName);
                        }
                        else
                        {
                            MessageBox.Show($"Layer '{acadLayerName}' has unsupported or unknown geometry type.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading layer '{layer.Title}': {ex.Message}\n{ex.StackTrace}");
                }
            }


            LoadButton.IsEnabled = true;
            this.Close();
        }

        private void DrawFeaturesOnCanvas(Feature[] features, int epsgCode, string layerName, string blockName = null, string blockAttributeField = null)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            var db = doc.Database;
            var ed = doc.Editor;

            var factory = new CoordinateSystemFactory();
            var tFactory = new CoordinateTransformationFactory();

            var sourceCrs = ProjectedCoordinateSystem.WebMercator;
            var targetCrs = GetProjectionByEpsg(factory, epsgCode);
            var transform = tFactory.CreateFromCoordinateSystems(sourceCrs, targetCrs);

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

                ObjectId layerId;
                if (!lt.Has(layerName))
                {
                    lt.UpgradeOpen();
                    var newLayer = new LayerTableRecord { Name = layerName };
                    layerId = lt.Add(newLayer);
                    tr.AddNewlyCreatedDBObject(newLayer, true);
                }
                else
                {
                    layerId = lt[layerName];
                }

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                foreach (var f in features)
                {
                    if (f.Geometry == null) continue;

                    if (f.Geometry.X.HasValue && f.Geometry.Y.HasValue)
                    {
                        double[] pt = transform.MathTransform.Transform(new[] { f.Geometry.X.Value, f.Geometry.Y.Value });
                        var point = new Point3d(pt[0], pt[1], 0);
                        if (!string.IsNullOrEmpty(blockName) && bt.Has(blockName))
                        {
                            ObjectId blkId = bt[blockName];
                            var br = new BlockReference(point, blkId);
                            br.LayerId = layerId;
                            btr.AppendEntity(br);
                            tr.AddNewlyCreatedDBObject(br, true);

                            var btrDef = (BlockTableRecord)tr.GetObject(blkId, OpenMode.ForRead);
                            foreach (ObjectId id in btrDef)
                            {
                                if (id.ObjectClass.DxfName == "ATTDEF")
                                {
                                    var attDef = (AttributeDefinition)tr.GetObject(id, OpenMode.ForRead);
                                    var attRef = new AttributeReference();
                                    attRef.SetAttributeFromBlock(attDef, br.BlockTransform);

                                    if (!string.IsNullOrEmpty(blockAttributeField))
                                    {
                                        if (f.Attributes.TryGetValue(blockAttributeField, out object val) && val != null)
                                            attRef.TextString = val.ToString();
                                        else
                                            attRef.TextString = "";
                                    }
                                    else
                                    {
                                        attRef.TextString = attDef.TextString;
                                    }

                                    br.AttributeCollection.AppendAttribute(attRef);
                                    tr.AddNewlyCreatedDBObject(attRef, true);
                                }
                            }
                        }
                        else
                        {
                            var circle = new Circle(point, Vector3d.ZAxis, 1);
                            circle.LayerId = layerId;
                            btr.AppendEntity(circle);
                            tr.AddNewlyCreatedDBObject(circle, true);
                        }
                    }
                    else if (f.Geometry.Paths != null)
                    {
                        foreach (var path in f.Geometry.Paths)
                        {
                            var pline = new Polyline();
                            for (int i = 0; i < path.Count; i++)
                            {
                                double[] transformed = transform.MathTransform.Transform(path[i]);
                                pline.AddVertexAt(i, new Point2d(transformed[0], transformed[1]), 0, 0, 0);
                            }
                            pline.LayerId = layerId;
                            btr.AppendEntity(pline);
                            tr.AddNewlyCreatedDBObject(pline, true);
                        }
                    }
                    else if (f.Geometry.Rings != null)
                    {
                        foreach (var ring in f.Geometry.Rings)
                        {
                            var pline = new Polyline();
                            for (int i = 0; i < ring.Count; i++)
                            {
                                double[] transformed = transform.MathTransform.Transform(ring[i]);
                                pline.AddVertexAt(i, new Point2d(transformed[0], transformed[1]), 0, 0, 0);
                            }
                            pline.Closed = true;
                            pline.LayerId = layerId;
                            btr.AppendEntity(pline);
                            tr.AddNewlyCreatedDBObject(pline, true);
                        }
                    }
                }
                tr.Commit();
            }

            ed.WriteMessage($"\nLoaded {features.Length} features into layer '{layerName}'.");
        }


        private ProjectedCoordinateSystem GetProjectionByEpsg(CoordinateSystemFactory cFactory, int epsgCode)
        {
            var mappings = new Dictionary<int, string>
            {
                { 4326, GeographicCoordinateSystem.WGS84.WKT },
                { 3857, ProjectedCoordinateSystem.WebMercator.WKT },
                { 27700, "PROJCS[\"OSGB 1936 / British National Grid\", GEOGCS[\"OSGB 1936\", DATUM[\"OSGB_1936\", SPHEROID[\"Airy 1830\",6377563.396,299.3249646,AUTHORITY[\"EPSG\",\"7001\"]], TOWGS84[375,-111,431,0,0,0,0], AUTHORITY[\"EPSG\",\"6277\"]], PRIMEM[\"Greenwich\",0, AUTHORITY[\"EPSG\",\"8901\"]], UNIT[\"degree\",0.0174532925199433, AUTHORITY[\"EPSG\",\"9122\"]], AUTHORITY[\"EPSG\",\"4277\"]], PROJECTION[\"Transverse_Mercator\"], PARAMETER[\"latitude_of_origin\",49], PARAMETER[\"central_meridian\",-2], PARAMETER[\"scale_factor\",0.9996012717], PARAMETER[\"false_easting\",400000], PARAMETER[\"false_northing\",-100000], UNIT[\"metre\",1, AUTHORITY[\"EPSG\",\"9001\"]], AUTHORITY[\"EPSG\",\"27700\"]]" }
            };

            string wkt = mappings.ContainsKey(epsgCode) ? mappings[epsgCode] : mappings[27700];
            return (ProjectedCoordinateSystem)cFactory.CreateFromWkt(wkt);
        }

        private static List<string> GetBlockNames()
        {
            var names = new List<string>();
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            var db = doc.Database;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId btrId in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                    // Ignore anonymous, layout, or special blocks
                    if (!btr.IsAnonymous && !btr.IsLayout)
                        names.Add(btr.Name);
                }
                tr.Commit();
            }
            return names;
        }

        private static string ExtractResourceToTempFile(string resourceFileName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            string resourceFullName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(resourceFileName));
            if (resourceFullName == null)
                throw new FileNotFoundException("Resource not found: " + resourceFileName);

            string tempPath = Path.Combine(Path.GetTempPath(), resourceFileName);
            using (var stream = assembly.GetManifestResourceStream(resourceFullName))
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                stream.CopyTo(fs);
            }
            return tempPath;
        }

        private static void ImportBlocksFromResource(Database db, string filePath)
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            using (doc.LockDocument())
            {
                using (Database sourceDb = new Database(false, true))
                {
                    if (filePath.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
                    {
                        sourceDb.DxfIn(filePath, null);
                    }
                    else
                    {
                        sourceDb.ReadDwgFile(filePath, FileOpenMode.OpenForReadAndAllShare, true, "");
                    }

                    ObjectIdCollection blockIds = new ObjectIdCollection();
                    using (Transaction tr = sourceDb.TransactionManager.StartTransaction())
                    {
                        BlockTable bt = (BlockTable)tr.GetObject(sourceDb.BlockTableId, OpenMode.ForRead);
                        foreach (ObjectId btrId in bt)
                        {
                            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                            if (!btr.IsAnonymous && !btr.IsLayout)
                            {
                                blockIds.Add(btrId);
                            }
                        }
                        tr.Commit();
                    }
                    IdMapping mapping = new IdMapping();
                    db.WblockCloneObjects(blockIds, db.BlockTableId, mapping, DuplicateRecordCloning.Ignore, false);
                }
            }
        }

        private static List<string> GetBlockAttributeTags(Database db, string blockName)
        {
            var tags = new List<string>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (!bt.Has(blockName))
                    return tags;
                var btr = (BlockTableRecord)tr.GetObject(bt[blockName], OpenMode.ForRead);
                foreach (ObjectId id in btr)
                {
                    if (id.ObjectClass.DxfName == "ATTDEF")
                    {
                        var attDef = (AttributeDefinition)tr.GetObject(id, OpenMode.ForRead);
                        tags.Add(attDef.Tag);
                    }
                }
                tr.Commit();
            }
            return tags;
        }

    }
}
