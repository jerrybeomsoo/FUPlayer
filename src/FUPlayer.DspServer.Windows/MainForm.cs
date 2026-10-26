using FUPlayer.ServerHost;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FUPlayer.Core.Localization;

namespace FUPlayer.ServerController;

/// <summary>A Forms controller for its own portable listener; no models or GPU devices are opened by the form.</summary>
internal sealed class MainForm : Form
{
    private readonly ControllerSettingsStore _store;
    private readonly ServerProcess _server;
    private readonly string? _loadError;
    private readonly TextBox _listen = new(), _key = new(), _models = new(), _cache = new();
    private readonly NumericUpDown _port = Number(1024, 65535), _first = Number(1024, 65535), _last = Number(1024, 65535);
    private readonly NumericUpDown _clients = Number(1, 16), _threads = Number(0, 63);
    private readonly CheckBox _internet = new(), _precision = new(), _force = new(), _autoStart = new(), _closeToTray = new();
    private readonly ComboBox _gpu = Choices(), _restorer = Choices(), _upscaler = Choices(), _language = Choices();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly Button _start = new(), _stop = new(), _save = new();
    private readonly Label _state = Metric(), _clientCount = Metric(), _cpu = Metric(), _memory = Metric();
    private readonly Label _notice = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, WordWrap = true, Font = new Font("Consolas", 9) };
    private readonly ConcurrentQueue<string> _messages = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly NotifyIcon _tray;
    private ControllerStatus? _status;
    private bool _busy, _exitReady, _exiting, _quitFromTray, _hadRunningServer;
    private readonly TabPage _connection, _processing;

    internal MainForm(ControllerSettingsStore store, ServerSettings settings, ServerProcess server, string? loadError)
    {
        _store = store; _server = server; _loadError = loadError;
        Text = Loc.T("FUPlayer DSP server");
        Name = "DspServerController";
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(1000, 840);
        MinimumSize = new Size(860, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        BackColor = SystemColors.Window;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(24, 16, 24, 12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 16) };
        heading.Controls.Add(new Label { Text = Loc.T("FUPlayer DSP server"), Font = new Font(Font.FontFamily, 23, FontStyle.Bold), AutoSize = true, Margin = Padding.Empty });
        heading.Controls.Add(new Label { Text = Loc.T("Process audio for FUPlayer on another computer."), AutoSize = true, ForeColor = SystemColors.GrayText });
        root.Controls.Add(heading, 0, 0);
        var metrics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 0, 0, 12) };
        foreach (var pair in new[] { (Loc.T("Server"), _state), (Loc.T("Connected clients"), _clientCount), (Loc.T("Server CPU"), _cpu), (Loc.T("Server memory"), _memory) })
        {
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            var card = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = SystemColors.ControlLight };
            card.Controls.Add(new Label { Text = pair.Item1, AutoSize = true, ForeColor = SystemColors.GrayText });
            card.Controls.Add(pair.Item2); metrics.Controls.Add(card);
        }
        root.Controls.Add(metrics, 0, 1);
        _connection = new TabPage(Loc.T("Connection")) { BackColor = SystemColors.Window, AutoScroll = true, Padding = new Padding(14) };
        _processing = new TabPage(Loc.T("Processing")) { BackColor = SystemColors.Window, AutoScroll = true, Padding = new Padding(14) };
        _tabs.TabPages.AddRange([_connection, _processing, ActivityPage()]);
        BuildConnection(); BuildProcessing();
        root.Controls.Add(_tabs, 0, 2);
        _notice.Margin = new Padding(0, 12, 0, 8);
        root.Controls.Add(_notice, 0, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
        ConfigureButton(_start, Loc.T("Start server"), "StartServer", async () => await StartServer());
        ConfigureButton(_stop, Loc.T("Stop server"), "StopServer", async () => await StopServer());
        ConfigureButton(_save, Loc.T("Save settings"), "SaveSettings", () => { Save(); return Task.CompletedTask; });
        if (!SystemInformation.HighContrast) { _start.BackColor = Color.FromArgb(30, 102, 205); _start.ForeColor = Color.White; _start.FlatStyle = FlatStyle.Flat; }
        buttons.Controls.AddRange([_start, _stop, _save]);
        buttons.Controls.Add(Button(Loc.T("Open cache folder"), () => { OpenFolder(CachePath()); return Task.CompletedTask; }));
        buttons.Controls.Add(Button(Loc.T("Licences"), async () =>
        {
            string folder = Path.Combine(_store.DirectoryPath, "Licences");
            await _server.ExtractLicensesAsync(folder); OpenFolder(folder);
        }));
        root.Controls.Add(buttons, 0, 4);
        Controls.Add(root);

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add(Loc.T("Show controller"), null, (_, _) => RestoreWindow());
        trayMenu.Items.Add(Loc.T("Exit and stop server"), null, (_, _) => { _quitFromTray = true; Close(); });
        _tray = new NotifyIcon { Icon = Icon, Text = "FUPlayer DSP server", ContextMenuStrip = trayMenu };
        _tray.DoubleClick += (_, _) => RestoreWindow();
        _server.Message += QueueMessage;
        _server.StatusChanged += status => Volatile.Write(ref _status, status);
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        FormClosing += OnClosing;
        SetValues(settings);
        RefreshStatus();
        Shown += async (_, _) =>
        {
            if (_loadError is not null) ShowError(new IOException(Loc.T("The saved settings could not be loaded. Defaults are shown; the saved file has not been changed.") + Environment.NewLine + _loadError));
            else if (_autoStart.Checked) await StartServer();
        };
    }

    private void BuildConnection()
    {
        TableLayoutPanel grid = Grid();
        _connection.Controls.Add(grid);
        _listen.Name = _listen.AccessibleName = "ListenAddress";
        _port.Name = _port.AccessibleName = "ListenerPort";
        AddRow(grid, Loc.T("Listen on IP"), _listen);
        AddRow(grid, Loc.T("UDP listener port"), _port);
        AddRow(grid, Loc.T("Maximum clients"), _clients);
        var range = Flow(_first, new Label { Text = "–", AutoSize = true, Padding = new Padding(0, 7, 0, 0) }, _last);
        AddRow(grid, Loc.T("Worker ports (local only)"), range);
        AddHint(grid, Loc.T("Only the listener UDP port needs forwarding. Workers stay on this computer."));
        _internet.Text = Loc.T("Allow Internet connections"); _internet.AutoSize = true;
        _internet.Name = "AllowInternet";
        AddRow(grid, "", _internet);
        AddHint(grid, Loc.T("Private LAN uses plain UDP without a key. Internet connections require an access key and encrypted UDP."));
        _key.Name = _key.AccessibleName = "AccessKeyFile";
        AddRow(grid, Loc.T("Access key file"), WithBrowse(_key, folder: false));
        AddRow(grid, "", Flow(
            Button(Loc.T("Create key"), CreateKey),
            Button(Loc.T("Copy access key"), () => { Clipboard.SetText(ServerSettings.ReadKey(_key.Text)); Notice(Loc.T("Access key copied. Paste it into FUPlayer's Network DSP settings.")); return Task.CompletedTask; })));
        string[] addresses = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a)).Select(a => a.ToString()).Distinct().ToArray();
        AddHint(grid, Loc.F("This computer: {0}", addresses.Length > 0 ? string.Join(" · ", addresses) : Environment.MachineName));
        _autoStart.Text = Loc.T("Start the server when this controller opens"); _autoStart.AutoSize = true;
        _closeToTray.Text = Loc.T("Keep running in the notification area when closing"); _closeToTray.AutoSize = true;
        AddRow(grid, "", _autoStart); AddRow(grid, "", _closeToTray);
        _language.Items.AddRange([new Choice("", Loc.T("System language")), new Choice("en", "English"), new Choice("ko", "한국어")]);
        _language.DropDownStyle = ComboBoxStyle.DropDownList;
        AddRow(grid, Loc.T("Language (next launch)"), _language);
    }

    private void BuildProcessing()
    {
        TableLayoutPanel grid = Grid(); _processing.Controls.Add(grid);
        AddHint(grid, Loc.T("Resources are used only while a client processes audio. Resampling and neural settings are chosen in FUPlayer."));
        AddRow(grid, Loc.T("DSP helper threads"), _threads);
        AddHint(grid, Loc.T("The processing thread is additional. Zero disables filter helper threads."));
        SetDeviceChoices(null);
        AddRow(grid, Loc.T("Filter processing"), _gpu);
        _precision.Text = Loc.T("Use 64-bit GPU arithmetic"); _precision.AutoSize = true;
        _force.Text = Loc.T("Use GPU even when its timing is slower"); _force.AutoSize = true;
        AddRow(grid, "", _precision); AddRow(grid, "", _force);
        AddRow(grid, Loc.T("Neural restorer"), _restorer); AddRow(grid, Loc.T("Neural upscaler"), _upscaler);
        AddRow(grid, "", Button(Loc.T("Refresh GPU list"), async () =>
        {
            Notice(Loc.T("Looking for GPU devices…"));
            ControllerDevices devices = await _server.ListDevicesAsync();
            SetDeviceChoices(devices); Notice(Loc.T("GPU device list updated."));
        }));
        AddHint(grid, Loc.T("Automatic prefers CUDA on NVIDIA and Vulkan on other GPUs. Neural GPU processing uses Windows DirectML."));
        AddRow(grid, Loc.T("Custom models (optional)"), WithBrowse(_models, folder: true));
        AddRow(grid, Loc.T("Cache folder (optional)"), WithBrowse(_cache, folder: true));
        AddHint(grid, Loc.T("Leave folders empty to use the bundled models and the per-user cache."));
    }

    private TabPage ActivityPage()
    {
        var tab = new TabPage(Loc.T("Activity")) { Padding = new Padding(12), BackColor = SystemColors.Window };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _log.Name = _log.AccessibleName = "ServerLog";
        layout.Controls.Add(_log, 0, 0);
        layout.Controls.Add(Flow(Button(Loc.T("Clear log"), () => { _log.Clear(); return Task.CompletedTask; }),
            Button(Loc.T("Save log"), () =>
            {
                using var dialog = new SaveFileDialog { Filter = "Text (*.txt)|*.txt", FileName = "fuplayer-dsp.log", DefaultExt = "txt" };
                if (dialog.ShowDialog(this) == DialogResult.OK) File.WriteAllText(dialog.FileName, _log.Text);
                return Task.CompletedTask;
            })), 0, 1);
        tab.Controls.Add(layout); return tab;
    }

    private async Task StartServer()
    {
        if (_busy || _server.IsRunning) return;
        _busy = true; RefreshStatus();
        try
        {
            ServerSettings settings = ReadValues(); settings.Validate(); _store.Save(settings);
            Notice(Loc.T("Starting server…"));
            await _server.StartAsync(settings);
            Notice(Loc.F("Listening on UDP {0}. Connect using this computer's IP or domain.", settings.Port));
        }
        catch (OperationCanceledException) when (_exiting) { }
        catch (Exception ex) when (ex is not (OutOfMemoryException or AccessViolationException)) { ShowError(ex); }
        finally { _busy = false; RefreshStatus(); }
    }

    private async Task StopServer()
    {
        if (_busy) return;
        _busy = true; RefreshStatus(); Notice(Loc.T("Stopping server…"));
        try { await _server.StopAsync(); Notice(Loc.T("Server stopped.")); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or AccessViolationException)) { ShowError(ex); }
        finally { _busy = false; RefreshStatus(); }
    }

    private void Save()
    {
        _store.Save(ReadValues()); Notice(Loc.T("Settings saved. Changes apply the next time the server starts."));
    }

    private ServerSettings ReadValues() => new()
    {
        ListenAddress = _listen.Text.Trim(), Port = (int)_port.Value, WorkerPortStart = (int)_first.Value, WorkerPortEnd = (int)_last.Value,
        Clients = (int)_clients.Value, Threads = (int)_threads.Value, AllowInternet = _internet.Checked, KeyFile = _key.Text.Trim(),
        GpuEnabled = Value(_gpu) != "cpu", GpuDevice = Value(_gpu) == "cpu" ? "" : Value(_gpu),
        GpuHighPrecision = _precision.Checked, GpuForce = _force.Checked,
        RestorerDevice = Value(_restorer), UpscalerDevice = Value(_upscaler), ModelsDirectory = _models.Text.Trim(), CacheDirectory = _cache.Text.Trim(),
        StartOnOpen = _autoStart.Checked, CloseToTray = _closeToTray.Checked, Language = Value(_language),
    };

    private void SetValues(ServerSettings settings)
    {
        _listen.Text = settings.ListenAddress; _key.Text = settings.KeyFile; _models.Text = settings.ModelsDirectory; _cache.Text = settings.CacheDirectory;
        _port.Value = Math.Clamp(settings.Port, 1024, 65535); _first.Value = Math.Clamp(settings.WorkerPortStart, 1024, 65535); _last.Value = Math.Clamp(settings.WorkerPortEnd, 1024, 65535);
        _clients.Value = Math.Clamp(settings.Clients, 1, 16); _threads.Value = Math.Clamp(settings.Threads, 0, 63);
        _internet.Checked = settings.AllowInternet; _precision.Checked = settings.GpuHighPrecision; _force.Checked = settings.GpuForce;
        Select(_gpu, settings.GpuEnabled ? settings.GpuDevice : "cpu"); Select(_restorer, settings.RestorerDevice); Select(_upscaler, settings.UpscalerDevice);
        _autoStart.Checked = settings.StartOnOpen; _closeToTray.Checked = settings.CloseToTray; Select(_language, settings.Language);
    }

    private void SetDeviceChoices(ControllerDevices? devices)
    {
        string gpu = Value(_gpu), restorer = Value(_restorer), upscaler = Value(_upscaler);
        if (restorer.Length == 0) restorer = "cpu";
        if (upscaler.Length == 0) upscaler = "auto";
        _gpu.Items.Clear(); _restorer.Items.Clear(); _upscaler.Items.Clear();
        _gpu.Items.AddRange([new Choice("", Loc.T("Automatic")), new Choice("cpu", Loc.T("CPU only")), new Choice("cuda", "CUDA"), new Choice("vulkan", "Vulkan"), new Choice("opencl", "OpenCL")]);
        foreach (ComboBox combo in new[] { _restorer, _upscaler })
        {
            combo.Items.AddRange([new Choice("cpu", Loc.T("CPU only")), new Choice("auto", Loc.T("Automatic"))]);
            foreach (string name in devices?.Neural ?? []) combo.Items.Add(new Choice(name, name));
        }
        foreach (ControllerDevice device in devices?.Convolution ?? []) _gpu.Items.Add(new Choice(device.Id, $"{device.Name} ({device.Backend})"));
        Select(_gpu, gpu); Select(_restorer, restorer); Select(_upscaler, upscaler);
    }

    private async Task CreateKey()
    {
        using var dialog = new SaveFileDialog { Filter = "Key (*.key)|*.key", DefaultExt = "key", FileName = "fuplayer-dsp.key" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            ServerSettings.CreateKey(dialog.FileName); _key.Text = dialog.FileName;
            Notice(Loc.T("Access key created. Keep this file private and copy the key to FUPlayer."));
        }
        await Task.CompletedTask;
    }

    private void RefreshStatus()
    {
        if (IsDisposed) return;
        if (_hadRunningServer && !_server.IsRunning && !_busy) Notice(Loc.T("Server stopped."));
        _hadRunningServer = _server.IsRunning;
        ControllerStatus? status = Volatile.Read(ref _status);
        _state.Text = status is null ? Loc.T("Stopped") : status.ActiveClients > 0 ? Loc.T("Processing") : Loc.T("Listening");
        _clientCount.Text = status?.ActiveClients.ToString() ?? "0";
        _cpu.Text = status is null ? "—" : $"{status.CpuPercent:F1}%";
        _memory.Text = status is null ? "—" : $"{status.MemoryBytes / 1048576d:F0} MiB";
        _start.Enabled = !_busy && !_server.IsRunning;
        _stop.Enabled = !_busy && _server.IsRunning;
        _save.Enabled = !_busy && !_server.IsRunning;
        _connection.Enabled = _processing.Enabled = !_busy && !_server.IsRunning;
        _tray.Text = "FUPlayer DSP · " + _state.Text;
        int lines = 0;
        var batch = new System.Text.StringBuilder();
        while (lines++ < 100 && _messages.TryDequeue(out string? message)) batch.AppendLine(message);
        if (batch.Length > 0)
        {
            if (_log.TextLength > 120_000) _log.Text = _log.Text[^60_000..];
            _log.AppendText(batch.ToString());
        }
    }

    private void QueueMessage(string message)
    {
        if (_messages.Count >= 2000) _messages.TryDequeue(out _);
        _messages.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");
    }

    private void Notice(string message) { _notice.ForeColor = SystemColors.GrayText; _notice.Text = message; QueueMessage(message); }
    private void ShowError(Exception error)
    {
        _notice.Text = error.Message; _notice.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : Color.Firebrick;
        QueueMessage(error.Message);
        MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exitReady) return;
        if (!_quitFromTray && !_exiting && _closeToTray.Checked && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; _tray.Visible = true; Hide(); return;
        }
        e.Cancel = true;
        if (_exiting) return;
        _exiting = true; Enabled = false;
        try { await _server.StopAsync(); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or AccessViolationException)) { QueueMessage(ex.Message); }
        finally { _exitReady = true; Close(); }
    }

    private void RestoreWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); _tray.Visible = false; }
    private string CachePath() => string.IsNullOrWhiteSpace(_cache.Text) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FUPlayer-DspServer") : Path.GetFullPath(_cache.Text);
    private static void OpenFolder(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }

    private Control WithBrowse(TextBox input, bool folder)
    {
        input.Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.Controls.Add(input, 0, 0);
        layout.Controls.Add(Button(Loc.T("Browse…"), () =>
        {
            if (folder) { using var dialog = new FolderBrowserDialog(); if (dialog.ShowDialog(this) == DialogResult.OK) input.Text = dialog.SelectedPath; }
            else { using var dialog = new OpenFileDialog { CheckFileExists = true }; if (dialog.ShowDialog(this) == DialogResult.OK) input.Text = dialog.FileName; }
            return Task.CompletedTask;
        }), 1, 0);
        return layout;
    }

    private static TableLayoutPanel Grid()
    {
        var grid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(0, 4, 0, 8) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 225)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    private static void AddRow(TableLayoutPanel grid, string label, Control control)
    {
        int row = grid.RowCount++; grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 7, 4, 0), Margin = new Padding(0, 0, 8, 9) }, 0, row);
        control.Margin = new Padding(0, 0, 0, 9);
        if (control is TextBox or ComboBox) control.Dock = DockStyle.Top;
        grid.Controls.Add(control, 1, row);
    }

    private static void AddHint(TableLayoutPanel grid, string text)
    {
        int row = grid.RowCount++; grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var hint = new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 14) };
        grid.Controls.Add(hint, 0, row); grid.SetColumnSpan(hint, 2);
    }

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = Padding.Empty };
        panel.Controls.AddRange(controls); return panel;
    }

    private Button Button(string text, Func<Task> action)
    {
        var button = new Button(); ConfigureButton(button, text, text, action); return button;
    }

    private void ConfigureButton(Button button, string text, string name, Func<Task> action)
    {
        button.Text = text; button.Name = name; button.AccessibleName = text; button.AutoSize = true; button.MinimumSize = new Size(98, 34); button.Margin = new Padding(0, 0, 8, 4);
        button.Click += async (_, _) =>
        {
            button.Enabled = false;
            try { await action(); }
            catch (Exception ex) when (ex is not (OutOfMemoryException or AccessViolationException)) { ShowError(ex); }
            finally { if (!IsDisposed) { button.Enabled = true; RefreshStatus(); } }
        };
    }

    private static NumericUpDown Number(int minimum, int maximum) => new() { Minimum = minimum, Maximum = maximum, Width = 130, ThousandsSeparator = false };
    private static Label Metric() => new() { AutoSize = true, Font = new Font("Segoe UI", 17, FontStyle.Bold), Margin = Padding.Empty };
    private static ComboBox Choices() => new() { Width = 350, DropDownStyle = ComboBoxStyle.DropDownList, IntegralHeight = false, DropDownHeight = 220 };
    private static string Value(ComboBox combo) => combo.SelectedItem is Choice choice ? choice.Value : "";
    private static void Select(ComboBox combo, string? value)
    {
        value ??= "";
        Choice? found = combo.Items.Cast<Choice>().FirstOrDefault(c => c.Value == value);
        if (found is null) { found = new Choice(value, value); combo.Items.Add(found); }
        combo.SelectedItem = found;
    }
    private sealed record Choice(string Value, string Label) { public override string ToString() => Label; }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _tray?.Dispose(); _server.Message -= QueueMessage; }
        base.Dispose(disposing);
    }
}
