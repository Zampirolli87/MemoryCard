using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("MemoryCard")]
[assembly: AssemblyProduct("MemoryCard")]
[assembly: AssemblyCompany("iZampirolli")]
[assembly: AssemblyDescription("Backup manual de saves")]
[assembly: AssemblyVersion("1.3.0.0")]

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool firstInstance;
        using (var instance = new Mutex(true, @"Local\BackupHydraManual", out firstInstance))
        {
            if (!firstInstance)
            {
                MessageBox.Show("O MemoryCard já está aberto.", "MemoryCard");
                return;
            }
            using (var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
            using (var window = new BackupWindow(CreateStorage()) { Icon = icon })
                Application.Run(window);
        }
    }

    internal static SaveBackup CreateStorage()
    {
#if PORTABLE
        var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MemoryCard-Dados");
        return new SaveBackup(root);
#else
        return new SaveBackup(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BackupHydraManual"));
#endif
    }
}

internal sealed class SaveBackup
{
    private const string EngineVersion = "0.31.0";
    private readonly string dataFolder;
    private readonly string engineFolder;
    private readonly string configFolder;
    private readonly string backupFolderOverride;
    private bool prepared;
    public string WorkingFolder { get { return dataFolder; } }
    public string RecoveryWarning { get; private set; }

    public SaveBackup(string dataFolder, string backupFolderOverride = null)
    {
        this.dataFolder = dataFolder;
        engineFolder = Path.Combine(dataFolder, "Ludusavi-" + EngineVersion);
        configFolder = Path.Combine(dataFolder, "Config");
        this.backupFolderOverride = backupFolderOverride;
    }

    public string BackupFolder
    {
        get { return backupFolderOverride ?? Path.Combine(GetDownloadsFolder(), "MemoryCard"); }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);

    private static string GetDownloadsFolder()
    {
        var folderId = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        IntPtr path;
        // Use the Downloads location selected in Windows, even if it has been moved.
        const uint DontVerify = 0x4000;
        int result = SHGetKnownFolderPath(ref folderId, DontVerify, IntPtr.Zero, out path);
        try
        {
            Marshal.ThrowExceptionForHR(result);
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            if (path != IntPtr.Zero) Marshal.FreeCoTaskMem(path);
        }
    }

    private string EnginePath { get { return Path.Combine(engineFolder, "ludusavi.exe"); } }

