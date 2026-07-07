using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DwarfMiner.Systems;

/// <summary>Persisted across runs. Drives meta-progress: starting bonuses, unlocks, totals.</summary>
public sealed class MetaSave
{
    public int TitansDefeated { get; set; }
    public int Escapes { get; set; }
    public int Deaths { get; set; }
    public int TotalOreMined { get; set; }
    public int DeepestDepth { get; set; }

    // Permanent unlocks earned by clearing runs.
    public int StartingPickaxePower { get; set; } = 1;
    public bool StartWithCannon { get; set; }

    // Star-map progression: how many planets on the PlanetDefs chain are selectable, and
    // which ones have been escaped by ship (drives the map's ESCAPED badges).
    public int PlanetsUnlocked { get; set; } = 1;
    public List<string> PlanetsEscaped { get; set; } = new();

    private static string SavePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DwarfMiner");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "meta.json");
        }
    }

    public static MetaSave Load()
    {
        try
        {
            if (!File.Exists(SavePath)) return new MetaSave();
            var json = File.ReadAllText(SavePath);
            return JsonSerializer.Deserialize<MetaSave>(json) ?? new MetaSave();
        }
        catch
        {
            return new MetaSave();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SavePath, json);
        }
        catch { /* best-effort persistence; don't crash the game on disk errors */ }
    }
}
