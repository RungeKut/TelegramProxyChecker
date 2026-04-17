using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TelegramProxyChecker
{
    public enum ProxyType { MTProto, HTTP, SOCKS5 }
    public enum ProxyStatus { Unknown, Online, Offline }

    public class ProxyInfo
    {
        public ProxyType Type { get; set; }
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public string Secret { get; set; } = string.Empty;
        public ProxyStatus Status { get; set; } = ProxyStatus.Unknown;
        public int Latency { get; set; } = -1;
        public DateTime LastCheck { get; set; }
        public DateTime DateAdded { get; set; }
        public int Frequency { get; set; } = 1000;
        public DateTime LastOnline { get; set; } // 🔹 Новое поле

        public string GetTelegramLink() => $"https://t.me/proxy?server={Host}&port={Port}&secret={Secret}";
    }

    public partial class MainForm : Form
    {
        // --- UI Elements ---
        private DataGridView dgv;
        private ComboBox cmbType;
        private NumericUpDown nudPort;
        private TextBox txtHost, txtSecret, txtLink, txtSearch;
        private Button btnAdd, btnAddLink, btnImportHtml, btnRemove, btnCheck, btnStop;
        private CheckBox chkHideOnline, chkHideOffline, chkHideUnknown, chkAutoCheck;
        private ProgressBar pbProgress;
        private Label lblStatus;
        private ContextMenuStrip cmsGrid;

        // --- Logic Variables ---
        private List<ProxyInfo> proxies = new();
        private const string JsonFile = "proxies.json";
        private CancellationTokenSource _cts;
        private bool _isChecking = false;
        private readonly System.Windows.Forms.Timer _autoCheckTimer = new System.Windows.Forms.Timer { Interval = 30000 };

        // 🔹 Состояние сортировки
        private int _sortColumn = 7; // По умолчанию DateAdded
        private bool _sortDescending = true;

        public MainForm()
        {
            InitializeComponent();
            LoadProxies();

            _autoCheckTimer.Tick += async (s, e) =>
            {
                if (chkAutoCheck.Checked && !_isChecking)
                    await CheckAllAsync(respectFrequency: true);
                else if (chkAutoCheck.Checked && _isChecking)
                    lblStatus.Text = "⏳ Пропуск тика: предыдущая проверка ещё выполняется...";
            };
        }

        private void InitializeComponent()
        {
            this.Text = "Telegram Proxy Checker";
            this.Size = new System.Drawing.Size(1200, 700);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MinimumSize = new System.Drawing.Size(900, 500);

            // --- DataGridView Setup ---
            dgv = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                AllowUserToResizeColumns = true,
                ReadOnly = false,
                VirtualMode = false,
                //AllowUserToSortColumns = false // Отключаем встроенную сортировку грида, используем свою
            };
            dgv.Columns.Add("Type", "Type");
            dgv.Columns.Add("Host", "Host / Server");
            dgv.Columns.Add("Port", "Port");
            dgv.Columns.Add("Secret", "Secret");
            dgv.Columns.Add("Status", "Status");
            dgv.Columns.Add("Latency", "Ping (ms)");
            dgv.Columns.Add("LastCheck", "Last Check");
            dgv.Columns.Add("DateAdded", "Date Added");
            dgv.Columns.Add("Frequency", "Frequency");
            dgv.Columns.Add("LastOnline", "Last Online"); // 🔹 Новый столбец

            dgv.Columns[1].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            dgv.Columns[8].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            dgv.Columns[9].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            dgv.Columns[8].ToolTipText = "0 = Никогда, 1000 = Всегда";

            foreach (DataGridViewColumn col in dgv.Columns) col.ReadOnly = true;
            dgv.Columns["Frequency"].ReadOnly = false;

            // 🔹 Сортировка по клику на заголовок
            dgv.ColumnHeaderMouseClick += Dgv_ColumnHeaderMouseClick;

            // --- Click to Open Link ---
            dgv.CellClick += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                var p = dgv.Rows[e.RowIndex].Tag as ProxyInfo;
                if (p != null && e.ColumnIndex == 1)
                    try { Process.Start(new ProcessStartInfo(p.GetTelegramLink()) { UseShellExecute = true }); } catch { }
            };

            // --- Edit Frequency ---
            dgv.CellEndEdit += (s, e) =>
            {
                if (e.ColumnIndex == dgv.Columns["Frequency"].Index && e.RowIndex >= 0)
                {
                    var p = dgv.Rows[e.RowIndex].Tag as ProxyInfo;
                    if (p != null && int.TryParse(dgv[e.RowIndex, e.ColumnIndex].Value?.ToString(), out int val))
                    {
                        p.Frequency = Math.Clamp(val, 0, 1000);
                        dgv[e.RowIndex, e.ColumnIndex].Value = p.Frequency;
                        SaveProxies();
                    }
                }
            };

            // --- Context Menu (Check Selected, Copy, etc) ---
            cmsGrid = new ContextMenuStrip();
            cmsGrid.Items.Add("🔍 Check Selected", null, (s, e) => CheckSelectedProxiesAsync());
            cmsGrid.Items.Add(new ToolStripSeparator());
            cmsGrid.Items.Add("🔗 Copy Link", null, (s, e) => CopySelectedLink());
            cmsGrid.Items.Add("📋 Copy All Links", null, (s, e) => CopyAllLinks());
            cmsGrid.Opening += (s, e) => e.Cancel = dgv.CurrentRow == null || proxies.Count == 0;
            dgv.ContextMenuStrip = cmsGrid;

            // --- Panel 1: Inputs ---
            var panel1 = new Panel { Dock = DockStyle.Top, Height = 55 };
            cmbType = new ComboBox { Left = 10, Top = 10, Width = 90 };
            cmbType.Items.AddRange(new[] { "MTProto", "HTTP", "SOCKS5" });
            cmbType.SelectedIndex = 0;
            txtHost = new TextBox { Left = 110, Top = 10, Width = 160 }; txtHost.PlaceholderText = "Host / IP";
            nudPort = new NumericUpDown { Left = 280, Top = 10, Width = 60, Minimum = 1, Maximum = 65535, Value = 443 };
            txtSecret = new TextBox { Left = 350, Top = 10, Width = 130 }; txtSecret.PlaceholderText = "Secret";
            btnAdd = new Button { Left = 490, Top = 10, Width = 60, Text = "Add" }; btnAdd.Click += BtnAdd_Click;
            txtLink = new TextBox { Left = 560, Top = 10, Width = 250 }; txtLink.PlaceholderText = "https://t.me/proxy?server=...";
            btnAddLink = new Button { Left = 820, Top = 10, Width = 90, Text = "Add Link" }; btnAddLink.Click += BtnAddLink_Click;
            btnRemove = new Button { Left = 920, Top = 10, Width = 70, Text = "Remove" }; btnRemove.Click += BtnRemove_Click;
            panel1.Controls.AddRange(new Control[] { cmbType, txtHost, nudPort, txtSecret, btnAdd, txtLink, btnAddLink, btnRemove });

            // --- Panel 2: Controls & Search ---
            var panel2 = new Panel { Dock = DockStyle.Top, Height = 35 };
            btnImportHtml = new Button { Left = 10, Top = 7, Width = 100, Text = "Import HTML" }; btnImportHtml.Click += BtnImportHtml_Click;
            btnCheck = new Button { Left = 120, Top = 7, Width = 90, Text = "Check All" }; btnCheck.Click += async (s, e) => await CheckAllAsync(respectFrequency: false);
            btnStop = new Button { Left = 220, Top = 7, Width = 70, Text = "Stop", Enabled = false, BackColor = System.Drawing.Color.LightCoral }; btnStop.Click += BtnStop_Click;
            chkHideOnline = new CheckBox { Left = 305, Top = 7, AutoSize = true, Text = "Hide Online" };
            chkHideOffline = new CheckBox { Left = 395, Top = 7, AutoSize = true, Text = "Hide Offline" };
            chkHideUnknown = new CheckBox { Left = 490, Top = 7, AutoSize = true, Text = "Hide Unknown" };
            chkAutoCheck = new CheckBox { Left = 595, Top = 7, AutoSize = true, Text = "Auto Check (30s)" };

            // 🔹 Search Box
            txtSearch = new TextBox { Left = 720, Top = 7, Width = 150 };
            txtSearch.PlaceholderText = "Search Host...";
            txtSearch.TextChanged += (s, e) => RefreshGrid();

            chkHideOnline.CheckedChanged += (s, e) => RefreshGrid();
            chkHideOffline.CheckedChanged += (s, e) => RefreshGrid();
            chkHideUnknown.CheckedChanged += (s, e) => RefreshGrid();
            chkAutoCheck.CheckedChanged += (s, e) => { if (chkAutoCheck.Checked) _autoCheckTimer.Start(); else _autoCheckTimer.Stop(); };
            panel2.Controls.AddRange(new Control[] { btnImportHtml, btnCheck, btnStop, chkHideOnline, chkHideOffline, chkHideUnknown, chkAutoCheck, txtSearch });

            pbProgress = new ProgressBar { Dock = DockStyle.Bottom, Height = 15, Visible = false };
            lblStatus = new Label { Dock = DockStyle.Bottom, Height = 20, Text = "Готово. Добавьте или импортируйте прокси, затем нажмите Check All." };
            this.Controls.AddRange(new Control[] { dgv, panel1, panel2, pbProgress, lblStatus });
        }

        // 🔹 Метод сортировки
        private void Dgv_ColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex < 0 || e.ColumnIndex >= dgv.Columns.Count) return;

            // Переключаем направление, если кликнули по тому же столбцу
            if (_sortColumn == e.ColumnIndex) _sortDescending = !_sortDescending;
            else { _sortColumn = e.ColumnIndex; _sortDescending = true; }

            ApplySorting();
            lblStatus.Text = $"🔃 Сортировка: {dgv.Columns[e.ColumnIndex].HeaderText} ({(_sortDescending ? "↓" : "↑")})";
        }

        private void ApplySorting()
        {
            // Сортировка работает напрямую с типами (DateTime, int, string), поэтому 
            // даты сравниваются хронологически, а числа - математически.
            proxies = _sortColumn switch
            {
                0 => _sortDescending ? proxies.OrderByDescending(p => p.Type).ToList() : proxies.OrderBy(p => p.Type).ToList(),
                1 => _sortDescending ? proxies.OrderByDescending(p => p.Host, StringComparer.OrdinalIgnoreCase).ToList() : proxies.OrderBy(p => p.Host, StringComparer.OrdinalIgnoreCase).ToList(),
                2 => _sortDescending ? proxies.OrderByDescending(p => p.Port).ToList() : proxies.OrderBy(p => p.Port).ToList(),
                3 => _sortDescending ? proxies.OrderByDescending(p => p.Secret, StringComparer.OrdinalIgnoreCase).ToList() : proxies.OrderBy(p => p.Secret, StringComparer.OrdinalIgnoreCase).ToList(),
                4 => _sortDescending ? proxies.OrderByDescending(p => p.Status).ToList() : proxies.OrderBy(p => p.Status).ToList(),
                5 => _sortDescending ? proxies.OrderByDescending(p => p.Latency).ToList() : proxies.OrderBy(p => p.Latency).ToList(),
                6 => _sortDescending ? proxies.OrderByDescending(p => p.LastCheck).ToList() : proxies.OrderBy(p => p.LastCheck).ToList(),
                7 => _sortDescending ? proxies.OrderByDescending(p => p.DateAdded).ToList() : proxies.OrderBy(p => p.DateAdded).ToList(),
                8 => _sortDescending ? proxies.OrderByDescending(p => p.Frequency).ToList() : proxies.OrderBy(p => p.Frequency).ToList(),
                9 => _sortDescending ? proxies.OrderByDescending(p => p.LastOnline).ToList() : proxies.OrderBy(p => p.LastOnline).ToList(),
                _ => proxies
            };
            RefreshGrid();
        }

        private void LoadProxies()
        {
            if (File.Exists(JsonFile))
            {
                try
                {
                    var json = File.ReadAllText(JsonFile);
                    proxies = JsonSerializer.Deserialize<List<ProxyInfo>>(json) ?? new List<ProxyInfo>();
                    foreach (var p in proxies)
                    {
                        if (p.DateAdded == default) p.DateAdded = DateTime.Now;
                        if (p.Frequency <= 0) p.Frequency = 1000;
                    }
                }
                catch { proxies = new List<ProxyInfo>(); }
            }
            RemoveDuplicates();
            ApplySorting(); // Применяем сортировку при загрузке
        }

        private void SaveProxies()
        {
            var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
            File.WriteAllText(JsonFile, JsonSerializer.Serialize(proxies, options));
        }

        private void RefreshGrid()
        {
            dgv.SuspendLayout();
            dgv.Rows.Clear();

            string filter = txtSearch?.Text?.Trim() ?? "";
            bool hasFilter = !string.IsNullOrEmpty(filter);

            foreach (var p in proxies)
            {
                if (p.Status == ProxyStatus.Online && chkHideOnline.Checked) continue;
                if (p.Status == ProxyStatus.Offline && chkHideOffline.Checked) continue;
                if (p.Status == ProxyStatus.Unknown && chkHideUnknown.Checked) continue;
                if (hasFilter && !p.Host.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

                var row = new DataGridViewRow();
                row.CreateCells(dgv,
                    p.Type.ToString(),
                    p.Host,
                    p.Port,
                    p.Secret,
                    p.Status.ToString(),
                    p.Latency >= 0 ? p.Latency.ToString() : "-",
                    p.LastCheck == default ? "-" : p.LastCheck.ToString("dd.MM.yyyy HH:mm"),
                    p.DateAdded.ToString("dd.MM.yyyy HH:mm"),
                    p.Frequency,
                    p.LastOnline == default ? "-" : p.LastOnline.ToString("dd.MM.yyyy HH:mm"));
                row.Tag = p;
                dgv.Rows.Add(row);
            }
            dgv.ResumeLayout();
        }

        private void RemoveDuplicates()
        {
            var seen = new Dictionary<string, ProxyInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in proxies)
            {
                var key = $"{p.Host}:{p.Port}:{p.Secret}";
                if (!seen.TryGetValue(key, out var existing) || p.Frequency > existing.Frequency)
                    seen[key] = p;
            }
            proxies = seen.Values.ToList();
        }

        private void BtnAdd_Click(object sender, EventArgs e)
        {
            var host = txtHost.Text.Trim();
            if (string.IsNullOrWhiteSpace(host)) return;
            proxies.Add(new ProxyInfo { Type = Enum.TryParse(cmbType.SelectedItem.ToString(), out ProxyType t) ? t : ProxyType.MTProto, Host = host, Port = (int)nudPort.Value, Secret = txtSecret.Text.Trim(), Status = ProxyStatus.Unknown, DateAdded = DateTime.Now, Frequency = 1000 });
            txtHost.Clear(); txtSecret.Clear(); RemoveDuplicates(); SaveProxies(); RefreshGrid();
            lblStatus.Text = "Прокси добавлен.";
        }

        private void BtnAddLink_Click(object sender, EventArgs e)
        {
            var link = txtLink.Text.Trim();
            if (string.IsNullOrWhiteSpace(link)) return;
            if (TryParseProxyLink(link, out string host, out int port, out string secret))
            {
                proxies.Add(new ProxyInfo { Type = ProxyType.MTProto, Host = host, Port = port, Secret = secret, Status = ProxyStatus.Unknown, DateAdded = DateTime.Now, Frequency = 1000 });
                txtLink.Clear(); RemoveDuplicates(); SaveProxies(); RefreshGrid(); lblStatus.Text = "Прокси добавлен из ссылки.";
            }
            else MessageBox.Show("Не удалось распознать ссылку.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private static bool TryParseProxyLink(string link, out string host, out int port, out string secret)
        {
            host = null; port = 0; secret = null;
            try { var m = Regex.Match(link, @"[?&]server=([^&\s]+)&port=(\d+)&secret=([^&\s]+)", RegexOptions.IgnoreCase); if (m.Success) { host = Uri.UnescapeDataString(m.Groups[1].Value); port = int.Parse(m.Groups[2].Value); secret = Uri.UnescapeDataString(m.Groups[3].Value); return true; } } catch { }
            return false;
        }

        private async void BtnImportHtml_Click(object sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog { Filter = "HTML Files|*.html;*.htm|All Files|*.*", Multiselect = true, Title = "Select Telegram HTML Exports" };
            if (ofd.ShowDialog() != DialogResult.OK) return;

            lblStatus.Text = "Парсинг HTML..."; this.Cursor = Cursors.WaitCursor;
            await Task.Run(() =>
            {
                var dateRegex = new Regex(@"(\d{1,2})\s+(January|February|March|April|May|June|July|August|September|October|November|December)\s+(\d{4})", RegexOptions.IgnoreCase);
                var timeRegex = new Regex(@"(\d{1,2}:\d{2})");
                var proxyRegex = new Regex(@"Server:\s*([^\s<]+)\s+Port:\s*(\d+)\s+Secret:\s*([^\s<]+)", RegexOptions.IgnoreCase);
                lock (proxies)
                {
                    foreach (var file in ofd.FileNames)
                    {
                        try
                        {
                            var html = File.ReadAllText(file);
                            var text = Regex.Replace(html, "<[^>]+>", " "); text = Regex.Replace(text, @"\s+", " ");
                            var matches = new List<(int Index, string Type, Match M)>();
                            matches.AddRange(dateRegex.Matches(text).Cast<Match>().Select(m => (m.Index, "Date", m)));
                            matches.AddRange(timeRegex.Matches(text).Cast<Match>().Select(m => (m.Index, "Time", m)));
                            matches.AddRange(proxyRegex.Matches(text).Cast<Match>().Select(m => (m.Index, "Proxy", m)));
                            matches.Sort((a, b) => a.Index.CompareTo(b.Index));

                            DateTime? currentDate = null; TimeSpan? currentTime = null;

                            foreach (var m in matches)
                            {
                                if (m.Type == "Date" && DateTime.TryParseExact(m.M.Value, "d MMMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d)) currentDate = d;
                                else if (m.Type == "Time" && TimeSpan.TryParse(m.M.Value, out TimeSpan t)) currentTime = t;
                                else if (m.Type == "Proxy")
                                {
                                    DateTime added = DateTime.Now;
                                    if (currentDate.HasValue)
                                    {
                                        added = currentDate.Value.Date;
                                        if (currentTime.HasValue) added += currentTime.Value;
                                        else added += TimeSpan.FromHours(12);
                                    }
                                    proxies.Add(new ProxyInfo { Type = ProxyType.MTProto, Host = m.M.Groups[1].Value.TrimEnd('.', ',', ';'), Port = int.Parse(m.M.Groups[2].Value), Secret = m.M.Groups[3].Value.TrimEnd('.', ',', ';'), Status = ProxyStatus.Unknown, DateAdded = added, Frequency = 1000 });
                                }
                            }
                        }
                        catch { }
                    }
                }
            });
            RemoveDuplicates(); SaveProxies(); RefreshGrid(); this.Cursor = Cursors.Default;
            lblStatus.Text = $"Импорт завершён. Всего прокси: {proxies.Count}";
        }

        private void BtnRemove_Click(object sender, EventArgs e)
        {
            if (dgv.SelectedRows.Count == 0) return;
            var toRemove = dgv.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag as ProxyInfo).Where(p => p != null).ToList();
            foreach (var p in toRemove) proxies.Remove(p);
            SaveProxies(); RefreshGrid(); lblStatus.Text = $"Удалено: {toRemove.Count}";
        }

        private void BtnStop_Click(object sender, EventArgs e) { _cts?.Cancel(); lblStatus.Text = "⛔ Остановка..."; }

        // 🔹 CHECK SELECTED
        private async Task CheckSelectedProxiesAsync()
        {
            var selected = dgv.SelectedRows.Cast<DataGridViewRow>()
                .Select(r => r.Tag as ProxyInfo)
                .Where(p => p != null).ToList();
            if (!selected.Any()) { lblStatus.Text = "⚠️ Выделите строки для проверки."; return; }

            btnCheck.Enabled = false; btnStop.Enabled = true; this.Cursor = Cursors.WaitCursor;
            lblStatus.Text = $"⏳ Проверка {selected.Count} выбранных прокси...";

            await Task.WhenAll(selected.Select(async p =>
            {
                var (online, lat, _) = await CheckProxyAsync(p.Host, p.Port, p.Secret, 3000, CancellationToken.None);
                p.Status = online ? ProxyStatus.Online : ProxyStatus.Offline;
                p.Latency = lat; p.LastCheck = DateTime.Now;
                p.Frequency = online ? Math.Min(1000, p.Frequency + 1) : Math.Max(0, p.Frequency - 1);
                if (online) p.LastOnline = DateTime.Now; // 🔹 Обновляем дату последнего онлайна
            }));

            ApplySorting(); SaveProxies();
            btnCheck.Enabled = true; btnStop.Enabled = false; this.Cursor = Cursors.Default;
            lblStatus.Text = $"✅ Проверка выбранных завершена. Онлайн: {selected.Count(p => p.Status == ProxyStatus.Online)} из {selected.Count}.";
        }

        private async Task CheckAllAsync(bool respectFrequency = false)
        {
            if (_isChecking || proxies.Count == 0) return;

            RemoveDuplicates();
            _isChecking = true;
            _cts = new CancellationTokenSource();
            btnCheck.Enabled = false; btnStop.Enabled = true;
            pbProgress.Visible = true; pbProgress.Value = 0; pbProgress.Maximum = proxies.Count;
            this.Cursor = Cursors.WaitCursor;

            int onlineCount = 0;
            int checkedCount = 0;
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 100, CancellationToken = _cts.Token };

            try
            {
                await Parallel.ForEachAsync(proxies, parallelOptions, async (p, ct) =>
                {
                    if (ct.IsCancellationRequested) return;

                    if (respectFrequency)
                    {
                        if (p.Frequency == 0) return;
                        if (Random.Shared.Next(1000) >= p.Frequency) return;
                    }

                    var (online, lat, _) = await CheckProxyAsync(p.Host, p.Port, p.Secret, 3000, ct);
                    p.Status = online ? ProxyStatus.Online : ProxyStatus.Offline;
                    p.Latency = lat; p.LastCheck = DateTime.Now;
                    if (online)
                    {
                        Interlocked.Increment(ref onlineCount);
                        p.LastOnline = DateTime.Now; // 🔹 Обновляем дату последнего онлайна
                    }
                    p.Frequency = online ? Math.Min(1000, p.Frequency + 1) : Math.Max(0, p.Frequency - 1);

                    int count = Interlocked.Increment(ref checkedCount);
                    if (count % 50 == 0 || count == proxies.Count)
                    {
                        this.Invoke(() =>
                        {
                            pbProgress.Value = Math.Min(count, proxies.Count);
                            lblStatus.Text = $"⏳ Проверка: {count} / {proxies.Count} ({(double)count / proxies.Count * 100:F1}%) | Онлайн: {onlineCount}";
                        });
                    }
                });

                if (_cts.IsCancellationRequested) lblStatus.Text = "⛔ Проверка остановлена пользователем.";
                else
                {
                    ApplySorting(); SaveProxies();
                    lblStatus.Text = chkAutoCheck.Checked
                        ? $"✅ Готово. Авто-проверка активна. Онлайн: {onlineCount} из {proxies.Count}."
                        : $"✅ Готово. Онлайн: {onlineCount} из {proxies.Count}.";
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { lblStatus.Text = $"❌ Ошибка: {ex.Message}"; }
            finally { _isChecking = false; _cts.Dispose(); btnCheck.Enabled = true; btnStop.Enabled = false; pbProgress.Visible = false; this.Cursor = Cursors.Default; }
        }

        // 🔹 MTProto Handshake Check
        private static async Task<(bool IsOnline, int Latency, string? Error)> CheckProxyAsync(string host, int port, string secret, int timeoutMs, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var tcp = new TcpClient(AddressFamily.InterNetwork);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);

                await tcp.ConnectAsync(host, port).WaitAsync(cts.Token);
                using var stream = tcp.GetStream();
                stream.ReadTimeout = timeoutMs;
                stream.WriteTimeout = timeoutMs;

                byte[] secretBytes = new byte[32];
                if (!string.IsNullOrEmpty(secret))
                {
                    string hex = Regex.Replace(secret, @"[^0-9a-fA-F]", "");
                    int len = Math.Min(32, hex.Length / 2);
                    for (int i = 0; i < len; i++)
                        secretBytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                }

                byte[] prefix = (port == 443) ? new byte[] { 0xdd, 0xdd, 0xdd, 0xdd } : new byte[] { 0xee, 0xee, 0xee, 0xee };
                byte[] handshake = new byte[40];
                Array.Copy(prefix, 0, handshake, 0, 4);
                Array.Copy(secretBytes, 0, handshake, 4, 32);

                await stream.WriteAsync(handshake, 0, handshake.Length, cts.Token);
                await stream.FlushAsync(cts.Token);

                byte[] buffer = new byte[4];
                int read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);

                sw.Stop();
                return (read > 0, (int)sw.ElapsedMilliseconds, null);
            }
            catch (OperationCanceledException) { sw.Stop(); return (false, -1, "Timeout"); }
            catch (IOException) when (sw.ElapsedMilliseconds < timeoutMs) { sw.Stop(); return (true, (int)sw.ElapsedMilliseconds, "Protocol Mismatch"); }
            catch (IOException) { sw.Stop(); return (false, -1, "IO Error"); }
            catch (SocketException ex) { sw.Stop(); return (false, -1, ex.SocketErrorCode.ToString()); }
            catch (Exception ex) { sw.Stop(); return (false, -1, ex.Message); }
        }

        private void CopySelectedLink() { var p = dgv.CurrentRow?.Tag as ProxyInfo; if (p != null) Clipboard.SetText(p.GetTelegramLink()); }
        private void CopyAllLinks() { Clipboard.SetText(string.Join(Environment.NewLine, proxies.Select(p => p.GetTelegramLink()))); lblStatus.Text = "📋 Скопировано."; }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _cts?.Cancel();
            _autoCheckTimer.Stop();
            _autoCheckTimer.Dispose();
            base.OnFormClosing(e);
        }
    }

    static class Program
    {
        [STAThread]
        static void Main() { ApplicationConfiguration.Initialize(); Application.Run(new MainForm()); }
    }
}