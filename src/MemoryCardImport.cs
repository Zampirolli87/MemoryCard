using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class ExternalSave : IDisposable
{
    private readonly string staging;
    public readonly string[] Files;
    public readonly string[] Roots;

    private ExternalSave(string staging, IEnumerable<string> files)
    {
        this.staging = staging;
        Files = files.ToArray();
        if (Files.Length == 0) throw new IOException("A origem não contém arquivos.");
        Roots = Files.SelectMany(Parents).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static ExternalSave Open(string source, string workFolder, bool folder)
    {
        var stage = Path.Combine(workFolder, "Importacao-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var files = new List<string>();
            var extension = Path.GetExtension(source);
            if (folder)
            {
                var sourceRoot = Path.GetFullPath(source).TrimEnd('\\') + "\\";
                if (stage.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Selecione a pasta específica do save como origem.");
                CopyDirectory(source, "", stage, files);
            }
            else if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using (var archive = ZipFile.OpenRead(source))
                {
                    var candidates = archive.Entries.Where(entry => entry.Name.Length > 0).ToArray();
                    if (candidates.Any(entry => entry.Name == "mapping.yaml") &&
                        candidates.Any(entry => entry.Name.StartsWith("backup-", StringComparison.Ordinal)
                            && entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Esse ZIP foi criado pelo MemoryCard. Abra em Restaurar backup.");
                    var paths = candidates.Select(entry => SafeRelative(entry.FullName)).ToArray();
                    if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
                        throw new InvalidDataException("O ZIP contém caminhos duplicados.");
                    var available = new DriveInfo(Path.GetPathRoot(stage)).AvailableFreeSpace;
                    if (candidates.Sum(entry => entry.Length) > available)
                        throw new IOException("Não há espaço para preparar os arquivos do ZIP.");
                    for (int i = 0; i < paths.Length; i++)
                    {
                        var entry = candidates[i];
                        var target = Path.Combine(stage, paths[i]);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        ExtractEntry(entry, target);
                        files.Add(paths[i]);
                    }
                }
            }
            else
            {
                if (new[] { ".7z", ".rar", ".tar", ".gz" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("Extraia esse pacote e use Escolher pasta de origem.");
                RejectLink(source);
                var name = SafeRelative(Path.GetFileName(source));
                CopyContents(source, Path.Combine(stage, name));
                files.Add(name);
            }

            return new ExternalSave(stage, files);
        }
        catch
        {
            Erase(stage);
            throw;
        }
    }

    private static void CopyDirectory(string root, string relative, string stage, List<string> files)
    {
        var folder = Path.Combine(root, relative);
        RejectLink(folder);
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            RejectLink(file);
            var path = SafeRelative(Path.Combine(relative, Path.GetFileName(file)));
            var target = Path.Combine(stage, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            CopyContents(file, target);
            files.Add(path);
        }
        foreach (var directory in Directory.EnumerateDirectories(folder))
            CopyDirectory(root, Path.Combine(relative, Path.GetFileName(directory)), stage, files);
    }

    private static IEnumerable<string> Parents(string path)
    {
        for (var parent = Path.GetDirectoryName(path); !String.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            yield return parent;
    }

    internal static void ExtractEntry(ZipArchiveEntry entry, string target)
    {
        using (var input = entry.Open())
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
        {
            long remaining = entry.Length;
            var buffer = new byte[65536];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (read > remaining) throw new InvalidDataException("Tamanho inválido no ZIP.");
                output.Write(buffer, 0, read);
                remaining -= read;
            }
            if (remaining != 0) throw new InvalidDataException("Arquivo incompleto no ZIP.");
        }
    }

    internal static string SafeRelative(string path)
    {
        var parts = path.Replace('/', '\\').Split('\\');
        foreach (var part in parts)
        {
            var primaryName = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            bool device = primaryName == "CON" || primaryName == "NUL" || primaryName == "PRN" || primaryName == "AUX"
                || primaryName == "CONIN$" || primaryName == "CONOUT$"
                || (primaryName.Length == 4 && (primaryName.StartsWith("COM") || primaryName.StartsWith("LPT"))
                    && "0123456789¹²³".IndexOf(primaryName[3]) >= 0);
            if (part.Length == 0 || part.TrimEnd('.', ' ') != part || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || device)
                throw new InvalidDataException("A origem contém um caminho inválido: " + path);
        }
        return String.Join("\\", parts);
    }

    internal static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("O caminho aponta para um link ou redirecionamento: " + path);
    }

    internal static void Erase(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    internal static void CopyContents(string source, string destination)
    {
        using (var input = File.OpenRead(source))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
            input.CopyTo(output);
    }

    public ImportPlan Preview(string root, string destination)
    {
        destination = Path.GetFullPath(destination);
        if (!Directory.Exists(destination)) throw new IOException("Selecione uma pasta de saves que já exista.");
        if (destination.Equals(Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Selecione a pasta do jogo no lugar da raiz do disco.");
        var prefix = String.IsNullOrEmpty(root) ? "" : root + "\\";
        if (prefix.Length > 0 && !Roots.Contains(root, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("A pasta de origem não existe.");
        var items = Files.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(path => new ImportItem(Path.Combine(staging, path), path.Substring(prefix.Length), destination)).ToArray();
        return new ImportPlan(destination, items);
    }

    public void Dispose() { Erase(staging); }
}

internal sealed class ImportItem
{
    public ImportItem(string source, string relative, string root)
    {
        Source = source;
        Relative = relative;
        Destination = Path.Combine(root, relative);
        var info = ReadDestination();
        if (info != null)
        {
            Exists = true;
            oldLength = info.Length;
            oldModified = info.LastWriteTimeUtc;
        }
    }

    public readonly string Source;
    public readonly string Relative;
    public readonly string Destination;
    public readonly bool Exists;
    private readonly long oldLength;
    private readonly DateTime oldModified;

    private FileInfo ReadDestination()
    {
        for (var directory = Path.GetDirectoryName(Destination); directory != null; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(directory)) throw new IOException("Um arquivo impede a criação da pasta: " + directory);
            if (Directory.Exists(directory)) ExternalSave.RejectLink(directory);
        }
        if (Directory.Exists(Destination)) throw new IOException("Uma pasta tem o mesmo nome de um arquivo a importar: " + Destination);
        try { ExternalSave.RejectLink(Destination); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        return new FileInfo(Destination);
    }

    public void Recheck()
    {
        var info = ReadDestination();
        if (Exists ? info == null || info.Length != oldLength || info.LastWriteTimeUtc != oldModified : info != null)
            throw new IOException("O destino mudou depois da prévia. Confira novamente: " + Destination);
    }
}

internal sealed class ImportPlan
{
    public readonly string Destination;
    public readonly ImportItem[] Items;

    public ImportPlan(string destination, ImportItem[] items)
    {
        Destination = destination;
        Items = items;
    }

    public string Apply(string backupFolder)
    {
        if (Items.Length == 0) throw new IOException("Selecione uma origem com arquivos.");
        foreach (var item in Items) item.Recheck();
        string safety = null;
        var originals = Items.Where(item => item.Exists).ToArray();
        if (originals.Length > 0)
        {
            safety = Path.Combine(backupFolder, "Segurança de importação",
                DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + " - " + Guid.NewGuid().ToString("N").Substring(0, 8));
            var root = Path.GetFullPath(backupFolder).TrimEnd('\\') + "\\";
            var target = Destination.TrimEnd('\\') + "\\";
            if (root.StartsWith(target, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Selecione a pasta de saves do jogo. Ela contém a pasta de backups do MemoryCard.");
            try
            {
                foreach (var item in originals)
                {
                    item.Recheck();
                    var copy = Path.Combine(safety, item.Relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(copy));
                    ExternalSave.CopyContents(item.Destination, copy);
                }
            }
            catch
            {
                ExternalSave.Erase(safety);
                throw;
            }
        }

        var written = new List<ImportItem>();
        var newDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var item in Items)
            {
                item.Recheck();
                for (var dir = Path.GetDirectoryName(item.Destination); dir != null && !Directory.Exists(dir);
                    dir = Path.GetDirectoryName(dir)) newDirectories.Add(dir);
                WriteFile(item.Source, item.Destination, item.Exists);
                written.Add(item);
            }
        }
        catch (Exception error)
        {
            bool rollbackFailed = false;
            foreach (var item in written.AsEnumerable().Reverse())
            {
                try
                {
                    // A running game may have saved again since our last write.
                    if (!SameContents(item.Destination, item.Source))
                    {
                        rollbackFailed = true;
                        continue;
                    }
                    if (item.Exists) WriteFile(Path.Combine(safety, item.Relative), item.Destination, true);
                    else File.Delete(item.Destination);
                }
                catch (IOException) { rollbackFailed = true; }
                catch (UnauthorizedAccessException) { rollbackFailed = true; }
            }
            foreach (var dir in newDirectories.OrderByDescending(path => path.Length))
            {
                try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
                catch (IOException) { rollbackFailed = true; }
                catch (UnauthorizedAccessException) { rollbackFailed = true; }
            }
            if (rollbackFailed)
                throw new IOException("A importação foi interrompida e alguns arquivos precisam ser verificados."
                    + (safety == null ? "" : "\r\nCópia dos arquivos originais: " + safety) + "\r\n" + error.Message, error);
            throw new IOException("A importação foi interrompida e as alterações feitas pelo app foram revertidas.\r\n" + error.Message, error);
        }

        return safety;
    }

    private static bool SameContents(string first, string second)
    {
        using (var left = File.OpenRead(first))
        using (var right = File.OpenRead(second))
        using (var hash = System.Security.Cryptography.SHA256.Create())
            return left.Length == right.Length && hash.ComputeHash(left).SequenceEqual(hash.ComputeHash(right));
    }

    private static void WriteFile(string source, string destination, bool overwrite)
    {
        var folder = Path.GetDirectoryName(destination);
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, ".memorycard-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            ExternalSave.CopyContents(source, temporary);
            if (overwrite) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class ImportWindow : Form
{
    private readonly string workingFolder;
    private readonly string backupFolder;
    private readonly ComboBox roots;
    private readonly Label sourceLabel;
    private readonly Label status;
    private readonly TextBox preview;
    private readonly AnimatedButton import;
    private readonly List<Control> navigation = new List<Control>();
    private ExternalSave source;
    private ImportPlan plan;
    private readonly string destination;
    private bool busy;
    public bool Imported { get; private set; }

    public ImportWindow(string workingFolder, string backupFolder, string game, string destination)
    {
        this.workingFolder = workingFolder;
        this.backupFolder = backupFolder;
        this.destination = destination;
        Text = "Importar save";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(790, 591);
        MinimumSize = new Size(715, 535);
        ShowInTaskbar = false;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 10);
        MinimizeBox = false;
        HandleCreated += (sender, args) => Theme.DarkTitleBar(this);
        FormClosing += (sender, args) => { if (busy && args.CloseReason == CloseReason.UserClosing) args.Cancel = true; };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(24, 15, 24, 17),
            ColumnCount = 1, RowCount = 8, BackColor = Theme.Surface
        };
        foreach (int height in new[] { 43, 46, 43, 29, 37, 0, 49, 42 })
            layout.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
        Controls.Add(layout);

        var title = ImportLabel("Importar save · " + game, Theme.Text);
        title.Font = new Font("Segoe UI", 18, FontStyle.Bold);
        layout.Controls.Add(title, 0, 0);
        var destinationBox = new TextBox
        {
            Text = "Destino detectado: " + destination, ReadOnly = true, Multiline = true, TabStop = false,
            BackColor = Theme.Field, ForeColor = Theme.Accent, BorderStyle = BorderStyle.FixedSingle,
            Dock = DockStyle.Fill, AccessibleName = "Pasta de destino detectada"
        };
        layout.Controls.Add(destinationBox, 0, 1);

        var sourceRow = Buttons();
        var file = Button("Escolher arquivo ou ZIP", 206, async () => await ChooseSource(false));
        var folder = Button("Escolher pasta de origem", 209, async () => await ChooseSource(true));
        sourceRow.Controls.AddRange(new Control[] { file, folder });
        layout.Controls.Add(sourceRow, 0, 2);
        sourceLabel = ImportLabel("Nenhuma origem selecionada", Theme.Muted);
        layout.Controls.Add(sourceLabel, 0, 3);

        var rootRow = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 1 };
        rootRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 157));
        rootRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rootRow.Controls.Add(ImportLabel("Copiar o conteúdo de", Theme.Muted), 0, 0);
        roots = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top,
            BackColor = Theme.Field, ForeColor = Theme.Text, Margin = new Padding(0, 3, 0, 3),
            AccessibleName = "Pasta de origem dentro do save" };
        roots.SelectedIndexChanged += (sender, args) => RefreshPlan();
        rootRow.Controls.Add(roots, 1, 0);
        layout.Controls.Add(rootRow, 0, 4);

        preview = new TextBox { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            Font = new Font("Consolas", 10), Dock = DockStyle.Fill, BackColor = Theme.Field, ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle, AccessibleName = "Prévia dos arquivos a importar",
            Text = "Escolha um arquivo, ZIP ou pasta de origem para ver a prévia." };
        layout.Controls.Add(preview, 0, 5);
        status = ImportLabel("", Theme.Accent);
        layout.Controls.Add(status, 0, 6);

        var actions = Buttons();
        actions.FlowDirection = FlowDirection.RightToLeft;
        import = new AnimatedButton("Importar arquivos", true) { Dock = DockStyle.None, Width = 166, Height = 38 };
        import.Click += async (sender, args) => await ImportSelected();
        var close = new AnimatedButton("Fechar", false) { Dock = DockStyle.None, Width = 100, Height = 38,
            Margin = new Padding(0, 0, 8, 0), DialogResult = DialogResult.Cancel };
        navigation.Add(close);
        CancelButton = close;
        actions.Controls.AddRange(new Control[] { import, close });
        layout.Controls.Add(actions, 0, 7);
        UpdateControls();
    }

    private static Label ImportLabel(string text, Color color)
    {
        return new Label { Text = text, ForeColor = color, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = Padding.Empty };
    }

    private static FlowLayoutPanel Buttons()
    {
        return new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
    }

    private AnimatedButton Button(string label, int width, Func<Task> action)
    {
        var button = new AnimatedButton(label, false) { Dock = DockStyle.None, Width = width, Height = 36,
            Margin = new Padding(0, 0, 8, 0) };
        button.Click += async (sender, args) => await action();
        navigation.Add(button);
        return button;
    }

    private void UpdateControls()
    {
        foreach (var button in navigation) button.Enabled = !busy;
        bool hasSubfolders = source != null && roots.Items.Count > 1;
        roots.Parent.Visible = hasSubfolders;
        roots.Enabled = !busy && hasSubfolders;
        import.Enabled = !busy && plan != null;
        UseWaitCursor = busy;
    }

    private async Task ChooseSource(bool folder)
    {
        string path;
        if (folder)
        {
            using (var picker = new FolderBrowserDialog { Description = "Selecione a pasta de origem do save", ShowNewFolderButton = false })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                path = picker.SelectedPath;
            }
        }
        else
        {
            using (var picker = new OpenFileDialog { Title = "Escolha o arquivo de save ou ZIP",
                Filter = "Qualquer arquivo|*.*", CheckFileExists = true, RestoreDirectory = true })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                path = picker.FileName;
            }
        }

        try { await LoadSource(path, folder); }
        catch (Exception error) { ShowError(error); }
    }

    internal async Task LoadSource(string path, bool folder)
    {
        busy = true;
        status.Text = "Preparando arquivos…";
        UpdateControls();
        try
        {
            var previous = source;
            source = null;
            plan = null;
            roots.Items.Clear();
            sourceLabel.Text = "Nenhuma origem selecionada";
            preview.Text = "Escolha um arquivo, ZIP ou pasta de origem para ver a prévia.";
            if (previous != null) previous.Dispose();
            source = await Task.Run(() => ExternalSave.Open(path, workingFolder, folder));
            sourceLabel.Text = "Origem: " + path;
            roots.Items.Clear();
            roots.Items.Add("(Raiz da origem)");
            roots.Items.AddRange(source.Roots);
            var preferred = PreferredRoot(source.Roots, destination);
            roots.SelectedIndex = preferred == null ? 0 : roots.Items.IndexOf(preferred);
        }
        finally { busy = false; RefreshPlan(); }
    }

    internal static string PreferredRoot(IEnumerable<string> roots, string destination)
    {
        var name = Path.GetFileName(destination.TrimEnd('\\', '/'));
        var matches = roots.Where(root => String.Equals(Path.GetFileName(root), name, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private void RefreshPlan()
    {
        if (busy) return;
        plan = null;
        status.Text = "";
        if (source != null)
        {
            try
            {
                var root = roots.SelectedIndex > 0 ? (string)roots.SelectedItem : "";
                plan = source.Preview(root, destination);
                int old = plan.Items.Count(item => item.Exists);
                preview.Lines = plan.Items.Take(500).Select(item => (item.Exists ? "SUBSTITUIR     " : "NOVO           ")
                    + item.Relative).Concat(plan.Items.Length > 500 ? new[] { "…" } : new string[0]).ToArray();
                status.Text = plan.Items.Length + " arquivo(s) · " + old + " substituição(ões). "
                    + (old > 0 ? "Os originais serão guardados antes da importação." : "");
            }
            catch (Exception error) { preview.Text = error.Message; }
        }
        UpdateControls();
    }

    private async Task ImportSelected()
    {
        var current = plan;
        if (current == null) return;
        var question = "Feche o jogo antes de continuar.\r\n\r\nCopiar " + current.Items.Length + " arquivo(s) para:\r\n"
            + current.Destination + "\r\n\r\nOs arquivos marcados como SUBSTITUIR serão guardados antes da cópia.";
        if (MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        busy = true;
        status.Text = "Importando saves…";
        UpdateControls();
        try
        {
            var safety = await Task.Run(() => current.Apply(backupFolder));
            Imported = true;
            plan = null;
            preview.Text = "Importação concluída em:\r\n" + current.Destination
                + (safety == null ? "" : "\r\n\r\nArquivos anteriores guardados em:\r\n" + safety);
            status.Text = "Save importado.";
        }
        catch (Exception error)
        {
            busy = false;
            RefreshPlan();
            ShowError(error);
        }
        finally { busy = false; UpdateControls(); }
    }

    private void ShowError(Exception error)
    {
        MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && source != null)
        {
            try { source.Dispose(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        base.Dispose(disposing);
    }
}

