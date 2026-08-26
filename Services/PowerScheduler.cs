using System.Windows.Threading;

namespace HdmiSwitch.Services;

/// <summary>關閉螢幕的目標。GdiName 為 null 代表「全部螢幕」。</summary>
public sealed record MonitorOption(string? GdiName, string Display);

/// <summary>
/// 一顆計時器處理兩種模式：
/// 1. 每天固定時間（持久化，來自 AppSettings.DailySchedules）
/// 2. 倒數計時（不持久化，純運行期狀態）
/// tick 週期刻意比一分鐘短，倒數才不會慢半分鐘才觸發；每日排程靠 (Id, 日期) 去重，
/// 所以同一分鐘內多次 tick 也只會觸發一次。
/// </summary>
internal sealed class PowerScheduler : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Func<IReadOnlyList<DailyPowerSchedule>> _schedules;
    private readonly Action<string?, string> _trigger;
    private readonly Dictionary<Guid, DateOnly> _firedToday = [];

    private DateTime? _countdownDue;
    private string? _countdownTarget;
    private string _countdownLabel = string.Empty;

    public PowerScheduler(
        Func<IReadOnlyList<DailyPowerSchedule>> schedules,
        Action<string?, string> trigger)
    {
        _schedules = schedules;
        _trigger = trigger;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) => Tick();
    }

    public event EventHandler? StateChanged;

    public bool HasCountdown => _countdownDue is not null;

    public string CountdownText
    {
        get
        {
            if (_countdownDue is not DateTime due)
            {
                return string.Empty;
            }

            var remaining = due - DateTime.Now;
            var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
            return $"倒數中：{_countdownLabel} 還有 {minutes} 分鐘關閉";
        }
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void StartCountdown(string? targetGdiName, string targetLabel, int minutes)
    {
        _countdownDue = DateTime.Now.AddMinutes(minutes);
        _countdownTarget = targetGdiName;
        _countdownLabel = targetLabel;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CancelCountdown()
    {
        if (_countdownDue is null)
        {
            return;
        }

        ClearCountdown();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _timer.Stop();
        ClearCountdown();
    }

    private void Tick()
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);

        foreach (var rule in _schedules())
        {
            if (!rule.Enabled)
            {
                continue;
            }

            if (now.Hour != rule.Time.Hours || now.Minute != rule.Time.Minutes)
            {
                continue;
            }

            if (_firedToday.TryGetValue(rule.Id, out var firedOn) && firedOn == today)
            {
                continue;
            }

            _firedToday[rule.Id] = today;
            _trigger(rule.TargetGdiName, $"每天 {rule.Time:hh\\:mm} 排程");
        }

        if (_countdownDue is not DateTime due)
        {
            return;
        }

        if (now >= due)
        {
            var target = _countdownTarget;
            var label = _countdownLabel;
            ClearCountdown();
            StateChanged?.Invoke(this, EventArgs.Empty);
            _trigger(target, $"倒數結束（{label}）");
            return;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearCountdown()
    {
        _countdownDue = null;
        _countdownTarget = null;
        _countdownLabel = string.Empty;
    }
}
