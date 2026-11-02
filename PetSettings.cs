using System.Text.Json;
using System.IO;

namespace OtterDesktop;

internal sealed class PetSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Scale { get; set; } = 1.0;
    public double AnimationSpeed { get; set; } = 1.0;
    public bool AlwaysOnTop { get; set; } = true;
    public bool Paused { get; set; }
    public bool HideFromScreenCapture { get; set; } = true;
    public bool QuietDuringTeamsMeetings { get; set; } = true;
    public bool ManualMeetingMode { get; set; }
    public bool QuietDuringFullscreenApps { get; set; } = true;
    public bool ReactToTyping { get; set; } = true;

    private static string FolderPath => AppContext.GetData("OtterDesktop.SettingsDirectory") as string ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OtterDesktop");

    private static string FilePath => Path.Combine(FolderPath, "settings.json");

    public static PetSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<PetSettings>(File.ReadAllText(FilePath)) ?? new PetSettings();
            }
        }
        catch
        {
            // Corrupt or locked settings should never stop the pet from launching.
        }

        return new PetSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Some managed work computers block profile writes; the pet still works without persistence.
        }
    }
}
