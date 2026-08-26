using System.Runtime.InteropServices;
using System.Windows.Input;
using HdmiSwitch.Native;

namespace HdmiSwitch.Services;

/// <summary>一次註冊的結果。失敗不靜默吞掉，交給呼叫端顯性處理。</summary>
public sealed record HotkeyRegistration(HotkeyBinding Binding, bool Success, string? Error);

/// <summary>
/// 包 RegisterHotKey / UnregisterHotKey。生命週期由 MainWindow 持有。
/// 每個 binding 對應一個唯一 id，WM_HOTKEY 進來時用 id 找回 InputFamily。
/// </summary>
internal sealed class HotkeyManager(IntPtr hwnd) : IDisposable
{
    private const int FirstId = 0xA100;

    private readonly Dictionary<int, InputFamily> _byId = [];
    private readonly List<int> _registered = [];

    /// <summary>先全部解除再重新註冊，回傳每一筆的成功／失敗。</summary>
    public IReadOnlyList<HotkeyRegistration> Apply(IReadOnlyList<HotkeyBinding> bindings)
    {
        UnregisterAll();

        var results = new List<HotkeyRegistration>();
        var id = FirstId;
        foreach (var binding in bindings)
        {
            if (binding.Key == 0)
            {
                continue;
            }

            var modifiers = binding.Modifiers | NativeMethods.ModNoRepeat;
            if (NativeMethods.RegisterHotKey(hwnd, id, modifiers, binding.Key))
            {
                _byId[id] = binding.Family;
                _registered.Add(id);
                results.Add(new HotkeyRegistration(binding, true, null));
            }
            else
            {
                var code = Marshal.GetLastWin32Error();
                results.Add(new HotkeyRegistration(
                    binding,
                    false,
                    code == NativeMethods.ErrorHotkeyAlreadyRegistered
                        ? "這組組合鍵已被 Windows 或其他程式佔用，請換一組。"
                        : $"註冊失敗（Win32 錯誤 {code}）。"));
            }

            id++;
        }

        return results;
    }

    public bool TryResolve(int id, out InputFamily family) => _byId.TryGetValue(id, out family);

    public void UnregisterAll()
    {
        foreach (var id in _registered)
        {
            NativeMethods.UnregisterHotKey(hwnd, id);
        }

        _registered.Clear();
        _byId.Clear();
    }

    public void Dispose() => UnregisterAll();
}

internal static class HotkeyText
{
    public static string Describe(uint modifiers, uint key)
    {
        if (key == 0)
        {
            return "未設定";
        }

        var parts = new List<string>();
        if ((modifiers & NativeMethods.ModControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & NativeMethods.ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & NativeMethods.ModShift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & NativeMethods.ModWin) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    private static string KeyName(uint virtualKey)
    {
        try
        {
            var key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
            return key == Key.None ? $"VK{virtualKey}" : key.ToString();
        }
        catch (Exception)
        {
            return $"VK{virtualKey}";
        }
    }
}
