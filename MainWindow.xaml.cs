using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using HdmiSwitch.Native;
using HdmiSwitch.Services;

namespace HdmiSwitch;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DispatcherTimer _timer;
    private bool _isSwitching;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => RefreshOutputs();
        Loaded += OnLoaded;
        Closed += (_, _) => _timer.Stop();
    }

    public ObservableCollection<OutputItem> Outputs { get; } = [];

    public ObservableCollection<string> Logs { get; } = [];

    public string SummaryText { get; private set; } = "讀取顯示輸出中…";

    public string RefreshText { get; private set; } = string.Empty;

    public bool CanSwitchAll => !_isSwitching && Outputs.Any(o => o.CanSwitchToHdmi);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshOutputs();
        _timer.Start();
        Log("開始監控顯示輸出。HDMI 孔有無接上螢幕會每 2 秒更新。");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmDisplayChange)
        {
            Dispatcher.BeginInvoke(RefreshOutputs);
        }

        return IntPtr.Zero;
    }

    private void RefreshOutputs()
    {
        if (_isSwitching)
        {
            return;
        }

        try
        {
            var snapshot = MonitorHub.Capture();
            Outputs.Clear();
            foreach (var item in snapshot.Outputs)
            {
                Outputs.Add(item);
            }

            SummaryText = snapshot.HdmiPortCount == 0
                ? "這台電腦目前沒有偵測到 HDMI 輸出孔"
                : $"本機 HDMI：{snapshot.HdmiPortCount} 孔，{snapshot.HdmiWithSinkCount} 孔有螢幕";
            RefreshText = $"更新於 {snapshot.CapturedAt:HH:mm:ss}";
            Raise(nameof(SummaryText), nameof(RefreshText), nameof(CanSwitchAll));
        }
        catch (Exception ex)
        {
            Log("讀取顯示狀態失敗：" + ex.Message);
        }
    }

    private async void SwitchAll_OnClick(object sender, RoutedEventArgs e) =>
        await SwitchAsync(null);

    private async void SwitchOne_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OutputItem item })
        {
            await SwitchAsync(string.IsNullOrWhiteSpace(item.SourceGdiName) ? null : item.SourceGdiName);
        }
    }

    private void EnableWindowsHdmi_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            MonitorHub.EnableWindowsHdmi();
            Log("已要求 Windows 延伸桌面到外接輸出（DisplaySwitch /extend）。");
        }
        catch (Exception ex)
        {
            Log("無法啟動 DisplaySwitch：" + ex.Message);
        }
    }

    private async Task SwitchAsync(string? gdiDeviceName)
    {
        _isSwitching = true;
        Raise(nameof(CanSwitchAll));
        Log(gdiDeviceName is null ? "開始把所有螢幕切到 HDMI…" : $"開始把 {gdiDeviceName} 切到 HDMI…");
        try
        {
            var result = await Task.Run(() => MonitorHub.SwitchToHdmi(gdiDeviceName));
            Log(result.Message);
        }
        catch (Exception ex)
        {
            Log("切換失敗：" + ex.Message);
        }
        finally
        {
            _isSwitching = false;
            RefreshOutputs();
        }
    }

    private void Log(string message)
    {
        Logs.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (Logs.Count > 40)
        {
            Logs.RemoveAt(Logs.Count - 1);
        }
    }

    private void Raise(params string[] names)
    {
        foreach (var name in names)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
