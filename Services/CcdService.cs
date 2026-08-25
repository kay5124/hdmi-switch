using System.Runtime.InteropServices;
using HdmiSwitch.Native;

namespace HdmiSwitch.Services;

internal sealed record GpuOutput(
    string Key,
    string SourceGdiName,
    string MonitorName,
    string Connector,
    int OutputTechnology,
    bool TargetAvailable,
    bool IsActive,
    uint TargetId);

internal static class CcdService
{
    public static IReadOnlyList<GpuOutput> QueryOutputs()
    {
        DisplayConfigPathInfo[]? paths = null;
        var pathCountUsed = 0u;
        var status = -1;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            status = NativeMethods.GetDisplayConfigBufferSizes(
                NativeMethods.QdcAllPaths,
                out var pathCount,
                out var modeCount);
            if (status != NativeMethods.ErrorSuccess || pathCount == 0)
            {
                return [];
            }

            paths = new DisplayConfigPathInfo[pathCount];
            var modes = new DisplayConfigModeInfo[Math.Max(modeCount, 1)];
            status = NativeMethods.QueryDisplayConfig(
                NativeMethods.QdcAllPaths,
                ref pathCount,
                paths,
                ref modeCount,
                modes,
                IntPtr.Zero);
            if (status == NativeMethods.ErrorSuccess)
            {
                pathCountUsed = pathCount;
                break;
            }
        }

        if (status != NativeMethods.ErrorSuccess || paths is null)
        {
            return [];
        }

        var byTarget = new Dictionary<string, GpuOutput>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < (int)pathCountUsed; i++)
        {
            var path = paths[i];
            var key = TargetKey(path.targetInfo.adapterId, path.targetInfo.id);
            var isActive = (path.flags & NativeMethods.DisplayConfigPathActive) != 0;
            if (byTarget.TryGetValue(key, out var existing))
            {
                if (!existing.IsActive && isActive)
                {
                    byTarget[key] = ToOutput(path, isActive);
                }

                continue;
            }

            byTarget[key] = ToOutput(path, isActive);
        }

        return byTarget.Values
            .OrderByDescending(o => o.IsActive)
            .ThenBy(o => o.Connector)
            .ThenBy(o => o.MonitorName)
            .ToArray();
    }

    public static string ConnectorName(int technology) => technology switch
    {
        OutputTechnology.Hdmi => "HDMI",
        OutputTechnology.Dvi => "DVI",
        OutputTechnology.Hd15 => "VGA",
        OutputTechnology.DisplayPortExternal => "DisplayPort",
        OutputTechnology.DisplayPortEmbedded => "內建 DisplayPort",
        OutputTechnology.Internal => "內建",
        OutputTechnology.Miracast => "Miracast",
        OutputTechnology.DisplayPortUsbTunnel => "USB DisplayPort",
        _ => $"其他 ({technology})"
    };

    private static GpuOutput ToOutput(DisplayConfigPathInfo path, bool isActive)
    {
        var sourceName = QuerySourceName(path.sourceInfo.adapterId, path.sourceInfo.id);
        var target = QueryTargetName(path.targetInfo.adapterId, path.targetInfo.id);
        var connector = ConnectorName(path.targetInfo.outputTechnology);
        var monitorName = string.IsNullOrWhiteSpace(target.FriendlyName)
            ? (path.targetInfo.targetAvailable ? $"{connector} 螢幕" : $"{connector} 輸出")
            : target.FriendlyName;

        return new GpuOutput(
            TargetKey(path.targetInfo.adapterId, path.targetInfo.id),
            sourceName,
            monitorName,
            connector,
            path.targetInfo.outputTechnology,
            path.targetInfo.targetAvailable,
            isActive,
            path.targetInfo.id);
    }

    private static string QuerySourceName(Luid adapterId, uint id)
    {
        var packet = new DisplayConfigSourceDeviceName
        {
            header = new DisplayConfigDeviceInfoHeader
            {
                type = NativeMethods.DisplayConfigDeviceInfoGetSourceName,
                size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                adapterId = adapterId,
                id = id
            },
            viewGdiDeviceName = string.Empty
        };

        return NativeMethods.DisplayConfigGetDeviceInfo(ref packet) == NativeMethods.ErrorSuccess
            ? packet.viewGdiDeviceName ?? string.Empty
            : string.Empty;
    }

    private static (string FriendlyName, string DevicePath) QueryTargetName(Luid adapterId, uint id)
    {
        var packet = new DisplayConfigTargetDeviceName
        {
            header = new DisplayConfigDeviceInfoHeader
            {
                type = NativeMethods.DisplayConfigDeviceInfoGetTargetName,
                size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                adapterId = adapterId,
                id = id
            },
            monitorFriendlyDeviceName = string.Empty,
            monitorDevicePath = string.Empty
        };

        if (NativeMethods.DisplayConfigGetDeviceInfo(ref packet) != NativeMethods.ErrorSuccess)
        {
            return (string.Empty, string.Empty);
        }

        return (packet.monitorFriendlyDeviceName ?? string.Empty, packet.monitorDevicePath ?? string.Empty);
    }

    private static string TargetKey(Luid adapterId, uint id) =>
        $"{adapterId.HighPart:X8}-{adapterId.LowPart:X8}:{id}";
}
