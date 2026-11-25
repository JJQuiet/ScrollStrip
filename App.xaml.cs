using System.Security.Principal;
using System.Windows;

namespace ScrollStrip;

public partial class App : System.Windows.Application
{
    private TrayIcon? _trayIcon;
    private TouchpadListener? _listener;
    private EdgeScrollController? _controller;
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _settingsRequest;
    private RegisteredWaitHandle? _settingsRequestWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string userId = WindowsIdentity.GetCurrent().User!.Value;
        _instanceMutex = new Mutex(true, $@"Local\ScrollStrip.Instance.{userId}", out _ownsMutex);
        _settingsRequest = new EventWaitHandle(false, EventResetMode.AutoReset,
            $@"Local\ScrollStrip.Settings.{userId}");
        if (!_ownsMutex)
        {
            _settingsRequest.Set();
            Shutdown();
            return;
        }

        var settings = AppSettings.Load();
        _controller = new EdgeScrollController(settings);
        _listener = new TouchpadListener();
        _listener.ContactsReceived += (contacts, count) =>
        {
            // ContactsReceived fires on the message-loop thread.
            _controller.OnContacts(contacts, count);
        };
        _listener.Start();
        _trayIcon = new TrayIcon(settings);

        _settingsRequestWait = ThreadPool.RegisterWaitForSingleObject(_settingsRequest, (_, _) =>
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(new Action(() => _trayIcon?.OpenSettings()));
        }, null, Timeout.Infinite, executeOnlyOnce: false);

        if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
            _trayIcon.OpenSettings();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _settingsRequestWait?.Unregister(null);
        _settingsRequest?.Dispose();
        _trayIcon?.Dispose();
        _listener?.Dispose();
        _controller?.Dispose();
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
