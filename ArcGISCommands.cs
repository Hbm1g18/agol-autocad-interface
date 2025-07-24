﻿using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Windows;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using Newtonsoft.Json;
using System.Windows.Input;
using Autodesk.AutoCAD.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;
using AcDocument = Autodesk.AutoCAD.ApplicationServices.Document;

[assembly: CommandClass(typeof(ArcGisAutoCAD.ArcGisRibbonPlugin))]
[assembly: CommandClass(typeof(ArcGisAutoCAD.ArcCommands))]

namespace ArcGisAutoCAD
{
    public class ArcGisRibbonPlugin : IExtensionApplication
    {
        public void Initialize()
        {
            PgLayerMetadata.CleanupForOpenDrawings();
            if (ComponentManager.Ribbon == null)
            {
                ComponentManager.ItemInitialized += OnRibbonReady;
            }
            else
            {
                CreateRibbon();
            }
        }

        public void Terminate() { }

        private void OnRibbonReady(object sender, RibbonItemEventArgs e)
        {
            if (ComponentManager.Ribbon != null)
            {
                ComponentManager.ItemInitialized -= OnRibbonReady;
                CreateRibbon();
            }
        }

        private void CreateRibbon()
        {
            var ribbon = ComponentManager.Ribbon;

            foreach (RibbonTab tab in ribbon.Tabs)
            {
                if (tab.Id == "ArcGISTab")
                    return;
            }

            var customTab = new RibbonTab
            {
                Title = "CADGIS Tools",
                Id = "ArcGISTab"
            };
            ribbon.Tabs.Add(customTab);

            var panelSource = new RibbonPanelSource
            {
                Title = "AGOL Utilities"
            };
            var panel = new RibbonPanel { Source = panelSource };
            customTab.Panels.Add(panel);

            var settingsBtn = new RibbonButton
            {
                Text = "Settings",
                ShowText = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                LargeImage = LoadImageResource("settings.png"),
                ShowImage = true,
                CommandHandler = new RibbonCommandHandler("ARCSETTINGS")
            };

            var foldersBtn = new RibbonButton
            {
                Text = "Import",
                ShowText = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                LargeImage = LoadImageResource("import.png"),
                CommandHandler = new RibbonCommandHandler("ARCFOLDERS")
            };

            panelSource.Items.Add(settingsBtn);
            panelSource.Items.Add(foldersBtn);

            var postgisPanelSource = new RibbonPanelSource
            {
                Title = "PostGIS"
            };
            var postgisPanel = new RibbonPanel { Source = postgisPanelSource };
            customTab.Panels.Add(postgisPanel);

            var pgSettingsBtn = new RibbonButton
            {
                Text = "PG Settings",
                ShowText = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                LargeImage = LoadImageResource("settings.png"),
                ShowImage = true,
                CommandHandler = new RibbonCommandHandler("PGSETTINGS")
            };
            var pgImportBtn = new RibbonButton
            {
                Text = "Import Table",
                ShowText = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                LargeImage = LoadImageResource("postgres.png"),
                ShowImage = true,
                CommandHandler = new RibbonCommandHandler("PGIMPORT")
            };
            var pgQueryImportBtn = new RibbonButton
            {
                Text = "Import by Query",
                ShowText = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                LargeImage = LoadImageResource("queryimport.png"),
                ShowImage = true,
                CommandHandler = new RibbonCommandHandler("PGQUERYIMPORT")
            };
            var pgRefreshBtn = new RibbonButton
            {
                Text = "Refresh Layer(s)",
                ShowText = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                LargeImage = LoadImageResource("refreshpg.png"),
                ShowImage = true,
                CommandHandler = new RibbonCommandHandler("PGREFRESH")
            };

            postgisPanelSource.Items.Add(pgSettingsBtn);
            postgisPanelSource.Items.Add(pgImportBtn);
            postgisPanelSource.Items.Add(pgQueryImportBtn);
            postgisPanelSource.Items.Add(pgRefreshBtn);
        }

        private BitmapImage LoadImageResource(string imageName)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(name => name.EndsWith(imageName));

                if (resourceName != null)
                {
                    using (var stream = assembly.GetManifestResourceStream(resourceName))
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.StreamSource = stream;
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        return bitmap;
                    }
                }

                var imagePath = Path.Combine(Path.GetDirectoryName(assembly.Location), "Resources", imageName);
                if (File.Exists(imagePath))
                {
                    return new BitmapImage(new Uri(imagePath));
                }

