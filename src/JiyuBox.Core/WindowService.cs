using System.Diagnostics;

namespace JiyuBox.Core;

/// <summary>窗口动作层：广播窗口化、退出黑屏、以及全部还原。</summary>
public sealed class WindowService
{
    private sealed class Snapshot
    {
        public long Style;
        public long ExStyle;
        public Native.RECT Rect;
        public bool Hidden;
        public string Kind = "";
    }

    private readonly Dictionary<IntPtr, Snapshot> _managed = new();

    public IReadOnlyCollection<IntPtr> Managed => _managed.Keys;

    /// <summary>把全屏广播窗口改成可拖动、可缩放的普通窗口。</summary>
    public ActionOutcome Windowize(IntPtr hwnd, double scale = 0.7)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd))
            return ActionOutcome.Fail("窗口不存在", "hwnd = 0");

        var style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        if (!_managed.ContainsKey(hwnd))
        {
            Native.GetWindowRect(hwnd, out var rect);
            _managed[hwnd] = new Snapshot { Style = style, ExStyle = ex, Rect = rect, Kind = "broadcast" };
        }

        var newStyle = (style & ~Native.WS_POPUP)
                       | Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_SYSMENU
                       | Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX | Native.WS_VISIBLE;
        var newEx = ex & ~Native.WS_EX_TOPMOST;

        Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, (IntPtr)newStyle);
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (IntPtr)newEx);

        var (monW, monH) = MonitorSize(hwnd);
        var w = (int)(monW * scale);
        var h = (int)(monH * scale);
        var x = (monW - w) / 2;
        var y = (monH - h) / 2;

        var ok = Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, x, y, w, h,
            Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW | Native.SWP_NOACTIVATE);

        return ok
            ? ActionOutcome.Ok($"已窗口化 {w}x{h}", $"style 0x{style:X8} → 0x{newStyle:X8}, ex 0x{ex:X8} → 0x{newEx:X8}")
            : ActionOutcome.Fail("SetWindowPos 失败", $"Win32 错误码 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
    }

    /// <summary>广播窗口化后，如果被极域重新拉回全屏，这里负责再压一次。</summary>
    public bool EnsureWindowized(IntPtr hwnd, double scale = 0.7)
    {
        if (!_managed.ContainsKey(hwnd) || !Native.IsWindow(hwnd)) return false;
        var style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        var reverted = (style & Native.WS_POPUP) != 0 || (ex & Native.WS_EX_TOPMOST) != 0;
        if (!reverted) return false;
        Windowize(hwnd, scale);
        return true;
    }

    /// <summary>退出黑屏安静：先隐藏，再发关闭消息（不杀进程）。</summary>
    public ActionOutcome ExitBlackScreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd))
            return ActionOutcome.Fail("窗口不存在", "hwnd = 0");

        if (!_managed.ContainsKey(hwnd))
        {
            Native.GetWindowRect(hwnd, out var rect);
            _managed[hwnd] = new Snapshot
            {
                Style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64(),
                ExStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64(),
                Rect = rect,
                Kind = "blackscreen"
            };
        }

        var hidden = Native.ShowWindow(hwnd, Native.SW_HIDE);
        Native.PostMessageW(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _managed[hwnd].Hidden = true;

        return hidden || !Native.IsWindowVisible(hwnd)
            ? ActionOutcome.Ok("已退出黑屏", "ShowWindow(SW_HIDE) + WM_CLOSE")
            : ActionOutcome.Fail("黑屏窗口未响应", "可能已被极域锁定，可尝试挂起极域后再处理");
    }

    /// <summary>按记录还原所有改动过的窗口。</summary>
    public ActionOutcome RestoreAll()
    {
        var count = 0;
        foreach (var (hwnd, snap) in _managed)
        {
            if (!Native.IsWindow(hwnd)) continue;

            if (snap.Hidden)
                Native.ShowWindow(hwnd, Native.SW_SHOW);

            Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, (IntPtr)snap.Style);
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (IntPtr)snap.ExStyle);

            // 置顶与否由 SetWindowPos 决定，不能只靠扩展样式位
            var zOrder = (snap.ExStyle & Native.WS_EX_TOPMOST) != 0
                ? Native.HWND_TOPMOST
                : Native.HWND_NOTOPMOST;

            Native.SetWindowPos(hwnd, zOrder, snap.Rect.Left, snap.Rect.Top,
                snap.Rect.Width, snap.Rect.Height,
                Native.SWP_FRAMECHANGED | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            count++;
        }

        _managed.Clear();
        return count > 0
            ? ActionOutcome.Ok($"已还原 {count} 个窗口", "")
            : ActionOutcome.Ok("没有需要还原的窗口", "");
    }

    private static (int W, int H) MonitorSize(IntPtr hwnd)
    {
        var mon = Native.MonitorFromWindow(hwnd, 2);
        if (mon != IntPtr.Zero)
        {
            var mi = new Native.MONITORINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>()
            };
            if (Native.GetMonitorInfoW(mon, ref mi))
                return (mi.rcWork.Width, mi.rcWork.Height);
        }
        return (Native.GetSystemMetrics(Native.SM_CXSCREEN), Native.GetSystemMetrics(Native.SM_CYSCREEN));
    }
}

/// <summary>动作结果：是否成功 + 人话 + 证据（句柄、样式、错误码）。</summary>
public sealed record ActionOutcome(bool Success, string Message, string Evidence)
{
    public static ActionOutcome Ok(string m, string e = "") => new(true, m, e);
    public static ActionOutcome Fail(string m, string e = "") => new(false, m, e);

    public override string ToString() => (Success ? "[OK] " : "[NG] ") + Message;
}
