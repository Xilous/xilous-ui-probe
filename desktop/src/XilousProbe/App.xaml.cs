using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using XilousProbe.Cdp;
using XilousProbe.Input;
using XilousProbe.Launcher;
using XilousProbe.Settings;
using XilousProbe.Uia;
using XilousProbe.Update;
using System.Windows.Threading;

namespace XilousProbe;

public partial class App : Application
{
    private const ModifierKeys HotkeyModifiers = ModifierKeys.Control | ModifierKeys.Shift;
    private const Key HotkeyKey = Key.X;

    private Mutex? _singleInstance;
    private TaskbarIcon? _tray;
    private MenuItem? _pointMenuItem;
    private GlobalHotkey? _hotkey;
    private LauncherWindow? _launcher;
    private SettingsWindow? _settingsWindow;
    private DesktopPointer? _desktop;
    private WebBridge? _bridge;
    private bool _pointMode;

    public SettingsStore Settings { get; private set; } = null!;
    public bool PointModeOn => _pointMode;
    public bool HotkeyRegistered => _hotkey?.Registered ?? false;
    public string HotkeyDisplay => GlobalHotkey.Describe(HotkeyModifiers, HotkeyKey);
    public event Action? PointModeChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // After a self-update the previous version may still be closing; it holds the mutex.
        Updater.FinishPreviousUpdate(e.Args);
        _singleInstance = new Mutex(true, @"Local\XilousProbe.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("Xilous Probe is already running. Look for its icon in the system tray.", "Xilous Probe");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log($"Unhandled: {args.Exception}");
            MessageBox.Show(args.Exception.Message, "Xilous Probe hit a problem");
            args.Handled = true;
        };

        Settings = new SettingsStore();
        _bridge = new WebBridge(Settings);
        _bridge.PointModeRequested += on => Dispatcher.BeginInvoke(() => SetPointMode(on));
        _bridge.SettingsRequested += () => Dispatcher.BeginInvoke(OpenSettings);
        _bridge.Log += Log;

        _desktop = new DesktopPointer(Dispatcher, pid => _bridge.OwnsProcess(pid));
        _desktop.ExitRequested += () => SetPointMode(false);

        _hotkey = new GlobalHotkey(HotkeyModifiers, HotkeyKey);
        _hotkey.Pressed += TogglePointMode;

