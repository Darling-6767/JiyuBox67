using System.Diagnostics;
using JiyuBox.Core;
using WinForms = System.Windows.Forms;

namespace JiyuBox.Smoke;

/// <summary>
/// 冒烟测试：不需要装极域、不需要管理员，验证 Core 的窗口/进程动作真的生效。
/// 做法：本进程开一个“假广播窗口”（全屏 + 置顶 + 无边框），然后走正式代码去窗口化它。
/// </summary>
internal static class Program
{
    private static readonly List<string> Results = new();
    private static int _failed;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        WinForms.Application.EnableVisualStyles();

        var form = new WinForms.Form
        {
            Text = "FakeBroadcast",
            FormBorderStyle = WinForms.FormBorderStyle.None,
            TopMost = true,
            ShowInTaskbar = false,
            WindowState = WinForms.FormWindowState.Maximized,
            BackColor = System.Drawing.Color.Black
        };

        form.Shown += (_, _) =>
        {
            try
            {
                RunTests(form);
            }
            catch (Exception ex)
            {
                Fail("未捕获异常：" + ex);
            }
            finally
            {
                form.Close();
            }
        };

        WinForms.Application.Run(form);

        Console.WriteLine();
        Console.WriteLine("================ 冒烟测试结果 ================");
        foreach (var line in Results) Console.WriteLine(line);
        Console.WriteLine(_failed == 0 ? "全部通过" : $"失败 {_failed} 项");
        return _failed == 0 ? 0 : 1;
    }

    private static void RunTests(WinForms.Form form)
    {
        var hwnd = form.Handle;
        var pid = (uint)Environment.ProcessId;

        // 1. 描述窗口
        var info = Probe.Describe(hwnd, pid);
        Check("Describe 能识别全屏置顶窗口",
            info is not null && info.FullScreen && info.Topmost && !info.Captioned,
            info?.ToString() ?? "null");

        // 2. 窗口化
        var windows = new WindowService();
        var r1 = windows.Windowize(hwnd);
        var after = Probe.Describe(hwnd, pid);
        Check("Windowize 成功返回", r1.Success, r1.Message + " " + r1.Evidence);
        Check("窗口化后带有标题栏", after is not null && after.Captioned,
            $"style=0x{after?.Style:X8}");
        Check("窗口化后不再是置顶", after is not null && !after.Topmost,
            $"ex=0x{after?.ExStyle:X8}");
        Check("窗口化后不再是全屏", after is not null && !after.FullScreen,
            $"{after?.Width}x{after?.Height}");

        // 3. 还原
        var r2 = windows.RestoreAll();
        var restored = Probe.Describe(hwnd, pid);
        Check("RestoreAll 成功返回", r2.Success, r2.Message);
        Check("还原后恢复全屏置顶",
            restored is not null && restored.Topmost && restored.FullScreen,
            restored?.ToString() ?? "null");

        // 4. 挂起 / 恢复（用一个无害的 cmd 进程当靶子）
        var proc = new ProcessService();
        using var victim = Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 30 /nobreak >nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
        if (victim is null)
        {
            Fail("无法启动测试进程");
        }
        else
        {
            Thread.Sleep(300);
            var s = proc.Suspend((uint)victim.Id);
            Check("NtSuspendProcess 返回成功", s.Success, s.Message + " " + s.Evidence);

            var alive = !victim.HasExited;
            Check("挂起后进程仍在（不是被杀）", alive, $"HasExited={victim.HasExited}");

            var rr = proc.Resume((uint)victim.Id);
            Check("NtResumeProcess 返回成功", rr.Success, rr.Message);

            try { victim.Kill(); } catch { /* 已退出 */ }
        }

        // 5. 键鼠锁解除（无锁状态下只是幂等调用）
        using var input = new InputUnlockService();
        var i1 = input.Enable();
        Thread.Sleep(300);
        var i2 = input.Disable();
        Check("InputUnlock 可开关", i1.Success && i2.Success && !input.Enabled,
            i1.Message + " / " + i2.Message);

        // 6. 适配档与探测（本机没装极域，应当正常返回“未检测到”而不是抛异常）
        var probe = new Probe(AdapterProfile.Jiyu2021());
        var scan = probe.Scan();
        Check("Probe 在未安装环境下不抛异常", scan is not null, scan.Summary);
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (!ok) _failed++;
        Results.Add($"{(ok ? "PASS" : "FAIL")}  {name}　{detail}");
    }

    private static void Fail(string message)
    {
        _failed++;
        Results.Add("FAIL  " + message);
    }
}
