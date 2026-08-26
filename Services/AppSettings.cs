using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HdmiSwitch.Services;

/// <summary>
/// 存在 %AppData%\HdmiSwitch\settings.json 的使用者設定。
/// 倒數關閉刻意不放進來：它是「現在啟動一次性計時」的運行期狀態，app 關掉就該消失。
/// </summary>
public sealed class AppSettings
{
    public List<HotkeyBinding> Hotkeys { get; set; } = [];

    public List<DailyPowerSchedule> DailySchedules { get; set; } = [];

    public List<InputLabelOverride> InputLabelOverrides { get; set; } = [];

    /// <summary>螢幕卡片的自訂顯示順序（存 OutputItem.Key）。使用者拖曳排序後寫入，新接上、清單裡沒有的螢幕排在最後。</summary>
    public List<string> ScreenOrder { get; set; } = [];
}

public sealed class HotkeyBinding
{
    public InputFamily Family { get; set; }

    /// <summary>MOD_ALT / MOD_CONTROL / MOD_SHIFT / MOD_WIN 的 OR 組合。</summary>
    public uint Modifiers { get; set; }

    /// <summary>Virtual-Key Code。0 代表未設定。</summary>
    public uint Key { get; set; }
}

public sealed class DailyPowerSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>null = 全部螢幕。</summary>
    public string? TargetGdiName { get; set; }

    /// <summary>當地時間 HH:mm。</summary>
    public TimeSpan Time { get; set; }

    public bool Enabled { get; set; } = true;
}

public sealed class InputLabelOverride
{
    /// <summary>螢幕的 EDID 友善名稱（抓不到時是 DDC Description）。同型號多台會共用，是已知限制。</summary>
    public string MonitorKey { get; set; } = "";

    public byte InputCode { get; set; }

    public string Label { get; set; } = "";
}

public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HdmiSwitch",
        "settings.json");

    /// <summary>讀不到或格式壞掉就視同空設定，不丟例外中斷啟動。</summary>
    public static AppSettings Load(out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            warning = $"設定檔讀取失敗，這次以空設定啟動：{ex.Message}";
            return new AppSettings();
        }
    }

    public static bool TrySave(AppSettings settings, out string? error)
    {
        error = null;
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
