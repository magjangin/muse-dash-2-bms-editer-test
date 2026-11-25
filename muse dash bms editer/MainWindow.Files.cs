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

public partial class MainWindow
{
        private async void OnLoadOggClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "OGG 파일 선택",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("OGG 오디오") { Patterns = new[] { "*.ogg" } },
                },
            });

            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is null)
                return;

            // 실패해도 이미 물려 있던 음원은 그대로 남는다. 사유만 알려준다.
            if (!await vm.LoadOggAsync(path))
                await ConfirmWindow.ShowMessageAsync(this, $"OGG를 불러오지 못했습니다.\n\n{vm.LastErrorMessage}", "OGG 로드 실패");
        }

        private async void OnLoadVideoClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "비디오 파일 선택",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("비디오 파일") { Patterns = new[] { "*.mp4", "*.webm", "*.mov", "*.avi", "*.mkv" } },
                },
            });

            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is null)
                return;

            vm.LoadVideo(path);
        }

        private async void OnOpenFolderClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "BMS 폴더 선택",
                AllowMultiple = false,
            });

            var folderPath = folders.FirstOrDefault()?.TryGetLocalPath();
            if (folderPath is null || !Directory.Exists(folderPath))
                return;

            // 확인은 **고를 것을 다 고른 뒤에** 받는다. 먼저 물으면, 선택을 취소해도
            // 이미 "버리시겠습니까"에 답한 뒤라 사용자만 헷갈린다.
            if (!await vm.ConfirmDiscardIfNeededAsync("현재 작업 중인 내용이 모두 사라집니다.\n폴더를 열까요?"))
                return;

            await LoadFolderMediaAsync(vm, folderPath);
        }

        private void OnClearVideoClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.ClearVideo();
        }

        private async void OnOpenFileClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "BMS 파일 선택",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("BMS 차트 파일") { Patterns = new[] { "*.bms", "*.bme", "*.bml" } },
                },
            });

            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is null)
                return;

            await OpenChartPathAsync(vm, path);
        }

        // 파일 메뉴·최근 파일·끌어다 놓기가 같이 쓴다.
        private async Task OpenChartPathAsync(MainWindowViewModel vm, string path)
        {
            // 확인은 열 파일을 고른 뒤에 받는다. (폴더 열기와 같은 이유)
            if (!await vm.ConfirmDiscardIfNeededAsync("현재 작업 중인 내용이 모두 사라집니다.\n파일을 열까요?"))
                return;

            if (!vm.LoadBms(path))
            {
                await ConfirmWindow.ShowMessageAsync(this, $"파일을 열지 못했습니다.\n\n{vm.LastErrorMessage}", "열기 실패");
                return;
            }

            // 앞 곡의 파형과 음원이 남지 않게 그 차트 폴더의 음원·영상을 같이 연다.
            var folderPath = Path.GetDirectoryName(path);
            if (folderPath is not null)
                await LoadFolderAudioAndVideoAsync(vm, folderPath);
        }

        // 탐색기 더블클릭 등 실행 인자로 받은 파일을 연다.
        // 창이 막 뜬 직후라 저장 안 한 내용이 없으므로 확인창을 띄우지 않는다.
        public async Task OpenExternalFileAsync(string path)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            if (!vm.LoadBms(path))
            {
                await ConfirmWindow.ShowMessageAsync(this, $"파일을 열지 못했습니다.\n\n{vm.LastErrorMessage}", "열기 실패");
                return;
            }

            // 고른 차트가 들어 있는 폴더에서 OGG·영상을 찾는다.
            var folderPath = Path.GetDirectoryName(path);
            if (folderPath is not null)
                await LoadFolderAudioAndVideoAsync(vm, folderPath);
        }

        private async void OnRegisterFileAssociationClick(object? sender, RoutedEventArgs e)
        {
            if (!OperatingSystem.IsWindows())
                return;

            var exePath = Environment.ProcessPath;
            if (exePath is null || Path.GetFileName(exePath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                await ConfirmWindow.ShowMessageAsync(
                    this, "배포된 exe 로 실행했을 때만 연결을 등록할 수 있습니다.", "연결 등록 실패");
                return;
            }

            try
            {
                FileAssociation.Register(exePath);
                ShellNotify.AssociationsChanged();
            }
            catch (Exception ex)
            {
                await ConfirmWindow.ShowMessageAsync(this, $"등록하지 못했습니다.\n\n{ex.Message}", "연결 등록 실패");
                return;
            }

            await ConfirmWindow.ShowMessageAsync(
                this,
                "이제 .bms / .bme / .bml 파일을 더블클릭하면 이 에디터로 열립니다.\n\n" +
                "다른 프로그램으로 열린다면 파일 속성의 '연결 프로그램'에서 이 에디터를 골라 주세요.",
                "연결 등록 완료");
        }

        private async void OnUnregisterFileAssociationClick(object? sender, RoutedEventArgs e)
        {
            if (!OperatingSystem.IsWindows())
                return;

            try
            {
                FileAssociation.Unregister();
                ShellNotify.AssociationsChanged();
            }
            catch (Exception ex)
            {
                await ConfirmWindow.ShowMessageAsync(this, $"연결을 해제하지 못했습니다.\n\n{ex.Message}", "연결 해제 실패");
                return;
            }

            await ConfirmWindow.ShowMessageAsync(
                this,
                "이 에디터에 등록된 .bms / .bme / .bml 파일 연결을 해제했습니다.",
                "연결 해제 완료");
        }

        private void OnKeyboardSpeedClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm
                && sender is MenuItem { Tag: string tag }
                && int.TryParse(tag, out var speed))
            {
                vm.KeyboardImportSpeed = speed;
            }
        }

        // 건반형 BMS(4K/5K/7K)를 열어 뮤즈 대시 두 레인으로 접는다.
        //
        // 여는 것이 아니라 **가져오기**다. 지금 문서의 키음 표를 그대로 쓰기 때문이다.
        // 건반 BMS 의 키음은 그 게임 소리라 뮤즈 대시 노트에 붙일 수 없다.
        private async void OnImportKeyboardChartClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "가져올 건반 BMS 선택",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("BMS 차트 파일") { Patterns = new[] { "*.bms", "*.bme", "*.bml", "*.pms" } },
                },
            });

            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is null)
                return;

            var report = await vm.ImportKeyboardChartAsync(path);
            if (report is null)
            {
                // 사용자가 확인 창에서 취소한 경우에는 알릴 것이 없다.
                if (vm.LastErrorMessage is { } message)
                    await ConfirmWindow.ShowMessageAsync(this, message, "가져오기 실패");

                return;
            }

            await ConfirmWindow.ShowMessageAsync(this, vm.DescribeDownmixReport(report), "가져오기 완료");
        }

        private async void OnSaveFileClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var path = vm.CurrentFilePath ?? await PickSavePathAsync(vm);
            if (path is null)
                return;

            await SaveAndReportAsync(vm, path);
        }

        private async void OnSaveFileAsClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var path = await PickSavePathAsync(vm);
            if (path is null)
                return;

            await SaveAndReportAsync(vm, path);
        }

        // 저장은 조용히 실패하면 안 된다. 실패하면 사유까지 보여준다.
        // 저장은 됐지만 알려야 할 것(인코딩을 UTF-8로 물린 경우)도 여기서 보여준다.
        private async Task SaveAndReportAsync(MainWindowViewModel vm, string path)
        {
            if (!await EnsureMayOverwriteAsync(vm, path))
                return;

            if (!vm.SaveBms(path))
            {
                await ConfirmWindow.ShowMessageAsync(this, $"저장하지 못했습니다.\n\n{vm.LastErrorMessage}", "저장 실패");
                return;
            }

            if (vm.LastWarningMessage is { } warning)
                await ConfirmWindow.ShowMessageAsync(this, warning, "저장 완료");
        }

        private async Task<string?> PickSavePathAsync(MainWindowViewModel vm)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "BMS 파일로 저장",
                DefaultExtension = "bms",
                SuggestedFileName = ToSafeFileName(vm.Title),
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("BMS 차트 파일") { Patterns = new[] { "*.bms", "*.bme", "*.bml" } },
                },
            });

            return file?.TryGetLocalPath();
        }

        // 곡 제목을 파일명으로 쓸 수 있게 다듬는다.
        //
        // 예전에는 제목을 그대로 넘겨서, `:` `?` `/` 같은 글자가 든 제목이면
        // 저장 대화상자가 걸렸다. 곡 제목에는 흔한 글자들이다.
        private static string ToSafeFileName(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return "chart";

            var safe = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();

            // 전부 걸러졌거나 점만 남은 경우.
            return safe.Trim('.').Length == 0 ? "chart" : safe;
        }

        private async Task LoadFolderMediaAsync(MainWindowViewModel vm, string folderPath)
        {
            var bmsPath = FolderMedia.FindBestFile(folderPath, FolderMedia.ChartExtensions);

            // 차트가 없는 폴더를 고르면 아무것도 건드리지 않는다.
            //
            // 예전에는 차트만 건너뛰고 음원·영상을 얹었다. 사용자는 새로 열렸다고 생각하는데
            // 화면의 차트는 옛것이고, CurrentFilePath 도 옛 파일을 가리켜서
            // 그대로 [저장]을 누르면 방금 연 폴더가 아니라 옛 차트가 덮어써졌다.
            if (bmsPath is null)
            {
                await ConfirmWindow.ShowMessageAsync(
                    this,
                    $"이 폴더에 BMS 차트가 없습니다.\n\n{folderPath}\n\n" +
                    "지금 작업 중인 차트를 그대로 두었습니다. 음원·영상도 바꾸지 않았습니다.",
                    "폴더 열기");
                return;
            }

            if (!vm.LoadBms(bmsPath))
            {
                await ConfirmWindow.ShowMessageAsync(this, $"차트를 열지 못했습니다.\n\n{vm.LastErrorMessage}", "열기 실패");
                return;
            }

            await LoadFolderAudioAndVideoAsync(vm, folderPath);
        }

        // 차트와 같은 폴더의 OGG·영상을 붙인다. 폴더 열기와 탐색기 더블클릭이 같이 쓴다.
        private async Task LoadFolderAudioAndVideoAsync(MainWindowViewModel vm, string folderPath)
        {
            var oggPath = FolderMedia.FindBestFile(folderPath, FolderMedia.AudioExtensions);
            if (oggPath is not null && !await vm.LoadOggAsync(oggPath))
                await ConfirmWindow.ShowMessageAsync(this, $"OGG를 불러오지 못했습니다.\n\n{vm.LastErrorMessage}", "OGG 로드 실패");

            var videoPath = FolderMedia.FindBestFile(folderPath, FolderMedia.VideoExtensions);
            if (videoPath is not null)
                vm.LoadVideo(videoPath);
            else
                vm.ClearVideo();
        }

        private async void OnAddWavClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "키음 파일 선택",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("키음 오디오") { Patterns = new[] { "*.wav", "*.ogg" } },
                },
            });

            var paths = files
                .Select(f => f.TryGetLocalPath())
                .Where(p => p is not null)
                .Select(p => p!)
                .ToArray();

            if (paths.Length == 0)
                return;

            // 예전에는 실패해도 아무 표시가 없어서, 키음이 한도를 넘으면 조용히 아무 일도 안 났다.
            vm.AddWavs(paths);
            if (vm.LastErrorMessage is not null)
                await ConfirmWindow.ShowMessageAsync(this, $"키음을 추가하지 못했습니다.\n\n{vm.LastErrorMessage}", "키음 추가 실패");
        }

        // 목록을 마우스로 골랐으면 키 입력을 격자로 돌려준다.
        // 안 그러면 이어서 누른 방향키가 노트가 아니라 붓(키음 선택)을 바꾼다.
        private void OnWavListPointerReleased(object? sender, PointerReleasedEventArgs e) => NoteGrid.Focus();
}
