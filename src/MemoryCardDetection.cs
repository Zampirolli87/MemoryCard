using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class DetectedGame
{
    private static readonly HashSet<string> SaveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "save", "saves", "savegame", "savegames", "savedata", "save_data", "save files", "savegames_data"
    };

    public readonly string Title;
    public readonly string[] Folders;
    public readonly bool HasRegistry;
    public readonly bool RecoveryOnly;
    private readonly HashSet<string> liveFiles;
    private readonly HashSet<string> liveRegistry;

    public DetectedGame(string title, IEnumerable<string> files, IEnumerable<string> registry, bool recoveryOnly = false)
    {
        Title = title;
        RecoveryOnly = recoveryOnly;
        liveFiles = new HashSet<string>(files.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        liveRegistry = new HashSet<string>(registry.Select(NormalizeRegistry), StringComparer.OrdinalIgnoreCase);
        HasRegistry = liveRegistry.Count > 0;
        var directories = liveFiles.Select(Path.GetDirectoryName).Where(path => !String.IsNullOrWhiteSpace(path))
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var explicitFolders = directories.Select(NamedSaveAncestor).Where(path => path != null).ToArray();
        IEnumerable<string> candidates = explicitFolders.Length > 0 ? explicitFolders : directories;
        Folders = candidates.Where(IsGameFolder).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static string NormalizeRegistry(string path)
    {
        return path.Replace('/', '\\').TrimEnd('\\');
    }

    public bool MatchesTargets(RestorePreview preview)
    {
        if (preview.Registry.Any(path => !liveRegistry.Contains(NormalizeRegistry(path)))) return false;
        return preview.Files.Select(Path.GetFullPath).All(path => liveFiles.Contains(path)
            || (!RecoveryOnly && Folders.Any(folder => path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))));
    }

    public void Remember(string dataFolder)
    {
        RecoveryRecord.Store(dataFolder, Title, liveFiles, liveRegistry);
    }

    private static string NamedSaveAncestor(string path)
    {
        for (var folder = path; !String.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
            if (SaveNames.Contains(Path.GetFileName(folder))) return folder;
        return null;
    }

    private static bool IsGameFolder(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        if (full.Equals(Path.GetPathRoot(path).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
        var special = new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows };
        return !special.Select(Environment.GetFolderPath).Any(folder => full.Equals(folder, StringComparison.OrdinalIgnoreCase));
    }
}

// Paths come from local scans, independent from the contents of an imported ZIP.
internal sealed class RecoveryRecord
{
    public string[] Files { get; set; }
    public string[] Registry { get; set; }

    private static string RecordPath(string root, string title)
    {
        using (var hash = SHA256.Create())
        {
            var machineTitle = Environment.MachineName + "\n" + Environment.UserDomainName + "\\" + Environment.UserName
                + "\n" + title.ToUpperInvariant();
            var key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(machineTitle))).Replace("-", "");
            return Path.Combine(root, "Recovery", key + ".json");
        }
    }

    public static RecoveryRecord Load(string root, string title)
    {
        var path = RecordPath(root, title);
        if (!File.Exists(path)) return null;
        try
        {
            var record = new JavaScriptSerializer().Deserialize<RecoveryRecord>(File.ReadAllText(path));
            return record != null && record.Files != null && record.Registry != null ? record : null;
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public static void Store(string root, string title, IEnumerable<string> files, IEnumerable<string> registry)
    {
        var previous = Load(root, title);
        var record = new RecoveryRecord {
            Files = files.Concat(previous == null ? new string[0] : previous.Files).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Registry = registry.Concat(previous == null ? new string[0] : previous.Registry).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
        var path = RecordPath(root, title);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(record), Encoding.UTF8);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

