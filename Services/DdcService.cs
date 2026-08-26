using System.Runtime.InteropServices;
using System.Text;
using HdmiSwitch.Native;

namespace HdmiSwitch.Services;

internal sealed record DdcState(
    string GdiDeviceName,
    string Description,
    bool Supported,
    string? Error,
    byte? CurrentInput,
    IReadOnlyList<byte> AvailableInputs);

internal static class DdcService
{
    private static readonly object CapabilitiesLock = new();
    private static readonly Dictionary<string, (DateTime At, string? Caps)> CapabilitiesCache = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<DdcState> QueryActiveMonitors()
    {
        var monitors = new List<IntPtr>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref Rect _, IntPtr _) =>
        {
            monitors.Add(hMonitor);
            return true;
        }, IntPtr.Zero);

        var results = new List<DdcState>();
        foreach (var hMonitor in monitors)
        {
            results.Add(QueryMonitor(hMonitor));
        }

        return results;
    }

    public static SwitchResult SwitchInput(string? gdiDeviceName, InputRequest request)
    {
        var targets = QueryPhysicalTargets()
            .Where(t => gdiDeviceName is null ||
                        string.Equals(t.GdiDeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (targets.Length == 0)
        {
            return SwitchResult.Fail(gdiDeviceName ?? "全部螢幕", "找不到可控制的實體螢幕。");
        }

        var notes = new List<string>();
        var anySuccess = false;
        foreach (var target in targets)
        {
            var result = SwitchPhysicalMonitor(target, request);
            notes.Add(result.Message);
            anySuccess |= result.Success;
        }

        return new SwitchResult(anySuccess, string.Join(Environment.NewLine, notes));
    }

    /// <summary>
    /// 用 DDC VCP 0xD6（Power Mode）送 0x04 軟關機。
    /// 選軟關機而非硬關機（0x05）：軟關機通常靠訊號恢復或螢幕電源鍵就能喚醒。
    /// </summary>
    public static SwitchResult PowerOff(string? gdiDeviceName)
    {
        var targets = QueryPhysicalTargets()
            .Where(t => gdiDeviceName is null ||
                        string.Equals(t.GdiDeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (targets.Length == 0)
        {
            return SwitchResult.Fail(gdiDeviceName ?? "全部螢幕", "找不到可控制的實體螢幕。");
        }

        var notes = new List<string>();
        var anySuccess = false;
        foreach (var target in targets)
        {
            var result = PowerOffPhysicalMonitor(target);
            notes.Add(result.Message);
            anySuccess |= result.Success;
        }

        return new SwitchResult(anySuccess, string.Join(Environment.NewLine, notes));
    }

    private static SwitchResult PowerOffPhysicalMonitor(PhysicalTarget target)
    {
        var physical = OpenPhysical(target.HMonitor);
        if (physical is null)
        {
            return SwitchResult.Fail(target.Description, "無法開啟 DDC/CI。");
        }

        try
        {
            var handle = physical.Value.Monitors[0].hPhysicalMonitor;
            return NativeMethods.SetVCPFeature(handle, NativeMethods.VcpPowerMode, NativeMethods.PowerModeSoftOff)
                ? SwitchResult.Ok(target.Description, "已送出關閉指令（DDC 軟關機）。")
                : SwitchResult.Fail(target.Description, "這台螢幕不接受 DDC 電源指令（VCP 0xD6）。");
        }
        finally
        {
            physical.Value.Dispose();
        }
    }

    private static DdcState QueryMonitor(IntPtr hMonitor)
    {
        var info = GetGdiName(hMonitor);
        if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
        {
            return new DdcState(info.Device, info.Device, false, "此輸出沒有實體螢幕（可能是虛擬顯示）。", null, []);
        }

        var physical = new PhysicalMonitor[count];
        if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physical))
        {
            return new DdcState(info.Device, info.Device, false, "無法開啟 DDC/CI。", null, []);
        }

        try
        {
            var first = physical[0];
            var description = string.IsNullOrWhiteSpace(first.szPhysicalMonitorDescription)
                ? info.Device
                : first.szPhysicalMonitorDescription;
            var caps = TryReadCapabilities(first.hPhysicalMonitor, info.Device);
            var available = InputSelect.ParseAvailableInputs(caps ?? string.Empty);
            if (!TryGetInput(first.hPhysicalMonitor, out var current))
            {
                return new DdcState(info.Device, description, false, "不支援輸入源查詢。請在螢幕 OSD 開啟 DDC/CI。", null, available);
            }

            return new DdcState(info.Device, description, true, null, current, available);
        }
        finally
        {
            NativeMethods.DestroyPhysicalMonitors(count, physical);
        }
    }

    private static SwitchResult SwitchPhysicalMonitor(PhysicalTarget target, InputRequest request)
    {
        var physical = OpenPhysical(target.HMonitor);
        if (physical is null)
        {
            return SwitchResult.Fail(target.Description, "無法開啟 DDC/CI。");
        }

        try
        {
            var handle = physical.Value.Monitors[0].hPhysicalMonitor;
            var caps = TryReadCapabilities(handle, target.GdiDeviceName);
            var available = InputSelect.ParseAvailableInputs(caps ?? string.Empty);
            byte? current = TryGetInput(handle, out var value) ? value : null;

            if (current is byte currentCode && AlreadyOnTarget(currentCode, request))
            {
                return SwitchResult.Ok(target.Description, $"已經是 {InputSelect.Name(currentCode)}。");
            }

            var candidates = Candidates(request, available);
            if (candidates.Length == 0)
            {
                return SwitchResult.Fail(target.Description, $"這台螢幕沒有可用的 {request.DisplayName} 輸入。");
            }

            foreach (var code in candidates)
            {
                if (!NativeMethods.SetVCPFeature(handle, NativeMethods.VcpInputSelect, code))
                {
                    continue;
                }

                Thread.Sleep(350);
                if (TryGetInput(handle, out var after) && after != code && after == current)
                {
                    continue;
                }

                return SwitchResult.Ok(target.Description, $"已切到 {InputSelect.Name(code)}。");
            }

            return SwitchResult.Fail(target.Description, $"切到 {request.DisplayName} 沒有成功。");
        }
        finally
        {
            physical.Value.Dispose();
        }
    }

    private static bool AlreadyOnTarget(byte current, InputRequest request)
    {
        if (request.ExactCode is byte exact)
        {
            return current == exact;
        }

        return request.Family is InputFamily family && InputSelect.FamilyOf(current) == family;
    }

    private static byte[] Candidates(InputRequest request, IReadOnlyList<byte> available)
    {
        if (request.ExactCode is byte exact)
        {
            return [exact];
        }

        if (request.Family is not InputFamily family)
        {
            return [];
        }

        var preferred = InputSelect.Preference(family);
        var filtered = preferred.Where(code => available.Count == 0 || available.Contains(code)).ToArray();
        return filtered.Length > 0 ? filtered : preferred.ToArray();
    }

    private static bool TryGetInput(IntPtr handle, out byte current)
    {
        current = 0;
        if (!NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                handle,
                NativeMethods.VcpInputSelect,
                out _,
                out var value,
                out _))
        {
            return false;
        }

        current = (byte)(value & 0xFF);
        return true;
    }

    private static string? TryReadCapabilities(IntPtr handle, string cacheKey)
    {
        lock (CapabilitiesLock)
        {
            if (!string.IsNullOrWhiteSpace(cacheKey) &&
                CapabilitiesCache.TryGetValue(cacheKey, out var cached) &&
                DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(60))
            {
                return cached.Caps;
            }
        }

        if (!NativeMethods.GetCapabilitiesStringLength(handle, out var length) || length == 0)
        {
            StoreCaps(cacheKey, null);
            return null;
        }

        var buffer = new StringBuilder((int)length);
        var caps = NativeMethods.CapabilitiesRequestAndCapabilitiesReply(handle, buffer, length)
            ? buffer.ToString()
            : null;
        StoreCaps(cacheKey, caps);
        return caps;
    }

    private static void StoreCaps(string cacheKey, string? caps)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return;
        }

        lock (CapabilitiesLock)
        {
            CapabilitiesCache[cacheKey] = (DateTime.UtcNow, caps);
        }
    }

    private static (string Device, string DeviceName) GetGdiName(IntPtr hMonitor)
    {
        var info = new MonitorInfoEx
        {
            cbSize = Marshal.SizeOf<MonitorInfoEx>(),
            szDevice = string.Empty
        };
        if (!NativeMethods.GetMonitorInfo(hMonitor, ref info))
        {
            return (string.Empty, string.Empty);
        }

        return (info.szDevice ?? string.Empty, info.szDevice ?? string.Empty);
    }

    private static IReadOnlyList<PhysicalTarget> QueryPhysicalTargets()
    {
        var monitors = new List<PhysicalTarget>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref Rect _, IntPtr _) =>
        {
            var gdi = GetGdiName(hMonitor);
            monitors.Add(new PhysicalTarget(hMonitor, gdi.Device, gdi.Device));
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    private static OpenedMonitors? OpenPhysical(IntPtr hMonitor)
    {
        if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
        {
            return null;
        }

        var physical = new PhysicalMonitor[count];
        if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physical))
        {
            return null;
        }

        return new OpenedMonitors(count, physical);
    }

    private readonly record struct PhysicalTarget(IntPtr HMonitor, string GdiDeviceName, string Description);

    private readonly struct OpenedMonitors(uint count, PhysicalMonitor[] monitors) : IDisposable
    {
        public PhysicalMonitor[] Monitors { get; } = monitors;

        public void Dispose() => NativeMethods.DestroyPhysicalMonitors(count, Monitors);
    }
}

internal readonly record struct SwitchResult(bool Success, string Message)
{
    public static SwitchResult Ok(string name, string message) => new(true, $"{name}：{message}");

    public static SwitchResult Fail(string name, string message) => new(false, $"{name}：{message}");
}
