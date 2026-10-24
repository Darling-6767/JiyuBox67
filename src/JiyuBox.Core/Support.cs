using System.Text;

namespace JiyuBox.Core;

/// <summary>输入解锁：极域用 BlockInput / ClipCursor 锁键鼠时，本进程以高权限反复解掉。</summary>
public sealed class InputUnlockService : IDisposable
{
    private Timer? _timer;

    public bool Enabled { get; private set; }

    public ActionOutcome Enable()
    {
        if (Enabled) return ActionOutcome.Ok("键鼠锁已处于解除状态", "");

        // 立即解一次，之后每 200ms 复查一次（极域会反复上锁）
        Native.BlockInput(false);
        Native.ClipCursor(IntPtr.Zero);
        _timer = new Timer(_ =>
        {
            try
            {
                Native.BlockInput(false);
                Native.ClipCursor(IntPtr.Zero);
            }
            catch
            {
                // 忽略：进程退出边缘
            }
        }, null, 200, 200);

        Enabled = true;
        return ActionOutcome.Ok("键鼠锁已解除", "BlockInput(FALSE) + ClipCursor(NULL) @200ms");
    }

    public ActionOutcome Disable()
    {
        _timer?.Dispose();
        _timer = null;
        Enabled = false;
        return ActionOutcome.Ok("已停止解除键鼠锁", "");
    }

    public void Dispose() => Disable();
}

/// <summary>让本程序窗口对教师端截屏隐身（Win10 2004+ 生效）。</summary>
public static class CaptureProtector
{
    public static bool Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE)) return true;
        return Native.SetWindowDisplayAffinity(hwnd, Native.WDA_MONITOR);
    }

    public static void Clear(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) Native.SetWindowDisplayAffinity(hwnd, Native.WDA_NONE);
    }
}

/// <summary>极简日志：本地文件 + 内存事件，供 UI 展示。</summary>
public sealed class Logger
{
    private readonly object _lock = new();
    private readonly string _file;

    public event Action<string>? Appended;

    public Logger(string? directory = null)
    {
        directory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JiyuBox", "logs");
        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, $"jiyubox-{DateTime.Now:yyyyMMdd}.log");
    }

    public string LogFile => _file;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERR ", message);

    private void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}][{level}] {message}";
        lock (_lock)
        {
            try { File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8); }
            catch { /* 日志失败不能影响主流程 */ }
        }
        Appended?.Invoke(line);
    }
}
