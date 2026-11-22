using System.Linq;
using System.ComponentModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using bms_editer.Models;
using bms_editer.Services;
using bms_editer.ViewModels;
using bms_editer.Views;
using bms_editer.Views.Controls;

namespace bms_editer;

public partial class MainWindow : Window
{
        // 지금 떠 있는 모드리스 보조 창(검색/통계/키음 팔레트). 종류당 하나만 띄운다.
        private readonly Dictionary<Type, Window> _toolWindows = new();
        private MainWindowViewModel? _boundVm;
        private readonly MainWindowViewModel _initialVm;

        public MainWindow()
        {
            InitializeComponent();
            _initialVm = new MainWindowViewModel();
            DataContext = _initialVm;
            Waveform.ScrubRequested += OnWaveformScrubRequested;
            Closing += OnWindowClosing;
            Closed += (_, _) =>
            {
                foreach (var toolWindow in _toolWindows.Values.ToArray())
                    toolWindow.Close();

                SaveSettingsIfEnabled();
                (DataContext as IDisposable)?.Dispose();
            };
            AddHandler(PointerPressedEvent, OnWindowPointerPressedTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(KeyDownEvent, OnWindowKeyDownTunnel, RoutingStrategies.Tunnel);

            // 목록 항목이 이벤트를 소비해도 받아야 한다.
            WavListBox.AddHandler(PointerReleasedEvent, OnWavListPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
            EditorScrollViewer.AddHandler(PointerWheelChangedEvent, OnEditorWheelTunnel, RoutingStrategies.Tunnel);
            UpdateEditorOrientation();
            UpdateWindowTitle();

            try
            {
                var iconFile = MainWindowViewModel.IsPersonalEdition ? "icon_personal.ico" : "icon_release.ico";

                // 어셈블리 이름은 배포 프로필마다 다르다("muse dash 2 bms editer 1" / "… 2").
                // 이름을 박아 두면 배포본에서만 그림을 못 찾아 1번·2번 아이콘이 안 뜬다.
                var assemblyName = typeof(MainWindow).Assembly.GetName().Name;
                var iconUri = new Uri($"avares://{assemblyName}/Assets/{iconFile}");
                if (Avalonia.Platform.AssetLoader.Exists(iconUri))
                    Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(iconUri));
            }
            catch
            {
                // 기본 아이콘 폴백
            }
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (_boundVm is not null)
            {
                _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
                _boundVm.ScrollToRatioRequested -= ScrollEditorToRatio;
                _boundVm.UserMessageRequested -= OnUserMessageRequested;

                if (ReferenceEquals(_boundVm, _initialVm) && !ReferenceEquals(DataContext, _initialVm))
                    _initialVm.Dispose();
            }

            _boundVm = DataContext as MainWindowViewModel;
            if (_boundVm is not null)
            {
                _boundVm.ConfirmAsync ??= message => ConfirmWindow.ShowAsync(this, message, "확인");
                _boundVm.PropertyChanged += OnViewModelPropertyChanged;
                _boundVm.ScrollToRatioRequested += ScrollEditorToRatio;
                _boundVm.UserMessageRequested += OnUserMessageRequested;
                UpdateEditorOrientation();
                UpdateWindowTitle();
            }
        }

        private async void OnUserMessageRequested(string message) =>
            await ConfirmWindow.ShowMessageAsync(this, message, "알림");

        // 창을 닫을 때 저장 안 한 작업이 사라지지 않게 막는다.
        //
        // 새로 만들기·열기에는 확인창이 있었는데 정작 창 닫기에는 없었다.
        // Closing 은 await 할 수 없으므로, 일단 닫기를 취소하고 물어본 뒤
        // 사용자가 정말 닫겠다고 하면 그때 다시 Close() 한다.
        private bool _isClosingConfirmed;

        private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
        {
            CaptureWindowPlacement();

            if (_isClosingConfirmed || DataContext is not MainWindowViewModel vm || !vm.IsDirty)
                return;

            e.Cancel = true;

            var name = vm.CurrentFilePath is { } path ? Path.GetFileName(path) : "제목 없음";
            var choice = await ConfirmWindow.ShowThreeWayAsync(
                this,
                $"'{name}' 의 바뀐 내용을 저장하지 않았습니다.\n\n저장하고 닫을까요?",
                confirmText: "저장하고 닫기",
                alternateText: "저장 안 함",
                title: "종료");

            switch (choice)
            {
                case ConfirmChoice.Cancel:
                    return;

                case ConfirmChoice.Confirm:
                    var savePath = vm.CurrentFilePath ?? await PickSavePathAsync(vm);
                    if (savePath is null || !await EnsureMayOverwriteAsync(vm, savePath))
                        return;

                    if (!vm.SaveBms(savePath))
                    {
                        await ConfirmWindow.ShowMessageAsync(
                            this, $"저장하지 못했습니다. 창을 닫지 않았습니다.\n\n{vm.LastErrorMessage}", "저장 실패");
                        return;
                    }
                    break;
            }

            // "저장 안 함"을 고른 작업을 다음 실행에서 복구하라고 묻지 않는다.
            vm.DiscardAutoSave();

            _isClosingConfirmed = true;
            Close();
        }

        private void UpdateWindowTitle()
        {
            if (DataContext is MainWindowViewModel vm)
                Title = vm.WindowTitle;
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.PlaybackPositionSeconds))
            {
                FollowPlaybackCursorIfNeeded();
                if (sender is MainWindowViewModel vm)
                    VideoPreview.SyncTo(vm.PlaybackPositionSeconds);
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.IsHorizontalView))
            {
                UpdateEditorOrientation();
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.WindowTitle))
            {
                UpdateWindowTitle();
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.CurrentFilePath))
            {
                if (sender is MainWindowViewModel { CurrentFilePath: { } current })
                    RememberRecentFile(current);
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.IsPlaying))
            {
                if (sender is not MainWindowViewModel vm)
                    return;

                if (vm.IsPlaying)
                    VideoPreview.PlayFrom(vm.PlaybackPositionSeconds);
                else
                    VideoPreview.PauseAt(vm.PlaybackPositionSeconds);
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.VideoFilePath))
            {
                if (sender is not MainWindowViewModel vm)
                    return;

                if (vm.VideoFilePath is { } videoPath)
                    VideoPreview.LoadVideo(videoPath);
                else
                    VideoPreview.ClearVideo();
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.PlaybackSpeed))
            {
                if (sender is MainWindowViewModel vm)
                    VideoPreview.SetPlaybackRate(vm.PlaybackSpeed);
            }
        }

        private void UpdateEditorOrientation()
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            if (vm.IsHorizontalView)
            {
                EditorSurface.Orientation = Avalonia.Layout.Orientation.Vertical;
                Waveform.Width = double.NaN;
                Waveform.Height = 220;
            }
            else
            {
                EditorSurface.Orientation = Avalonia.Layout.Orientation.Horizontal;
                Waveform.Width = 220;
                Waveform.Height = double.NaN;
            }
        }

        // 통계·검색 창에서 고른 노트가 있는 자리로 격자를 옮긴다.
        //
        // 이게 없으면 통계 창에서 키음 번호를 눌러도 화면에서는 아무 일도 안 일어난 것처럼
        // 보인다. 선택은 됐는데 그 노트들이 보이는 구간 밖에 있기 때문이다.
        //
        // 선택 직후에는 아직 레이아웃이 끝나지 않아 Viewport 가 0일 수 있으므로
        // 한 박자 뒤에 옮긴다.
        private void ScrollEditorToRatio(double ratio)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is not MainWindowViewModel vm)
                    return;

                var timelineLength = GetTimelineLength(vm);
                if (timelineLength <= 0)
                    return;

                var offset = EditorScrollViewer.Offset;

                if (vm.IsHorizontalView)
                {
                    var viewport = EditorScrollViewer.Viewport.Width;
                    if (viewport <= 0)
                        return;

                    var target = (ratio * timelineLength) - (viewport * 0.5);
                    var max = Math.Max(0, EditorSurface.Bounds.Width - viewport);
                    EditorScrollViewer.Offset = new Vector(Math.Clamp(target, 0, max), offset.Y);
                }
                else
                {
                    var viewport = EditorScrollViewer.Viewport.Height;
                    if (viewport <= 0)
                        return;

                    var target = ((1.0 - ratio) * timelineLength) - (viewport * 0.5);
                    var max = Math.Max(0, EditorSurface.Bounds.Height - viewport);
                    EditorScrollViewer.Offset = new Vector(offset.X, Math.Clamp(target, 0, max));
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        private void FollowPlaybackCursorIfNeeded()
        {
            if (DataContext is not MainWindowViewModel vm ||
                !vm.FollowPlaybackCursor ||
                !vm.IsPlaybackCursorVisible ||
                vm.OggDurationSeconds <= 0)
            {
                return;
            }

            var timelineLength = GetTimelineLength(vm);
            if (timelineLength <= 0)
                return;

            var offset = EditorScrollViewer.Offset;

            if (vm.IsHorizontalView)
            {
                if (EditorScrollViewer.Viewport.Width <= 0)
                    return;

                var cursorX = vm.PlaybackCursorRatio * timelineLength;
                var leftMargin = EditorScrollViewer.Viewport.Width * 0.25;
                var rightMargin = EditorScrollViewer.Viewport.Width * 0.75;
                var cursorInView = cursorX - offset.X;

                if (cursorInView < leftMargin || cursorInView > rightMargin)
                {
                    var targetX = cursorX - (EditorScrollViewer.Viewport.Width * 0.5);
                    var maxX = Math.Max(0, EditorSurface.Bounds.Width - EditorScrollViewer.Viewport.Width);
                    targetX = Math.Clamp(targetX, 0, maxX);
                    EditorScrollViewer.Offset = new Vector(targetX, offset.Y);
                }
            }
            else
            {
                if (EditorScrollViewer.Viewport.Height <= 0)
                    return;

                var cursorY = (1.0 - vm.PlaybackCursorRatio) * timelineLength;
                var topMargin = EditorScrollViewer.Viewport.Height * 0.25;
                var bottomMargin = EditorScrollViewer.Viewport.Height * 0.75;
                var cursorInView = cursorY - offset.Y;

                if (cursorInView < topMargin || cursorInView > bottomMargin)
                {
                    var targetY = cursorY - (EditorScrollViewer.Viewport.Height * 0.5);
                    var maxY = Math.Max(0, EditorSurface.Bounds.Height - EditorScrollViewer.Viewport.Height);
                    targetY = Math.Clamp(targetY, 0, maxY);
                    EditorScrollViewer.Offset = new Vector(offset.X, targetY);
                }
            }
        }

        // 격자·파형 컨트롤과 같은 규칙을 그대로 부른다. 식을 여기 따로 두지 않는다.
        private static double GetTimelineLength(MainWindowViewModel vm) =>
            TimelineControlBase.ComputeTimelineLength(
                vm.OggDurationSeconds, vm.MeasureCount, vm.RowHeight, vm.VerticalZoom, vm.BeatSplit, vm.GridMeasure);
    }
