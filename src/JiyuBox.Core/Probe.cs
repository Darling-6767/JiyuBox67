using System.Diagnostics;
using System.Text;

namespace JiyuBox.Core;

/// <summary>一个顶层窗口的可判定信息。</summary>
public sealed record WindowInfo(
    IntPtr Hwnd,
    uint Pid,
    string ClassName,
    string Title,
    long Style,
    long ExStyle,
    bool Visible,
    bool Topmost,
    bool FullScreen,
    int Width,
    int Height)
{
    public bool Captioned => (Style & Native.WS_CAPTION) == Native.WS_CAPTION;
    public bool Popup => (Style & Native.WS_POPUP) != 0;
    public bool Layered => (ExStyle & Native.WS_EX_LAYERED) != 0;

    public override string ToString() =>
        $"0x{Hwnd.ToInt64():X8} pid={Pid} class='{ClassName}' title='{Title}' {Width}x{Height} " +
        $"topmost={Topmost} fullscreen={FullScreen} style=0x{Style:X8} ex=0x{ExStyle:X8}";
}

/// <summary>被控端进程信息。</summary>
public sealed record PeerProcess(uint Pid, string Name, string? Path, string? Version, bool Responding);

/// <summary>一次探测的完整结果。</summary>
public sealed class ProbeResult
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public bool Running { get; init; }
    public string AdapterName { get; init; } = "";
    public string? Version { get; init; }
    public IReadOnlyList<PeerProcess> Processes { get; init; } = Array.Empty<PeerProcess>();
    public IReadOnlyList<WindowInfo> Windows { get; init; } = Array.Empty<WindowInfo>();
    public WindowInfo? BroadcastWindow { get; init; }
    public WindowInfo? BlackScreenWindow { get; init; }
    public string Summary { get; init; } = "";
}

/// <summary>
/// 探测层：只读。负责回答“极域在不在、什么版本、现在是什么状态”。
/// 所有判断都基于进程 + 窗口属性，不依赖硬编码窗口类名（类名只作为猜想的加分项）。
/// </summary>
public sealed class Probe(AdapterProfile adapter)
{
    public AdapterProfile Adapter { get; } = adapter;

    public ProbeResult Scan()
    {
        var peers = FindProcesses();
        var windows = peers.Count == 0 ? new List<WindowInfo>() : EnumeratePeerWindows(peers);

        var broadcast = windows
            .Where(w => w.Visible && w.FullScreen && !w.Captioned)
            .OrderByDescending(w => w.Popup)
            .ThenByDescending(w => w.Topmost)
            .FirstOrDefault();

        var black = windows
            .Where(w => w.Visible && w.FullScreen && w.Topmost && !ReferenceEquals(w, broadcast))
            .OrderByDescending(w => HitHint(w, Adapter.BlackScreenHints))
            .ThenByDescending(w => w.Layered)
            .FirstOrDefault();

        var version = peers
            .Select(p => p.Version)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var summary = peers.Count == 0
            ? $"未检测到极域进程（适配档：{Adapter.Name}）"
            : $"检测到 {peers.Count} 个进程" +
              (broadcast is null ? "，当前没有全屏广播" : "，正在全屏广播") +
              (black is null ? "" : "，疑似黑屏安静");

        return new ProbeResult
        {
            Running = peers.Count > 0,
            AdapterName = Adapter.Name,
            Version = version,
            Processes = peers,
            Windows = windows,
            BroadcastWindow = broadcast,
            BlackScreenWindow = black,
            Summary = summary
        };
    }

    private static int HitHint(WindowInfo w, List<string> hints)
    {
        var score = 0;
        foreach (var h in hints)
        {
            if (h.Length == 0) continue;
            if (w.ClassName.Contains(h, StringComparison.OrdinalIgnoreCase)) score += 2;
            if (w.Title.Contains(h, StringComparison.OrdinalIgnoreCase)) score += 1;
        }
        return score;
    }

    private List<PeerProcess> FindProcesses()
    {
        var wanted = new HashSet<string>(Adapter.ProcessNames, StringComparer.OrdinalIgnoreCase);
        var list = new List<PeerProcess>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var name = p.ProcessName + ".exe";
                if (!wanted.Contains(name) && !wanted.Contains(p.ProcessName))
                    continue;

                var path = TryGetPath((uint)p.Id);
                list.Add(new PeerProcess((uint)p.Id, name, path, TryVersion(path), SafeResponding(p)));
            }
            catch
            {
                // 进程可能在枚举过程中退出
            }
            finally
            {
                p.Dispose();
            }
        }

        return list;
    }

    private static bool SafeResponding(Process p)
    {
        try { return p.Responding; } catch { return false; }
    }

    private static string? TryGetPath(uint pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return Native.QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    private static string? TryVersion(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(vi.FileVersion) ? vi.ProductVersion : vi.FileVersion;
        }
        catch
        {
            return null;
        }
    }

    private static List<WindowInfo> EnumeratePeerWindows(IReadOnlyList<PeerProcess> peers)
    {
        var pids = peers.Select(p => p.Pid).ToHashSet();
        var result = new List<WindowInfo>();

        Native.EnumWindows((hwnd, _) =>
        {
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains(pid)) return true;

            var info = Describe(hwnd, pid);
            if (info is not null) result.Add(info);
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>把一个 hwnd 描述成可判定的信息；不是顶层窗口则返回 null。</summary>
    public static WindowInfo? Describe(IntPtr hwnd, uint pid)
    {
        var style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();

        if ((style & Native.WS_CHILD) != 0) return null;   // 子窗口不算

        var cls = new StringBuilder(256);
        Native.GetClassNameW(hwnd, cls, cls.Capacity);
        var title = new StringBuilder(512);
        Native.GetWindowTextW(hwnd, title, title.Capacity);

        Native.GetWindowRect(hwnd, out var rect);
        var (monW, monH) = MonitorSize(hwnd);
        var full = rect.Width >= monW * 0.95 && rect.Height >= monH * 0.95;

        return new WindowInfo(
            hwnd, pid, cls.ToString(), title.ToString(), style, ex,
            Native.IsWindowVisible(hwnd),
            (ex & Native.WS_EX_TOPMOST) != 0,
            full, rect.Width, rect.Height);
    }

    private static (int W, int H) MonitorSize(IntPtr hwnd)
    {
        var mon = Native.MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (mon != IntPtr.Zero)
        {
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            if (Native.GetMonitorInfoW(mon, ref mi))
                return (mi.rcMonitor.Width, mi.rcMonitor.Height);
        }
        return (Native.GetSystemMetrics(Native.SM_CXSCREEN), Native.GetSystemMetrics(Native.SM_CYSCREEN));
    }
}
