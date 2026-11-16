using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using XilousProbe.Input;
using XilousProbe.Widget;
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
    private GlobalHotkey? _hotkey;
    private WidgetWindow? _widget;
    private DesktopPointer? _desktop;
    private bool _pointMode;

    public SettingsStore Settings { get; private set; } = null!;
    public bool PointModeOn => _pointMode;
    public bool HotkeyRegistered => _hotkey?.Registered ?? false;
    public string HotkeyDisplay => GlobalHotkey.Describe(HotkeyModifiers, HotkeyKey);
    public event Action? PointModeChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Theme.XilousTheme.Start(Dispatcher);
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

        _desktop = new DesktopPointer(Dispatcher);
        _desktop.ExitRequested += () => SetPointMode(false);

        _hotkey = new GlobalHotkey(HotkeyModifiers, HotkeyKey);
        _hotkey.Pressed += TogglePointMode;

        CreateTray();
        if (!Settings.Data.WidgetHidden) ShowWidget();
        if (e.Args.Contains("--after-update"))
            Dispatcher.BeginInvoke(() => Notify("Updated", $"Updated to v{Updater.CurrentVersion}."), DispatcherPriority.ApplicationIdle);
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
        if (_stagedUpdate is not null) { await ApplyStagedUpdateAsync(); return; }
        _updateBusy = true;
        if (manual) _widget?.ShowMessage("Checking for updates…", 30);
        try
        {
            var update = await _updater.CheckAsync();
            if (update is null)
            {
                if (manual) Notify("Up to date", $"Xilous Probe v{Updater.CurrentVersion} is the latest release.");
                return;
            }
            Log($"Update available: {update.Tag}");
            // Every stage shows in the widget, whether the update was asked for or found in the
            // background, so a restart never comes out of nowhere.
            var label = $"v{update.Version}";
            _widget?.ShowMessage($"Downloading {label}…", 60);
            int lastPercent = -1;
            var progress = new Progress<double>(p =>
            {
                int percent = (int)(p * 100);
                if (percent == lastPercent) return;
                lastPercent = percent;
                _widget?.ShowMessage($"Downloading {label}… {percent}%", 60);
            });
            _stagedUpdate = await _updater.DownloadAndStageAsync(update, progress,
                verifying: () => Dispatcher.BeginInvoke(() => _widget?.ShowMessage("Verifying the download…", 60)));
            _stagedVersion = update.Version.ToString();
            Log($"Update {update.Tag} staged");
            if (_pointMode) _widget?.ShowMessage($"{label} is ready. It installs when you stop pointing.", 8);
            await ApplyStagedUpdateAsync();
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

    private bool _restarting;

    /// <summary>
    /// Restarts into the staged update, unless the user is in the middle of pointing. The widget
    /// says so for a couple of seconds first, so the restart is never a surprise.
    /// </summary>
    private async Task ApplyStagedUpdateAsync()
    {
        if (_stagedUpdate is null || _pointMode || _restarting) return;
        _restarting = true;
        Notify("Updating", $"Restarting into v{_stagedVersion}…");
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (_pointMode) { _restarting = false; return; } // started pointing meanwhile: wait for it to end
        Updater.Relaunch(_stagedUpdate);
        Quit();
    }

    /// <summary>
    /// Tells the user something: in the widget's status line, opened so it can be read, and as a
    /// Windows notification from the tray icon (which Focus assist or notification settings may hide).
    /// </summary>
    private void Notify(string title, string message)
    {
        if (_widget is { IsVisible: true }) _widget.ShowMessage(message);
        try { _tray?.ShowNotification(title, message); }
        catch { /* notifications off */ }
        Log($"{title}: {message}");
    }

    /// <summary>The menu behind the tray icon and a right-click on the widget: built fresh each time, so it is always current.</summary>
    public ContextMenu BuildMenu()
    {
        var point = new MenuItem { Header = $"{(_pointMode ? "Stop" : "Start")} pointing", InputGestureText = HotkeyDisplay, FontWeight = FontWeights.SemiBold };
        point.Click += (_, _) => TogglePointMode();
        var widget = new MenuItem { Header = _widget is { IsVisible: true } ? "Hide widget" : "Show widget" };
        widget.Click += (_, _) => { if (_widget is { IsVisible: true }) HideWidget(); else ShowWidget(); };
        var reset = new MenuItem { Header = "Reset position" };
        reset.Click += (_, _) => { ShowWidget(); _widget?.ResetPosition(); };
        var version = new MenuItem { Header = $"Xilous Probe v{Updater.CurrentVersion}", IsEnabled = false };
        var updates = new MenuItem { Header = "Check for updates" };
        updates.Click += async (_, _) => await CheckForUpdatesAsync(manual: true);
        var admin = new MenuItem { Header = "Restart as administrator", ToolTip = "Needed only to point at apps that run elevated" };
        admin.Click += (_, _) => RestartElevated();
        admin.Visibility = IsElevated ? Visibility.Collapsed : Visibility.Visible;
        var quit = new MenuItem { Header = "Quit" };
        quit.Click += (_, _) => Quit();

        var menu = new ContextMenu();
        foreach (var item in new object[] { point, new Separator(), widget, reset, new Separator(), version, updates, admin, quit })
            menu.Items.Add(item);
        return menu;
    }

    private void CreateTray()
    {
        _tray = new TaskbarIcon
        {
            ToolTipText = "Xilous Probe",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")),
            ContextMenu = BuildMenu(),
            NoLeftClickDelay = true,
        };
        _tray.TrayLeftMouseUp += (_, _) => TogglePointMode();
        _tray.ForceCreate();
    }

    private void RefreshTrayMenu()
    {
        if (_tray is not null) _tray.ContextMenu = BuildMenu();
    }

    public void ShowWidget()
    {
        _widget ??= new WidgetWindow(this);
        _widget.SetPointing(_pointMode);
        _widget.Show();
        if (Settings.Data.WidgetHidden) { Settings.Data.WidgetHidden = false; Settings.Save(); }
        RefreshTrayMenu();
    }

    public void HideWidget()
    {
        _widget?.Hide();
        Settings.Data.WidgetHidden = true;
        Settings.Save();
        RefreshTrayMenu();
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
        RefreshTrayMenu();
        _widget?.SetPointing(_pointMode);
        if (_tray is not null) _tray.ToolTipText = _pointMode ? "Xilous Probe: pointing" : "Xilous Probe";
        PointModeChanged?.Invoke();
        if (!_pointMode && _stagedUpdate is not null) _ = ApplyStagedUpdateAsync();
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
