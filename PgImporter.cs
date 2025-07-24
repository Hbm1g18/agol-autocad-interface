using System;
using System.Collections.Generic;
using Npgsql;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;
using NetTopologySuite.Geometries;

namespace ArcGisAutoCAD
{
    public static class PgImporter
    {
        /// <summary>
        /// Imports features from PostGIS into AutoCAD using metadata (connection, table, geometry, etc).
        /// </summary>
        /// <param name="meta">The PostGIS layer metadata describing what and how to import.</param>
        public static void Import(PgLayerMeta meta)
        {
            // Use credentials from meta, but password from latest PgSettings
            NpgsqlConnection.GlobalTypeMapper.UseNetTopologySuite();
            var settings = PgSettings.Load();

            var features = new List<Dictionary<string, object>>();

            try
            {
                using (var conn = new NpgsqlConnection(
                    $"Host={meta.Host};Username={meta.Username};Password={settings.Password};Database={meta.Database};Port={settings.Port};"))
                {
                    conn.Open();

                    string sql = !string.IsNullOrWhiteSpace(meta.ImportSql)
                        ? meta.ImportSql
                        : $"SELECT *, (\"{meta.GeomColumn}\") AS geom FROM \"{meta.Schema}\".\"{meta.Table}\"";

                    using var cmd = new NpgsqlCommand(sql, conn);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var row = new Dictionary<string, object>();
                        for (int i = 0; i < reader.FieldCount; i++)
                            row[reader.GetName(i)] = reader.GetValue(i);
                        features.Add(row);
                    }
                }

                if (features.Count == 0)
                    return;

                DrawFeatures(features, meta.GeomColumn, meta.GeomType, meta.Srid, 27700, meta.AcadLayer);
            }
            catch (Exception ex)
            {
                var ed = Application.DocumentManager.MdiActiveDocument.Editor;
                ed.WriteMessage($"\nPostGIS refresh failed for layer {meta.AcadLayer}: {ex.Message}");
            }
        }

        private static void DrawFeatures(List<Dictionary<string, object>> features, string geomColumn, string geomType,
            int sourceEpsg, int targetEpsg, string acadLayer)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
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
                    NetTopologySuite.Geometries.Geometry ntsGeometry = null;
                    // Prefer the "geom" alias if present, fallback to actual geometry column name
                    if (feature.ContainsKey("geom") && feature["geom"] is NetTopologySuite.Geometries.Geometry)
                        ntsGeometry = feature["geom"] as NetTopologySuite.Geometries.Geometry;
                    else if (feature.ContainsKey(geomColumn) && feature[geomColumn] is NetTopologySuite.Geometries.Geometry)
                        ntsGeometry = feature[geomColumn] as NetTopologySuite.Geometries.Geometry;

                    if (ntsGeometry != null)
                    {
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
                        ed.WriteMessage("\nFeature geometry is null or of unexpected type.");
                    }
                }

                tr.Commit();
            }

            ed.WriteMessage($"\nLoaded {features.Count} {geomType} features into '{acadLayer}'.");
        }
    }
}
