using System.Diagnostics;

namespace JiyuBox.Core;

/// <summary>
/// 进程动作层：只做挂起 / 恢复，永不结束进程。
/// 挂起用 NtSuspendProcess（对应任务管理器的“挂起”），恢复用 NtResumeProcess。
/// </summary>
public sealed class ProcessService
{
    private readonly HashSet<uint> _suspended = new();

    public IReadOnlyCollection<uint> Suspended => _suspended;

    public bool IsElevated
    {
        get
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public ActionOutcome SuspendAll(IEnumerable<uint> pids)
    {
        Native.EnableDebugPrivilege();
        var ok = new List<uint>();
        var fail = new List<string>();

        foreach (var pid in pids.Distinct())
        {
            var r = Suspend(pid);
            if (r.Success) ok.Add(pid); else fail.Add($"{pid}: {r.Message}");
        }

        return fail.Count == 0 && ok.Count > 0
            ? ActionOutcome.Ok($"已挂起 {ok.Count} 个进程", string.Join(", ", ok))
            : ActionOutcome.Fail($"挂起成功 {ok.Count}，失败 {fail.Count}", string.Join("; ", fail));
    }

    public ActionOutcome Suspend(uint pid)
    {
        if (pid == 0) return ActionOutcome.Fail("无效 PID");

        var h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero)
            return ActionOutcome.Fail("OpenProcess 失败（需要管理员权限）",
                $"pid={pid}, Win32={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");

        try
        {
            var status = Native.NtSuspendProcess(h);
            if (status == 0)
            {
                _suspended.Add(pid);
                return ActionOutcome.Ok($"已挂起 pid {pid}", $"NTSTATUS=0x{status:X8}");
            }
            return ActionOutcome.Fail($"NtSuspendProcess 返回 0x{status:X8}", $"pid={pid}");
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    public ActionOutcome ResumeAll()
    {
        if (_suspended.Count == 0)
            return ActionOutcome.Ok("没有处于挂起状态的进程", "");

        var ok = 0;
        var fail = new List<string>();
        foreach (var pid in _suspended.ToList())
        {
            var r = Resume(pid);
            if (r.Success) ok++; else fail.Add($"{pid}: {r.Message}");
        }

        return fail.Count == 0
            ? ActionOutcome.Ok($"已恢复 {ok} 个进程", "")
            : ActionOutcome.Fail($"恢复成功 {ok}，失败 {fail.Count}", string.Join("; ", fail));
    }

    public ActionOutcome Resume(uint pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero)
            return ActionOutcome.Fail("OpenProcess 失败", $"pid={pid}");

        try
        {
            var status = Native.NtResumeProcess(h);
            if (status == 0)
            {
                _suspended.Remove(pid);
                return ActionOutcome.Ok($"已恢复 pid {pid}", "");
            }
            return ActionOutcome.Fail($"NtResumeProcess 返回 0x{status:X8}", $"pid={pid}");
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    /// <summary>进程是否还活着（用于状态刷新）。</summary>
    public static bool Alive(uint pid)
    {
        try { return Process.GetProcessById((int)pid) is not null; }
        catch { return false; }
    }
}
