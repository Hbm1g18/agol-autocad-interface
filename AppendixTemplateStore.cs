// AppendixTemplateStore.cs
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace ArcGisAutoCAD
{
    public static class AppendixTemplateStore
    {
        private static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArcGisAutoCAD", "appendixTemplates.json");

        public static List<AppendixTemplate> Load()
        {
            if (File.Exists(FilePath))
                return JsonConvert.DeserializeObject<List<AppendixTemplate>>(File.ReadAllText(FilePath)) ?? new();
            return new();
        }

        public static void Save(List<AppendixTemplate> templates)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(templates, Formatting.Indented));
        }
    }
}
