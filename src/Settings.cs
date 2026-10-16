using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Dictee;

internal sealed class Settings
{
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dictee");
    static readonly string FilePath = Path.Combine(Folder, "settings.json");

    public int HotkeyVk { get; set; } = 0x14; // Caps Lock
    public int HotkeyScan { get; set; } = 0x3A;
    public bool HotkeyExtended { get; set; }
    public string Language { get; set; } = "fr";

    [JsonIgnore]
    public Hotkey Hotkey
    {
        get => new(HotkeyVk, HotkeyScan, HotkeyExtended);
        set { HotkeyVk = value.Vk; HotkeyScan = value.Scan; HotkeyExtended = value.Extended; }
    }

    public static bool Exists => File.Exists(FilePath);

    public static Settings Load()
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.Settings) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJson.Default.Settings));
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Settings))]
internal sealed partial class SettingsJson : JsonSerializerContext;

internal static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Dictee";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(ValueName, false);
        }
    }
}

internal static class Log
{
    static readonly string FilePath = Path.Combine(Settings.Folder, "dictee.log");

    public static void Error(Exception? ex) => Write(ex?.ToString() ?? "Unknown error");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            var file = new FileInfo(FilePath);
            if (file.Exists && file.Length > 512_000) file.Delete();
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
