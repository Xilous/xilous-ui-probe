using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XilousProbe.Settings;

namespace XilousProbe.Launcher;

public partial class AppEntryWindow : Window
{
    private readonly AppEntry _entry;
    public bool Deleted { get; private set; }

    public AppEntryWindow(AppEntry entry)
    {
        _entry = entry;
        InitializeComponent();
        NameBox.Text = entry.Name;
        CommandBox.Text = entry.Command;
        ArgsBox.Text = entry.Arguments;
        DirBox.Text = entry.WorkingDirectory;
        PortBox.Text = entry.Port > 0 ? entry.Port.ToString() : "";
        KindBox.SelectedIndex = (int)entry.Kind;
        DeleteButton.Visibility = string.IsNullOrEmpty(entry.Name) ? Visibility.Collapsed : Visibility.Visible;
    }

    private AppKind SelectedKind => (AppKind)Math.Max(0, KindBox.SelectedIndex);

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CommandPanel is null) return;
        var kind = SelectedKind;
        CommandPanel.Visibility = kind == AppKind.Attach ? Visibility.Collapsed : Visibility.Visible;
        CommandLabel.Text = kind == AppKind.Executable ? "Executable" : "Command";
        BrowseButton.Visibility = kind == AppKind.Executable ? Visibility.Visible : Visibility.Collapsed;
        Hint.Text = kind switch
        {
            AppKind.DevCommand => "electron-vite: run dev -- --remoteDebuggingPort {port}. electron-forge: start -- -- --remote-debugging-port={port}.",
            AppKind.Executable => "--remote-debugging-port={port} is added for you. An app whose fuses block it is still pointed at through UI Automation.",
            _ => "Start the app yourself with --remote-debugging-port, or with app.commandLine.appendSwitch('remote-debugging-port', …) in its main process.",
        };
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return;
        CommandBox.Text = dlg.FileName;
        if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
        if (string.IsNullOrWhiteSpace(DirBox.Text)) DirBox.Text = System.IO.Path.GetDirectoryName(dlg.FileName) ?? "";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var kind = SelectedKind;
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { Error.Text = "Give it a name."; return; }
        if (!int.TryParse(PortBox.Text.Trim(), out int port) || port is < 1024 or > 65535)
        { Error.Text = "The port must be a number from 1024 to 65535."; return; }
        if (kind != AppKind.Attach && string.IsNullOrWhiteSpace(CommandBox.Text))
        { Error.Text = kind == AppKind.Executable ? "Choose the .exe." : "Type the command."; return; }
        if (kind == AppKind.DevCommand && !ArgsBox.Text.Contains("{port}") && !CommandBox.Text.Contains("{port}"))
        { Error.Text = "Put {port} where the command takes its remote debugging port, or the app can't be attached."; return; }

        _entry.Name = NameBox.Text.Trim();
        _entry.Kind = kind;
        _entry.Command = CommandBox.Text.Trim();
        _entry.Arguments = ArgsBox.Text.Trim();
        _entry.WorkingDirectory = DirBox.Text.Trim();
        _entry.Port = port;
        DialogResult = true;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        Deleted = true;
        DialogResult = false;
    }
}
