using System.Runtime.InteropServices;
using System.Text;

namespace Dictee;

internal readonly record struct KeyStroke(int Vk, bool Up, int Scan = 0, bool Extended = false);

internal readonly record struct Hotkey(int Vk, int Scan, bool Extended)
{
    public KeyStroke Stroke(bool up) => new(Vk, up, Scan, Extended);

    public string DisplayName
    {
        get
        {
            var buffer = new StringBuilder(64);
            int lParam = (Scan << 16) | (Extended ? 1 << 24 : 0);
            return Native.GetKeyNameText(lParam, buffer, buffer.Capacity) > 0
                ? buffer.ToString()
                : ((Keys)Vk).ToString();
        }
    }
}

internal static class InputSender
{
    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_EXTENDEDKEY = 0x1;
    const uint KEYEVENTF_KEYUP = 0x2;

    public static void Send(params ReadOnlySpan<KeyStroke> strokes)
    {
        var inputs = new Native.INPUT[strokes.Length];
        for (int i = 0; i < strokes.Length; i++)
        {
            var s = strokes[i];
            inputs[i] = new Native.INPUT
            {
                type = INPUT_KEYBOARD,
                u = new Native.InputUnion
                {
                    ki = new Native.KEYBDINPUT
                    {
                        wVk = (ushort)s.Vk,
                        wScan = (ushort)(s.Scan != 0 ? (uint)s.Scan : Native.MapVirtualKey((uint)s.Vk, 0)),
                        dwFlags = (s.Up ? KEYEVENTF_KEYUP : 0) | (s.Extended ? KEYEVENTF_EXTENDEDKEY : 0),
                    },
                },
            };
        }
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }
}
