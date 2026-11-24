using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Microsoft.Web.WebView2.Core;
using DrawingColorTranslator = System.Drawing.ColorTranslator;
using DrawingRectangle = System.Drawing.Rectangle;

namespace bms_editer.Views.Controls;

public sealed class VideoPreviewControl : Control, IDisposable
{
    private const string PreviewHostName = "bms-video-preview.local";

    private CoreWebView2Controller? _controller;
    private CoreWebView2? _webView;
    private Task? _initializationTask;
    private string? _pendingVideoPath;
    private bool _hasVideo;
    private bool _isPlaying;
    private double _lastRequestedSeconds;
    private string _statusText = "VIDEO";
    private string? _cachedStatusText;
    private FormattedText? _cachedStatusFormattedText;
    private int _videoLoadGeneration;

    private static readonly IBrush BackgroundBrush = new ImmutableSolidColorBrush(Color.FromRgb(10, 10, 12));
    private static readonly IBrush StatusTextBrush = new ImmutableSolidColorBrush(Color.FromArgb(150, 210, 210, 210));
    private static readonly Typeface StatusTypeface = new("Inter, Arial, sans-serif");

    public VideoPreviewControl()
    {
        ClipToBounds = true;
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    // 스크롤로 보이는 부분. 네이티브 창이라 Avalonia 의 클립이 안 먹어서 직접 잘라야 한다.
    private Rect? _effectiveViewport;

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        _effectiveViewport = e.EffectiveViewport;
        UpdateBounds();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(BackgroundBrush, Bounds);
        if (!_hasVideo || _webView is null)
        {
            if (_cachedStatusFormattedText is null || _cachedStatusText != _statusText)
            {
                _cachedStatusText = _statusText;
                _cachedStatusFormattedText = new FormattedText(
                    _statusText,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    StatusTypeface,
                    13,
                    StatusTextBrush);
            }

            context.DrawText(
                _cachedStatusFormattedText,
                new Point((Bounds.Width - _cachedStatusFormattedText.Width) / 2, (Bounds.Height - _cachedStatusFormattedText.Height) / 2));
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _disposed = false;
        LayoutUpdated += OnLayoutUpdated;

        // 비디오가 실제로 설정된 경우에만 WebView2 를 기동한다(지연 초기화).
        // 비디오 없는 차트에서 불필요한 WebView2 프로세스를 띄우지 않고, 런타임 미설치 PC 에서도 시작 오류를 막는다.
        if (_hasVideo && _pendingVideoPath is not null)
            _ = ReattachAsync();
    }

    private async Task ReattachAsync()
    {
        var generation = _videoLoadGeneration;
        await EnsureWebViewAsync();
        if (_disposed || generation != _videoLoadGeneration || !_hasVideo || _pendingVideoPath is not { } path || _webView is null)
            return;

        MapVideoFolder(path);
        NavigateSafe(BuildVideoHtml(path, _lastRequestedSeconds));
        UpdateBounds();
        PauseAt(_lastRequestedSeconds);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LayoutUpdated -= OnLayoutUpdated;
        Dispose();
        _initializationTask = null;
        base.OnDetachedFromVisualTree(e);
    }

    public async void LoadVideo(string filePath)
    {
        var generation = ++_videoLoadGeneration;
        _pendingVideoPath = filePath;
        _hasVideo = true;
        _statusText = "비디오 로딩 중";
        InvalidateVisual();

        await EnsureWebViewAsync();
        if (_disposed || generation != _videoLoadGeneration || !_hasVideo)
            return;

        if (_webView is null)
        {
            _statusText = "비디오 엔진 초기화 실패";
            InvalidateVisual();
            return;
        }

        MapVideoFolder(filePath);
        NavigateSafe(BuildVideoHtml(filePath, _lastRequestedSeconds));
        UpdateBounds();
        PauseAt(_lastRequestedSeconds);
    }

    public void ClearVideo()
    {
        _videoLoadGeneration++;
        _pendingVideoPath = null;
        _hasVideo = false;
        _isPlaying = false;
        _statusText = "VIDEO";
        InvalidateVisual();

        // 이미 WebView2 가 떠 있을 때만 비운다. 비디오를 연 적 없는데 엔진을 기동하지 않는다.
        if (_webView is not null)
            NavigateSafe(BuildEmptyHtml());
    }

    private double _playbackRate = 1.0;

    public void SetPlaybackRate(double rate)
    {
        _playbackRate = Math.Clamp(rate, 0.05, 4.0);
        var rateText = _playbackRate.ToString("0.###", CultureInfo.InvariantCulture);
        ExecuteVideoScript($"(() => {{ const v = document.getElementById('previewVideo'); if (v) v.playbackRate = {rateText}; }})()");
    }

    public void PlayFrom(double seconds)
    {
        _isPlaying = true;
        _lastSyncedSeconds = double.NegativeInfinity;
        _lastRequestedSeconds = Math.Max(0, seconds);
        ExecuteVideoScript(SetTimeScript(_lastRequestedSeconds, 0.12, play: true, _playbackRate));
    }

    public void PauseAt(double seconds)
    {
        _isPlaying = false;
        _lastSyncedSeconds = double.NegativeInfinity;
        _lastRequestedSeconds = Math.Max(0, seconds);
        ExecuteVideoScript(SetTimeScript(_lastRequestedSeconds, 0.06, play: false, _playbackRate));
    }

    // 마지막으로 WebView2 에 실제로 보낸 시각. 너무 자주 보내지 않으려고 기억해 둔다.
    private double _lastSyncedSeconds = double.NegativeInfinity;

    // 재생 중 커서 위치가 바뀔 때마다 불린다(16ms마다, 초당 약 60번).
    //
    // 예전에는 그때마다 ExecuteScriptAsync 를 보내서 초당 30번 IPC 왕복이 돌았다.
    // 오차 검사는 어차피 JS 안에서 하므로 **보낼 필요 없는 호출까지 전부 나갔다.**
    // 0.2초에 한 번이면 영상 동기화에 충분하다.
    private const double VideoSyncIntervalSeconds = 0.2;

    public void SyncTo(double seconds)
    {
        _lastRequestedSeconds = Math.Max(0, seconds);

        // 뒤로 감았거나(스크럽) 충분히 시간이 지났을 때만 보낸다.
        if (Math.Abs(_lastRequestedSeconds - _lastSyncedSeconds) < VideoSyncIntervalSeconds)
            return;

        _lastSyncedSeconds = _lastRequestedSeconds;

        if (_isPlaying)
            ExecuteVideoScript(SetTimeScript(_lastRequestedSeconds, 0.25, play: true, _playbackRate));
        else
            ExecuteVideoScript(SetTimeScript(_lastRequestedSeconds, 0.06, play: false, _playbackRate));
    }

    // 이미 떼어냈는지. 초기화가 끝나기 전에 떼어내면 이 표시를 보고 바로 정리한다.
    //
    // 예전에는 Dispose 가 먼저 돌고 뒤늦게 CreateCoreWebView2ControllerAsync 가 완료되면
    // _controller 에 살아 있는 컨트롤러가 대입되고 아무도 Close() 하지 않았다.
    // WebView2 프로세스가 그대로 남았다.
    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        _lastBounds = null;
        _lastIsVisible = false;
        if (_webView is not null)
        {
            _webView.ProcessFailed -= OnWebViewProcessFailed;
            _webView = null;
        }
        try
        {
            _controller?.Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebView2 Close 오류: {ex.Message}");
        }
        _controller = null;
    }

