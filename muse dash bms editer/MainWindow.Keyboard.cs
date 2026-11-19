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
        // 스페이스는 버튼보다 먼저 받는다. (터널 단계)
        //
        // 버블 단계에서 받으면 방금 누른 버튼이 포커스를 쥐고 있어서, 스페이스가 재생 대신
        // 그 버튼을 또 누른다. ✏️ 를 켜고 들어 보려는 순간 편집 모드가 꺼지는 식이다.
        private void OnWindowKeyDownTunnel(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None
                || DataContext is not MainWindowViewModel vm
                || IsWithin<TextBox>(FocusManager?.GetFocusedElement()))
            {
                return;
            }

            vm.TogglePlaybackCommand.Execute(null);
            e.Handled = true;
        }

        private void OnNoteGridKeyDown(object? sender, KeyEventArgs e) => HandleEditorKey(e);

        // 창 전체에서 받는 단축키.
        //
        // 예전에는 Delete·방향키가 **격자에 포커스가 있을 때만** 먹었다. 팔레트나 사이드바를
        // 한 번 만지면 조용히 안 먹었고 안내도 없었다. 이제 창이 받아준다.
        // 격자가 먼저 처리했으면 e.Handled 가 서 있어 두 번 돌지 않는다.
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.Handled || DataContext is not MainWindowViewModel vm)
                return;

            var focused = FocusManager?.GetFocusedElement();

            // 글자를 치는 중이어도 저장·열기·새로 만들기는 받는다. BPM 을 고치고 바로 Ctrl+S 를 눌렀는데
            // 아무 일도 안 일어나면 저장된 줄 알고 넘어가게 된다. 되돌리기(Ctrl+Z/Y)만 글자 칸의 것이다.
            if (IsWithin<TextBox>(focused))
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.S or Key.O or Key.N)
                {
                    // 숫자 칸은 포커스를 잃어야 값이 확정된다. 그래야 방금 친 BPM 이 저장에 들어간다.
                    if (IsWithin<NumericUpDown>(focused))
                        NoteGrid.Focus();

                    HandleControlShortcut(e);
                }

                return;
            }

            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                HandleControlShortcut(e);
                return;
            }

            // 방향키·Delete 는 목록·콤보·슬라이더에서는 그쪽 것이다.
            if (IsWithin<ListBox>(focused) || IsWithin<ComboBox>(focused)
                || IsWithin<Slider>(focused) || IsWithin<NumericUpDown>(focused))
            {
                return;
            }

            HandleEditorKey(e);
        }

        private void HandleControlShortcut(KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.S when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    OnSaveFileAsClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.S:
                    OnSaveFileClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.O:
                    OnOpenFileClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.N:
                    if (DataContext is MainWindowViewModel vm)
                        vm.NewFileCommand.Execute(null);
                    e.Handled = true;
                    break;

                // Ctrl+Shift+Z 도 다시 하기로 받는다. Ctrl+Y 만 두면 그쪽 버릇이 든 손이 헛돈다.
                case Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                case Key.Y:
                    ExecuteIfPossible(v => v.RedoCommand);
                    e.Handled = true;
                    break;

                case Key.Z:
                    ExecuteIfPossible(v => v.UndoCommand);
                    e.Handled = true;
                    break;
            }
        }

        // 되돌릴 것이 없으면 아무 일도 하지 않는다. CanExecute 를 보지 않고 부르면
        // 비어 있는 칸에서도 눌린 것처럼 동작한다.
        private void ExecuteIfPossible(Func<MainWindowViewModel, IRelayCommand> pick)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var command = pick(vm);
            if (command.CanExecute(null))
                command.Execute(null);
        }

        // 격자 편집 키(선택 해제·삭제·이동). 격자에서도 창에서도 같은 규칙을 쓴다.
        private void HandleEditorKey(KeyEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            if (e.KeyModifiers == KeyModifiers.None && TryHandlePlainKey(vm, e.Key))
            {
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.Delete:
                    vm.DeleteSelectedNotesCommand.Execute(null);
                    e.Handled = true;
                    return;

                case Key.Escape:
                    vm.ClearNoteSelection();
                    e.Handled = true;
                    return;

            }

            NoteMoveDirection? direction = e.Key switch
            {
                Key.Up => vm.IsHorizontalView ? NoteMoveDirection.LanePrevious : NoteMoveDirection.TimeForward,
                Key.Down => vm.IsHorizontalView ? NoteMoveDirection.LaneNext : NoteMoveDirection.TimeBackward,
                Key.Left => vm.IsHorizontalView ? NoteMoveDirection.TimeBackward : NoteMoveDirection.LanePrevious,
                Key.Right => vm.IsHorizontalView ? NoteMoveDirection.TimeForward : NoteMoveDirection.LaneNext,
                _ => null
            };

            if (direction is { } d)
            {
                vm.MoveSelectedNotesCommand.Execute(d);
                e.Handled = true;
            }
        }

        // 글자·숫자 한 키 단축키. Ctrl 과 같이 누른 것은 여기로 오지 않는다.
        private static bool TryHandlePlainKey(MainWindowViewModel vm, Key key)
        {
            switch (key)
            {
                case Key.G:
                    vm.SnapToGrid = !vm.SnapToGrid;
                    return true;

                case Key.D:
                    vm.LockVerticalPosition = !vm.LockVerticalPosition;
                    return true;

                case Key.E:
                    vm.IsEditMode = !vm.IsEditMode;
                    return true;
            }

            // 숫자 키로 격자 분할을 바꾼다. 잇단음 구간을 오갈 때 패널까지 손이 가지 않게 한다.
            int? split = key switch
            {
                Key.D1 or Key.NumPad1 => 4,
                Key.D2 or Key.NumPad2 => 8,
                Key.D3 or Key.NumPad3 => 12,
                Key.D4 or Key.NumPad4 => 16,
                Key.D6 or Key.NumPad6 => 24,
                Key.D8 or Key.NumPad8 => 32,
                _ => null,
            };

            if (split is not { } value)
                return false;

            vm.BeatSplit = value;
            return true;
        }

        // 포커스가 T 안에(또는 T 자신에) 있는지.
        private static bool IsWithin<T>(IInputElement? focused) where T : class
        {
            if (focused is not Visual visual)
                return false;

            foreach (var ancestor in visual.GetSelfAndVisualAncestors())
            {
                if (ancestor is T)
                    return true;
            }

            return false;
        }
}