    public void Prepare()
    {
        if (prepared && File.Exists(EnginePath)) return;
        if (File.Exists(EnginePath))
        {
            prepared = BackupMetadata.HashFile(EnginePath) == "5192FD1EF31B5718CDB787D3F41ABDCCA8682BA4CBAD31812D44CA6630EC2C0C";
            if (prepared) return;
        }

        Directory.CreateDirectory(engineFolder);
        var assembly = Assembly.GetExecutingAssembly();
        var temp = EnginePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var license = assembly.GetManifestResourceStream("LudusaviLicense"))
            using (var destination = File.Create(Path.Combine(engineFolder, "LICENSE.txt")))
                license.CopyTo(destination);
            using (var stream = assembly.GetManifestResourceStream("LudusaviZip"))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                archive.GetEntry("ludusavi.exe").ExtractToFile(temp, true);
            if (File.Exists(EnginePath)) File.Replace(temp, EnginePath, null);
            else File.Move(temp, EnginePath);
            prepared = true;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public List<DetectedGame> FindGames()
    {
        Prepare();
        var games = ReadGames(Run("backup", "--preview", "--api", "--no-cloud-sync"));
        var detected = games.Where(game => HasSaveData(game.Value))
            .Select(game => new DetectedGame(game.Key, GetTargets((Dictionary<string, object>)game.Value, "files"),
                GetTargets((Dictionary<string, object>)game.Value, "registry"))).ToList();
        RecoveryWarning = "";
        try { AddRecoveryGames(detected); }
        catch (IOException) { RecoveryWarning = "Não foi possível consultar as cópias locais para recuperação."; }
        catch (UnauthorizedAccessException) { RecoveryWarning = "Acesso à pasta de backups indisponível."; }
        return detected.OrderBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void AddRecoveryGames(List<DetectedGame> detected)
    {
        foreach (var title in ListBackups().Select(entry => entry.Game).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var record = RecoveryRecord.Load(dataFolder, title);
            var live = detected.FirstOrDefault(game => String.Equals(game.Title, title, StringComparison.OrdinalIgnoreCase));
            if (live == null && record != null)
                detected.Add(new DetectedGame(title, record.Files, record.Registry, true));
            else if (live != null && record == null)
                live.Remember(dataFolder);
        }
    }

    public void ConfigureSaves()
    {
        Prepare();
        using (var process = Process.Start(NewProcess(false, "gui")))
            process.WaitForExit();
    }

    public string CreateBackup(string title, RestorePreview beforeRestore = null)
    {
        Prepare();
        var backupFolder = BackupFolder;
        Directory.CreateDirectory(backupFolder);
        var staging = Path.Combine(dataFolder, "Temporario-" + Guid.NewGuid().ToString("N"));
        var safeTitle = String.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        safeTitle = new String(safeTitle.Take(60).ToArray()).Trim().TrimEnd('.');
        var created = DateTimeOffset.Now;
        var name = safeTitle + (beforeRestore == null ? "" : " - Segurança") + " - "
            + created.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture)
            + " - " + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip";
        var archivePath = Path.Combine(backupFolder, name);
        var temporaryArchive = archivePath + ".tmp";
        try
        {
            var games = ReadGames(Run("backup", "--path", staging, "--force", "--api", "--no-cloud-sync",
                "--format", "zip", "--", title));
            if (!games.ContainsKey(title) || !HasSaveData(games[title]) || !Directory.Exists(staging))
                throw new InvalidOperationException("Nenhum arquivo de save foi encontrado para este jogo.");

            if (beforeRestore != null)
            {
                var game = (Dictionary<string, object>)games[title];
                var files = new HashSet<string>(GetTargets(game, "files").Select(NormalizeTarget), StringComparer.OrdinalIgnoreCase);
                var registry = new HashSet<string>(GetTargets(game, "registry").Select(NormalizeTarget), StringComparer.OrdinalIgnoreCase);
                if (beforeRestore.ExistingFiles().Any(path => !files.Contains(NormalizeTarget(path)))
                    || beforeRestore.ExistingRegistry().Any(path => !registry.Contains(NormalizeTarget(path))))
                    throw new IOException("A cópia de segurança não cobriu todos os dados atuais que seriam sobrescritos.");
            }

            var info = new BackupMetadata { Game = title, Created = created.ToString("o"), Safety = beforeRestore != null };
            var gameDir = Directory.EnumerateDirectories(staging).Single();
            info.MappingSha256 = BackupMetadata.HashFile(Path.Combine(gameDir, "mapping.yaml"));
            info.PayloadSha256 = BackupMetadata.HashFile(Directory.EnumerateFiles(gameDir, "backup-*.zip").Single());
            File.WriteAllText(Path.Combine(staging, BackupHistory.MetadataFile),
                new JavaScriptSerializer().Serialize(info), Encoding.UTF8);
            ZipFile.CreateFromDirectory(staging, temporaryArchive, CompressionLevel.NoCompression, false, Encoding.UTF8);
            var saved = (Dictionary<string, object>)games[title];
            new DetectedGame(title, GetTargets(saved, "files"), GetTargets(saved, "registry")).Remember(dataFolder);
            File.Move(temporaryArchive, archivePath);
            return archivePath;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            if (File.Exists(temporaryArchive)) File.Delete(temporaryArchive);
        }
    }

    private static string NormalizeTarget(string target)
    {
        return target.Replace('/', '\\').TrimEnd('\\');
    }

    public IList<BackupEntry> ListBackups()
    {
        return BackupHistory.Read(new[] { BackupFolder, Path.Combine(dataFolder, "Backups") });
    }

    public string SaveCurrentProgress(RestorePreview preview)
    {
        if (!preview.ExistingRegistry().Any() && !preview.ExistingFiles().Any()) return null;
        return CreateBackup(preview.Title, preview);
    }

