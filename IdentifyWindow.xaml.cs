using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using HdmiSwitch.Native;

namespace HdmiSwitch;

public partial class IdentifyWindow : Window
{
    private readonly DispatcherTimer _autoCloseTimer;

    public IdentifyWindow(int number, string place)
    {
        InitializeComponent();
        NumberText.Text = number.ToString();
        PlaceText.Text = place;
        _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _autoCloseTimer.Tick += OnAutoClose;
        Closing += (_, _) =>
        {
            _autoCloseTimer.Stop();
            _autoCloseTimer.Tick -= OnAutoClose;
        };
    }

    public void ShowOn(int pixelLeft, int pixelTop, int pixelWidth, int pixelHeight)
    {
        ShowActivated = false;
        Show();
        UpdateLayout();

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        MakeClickThrough(hwnd);

        var origin = new NativePoint
        {
            X = pixelLeft + 8,
            Y = pixelTop + 8
        };
        var monitor = NativeMethods.MonitorFromPoint(origin, NativeMethods.MonitorDefaultToNearest);
        var scale = DpiScale(monitor);
        var width = Math.Max((int)Math.Round(ActualWidth * scale), 1);
        var height = Math.Max((int)Math.Round(ActualHeight * scale), 1);
        var margin = (int)Math.Round(16 * scale);
        var x = pixelLeft + margin;
        var y = pixelTop + margin;
        NativeMethods.SetWindowPos(
            hwnd,
            new IntPtr(-1),
            x,
            y,
            width,
            height,
            NativeMethods.SwpShowWindow | NativeMethods.SwpNoActivate);

        _autoCloseTimer.Start();
    }

    private void OnAutoClose(object? sender, EventArgs e)
    {
        _autoCloseTimer.Stop();
        Close();
    }

    private static void MakeClickThrough(IntPtr hwnd)
    {
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        style |= NativeMethods.WsExTransparent | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, new IntPtr(style));
    }

    private static double DpiScale(IntPtr monitor)
    {
        if (monitor != IntPtr.Zero &&
            NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MonitorDpiEffective, out var dpiX, out _) == 0 &&
            dpiX > 0)
        {
            return dpiX / 96.0;
        }

        return 1.0;
    }
}
