using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class BackupMetadata
{
    public string Game { get; set; }
    public string Created { get; set; }
    public bool Safety { get; set; }
    public string MappingSha256 { get; set; }
    public string PayloadSha256 { get; set; }

    public static string HashFile(string path)
    {
        using (var file = File.OpenRead(path))
        using (var hash = System.Security.Cryptography.SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "");
    }
}

internal sealed class BackupEntry
{
    public string Game { get; set; }
    public DateTimeOffset Created { get; set; }
    public bool Safety { get; set; }
    public long Size { get; set; }
    public string Path { get; set; }
}

internal static class BackupHistory
{
    public const string MetadataFile = "MemoryCard.json";

    public static IList<BackupEntry> Read(IEnumerable<string> folders)
    {
        var history = new List<BackupEntry>();
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.zip"))
            {
                try
                {
                    var entry = ReadEntry(file);
                    if (entry != null) history.Add(entry);
                }
                catch (Exception error)
                {
                    // Downloads can contain ZIP files that are unrelated, incomplete, or inaccessible.
                    if (!(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException
                        || error is ArgumentException || error is InvalidOperationException)) throw;
                }
            }
        }
        return history.OrderByDescending(entry => entry.Created).ToList();
    }

    private static BackupEntry ReadEntry(string file)
    {
        using (var zip = ZipFile.OpenRead(file))
        {
            var mapping = zip.Entries.SingleOrDefault(entry => entry.Name == "mapping.yaml");
            var payload = zip.Entries.SingleOrDefault(entry => entry.Name.StartsWith("backup-", StringComparison.Ordinal)
                && entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (mapping == null || payload == null) return null;

            var info = new FileInfo(file);
            var backup = new BackupEntry
            {
                Path = file, Created = new DateTimeOffset(info.LastWriteTime), Size = info.Length,
                Game = mapping.FullName.Replace('\\', '/').Split('/')[0]
            };

            var metadata = zip.GetEntry(MetadataFile);
            DateTimeOffset payloadTime;
            if (DateTimeOffset.TryParseExact(payload.Name, "'backup-'yyyyMMdd'T'HHmmss'Z.zip'",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out payloadTime)) backup.Created = payloadTime;
            if (metadata != null && metadata.Length <= 8192)
            {
                using (var reader = new StreamReader(metadata.Open()))
                {
                    var data = new JavaScriptSerializer().Deserialize<BackupMetadata>(reader.ReadToEnd());
                    if (data != null && !String.IsNullOrWhiteSpace(data.Game)) backup.Game = data.Game;
                    DateTimeOffset created;
                    if (data != null && DateTimeOffset.TryParse(data.Created, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out created)) backup.Created = created;
                    if (data != null) backup.Safety = data.Safety;
                }
            }
            else if (mapping.Length < 65536)
            {
                using (var reader = new StreamReader(mapping.Open()))
                {
                    for (int i = 0; i < 4 && !reader.EndOfStream; i++)
                    {
                        var line = reader.ReadLine();
                        if (!line.StartsWith("name: ", StringComparison.Ordinal)) continue;
                        var title = line.Substring(6).Trim();
                        if (title.Length >= 2 && title[0] == '\'' && title[title.Length - 1] == '\'')
                            title = title.Substring(1, title.Length - 2).Replace("''", "'");
                        else if (title.StartsWith("\"") && title.EndsWith("\""))
                            title = new JavaScriptSerializer().Deserialize<string>(title);
                        if (!String.IsNullOrWhiteSpace(title)) backup.Game = title;
                        break;
                    }
                }
            }
            return backup;
        }
    }
}

internal sealed class HistoryWindow : Form
{
    private readonly DataGridView table;
    private readonly TextBox search;
    private readonly Label count;
    private readonly Button restore;
    private readonly Button reveal;
    private readonly IList<BackupEntry> entries;

