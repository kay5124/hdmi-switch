using System.Runtime.InteropServices;
using HdmiSwitch.Native;

namespace HdmiSwitch.Services;

internal sealed record PhysicalScreen(
    string GdiDeviceName,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary)
{
    public int CenterX => Left + Width / 2;

    public int CenterY => Top + Height / 2;
}

internal static class ScreenLayout
{
    public const double CanvasWidth = 900;
    public const double CanvasHeight = 300;

    public static IReadOnlyList<PhysicalScreen> Query()
    {
        var screens = new List<PhysicalScreen>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref Rect _, IntPtr _) =>
        {
            var info = ReadInfo(hMonitor);
            if (info is null)
            {
                return true;
            }

            screens.Add(info);
            return true;
        }, IntPtr.Zero);
        return screens;
    }

    public static string? GdiFromWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        return ReadInfo(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest))?.GdiDeviceName;
    }

    public static string? GdiFromCursor()
    {
        if (!NativeMethods.GetCursorPos(out var point))
        {
            return null;
        }

        return ReadInfo(NativeMethods.MonitorFromPoint(point, NativeMethods.MonitorDefaultToNearest))?.GdiDeviceName;
    }

    public static void Arrange(IReadOnlyList<OutputItem> items, string? appGdi, string? mouseGdi)
    {
        var placed = items.Where(i => i.HasDesktopBounds)
            .OrderBy(i => i.PixelLeft)
            .ThenBy(i => i.PixelTop)
            .ToList();
        if (placed.Count == 0)
        {
            return;
        }

        var number = 1;
        foreach (var item in placed)
        {
            item.DisplayNumber = number++;
            item.IsAppHere = NamesEqual(item.SourceGdiName, appGdi);
            item.IsMouseHere = NamesEqual(item.SourceGdiName, mouseGdi);
        }

        AssignPlaceLabels(placed);
        AssignCanvas(placed);
    }

    public static void UpdateHereFlags(IEnumerable<OutputItem> items, string? appGdi, string? mouseGdi)
    {
        foreach (var item in items)
        {
            if (!item.HasDesktopBounds)
            {
                continue;
            }

            item.IsAppHere = NamesEqual(item.SourceGdiName, appGdi);
            item.IsMouseHere = NamesEqual(item.SourceGdiName, mouseGdi);
        }
    }

    private static PhysicalScreen? ReadInfo(IntPtr hMonitor)
    {
        if (hMonitor == IntPtr.Zero)
        {
            return null;
        }

        var info = new MonitorInfoEx
        {
            cbSize = Marshal.SizeOf<MonitorInfoEx>(),
            szDevice = string.Empty
        };
        if (!NativeMethods.GetMonitorInfo(hMonitor, ref info))
        {
            return null;
        }

        return new PhysicalScreen(
            info.szDevice ?? string.Empty,
            info.rcMonitor.Left,
            info.rcMonitor.Top,
            info.rcMonitor.Right - info.rcMonitor.Left,
            info.rcMonitor.Bottom - info.rcMonitor.Top,
            (info.dwFlags & NativeMethods.MonitorInfoPrimary) != 0);
    }

    private static void AssignPlaceLabels(List<OutputItem> placed)
    {
        var xs = placed.Select(i => (double)i.PixelLeft + i.PixelWidth / 2.0).ToList();
        var ys = placed.Select(i => (double)i.PixelTop + i.PixelHeight / 2.0).ToList();
        foreach (var item in placed)
        {
            var cx = item.PixelLeft + item.PixelWidth / 2.0;
            var cy = item.PixelTop + item.PixelHeight / 2.0;
            var horizontal = AxisLabel(cx, xs, "左", "中", "右");
            var vertical = AxisLabel(cy, ys, "上", "中", "下");
            item.PlaceLabel = Combine(horizontal, vertical, item.IsPrimary);
            item.PlaceTitle = $"螢幕 {item.DisplayNumber} · {item.PlaceLabel}";
        }
    }

    private static string AxisLabel(double value, List<double> values, string low, string mid, string high)
    {
        if (values.Count <= 1 || values.Max() - values.Min() < 80)
        {
            return string.Empty;
        }

        var min = values.Min();
        var max = values.Max();
        if (values.Count == 2 || max - min < 160)
        {
            return value <= (min + max) / 2 ? low : high;
        }

        var third = (max - min) / 3.0;
        if (value < min + third)
        {
            return low;
        }

        if (value > max - third)
        {
            return high;
        }

        return mid;
    }

    private static string Combine(string horizontal, string vertical, bool isPrimary)
    {
        if (horizontal.Length == 0 && vertical.Length == 0)
        {
            return isPrimary ? "主螢幕" : "螢幕";
        }

        if (vertical.Length == 0)
        {
            return horizontal;
        }

        if (horizontal.Length == 0)
        {
            return vertical;
        }

        if (vertical == "中")
        {
            return horizontal;
        }

        if (horizontal == "中")
        {
            return vertical;
        }

        return horizontal + vertical;
    }

    private static void AssignCanvas(List<OutputItem> placed)
    {
        const double pad = 16;
        var minL = placed.Min(i => i.PixelLeft);
        var minT = placed.Min(i => i.PixelTop);
        var maxR = placed.Max(i => i.PixelLeft + i.PixelWidth);
        var maxB = placed.Max(i => i.PixelTop + i.PixelHeight);
        var unionW = Math.Max(maxR - minL, 1);
        var unionH = Math.Max(maxB - minT, 1);
        var innerW = CanvasWidth - pad * 2;
        var innerH = CanvasHeight - pad * 2;
        var scale = Math.Min(innerW / unionW, innerH / unionH);
        var offsetX = pad + (innerW - unionW * scale) / 2;
        var offsetY = pad + (innerH - unionH * scale) / 2;
        foreach (var item in placed)
        {
            item.LayoutX = offsetX + (item.PixelLeft - minL) * scale;
            item.LayoutY = offsetY + (item.PixelTop - minT) * scale;
            item.LayoutW = Math.Max(item.PixelWidth * scale, 120);
            item.LayoutH = Math.Max(item.PixelHeight * scale, 88);
        }
    }

    private static bool NamesEqual(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
