using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using bms_editer.Services;
using bms_editer.Views;

namespace bms_editer;

public partial class App : Application
{
    // 탐색기에서 BMS 파일을 더블클릭해 실행됐을 때 그 파일 경로. Program.Main 이 채운다.
    public static string? StartupFilePath { get; set; }

    private static readonly object ExternalOpenLock = new();
    private static System.Action<string?>? _externalOpenRequested;
    private static string? _pendingExternalOpen;

    // 단일 인스턴스 파이프는 Avalonia 를 띄우기 전에 열린다. 창이 아직 구독하기 전에
    // 두 번째 실행이 경로를 넘기면 예전에는 아무 말 없이 버려졌다. 받는 쪽이 없으면
    // 마지막 경로 하나를 맡아 두었다가 창이 시작을 마친 뒤 연다.
    public static event System.Action<string?>? ExternalOpenRequested
    {
        add { lock (ExternalOpenLock) _externalOpenRequested += value; }
        remove { lock (ExternalOpenLock) _externalOpenRequested -= value; }
    }

    public static void RaiseExternalOpenRequested(string? filePath)
    {
        System.Action<string?>? handler;
        lock (ExternalOpenLock)
        {
            handler = _externalOpenRequested;
            if (handler is null)
            {
                if (!string.IsNullOrWhiteSpace(filePath))
                    _pendingExternalOpen = filePath;
                return;
            }
        }

        handler(filePath);
    }

    public static string? TakePendingExternalOpen()
    {
        lock (ExternalOpenLock)
        {
            var path = _pendingExternalOpen;
            _pendingExternalOpen = null;
            return path;
        }
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // 죽기 직전에 부르는 비상 저장. 다른 스레드에서도 불릴 수 있어 창을 거치지 않고 뷰모델을 직접 잡아 둔다.
    public static System.Func<bool>? EmergencySave { get; private set; }

    private MainWindow? _mainWindow;
    private bool _isShowingErrorNotice;
    private long _lastExceptionRecordMs = long.MinValue;
    internal const long ExceptionRecordCooldownMs = 5000;

    public static bool ShouldRecordUiException(long nowMs, ref long lastRecordMs, long cooldownMs = ExceptionRecordCooldownMs)
    {
        if (lastRecordMs != long.MinValue && nowMs - lastRecordMs < cooldownMs)
            return false;

        lastRecordMs = nowMs;
        return true;
    }

    // 클릭 처리기 하나에서 난 예외가 앱을 그대로 끄면 저장 안 한 작업이 전부 사라진다.
    // 기록을 남기고 알린 뒤 계속 쓸 수 있게 둔다. 사용자가 그 자리에서 저장할 수 있다.
    // 재생 타이머 등 반복 루프에서 예외가 연달아 날 때 디스크 저장·로그 폭주를 막기 위해 쿨다운을 둔다.
    private void OnUiThreadException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        if (!ShouldRecordUiException(System.Environment.TickCount64, ref _lastExceptionRecordMs))
            return;

        var logPath = CrashLog.Write(e.Exception, "UI");
        var saved = EmergencySave?.Invoke() == true;

        if (_isShowingErrorNotice || _mainWindow is not { IsVisible: true } owner)
            return;

        _isShowingErrorNotice = true;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await ConfirmWindow.ShowMessageAsync(
                    owner,
                    $"예상하지 못한 오류가 났습니다. 작업 내용을 먼저 저장해 주십시오.\n\n{e.Exception.Message}"
                    + (saved ? "\n\n지금까지의 작업은 자동 저장에 남겨 두었습니다." : "")
                    + (logPath is null ? "" : $"\n\n기록: {logPath}"),
                    "오류");
            }
            finally
            {
                _isShowingErrorNotice = false;
            }
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += OnUiThreadException;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            _mainWindow = mainWindow;
            desktop.MainWindow = mainWindow;

            mainWindow.EnableDesktopServices();
            if (mainWindow.DataContext is ViewModels.MainWindowViewModel vm)
                EmergencySave = vm.EmergencySave;

            mainWindow.Opened += async (_, _) => await mainWindow.RunStartupAsync(StartupFilePath);
        }

        base.OnFrameworkInitializationCompleted();
    }
}