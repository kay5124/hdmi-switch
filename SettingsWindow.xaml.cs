using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HdmiSwitch.Native;
using HdmiSwitch.Services;

namespace HdmiSwitch;

public partial class SettingsWindow : Window
{
    private static readonly string[] TimeFormats = ["hh\\:mm", "h\\:mm"];

    private readonly AppSettings _settings;
    private readonly Func<IReadOnlyList<HotkeyBinding>, IReadOnlyList<HotkeyRegistration>> _applyHotkeys;
    private bool _saved;

    internal SettingsWindow(
        AppSettings settings,
        IReadOnlyList<OutputItem> screens,
        IReadOnlyList<InputFamily> families,
        Func<IReadOnlyList<HotkeyBinding>, IReadOnlyList<HotkeyRegistration>> applyHotkeys)
    {
        InitializeComponent();
        DataContext = this;
        _settings = settings;
        _applyHotkeys = applyHotkeys;

        PowerTargets.Add(new MonitorOption(null, "全部螢幕"));
        foreach (var screen in screens.Where(s => !string.IsNullOrWhiteSpace(s.SourceGdiName)))
        {
            PowerTargets.Add(new MonitorOption(screen.SourceGdiName, screen.PlaceTitle));
        }

        BuildHotkeyRows(families);
        BuildScheduleRows();
        BuildLabelRows(screens);
        Closing += OnClosing;
    }

    public ObservableCollection<HotkeyRow> HotkeyRows { get; } = [];

    public ObservableCollection<ScheduleRow> ScheduleRows { get; } = [];

    public ObservableCollection<LabelRow> LabelRows { get; } = [];

    public ObservableCollection<MonitorOption> PowerTargets { get; } = [];

    private void BuildHotkeyRows(IReadOnlyList<InputFamily> families)
    {
        var wanted = new HashSet<InputFamily>(families);
        foreach (var binding in _settings.Hotkeys)
        {
            wanted.Add(binding.Family);
        }

        var ordered = InputSelect.BatchOrder.Where(wanted.Contains).ToArray();
        if (ordered.Length == 0)
        {
            ordered = InputSelect.BatchOrder.ToArray();
        }

        foreach (var family in ordered)
        {
            var saved = _settings.Hotkeys.FirstOrDefault(b => b.Family == family);
            HotkeyRows.Add(new HotkeyRow(
                family,
                InputSelect.FamilyName(family),
                saved?.Modifiers ?? 0,
                saved?.Key ?? 0));
        }
    }

    private void BuildScheduleRows()
    {
        foreach (var schedule in _settings.DailySchedules)
        {
            ScheduleRows.Add(new ScheduleRow(
                schedule.Id,
                ResolveTarget(schedule.TargetGdiName),
                $"{schedule.Time.Hours:00}:{schedule.Time.Minutes:00}",
                schedule.Enabled));
        }
    }

