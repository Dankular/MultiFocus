using System.Collections.Generic;
using System.Diagnostics;
using SharpestInjector;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Linq;
using System.IO;
using System;
using WinForms = System.Windows.Forms;

namespace PhantomPlayGUI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        readonly PeFile Dll32;
        readonly PeFile Dll64;

        // Full process list from the last Refresh, before search filtering.
        List<ProcessInfo> allProcesses = new List<ProcessInfo>();
        // Processes we currently consider injected, keyed by process id.
        readonly Dictionary<uint, ProcessInfo> injected = new Dictionary<uint, ProcessInfo>();
        // Process ids the auto-injector already handled (success or failure) to avoid retry storms.
        readonly HashSet<uint> autoInjectAttempted = new HashSet<uint>();

        DispatcherTimer autoInjectTimer;
        WinForms.NotifyIcon trayIcon;

        // True while we load settings, so change handlers don't fight the initial state.
        bool initializing;

        public MainWindow()
        {
            InitializeComponent();

            try
            {
                // Resolve the payload DLLs next to the exe, not against the current working
                // directory. When the app runs elevated the CWD is C:\Windows\System32, so a
                // relative path would fail to load (and the injector writes this full path into
                // the target for LoadLibraryW, so it must be absolute and correct).
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                Dll32 = PeFile.Parse(Path.Combine(dir, "PhantomPlay.dll"));
                Dll64 = PeFile.Parse(Path.Combine(dir, "PhantomPlay64.dll"));
            }
            catch
            {
                // Keep the GUI usable even if the payload DLLs aren't next to the exe yet.
            }

            RestoreWindowPlacement();
        }

        #region Lifetime

        // Restore the last window position/size (before the window is shown, so there's no flicker).
        private void RestoreWindowPlacement()
        {
            var s = Properties.Settings.Default;
            if (!s.WindowPlacementSaved)
                return;

            double w = s.WindowWidth, h = s.WindowHeight, l = s.WindowLeft, t = s.WindowTop;
            if (w < MinWidth || h < MinHeight)
                return; // unset/corrupt

            // Only restore if the window would land at least partly on a currently-visible screen
            // (guards against a monitor being disconnected since last run).
            double vsL = SystemParameters.VirtualScreenLeft;
            double vsT = SystemParameters.VirtualScreenTop;
            double vsR = vsL + SystemParameters.VirtualScreenWidth;
            double vsB = vsT + SystemParameters.VirtualScreenHeight;
            bool onScreen = (l + w > vsL + 40) && (l < vsR - 40) && (t + h > vsT + 40) && (t < vsB - 40);
            if (!onScreen)
                return;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l; Top = t; Width = w; Height = h;
            if (s.WindowMaximized)
                WindowState = WindowState.Maximized;
        }

        // Persist the current position/size (using the normal bounds even if maximized/minimized).
        private void SaveWindowPlacement()
        {
            var s = Properties.Settings.Default;

            if (WindowState == WindowState.Normal)
            {
                s.WindowLeft = Left; s.WindowTop = Top; s.WindowWidth = Width; s.WindowHeight = Height;
            }
            else if (!RestoreBounds.IsEmpty)
            {
                var rb = RestoreBounds;
                s.WindowLeft = rb.Left; s.WindowTop = rb.Top; s.WindowWidth = rb.Width; s.WindowHeight = rb.Height;
            }

            s.WindowMaximized = WindowState == WindowState.Maximized;
            s.WindowPlacementSaved = true;
            s.Save();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            initializing = true;

            // A brand new StringCollection setting starts out null.
            if (Properties.Settings.Default.Presets == null)
                Properties.Settings.Default.Presets = new System.Collections.Specialized.StringCollection();

            AutoInjectCheck.IsChecked = Properties.Settings.Default.AutoInjectEnabled;
            MinimizeTrayCheck.IsChecked = Properties.Settings.Default.MinimizeToTray;
            SetTheme(Properties.Settings.Default.Theme, save: false);

            RefreshPresetsList();
            InitTray();

            initializing = false;

            autoInjectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            autoInjectTimer.Tick += AutoInjectTimer_Tick;
            autoInjectTimer.Start();

            // Populate the process list right away, but let the window render first so it
            // doesn't appear frozen while GetProcessInfo runs over every process.
            Dispatcher.BeginInvoke(new Action(() => Refresh(null, null)), DispatcherPriority.Background);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Win10 20H1+/Win11); 19 on older builds.
        private void ApplyTitleBarTheme(bool dark)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            int useDark = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));
        }

        private void SetTheme(string theme, bool save)
        {
            bool dark = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase);
            string normalized = dark ? "Dark" : "Light";

            App.ApplyTheme(normalized);
            ApplyTitleBarTheme(dark);
            ThemeButton.Content = "Theme: " + normalized;

            if (save)
            {
                Properties.Settings.Default.Theme = normalized;
                Properties.Settings.Default.Save();
            }
        }

        private void ThemeButton_Click(object sender, RoutedEventArgs e)
        {
            bool currentlyDark = string.Equals(Properties.Settings.Default.Theme, "Dark", StringComparison.OrdinalIgnoreCase);
            SetTheme(currentlyDark ? "Light" : "Dark", save: true);
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized && Properties.Settings.Default.MinimizeToTray)
            {
                Hide();
                ShowInTaskbar = false;
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveWindowPlacement();
            autoInjectTimer?.Stop();

            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
        }

        #endregion

        #region Tray

        private void InitTray()
        {
            trayIcon = new WinForms.NotifyIcon
            {
                Text = "PhantomPlay",
                Visible = true
            };

            try
            {
                var streamInfo = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/app.ico"));
                if (streamInfo != null)
                    trayIcon.Icon = new System.Drawing.Icon(streamInfo.Stream);
                else
                    trayIcon.Icon = System.Drawing.SystemIcons.Application;
            }
            catch
            {
                trayIcon.Icon = System.Drawing.SystemIcons.Application;
            }

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Show", null, (s, e) => ShowFromTray());
            menu.Items.Add("Exit", null, (s, e) => { trayIcon.Visible = false; Close(); });
            trayIcon.ContextMenuStrip = menu;
            trayIcon.DoubleClick += (s, e) => ShowFromTray();
        }

        private void ShowFromTray()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = WindowState.Normal;
            Activate();
        }

        private void Notify(string message)
        {
            if (trayIcon == null)
                return;

            trayIcon.BalloonTipTitle = "PhantomPlay";
            trayIcon.BalloonTipText = message;
            trayIcon.ShowBalloonTip(3000);
        }

        #endregion

        #region Process list + search

        private void Refresh(object sender, RoutedEventArgs e)
        {
            var list = new List<ProcessInfo>();

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var proc = Injector.GetProcessInfo(process);

                    if (proc.Modules.Count == 0 || proc.WindowHandle == IntPtr.Zero)
                        continue;

                    proc.FileName = Path.GetFileName(proc.Modules.First().Value.Path);
                    list.Add(proc);
                }
                catch
                {
                    // Ignore processes we can't enumerate (access denied, exited, etc.).
                }
            }

            allProcesses = list
                .OrderBy(x => x.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToList();

            ApplyFilter();
        }

        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // TextChanged can fire before the first Refresh; guard against it.
            if (allProcesses != null)
                ApplyFilter();
        }

        private void ApplyFilter()
        {
            Processes.Items.Clear();

            var query = SearchBox?.Text?.Trim() ?? string.Empty;

            foreach (var proc in allProcesses)
            {
                if (injected.ContainsKey(proc.Id))
                    continue;

                if (query.Length > 0 &&
                    proc.ToString().IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                Processes.Items.Add(proc);
            }
        }

        #endregion

        #region Inject / Unload

        private void Inject(object sender, RoutedEventArgs e)
        {
            if (!(Processes.SelectedItem is ProcessInfo selected))
                return;

            if (InjectInternal(selected))
                ApplyFilter();
        }

        private bool InjectInternal(ProcessInfo target)
        {
            PeFile dll = target.Is64Bit ? Dll64 : Dll32;

            if (dll == null)
            {
                MessageBox.Show(
                    "The DLL payload could not be loaded. Make sure PhantomPlay.dll and PhantomPlay64.dll are next to the exe.",
                    "PhantomPlay", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            Injector.Inject(target, dll);

            injected[target.Id] = target;
            autoInjectAttempted.Add(target.Id);

            if (!InjectedProcesses.Items.Contains(target))
                InjectedProcesses.Items.Add(target);

            return true;
        }

        private void Unload(object sender, RoutedEventArgs e)
        {
            if (!(InjectedProcesses.SelectedItem is ProcessInfo selected))
                return;

            PeFile dll = selected.Is64Bit ? Dll64 : Dll32;

            if (dll != null)
                Injector.Unload(selected, dll);

            injected.Remove(selected.Id);
            autoInjectAttempted.Remove(selected.Id);
            InjectedProcesses.Items.Remove(selected);

            ApplyFilter();
        }

        #endregion

        #region Presets

        private void RefreshPresetsList()
        {
            PresetsList.Items.Clear();

            var presets = Properties.Settings.Default.Presets;
            if (presets == null)
                return;

            foreach (var name in presets)
                PresetsList.Items.Add(name);
        }

        private void AddPreset(object sender, RoutedEventArgs e)
        {
            if (!(Processes.SelectedItem is ProcessInfo selected) || string.IsNullOrEmpty(selected.FileName))
                return;

            var presets = Properties.Settings.Default.Presets
                          ?? (Properties.Settings.Default.Presets = new System.Collections.Specialized.StringCollection());

            bool exists = presets.Cast<string>()
                .Any(p => string.Equals(p, selected.FileName, StringComparison.OrdinalIgnoreCase));

            if (!exists)
            {
                presets.Add(selected.FileName);
                Properties.Settings.Default.Save();
                RefreshPresetsList();
            }
        }

        private void RemovePreset(object sender, RoutedEventArgs e)
        {
            if (!(PresetsList.SelectedItem is string selected))
                return;

            var presets = Properties.Settings.Default.Presets;
            if (presets == null)
                return;

            presets.Remove(selected);
            Properties.Settings.Default.Save();
            RefreshPresetsList();
        }

        #endregion

        #region Auto-inject

        private void AutoInjectCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (initializing)
                return;

            Properties.Settings.Default.AutoInjectEnabled = AutoInjectCheck.IsChecked == true;
            Properties.Settings.Default.Save();
        }

        private void MinimizeTrayCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (initializing)
                return;

            Properties.Settings.Default.MinimizeToTray = MinimizeTrayCheck.IsChecked == true;
            Properties.Settings.Default.Save();
        }

        private void AutoInjectTimer_Tick(object sender, EventArgs e)
        {
            var live = new HashSet<uint>();

            bool autoEnabled = Properties.Settings.Default.AutoInjectEnabled;

            // Preset executable names without extension, for cheap matching against ProcessName.
            var presetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (autoEnabled && Properties.Settings.Default.Presets != null)
            {
                foreach (string p in Properties.Settings.Default.Presets)
                    presetNames.Add(Path.GetFileNameWithoutExtension(p));
            }

            bool injectedSomething = false;

            foreach (var process in Process.GetProcesses())
            {
                uint id;
                try { id = (uint)process.Id; }
                catch { process.Dispose(); continue; }

                live.Add(id);

                try
                {
                    if (!autoEnabled || presetNames.Count == 0)
                        continue;

                    if (injected.ContainsKey(id) || autoInjectAttempted.Contains(id))
                        continue;

                    if (process.MainWindowHandle == IntPtr.Zero)
                        continue; // Window not ready yet; retry next tick.

                    if (!presetNames.Contains(process.ProcessName))
                        continue;

                    var info = Injector.GetProcessInfo(process);
                    if (info.WindowHandle == IntPtr.Zero || info.Modules.Count == 0)
                        continue;

                    info.FileName = Path.GetFileName(info.Modules.First().Value.Path);

                    if (InjectInternal(info))
                    {
                        injectedSomething = true;
                        Notify($"Injected into {info}");
                    }
                }
                catch
                {
                    // Don't hammer a process that keeps throwing.
                    autoInjectAttempted.Add(id);
                }
                finally
                {
                    process.Dispose();
                }
            }

            // Forget ids that are no longer running so relaunches get picked up again.
            autoInjectAttempted.RemoveWhere(x => !live.Contains(x));

            if (injectedSomething)
                ApplyFilter();
        }

        #endregion
    }
}