                return null;
            }
            catch
            {
                return null;
            }
        }
    }

    public class RibbonCommandHandler : ICommand
    {
        private readonly string _command;

        public RibbonCommandHandler(string command)
        {
            _command = command;
        }

        public bool CanExecute(object parameter) => true;

        public void Execute(object parameter)
        {
            var doc = AcApplication.DocumentManager.MdiActiveDocument;
            doc.SendStringToExecute($"{_command} ", true, false, true);
        }

        public event EventHandler CanExecuteChanged { add { } remove { } }
    }

    public class ArcCommands
    {
        [CommandMethod("ARCSETTINGS")]
        public void ArcSettingsCommand()
        {
            var loginWindow = new LoginWindow();
            bool? result = loginWindow.ShowDialog();

            if (result == true)
            {
                var ed = AcApplication.DocumentManager.MdiActiveDocument.Editor;
                var username = ArcSettings.Load().Username;
                ed.WriteMessage($"\nSuccessfully logged in as {username}");
            }
        }

        [CommandMethod("ARCFOLDERS")]
        public async void ArcFoldersCommand()
        {
            Editor ed = AcApplication.DocumentManager.MdiActiveDocument.Editor;
            ArcSettings settings = ArcSettings.Load();

            if (string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.Password))
            {
                ed.WriteMessage("\nPlease run ARCSETTINGS to save your ArcGIS credentials first.");
                return;
            }

            var client = new ArcGisServiceFinder(settings.Username, settings.Password);

            try
            {
                ed.WriteMessage("\nLogging in...");
                bool loggedIn = await client.GenerateTokenAsync();
                if (!loggedIn)
                {
                    ed.WriteMessage("\nLogin failed. Check your credentials.");
                    return;
                }

                var folders = await client.GetFoldersAsync();

                var window = new FoldersWindow(folders);
                window.ShowDialog();
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nError: {ex.Message}");
            }
        }

        [CommandMethod("PGSETTINGS")]
        public void PgSettingsCommand()
        {
            var window = new PgLoginWindow();
            bool? result = window.ShowDialog();
            var ed = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument.Editor;
            if (result == true)
            {
                ed.WriteMessage("\nPostGIS connection saved.");
            }
            else
            {
                ed.WriteMessage("\nPostGIS login canceled or failed.");
            }
        }

        [CommandMethod("PGIMPORT")]
        public void PgImportCommand()
        {
            var window = new PgImportWindow();
            window.ShowDialog();
        }

        [CommandMethod("PGQUERYIMPORT")]
        public void PgQueryImportCommand()
        {
            var window = new PgQueryImportWindow();
            window.ShowDialog();
        }

        [CommandMethod("PGREFRESH")]
        public void PgRefreshCommand()
        {
            var metas = PgLayerMetadata.Load();
            var ed = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument.Editor;

            if (metas.Count == 0)
            {
                ed.WriteMessage("\nNo PostGIS layers found for refresh.");
                return;
            }

            var dialog = new PgLayerRefreshDialog(metas);
            if (dialog.ShowDialog() != true || dialog.SelectedLayers.Count == 0)
                return;

            foreach (var meta in dialog.SelectedLayers)
            {
                AcadUtils.ClearLayerContents(meta.AcadLayer);
                PgImporter.Import(meta);
            }

            ed.WriteMessage($"\nRefreshed {dialog.SelectedLayers.Count} layer(s) from PostGIS.");
        }
    }

    public class ArcSettings
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public int TargetEpsg { get; set; } = 27700;

        private static string SettingsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArcGisAutoCAD", "settings.json");

        public static ArcSettings Load()
        {
            if (File.Exists(SettingsPath))
                return JsonConvert.DeserializeObject<ArcSettings>(File.ReadAllText(SettingsPath));
            return new ArcSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(this));
        }
    }

    public class FeatureResponse
    {
        [JsonProperty("features")]
        public Feature[] Features { get; set; } = Array.Empty<Feature>();
    }

    public class Feature
    {
        [JsonProperty("attributes")]
        public Dictionary<string, object> Attributes { get; set; } = new Dictionary<string, object>();

        [JsonProperty("geometry")]
        public Geometry Geometry { get; set; }
    }

    public class Geometry
    {
        [JsonProperty("x")]
        public double? X { get; set; }

        [JsonProperty("y")]
        public double? Y { get; set; }

        [JsonProperty("paths")]
        public List<List<double[]>> Paths { get; set; }

        [JsonProperty("rings")]
        public List<List<double[]>> Rings { get; set; }
    }

    public class ArcGisServiceFinder
    {
        private readonly HttpClient _httpClient;
        private readonly string _username;
        private readonly string _password;
        private string _token;

        private const string TokenUrl = "https://www.arcgis.com/sharing/rest/generateToken";
        private const string PortalUrl = "https://www.arcgis.com/sharing/rest";

        public ArcGisServiceFinder(string username, string password)
        {
            _httpClient = new HttpClient();
            _username = username;
            _password = password;
        }

        public async Task<bool> GenerateTokenAsync()
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("username", _username),
                new KeyValuePair<string, string>("password", _password),
                new KeyValuePair<string, string>("referer", "https://www.arcgis.com"),
                new KeyValuePair<string, string>("expiration", "60"),
                new KeyValuePair<string, string>("f", "json")
            });

            var response = await _httpClient.PostAsync(TokenUrl, content);
            string json = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<TokenResponse>(json);

            if (!string.IsNullOrWhiteSpace(result?.Token))
            {
                _token = result.Token;
                return true;
            }

            return false;
        }

        public async Task<List<Folder>> GetFoldersAsync()
        {
            string userUrl = $"{PortalUrl}/community/self?f=json&token={_token}";
            string userJson = await _httpClient.GetStringAsync(userUrl);
            var userInfo = JsonConvert.DeserializeObject<UserInfo>(userJson);

            string contentUrl = $"{PortalUrl}/content/users/{userInfo.Username}?f=json&token={_token}";
            string contentJson = await _httpClient.GetStringAsync(contentUrl);
            var contentInfo = JsonConvert.DeserializeObject<ContentResponse>(contentJson);

            List<Folder> folders = new List<Folder>(contentInfo.Folders ?? Array.Empty<Folder>());
            folders.Insert(0, new Folder { Id = "", Title = "Root" });
            return folders;
        }

        public async Task<List<Item>> GetFeatureServicesAsync(string folderId)
        {
            string userUrl = $"{PortalUrl}/community/self?f=json&token={_token}";
            var userResponse = await _httpClient.GetStringAsync(userUrl);
            var userInfo = JsonConvert.DeserializeObject<UserInfo>(userResponse);

            string folderUrl = string.IsNullOrEmpty(folderId)
                ? $"{PortalUrl}/content/users/{userInfo.Username}?f=json&token={_token}"
                : $"{PortalUrl}/content/users/{userInfo.Username}/{folderId}?f=json&token={_token}";

            var folderResponse = await _httpClient.GetStringAsync(folderUrl);
            var folderContent = JsonConvert.DeserializeObject<ContentResponse>(folderResponse);

            List<Item> services = new List<Item>();
            foreach (var item in folderContent.Items)
            {
                if (item.Type.Equals("Feature Service", StringComparison.OrdinalIgnoreCase))
                {
                    services.Add(item);
                }
            }

            return services;
        }

        public async Task<ServiceItem> GetServiceInfoAsync(string serviceId)
        {
            string itemUrl = $"https://www.arcgis.com/sharing/rest/content/items/{serviceId}?f=json&token={_token}";
            var itemResponse = await _httpClient.GetStringAsync(itemUrl);
            var itemData = JsonConvert.DeserializeObject<ServiceItem>(itemResponse);

            if (!string.IsNullOrEmpty(itemData?.Url))
            {
                string layersUrl = $"{itemData.Url}?f=json&token={_token}";
                var layersResponse = await _httpClient.GetStringAsync(layersUrl);
                itemData.ServiceInfo = JsonConvert.DeserializeObject<ServiceInfo>(layersResponse);
            }

            return itemData;
        }

        public async Task<FeatureResponse> QueryLayerDataAsync(string serviceUrl, int layerId)
        {
            string queryUrl = $"{serviceUrl}/{layerId}/query?where=1=1&outFields=*&f=json&token={_token}";
            var queryResponse = await _httpClient.GetStringAsync(queryUrl);
            return JsonConvert.DeserializeObject<FeatureResponse>(queryResponse);
        }
    }

    public class TokenResponse
    {
        [JsonProperty("token")]
        public string Token { get; set; }
    }

    public class UserInfo
    {
        [JsonProperty("username")]
        public string Username { get; set; }
    }

    public class ContentResponse
    {
        [JsonProperty("folders")]
        public Folder[] Folders { get; set; } = Array.Empty<Folder>();

        [JsonProperty("items")]
        public Item[] Items { get; set; } = Array.Empty<Item>();
    }

    public class Item
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }
    }

    public class Folder
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }
    }

    public class ServiceItem
    {
        [JsonProperty("url")]
        public string Url { get; set; }

        [JsonIgnore]
        public ServiceInfo ServiceInfo { get; set; }

        public Layer[] Layers => ServiceInfo?.Layers;
    }

    public class ServiceInfo
    {
        [JsonProperty("layers")]
        public Layer[] Layers { get; set; }
    }

    public class Layer
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("geometryType")]
        public string GeometryType { get; set; }
    }
}
