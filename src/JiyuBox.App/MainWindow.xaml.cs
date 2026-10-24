using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Application = System.Windows.Application;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using JiyuBox.Core;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace JiyuBox;

public partial class MainWindow : Window
{
    private const int HotkeyWindowize = 0x4A01;
    private const int HotkeyBlack = 0x4A02;
    private const int HotkeySuspend = 0x4A03;

    private readonly Logger _log = new();
    private readonly WindowService _windows = new();
    private readonly ProcessService _process = new();
    private readonly InputUnlockService _input = new();
    private readonly ObservableCollection<string> _logLines = new();

    private Config _config = new();
    private AdapterProfile _adapter = AdapterProfile.Jiyu2021();
    private Probe _probe;
    private ProbeResult? _last;
    private DispatcherTimer? _statusTimer;
    private DispatcherTimer? _watchTimer;
    private DispatcherTimer? _topTimer;
    private WinForms.NotifyIcon? _tray;
    private HwndSource? _source;
    private bool _reallyExit;
    private bool _suspended;

    public MainWindow()
    {
        InitializeComponent();

        _config = Config.Load();
        _adapter = AdapterProfile.LoadOrCreate(Config.AdapterPath, _config.Preset);
        _probe = new Probe(_adapter);

        LogList.ItemsSource = _logLines;
        _log.Appended += line => Dispatcher.BeginInvoke(() =>
        {
            _logLines.Add(line);
            while (_logLines.Count > 400) _logLines.RemoveAt(0);
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
        });

        SubtitleText.Text = $"JiyuBox {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} · 只挂起不杀进程";
        LogPathText.Text = _log.LogFile;
        ElevationText.Text = _process.IsElevated
            ? "已提权 · 可挂起 SYSTEM 进程"
            : "未提权 · 挂起极域可能失败（请右键以管理员身份运行）";

        _log.Info($"启动，适配档：{_adapter.Name}");
        LogList.ItemsSource = _logLines;

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        Closed += (_, _) => { Cleanup(); if (_reallyExit) Application.Current.Shutdown(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitTray();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        _topTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _topTimer.Tick += (_, _) => KeepOnTop();
        _topTimer.Start();

        _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _watchTimer.Tick += (_, _) => AutoWatch();
        _watchTimer.Start();

        RefreshStatus();

        if (_config.StartMinimized)
        {
            Hide();
            _tray?.ShowBalloonTip(2000, "JiyuBox 已在后台运行", "双击托盘图标打开主界面", WinForms.ToolTipIcon.Info);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        var hwnd = helper.Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);

        if (_config.ProtectFromCapture)
        {
            var ok = CaptureProtector.Apply(hwnd);
            _log.Info(ok ? "本窗口已对截屏隐身（WDA）" : "防截屏未生效（需要 Win10 2004+）");
        }

        NativeHotkeys.Register(hwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case HotkeyWindowize: OnWindowize(this, new RoutedEventArgs()); handled = true; break;
                case HotkeyBlack: OnExitBlack(this, new RoutedEventArgs()); handled = true; break;
                case HotkeySuspend: OnToggleSuspend(this, new RoutedEventArgs()); handled = true; break;
            }
        }
        return IntPtr.Zero;
    }

    // ---------------- 状态 ----------------

    private void RefreshStatus()
    {
        try
        {
            _last = _probe.Scan();
        }
        catch (Exception ex)
        {
            _log.Error("探测失败：" + ex.Message);
            return;
        }

        AdapterValue.Text = _last.AdapterName;
        ProcValue.Text = _last.Running
            ? string.Join("  ", _last.Processes.Select(p => $"{p.Name}({p.Pid})"))
            : "未检测到";
        VersionValue.Text = string.IsNullOrWhiteSpace(_last.Version) ? "—" : _last.Version;
        BroadcastValue.Text = _last.BroadcastWindow is null ? "无" : "正在广播（全屏置顶）";
        BlackValue.Text = _last.BlackScreenWindow is null ? "无" : "检测到疑似黑屏窗口";
        SuspendValue.Text = !_last.Running
            ? "—"
            : _suspended
                ? $"已挂起（{_process.Suspended.Count} 个进程）"
                : "运行中";
        InputValue.Text = _input.Enabled ? "已解除（持续压制）" : "未处理";

        Paint(BroadcastValue, _last.BroadcastWindow is null ? "Muted" : "Warn");
        Paint(BlackValue, _last.BlackScreenWindow is null ? "Muted" : "Warn");
        Paint(SuspendValue, _suspended ? "Warn" : "Ok");
        Paint(InputValue, _input.Enabled ? "Ok" : "Muted");

        StatusText.Text = _last.Running ? "已找到极域" : "未找到极域";
        StatusDot.Fill = Brush(_last.Running ? "Ok" : "Muted");
        StatusPill.BorderBrush = Brush(_last.Running ? "Ok" : "CardBorder");

        DetailValue.Text = _last.Windows.Count == 0
            ? _last.Summary
            : _last.Summary + "\n" + string.Join("\n", _last.Windows.Take(4).Select(w => "· " + w));

        BtnWindowize.IsEnabled = _last.BroadcastWindow is not null;
        BtnBlack.IsEnabled = _last.BlackScreenWindow is not null;
        BtnSuspend.IsEnabled = _last.Running || _suspended;
        BtnSuspend.Content = _suspended ? "恢复极域　Ctrl+Alt+P" : "挂起极域　Ctrl+Alt+P";
        BtnInput.Content = _input.Enabled ? "恢复键鼠锁状态" : "解除键鼠锁";
    }

    /// <summary>保持窗口在最上层：UIAccess 版才能真正压过极域的全屏广播窗口。</summary>
    private void KeepOnTop()
    {
        if (!_config.KeepTopmost || !IsVisible) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void AutoWatch()
    {
        if (_last?.BroadcastWindow is { } w && _windows.Managed.Contains(w.Hwnd))
        {
            if (_windows.EnsureWindowized(w.Hwnd, _config.WindowScale))
                _log.Info("检测到广播窗口被拉回全屏，已重新窗口化");
        }
    }

    // ---------------- 动作 ----------------

    private void OnWindowize(object sender, RoutedEventArgs e)
    {
        var target = _last?.BroadcastWindow ?? _last?.Windows.FirstOrDefault(w => w.FullScreen);
        if (target is null)
        {
            _log.Warn("没有找到全屏广播窗口，可能当前未在广播状态");
            return;
        }

        var r = _windows.Windowize(target.Hwnd, _config.WindowScale);
        Report(r, $"窗口化广播 0x{target.Hwnd.ToInt64():X}");
        RefreshStatus();
    }

    private void OnExitBlack(object sender, RoutedEventArgs e)
    {
        var target = _last?.BlackScreenWindow;
        if (target is null)
        {
            _log.Warn("没有检测到黑屏窗口");
            return;
        }

        var r = _windows.ExitBlackScreen(target.Hwnd);
        Report(r, $"退出黑屏 0x{target.Hwnd.ToInt64():X}");
        RefreshStatus();
    }

    private void OnToggleInput(object sender, RoutedEventArgs e)
    {
        if (_input.Enabled)
        {
            var r = _input.Disable();
            Report(r, "停止解除键鼠锁");
        }
        else
        {
            var r = _input.Enable();
            Report(r, "解除键鼠锁");
        }
        RefreshStatus();
    }

    private void OnToggleSuspend(object sender, RoutedEventArgs e)
    {
        if (_suspended)
        {
            var r = _process.ResumeAll();
            Report(r, "恢复极域");
            if (r.Success) _suspended = false;
        }
        else
        {
            var pids = _last?.Processes.Select(p => p.Pid).ToArray() ?? Array.Empty<uint>();
            if (pids.Length == 0)
            {
                _log.Warn("没有可挂起的极域进程");
                return;
            }

            var r = _process.SuspendAll(pids);
            Report(r, "挂起极域");
            if (r.Success) _suspended = true;
        }
        RefreshStatus();
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        var w = _windows.RestoreAll();
        var p = _process.ResumeAll();
        var i = _input.Disable();
        _suspended = false;

        Report(w, "还原窗口");
        Report(p, "恢复进程");
        Report(i, "关闭键鼠锁解除");
        RefreshStatus();
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        RefreshStatus();
        _log.Info("手动刷新状态");
    }

    private void Report(ActionOutcome r, string what)
    {
        var line = $"{what} → {r.Message}" + (string.IsNullOrEmpty(r.Evidence) ? "" : $"　（{r.Evidence}）");
        if (r.Success) _log.Info(line); else _log.Warn(line);
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_log.LogFile}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn("打开日志目录失败：" + ex.Message);
        }
    }

