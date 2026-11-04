using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace PhoneDrive.Wpf.Services;

internal static class SingleInstanceActivation
{
    private static string PipeName => "PhoneDrive.Activate." + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(PhoneDrive.Core.AppPaths.DataDirectory.ToUpperInvariant())))[..24];

    public static void Signal()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(5000);
            client.WriteByte(1);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            MessageBox.Show("已有 PhoneDrive 正在启动或退出，请稍后再试。若旧版本尚未退出，请从托盘打开或退出旧版后再启动。", "PhoneDrive");
        }
    }

    public static async Task ListenAsync(Dispatcher dispatcher, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token);
                var data = new byte[1];
                using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
                request.CancelAfter(2000);
                try
                {
                    if (await server.ReadAsync(data, request.Token) == 1 && data[0] == 1)
                        await dispatcher.InvokeAsync(() =>
                        {
                            if (Application.Current.MainWindow is MainWindow window) window.RestoreMainWindow();
                        });
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (IOException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.WriteCrash("主窗口唤起监听失败", ex); }
    }
}
