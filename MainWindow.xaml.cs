using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    private readonly AppSettings _settings;
    private readonly string? _settingsWarning;
    private HwndSource? _hwndSource;
    private HotkeyManager? _hotkeys;
    private PowerScheduler? _scheduler;
    private int _hereBusy;
    private bool _isSwitching;
    private bool _closed;
    private bool _ready;
    private OutputItem? _dragCandidate;
    private Point _dragStartPoint;

    private const string ScreenDragFormat = "HdmiSwitch.ScreenCard";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _settings = AppSettingsStore.Load(out _settingsWarning);
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

    public ObservableCollection<MonitorOption> PowerTargets { get; } = [];

    public string SummaryText { get; private set; } = "讀取顯示輸出中…";

    public string RefreshText { get; private set; } = string.Empty;

    public string CurrentScreenText { get; private set; } = "偵測中…";

    public bool CanBatchSwitch =>
        !_isSwitching && DesktopScreens.Any(o => o.CanSwitch);

    public bool HasCountdown => _scheduler?.HasCountdown == true;

    public string CountdownText => _scheduler?.CountdownText ?? string.Empty;

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
                _scheduler = new PowerScheduler(() => _settings.DailySchedules, OnScheduledPower);
                _scheduler.StateChanged += (_, _) => Raise(nameof(HasCountdown), nameof(CountdownText));
                _scheduler.Start();
                Log("開始監控。點輸入名稱可切到 HDMI／DP／VGA／DVI；琥珀框是這個視窗所在的螢幕。");
                if (_settingsWarning is not null)
                {
                    Log(_settingsWarning);
                }

                ApplyHotkeys(_settings.Hotkeys);
            }
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _closed = true;
        _refreshTimer.Stop();
        _hereTimer.Stop();
        _scheduler?.Dispose();
        _scheduler = null;
        _hotkeys?.Dispose();
        _hotkeys = null;
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
        _scheduler?.Dispose();
        _scheduler = null;
        _hotkeys?.Dispose();
        _hotkeys = null;
        CloseIdentifyWindows();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _hwndSource?.AddHook(WndProc);
        _hotkeys = new HotkeyManager(AppHwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmDisplayChange)
        {
            _ = RefreshOutputsAsync();
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WmHotkey &&
            _hotkeys is not null &&
            _hotkeys.TryResolve(wParam.ToInt32(), out var family))
        {
            handled = true;
            _ = SwitchAllFamilyAsync(family, InputSelect.FamilyName(family), "快捷鍵：");
        }

        return IntPtr.Zero;
    }

    /// <summary>把 bindings 送去註冊，成功／失敗都寫 Log，並把結果交回給 SettingsWindow 顯示。</summary>
    private IReadOnlyList<HotkeyRegistration> ApplyHotkeys(IReadOnlyList<HotkeyBinding> bindings)
    {
        if (_hotkeys is null)
        {
            return bindings
                .Select(b => new HotkeyRegistration(b, false, "視窗尚未建立，暫時無法註冊快捷鍵。"))
                .ToArray();
        }

        var results = _hotkeys.Apply(bindings);
        foreach (var result in results)
        {
            var combo = HotkeyText.Describe(result.Binding.Modifiers, result.Binding.Key);
            var target = InputSelect.FamilyName(result.Binding.Family);
            Log(result.Success
                ? $"快捷鍵 {combo} → 全部切到 {target}。"
                : $"快捷鍵 {combo}（{target}）註冊失敗：{result.Error}");
        }

        return results;
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
        ApplyInputLabelOverrides(snapshot.Outputs);

        Merge(
            DesktopScreens,
            snapshot.Outputs.Where(o => o.HasDesktopBounds).ToArray(),
            _dragCandidate is null ? _settings.ScreenOrder : null);
        Merge(
            OtherOutputs,
            snapshot.Outputs.Where(o => !o.HasDesktopBounds).ToArray());

        SummaryText = snapshot.HdmiPortCount == 0
            ? "這台電腦目前沒有偵測到 HDMI 輸出孔"
            : $"本機 HDMI：{snapshot.HdmiPortCount} 孔，{snapshot.HdmiWithSinkCount} 孔有螢幕";
        RefreshText = $"更新於 {snapshot.CapturedAt:HH:mm:ss}";
        UpdateCurrentScreenText();
        RebuildBatchOptions();
        UpdateResolutions();
        RebuildPowerTargets();
        Raise(nameof(SummaryText), nameof(RefreshText), nameof(CanBatchSwitch));
    }

    /// <summary>
    /// 覆寫在合併快照「之前」套用：MonitorHub.Capture 維持純查詢、無狀態，
    /// 顯示名稱的個人化留在 UI 這層。
    /// </summary>
    private void ApplyInputLabelOverrides(IReadOnlyList<OutputItem> outputs)
    {
        if (_settings.InputLabelOverrides.Count == 0)
        {
            return;
        }

        foreach (var item in outputs)
        {
            if (string.IsNullOrWhiteSpace(item.Title))
            {
                continue;
            }

            var overrides = _settings.InputLabelOverrides
                .Where(o => !string.IsNullOrWhiteSpace(o.Label) &&
                            string.Equals(o.MonitorKey, item.Title, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (overrides.Length == 0)
            {
                continue;
            }

            item.Inputs = item.Inputs
                .Select(chip => chip.Code is byte code &&
                                overrides.FirstOrDefault(o => o.InputCode == code) is { } hit
                    ? chip with { Label = hit.Label }
                    : chip)
                .ToArray();
        }
    }

    private void UpdateResolutions()
    {
        foreach (var item in DesktopScreens)
        {
            if (string.IsNullOrWhiteSpace(item.SourceGdiName))
            {
                item.SetResolutions([], null);
                continue;
            }

            var modes = ResolutionService.ListResolutions(item.SourceGdiName);
            var current = ResolutionService.Current(item.SourceGdiName);
            var selected = current is null
                ? null
                : modes.FirstOrDefault(m => m.Width == current.Width && m.Height == current.Height);
            item.SetResolutions(modes, selected);
        }
    }

    private void RebuildPowerTargets()
    {
        var next = new List<MonitorOption> { new(null, "全部螢幕") };
        next.AddRange(DesktopScreens
            .Where(s => !string.IsNullOrWhiteSpace(s.SourceGdiName))
            .Select(s => new MonitorOption(s.SourceGdiName, s.PlaceTitle)));

        if (PowerTargets.SequenceEqual(next))
        {
            return;
        }

        var keep = CountdownTarget?.SelectedItem as MonitorOption;
        PowerTargets.Clear();
        foreach (var option in next)
        {
            PowerTargets.Add(option);
        }

        if (CountdownTarget is not null)
        {
            CountdownTarget.SelectedItem = keep is not null && next.Contains(keep) ? keep : next[0];
        }
    }

    private static void Merge(
        ObservableCollection<OutputItem> target,
        IReadOnlyList<OutputItem> next,
        IReadOnlyList<string>? order = null)
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

        if (order is { Count: > 0 })
        {
            ApplyOrder(target, order);
        }
    }

    /// <summary>依已存的自訂順序（螢幕 Key 清單）穩定重排；清單裡沒有的（新接上的螢幕）保留原本相對順序、排在最後。</summary>
    private static void ApplyOrder(ObservableCollection<OutputItem> target, IReadOnlyList<string> order)
    {
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < order.Count; i++)
        {
            rank.TryAdd(order[i], i);
        }

        var sorted = target
            .Select((item, index) => (item, index))
            .OrderBy(t => rank.TryGetValue(t.item.Key, out var r) ? r : int.MaxValue)
            .ThenBy(t => t.index)
            .Select(t => t.item)
            .ToList();

        for (var i = 0; i < sorted.Count; i++)
        {
            var from = target.IndexOf(sorted[i]);
            if (from != i)
            {
                target.Move(from, i);
            }
        }
    }

    private void ScreenCard_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DependencyObject card || IsInteractiveElement(e.OriginalSource as DependencyObject, card))
        {
            _dragCandidate = null;
            return;
        }

        _dragCandidate = FindDataContext<OutputItem>(card);
        _dragStartPoint = e.GetPosition(null);
    }

    private void ScreenCard_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var dragged = _dragCandidate;
        try
        {
            // 拖曳期間讓 _dragCandidate 保持非 null，讓背景刷新（DoDragDrop 內部會跑訊息迴圈）暫停套用自訂順序，
            // 避免每秒的重排跟使用者正在拖的手勢互相打架。
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(ScreenDragFormat, dragged), DragDropEffects.Move);
        }
        finally
        {
            _dragCandidate = null;
        }
    }

    /// <summary>從 e.OriginalSource 往上走到卡片邊界為止：途中碰到按鈕／下拉選單等可互動控制項就不當成拖曳手勢，讓點擊照常生效。</summary>
    private static bool IsInteractiveElement(DependencyObject? source, DependencyObject boundary)
    {
        while (source is not null && !ReferenceEquals(source, boundary))
        {
            if (source is ButtonBase or ComboBox or TextBoxBase or ScrollBar or Thumb)
            {
                return true;
            }

            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }

        return false;
    }

    /// <summary>拖曳懸停就即時搬動卡片順序（所見即所得的預覽），不用等放開才變動；Drop 只負責把最終順序存檔。</summary>
    private void ScreenCard_OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(ScreenDragFormat) ||
            e.Data.GetData(ScreenDragFormat) is not OutputItem dragged ||
            sender is not FrameworkElement { DataContext: OutputItem target })
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        if (ReferenceEquals(dragged, target))
        {
            return;
        }

        var from = DesktopScreens.IndexOf(dragged);
        var to = DesktopScreens.IndexOf(target);
        if (from >= 0 && to >= 0 && from != to)
        {
            DesktopScreens.Move(from, to);
        }
    }

    private void ScreenCard_OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(ScreenDragFormat))
        {
            return;
        }

        e.Handled = true;
        _settings.ScreenOrder = DesktopScreens.Select(s => s.Key).ToList();
        if (!AppSettingsStore.TrySave(_settings, out var error))
        {
            Log("螢幕排序記不住（這次仍照排的順序顯示，重開會消失）：" + error);
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

        await SwitchAllFamilyAsync(option.Family, option.Label);
    }

    /// <summary>按鈕點擊與快捷鍵共用的批次切換：只切偵測到有訊號的螢幕，其餘略過並 Log。</summary>
    private async Task SwitchAllFamilyAsync(InputFamily family, string label, string prefix = "")
    {
        if (_isSwitching || _closed)
        {
            return;
        }

        var ready = DesktopScreens.Where(s => s.CanSwitch && s.HasLikelySignal(family)).ToArray();
        foreach (var skipped in DesktopScreens.Where(s => s.CanSwitch && !s.HasLikelySignal(family)))
        {
            Log($"{skipped.PlaceTitle} 略過：這台電腦沒有接到 {label}。");
        }

        if (ready.Length == 0)
        {
            Log($"{prefix}沒有螢幕適合切到 {label}（本機看起來沒接這類線）。");
            return;
        }

        _isSwitching = true;
        Raise(nameof(CanBatchSwitch));
        Log($"{prefix}開始把 {ready.Length} 台螢幕切到 {label}…");
        try
        {
            var request = InputRequest.OfFamily(family);
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

    private async void PowerOffOne_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OutputItem item })
        {
            return;
        }

        if (!item.CanSwitch)
        {
            Log($"{item.PlaceTitle} 沒有 DDC/CI，無法單獨關閉；可以改用「全部關閉」。");
            return;
        }

        await PowerOffOneAsync(item.SourceGdiName, item.PlaceTitle, string.Empty);
    }

    private void PowerOffAll_OnClick(object sender, RoutedEventArgs e) => PowerOffAll(string.Empty);

    private void StartCountdown_OnClick(object sender, RoutedEventArgs e)
    {
        if (_scheduler is null)
        {
            return;
        }

        var target = CountdownTarget.SelectedItem as MonitorOption ?? PowerTargets.FirstOrDefault();
        if (target is null)
        {
            Log("目前沒有可關閉的目標。");
            return;
        }

        if (!int.TryParse((CountdownMinutes.Text ?? string.Empty).Trim(), out var minutes) ||
            minutes < 1 || minutes > 1440)
        {
            Log("倒數分鐘數請輸入 1～1440 的整數。");
            return;
        }

        _scheduler.StartCountdown(target.GdiName, target.Display, minutes);
        Log($"已開始倒數：{minutes} 分鐘後關閉 {target.Display}。（app 關掉倒數就取消）");
        Raise(nameof(HasCountdown), nameof(CountdownText));
    }

    private void CancelCountdown_OnClick(object sender, RoutedEventArgs e)
    {
        if (_scheduler is null || !_scheduler.HasCountdown)
        {
            return;
        }

        _scheduler.CancelCountdown();
        Log("已取消倒數關閉。");
        Raise(nameof(HasCountdown), nameof(CountdownText));
    }

    private void OpenSettings_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(
            _settings,
            DesktopScreens.ToArray(),
            BatchInputOptions.Select(o => o.Family).ToArray(),
            ApplyHotkeys)
        {
            Owner = this
        };

        if (window.ShowDialog() != true)
        {
            Log("設定未儲存。");
            return;
        }

        if (AppSettingsStore.TrySave(_settings, out var error))
        {
            Log($"設定已儲存到 {AppSettingsStore.FilePath}。");
        }
        else
        {
            Log("設定存檔失敗（快捷鍵這次仍生效，但重開會消失）：" + error);
        }

        _ = RefreshOutputsAsync();
    }

    private async void Resolution_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: DisplayMode mode, DataContext: OutputItem item })
        {
            return;
        }

        if (_closed || _isSwitching || string.IsNullOrWhiteSpace(item.SourceGdiName))
        {
            return;
        }

        // 重新整理時會把 SelectedResolution 設回目前值，這裡靠比對現況擋掉自我觸發。
        var current = ResolutionService.Current(item.SourceGdiName);
        if (current is not null && current.Width == mode.Width && current.Height == mode.Height)
        {
            return;
        }

        var gdiName = item.SourceGdiName;
        try
        {
            var result = await Task.Run(() => ResolutionService.Apply(gdiName, mode)).ConfigureAwait(true);
            Log($"{item.PlaceTitle}：{result.Message}");
        }
        catch (Exception ex)
        {
            Log($"{item.PlaceTitle} 切換解析度失敗：{ex.Message}");
        }
        finally
        {
            await RefreshOutputsAsync().ConfigureAwait(true);
        }
    }

    private void OnScheduledPower(string? gdiDeviceName, string reason)
    {
        if (gdiDeviceName is null)
        {
            PowerOffAll(reason + "：");
            return;
        }

        var title = DesktopScreens
            .FirstOrDefault(s => string.Equals(s.SourceGdiName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
            ?.PlaceTitle ?? gdiDeviceName;
        _ = PowerOffOneAsync(gdiDeviceName, title, reason + "：");
    }

    private async Task PowerOffOneAsync(string gdiDeviceName, string title, string prefix)
    {
        try
        {
            var result = await Task.Run(() => MonitorHub.PowerOff(gdiDeviceName)).ConfigureAwait(true);
            Log(prefix + result.Message);
        }
        catch (Exception ex)
        {
            Log($"{prefix}{title} 關閉失敗：{ex.Message}");
        }
    }

    private void PowerOffAll(string prefix)
    {
        try
        {
            MonitorHub.PowerOffAllWindows();
            Log($"{prefix}已送出全部螢幕關閉指令（含這個視窗所在的螢幕）。移動滑鼠或按鍵盤即可喚醒，不是當機。");
        }
        catch (Exception ex)
        {
            Log($"{prefix}全部關閉失敗：{ex.Message}");
        }
    }

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
