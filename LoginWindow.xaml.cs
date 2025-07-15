using System.Windows;
using System.Windows.Controls;

namespace ArcGisAutoCAD
{
    public partial class LoginWindow : Window
    {
        public LoginWindow()
        {
            InitializeComponent();
            var settings = ArcSettings.Load();
            UsernameBox.Text = settings.Username ?? "";
            CrsDropdown.SelectedIndex = settings.TargetEpsg switch
            {
                4326 => 0,
                3857 => 1,
                27700 => 2,
                _ => 3
            };
            // if (CrsDropdown.SelectedIndex == 3)
            // {
            //     CustomCrsBox.Visibility = Visibility.Visible;
            //     CustomCrsBox.Text = settings.TargetEpsg.ToString();
            // }

            if (!string.IsNullOrWhiteSpace(settings.Username))
            {
                StatusText.Text = $"Currently logged in as {settings.Username}";
            }
        }

        private void CrsDropdown_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // CustomCrsBox.Visibility = CrsDropdown.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void Login_Click(object sender, RoutedEventArgs e)
        {
            string username = UsernameBox.Text;
            string password = PasswordBox.Password;

            var client = new ArcGisServiceFinder(username, password);
            StatusText.Text = "Verifying login...";
            bool success = await client.GenerateTokenAsync();

            if (success)
            {
                int epsg = CrsDropdown.SelectedIndex switch
                {
                    0 => 4326,
                    1 => 3857,
                    2 => 27700,
                    // 3 => int.TryParse(CustomCrsBox.Text, out int val) ? val : 27700,
                    _ => 27700
                };

                ArcSettings settings = new ArcSettings
                {
                    Username = username,
                    Password = password,
                    TargetEpsg = epsg
                };
                settings.Save();
                StatusText.Text = $"Currently logged in as {username}";
                this.DialogResult = true;
                this.Close();
            }
            else
            {
                StatusText.Text = "Login failed.";
                StatusText.Foreground = System.Windows.Media.Brushes.Red;
            }
        }

        private void Logout_Click(object sender, RoutedEventArgs e)
        {
            ArcSettings settings = new ArcSettings { Username = "", Password = "", TargetEpsg = 27700 };
            settings.Save();
            StatusText.Text = "Logged out.";
        }
    }
}
