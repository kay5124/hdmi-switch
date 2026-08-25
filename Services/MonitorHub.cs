using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HdmiSwitch.Services;

public enum SignalKind
{
    On,
    Off,
    Unknown
}

public sealed record InputChip(
    string Label,
    SignalKind Signal,
    bool IsCurrent);

public sealed class OutputItem : INotifyPropertyChanged
{
    private bool _isAppHere;
    private bool _isMouseHere;

    public required string Key { get; init; }
    public required string Title { get; set; }
    public required string Connector { get; set; }
    public required string SourceGdiName { get; set; }
    public required bool IsActive { get; set; }
    public required bool TargetAvailable { get; set; }
    public required SignalKind Signal { get; set; }
    public required string SignalText { get; set; }
    public required string PathText { get; set; }
    public required string CurrentInputText { get; set; }
    public required string DdcText { get; set; }
    public required bool CanSwitchToHdmi { get; set; }
    public required bool CanEnableWindowsHdmi { get; set; }
    public required IReadOnlyList<InputChip> Inputs { get; set; }
    public bool HasDesktopBounds { get; init; }
    public bool IsPrimary { get; set; }
    public int PixelLeft { get; set; }
    public int PixelTop { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public int DisplayNumber { get; set; }
    public string PlaceLabel { get; set; } = string.Empty;
    public string PlaceTitle { get; set; } = string.Empty;
    public double LayoutX { get; set; }
    public double LayoutY { get; set; }
    public double LayoutW { get; set; }
    public double LayoutH { get; set; }

    public bool IsAppHere
    {
        get => _isAppHere;
        set => Set(ref _isAppHere, value);
    }

    public bool IsMouseHere
    {
        get => _isMouseHere;
        set => Set(ref _isMouseHere, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ApplyFrom(OutputItem source)
    {
        Assign(nameof(Title), Title, source.Title, v => Title = v);
        Assign(nameof(Connector), Connector, source.Connector, v => Connector = v);
        Assign(nameof(SourceGdiName), SourceGdiName, source.SourceGdiName, v => SourceGdiName = v);
        Assign(nameof(IsActive), IsActive, source.IsActive, v => IsActive = v);
        Assign(nameof(TargetAvailable), TargetAvailable, source.TargetAvailable, v => TargetAvailable = v);
        Assign(nameof(Signal), Signal, source.Signal, v => Signal = v);
        Assign(nameof(SignalText), SignalText, source.SignalText, v => SignalText = v);
        Assign(nameof(PathText), PathText, source.PathText, v => PathText = v);
        Assign(nameof(CurrentInputText), CurrentInputText, source.CurrentInputText, v => CurrentInputText = v);
        Assign(nameof(DdcText), DdcText, source.DdcText, v => DdcText = v);
        Assign(nameof(CanSwitchToHdmi), CanSwitchToHdmi, source.CanSwitchToHdmi, v => CanSwitchToHdmi = v);
        Assign(nameof(CanEnableWindowsHdmi), CanEnableWindowsHdmi, source.CanEnableWindowsHdmi, v => CanEnableWindowsHdmi = v);
        if (!Inputs.SequenceEqual(source.Inputs))
        {
            Inputs = source.Inputs;
            OnPropertyChanged(nameof(Inputs));
        }

        Assign(nameof(IsPrimary), IsPrimary, source.IsPrimary, v => IsPrimary = v);
        Assign(nameof(PixelLeft), PixelLeft, source.PixelLeft, v => PixelLeft = v);
        Assign(nameof(PixelTop), PixelTop, source.PixelTop, v => PixelTop = v);
        Assign(nameof(PixelWidth), PixelWidth, source.PixelWidth, v => PixelWidth = v);
        Assign(nameof(PixelHeight), PixelHeight, source.PixelHeight, v => PixelHeight = v);
        Assign(nameof(DisplayNumber), DisplayNumber, source.DisplayNumber, v => DisplayNumber = v);
        Assign(nameof(PlaceLabel), PlaceLabel, source.PlaceLabel, v => PlaceLabel = v);
        Assign(nameof(PlaceTitle), PlaceTitle, source.PlaceTitle, v => PlaceTitle = v);
        Assign(nameof(LayoutX), LayoutX, source.LayoutX, v => LayoutX = v);
        Assign(nameof(LayoutY), LayoutY, source.LayoutY, v => LayoutY = v);
        Assign(nameof(LayoutW), LayoutW, source.LayoutW, v => LayoutW = v);
        Assign(nameof(LayoutH), LayoutH, source.LayoutH, v => LayoutH = v);
        IsAppHere = source.IsAppHere;
        IsMouseHere = source.IsMouseHere;
    }

    private void Assign<T>(string name, T current, T next, Action<T> setter)
    {
        if (EqualityComparer<T>.Default.Equals(current, next))
        {
            return;
        }

        setter(next);
        OnPropertyChanged(name);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class MonitorSnapshot
{
    public required IReadOnlyList<OutputItem> Outputs { get; init; }
    public required int HdmiPortCount { get; init; }
    public required int HdmiWithSinkCount { get; init; }
    public required DateTime CapturedAt { get; init; }
}

internal static class MonitorHub
{
    public static MonitorSnapshot Capture(IntPtr appHwnd = default)
    {
        var gpuOutputs = CcdService.QueryOutputs();
        var ddcStates = DdcService.QueryActiveMonitors();
        var screens = ScreenLayout.Query();
        var screenByGdi = screens
            .GroupBy(s => s.GdiDeviceName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var ddcByGdi = ddcStates
            .GroupBy(d => d.GdiDeviceName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var usedDdc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<OutputItem>();

        foreach (var gpu in gpuOutputs
                     .Where(o => o.IsActive)
                     .GroupBy(o => string.IsNullOrWhiteSpace(o.SourceGdiName) ? o.Key : o.SourceGdiName, StringComparer.OrdinalIgnoreCase)
                     .Select(g => g.First()))
        {
            ddcByGdi.TryGetValue(gpu.SourceGdiName, out var ddc);
            screenByGdi.TryGetValue(gpu.SourceGdiName, out var screen);
            if (ddc is not null)
            {
                usedDdc.Add(ddc.GdiDeviceName);
            }

            items.Add(ToItem(gpu, ddc, screen));
        }

        foreach (var gpu in gpuOutputs.Where(o =>
                     !o.IsActive &&
                     o.OutputTechnology == Native.OutputTechnology.Hdmi &&
                     o.TargetAvailable))
        {
            items.Add(ToItem(gpu, null, null));
        }

        var unusedHdmiCount = gpuOutputs.Count(o =>
            !o.IsActive &&
            o.OutputTechnology == Native.OutputTechnology.Hdmi &&
            !o.TargetAvailable);
        if (unusedHdmiCount > 0)
        {
            items.Add(UnusedHdmiSummary(unusedHdmiCount));
        }

        foreach (var ddc in ddcStates.Where(d => !usedDdc.Contains(d.GdiDeviceName)))
        {
            screenByGdi.TryGetValue(ddc.GdiDeviceName, out var screen);
            items.Add(ToOrphanDdcItem(ddc, screen));
        }

        ScreenLayout.Arrange(items, ScreenLayout.GdiFromWindow(appHwnd), ScreenLayout.GdiFromCursor());

        var hdmiPorts = gpuOutputs.Where(o => o.OutputTechnology == Native.OutputTechnology.Hdmi).ToArray();
        return new MonitorSnapshot
        {
            Outputs = items,
            HdmiPortCount = hdmiPorts.Length,
            HdmiWithSinkCount = hdmiPorts.Count(o => o.TargetAvailable),
            CapturedAt = DateTime.Now
        };
    }

    public static SwitchResult SwitchToHdmi(string? gdiDeviceName) =>
        DdcService.SwitchToHdmi(gdiDeviceName);

    public static void EnableWindowsHdmi()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "DisplaySwitch.exe",
            Arguments = "/extend",
            UseShellExecute = true
        });
    }

    private static OutputItem ToItem(GpuOutput gpu, DdcState? ddc, PhysicalScreen? screen)
    {
        var signal = ResolveGpuSignal(gpu);
        var inputs = BuildChips(gpu, ddc, signal);
        var canSwitch = ddc?.Supported == true && inputs.Any(i => i.Label.StartsWith("HDMI", StringComparison.Ordinal));
        if (ddc?.Supported == true && !canSwitch)
        {
            canSwitch = true;
        }

        return new OutputItem
        {
            Key = gpu.Key,
            Title = gpu.MonitorName,
            Connector = gpu.Connector,
            SourceGdiName = gpu.SourceGdiName,
            IsActive = gpu.IsActive,
            TargetAvailable = gpu.TargetAvailable,
            Signal = signal,
            SignalText = SignalText(signal),
            PathText = gpu.IsActive ? "本機正在輸出" : gpu.TargetAvailable ? "已接上螢幕，Windows 尚未使用" : "未接上螢幕",
            CurrentInputText = ddc?.CurrentInput is byte code
                ? InputSelect.Name(code)
                : "無法讀取",
            DdcText = ddc is null
                ? (gpu.IsActive ? "沒有對應的 DDC 控制碼" : "未使用中，無法做 DDC 切換")
                : ddc.Supported
                    ? "DDC/CI 可用"
                    : ddc.Error ?? "DDC/CI 不可用",
            CanSwitchToHdmi = canSwitch,
            CanEnableWindowsHdmi = gpu.OutputTechnology == Native.OutputTechnology.Hdmi
                && gpu.TargetAvailable
                && !gpu.IsActive,
            Inputs = inputs,
            HasDesktopBounds = screen is not null,
            IsPrimary = screen?.IsPrimary ?? false,
            PixelLeft = screen?.Left ?? 0,
            PixelTop = screen?.Top ?? 0,
            PixelWidth = screen?.Width ?? 0,
            PixelHeight = screen?.Height ?? 0
        };
    }

    private static OutputItem UnusedHdmiSummary(int count) =>
        new()
        {
            Key = "hdmi-unused",
            Title = $"本機 HDMI 輸出孔 ×{count}",
            Connector = "HDMI",
            SourceGdiName = string.Empty,
            IsActive = false,
            TargetAvailable = false,
            Signal = SignalKind.Off,
            SignalText = "無訊號",
            PathText = "這些 HDMI 孔目前沒有偵測到螢幕（沒有 HPD／EDID）",
            CurrentInputText = "無",
            DdcText = "沒有接上螢幕，無法切換輸入源",
            CanSwitchToHdmi = false,
            CanEnableWindowsHdmi = false,
            Inputs = [new InputChip("HDMI", SignalKind.Off, false)]
        };

    private static OutputItem ToOrphanDdcItem(DdcState ddc, PhysicalScreen? screen)
    {
        var signal = ddc.Supported ? SignalKind.On : SignalKind.Unknown;
        return new OutputItem
        {
            Key = "ddc:" + ddc.GdiDeviceName,
            Title = ddc.Description,
            Connector = "未知",
            SourceGdiName = ddc.GdiDeviceName,
            IsActive = true,
            TargetAvailable = true,
            Signal = signal,
            SignalText = SignalText(signal),
            PathText = "本機正在輸出",
            CurrentInputText = ddc.CurrentInput is byte code ? InputSelect.Name(code) : "無法讀取",
            DdcText = ddc.Supported ? "DDC/CI 可用" : ddc.Error ?? "DDC/CI 不可用",
            CanSwitchToHdmi = ddc.Supported,
            CanEnableWindowsHdmi = false,
            Inputs = BuildChips(null, ddc, signal),
            HasDesktopBounds = screen is not null,
            IsPrimary = screen?.IsPrimary ?? false,
            PixelLeft = screen?.Left ?? 0,
            PixelTop = screen?.Top ?? 0,
            PixelWidth = screen?.Width ?? 0,
            PixelHeight = screen?.Height ?? 0
        };
    }

    private static SignalKind ResolveGpuSignal(GpuOutput gpu)
    {
        if (gpu.TargetAvailable || gpu.IsActive)
        {
            return SignalKind.On;
        }

        return SignalKind.Off;
    }

    private static string SignalText(SignalKind kind) => kind switch
    {
        SignalKind.On => "有訊號",
        SignalKind.Off => "無訊號",
        _ => "無法偵測"
    };

    private static IReadOnlyList<InputChip> BuildChips(GpuOutput? gpu, DdcState? ddc, SignalKind gpuSignal)
    {
        var chips = new List<InputChip>();
        var codes = new List<byte>();
        if (ddc is not null)
        {
            foreach (var code in ddc.AvailableInputs)
            {
                if (!codes.Contains(code))
                {
                    codes.Add(code);
                }
            }

            if (ddc.CurrentInput is byte current && !codes.Contains(current))
            {
                codes.Insert(0, current);
            }
        }

        if (codes.Count == 0)
        {
            if (gpu is not null)
            {
                chips.Add(new InputChip(gpu.Connector, gpuSignal, gpu.IsActive));
            }

            return chips;
        }

        foreach (var code in codes)
        {
            var isCurrent = ddc?.CurrentInput == code;
            SignalKind signal;
            if (isCurrent)
            {
                signal = gpuSignal == SignalKind.Off ? SignalKind.Unknown : SignalKind.On;
            }
            else
            {
                signal = SignalKind.Unknown;
            }

            chips.Add(new InputChip(InputSelect.Name(code), signal, isCurrent));
        }

        return chips;
    }
}
