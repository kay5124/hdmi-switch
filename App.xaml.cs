using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using HdmiSwitch.Native;
using HdmiSwitch.Services;

namespace HdmiSwitch;

public partial class App : Application
{
    private const string MutexName = @"Local\HdmiSwitch.SingleInstance";
    private const string ActivateEventName = @"Local\HdmiSwitch.Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWait;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        if (e.Args.Any(a => string.Equals(a, "--probe", StringComparison.OrdinalIgnoreCase)))
        {
            base.OnStartup(e);
            DumpProbe();
            Shutdown();
            return;
        }

        if (!TryOwnSingleInstance())
        {
            SignalRunningInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ListenForActivation();
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopActivationListener();
        ReleaseSingleInstance();
        base.OnExit(e);
    }

    public void BringMainWindowToFront()
    {
        if (MainWindow is not { } window)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SwRestore);
            NativeMethods.SetForegroundWindow(hwnd);
        }
    }

    private bool TryOwnSingleInstance()
    {
        _mutex = new Mutex(false, MutexName);
        try
        {
            _ownsMutex = _mutex.WaitOne(0, false);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        return _ownsMutex;
    }

    private void ListenForActivation()
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateWait = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => Dispatcher.BeginInvoke(BringMainWindowToFront),
            null,
            Timeout.Infinite,
            false);
    }

    private static void SignalRunningInstance()
    {
        try
        {
            using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
            activate.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // 上一份行程還沒建好激活事件，略過
        }
    }

    private void StopActivationListener()
    {
        _activateWait?.Unregister(null);
        _activateWait = null;
        _activateEvent?.Dispose();
        _activateEvent = null;
    }

    private void ReleaseSingleInstance()
    {
        if (_mutex is null)
        {
            return;
        }

        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 已釋放
            }

            _ownsMutex = false;
        }

        _mutex.Dispose();
        _mutex = null;
    }

    private static void DumpProbe()
    {
        var snapshot = MonitorHub.Capture();
        var sb = new StringBuilder();
        sb.AppendLine($"hdmiPorts={snapshot.HdmiPortCount} hdmiWithSink={snapshot.HdmiWithSinkCount} outputs={snapshot.Outputs.Count}");
        foreach (var item in snapshot.Outputs)
        {
            sb.AppendLine($"{item.PlaceTitle} | {item.Title} | {item.Connector} | bounds={item.PixelLeft},{item.PixelTop} {item.PixelWidth}x{item.PixelHeight} | 輸入={item.CurrentInputText}");
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "probe-result.txt"), sb.ToString());
    }
}
