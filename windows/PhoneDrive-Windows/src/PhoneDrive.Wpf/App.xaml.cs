using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using PhoneDrive.Wpf.Services;

namespace PhoneDrive.Wpf;

public partial class App : Application
{
    public static string DisplayVersion => typeof(App).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";
    private FileStream? _instanceLock;
    private static readonly string CrashLogPath = Path.Combine(PhoneDrive.Core.AppPaths.DataDirectory, "crash.log");

    public App()
    {
        // WinExe 没有控制台，异常必须落盘，否则用户只能描述“闪退”
        DispatcherUnhandledException += (_, e) =>
        {
            WriteCrash("UI 线程未处理异常", e.Exception);
            MessageBox.Show(
                $"操作失败：{e.Exception.Message}\n\n详细信息已写入:\n{CrashLogPath}",
                "PhoneDrive", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrash("AppDomain 未处理异常", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrash("未观察的任务异常", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(PhoneDrive.Core.AppPaths.DataDirectory);
            _instanceLock = new FileStream(Path.Combine(PhoneDrive.Core.AppPaths.DataDirectory, "instance.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            StartupUri = null;
            MessageBox.Show("PhoneDrive 已在运行，请从系统托盘打开已有窗口。", "PhoneDrive");
            Shutdown(); return;
        }
        ThemeManager.Apply(new UiStateStore().Load().Theme);
        PhoneDrive.Core.DiagnosticLog.Write("startup", $"PhoneDrive {DisplayVersion}; single instance; " + Environment.ProcessPath);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceLock?.Dispose();
        base.OnExit(e);
    }

    internal static void WriteCrash(string category, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            var text = new System.Text.StringBuilder();
            text.AppendLine("========================================================");
            text.AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {category}");
            text.AppendLine(ex?.ToString() ?? "(无异常对象)");
            File.AppendAllText(CrashLogPath, text.ToString());
        }
        catch
        {
            // 日志失败不得引发二次崩溃
        }
    }
}
