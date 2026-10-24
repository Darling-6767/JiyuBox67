using WinForms = System.Windows.Forms;

namespace FakePeer;

/// <summary>
/// 端到端测试用的“假极域学生端”：一个全屏、置顶、无边框的黑窗口，
/// 进程名 FakePeer.exe，可被 adapter.json 当成目标识别。
/// 启动后把窗口句柄写到 %TEMP%\fakepeer_hwnd.txt，方便外部校验样式。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        WinForms.Application.EnableVisualStyles();

        var form = new WinForms.Form
        {
            Text = "FakeBroadcast",
            FormBorderStyle = WinForms.FormBorderStyle.None,
            TopMost = true,
            ShowInTaskbar = true,
            WindowState = WinForms.FormWindowState.Maximized,
            BackColor = System.Drawing.Color.Black,
            KeyPreview = true
        };

        form.Shown += (_, _) =>
        {
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "fakepeer_hwnd.txt");
                File.WriteAllText(path, $"{form.Handle.ToInt64()} {Environment.ProcessId}");
            }
            catch { /* 测试工具，忽略 */ }
        };

        // 按 Esc 退出，方便手工测试
        form.KeyDown += (_, e) => { if (e.KeyCode == WinForms.Keys.Escape) form.Close(); };

        WinForms.Application.Run(form);
    }
}