    public HistoryWindow(IList<BackupEntry> entries, string game)
    {
        this.entries = entries;
        Text = "Histórico de backups";
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(800, 480);
        MinimumSize = new Size(680, 390);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        ShowInTaskbar = false;
        MinimizeBox = false;
        HandleCreated += (sender, args) => Theme.DarkTitleBar(this);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(22, 14, 22, 16),
            ColumnCount = 1, RowCount = 5
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        layout.Controls.Add(new Label
        {
            Text = "Histórico de backups", Font = new Font("Segoe UI", 18, FontStyle.Bold),
            Dock = DockStyle.Fill, Margin = Padding.Empty
        }, 0, 0);
        Controls.Add(layout);

        var searchFrame = new SurfacePanel
        {
            Dock = DockStyle.Fill, BackColor = Theme.Field, Padding = new Padding(12, 8, 12, 4),
            Margin = new Padding(0, 0, 0, 7)
        };
        search = new TextBox
        {
            Dock = DockStyle.Fill, BackColor = Theme.Field, ForeColor = Theme.Text, BorderStyle = BorderStyle.None,
            AccessibleName = "Filtrar pelo nome do jogo"
        };
        search.HandleCreated += (sender, args) => Theme.SearchHint(search, "Filtrar pelo nome do jogo");
        searchFrame.Controls.Add(search);
        layout.Controls.Add(searchFrame, 0, 1);

        table = new DataGridView
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, BackgroundColor = Theme.Field, GridColor = Theme.Border,
            BorderStyle = BorderStyle.FixedSingle, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false,
            MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, EnableHeadersVisualStyles = false,
            AccessibleName = "Backups encontrados"
        };
        table.DefaultCellStyle.BackColor = Theme.Field;
        table.DefaultCellStyle.ForeColor = Theme.Text;
        table.DefaultCellStyle.SelectionBackColor = Color.FromArgb(62, 38, 103);
        table.DefaultCellStyle.SelectionForeColor = Theme.Text;
        table.DefaultCellStyle.Padding = new Padding(7, 0, 4, 0);
        table.ColumnHeadersDefaultCellStyle.BackColor = Theme.Surface;
        table.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Accent;
        table.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Surface;
        table.ColumnHeadersHeight = 34;
        table.RowTemplate.Height = 38;
        AddColumn("Jogo", 44);
        AddColumn("Data e hora", 24);
        AddColumn("Tamanho", 14);
        AddColumn("Tipo", 18);
        table.SelectionChanged += (sender, args) => UpdateButtons();
        table.CellDoubleClick += (sender, args) => { if (args.RowIndex >= 0) ChooseRestore(); };
        layout.Controls.Add(table, 0, 2);

        count = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft };
        layout.Controls.Add(count, 0, 3);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Margin = Padding.Empty };
        restore = new AnimatedButton("Restaurar", true) { Dock = DockStyle.None, Width = 125, Height = 38 };
        restore.Click += (sender, args) => ChooseRestore();
        reveal = new AnimatedButton("Mostrar arquivo", false) { Dock = DockStyle.None, Width = 148, Height = 38,
            Margin = new Padding(0, 0, 8, 0) };
        reveal.Click += (sender, args) => RevealFile();
        var close = new AnimatedButton("Fechar", false) { Dock = DockStyle.None, Width = 95, Height = 38,
            Margin = new Padding(0, 0, 8, 0), DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange(new Control[] { restore, reveal, close });
        CancelButton = close;
        layout.Controls.Add(actions, 0, 4);

        search.Text = entries.Any(entry => String.Equals(entry.Game, game, StringComparison.CurrentCultureIgnoreCase)) ? game : "";
        search.TextChanged += (sender, args) => RefreshRows();
        RefreshRows();
    }

    public string ChosenZip { get; private set; }

    private void AddColumn(string label, float weight)
    {
        table.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = label, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable
        });
    }

    private BackupEntry Selected
    {
        get { return table.SelectedRows.Count == 1 ? table.SelectedRows[0].Tag as BackupEntry : null; }
    }

    private void RefreshRows()
    {
        var query = search.Text.Trim();
        table.Rows.Clear();
        foreach (var entry in entries.Where(item => CultureInfo.CurrentCulture.CompareInfo.IndexOf(
            item.Game, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0))
        {
            int index = table.Rows.Add(entry.Game, entry.Created.LocalDateTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
                FormatSize(entry.Size), entry.Safety ? "Segurança" : "Manual");
            table.Rows[index].Tag = entry;
        }
        table.ClearSelection();
        count.Text = table.Rows.Count == 0 ? "Nenhum backup encontrado." :
            table.Rows.Count == 1 ? "1 backup encontrado" : table.Rows.Count + " backups encontrados";
        UpdateButtons();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024L * 1024) return (bytes / 1024d).ToString("N1") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / (1024d * 1024)).ToString("N1") + " MB";
        return (bytes / (1024d * 1024 * 1024)).ToString("N1") + " GB";
    }

    private void UpdateButtons()
    {
        if (restore == null || reveal == null) return;
        reveal.Enabled = restore.Enabled = Selected != null;
    }

    private void ChooseRestore()
    {
        var entry = Selected;
        if (entry == null) return;
        ChosenZip = entry.Path;
        DialogResult = DialogResult.OK;
    }

    private void RevealFile()
    {
        var entry = Selected;
        if (entry == null) return;
        if (!File.Exists(entry.Path))
        {
            MessageBox.Show(this, "O ZIP foi movido ou removido.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try { Process.Start("explorer.exe", "/select,\"" + entry.Path + "\""); }
        catch (Exception error) { MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}

