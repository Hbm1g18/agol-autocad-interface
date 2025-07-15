using System;
using System.Windows;
using Npgsql; // Add via NuGet if not already

namespace ArcGisAutoCAD
{
    public partial class PgLoginWindow : Window
    {
        public PgLoginWindow()
        {
            InitializeComponent();
            // Optional: Load previously saved settings
            var settings = PgSettings.Load();
            if (settings != null)
            {
                HostBox.Text = settings.Host;
                UsernameBox.Text = settings.Username;
                DatabaseBox.Text = settings.Database;
                PortBox.Text = settings.Port > 0 ? settings.Port.ToString() : "5432";
            }
        }

        private void Login_Click(object sender, RoutedEventArgs e)
        {
            string host = HostBox.Text.Trim();
            string username = UsernameBox.Text.Trim();
            string password = PasswordBox.Password;
            string database = DatabaseBox.Text.Trim();
            string port = PortBox.Text.Trim();

            string connString = $"Host={host};Username={username};Password={password};Database={database};Port={port};";

            try
            {
                using (var conn = new NpgsqlConnection(connString))
                {
                    conn.Open();
                }
                // Success: Save settings
                var settings = new PgSettings
                {
                    Host = host,
                    Username = username,
                    Password = password,
                    Database = database,
                    Port = int.TryParse(port, out int p) ? p : 5432
                };
                settings.Save();
                this.DialogResult = true;
                this.Close();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Login failed: " + ex.Message;
            }
        }
    }

    // Simple settings class (save as JSON, similar to ArcSettings)
    public class PgSettings
    {
        public string Host { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string Database { get; set; }
        public int Port { get; set; } = 5432;

        private static string SettingsPath =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArcGisAutoCAD", "pgsettings.json");

        public static PgSettings Load()
        {
            if (System.IO.File.Exists(SettingsPath))
                return Newtonsoft.Json.JsonConvert.DeserializeObject<PgSettings>(System.IO.File.ReadAllText(SettingsPath));
            return new PgSettings();
        }

        public void Save()
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath));
            System.IO.File.WriteAllText(SettingsPath, Newtonsoft.Json.JsonConvert.SerializeObject(this));
        }
    }
}
