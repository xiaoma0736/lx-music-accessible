using System.IO;

namespace LXMusicAccessible.Data;

/// <summary>
/// Resolves the app's data directory (SQLite database + logs).
/// A pointer file in the default location records the current data directory,
/// so the directory can be resolved before the database itself is opened.
/// </summary>
public static class AppDataPaths
{
    public const string DefaultDirName = "lxmusic";
    private const string PointerFileName = "data_dir.txt";

    /// <summary>Default data directory under %LocalAppData%.</summary>
    public static string DefaultDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DefaultDirName);

    /// <summary>Effective data directory: pointer-file value if valid, else the default.</summary>
    public static string ResolveDataDir()
    {
        try
        {
            var pointer = Path.Combine(DefaultDataDir, PointerFileName);
            if (File.Exists(pointer))
            {
                var line = File.ReadAllText(pointer).Trim();
                if (!string.IsNullOrEmpty(line) && Path.IsPathRooted(line))
                    return line;
            }
        }
        catch { }
        return DefaultDataDir;
    }

    /// <summary>Record the current data directory in the default location.</summary>
    public static void WriteDataDirPointer(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(DefaultDataDir);
        File.WriteAllText(Path.Combine(DefaultDataDir, PointerFileName), dir.Trim());
    }
}