    private void BuildLabelRows(IReadOnlyList<OutputItem> screens)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var screen in screens)
        {
            var monitorKey = screen.Title;
            if (string.IsNullOrWhiteSpace(monitorKey))
            {
                continue;
            }

            foreach (var chip in screen.Inputs)
            {
                if (chip.Code is not byte code)
                {
                    continue;
                }

                if (!seen.Add($"{monitorKey}|{code}"))
                {
                    continue;
                }

                var saved = _settings.InputLabelOverrides.FirstOrDefault(o =>
                    string.Equals(o.MonitorKey, monitorKey, StringComparison.OrdinalIgnoreCase) &&
                    o.InputCode == code);

                LabelRows.Add(new LabelRow(
                    monitorKey,
                    screen.PlaceTitle,
                    code,
                    InputSelect.Name(code),
                    saved?.Label ?? string.Empty));
            }
        }
    }

    private MonitorOption ResolveTarget(string? gdiName)
    {
        if (gdiName is null)
        {
            return PowerTargets[0];
        }

        var hit = PowerTargets.FirstOrDefault(o =>
            string.Equals(o.GdiName, gdiName, StringComparison.OrdinalIgnoreCase));
        if (hit is not null)
        {
            return hit;
        }

        var missing = new MonitorOption(gdiName, $"{gdiName}（目前偵測不到）");
        PowerTargets.Add(missing);
        return missing;
    }

    private void HotkeyBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: HotkeyRow row })
        {
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            return;
        }

        if (key == Key.Escape)
        {
            row.Assign(0, 0);
            return;
        }

        uint modifiers = 0;
        var pressed = Keyboard.Modifiers;
        if (pressed.HasFlag(ModifierKeys.Control))
        {
            modifiers |= NativeMethods.ModControl;
        }

        if (pressed.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= NativeMethods.ModAlt;
        }

        if (pressed.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= NativeMethods.ModShift;
        }

        if (pressed.HasFlag(ModifierKeys.Windows))
        {
            modifiers |= NativeMethods.ModWin;
        }

        if (modifiers == 0)
        {
            row.SetError("請至少搭配一個 Ctrl／Alt／Shift／Win，否則會蓋掉一般打字。");
            return;
        }

        row.Assign(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
    }

    private void ClearHotkey_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HotkeyRow row })
        {
            row.Assign(0, 0);
        }
    }

    private void AddSchedule_OnClick(object sender, RoutedEventArgs e) =>
        ScheduleRows.Add(new ScheduleRow(Guid.NewGuid(), PowerTargets[0], "23:00", true));

    private void RemoveSchedule_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ScheduleRow row })
        {
            ScheduleRows.Remove(row);
        }
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var bindings = HotkeyRows
            .Where(r => r.Key != 0)
            .Select(r => new HotkeyBinding { Family = r.Family, Modifiers = r.Modifiers, Key = r.Key })
            .ToArray();

        foreach (var row in HotkeyRows)
        {
            row.SetError(null);
        }

        // 註冊失敗（組合鍵被佔用）不靜默吞掉：標紅、留在視窗讓使用者換一組。
        var failed = _applyHotkeys(bindings).Where(r => !r.Success).ToArray();
        if (failed.Length > 0)
        {
            foreach (var failure in failed)
            {
                HotkeyRows.FirstOrDefault(r => r.Family == failure.Binding.Family)?.SetError(failure.Error);
            }

            return;
        }

        var schedules = new List<DailyPowerSchedule>();
        var scheduleError = false;
        foreach (var row in ScheduleRows)
        {
            if (!TimeSpan.TryParseExact(
                    (row.TimeText ?? string.Empty).Trim(),
                    TimeFormats,
                    CultureInfo.InvariantCulture,
                    out var time) ||
                time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
            {
                row.SetError("時間格式要像 22:30（24 小時制）。");
                scheduleError = true;
                continue;
            }

            row.SetError(null);
            schedules.Add(new DailyPowerSchedule
            {
                Id = row.Id,
                TargetGdiName = row.Target?.GdiName,
                Time = time,
                Enabled = row.Enabled
            });
        }

        if (scheduleError)
        {
            return;
        }

        // 這次沒列出來的螢幕（目前偵測不到）保留原本的覆寫，不要被清掉。
        var handled = LabelRows.Select(r => (r.MonitorKey, r.Code)).ToHashSet();
        var labels = _settings.InputLabelOverrides
            .Where(o => !handled.Contains((o.MonitorKey, o.InputCode)))
            .ToList();
        labels.AddRange(LabelRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Label))
            .Select(r => new InputLabelOverride
            {
                MonitorKey = r.MonitorKey,
                InputCode = r.Code,
                Label = r.Label.Trim()
            }));

        _settings.Hotkeys = bindings.ToList();
        _settings.DailySchedules = schedules;
        _settings.InputLabelOverrides = labels;

        _saved = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_saved)
        {
            return;
        }

        // 取消時把快捷鍵還原成存檔裡的狀態（Save 失敗那次可能已經改動過註冊）。
        _applyHotkeys(_settings.Hotkeys);
    }
}

public sealed class HotkeyRow(InputFamily family, string familyLabel, uint modifiers, uint key) : INotifyPropertyChanged
{
    private uint _modifiers = modifiers;
    private uint _key = key;
    private string? _error;

    public InputFamily Family { get; } = family;

    public string FamilyLabel { get; } = familyLabel;

    public uint Modifiers => _modifiers;

    public uint Key => _key;

    public string Display => HotkeyText.Describe(_modifiers, _key);

    public string? Error => _error;

    public bool HasError => !string.IsNullOrEmpty(_error);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Assign(uint newModifiers, uint newKey)
    {
        _modifiers = newModifiers;
        _key = newKey;
        _error = null;
        Raise(nameof(Modifiers), nameof(Key), nameof(Display), nameof(Error), nameof(HasError));
    }

    public void SetError(string? error)
    {
        _error = error;
        Raise(nameof(Error), nameof(HasError));
    }

    private void Raise(params string[] names)
    {
        foreach (var name in names)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

public sealed class ScheduleRow(Guid id, MonitorOption target, string timeText, bool enabled) : INotifyPropertyChanged
{
    private MonitorOption _target = target;
    private string _timeText = timeText;
    private bool _enabled = enabled;
    private string? _error;

    public Guid Id { get; } = id;

    public MonitorOption Target
    {
        get => _target;
        set => Set(ref _target, value);
    }

    public string TimeText
    {
        get => _timeText;
        set => Set(ref _timeText, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public string? Error => _error;

    public bool HasError => !string.IsNullOrEmpty(_error);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetError(string? error)
    {
        _error = error;
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
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

public sealed class LabelRow(string monitorKey, string monitorTitle, byte code, string defaultLabel, string label)
    : INotifyPropertyChanged
{
    private string _label = label;

    public string MonitorKey { get; } = monitorKey;

    public string MonitorTitle { get; } = monitorTitle;

    public byte Code { get; } = code;

    public string DefaultLabel { get; } = defaultLabel;

    public string Label
    {
        get => _label;
        set
        {
            if (_label == value)
            {
                return;
            }

            _label = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
