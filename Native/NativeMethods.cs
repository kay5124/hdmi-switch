using System.Runtime.InteropServices;
using System.Text;

namespace HdmiSwitch.Native;

internal static class NativeMethods
{
    public const int WmDisplayChange = 0x007E;
    public const int ErrorSuccess = 0;
    public const uint QdcAllPaths = 1;
    public const uint DisplayConfigPathActive = 1;
    public const int DisplayConfigDeviceInfoGetSourceName = 1;
    public const int DisplayConfigDeviceInfoGetTargetName = 2;
    public const byte VcpInputSelect = 0x60;
    public const int CchDeviceName = 32;

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathInfoArray,
        ref uint numModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor,
        uint dwPhysicalMonitorArraySize,
        [Out] PhysicalMonitor[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PhysicalMonitor[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetVCPFeatureAndVCPFeatureReply(
        IntPtr hPhysicalMonitor,
        byte bVCPCode,
        out uint pvct,
        out uint pdwCurrentValue,
        out uint pdwMaximumValue);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool SetVCPFeature(IntPtr hPhysicalMonitor, byte bVCPCode, uint dwNewValue);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetCapabilitiesStringLength(IntPtr hPhysicalMonitor, out uint pdwCapabilitiesStringLengthInCharacters);

    [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern bool CapabilitiesRequestAndCapabilitiesReply(
        IntPtr hPhysicalMonitor,
        StringBuilder pszASCIICapabilitiesString,
        uint dwCapabilitiesStringLengthInCharacters);
}

[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MonitorInfoEx
{
    public int cbSize;
    public Rect rcMonitor;
    public Rect rcWork;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NativeMethods.CchDeviceName)]
    public string szDevice;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Luid
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigRational
{
    public uint Numerator;
    public uint Denominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfig2DRegion
{
    public uint cx;
    public uint cy;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigPathSourceInfo
{
    public Luid adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigPathTargetInfo
{
    public Luid adapterId;
    public uint id;
    public uint modeInfoIdx;
    public int outputTechnology;
    public uint rotation;
    public uint scaling;
    public DisplayConfigRational refreshRate;
    public int scanLineOrdering;
    [MarshalAs(UnmanagedType.Bool)]
    public bool targetAvailable;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigPathInfo
{
    public DisplayConfigPathSourceInfo sourceInfo;
    public DisplayConfigPathTargetInfo targetInfo;
    public uint flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigVideoSignalInfo
{
    public ulong pixelRate;
    public DisplayConfigRational hSyncFreq;
    public DisplayConfigRational vSyncFreq;
    public DisplayConfig2DRegion activeSize;
    public DisplayConfig2DRegion totalSize;
    public uint videoStandard;
    public int scanLineOrdering;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigTargetMode
{
    public DisplayConfigVideoSignalInfo targetVideoSignalInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PointL
{
    public int x;
    public int y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigSourceMode
{
    public uint width;
    public uint height;
    public uint pixelFormat;
    public PointL position;
}

[StructLayout(LayoutKind.Explicit)]
internal struct DisplayConfigModeInfoUnion
{
    [FieldOffset(0)] public DisplayConfigTargetMode targetMode;
    [FieldOffset(0)] public DisplayConfigSourceMode sourceMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigModeInfo
{
    public int infoType;
    public uint id;
    public Luid adapterId;
    public DisplayConfigModeInfoUnion modeInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigDeviceInfoHeader
{
    public int type;
    public uint size;
    public Luid adapterId;
    public uint id;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DisplayConfigSourceDeviceName
{
    public DisplayConfigDeviceInfoHeader header;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NativeMethods.CchDeviceName)]
    public string viewGdiDeviceName;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DisplayConfigTargetDeviceName
{
    public DisplayConfigDeviceInfoHeader header;
    public uint flags;
    public int outputTechnology;
    public ushort edidManufactureId;
    public ushort edidProductCodeId;
    public uint connectorInstance;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string monitorFriendlyDeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string monitorDevicePath;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct PhysicalMonitor
{
    public IntPtr hPhysicalMonitor;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string szPhysicalMonitorDescription;
}

internal static class OutputTechnology
{
    public const int Hdmi = 5;
    public const int Dvi = 4;
    public const int Hd15 = 0;
    public const int DisplayPortExternal = 10;
    public const int DisplayPortEmbedded = 11;
    public const int Internal = unchecked((int)0x80000000);
    public const int Miracast = 15;
    public const int DisplayPortUsbTunnel = 18;
}
