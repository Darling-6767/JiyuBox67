using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using System.Windows.Threading;

namespace JiyuBox;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Global\JiyuBox.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("极域工具箱已经在运行了，请看任务栏托盘。", "JiyuBox",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                MessageBox.Show($"程序发生未处理异常：{args.ExceptionObject}", "JiyuBox",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* 退出路径上忽略 */ }
        };

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"操作失败：{e.Exception.Message}", "JiyuBox",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
