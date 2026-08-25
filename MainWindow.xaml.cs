using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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
    private HwndSource? _hwndSource;
    private int _hereBusy;
    private bool _isSwitching;
    private bool _closed;
    private bool _ready;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => _ = RefreshOutputsAsync();
        _hereTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _hereTimer.Tick += (_, _) => _ = RefreshHereFlagsAsync();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    public ObservableCollection<OutputItem> DesktopScreens { get; } = [];

    public ObservableCollection<OutputItem> OtherOutputs { get; } = [];

    public ObservableCollection<string> Logs { get; } = [];

    public ObservableCollection<InputOption> BatchInputOptions { get; } = [];

    public string SummaryText { get; private set; } = "讀取顯示輸出中…";

    public string RefreshText { get; private set; } = string.Empty;

    public string CurrentScreenText { get; private set; } = "偵測中…";

    public bool CanBatchSwitch =>
        !_isSwitching && DesktopScreens.Any(o => o.CanSwitch);

    public bool IsReady
    {
        get => _ready;
        private set
        {
            if (_ready == value)
            {
                return;
            }

            _ready = value;
            Raise(nameof(IsReady));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private IntPtr AppHwnd => new WindowInteropHelper(this).Handle;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshOutputsAsync().ConfigureAwait(true);
        }
        finally
        {
            if (!_closed)
            {
                IsReady = true;
                _refreshTimer.Start();
                _hereTimer.Start();
                Log("開始監控。點輸入名稱可切到 HDMI／DP／VGA／DVI；琥珀框是這個視窗所在的螢幕。");
            }
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _closed = true;
        _refreshTimer.Stop();
        _hereTimer.Stop();
        CloseIdentifyWindows();
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
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
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _hwndSource?.AddHook(WndProc);
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
        if (_isSwitching || _closed)
        {
            return;
        }

        if (!await _refreshLock.WaitAsync(0).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            if (_closed)
            {
                return;
            }

            var hwnd = AppHwnd;
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
            try
            {
                _refreshLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // 視窗已關
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
        RebuildBatchOptions();
        Raise(nameof(SummaryText), nameof(RefreshText), nameof(CanBatchSwitch));
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
        var text = here is null ? "—" : here.PlaceTitle;
        if (CurrentScreenText == text)
        {
            return;
        }

        CurrentScreenText = text;
        Raise(nameof(CurrentScreenText));
    }

    private void RebuildBatchOptions()
    {
        var families = DesktopScreens
            .SelectMany(s => s.Inputs)
            .Select(chip => chip.Code is byte code ? InputSelect.FamilyOf(code) : null)
            .Where(family => family is not null)
            .Select(family => family!.Value)
            .ToHashSet();
        var next = InputSelect.BatchOrder
            .Where(families.Contains)
            .Select(family => new InputOption(InputSelect.FamilyName(family), family))
            .ToArray();
        if (BatchInputOptions.Count == next.Length &&
            BatchInputOptions.Zip(next, (current, item) => current == item).All(same => same))
        {
            return;
        }

        BatchInputOptions.Clear();
        foreach (var option in next)
        {
            BatchInputOptions.Add(option);
        }
    }

    private async void SwitchInputChip_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not InputChip { Code: byte code } chip)
        {
            return;
        }

        var item = FindDataContext<OutputItem>(element);
        if (item is null || !item.CanSwitch)
        {
            Log("這台螢幕目前無法用 DDC/CI 切換輸入。");
            return;
        }

        if (chip.IsCurrent)
        {
            Log($"{item.PlaceTitle} 已經是 {chip.Label}。");
            return;
        }

        if (chip.Signal == SignalKind.Off &&
            MessageBox.Show(
                this,
                $"{item.PlaceTitle} 的 {chip.Label}，這台電腦沒有接到這個輸入。\n切過去畫面可能會暗掉，而且要用螢幕本身的按鈕才能切回來。\n\n仍要切換？",
                "可能沒有訊號",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        await SwitchAsync(item.SourceGdiName, InputRequest.Exact(code));
    }

    private async void SwitchAllFamily_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: InputOption option })
        {
            return;
        }

        var ready = DesktopScreens.Where(s => s.CanSwitch && s.HasLikelySignal(option.Family)).ToArray();
        foreach (var skipped in DesktopScreens.Where(s => s.CanSwitch && !s.HasLikelySignal(option.Family)))
        {
            Log($"{skipped.PlaceTitle} 略過：這台電腦沒有接到 {option.Label}。");
        }

        if (ready.Length == 0)
        {
            Log($"沒有螢幕適合切到 {option.Label}（本機看起來沒接這類線）。");
            return;
        }

        _isSwitching = true;
        Raise(nameof(CanBatchSwitch));
        Log($"開始把 {ready.Length} 台螢幕切到 {option.Label}…");
        try
        {
            var request = InputRequest.OfFamily(option.Family);
            var names = ready.Select(s => s.SourceGdiName).ToArray();
            var result = await Task.Run(() =>
            {
                var notes = new List<string>();
                var any = false;
                foreach (var name in names)
                {
                    var one = MonitorHub.SwitchInput(name, request);
                    notes.Add(one.Message);
                    any |= one.Success;
                }

                return new SwitchResult(any, string.Join(Environment.NewLine, notes));
            }).ConfigureAwait(true);
            Log(result.Message);
        }
        catch (Exception ex)
        {
            Log("切換失敗：" + ex.Message);
        }
        finally
        {
            _isSwitching = false;
            Raise(nameof(CanBatchSwitch));
            await RefreshOutputsAsync().ConfigureAwait(true);
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

    private async Task SwitchAsync(string? gdiDeviceName, InputRequest request)
    {
        _isSwitching = true;
        Raise(nameof(CanBatchSwitch));
        Log(gdiDeviceName is null
            ? $"開始把所有螢幕切到 {request.DisplayName}…"
            : $"開始把螢幕切到 {request.DisplayName}…");
        try
        {
            var result = await Task.Run(() => MonitorHub.SwitchInput(gdiDeviceName, request)).ConfigureAwait(true);
            Log(result.Message);
        }
        catch (Exception ex)
        {
            Log("切換失敗：" + ex.Message);
        }
        finally
        {
            _isSwitching = false;
            Raise(nameof(CanBatchSwitch));
            await RefreshOutputsAsync().ConfigureAwait(true);
        }
    }

    private static T? FindDataContext<T>(DependencyObject? start) where T : class
    {
        while (start is not null)
        {
            if (start is FrameworkElement { DataContext: T match })
            {
                return match;
            }

            start = VisualTreeHelper.GetParent(start);
        }

        return null;
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
