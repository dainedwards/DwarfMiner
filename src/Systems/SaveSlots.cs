using System;
using System.IO;

namespace DwarfMiner.Systems;

/// <summary>
/// Save-file slots: three independent profiles, each its own directory holding that
/// profile's meta.json (campaign/meta progress) and run.sav (suspended run). The title
/// screen picks the active slot; MetaSave and RunSave route their paths through it.
/// </summary>
public static class SaveSlots
{
    public const int Count = 3;

    /// <summary>The profile all saves read/write. Chosen on the title screen; defaults to
    /// slot 1 so headless test hooks (DM_AUTOSTART etc.) work without a menu.</summary>
    public static int Active = 1;

    private static string Root
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DwarfMiner");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Dir(int slot)
    {
        var dir = Path.Combine(Root, $"slot{slot}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Pre-slot saves lived at the root — adopt them as slot 1 so nobody's
    /// campaign vanishes behind the new menu.</summary>
    public static void MigrateLegacy()
    {
        try
        {
            var legacyMeta = Path.Combine(Root, "meta.json");
            var slot1Meta = Path.Combine(Dir(1), "meta.json");
            if (File.Exists(legacyMeta) && !File.Exists(slot1Meta))
                File.Move(legacyMeta, slot1Meta);
            var legacyRun = Path.Combine(Root, "run.sav");
            var slot1Run = Path.Combine(Dir(1), "run.sav");
            if (File.Exists(legacyRun) && !File.Exists(slot1Run))
                File.Move(legacyRun, slot1Run);
        }
        catch
        {
            // Best-effort — a failed migration just means the legacy files stay put.
        }
    }
}
