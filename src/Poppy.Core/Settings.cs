using System.Text.Json;
using System.Text.Json.Serialization;

namespace Poppy.Core;

/// <summary>User settings, stored as JSON in %AppData%\Poppy\settings.json.</summary>
public sealed class AppSettings
{
    public const int VK_TAB = 0x09, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_CAPITAL = 0x14, VK_SPACE = 0x20;

    public int ConvertKey { get; set; } = VK_SHIFT;
    public int ToolsKey { get; set; } = VK_CONTROL;
    public int ActionsKey { get; set; } = VK_MENU;

    /// <summary>"auto", "en" or "tr".</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Empty = save next to the original file.</summary>
    public string OutputFolder { get; set; } = "";

    public bool RevealWhenDone { get; set; }
    public bool PlaySound { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool FirstRunDone { get; set; }

    /// <summary>1.0 = default ring size.</summary>
    public double RingScale { get; set; } = 1.0;

    public RingMode? ModeForKey(int vk)
    {
        vk = NormalizeKey(vk);
        if (vk == ConvertKey) return RingMode.Convert;
        if (vk == ToolsKey) return RingMode.Tools;
        if (vk == ActionsKey) return RingMode.Actions;
        return null;
    }

    /// <summary>Left/right variants (LSHIFT, RCONTROL...) count as the generic key.</summary>
    public static int NormalizeKey(int vk) => vk switch
    {
        0xA0 or 0xA1 => VK_SHIFT,
        0xA2 or 0xA3 => VK_CONTROL,
        0xA4 or 0xA5 => VK_MENU,
        _ => vk,
    };

    public static readonly IReadOnlyList<(int Vk, string Name)> KeyChoices =
    [
        (VK_SHIFT, "Shift"), (VK_CONTROL, "Ctrl"), (VK_MENU, "Alt"), (VK_SPACE, "Space"),
        (VK_TAB, "Tab"), (VK_CAPITAL, "Caps Lock"),
        (0x70, "F1"), (0x71, "F2"), (0x72, "F3"), (0x73, "F4"),
    ];

    public static string KeyName(int vk) => KeyChoices.FirstOrDefault(k => k.Vk == vk).Name ?? $"0x{vk:X2}";

    // ---------- Storage ----------

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static string Folder { get; set; } =
        Environment.GetEnvironmentVariable("POPPY_SETTINGS_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Poppy");

    static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public void Apply()
    {
        L.Language = Language is "en" or "tr" ? Language
            : System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en";
        Ops.OutputPaths.OverrideDir = OutputFolder.Length > 0 ? OutputFolder : null;
    }
}
