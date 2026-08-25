using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using HdmiSwitch.Native;
using HdmiSwitch.Services;

namespace HdmiSwitch;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _hereTimer;
    private readonly List<IdentifyWindow> _identifyWindows = [];
    private bool _isSwitching;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshOutputs();
        _hereTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _hereTimer.Tick += (_, _) => RefreshHereFlags();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public ObservableCollection<OutputItem> DesktopScreens { get; } = [];

    public ObservableCollection<OutputItem> OtherOutputs { get; } = [];

    public ObservableCollection<string> Logs { get; } = [];

    public string SummaryText { get; private set; } = "讀取顯示輸出中…";

    public string RefreshText { get; private set; } = string.Empty;

    public string CurrentScreenText { get; private set; } = "這個視窗在：偵測中…";

    public bool CanSwitchAll =>
        !_isSwitching && (DesktopScreens.Any(o => o.CanSwitchToHdmi) || OtherOutputs.Any(o => o.CanSwitchToHdmi));

    public event PropertyChangedEventHandler? PropertyChanged;

    private IntPtr AppHwnd => new WindowInteropHelper(this).Handle;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshOutputs();
        _refreshTimer.Start();
        _hereTimer.Start();
        Log("開始監控。青框是這個視窗所在的螢幕；「識別」會在真實螢幕顯示左／中／右編號。");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _hereTimer.Stop();
        CloseIdentifyWindows();
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

    private void MainWindow_OnLocationChanged(object? sender, EventArgs e) => RefreshHereFlags();

    private void RefreshOutputs()
    {
        if (_isSwitching)
        {
            return;
        }

        try
        {
            var snapshot = MonitorHub.Capture(AppHwnd);
            DesktopScreens.Clear();
            OtherOutputs.Clear();
            foreach (var item in snapshot.Outputs)
            {
                if (item.HasDesktopBounds)
                {
                    DesktopScreens.Add(item);
                }
                else
                {
                    OtherOutputs.Add(item);
                }
            }

            SummaryText = snapshot.HdmiPortCount == 0
                ? "這台電腦目前沒有偵測到 HDMI 輸出孔"
                : $"本機 HDMI：{snapshot.HdmiPortCount} 孔，{snapshot.HdmiWithSinkCount} 孔有螢幕";
            RefreshText = $"更新於 {snapshot.CapturedAt:HH:mm:ss}";
            UpdateCurrentScreenText();
            Raise(nameof(SummaryText), nameof(RefreshText), nameof(CanSwitchAll));
        }
        catch (Exception ex)
        {
            Log("讀取顯示狀態失敗：" + ex.Message);
        }
    }

    private void RefreshHereFlags()
    {
        if (_isSwitching || DesktopScreens.Count == 0)
        {
            return;
        }

        ScreenLayout.UpdateHereFlags(DesktopScreens, ScreenLayout.GdiFromWindow(AppHwnd), ScreenLayout.GdiFromCursor());
        UpdateCurrentScreenText();
    }

    private void UpdateCurrentScreenText()
    {
        var here = DesktopScreens.FirstOrDefault(s => s.IsAppHere);
        CurrentScreenText = here is null
            ? "這個視窗在：無法對應"
            : $"這個視窗在：{here.PlaceTitle}";
        Raise(nameof(CurrentScreenText));
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

    private void IdentifyOne_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OutputItem item })
        {
            ShowIdentify([item]);
        }
    }

    private void IdentifyAll_OnClick(object sender, RoutedEventArgs e) =>
        ShowIdentify(DesktopScreens.ToArray());

    private void ShowIdentify(IReadOnlyList<OutputItem> screens)
    {
        CloseIdentifyWindows();
        foreach (var screen in screens.Where(s => s.HasDesktopBounds))
        {
            var overlay = new IdentifyWindow(screen.DisplayNumber, screen.PlaceLabel, screen.Title)
            {
                Owner = this
            };
            overlay.Closed += (_, _) => _identifyWindows.Remove(overlay);
            _identifyWindows.Add(overlay);
            overlay.ShowOn(screen.PixelLeft, screen.PixelTop, screen.PixelWidth, screen.PixelHeight);
        }

        var names = string.Join("、", screens.Select(s => s.PlaceTitle));
        Log("已在螢幕上顯示編號：" + names);
    }

    private void CloseIdentifyWindows()
    {
        foreach (var window in _identifyWindows.ToArray())
        {
            try
            {
                window.Close();
            }
            catch (Exception)
            {
                // overlay 可能已自己關掉
            }
        }

        _identifyWindows.Clear();
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
