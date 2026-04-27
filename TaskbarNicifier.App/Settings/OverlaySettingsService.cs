using System;
using System.IO;
using System.Text.Json;

namespace TaskbarNicifier.App.Settings;

public sealed class OverlaySettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public string SettingsPath { get; }

    public OverlaySettingsService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarNicifier");
        SettingsPath = Path.Combine(dir, "settings.json");
    }

    public OverlaySettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new OverlaySettings();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<OverlaySettings>(json, JsonOptions) ?? new OverlaySettings();
        }
        catch
        {
            return new OverlaySettings();
        }
    }

    public void Save(OverlaySettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort persistence; ignore failures.
        }
    }
}

