using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using bms_editer.Services;

namespace bms_editer;

internal class Program
{
    private const string SingleInstanceMutexName = @"Local\" + AppIdentity.SingleInstanceName + "_SingleInstance";

    // 뮤텍스는 Local\ 이라 로그인 세션마다 따로지만 파이프 이름은 PC 전체에서 하나다.
    // 세션 번호를 붙이지 않으면 다른 계정이 먼저 띄운 에디터가 파이프를 쥐고 있어
    // 이쪽 서버가 만들어지지 않는다.
    private static readonly string SingleInstancePipeName =
        $"{AppIdentity.SingleInstanceName}_IpcPipe_{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception fatal)
                CrashLog.Write(fatal, "AppDomain");

            // 이 뒤로 프로세스가 끝난다. 살릴 수는 없어도 작업은 남긴다.
            App.EmergencySave?.Invoke();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception, "Task");
            e.SetObserved();
        };

        try
        {
            var startupPath = args.FirstOrDefault(IsChartFile);

            using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
            if (!createdNew && TryForwardToRunningInstance(startupPath))
                return;

            using var pipeCts = new CancellationTokenSource();
            if (createdNew)
                StartSingleInstanceServer(pipeCts.Token);

            App.StartupFilePath = startupPath;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            pipeCts.Cancel();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "Main");
            Console.Error.WriteLine(ex);
            throw;
        }
    }

    private static bool TryForwardToRunningInstance(string? filePath)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", SingleInstancePipeName, PipeDirection.Out);
            client.Connect(timeout: 1000);
            using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true);
            writer.Write(filePath ?? "");
            writer.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void StartSingleInstanceServer(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        SingleInstancePipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(cancellationToken);
                    using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                    var path = await reader.ReadToEndAsync(cancellationToken);
                    App.RaiseExternalOpenRequested(string.IsNullOrWhiteSpace(path) ? null : path);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // 파이프 일시 오류는 다음 연결을 계속 받는다.
                    // 같은 오류가 곧바로 되풀이되면(이름을 남이 쥐고 있음 등) 쉬지 않고 돌며
                    // CPU 한 코어를 다 먹으므로 잠깐 쉬었다 다시 연다.
                    try { await Task.Delay(500, cancellationToken); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }, cancellationToken);
    }

    // 탐색기 연결로 넘어온 인자 중 실제 차트 파일만 골라낸다.
    private static bool IsChartFile(string path)
        => File.Exists(path) && FileAssociation.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