    private void OnWebViewProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            _webView = null;
            _controller = null;
            _initializationTask = null;
            _lastBounds = null;
            _statusText = "비디오 엔진이 예기치 않게 종료되었습니다";
            InvalidateVisual();
        }
    }

    private async Task EnsureWebViewAsync()
    {
        if (_disposed || _controller is not null)
            return;

        if (_initializationTask is not null)
        {
            await _initializationTask;
            if (_controller is not null)
                return;

            _initializationTask = null;
        }

        _initializationTask = InitializeWebViewAsync();
        await _initializationTask;
        if (_controller is null)
            _initializationTask = null;
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            var handle = topLevel?.TryGetPlatformHandle();
            if (handle is null || handle.Handle == IntPtr.Zero)
                return;

            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Services.CrashLog.AppDataFolderName,
                "WebView2");

            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            var controller = await environment.CreateCoreWebView2ControllerAsync(handle.Handle);

            // 만드는 사이에 떼어냈으면 여기서 닫는다. 안 그러면 WebView2 프로세스가 남는다.
            if (_disposed)
            {
                controller.Close();
                return;
            }

            _controller = controller;
            _controller.DefaultBackgroundColor = DrawingColorTranslator.FromHtml("#0A0A0C");
            _controller.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
            _controller.IsVisible = IsEffectivelyVisible;

            _webView = _controller.CoreWebView2;
            _webView.ProcessFailed += OnWebViewProcessFailed;
            _webView.Settings.AreDefaultContextMenusEnabled = false;
            _webView.Settings.AreDevToolsEnabled = false;
            NavigateSafe(BuildEmptyHtml());

            if (_hasVideo && _pendingVideoPath is not null)
            {
                MapVideoFolder(_pendingVideoPath);
                NavigateSafe(BuildVideoHtml(_pendingVideoPath, _lastRequestedSeconds));
            }

            UpdateBounds();
            InvalidateVisual();
        }
        catch (Exception ex)
        {
            _statusText = $"비디오 엔진 오류: {ex.Message}";
            InvalidateVisual();
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => UpdateBounds();

    // 마지막으로 WebView2 에 넘긴 값. 안 바뀌었으면 COM 호출을 하지 않는다.
    //
    // LayoutUpdated 는 창 어디든 레이아웃이 돌 때마다 온다. 그때마다 Bounds 대입과
    // NotifyParentWindowPositionChanged 로 프로세스 간 호출을 두 번씩 하고 있었다.
    private DrawingRectangle? _lastBounds;
    private bool _lastIsVisible;

    private void UpdateBounds()
    {
        if (_controller is null)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not Visual topLevelVisual)
            return;

        var origin = this.TranslatePoint(new Point(0, 0), topLevelVisual);
        if (origin is null)
            return;

        // 스크롤로 가려진 만큼 잘라 넣는다. 안 자르면 오른쪽 패널을 스크롤할 때 영상이 툴바 위로 삐져나온다.
        var visible = new Rect(Bounds.Size);
        if (_effectiveViewport is { } viewport)
            visible = visible.Intersect(viewport);

        var scale = topLevel.RenderScaling;
        var bounds = new DrawingRectangle(
            (int)Math.Round((origin.Value.X + visible.X) * scale),
            (int)Math.Round((origin.Value.Y + visible.Y) * scale),
            Math.Max(1, (int)Math.Round(visible.Width * scale)),
            Math.Max(1, (int)Math.Round(visible.Height * scale)));
        var isVisible = IsEffectivelyVisible && visible.Width > 0 && visible.Height > 0;

        if (_lastBounds == bounds && _lastIsVisible == isVisible)
            return;

        try
        {
            _controller.IsVisible = isVisible;
            _controller.Bounds = bounds;
            _controller.NotifyParentWindowPositionChanged();
            _lastBounds = bounds;
            _lastIsVisible = isVisible;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebView2 Bounds 갱신 오류: {ex.Message}");
        }
    }

    private void NavigateSafe(string html)
    {
        if (_webView is null)
            return;

        try
        {
            _webView.NavigateToString(html);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebView2 Navigate 오류: {ex.Message}");
        }
    }

    private void ExecuteVideoScript(string script)
    {
        if (!_hasVideo || _webView is null)
            return;

        try
        {
            _ = _webView.ExecuteScriptAsync(script).ContinueWith(
                static t => System.Diagnostics.Debug.WriteLine($"WebView2 스크립트 오류: {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebView2 스크립트 호출 오류: {ex.Message}");
        }
    }

    private void MapVideoFolder(string filePath)
    {
        if (_webView is null)
            return;

        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        try
        {
            _webView.SetVirtualHostNameToFolderMapping(
                PreviewHostName,
                directory,
                CoreWebView2HostResourceAccessKind.Allow);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebView2 폴더 매핑 오류: {ex.Message}");
        }
    }

    private static string SetTimeScript(double seconds, double tolerance, bool play, double playbackRate)
    {
        var secondsText = seconds.ToString("0.###", CultureInfo.InvariantCulture);
        var toleranceText = tolerance.ToString("0.###", CultureInfo.InvariantCulture);
        var rateText = Math.Clamp(playbackRate, 0.05, 4.0).ToString("0.###", CultureInfo.InvariantCulture);
        var action = play ? "v.play().catch(()=>{});" : "v.pause();";
        return
            "(() => {" +
            "const v = document.getElementById('previewVideo');" +
            "if (!v) return;" +
            "v.playbackRate = " + rateText + ";" +
            "const t = " + secondsText + ";" +
            "if (Number.isFinite(v.duration) && t > v.duration) { v.pause(); return; }" +
            "if (Math.abs(v.currentTime - t) > " + toleranceText + ") v.currentTime = t;" +
            action +
            "})()";
    }

    private static string BuildVideoHtml(string filePath, double initialSeconds)
    {
        var fileName = Uri.EscapeDataString(Path.GetFileName(filePath));
        var uri = JsonSerializer.Serialize($"https://{PreviewHostName}/{fileName}");
        var initialSecondsText = Math.Max(0, initialSeconds).ToString("0.###", CultureInfo.InvariantCulture);
        return
            "<!doctype html><html><head><meta charset=\"utf-8\">" +
            "<style>html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#0a0a0c}" +
            "video{width:100%;height:100%;object-fit:contain;background:#0a0a0c;display:block}" +
            "#msg{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;color:#cfcfcf;" +
            "font:13px Inter,Arial,sans-serif;text-align:center;padding:14px;box-sizing:border-box}</style></head>" +
            "<body><video id=\"previewVideo\" src=" + uri + " muted playsinline preload=\"auto\"></video>" +
            "<div id=\"msg\">비디오 로딩 중</div><script>" +
            "const v=document.getElementById('previewVideo');const msg=document.getElementById('msg');" +
            "const initial=" + initialSecondsText + ";" +
            "v.addEventListener('loadedmetadata',()=>{msg.style.display='none';try{v.currentTime=initial;}catch(e){}});" +
            "v.addEventListener('canplay',()=>{msg.style.display='none';});" +
            "v.addEventListener('error',()=>{msg.textContent='이 비디오 형식/코덱은 현재 프리뷰에서 지원되지 않습니다. MP4(H.264) 또는 WebM을 권장합니다.';});" +
            "</script></body></html>";
    }

    private static string BuildEmptyHtml() =>
        "<!doctype html><html><head><meta charset=\"utf-8\">" +
        "<style>html,body{margin:0;width:100%;height:100%;background:#0a0a0c}</style></head><body></body></html>";
}
