namespace HdmiSwitch.Services;

internal static class InputSelect
{
    public static readonly byte Hdmi1 = 0x11;
    public static readonly byte Hdmi2 = 0x12;
    public static readonly byte Hdmi3 = 0x13;

    public static IReadOnlyList<byte> HdmiPreferenceOrder { get; } = [Hdmi1, Hdmi2, Hdmi3];

    public static bool IsHdmi(byte code) =>
        code is 0x11 or 0x12 or 0x13;

    public static string Name(byte code) => code switch
    {
        0x01 => "VGA-1",
        0x02 => "VGA-2",
        0x03 => "DVI-1",
        0x04 => "DVI-2",
        0x0F => "DisplayPort-1",
        0x10 => "DisplayPort-2",
        0x11 => "HDMI-1",
        0x12 => "HDMI-2",
        0x13 => "HDMI-3",
        _ => $"輸入 0x{code:X2}"
    };

    public static IReadOnlyList<byte> ParseAvailableInputs(string capabilities)
    {
        if (string.IsNullOrWhiteSpace(capabilities))
        {
            return [];
        }

        var vcpIndex = capabilities.IndexOf("vcp(", StringComparison.OrdinalIgnoreCase);
        var searchFrom = vcpIndex >= 0 ? vcpIndex : 0;
        var featureIndex = capabilities.IndexOf("60(", searchFrom, StringComparison.OrdinalIgnoreCase);
        if (featureIndex < 0)
        {
            return [];
        }

        var open = capabilities.IndexOf('(', featureIndex);
        var close = capabilities.IndexOf(')', open + 1);
        if (open < 0 || close < 0)
        {
            return [];
        }

        var inner = capabilities.Substring(open + 1, close - open - 1);
        var values = new List<byte>();
        foreach (var token in inner.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (byte.TryParse(token, System.Globalization.NumberStyles.HexNumber, null, out var code))
            {
                values.Add(code);
            }
        }

        return values;
    }
}
