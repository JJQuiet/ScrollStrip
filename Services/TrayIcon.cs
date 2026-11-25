using System.Drawing;
using System.Windows.Forms;

namespace ScrollStrip;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly AppSettings _settings;
    private SettingsWindow? _settingsWindow;
    private readonly Icon _appIcon;

    public TrayIcon(AppSettings settings)
    {
        _settings = settings;

        var menu = new ContextMenuStrip();
        var settingsItem = new ToolStripMenuItem("Settings");
        settingsItem.Click += (_, _) => OpenSettings();
        var quitItem = new ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => System.Windows.Application.Current.Shutdown();
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quitItem);

        _appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (Icon)SystemIcons.Application.Clone();

        _notifyIcon = new NotifyIcon
        {
            Text = "ScrollStrip",
            Icon = _appIcon,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => OpenSettings();
    }

    public void OpenSettings()
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(_settings);
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _appIcon.Dispose();
    }
}
