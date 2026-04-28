using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskbarNicifier.App.Settings;

public sealed class OverlaySettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
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
                return CreateDefaultSettings();

            var json = File.ReadAllText(SettingsPath);
            var s = JsonSerializer.Deserialize<OverlaySettings>(json, JsonOptions) ?? new OverlaySettings();
            NormalizeGrouping(s);
            return s;
        }
        catch
        {
            return CreateDefaultSettings();
        }
    }

    private static OverlaySettings CreateDefaultSettings()
    {
        var s = new OverlaySettings();
        NormalizeGrouping(s);
        return s;
    }

    private static void NormalizeGrouping(OverlaySettings s)
    {
        GroupingSettingsBootstrap.EnsureGroupingContainer(s);
        GroupingSettingsBootstrap.EnsureDefaultGroups(s.Grouping);
        GroupingSettingsBootstrap.NormalizeGroupAlignments(s.Grouping);
        s.Grouping.LastNonHiddenGroupByAppKey ??= new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

