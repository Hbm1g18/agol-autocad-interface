using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ArcGisAutoCAD
{
    // Describes a PostGIS-imported AutoCAD layer and its source parameters
    public class PgLayerMeta
    {
        public string AcadLayer { get; set; }         // The name of the AutoCAD layer
        public string Host { get; set; }              // PostGIS server
        public string Database { get; set; }          // Database name
        public string Username { get; set; }          // Username used for import
        public string Schema { get; set; }            // Table schema
        public string Table { get; set; }             // Table name
        public string GeomColumn { get; set; }        // Geometry column
        public string GeomType { get; set; }          // Type (Point, LineString, Polygon, etc)
        public int Srid { get; set; }                 // Source SRID
        public string ImportSql { get; set; }         // Custom SQL (if imported by query)
        public string DwgFile { get; set; }
        public DateTime LastImported { get; set; }    // Timestamp of last import

        // Optionally add more properties if you want (e.g. split by attribute, target CRS, etc.)
    }

    public static class PgLayerMetadata
    {
        private static string MetaPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArcGisAutoCAD", "pg_layer_metadata.json");

        // Loads all known PostGIS-imported layers for this user
        public static List<PgLayerMeta> Load()
        {
            if (File.Exists(MetaPath))
                return JsonConvert.DeserializeObject<List<PgLayerMeta>>(File.ReadAllText(MetaPath));
            return new List<PgLayerMeta>();
        }

        // Saves the full list (usually you don't need to call this directly)
        public static void Save(List<PgLayerMeta> items)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MetaPath));
            File.WriteAllText(MetaPath, JsonConvert.SerializeObject(items, Formatting.Indented));
        }

        // Adds or updates a metadata record for a layer (by AcadLayer name)
        public static void AddOrUpdate(PgLayerMeta meta)
        {
            var items = Load();
            var existing = items.Find(x => x.AcadLayer == meta.AcadLayer);
            if (existing != null)
                items.Remove(existing);
            items.Add(meta);
            Save(items);
        }

        // Removes metadata for a specific layer by name
        public static void Remove(string acadLayer)
        {
            var items = Load();
            items.RemoveAll(x => x.AcadLayer == acadLayer);
            Save(items);
        }
        public static void CleanupForOpenDrawings()
        {
            // List of all open DWG full paths
            var openDwgFiles = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager
                .Cast<Autodesk.AutoCAD.ApplicationServices.Document>()
                .Select(d => d.Name)
                .ToHashSet(System.StringComparer.OrdinalIgnoreCase);

            var items = Load();
            var cleaned = items
                .Where(x => !string.IsNullOrEmpty(x.DwgFile) && openDwgFiles.Contains(x.DwgFile))
                .ToList();
            Save(cleaned);
        }
    }
}