        CreateTray();
        ShowLauncher();
        _ = ReattachProbeChromeAsync();
        StartUpdateChecks();
    }

    // ===== Self-update =====

    private readonly Updater _updater = new();
    private DispatcherTimer? _updateTimer;
    private string? _stagedUpdate;      // path of an updated exe waiting for Point Mode to end
    private string? _stagedVersion;
    private bool _updateBusy;

    private void StartUpdateChecks()
    {
        if (!Updater.IsReleaseBuild) return;
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(manual: false);
        _updateTimer.Start();
        // A little after start, so launching is never slowed by the network.
        _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => Dispatcher.InvokeAsync(() => CheckForUpdatesAsync(manual: false)));
    }

    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (!Updater.IsReleaseBuild)
        {
            if (manual) Notify("Updates", "This is a development build; only release builds update themselves.");
            return;
        }
        if (_updateBusy) return;
        if (_stagedUpdate is not null) { ApplyStagedUpdate(); return; }
        _updateBusy = true;
        try
        {
            var update = await _updater.CheckAsync();
            if (update is null)
            {
                if (manual) Notify("Up to date", $"Xilous Probe v{Updater.CurrentVersion} is the latest release.");
                return;
            }
            Log($"Update available: {update.Tag}");
            _stagedUpdate = await _updater.DownloadAndStageAsync(update);
            _stagedVersion = update.Version.ToString();
            Log($"Update {update.Tag} staged");
            ApplyStagedUpdate();
        }
        catch (Exception ex)
        {
            Log($"Update failed: {ex}");
            if (manual) Notify("Update failed", ex.Message);
        }
        finally
        {
            _updateBusy = false;
        }
    }

    /// <summary>Restarts into the staged update, unless the user is in the middle of pointing.</summary>
    private void ApplyStagedUpdate()
    {
        if (_stagedUpdate is null || _pointMode) return;
        Notify("Updated", $"Restarting into Xilous Probe v{_stagedVersion}.");
        Updater.Relaunch(_stagedUpdate);
        Quit();
    }

    private void Notify(string title, string message)
    {
        try { _tray?.ShowNotification(title, message); }
        catch { /* notifications off */ }
        Log($"{title}: {message}");
    }

    private void CreateTray()
    {
        _pointMenuItem = new MenuItem { Header = $"Start pointing\t{HotkeyDisplay}" };
        _pointMenuItem.Click += (_, _) => TogglePointMode();
        var open = new MenuItem { Header = "Open Xilous Probe" };
        open.Click += (_, _) => ShowLauncher();
        var settings = new MenuItem { Header = "Settings" };
        settings.Click += (_, _) => OpenSettings();
        var admin = new MenuItem { Header = "Restart as administrator", ToolTip = "Needed only to point at apps that run elevated" };
        admin.Click += (_, _) => RestartElevated();
        admin.Visibility = IsElevated ? Visibility.Collapsed : Visibility.Visible;
        var quit = new MenuItem { Header = "Quit" };
        quit.Click += (_, _) => Quit();
        var version = new MenuItem { Header = $"Xilous Probe v{Updater.CurrentVersion}", IsEnabled = false };
        var updates = new MenuItem { Header = "Check for updates" };
        updates.Click += async (_, _) => await CheckForUpdatesAsync(manual: true);

        var menu = new ContextMenu();
        foreach (var item in new object[] { _pointMenuItem, open, settings, new Separator(), version, updates, admin, quit }) menu.Items.Add(item);

        _tray = new TaskbarIcon
        {
            ToolTipText = "Xilous Probe",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")),
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        _tray.TrayLeftMouseUp += (_, _) => TogglePointMode();
        _tray.ForceCreate();
    }

    public void ShowLauncher()
    {
        if (_launcher is null)
        {
            _launcher = new LauncherWindow(this);
            _launcher.Closed += (_, _) => _launcher = null;
        }
        _launcher.Show();
        if (_launcher.WindowState == WindowState.Minimized) _launcher.WindowState = WindowState.Normal;
        _launcher.Activate();
    }

    public void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(Settings);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void TogglePointMode() => SetPointMode(!_pointMode);

    public void SetPointMode(bool on)
    {
        if (on == _pointMode) return;
        _pointMode = on;
        try
        {
            if (on) _desktop!.Start(); else _desktop!.Stop();
        }
        catch (Exception ex)
        {
            _pointMode = false;
            MessageBox.Show(ex.Message, "Point Mode could not start");
        }
        _ = _bridge!.SetPointModeAsync(_pointMode);
        if (_pointMenuItem is not null) _pointMenuItem.Header = $"{(_pointMode ? "Stop" : "Start")} pointing\t{HotkeyDisplay}";
        if (_tray is not null) _tray.ToolTipText = _pointMode ? "Xilous Probe: pointing" : "Xilous Probe";
        PointModeChanged?.Invoke();
        if (!_pointMode && _stagedUpdate is not null) Dispatcher.BeginInvoke(ApplyStagedUpdate);
    }

    /// <summary>Opens a URL in the probe Chrome: a new tab if it is running, a fresh launch if not.</summary>
    public async Task OpenUrlAsync(string url)
    {
        try
        {
            if (await ChromeLauncher.FindRunningPortAsync() is int running)
            {
                var browser = await _bridge!.AttachAsync("Probe Chrome", running, TimeSpan.FromSeconds(5));
                await browser.OpenTabAsync(url);
                _launcher?.SetBrowserStatus($"Opened in the probe Chrome. {HotkeyDisplay} to point.");
                return;
            }

            bool firstRun = !Directory.Exists(ChromeLauncher.ProfileDir);
            _launcher?.SetBrowserStatus("Starting the probe Chrome…");
            int port = await ChromeLauncher.LaunchAsync(url);
            await _bridge!.AttachAsync("Probe Chrome", port, TimeSpan.FromSeconds(15));
            _launcher?.SetBrowserStatus(firstRun || !Settings.Data.ChromeProfileIntroShown
                ? "This is the probe Chrome's own profile. Sign in to Chrome there once (profile icon, top right) and sync brings your bookmarks, extensions and passwords. Logins stay after that."
                : $"Probe Chrome is ready. {HotkeyDisplay} to point.");
            if (!Settings.Data.ChromeProfileIntroShown)
            {
                Settings.Data.ChromeProfileIntroShown = true;
                Settings.Save();
            }
        }
        catch (Exception ex)
        {
            Log($"Open URL failed: {ex}");
            _launcher?.SetBrowserStatus($"Could not open it: {ex.Message}");
        }
    }

    /// <summary>A probe Chrome left running from the last session is picked back up.</summary>
    private async Task ReattachProbeChromeAsync()
    {
        try
        {
            if (await ChromeLauncher.FindRunningPortAsync() is int port)
                await _bridge!.AttachAsync("Probe Chrome", port, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Log($"Reattach failed: {ex.Message}");
        }
    }

    public async Task LaunchAppAsync(AppEntry entry)
    {
        try
        {
            if (_bridge!.BrowserOnPort(entry.Port) is not null)
            {
                _launcher?.SetAppsStatus($"{entry.Name} is already attached. {HotkeyDisplay} to point.");
                return;
            }
            // Something already listening means the app is running: attach instead of starting a second copy.
            if (entry.Kind != AppKind.Attach && await CdpBrowser.ProbeAsync(entry.Port) is null)
            {
                _launcher?.SetAppsStatus($"Starting {entry.Name}…");
                AppLauncher.Start(entry);
            }
            else
            {
                _launcher?.SetAppsStatus($"Attaching to {entry.Name} on port {entry.Port}…");
            }
            // A dev server builds before Electron starts, so give it time.
            var wait = entry.Kind == AppKind.DevCommand ? TimeSpan.FromSeconds(120) : TimeSpan.FromSeconds(30);
            var browser = await _bridge.AttachAsync(entry.Name, entry.Port, wait);
            _launcher?.SetAppsStatus($"{entry.Name} is attached ({browser.Pages.Count} window{(browser.Pages.Count == 1 ? "" : "s")}). {HotkeyDisplay} to point.");
        }
        catch (Exception ex)
        {
            Log($"Launch {entry.Name} failed: {ex}");
            _launcher?.SetAppsStatus($"{entry.Name}: {ex.Message} It can still be pointed at through UI Automation, without the web pointer.");
        }
    }

    private static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    private void RestartElevated()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        try
        {
            _singleInstance?.ReleaseMutex();
            _singleInstance?.Dispose();
            _singleInstance = null;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Quit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The UAC prompt was declined: keep running as we were.
            _singleInstance = new Mutex(true, @"Local\XilousProbe.SingleInstance", out _);
        }
    }

    private void Quit()
    {
        SetPointMode(false);
        _desktop?.Dispose();
        _hotkey?.Dispose();
        _tray?.Dispose();
        if (_bridge is not null) _bridge.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        Shutdown();
    }

    private static readonly object LogGate = new();
    public static string LogPath { get; } = Path.Combine(SettingsStore.LocalDataDir, "xilous-probe.log");

    public static void Log(string message)
    {
        lock (LogGate)
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.LocalDataDir);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 2 * 1024 * 1024) info.Delete();
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch (IOException) { }
        }
        Debug.WriteLine(message);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
