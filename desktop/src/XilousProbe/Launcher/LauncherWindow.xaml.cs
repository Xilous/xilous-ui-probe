using System.Windows;
using XilousProbe.Settings;

namespace XilousProbe.Launcher;

public partial class LauncherWindow : Window
{
    private readonly App _app;

    public LauncherWindow(App app)
    {
        _app = app;
        InitializeComponent();
        HotkeyHint.Text = app.HotkeyRegistered
            ? $"{app.HotkeyDisplay} toggles Point Mode anywhere. Esc steps back."
            : $"{app.HotkeyDisplay} is taken by another app. Use the tray icon or the button.";
        UrlBox.Text = app.Settings.Data.LastUrl;
        Footer.Text = $"v{typeof(App).Assembly.GetName().Version?.ToString(3)}";
        RefreshApps();
        RefreshPointButton();
        app.PointModeChanged += RefreshPointButton;
        Closed += (_, _) => app.PointModeChanged -= RefreshPointButton;
    }

    public void RefreshApps()
    {
        AppsList.ItemsSource = null;
        AppsList.ItemsSource = _app.Settings.Data.Apps;
    }

    private void RefreshPointButton() =>
        Dispatcher.Invoke(() => PointButton.Content = _app.PointModeOn ? "Stop pointing" : "Start pointing");

    public void SetBrowserStatus(string text) => Dispatcher.Invoke(() => BrowserStatus.Text = text);
    public void SetAppsStatus(string text) => Dispatcher.Invoke(() => AppsStatus.Text = text);

    private void OnPointClick(object sender, RoutedEventArgs e) => _app.TogglePointMode();

    private async void OnOpenUrl(object sender, RoutedEventArgs e)
    {
        var url = NormalizeUrl(UrlBox.Text);
        if (url is null) { SetBrowserStatus("Type a URL, such as localhost:5173 or example.com."); return; }
        UrlBox.Text = url;
        _app.Settings.Data.LastUrl = url;
        _app.Settings.Save();
        await _app.OpenUrlAsync(url);
    }

    /// <summary>Accepts what people type: bare hosts, localhost:port, or full URLs.</summary>
    public static string? NormalizeUrl(string? raw)
    {
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Contains("://")) return s;
        bool local = s.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                     s.StartsWith("127.", StringComparison.Ordinal) || s.StartsWith("[::1]", StringComparison.Ordinal);
        return (local ? "http://" : "https://") + s;
    }

    private async void OnLaunchApp(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AppEntry entry) await _app.LaunchAppAsync(entry);
    }

    private void OnEditApp(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AppEntry entry) return;
        var dlg = new AppEntryWindow(entry) { Owner = this };
        var result = dlg.ShowDialog();
        if (dlg.Deleted) _app.Settings.Data.Apps.Remove(entry);
        if (result == true || dlg.Deleted) { _app.Settings.Save(); RefreshApps(); }
    }

    private void OnAddApp(object sender, RoutedEventArgs e)
    {
        var entry = new AppEntry { Kind = AppKind.Executable, Port = 9334 };
        var dlg = new AppEntryWindow(entry) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _app.Settings.Data.Apps.Add(entry);
        _app.Settings.Save();
        RefreshApps();
    }

    private void OnSettings(object sender, RoutedEventArgs e) => _app.OpenSettings();
}
