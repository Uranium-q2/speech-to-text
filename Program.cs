using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

ApplicationConfiguration.Initialize();
Application.Run(new Overlay());

class Overlay : Form
{
    static readonly Color Cyan = Color.FromArgb(0, 240, 255), Muted = Color.FromArgb(115, 131, 156),
        Main = Color.FromArgb(7, 10, 18), Card = Color.FromArgb(15, 20, 34), Red = Color.FromArgb(255, 0, 60);
    static readonly HttpClient Http = new();
    static readonly (string Name, string Code)[] Targets =
    {
        ("Polish", "pl"), ("English", "en"), ("German", "de"), ("French", "fr"),
        ("Spanish", "es"), ("Italian", "it"), ("Russian", "ru"), ("Ukrainian", "uk"), ("Czech", "cs")
    };
    static readonly string ModelPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpeechToText", "ggml-small.bin");

    readonly Label lblText = new() { Dock = DockStyle.Fill, ForeColor = Color.White, Font = new Font("Consolas", 12), Padding = new Padding(10, 4, 10, 6) };
    readonly Label lblTr = new() { Dock = DockStyle.Bottom, Height = 60, ForeColor = Cyan, Font = new Font("Consolas", 12, FontStyle.Bold), Padding = new Padding(10, 0, 10, 6) };
    readonly Label lblInfo = new() { AutoSize = true, ForeColor = Muted, Font = new Font("Consolas", 8), Margin = new Padding(8, 9, 0, 0), Text = "starting..." };
    readonly Button btnRec = Btn("●");
    readonly Button btnPin = Btn("📌");
    readonly CheckBox chkTr = new() { Text = "Translate →", AutoSize = true, ForeColor = Muted, Font = new Font("Consolas", 9), Margin = new Padding(10, 7, 0, 0) };
    readonly ComboBox cbTarget = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 95, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(15, 20, 34), ForeColor = Cyan, Margin = new Padding(3, 3, 0, 0) };

    WhisperProcessor? processor;
    WaveInEvent? mic;
    readonly List<byte> buffer = new();
    int silentMs;
    bool hasSpeech, busy;

    public Overlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Main;
        Opacity = 0.94;
        Padding = new Padding(1);
        HandleCreated += (_, _) =>
        {
            int round = 2, border = 0xFFF000;
            DwmSetWindowAttribute(Handle, 33, ref round, 4);
            DwmSetWindowAttribute(Handle, 34, ref border, 4);
        };
        Size = new Size(620, 170);
        StartPosition = FormStartPosition.Manual;
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - 20);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false, BackColor = Color.FromArgb(5, 7, 13) };
        var handle = new Label { Text = "::", AutoSize = true, ForeColor = Muted, Font = new Font("Consolas", 11, FontStyle.Bold), Cursor = Cursors.SizeAll, Margin = new Padding(6, 6, 4, 0) };
        var btnClose = Btn("✕");
        btnClose.ForeColor = Red;
        btnClose.Click += (_, _) => Close();
        btnClose.Dock = DockStyle.None;
        bar.Controls.AddRange(new Control[] { handle, btnRec, btnPin, chkTr, cbTarget, lblInfo });

        Controls.Add(lblText);
        Controls.Add(lblTr);
        Controls.Add(bar);
        Controls.Add(btnClose);
        btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnClose.Location = new Point(Width - 36, 2);
        btnClose.BringToFront();

        foreach (var t in Targets) cbTarget.Items.Add(t.Name);
        cbTarget.SelectedIndex = 1;

        foreach (Control c in new Control[] { bar, handle, lblText, lblTr, lblInfo })
            c.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0); } };

        btnRec.Click += (_, _) => Toggle();
        btnPin.ForeColor = Cyan;
        btnPin.Click += (_, _) => TogglePin();
        Load += async (_, _) => await Init();
        FormClosing += (_, _) => { mic?.Dispose(); processor?.Dispose(); Translator.Dispose(); };
    }

    static Button Btn(string t) => new()
    {
        Text = t, Width = 32, Height = 28, FlatStyle = FlatStyle.Flat, ForeColor = Cyan, Font = new Font("Consolas", 10, FontStyle.Bold),
        BackColor = Card, Margin = new Padding(2, 2, 0, 0), FlatAppearance = { BorderColor = Color.FromArgb(27, 37, 59) }
    };

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern int SendMessage(IntPtr h, int m, int w, int l);

    async Task Init()
    {
        try
        {
            if (!File.Exists(ModelPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
                lblInfo.Text = "downloading model (~470 MB)...";
                using var s = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Small);
                using (var f = File.Create(ModelPath + ".tmp")) await s.CopyToAsync(f);
                File.Move(ModelPath + ".tmp", ModelPath);
            }
            lblInfo.Text = "loading model...";
            var factory = WhisperFactory.FromPath(ModelPath);
            processor = factory.CreateBuilder().WithLanguage("auto").Build();
            lblInfo.Text = "ready - click ●";
        }
        catch (Exception ex) { lblInfo.Text = "error: " + ex.Message; }
    }

    void TogglePin()
    {
        TopMost = !TopMost;
        btnPin.ForeColor = TopMost ? Cyan : Muted;
        lblInfo.Text = TopMost ? "pinned on top" : "unpinned";
    }

    void Toggle()
    {
        if (processor == null) return;
        if (mic != null)
        {
            mic.StopRecording(); mic.Dispose(); mic = null;
            btnRec.ForeColor = Cyan; lblInfo.Text = "stopped";
            return;
        }
        buffer.Clear(); hasSpeech = false; silentMs = 0;
        mic = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 };
        mic.DataAvailable += OnData;
        mic.StartRecording();
        btnRec.ForeColor = Red; lblInfo.Text = "listening...";
    }

    void OnData(object? s, WaveInEventArgs e)
    {
        double sum = 0; int n = e.BytesRecorded / 2;
        for (int i = 0; i < n; i++) { short v = BitConverter.ToInt16(e.Buffer, i * 2); sum += (double)v * v; }
        bool speech = Math.Sqrt(sum / Math.Max(n, 1)) > 500;

        if (speech) { hasSpeech = true; silentMs = 0; } else silentMs += 100;

        buffer.AddRange(new ArraySegment<byte>(e.Buffer, 0, e.BytesRecorded));
        if (!hasSpeech && buffer.Count > 16000) buffer.RemoveRange(0, buffer.Count - 16000);

        bool flush = hasSpeech && (silentMs >= 700 || buffer.Count > 16000 * 2 * 12);
        if (flush && !busy)
        {
            var data = buffer.ToArray();
            buffer.Clear(); hasSpeech = false; silentMs = 0;
            _ = Process(data);
        }
    }

    async Task Process(byte[] pcm)
    {
        busy = true;
        try
        {
            BeginInvoke(() => lblInfo.Text = "processing...");
            var ms = new MemoryStream();
            using (var w = new WaveFileWriter(new NonClosing(ms), new WaveFormat(16000, 16, 1))) w.Write(pcm, 0, pcm.Length);
            ms.Position = 0;

            var text = new System.Text.StringBuilder(); string lang = "";
            await foreach (var seg in processor!.ProcessAsync(ms))
            {
                text.Append(seg.Text); lang = seg.Language;
            }
            string t = text.ToString().Trim();
            if (t.Length < 2 || t.StartsWith('[') || t.StartsWith('('))
            {
                BeginInvoke(() => lblInfo.Text = mic != null ? "listening..." : "stopped");
                return;
            }
            BeginInvoke(() => { lblText.Text = t; lblInfo.Text = $"[{lang}]"; });
            if (chkTr.Checked)
            {
                string dst = "en";
                Invoke(() => dst = Targets[cbTarget.SelectedIndex].Code);
                string tr = await Translate(t, lang, dst);
                BeginInvoke(() => lblTr.Text = tr);
            }
        }
        catch (Exception ex) { BeginInvoke(() => lblInfo.Text = "error: " + ex.Message); }
        finally { busy = false; }
    }

    
    static readonly LocalTranslator Translator = new();

    static Task<string> Translate(string text, string from, string to) => Translator.Translate(text, from, to);

    sealed class LocalTranslator : IDisposable
    {
        static readonly string ScriptPath = Path.Combine(AppContext.BaseDirectory, "translator_service.py");
        System.Diagnostics.Process? proc;
        readonly SemaphoreSlim gate = new(1, 1);

        void EnsureStarted()
        {
            if (proc is { HasExited: false }) return;
            string script = File.Exists(ScriptPath)
                ? ScriptPath
                : Path.Combine(Directory.GetCurrentDirectory(), "translator_service.py");
            var psi = new ProcessStartInfo("python", $"\"{script}\"")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                EnvironmentVariables = { ["PYTHONIOENCODING"] = "utf-8" }
            };
            proc = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Could not start python translator_service.py");
        }

        public async Task<string> Translate(string text, string from, string to)
        {
            if (from == to || string.IsNullOrWhiteSpace(text)) return text;
            await gate.WaitAsync();
            try
            {
                EnsureStarted();
                var req = JsonSerializer.Serialize(new { text, from, to });
                await proc!.StandardInput.WriteLineAsync(req);
                await proc.StandardInput.FlushAsync();
                string? line = await proc.StandardOutput.ReadLineAsync();
                if (line == null) throw new InvalidOperationException("translator process closed unexpectedly");
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.GetProperty("ok").GetBoolean())
                    throw new InvalidOperationException(root.TryGetProperty("error", out var e) ? e.GetString() : "unknown error");
                return root.GetProperty("text").GetString() ?? text;
            }
            finally { gate.Release(); }
        }

        public void Dispose()
        {
            try { if (proc is { HasExited: false }) proc.Kill(); } catch { /* ignore */ }
            proc?.Dispose();
        }
    }

    sealed class NonClosing(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] b, int o, int c) => inner.Read(b, o, c);
        public override long Seek(long o, SeekOrigin s) => inner.Seek(o, s);
        public override void SetLength(long v) => inner.SetLength(v);
        public override void Write(byte[] b, int o, int c) => inner.Write(b, o, c);
        protected override void Dispose(bool d) { }
    }
}