    public RestorePreview InspectBackup(string zipPath)
    {
        Prepare();
        var staging = Path.Combine(dataFolder, "Restauracao-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var entries = archive.Entries.Where(entry => entry.Name.Length > 0
                    && entry.FullName != BackupHistory.MetadataFile).ToArray();
                var mapping = entries.SingleOrDefault(entry => entry.Name == "mapping.yaml");
                var payload = entries.SingleOrDefault(entry => entry.Name.StartsWith("backup-", StringComparison.Ordinal)
                    && entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                if (entries.Length != 2 || mapping == null || payload == null)
                    throw new InvalidDataException("Selecione um ZIP criado pelo MemoryCard.");

                var mappingParts = ExternalSave.SafeRelative(mapping.FullName).Split('\\');
                var payloadParts = ExternalSave.SafeRelative(payload.FullName).Split('\\');
                if (mappingParts.Length != 2 || payloadParts.Length != 2 || mappingParts[0] != payloadParts[0])
                    throw new InvalidDataException("A estrutura do ZIP do MemoryCard é inválida.");

                var gameFolder = Path.Combine(staging, mappingParts[0]);
                Directory.CreateDirectory(gameFolder);
                var mappingFile = Path.Combine(gameFolder, "mapping.yaml");
                var payloadFile = Path.Combine(gameFolder, payloadParts[1]);
                ExternalSave.ExtractEntry(mapping, mappingFile);
                ExternalSave.ExtractEntry(payload, payloadFile);
                var metadata = archive.GetEntry(BackupHistory.MetadataFile);
                if (metadata != null)
                {
                    if (metadata.Length > 8192) throw new InvalidDataException("Os metadados do backup são inválidos.");
                    var metadataPath = Path.Combine(staging, BackupHistory.MetadataFile);
                    ExternalSave.ExtractEntry(metadata, metadataPath);
                    var info = new JavaScriptSerializer().Deserialize<BackupMetadata>(File.ReadAllText(metadataPath));
                    if (info == null || (info.PayloadSha256 != null || info.MappingSha256 != null)
                        && (BackupMetadata.HashFile(mappingFile) != info.MappingSha256
                            || BackupMetadata.HashFile(payloadFile) != info.PayloadSha256))
                        throw new InvalidDataException("A verificação de integridade falhou. Escolha outra cópia do backup.");
                }
            }

            var games = ReadGames(Run("restore", "--preview", "--api", "--path", staging, "--no-cloud-sync"));
            if (games.Count != 1 || !HasSaveData(games.Single().Value))
                throw new InvalidDataException("O ZIP não contém saves que o MemoryCard consiga restaurar.");

            var game = games.Single();
            var data = (Dictionary<string, object>)game.Value;
            return new RestorePreview(staging, game.Key, GetTargets(data, "files").ToArray(),
                GetTargets(data, "registry").ToArray());
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            throw;
        }
    }

    public void RestoreBackup(RestorePreview preview)
    {
        var games = ReadGames(Run("restore", "--force", "--api", "--path", preview.StagingFolder,
            "--no-cloud-sync", "--", preview.Title));
        if (!games.ContainsKey(preview.Title) || !HasSaveData(games[preview.Title]))
            throw new InvalidOperationException("O MemoryCard não conseguiu restaurar os dados deste jogo.");
    }

    private static IEnumerable<string> GetTargets(Dictionary<string, object> game, string property)
    {
        object entries;
        if (!game.TryGetValue(property, out entries)) yield break;
        var map = entries as Dictionary<string, object>;
        if (map == null) yield break;
        foreach (var entry in map)
        {
            var details = entry.Value as Dictionary<string, object>;
            if (details == null || IsTrue(details, "ignored") || IsTrue(details, "failed")) continue;
            // The map key is the live Windows path for both backup and restore.
            yield return entry.Key;
        }
    }

    private static bool HasSaveData(object value)
    {
        var game = value as Dictionary<string, object>;
        if (game == null) return false;

        foreach (var property in new[] { "files", "registry" })
        {
            object entries;
            if (!game.TryGetValue(property, out entries)) continue;
            var map = entries as Dictionary<string, object>;
            if (map != null && map.Values.OfType<Dictionary<string, object>>().Any(
                item => !IsTrue(item, "ignored") && !IsTrue(item, "failed"))) return true;
        }
        return false;
    }

    private static bool IsTrue(Dictionary<string, object> values, string key)
    {
        object value;
        return values.TryGetValue(key, out value) && Equals(value, true);
    }

    private static Dictionary<string, object> ReadGames(string json)
    {
        var parser = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        var report = parser.DeserializeObject(json) as Dictionary<string, object>;
        object value;
        if (report != null && report.TryGetValue("errors", out value) && value is Dictionary<string, object>
            && IsTrue((Dictionary<string, object>)value, "someGamesFailed"))
            throw new IOException("O Ludusavi informou uma falha parcial ao processar os saves.");
        if (report != null && report.TryGetValue("games", out value) && value is Dictionary<string, object>)
            return (Dictionary<string, object>)value;
        throw new IOException("O Ludusavi não retornou uma lista de saves válida.");
    }

    private ProcessStartInfo NewProcess(bool hidden, params string[] args)
    {
        return new ProcessStartInfo
        {
            FileName = EnginePath,
            Arguments = String.Join(" ", new[] { "--config", configFolder }.Concat(args).Select(QuoteArgument)),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = hidden,
            RedirectStandardError = hidden,
            StandardOutputEncoding = hidden ? Encoding.UTF8 : null,
            StandardErrorEncoding = hidden ? Encoding.UTF8 : null,
        };
    }

    private string Run(params string[] args)
    {
        using (var process = Process.Start(NewProcess(true, args)))
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (args.Contains("--preview") && !process.WaitForExit(5 * 60 * 1000))
            {
                process.Kill();
                process.WaitForExit();
                throw new IOException("O Ludusavi levou mais de cinco minutos. Tente novamente.");
            }
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                var error = stderr.GetAwaiter().GetResult().Trim();
                if (error.Length > 800) error = error.Substring(0, 800);
                throw new IOException(error.Length > 0 ? error : "O Ludusavi não conseguiu concluir a operação.");
            }
            return stdout.GetAwaiter().GetResult();
        }
    }

    private static string QuoteArgument(string value)
    {
        var quoted = new StringBuilder().Append('"');
        int slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }
            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }
}

