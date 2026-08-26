using System.Runtime.InteropServices;
using HdmiSwitch.Native;

namespace HdmiSwitch.Services;

/// <summary>
/// 一個解析度選項。同一個 寬×高 只留更新頻率最高的一筆，
/// 所以 Label 刻意不顯示頻率（避免顯示的頻率跟目前實際跑的不一樣）。
/// </summary>
public sealed record DisplayMode(int Width, int Height, int Frequency)
{
    public string Label => $"{Width} × {Height}";

    public string Detail => Frequency > 0 ? $"{Width} × {Height} @ {Frequency}Hz" : Label;
}

/// <summary>查詢／套用 GDI 裝置的顯示模式。維持 MonitorHub 風格：靜態、失敗回傳結果不拋例外。</summary>
internal static class ResolutionService
{
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, (DateTime At, IReadOnlyList<DisplayMode> Modes)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>列舉該 GDI 裝置支援的模式，用 寬×高 去重（留最高頻率），由大到小排序。</summary>
    public static IReadOnlyList<DisplayMode> ListResolutions(string gdiDeviceName)
    {
        if (string.IsNullOrWhiteSpace(gdiDeviceName))
        {
            return [];
        }

        lock (CacheLock)
        {
            if (Cache.TryGetValue(gdiDeviceName, out var cached) &&
                DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(5))
            {
                return cached.Modes;
            }
        }

        var best = new Dictionary<(int Width, int Height), int>();
        var index = 0;
        while (true)
        {
            var mode = NewDevmode();
            if (!NativeMethods.EnumDisplaySettingsEx(gdiDeviceName, index, ref mode, 0))
            {
                break;
            }

            index++;
            if (mode.dmBitsPerPel < 32 || mode.dmPelsWidth == 0 || mode.dmPelsHeight == 0)
            {
                continue;
            }

            var key = ((int)mode.dmPelsWidth, (int)mode.dmPelsHeight);
            var frequency = (int)mode.dmDisplayFrequency;
            if (!best.TryGetValue(key, out var existing) || frequency > existing)
            {
                best[key] = frequency;
            }
        }

        var list = best
            .Select(pair => new DisplayMode(pair.Key.Width, pair.Key.Height, pair.Value))
            .OrderByDescending(m => (long)m.Width * m.Height)
            .ThenByDescending(m => m.Width)
            .ToArray();

        lock (CacheLock)
        {
            Cache[gdiDeviceName] = (DateTime.UtcNow, list);
        }

        return list;
    }

    public static DisplayMode? Current(string gdiDeviceName)
    {
        if (string.IsNullOrWhiteSpace(gdiDeviceName))
        {
            return null;
        }

        var mode = NewDevmode();
        if (!NativeMethods.EnumDisplaySettingsEx(gdiDeviceName, NativeMethods.EnumCurrentSettings, ref mode, 0))
        {
            return null;
        }

        return new DisplayMode((int)mode.dmPelsWidth, (int)mode.dmPelsHeight, (int)mode.dmDisplayFrequency);
    }

    public static SwitchResult Apply(string gdiDeviceName, DisplayMode target)
    {
        if (string.IsNullOrWhiteSpace(gdiDeviceName))
        {
            return SwitchResult.Fail("解析度", "這個項目沒有對應的顯示裝置。");
        }

        var mode = NewDevmode();
        if (!NativeMethods.EnumDisplaySettingsEx(gdiDeviceName, NativeMethods.EnumCurrentSettings, ref mode, 0))
        {
            return SwitchResult.Fail(gdiDeviceName, "讀不到目前的顯示模式。");
        }

        mode.dmPelsWidth = (uint)target.Width;
        mode.dmPelsHeight = (uint)target.Height;
        mode.dmFields |= NativeMethods.DmPelsWidth | NativeMethods.DmPelsHeight;
        if (target.Frequency > 0)
        {
            mode.dmDisplayFrequency = (uint)target.Frequency;
            mode.dmFields |= NativeMethods.DmDisplayFrequency;
        }

        var result = NativeMethods.ChangeDisplaySettingsEx(
            gdiDeviceName,
            ref mode,
            IntPtr.Zero,
            NativeMethods.CdsUpdateRegistry,
            IntPtr.Zero);

        return result == NativeMethods.DispChangeSuccessful
            ? SwitchResult.Ok(gdiDeviceName, $"已切到 {target.Detail}。")
            : SwitchResult.Fail(gdiDeviceName, $"Windows 拒絕了 {target.Detail}（代碼 {result}）。");
    }

    private static Devmode NewDevmode() => new()
    {
        dmDeviceName = string.Empty,
        dmFormName = string.Empty,
        dmSize = (ushort)Marshal.SizeOf<Devmode>()
    };
}
