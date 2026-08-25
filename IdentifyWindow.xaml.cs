using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using HdmiSwitch.Native;

namespace HdmiSwitch;

public partial class IdentifyWindow : Window
{
    public IdentifyWindow(int number, string place, string title)
    {
        InitializeComponent();
        NumberText.Text = number.ToString();
        PlaceText.Text = place;
        TitleText.Text = title;
    }

    public void ShowOn(int pixelLeft, int pixelTop, int pixelWidth, int pixelHeight)
    {
        ShowActivated = false;
        Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(
            hwnd,
            new IntPtr(-1),
            pixelLeft,
            pixelTop,
            pixelWidth,
            pixelHeight,
            NativeMethods.SwpShowWindow | NativeMethods.SwpNoActivate);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Close();
        };
        timer.Start();
    }
}
