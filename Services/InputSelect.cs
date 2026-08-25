using HdmiSwitch.Native;

namespace HdmiSwitch.Services;

public enum InputFamily
{
    Hdmi,
    DisplayPort,
    Vga,
    Dvi
}

public sealed record InputOption(string Label, InputFamily Family);

internal static class InputSelect
{
    public static readonly byte Hdmi1 = 0x11;
    public static readonly byte Hdmi2 = 0x12;
    public static readonly byte Hdmi3 = 0x13;

    public static IReadOnlyList<InputFamily> BatchOrder { get; } =
        [InputFamily.Hdmi, InputFamily.DisplayPort, InputFamily.Vga, InputFamily.Dvi];

    public static IReadOnlyList<byte> Preference(InputFamily family) => family switch
    {
        InputFamily.Hdmi => [0x11, 0x12, 0x13],
        InputFamily.DisplayPort => [0x0F, 0x10],
        InputFamily.Vga => [0x01, 0x02],
        InputFamily.Dvi => [0x03, 0x04],
        _ => []
    };

    public static InputFamily? FamilyFromTechnology(int technology) => technology switch
    {
        OutputTechnology.Hdmi => InputFamily.Hdmi,
        OutputTechnology.Dvi => InputFamily.Dvi,
        OutputTechnology.Hd15 => InputFamily.Vga,
        OutputTechnology.DisplayPortExternal or OutputTechnology.DisplayPortEmbedded
            or OutputTechnology.DisplayPortUsbTunnel => InputFamily.DisplayPort,
        _ => null
    };

    public static InputFamily? FamilyOf(byte code) => code switch
    {
        0x11 or 0x12 or 0x13 => InputFamily.Hdmi,
        0x0F or 0x10 => InputFamily.DisplayPort,
        0x01 or 0x02 => InputFamily.Vga,
        0x03 or 0x04 => InputFamily.Dvi,
        _ => null
    };

    public static string FamilyName(InputFamily family) => family switch
    {
        InputFamily.Hdmi => "HDMI",
        InputFamily.DisplayPort => "DisplayPort",
        InputFamily.Vga => "VGA",
        InputFamily.Dvi => "DVI",
        _ => family.ToString()
    };

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

internal readonly record struct InputRequest(byte? ExactCode, InputFamily? Family)
{
    public static InputRequest Exact(byte code) => new(code, null);

    public static InputRequest OfFamily(InputFamily family) => new(null, family);

    public string DisplayName => ExactCode is byte code
        ? InputSelect.Name(code)
        : Family is InputFamily family
            ? InputSelect.FamilyName(family)
            : "輸入";
}
