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
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private int _hereBusy;
    private bool _isSwitching;
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => _ = RefreshOutputsAsync();
        _hereTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _hereTimer.Tick += (_, _) => _ = RefreshHereFlagsAsync();
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
        _ = RefreshOutputsAsync();
        _refreshTimer.Start();
        _hereTimer.Start();
        Log("開始監控。青框是這個視窗所在的螢幕；「識別」會在真實螢幕顯示左／中／右編號。");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
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
            _ = RefreshOutputsAsync();
        }

        return IntPtr.Zero;
    }

    private async Task RefreshOutputsAsync()
    {
        if (_isSwitching || _closed || !await _refreshLock.WaitAsync(0).ConfigureAwait(true))
        {
            return;
        }

        var hwnd = AppHwnd;
        try
        {
            var snapshot = await Task.Run(() => MonitorHub.Capture(hwnd)).ConfigureAwait(true);
            if (_closed)
            {
                return;
            }

            ApplySnapshot(snapshot);
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                Log("讀取顯示狀態失敗：" + ex.Message);
            }
        }
        finally
        {
            if (!_closed)
            {
                _refreshLock.Release();
            }
        }
    }

    private void ApplySnapshot(MonitorSnapshot snapshot)
    {
        Merge(
            DesktopScreens,
            snapshot.Outputs.Where(o => o.HasDesktopBounds).ToArray());
        Merge(
            OtherOutputs,
            snapshot.Outputs.Where(o => !o.HasDesktopBounds).ToArray());

        SummaryText = snapshot.HdmiPortCount == 0
            ? "這台電腦目前沒有偵測到 HDMI 輸出孔"
            : $"本機 HDMI：{snapshot.HdmiPortCount} 孔，{snapshot.HdmiWithSinkCount} 孔有螢幕";
        RefreshText = $"更新於 {snapshot.CapturedAt:HH:mm:ss}";
        UpdateCurrentScreenText();
        Raise(nameof(SummaryText), nameof(RefreshText), nameof(CanSwitchAll));
    }

    private static void Merge(ObservableCollection<OutputItem> target, IReadOnlyList<OutputItem> next)
    {
        var nextByKey = next.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!nextByKey.ContainsKey(target[i].Key))
            {
                target.RemoveAt(i);
            }
        }

        var existing = target.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var item in next)
        {
            if (existing.TryGetValue(item.Key, out var current))
            {
                current.ApplyFrom(item);
            }
            else
            {
                target.Add(item);
            }
        }
    }

    private async Task RefreshHereFlagsAsync()
    {
        if (_isSwitching || _closed || DesktopScreens.Count == 0 ||
            Interlocked.CompareExchange(ref _hereBusy, 1, 0) != 0)
        {
            return;
        }

        var hwnd = AppHwnd;
        try
        {
            var (appGdi, mouseGdi) = await Task.Run(() =>
                (ScreenLayout.GdiFromWindow(hwnd), ScreenLayout.GdiFromCursor())).ConfigureAwait(true);
            if (_closed)
            {
                return;
            }

            ScreenLayout.UpdateHereFlags(DesktopScreens, appGdi, mouseGdi);
            UpdateCurrentScreenText();
        }
        catch (Exception)
        {
            // 游標／視窗所在螢幕偵測失敗時維持上一幀，避免打斷點擊
        }
        finally
        {
            Interlocked.Exchange(ref _hereBusy, 0);
        }
    }

    private void UpdateCurrentScreenText()
    {
        var here = DesktopScreens.FirstOrDefault(s => s.IsAppHere);
        var text = here is null
            ? "這個視窗在：無法對應"
            : $"這個視窗在：{here.PlaceTitle}";
        if (CurrentScreenText == text)
        {
            return;
        }

        CurrentScreenText = text;
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
            var overlay = new IdentifyWindow(screen.DisplayNumber, screen.PlaceLabel)
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
            var result = await Task.Run(() => MonitorHub.SwitchToHdmi(gdiDeviceName)).ConfigureAwait(true);
            Log(result.Message);
        }
        catch (Exception ex)
        {
            Log("切換失敗：" + ex.Message);
        }
        finally
        {
            _isSwitching = false;
            Raise(nameof(CanSwitchAll));
            await RefreshOutputsAsync().ConfigureAwait(true);
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
