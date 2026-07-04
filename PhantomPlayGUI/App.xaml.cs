using System;
using System.Windows;

namespace PhantomPlayGUI
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ApplyTheme(PhantomPlayGUI.Properties.Settings.Default.Theme);
        }

        /// <summary>
        /// Swaps the active theme dictionary (index 0 of the app's merged dictionaries).
        /// All control styles bind their colors via DynamicResource, so this re-themes live.
        /// </summary>
        public static void ApplyTheme(string theme)
        {
            string file = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
            var dict = new ResourceDictionary { Source = new Uri($"Themes/{file}.xaml", UriKind.Relative) };

            var merged = Current.Resources.MergedDictionaries;
            if (merged.Count > 0)
                merged[0] = dict;
            else
                merged.Add(dict);
        }
    }
}