internal sealed class RestorePreview : IDisposable
{
    public RestorePreview(string stagingFolder, string title, string[] files, string[] registry)
    {
        StagingFolder = stagingFolder;
        Title = title;
        Files = files;
        Registry = registry;
    }

    public string StagingFolder { get; private set; }
    public string Title { get; private set; }
    public string[] Files { get; private set; }
    public string[] Registry { get; private set; }
    public string[] Targets { get { return Files.Concat(Registry).ToArray(); } }

    public IEnumerable<string> ExistingFiles()
    {
        foreach (string path in Files)
        {
            try { File.GetAttributes(path); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            yield return path;
        }
    }

    public IEnumerable<string> ExistingRegistry()
    {
        return Registry.Where(path => Microsoft.Win32.Registry.GetValue(path.Replace('/', '\\'), "",
            String.Empty) != null);
    }

    public void Dispose()
    {
        if (Directory.Exists(StagingFolder)) Directory.Delete(StagingFolder, true);
    }
}

internal sealed class BackupWindow : Form
{
    private readonly SaveBackup backup;
    private readonly TextBox search;
    private readonly ListBox games;
    private readonly Label status;
    private readonly Label selectedGame;
    private readonly Label locationLabel;
    private readonly TextBox singleFolder;
    private readonly ComboBox multipleFolders;
    private readonly Button openSaveFolder;
    private readonly Label gameCount;
    private readonly Label emptyList;
    private readonly ActivityTrack activity;
    private readonly Button saveButton;
    private readonly Button restoreButton;
    private readonly Button historyButton;
    private readonly Button importButton;
    private readonly Button scanButton;
    private readonly Button customButton;
    private Dictionary<string, DetectedGame> allGames = new Dictionary<string, DetectedGame>(StringComparer.OrdinalIgnoreCase);
    private DetectedGame displayedGame;
    private bool changingFolders;
    private bool busy;

    public BackupWindow(SaveBackup backup)
    {
        this.backup = backup;
        Text = "MemoryCard";
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10F);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        ClientSize = new Size(980, 690);
        MinimumSize = new Size(800, 694);
        DoubleBuffered = true;

        var layout = Grid(Theme.Background, new Padding(26, 24, 26, 14), 108, 0, 46);
        Controls.Add(layout);
        var banner = new BrandBanner { Dock = DockStyle.Fill, Margin = Padding.Empty };
        layout.Controls.Add(banner, 0, 0);

        var columns = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Margin = new Padding(0, 16, 0, 0), ColumnCount = 2, RowCount = 1
        };
        columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        layout.Controls.Add(columns, 0, 1);

