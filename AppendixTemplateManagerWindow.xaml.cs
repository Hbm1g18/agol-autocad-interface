// AppendixTemplateManagerWindow.xaml.cs
using System.Collections.Generic;
using System.Windows;
using Microsoft.Win32;

namespace ArcGisAutoCAD
{
    public partial class AppendixTemplateManagerWindow : Window
    {
        private List<AppendixTemplate> _templates;

        public AppendixTemplateManagerWindow()
        {
            InitializeComponent();
            LoadTemplates();
        }

        private void LoadTemplates()
        {
            _templates = AppendixTemplateStore.Load();
            TemplatesList.ItemsSource = null;
            TemplatesList.ItemsSource = _templates;
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "AutoCAD Drawing (*.dwg;*.dwt)|*.dwg;*.dwt" };
            if (dlg.ShowDialog() != true) return;

            var namePrompt = new SimplePromptWindow("Enter a name for this template:");
            if (namePrompt.ShowDialog() != true) return;
            string name = namePrompt.ResponseText;

            var descPrompt = new SimplePromptWindow("Enter a description:");
            if (descPrompt.ShowDialog() != true) return;
            string desc = descPrompt.ResponseText;

            _templates.Add(new AppendixTemplate
            {
                Name = name,
                DwgPath = dlg.FileName,
                Description = desc
            });
            AppendixTemplateStore.Save(_templates);
            LoadTemplates();
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (TemplatesList.SelectedItem is AppendixTemplate selected)
            {
                _templates.Remove(selected);
                AppendixTemplateStore.Save(_templates);
                LoadTemplates();
            }
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (TemplatesList.SelectedItem is AppendixTemplate selected)
            {
                var namePrompt = new SimplePromptWindow("Update template name:");
                namePrompt.InputBox.Text = selected.Name;
                if (namePrompt.ShowDialog() != true) return;
                selected.Name = namePrompt.ResponseText;

                var descPrompt = new SimplePromptWindow("Update description:");
                descPrompt.InputBox.Text = selected.Description;
                if (descPrompt.ShowDialog() != true) return;
                selected.Description = descPrompt.ResponseText;

                AppendixTemplateStore.Save(_templates);
                LoadTemplates();
            }
        }
    }
}
