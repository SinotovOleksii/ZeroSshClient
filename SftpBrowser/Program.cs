using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SftpBrowser;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        string user = "deployer";
        string host = "192.168.159.138";

        if (args.Length > 0)
        {
            var arg = args[0];
            var atIdx = arg.IndexOf('@');
            if (atIdx > 0 && atIdx < arg.Length - 1)
            {
                user = arg[..atIdx];
                host = arg[(atIdx + 1)..];
            }
        }

        Application.Run(new MainForm(user, host));
    }
}

public class MainForm : Form
{
    // --- Параметри SFTP із командного рядка ---
    private readonly string _sftpHost = null!;
    private readonly int    _sftpPort;
    private readonly string _sftpUser = null!;

    // --- UI ---
    private TextBox txtRemotePath      = null!;
    private TextBox txtLocalPath       = null!;
    private ListView lvRemote          = null!;
    private ListView lvLocal           = null!;
    private Button btnCopyFromRemote   = null!;
    private Button btnCopyFromLocal    = null!;
    private Button btnCreateFolder     = null!;
    private Button btnDelete           = null!;
    private Label  lblStatus           = null!;

    // Поточні шляхи
    private string _remotePath = "/";
    private string _localPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private enum PanelSide { Remote, Local }
    private PanelSide _activeSide = PanelSide.Remote;

    public MainForm(string sftpUser, string sftpHost, int sftpPort = 22)
    {
        _sftpUser = EnsureSshIdentifier(sftpUser, nameof(sftpUser));
        _sftpHost = EnsureSshIdentifier(sftpHost, nameof(sftpHost));
        _sftpPort = sftpPort;

        Text = $"SFTP Browser - {_sftpUser}@{_sftpHost}";
        Width = 1200;
        Height = 600;

        KeyPreview = true; // для гарячих клавіш F5/F6/F7/F8
        KeyDown += MainForm_KeyDown;

        InitUi();

        txtRemotePath.Text = _remotePath;
        txtLocalPath.Text  = _localPath;

        RefreshLocal();
        _ = RefreshRemoteAsync();
        
    }

    private class ItemTag
    {
        public bool IsDir { get; set; }
        public string FullPath { get; set; } = "";
    }

    private void InitUi()
    {
        // Панель кнопок внизу
        var panelButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 35,
            FlowDirection = FlowDirection.LeftToRight
        };

        btnCopyFromRemote = new Button { Text = "F5 Copy from remote", Width = 150 };
        btnCopyFromLocal  = new Button { Text = "F6 Copy from local",  Width = 150 };
        btnCreateFolder   = new Button { Text = "F7 Create folder",    Width = 150 };
        btnDelete         = new Button { Text = "F8 Delete",           Width = 150 };

        btnCopyFromRemote.Click += async (s, e) => await CopyFromRemoteAsync();
        btnCopyFromLocal.Click  += async (s, e) => await CopyFromLocalAsync();
        btnCreateFolder.Click   += async (s, e) => await CreateFolderAsync();
        btnDelete.Click         += async (s, e) => await DeleteAsync();

        panelButtons.Controls.Add(btnCopyFromRemote);
        panelButtons.Controls.Add(btnCopyFromLocal);
        panelButtons.Controls.Add(btnCreateFolder);
        panelButtons.Controls.Add(btnDelete);