    // ---------------- 托盘 / 生命周期 ----------------

    private void InitTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Shield,
            Text = "极域工具箱 JiyuBox",
            Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开主界面", null, (_, _) => ShowFromTray());
        menu.Items.Add("全部还原", null, (_, _) => Dispatcher.Invoke(() => OnRestore(this, new RoutedEventArgs())));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApp));
        _tray.ContextMenuStrip = menu;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        KeepOnTop();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyExit) return;
        e.Cancel = true;
        Hide();
        _tray?.ShowBalloonTip(1500, "JiyuBox 仍在运行", "窗口化/挂起状态保持中，双击托盘图标可恢复界面", WinForms.ToolTipIcon.Info);
    }

    private void ExitApp()
    {
        _reallyExit = true;
        Close();
    }

    private void Cleanup()
    {
        try
        {
            _statusTimer?.Stop();
            _topTimer?.Stop();
            _watchTimer?.Stop();
            _windows.RestoreAll();
            _process.ResumeAll();
            _input.Dispose();
            if (_source is not null) _source.RemoveHook(WndProc);
            if (_source is not null) NativeHotkeys.Unregister(_source.Handle);
            if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
            _log.Info("已退出，窗口与进程状态已还原");
        }
        catch
        {
            // 退出路径不抛异常
        }
    }

    // ---------------- 小工具 ----------------

    private void Paint(System.Windows.Controls.TextBlock block, string tone)
    {
        block.Foreground = tone switch
        {
            "Ok" => Brush("Ok"),
            "Warn" => Brush("Warn"),
            "Bad" => Brush("Bad"),
            _ => Brush("Muted")
        };
    }

    private static SolidColorBrush Brush(string key) =>
        (SolidColorBrush)System.Windows.Application.Current.Resources[key + "Brush"];

    private sealed class Config
    {
        public string Preset { get; set; } = "2021";     // 2016 | 2021
        public double WindowScale { get; set; } = 0.7;
        public bool AutoWatch { get; set; } = true;
        public bool StartMinimized { get; set; }
        public bool ProtectFromCapture { get; set; } = true;
        public bool KeepTopmost { get; set; } = true;

        public static string Dir => AppContext.BaseDirectory;
        public static string PathFile => System.IO.Path.Combine(Dir, "config.json");
        public static string AdapterPath => System.IO.Path.Combine(Dir, "adapter.json");

        public static Config Load()
        {
            try
            {
                if (File.Exists(PathFile))
                {
                    var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(PathFile));
                    if (cfg is not null) return cfg;
                }
            }
            catch { /* 用默认值 */ }

            var def = new Config();
            try { File.WriteAllText(PathFile, JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = true })); }
            catch { /* 只读目录忽略 */ }
            return def;
        }
    }
}

/// <summary>全局热键（挂在主窗口句柄上）。</summary>
internal static class NativeHotkeys
{
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;

    public static void Register(IntPtr hwnd)
    {
        Native.RegisterHotKey(hwnd, 0x4A01, MOD_CONTROL | MOD_ALT, 0x57); // Ctrl+Alt+W
        Native.RegisterHotKey(hwnd, 0x4A02, MOD_CONTROL | MOD_ALT, 0x42); // Ctrl+Alt+B
        Native.RegisterHotKey(hwnd, 0x4A03, MOD_CONTROL | MOD_ALT, 0x50); // Ctrl+Alt+P
    }

    public static void Unregister(IntPtr hwnd)
    {
        Native.UnregisterHotKey(hwnd, 0x4A01);
        Native.UnregisterHotKey(hwnd, 0x4A02);
        Native.UnregisterHotKey(hwnd, 0x4A03);
    }
}
