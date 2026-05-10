using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskbarNicifier.App.Settings;

public sealed class OverlaySettingsService
{
    internal const string PrimaryMonitorKey = "primary";

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
            Normalize(s);
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
        Normalize(s);
        return s;
    }

    private static void Normalize(OverlaySettings s)
    {
        s.Layout ??= new LayoutSettings();
        s.Integrated ??= new IntegratedOverlaySettings();
        s.IntegratedByMonitor ??= new Dictionary<string, IntegratedOverlaySettings>(StringComparer.OrdinalIgnoreCase);

        // Migration: if legacy Integrated bounds exist but per-monitor bounds are missing,
        // seed the primary key from the legacy values.
        if (s.IntegratedByMonitor.Count == 0 && HasAnyIntegratedBounds(s.Integrated))
            s.IntegratedByMonitor[PrimaryMonitorKey] = CloneIntegratedBounds(s.Integrated);

        GroupingSettingsBootstrap.EnsureGroupingContainer(s);
        GroupingSettingsBootstrap.EnsureDefaultGroups(s.Grouping);
        GroupingSettingsBootstrap.NormalizeGroupAlignments(s.Grouping);
        s.Grouping.LastNonHiddenGroupByAppKey ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        NormalizePinnedAppsDictionary(s.Grouping);

        if (string.IsNullOrWhiteSpace(s.Layout.StripAccentColor))
            s.Layout.StripAccentColor = "#FF000000";
    }

    private static bool HasAnyIntegratedBounds(IntegratedOverlaySettings s)
        => s.Left is not null || s.Top is not null || s.Width is not null || s.Height is not null;

    private static IntegratedOverlaySettings CloneIntegratedBounds(IntegratedOverlaySettings s)
        => new()
        {
            Left = s.Left,
            Top = s.Top,
            Width = s.Width,
            Height = s.Height,
        };

    private static void NormalizePinnedAppsDictionary(GroupingSettings g)
    {
        if (g.PinnedAppsByKey is null || g.PinnedAppsByKey.Count == 0)
        {
            g.PinnedAppsByKey = new Dictionary<string, PinnedAppSettings>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        var merged = new Dictionary<string, PinnedAppSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in g.PinnedAppsByKey)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null)
                continue;
            kv.Value.AppKey = kv.Value.AppKey.Length > 0 ? kv.Value.AppKey : kv.Key;
            merged[kv.Key] = kv.Value;
        }

        g.PinnedAppsByKey = merged;
    }

    public void Save(OverlaySettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            Normalize(settings);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort persistence; ignore failures.
        }
    }
}