        var library = new SurfacePanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0), Padding = new Padding(9) };
        var libraryGrid = Grid(Theme.Surface, new Padding(8, 6, 8, 6), 36, 49, 0, 29, 36);
        library.Controls.Add(libraryGrid);
        columns.Controls.Add(library, 0, 0);
        libraryGrid.Controls.Add(TextLabel("Jogos encontrados", Theme.Text, 16, true), 0, 0);

        var searchRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 3, 0, 9)
        };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        searchRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var searchField = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(9, 8, 9, 4),
            Margin = new Padding(0, 0, 9, 0), BackColor = Theme.Field };
        search = new TextBox { Dock = DockStyle.Fill, BackColor = Theme.Field, ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None, AccessibleName = "Buscar jogo" };
        search.TextChanged += (sender, args) => ApplyFilter();
        search.HandleCreated += (sender, args) => Theme.SearchHint(search, "Buscar jogo");
        searchField.Controls.Add(search);
        searchRow.Controls.Add(searchField, 0, 0);
        scanButton = new AnimatedButton("Atualizar lista", false);
        scanButton.Click += async (sender, args) => await Scan();
        searchRow.Controls.Add(scanButton, 1, 0);
        libraryGrid.Controls.Add(searchRow, 0, 1);

        var listPanel = new SurfacePanel { Dock = DockStyle.Fill, BackColor = Theme.Field,
            Padding = new Padding(5), Margin = Padding.Empty };
        games = new GameList();
        games.SelectedIndexChanged += (sender, args) => UpdateButtons();
        emptyList = TextLabel("Procurando saves…", Theme.Muted);
        emptyList.TextAlign = ContentAlignment.MiddleCenter;
        emptyList.BackColor = Theme.Field;
        listPanel.Controls.Add(games);
        listPanel.Controls.Add(emptyList);
        emptyList.BringToFront();
        libraryGrid.Controls.Add(listPanel, 0, 2);
        gameCount = TextLabel("", Theme.Muted, 9);
        libraryGrid.Controls.Add(gameCount, 0, 3);
        customButton = new AnimatedButton("Meu jogo não aparece", false) { Margin = new Padding(0, 2, 0, 0) };
        customButton.Click += async (sender, args) => await Configure();
        libraryGrid.Controls.Add(customButton, 0, 4);

        var details = new SurfacePanel { Dock = DockStyle.Fill, Margin = new Padding(8, 0, 0, 0), Padding = new Padding(9) };
        var detailGrid = Grid(Theme.Surface, new Padding(9, 6, 9, 5), 34, 48, 38, 75, 44, 44, 44, 44, 44, 0, 34);
        details.Controls.Add(detailGrid);
        columns.Controls.Add(details, 1, 0);
        detailGrid.Controls.Add(TextLabel("Gerenciar saves", Theme.Text, 16, true), 0, 0);
        selectedGame = TextLabel("Selecione um jogo", Theme.Accent, 15, true);
        selectedGame.AutoEllipsis = true;
        detailGrid.Controls.Add(selectedGame, 0, 1);
        locationLabel = TextLabel("", Theme.Muted, 9);
        detailGrid.Controls.Add(locationLabel, 0, 2);
        var location = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6) };
        singleFolder = new TextBox { ReadOnly = true, Multiline = true, BackColor = Theme.Field, ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill, AccessibleName = "Pasta de saves encontrada" };
        multipleFolders = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top,
            BackColor = Theme.Field, ForeColor = Theme.Text, DropDownWidth = 620, AccessibleName = "Escolher pasta de saves" };
        multipleFolders.SelectedIndexChanged += (sender, args) => { if (!changingFolders) UpdateButtons(); };
        location.Controls.Add(singleFolder);
        location.Controls.Add(multipleFolders);
        detailGrid.Controls.Add(location, 0, 3);

        saveButton = new AnimatedButton("Fazer backup", true) { Margin = new Padding(0, 3, 0, 4) };
        saveButton.Click += async (sender, args) => await BackUpGame();
        detailGrid.Controls.Add(saveButton, 0, 4);
        restoreButton = new AnimatedButton("Restaurar backup", false) { Margin = new Padding(0, 3, 0, 4) };
        restoreButton.Click += async (sender, args) => await RestoreGame();
        detailGrid.Controls.Add(restoreButton, 0, 5);
        historyButton = new AnimatedButton("Histórico de backups", false) { Margin = new Padding(0, 3, 0, 4) };
        historyButton.Click += async (sender, args) => await ShowHistory();
        detailGrid.Controls.Add(historyButton, 0, 6);
        importButton = new AnimatedButton("Importar save", false) { Margin = new Padding(0, 3, 0, 4) };
        importButton.Click += async (sender, args) => await ImportExternal();
        detailGrid.Controls.Add(importButton, 0, 7);
        openSaveFolder = new AnimatedButton("Abrir pasta de saves", false) { Margin = new Padding(0, 3, 0, 4) };
        openSaveFolder.Click += (sender, args) => OpenGameFolder();
        detailGrid.Controls.Add(openSaveFolder, 0, 8);
        var backups = new LinkLabel { Text = "Abrir pasta de backups", LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.Text, VisitedLinkColor = Theme.Accent, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
        backups.LinkClicked += (sender, args) => OpenBackups();
        detailGrid.Controls.Add(backups, 0, 10);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0),
            ColumnCount = 2, RowCount = 2 };
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        status = TextLabel("", Theme.Muted, 9);
        status.AutoEllipsis = true;
        status.TextChanged += (sender, args) => status.ForeColor = status.Text.StartsWith("Backup salvo")
            || status.Text.StartsWith("Save restaurado")
            ? Theme.Success : Theme.Muted;
        footer.Controls.Add(status, 0, 0);
        var credit = TextLabel("Desenvolvido por iZampirolli", Theme.Accent, 9, true);
        credit.TextAlign = ContentAlignment.MiddleRight;
        footer.Controls.Add(credit, 1, 0);
        activity = new ActivityTrack();
        footer.Controls.Add(activity, 0, 1);
        layout.Controls.Add(footer, 0, 2);

        HandleCreated += (sender, args) => Theme.DarkTitleBar(this);
        Shown += async (sender, args) =>
        {
            banner.Reveal();
            await Scan();
        };
        FormClosing += (sender, args) =>
        {
            if (busy && args.CloseReason == CloseReason.UserClosing) args.Cancel = true;
        };
        UpdateButtons();
    }

    private static TableLayoutPanel Grid(Color color, Padding padding, params int[] rows)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = color, Padding = padding,
            Margin = Padding.Empty, ColumnCount = 1, RowCount = rows.Length };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in rows)
            grid.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
        return grid;
    }

    private static Label TextLabel(string text, Color color, float size = 10, bool bold = false)
    {
        return new Label { Text = text, ForeColor = color, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty,
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular) };
    }

    private void SetBusy(bool value, string message)
    {
        busy = value;
        activity.SetActive(value);
        UseWaitCursor = value;
        RefreshListState();
        if (message != null) status.Text = message;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var game = CurrentGame;
        if (game != displayedGame)
        {
            var previous = CurrentFolder;
            bool sameTitle = displayedGame != null && game != null && displayedGame.Title == game.Title;
            displayedGame = game;
            changingFolders = true;
            try
            {
                multipleFolders.Items.Clear();
                if (game != null && game.Folders.Length > 1)
                {
                    multipleFolders.Items.AddRange(game.Folders);
                    multipleFolders.SelectedIndex = sameTitle && previous != null ? multipleFolders.Items.IndexOf(previous) : -1;
                }
            }
            finally { changingFolders = false; }
        }

        selectedGame.Text = game == null ? "Selecione um jogo" : game.Title;
        var folder = CurrentFolder;
        bool hasFolders = game != null && game.Folders.Length > 0;
        multipleFolders.Visible = game != null && game.Folders.Length > 1;
        multipleFolders.Enabled = !busy;
        singleFolder.Visible = hasFolders && game.Folders.Length == 1;
        singleFolder.Text = hasFolders && game.Folders.Length == 1 ? folder : "";
        locationLabel.Text = game == null ? "Escolha um jogo da lista para ver a pasta de saves."
            : hasFolders ? (folder == null ? "Há várias pastas. Escolha a que deseja usar." : "Pasta de saves encontrada")
            : game.HasRegistry ? "Saves encontrados no Registro do Windows."
            : "Nenhuma pasta específica de saves foi encontrada.";
        bool recovery = game != null && game.RecoveryOnly;
        if (recovery) locationLabel.Text = "Backup disponível para recuperar o progresso.";
        bool canManage = game != null && (folder != null || (!hasFolders && game.HasRegistry) || recovery);
        saveButton.Visible = restoreButton.Visible = historyButton.Visible = game != null;
        importButton.Visible = openSaveFolder.Visible = hasFolders && !recovery;
        saveButton.Enabled = restoreButton.Enabled = historyButton.Enabled = !busy && canManage;
        saveButton.Visible = game != null && !recovery;
        importButton.Enabled = openSaveFolder.Enabled = !busy && folder != null;
        scanButton.Enabled = customButton.Enabled = !busy;
    }

    private DetectedGame CurrentGame
    {
        get
        {
            DetectedGame game;
            var title = games.SelectedItem as string;
            return title != null && allGames.TryGetValue(title, out game) ? game : null;
        }
    }

    private string CurrentFolder
    {
        get
        {
            if (displayedGame == null) return null;
            return displayedGame.Folders.Length == 1 ? displayedGame.Folders[0] : multipleFolders.SelectedItem as string;
        }
    }

    private void ApplyFilter()
    {
        var selected = games.SelectedItem as string;
        var folder = CurrentFolder;
        var query = search.Text.Trim();
        var found = allGames.Keys.Where(title => CultureInfo.CurrentCulture.CompareInfo.IndexOf(
            title, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)
            .OrderBy(title => title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        games.BeginUpdate();
        games.Items.Clear();
        games.Items.AddRange(found);
        if (selected != null && games.Items.Contains(selected)) games.SelectedItem = selected;
        if (selected != null && selected == games.SelectedItem as string && folder != null && multipleFolders.Items.Contains(folder))
            multipleFolders.SelectedItem = folder;
        if (games.SelectedIndex < 0 && games.Items.Count == 1) games.SelectedIndex = 0;
        games.EndUpdate();
        RefreshListState();
        UpdateButtons();
    }

    private void RefreshListState()
    {
        int count = games.Items.Count;
        emptyList.Visible = count == 0;
        emptyList.Text = busy ? "Procurando saves…" : search.TextLength == 0
            ? "Nenhum save encontrado." : "Nenhum jogo corresponde à busca.";
        gameCount.Text = count == 1 ? "1 jogo encontrado" : count + " jogos encontrados";
    }

    private async Task Scan()
    {
        SetBusy(true, "Procurando saves neste computador…");
        try
        {
            allGames = (await Task.Run(() => backup.FindGames())).ToDictionary(game => game.Title, StringComparer.OrdinalIgnoreCase);
            ApplyFilter();
            status.Text = !String.IsNullOrEmpty(backup.RecoveryWarning) ? backup.RecoveryWarning : allGames.Count == 0
                ? "Nenhum save encontrado. Abra o jogo, crie um save e atualize a lista."
                : "";
        }
        catch (Exception error)
        {
            status.Text = "A busca de saves falhou.";
            ShowMessage(error.Message, MessageBoxIcon.Warning);
        }
        finally { SetBusy(false, null); }
    }

    private async Task Configure()
    {
        ShowMessage("Na janela do Ludusavi, use a aba Custom Games para adicionar seu jogo e o caminho do save. Feche a janela para atualizar a lista.",
            MessageBoxIcon.Information);
        SetBusy(true, "Aguardando a configuração do Ludusavi…");
        try { await Task.Run(() => backup.ConfigureSaves()); }
        catch (Exception error)
        {
            SetBusy(false, "Não foi possível abrir o Ludusavi.");
            ShowMessage(error.Message, MessageBoxIcon.Warning);
            return;
        }
        await Scan();
    }

    private async Task BackUpGame()
    {
        var title = games.SelectedItem as string;
        if (title == null) return;
        SetBusy(true, "Copiando o save de " + title + "…");
        try
        {
            await Task.Run(() => backup.CreateBackup(title));
            status.Text = "Backup salvo. Use o histórico para localizar o ZIP.";
        }
        catch (Exception error)
        {
            status.Text = "O backup falhou.";
            ShowMessage(error.Message, MessageBoxIcon.Warning);
        }
        finally { SetBusy(false, null); }
    }

    private async Task RestoreGame()
    {
        var game = CurrentGame;
        if (game == null) return;
        string selectedZip;
        try
        {
            var folder = backup.BackupFolder;
            Directory.CreateDirectory(folder);
            using (var picker = new OpenFileDialog
            {
                InitialDirectory = folder,
                Title = "Escolha o ZIP que deseja restaurar",
                Filter = "Backups do MemoryCard (*.zip)|*.zip",
                CheckFileExists = true,
                RestoreDirectory = true
            })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                selectedZip = picker.FileName;
            }
        }
        catch (Exception error)
        {
            ShowMessage(error.Message, MessageBoxIcon.Warning);
            return;
        }

        await RestoreArchive(selectedZip, game);
    }

    private async Task ImportExternal()
    {
        var game = CurrentGame;
        var folder = CurrentFolder;
        if (game == null || folder == null) return;
        try
        {
            bool imported;
            using (var dialog = new ImportWindow(backup.WorkingFolder, backup.BackupFolder, game.Title, folder))
            {
                dialog.ShowDialog(this);
                imported = dialog.Imported;
            }
            if (imported) await Scan();
        }
        catch (Exception error) { ShowMessage(error.Message, MessageBoxIcon.Warning); }
    }

    private async Task ShowHistory()
    {
        var game = CurrentGame;
        if (game == null) return;
        IList<BackupEntry> entries;
        var previousStatus = status.Text;
        SetBusy(true, "Lendo o histórico de backups…");
        try
        {
            entries = await Task.Run(() => backup.ListBackups()
                .Where(entry => String.Equals(entry.Game, game.Title, StringComparison.OrdinalIgnoreCase)).ToList());
        }
        catch (Exception error)
        {
            previousStatus = "Não foi possível abrir o histórico.";
            ShowMessage(error.Message, MessageBoxIcon.Warning);
            return;
        }
        finally { SetBusy(false, previousStatus); }

        string selectedZip;
        using (var dialog = new HistoryWindow(entries, game.Title))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            selectedZip = dialog.ChosenZip;
        }
        await RestoreArchive(selectedZip, game);
    }

    private async Task RestoreArchive(string selectedZip, DetectedGame game)
    {
        SetBusy(true, "Lendo o backup…");
        try
        {
            using (var preview = await Task.Run(() => backup.InspectBackup(selectedZip)))
            {
                if (!String.Equals(game.Title, preview.Title, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Este backup é de " + preview.Title + ". Selecione esse jogo na tela principal.");
                if (!game.MatchesTargets(preview))
                    throw new InvalidOperationException("O ZIP aponta para caminhos diferentes dos saves encontrados neste PC.");
                var message = "Feche o jogo. A restauração pode sobrescrever o progresso atual.\r\n\r\n"
                    + "Arquivo: " + Path.GetFileName(selectedZip) + "\r\n"
                    + "Itens a restaurar: " + preview.Targets.Length + "\r\n\r\n"
                    + String.Join("\r\n", preview.Targets);
                bool keepCurrent = true;
                if (Prompt("Restaurar " + preview.Title, message, "Restaurar", true,
                    value => keepCurrent = value) != DialogResult.OK)
                {
                    status.Text = "Restauração cancelada.";
                    return;
                }

                string safety = null;
                if (keepCurrent)
                {
                    try
                    {
                        status.Text = "Guardando o progresso atual…";
                        safety = await Task.Run(() => backup.SaveCurrentProgress(preview));
                    }
                    catch (Exception error)
                    {
                        status.Text = "Restauração interrompida. O progresso atual foi preservado.";
                        ShowMessage("A cópia de segurança falhou. A restauração não foi iniciada.\r\n\r\n"
                            + error.Message, MessageBoxIcon.Warning);
                        return;
                    }
                }
                status.Text = "Restaurando o save de " + preview.Title + "…";
                await Task.Run(() => backup.RestoreBackup(preview));
                if (game.RecoveryOnly) await Scan();
                status.Text = safety == null ? "Save restaurado: " + preview.Title
                    : "Save restaurado. A cópia de segurança está no histórico.";
            }
        }
        catch (Exception error)
        {
            status.Text = "A restauração falhou.";
            ShowMessage(error.Message, MessageBoxIcon.Warning);
        }
        finally { SetBusy(false, null); }
    }

    private void OpenGameFolder()
    {
        var folder = CurrentFolder;
        if (folder == null) return;
        try
        {
            if (!Directory.Exists(folder)) throw new IOException("A pasta de saves não existe mais. Atualize a lista.");
            Process.Start(folder);
        }
        catch (Exception error) { ShowMessage(error.Message, MessageBoxIcon.Warning); }
    }

    private void OpenBackups()
    {
        try
        {
            var folder = backup.BackupFolder;
            Directory.CreateDirectory(folder);
            Process.Start(folder);
        }
        catch (Exception error) { ShowMessage(error.Message, MessageBoxIcon.Warning); }
    }

    private void ShowMessage(string message, MessageBoxIcon icon)
    {
        Prompt(icon == MessageBoxIcon.Warning ? "Atenção" : "Configurar saves", message, "Entendi");
    }

    private DialogResult Prompt(string title, string message, string action, bool canCancel = false,
        Action<bool> saveCurrentChoice = null)
    {
        using (var dialog = new Form
        {
            Text = "MemoryCard", ClientSize = new Size(560, canCancel ? 332 : 224), StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
            ShowInTaskbar = false, BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Font
        })
        {
            dialog.HandleCreated += (sender, args) => Theme.DarkTitleBar(dialog);
            var grid = Grid(Theme.Surface, new Padding(22, 12, 22, 16), 42, 0, saveCurrentChoice == null ? 1 : 42, 37);
            dialog.Controls.Add(grid);
            var summary = TextLabel(title, Theme.Text, 16, true);
            summary.AutoEllipsis = true;
            grid.Controls.Add(summary, 0, 0);
            grid.Controls.Add(new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, TabStop = false,
                BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Muted,
                ScrollBars = ScrollBars.Vertical, Text = message, Margin = new Padding(0, 3, 0, 10)
            }, 0, 1);
            CheckBox safety = null;
            if (saveCurrentChoice != null)
            {
                safety = new CheckBox { Text = "Guardar meu progresso atual", Checked = true,
                    ForeColor = Theme.Text, Dock = DockStyle.Fill, Margin = Padding.Empty };
                grid.Controls.Add(safety, 0, 2);
            }
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty,
                FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var ok = new AnimatedButton(action, true) { Dock = DockStyle.None, Width = 145, Height = 37,
                DialogResult = DialogResult.OK };
            actions.Controls.Add(ok);
            if (canCancel)
            {
                var cancel = new AnimatedButton("Cancelar", false) { Dock = DockStyle.None, Width = 110, Height = 37,
                    Margin = new Padding(0, 0, 8, 0), DialogResult = DialogResult.Cancel };
                actions.Controls.Add(cancel);
                dialog.CancelButton = cancel;
                dialog.AcceptButton = cancel;
                dialog.ActiveControl = cancel;
            }
            else
            {
                dialog.AcceptButton = ok;
            }
            grid.Controls.Add(actions, 0, 3);
            var result = dialog.ShowDialog(this);
            if (result == DialogResult.OK && safety != null) saveCurrentChoice(safety.Checked);
            return result;
        }
    }
}