        // Основна панель: 2 колонки (ліва/права), 2 рядки (адреса, список)
        var panelMain = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2
        };
        panelMain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panelMain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panelMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); // рядок адрес
        panelMain.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // списки

        txtRemotePath = new TextBox { Dock = DockStyle.Fill };
        txtLocalPath  = new TextBox { Dock = DockStyle.Fill };

        txtRemotePath.KeyDown += async (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                try
                {
                    _remotePath = NormalizeRemotePath(txtRemotePath.Text);
                    await RefreshRemoteAsync();
                }
                catch (ArgumentException ex)
                {
                    ShowStatus($"Invalid remote path: {ex.Message}");
                }
            }
        };

        txtLocalPath.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                if (Directory.Exists(txtLocalPath.Text))
                {
                    _localPath = txtLocalPath.Text;
                    RefreshLocal();
                }
                else
                {
                    ShowStatus("Local path does not exist.");
                }
            }
        };

        lvRemote = CreateRemoteListView();
        lvLocal  = CreateLocalListView();

        lvRemote.MouseClick += (s, e) => _activeSide = PanelSide.Remote;
        lvLocal.MouseClick  += (s, e) => _activeSide = PanelSide.Local;

        lvRemote.DoubleClick += async (s, e) => await RemoteItemDoubleClickAsync();
        lvLocal.DoubleClick  += (s, e) => LocalItemDoubleClick();

        // Рядок 0 – адреси
        panelMain.Controls.Add(txtRemotePath, 0, 0);
        panelMain.Controls.Add(txtLocalPath,  1, 0);
        // Рядок 1 – списки
        panelMain.Controls.Add(lvRemote, 0, 1);
        panelMain.Controls.Add(lvLocal,  1, 1);

        // Статус-бар
        lblStatus = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft
        };

        Controls.Add(panelMain);
        Controls.Add(panelButtons);
        Controls.Add(lblStatus);
    }

    private ListView CreateRemoteListView()
    {
        var lv = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true
        };
        lv.Columns.Add("Name", 150);
        lv.Columns.Add("Size", 60);
        lv.Columns.Add("Owner", 80);
        lv.Columns.Add("Group", 80);
        lv.Columns.Add("Date", 100);
        lv.Columns.Add("Perm", 100);
        return lv;
    }

    private ListView CreateLocalListView()
    {
        var lv = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true
        };
        lv.Columns.Add("Name", 300);
        lv.Columns.Add("Size", 80);
        lv.Columns.Add("Type", 80);
        return lv;
    }

    private async void MainForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F5)
        {
            await CopyFromRemoteAsync();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.F6)
        {
            await CopyFromLocalAsync();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.F7)
        {
            await CreateFolderAsync();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.F8)
        {
            await DeleteAsync();
            e.Handled = true;
        }
    }

    // ---------------- REMOTE ----------------

    private async Task RefreshRemoteAsync()
    {
        try
        {
            ShowStatus("Listing remote...");
            lvRemote.Items.Clear();

            string batch = $"ls -l {QuoteSftpPath(_remotePath)}\n";
            string output = await RunSftpBatchAsync(batch);

            var items = ParseSftpLs(output);

            // Додаємо "..", якщо не корінь
            if (_remotePath != "/")
            {
                var parentItem = new ListViewItem("..");
                parentItem.SubItems.Add("");
                parentItem.SubItems.Add("Up");
                parentItem.Tag = new ItemTag { IsDir = true, FullPath = GetParentRemotePath(_remotePath) };
                lvRemote.Items.Add(parentItem);
            }

            foreach (var it in items)
            {
                var lvi = new ListViewItem(it.Name);
                lvi.SubItems.Add(it.IsDir ? "" : FormatSize(it.Size));
                lvi.SubItems.Add(it.Owner);
                lvi.SubItems.Add(it.Group);
                lvi.SubItems.Add(it.Date);
                lvi.SubItems.Add(it.Permissions);
                lvi.Tag = new ItemTag
                {
                    IsDir = it.IsDir,
                    FullPath = CombineRemotePath(_remotePath, it.Name)
                };
                lvRemote.Items.Add(lvi);
            }

            txtRemotePath.Text = _remotePath;
            ShowStatus($"Remote: {_remotePath}");
        }
        catch (Exception ex)
        {
            ShowStatus("Remote error: " + ex.Message);
        }
    }

    private async Task RemoteItemDoubleClickAsync()
    {
        if (lvRemote.SelectedItems.Count == 0) return;
        var lvi = lvRemote.SelectedItems[0];
        var tag = lvi.Tag as ItemTag;
        if (lvi.Text == ".." && tag != null)
        {
            _remotePath = tag.FullPath;
            await RefreshRemoteAsync();
            return;
        }

        if (tag == null || !tag.IsDir) return;

        _remotePath = tag.FullPath;
        await RefreshRemoteAsync();
    }

    // ---------------- LOCAL ----------------

    private void RefreshLocal()
    {
        try
        {
            ShowStatus("Listing local...");
            lvLocal.Items.Clear();

            if (!Directory.Exists(_localPath))
            {
                ShowStatus("Local path does not exist.");
                return;
            }

            // ".." якщо не корінь
            string root = Path.GetPathRoot(_localPath) ?? _localPath;
            if (!string.Equals(_localPath.TrimEnd(Path.DirectorySeparatorChar),
                               root.TrimEnd(Path.DirectorySeparatorChar),
                               StringComparison.OrdinalIgnoreCase))
            {
                string parent = Directory.GetParent(_localPath)?.FullName ?? _localPath;
                var up = new ListViewItem("..");
                up.SubItems.Add("");
                up.SubItems.Add("Up");
                up.Tag = new ItemTag { IsDir = true, FullPath = parent };
                lvLocal.Items.Add(up);
            }

            foreach (var dir in Directory.GetDirectories(_localPath))
            {
                var name = Path.GetFileName(dir);
                var lvi = new ListViewItem(name);
                lvi.SubItems.Add("");
                lvi.SubItems.Add("Dir");
                lvi.Tag = new ItemTag { IsDir = true, FullPath = dir };
                lvLocal.Items.Add(lvi);
            }

            foreach (var file in Directory.GetFiles(_localPath))
            {
                var fi = new FileInfo(file);
                var lvi = new ListViewItem(fi.Name);
                lvi.SubItems.Add(FormatSize(fi.Length));
                lvi.SubItems.Add("File");
                lvi.Tag = new ItemTag { IsDir = false, FullPath = file };
                lvLocal.Items.Add(lvi);
            }

            txtLocalPath.Text = _localPath;
            ShowStatus($"Local: {_localPath}");
        }
        catch (Exception ex)
        {
            ShowStatus("Local error: " + ex.Message);
        }
    }

    private void LocalItemDoubleClick()
    {
        if (lvLocal.SelectedItems.Count == 0) return;
        var lvi = lvLocal.SelectedItems[0];
        var tag = lvi.Tag as ItemTag;
        if (lvi.Text == ".." && tag != null)
        {
            _localPath = tag.FullPath;
            RefreshLocal();
            return;
        }

        if (tag == null || !tag.IsDir) return;

        _localPath = tag.FullPath;
        RefreshLocal();
    }

    // ---------------- COPY / MKDIR ----------------

    private async Task CopyFromRemoteAsync()
    {
        if (lvRemote.SelectedItems.Count == 0)
        {
            ShowStatus("Select remote file/dir first.");
            return;
        }
        var sel = lvRemote.SelectedItems[0];
        if (sel.Text == "..")
        {
            ShowStatus("Cannot copy \"..\".");
            return;
        }

        var tag = sel.Tag as ItemTag;
        if (tag == null)
        {
            ShowStatus("No remote item tag.");
            return;
        }

        try
        {
            ShowStatus($"Copying from remote: {tag.FullPath}");
            var sb = new StringBuilder();
            sb.AppendLine($"lcd {QuoteLocalPath(_localPath)}");
            if (tag.IsDir)
                sb.AppendLine($"get -r {QuoteSftpPath(tag.FullPath)}");
            else
                sb.AppendLine($"get {QuoteSftpPath(tag.FullPath)}");

            string cmd = sb.ToString();
            await RunSftpBatchAsync(cmd);

            RefreshLocal();
            ShowStatus("Copy from remote done.");
        }
        catch (Exception ex)
        {
            ShowStatus("Copy from remote error: " + ex.Message);
        }
    }

    private async Task CopyFromLocalAsync()
    {
        if (lvLocal.SelectedItems.Count == 0)
        {
            ShowStatus("Select local file/dir first.");
            return;
        }
        var sel = lvLocal.SelectedItems[0];
        if (sel.Text == "..")
        {
            ShowStatus("Cannot copy \"..\".");
            return;
        }

        var tag = sel.Tag as ItemTag;
        if (tag == null)
        {
            ShowStatus("No local item tag.");
            return;
        }

        try
        {
            ShowStatus($"Copying from local: {tag.FullPath}");
            var sb = new StringBuilder();
            sb.AppendLine($"cd {QuoteSftpPath(_remotePath)}");
            if (tag.IsDir)
                sb.AppendLine($"put -r {QuoteLocalPath(tag.FullPath)}");
            else
                sb.AppendLine($"put {QuoteLocalPath(tag.FullPath)}");

            await RunSftpBatchAsync(sb.ToString());

            await RefreshRemoteAsync();
            ShowStatus("Copy from local done.");
        }
        catch (Exception ex)
        {
            ShowStatus("Copy from local error: " + ex.Message);
        }
    }

    private async Task CreateFolderAsync()
    {
        if (_activeSide == PanelSide.Remote)
        {
            string? name = Prompt("New remote folder name:", "mkdir");
            if (string.IsNullOrWhiteSpace(name)) return;

            string remoteFull = CombineRemotePath(_remotePath, name.Trim());
            try
            {
                ShowStatus("Creating remote folder...");
                string batch = $"mkdir {QuoteSftpPath(remoteFull)}\n";
                await RunSftpBatchAsync(batch);
                await RefreshRemoteAsync();
                ShowStatus("Remote folder created.");
            }
            catch (Exception ex)
            {
                ShowStatus("Remote mkdir error: " + ex.Message);
            }
        }
        else
        {
            string? name = Prompt("New local folder name:", "mkdir");
            if (string.IsNullOrWhiteSpace(name)) return;

            string localFull = Path.Combine(_localPath, name.Trim());
            try
            {
                Directory.CreateDirectory(localFull);
                RefreshLocal();
                ShowStatus("Local folder created.");
            }
            catch (Exception ex)
            {
                ShowStatus("Local mkdir error: " + ex.Message);
            }
        }
    }

    private async Task DeleteAsync()
    {
        if (_activeSide == PanelSide.Remote)
        {
            if (lvRemote.SelectedItems.Count == 0)
            {
                ShowStatus("Select remote item to delete.");
                return;
            }

            var sel = lvRemote.SelectedItems[0];
            if (sel.Text == "..")
            {
                ShowStatus("Cannot delete \"..\".");
                return;
            }

            var tag = sel.Tag as ItemTag;
            if (tag == null)
            {
                ShowStatus("No remote item tag.");
                return;
            }

            var confirm = MessageBox.Show(
                this,
                $"Delete remote {(tag.IsDir ? "directory" : "file")}?\n{tag.FullPath}",
                "Confirm delete",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes)
                return;

            try
            {
                ShowStatus("Deleting remote...");

                string cmd = tag.IsDir
                    ? $"rmdir {QuoteSftpPath(tag.FullPath)}\n"
                    : $"rm {QuoteSftpPath(tag.FullPath)}\n";

                await RunSftpBatchAsync(cmd);
                await RefreshRemoteAsync();

                ShowStatus("Remote delete done.");
            }
            catch (Exception ex)
            {
                ShowStatus("Remote delete error: " + ex.Message);
            }
        }
        else // Local
        {
            if (lvLocal.SelectedItems.Count == 0)
            {
                ShowStatus("Select local item to delete.");
                return;
            }

            var sel = lvLocal.SelectedItems[0];
            if (sel.Text == "..")
            {
                ShowStatus("Cannot delete \"..\".");
                return;
            }

            var tag = sel.Tag as ItemTag;
            if (tag == null)
            {
                ShowStatus("No local item tag.");
                return;
            }

            var confirm = MessageBox.Show(
                this,
                $"Delete local {(tag.IsDir ? "directory" : "file")}?\n{tag.FullPath}",
                "Confirm delete",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes)
                return;

            try
            {
                ShowStatus("Deleting local...");

                if (tag.IsDir)
                {
                    Directory.Delete(tag.FullPath, recursive: true);
                }
                else
                {
                    File.Delete(tag.FullPath);
                }

                RefreshLocal();
                ShowStatus("Local delete done.");
            }
            catch (Exception ex)
            {
                ShowStatus("Local delete error: " + ex.Message);
            }
        }
    }

    // ---------------- SFTP WRAPPER ----------------

    private async Task<string> RunSftpBatchAsync(string batchCommands)
    {
        string tempFile = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(tempFile, batchCommands, Encoding.UTF8);

            var psi = new ProcessStartInfo
            {
                FileName = "sftp",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-P");
            psi.ArgumentList.Add(_sftpPort.ToString());
            psi.ArgumentList.Add("-b");
            psi.ArgumentList.Add(tempFile);
            psi.ArgumentList.Add($"{_sftpUser}@{_sftpHost}");

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            string stdout = await proc.StandardOutput.ReadToEndAsync();
            string stderr = await proc.StandardError.ReadToEndAsync();

            proc.WaitForExit();

            if (proc.ExitCode != 0)
                throw new Exception($"sftp exit {proc.ExitCode}: {stderr}");

            return stdout;
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
            catch
            {
                // ignore delete error
            }
        }
    }

    private record SftpItem(
        string Name,
        bool IsDir,
        long Size,
        string Permissions,
        string Owner,
        string Group,
        string Date
    );

    private List<SftpItem> ParseSftpLs(string output)
    {
        var list = new List<SftpItem>();
        using var reader = new StringReader(output);
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length < 10) continue;

            char c0 = line[0];
            if (c0 != 'd' && c0 != '-') continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 9) continue;

            string perms = parts[0];
            bool isDir = perms.StartsWith("d", StringComparison.OrdinalIgnoreCase);

            string owner = parts[2];
            string group = parts[3];

            long size = 0;
            long.TryParse(parts[4], out size);

            string date = string.Join(' ', parts[5], parts[6], parts[7]);

            string rawName = string.Join(' ', parts.Skip(8)); // може бути "appmgmt" або "/opt/appmgmt"

            string name = rawName.Trim();
            if (name.StartsWith("/"))
            {
                // /opt/appmgmt → appmgmt
                name = Path.GetFileName(name);
            }
            if (string.IsNullOrEmpty(name)) continue;

            list.Add(new SftpItem(name, isDir, size, perms, owner, group, date));
        }

        return list;
    }


    // ---------------- HELPERS ----------------

    private string NormalizeRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        path = path.Trim().Replace('\\', '/');

        if (path.Any(char.IsControl))
            throw new ArgumentException("Path cannot contain control characters.", nameof(path));
            
        if (!path.StartsWith("/"))
            path = "/" + path;

        while (path.Contains("//"))
            path = path.Replace("//", "/");

        if (path.Length > 1 && path.EndsWith("/"))
            path = path.TrimEnd('/');

        return path;
    }

    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    private string CombineRemotePath(string basePath, string name)
    {
        basePath = NormalizeRemotePath(basePath);
        if (string.IsNullOrWhiteSpace(name))
            return basePath;

        if (name.StartsWith("/"))
            return NormalizeRemotePath(name);

        if (basePath == "/")
            return "/" + name;

        return basePath + "/" + name;
    }

    private static string QuoteSftpPath(string path)
    {
        if (path == null)
            throw new ArgumentNullException(nameof(path));

        if (path.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            throw new ArgumentException("Path cannot contain newline characters.", nameof(path));

        string normalized = path.Replace("\\", "/");
        string escaped = normalized.Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    private static string QuoteLocalPath(string path)
    {
        if (path == null)
            throw new ArgumentNullException(nameof(path));

        if (path.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            throw new ArgumentException("Path cannot contain newline characters.", nameof(path));

        string escaped = path.Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    private static string EnsureSshIdentifier(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be empty.", paramName);

        foreach (char ch in value)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
                throw new ArgumentException("Value cannot contain whitespace or control characters.", paramName);
        }

        return value;
    }

    private string GetParentRemotePath(string path)
    {
        path = NormalizeRemotePath(path);
        if (path == "/") return "/";
        string trimmed = path.TrimEnd('/');
        int idx = trimmed.LastIndexOf('/');
        if (idx <= 0) return "/";
        return trimmed.Substring(0, idx);
    }

    private void ShowStatus(string msg)
    {
        lblStatus.Text = msg;
    }

    private string? Prompt(string text, string caption)
    {
        var form = new Form
        {
            Width = 400,
            Height = 140,
            Text = caption,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false
        };

        var lbl = new Label { Left = 10, Top = 07, Width = 360, Text = text };
        var tb = new TextBox { Left = 10, Top = 30, Width = 360 };
        var btnOk = new Button { Text = "OK", Left = 220, Width = 70, Top = 60, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "Cancel", Left = 300, Width = 70, Top = 60, DialogResult = DialogResult.Cancel };

        form.Controls.Add(lbl);
        form.Controls.Add(tb);
        form.Controls.Add(btnOk);
        form.Controls.Add(btnCancel);

        form.AcceptButton = btnOk;
        form.CancelButton = btnCancel;

        return form.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
    }
}
