using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using bms_editer.Models;
using bms_editer.Services;
using bms_editer.ViewModels;
using Xunit;

namespace bms_editer.Tests;

// v0.1.7 감사에서 고친 것들의 회귀 테스트.
public sealed class AuditV017Tests : IDisposable
{
    private readonly string _tempDir;

    public AuditV017Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bms-audit017-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // 임시 폴더 정리 실패 무시
        }
    }

    // ── 저장: 분할 수가 1920 을 넘어도 노트 위치가 그대로 ─────────────────────────

    [Fact]
    public void Writer_KeepsExactPositions_WhenLcmExceedsResolutionCap()
    {
        // 7분할 자리와 384분할 자리를 한 마디·한 레인에: LCM 2688 > 1920.
        // 예전에는 1920 으로 잘려 1/7 노트가 가장 가까운 칸(274/1920)으로 밀렸다.
        var path = Path.Combine(_tempDir, "lcm.bms");
        File.WriteAllText(path, "#BPM 120\r\n#WAV01 a.wav\r\n#WAV02 b.wav\r\n#00113:00010000000000\r\n", new UTF8Encoding(false));

        using var vm = new MainWindowViewModel();
        Assert.True(vm.LoadBms(path), vm.LastErrorMessage);
        vm.Chart.Notes.Add(new BmsNote { Measure = 1, LaneId = "13", Position = 5.0 / 384, WavKey = "02" });

        var text = BmsWriter.Write(vm.Chart, vm.Title, vm.Artist, vm.Genre, vm.Bpm,
            vm.Player, vm.Rank, vm.Level, vm.WavList, path);
        File.WriteAllText(path, text, new UTF8Encoding(false));

        var reparsed = BmsParser.Parse(path).Chart.Notes.Where(n => n.LaneId == "13").OrderBy(n => n.Position).ToList();
        Assert.Equal(2, reparsed.Count);
        Assert.Equal(5.0 / 384, reparsed[0].Position, 9);
        Assert.Equal(1.0 / 7, reparsed[1].Position, 9);
    }

    [Fact]
    public void Writer_StillWritesOneLine_ForOrdinaryResolutions()
    {
        var path = Path.Combine(_tempDir, "plain.bms");
        File.WriteAllText(path, "#BPM 120\r\n#WAV01 a.wav\r\n#00113:01000100\r\n", new UTF8Encoding(false));

        using var vm = new MainWindowViewModel();
        Assert.True(vm.LoadBms(path), vm.LastErrorMessage);
        vm.Chart.Notes.Add(new BmsNote { Measure = 1, LaneId = "13", Position = 3.0 / 16, WavKey = "01" });

        var text = BmsWriter.Write(vm.Chart, vm.Title, vm.Artist, vm.Genre, vm.Bpm,
            vm.Player, vm.Rank, vm.Level, vm.WavList, path);

        Assert.Single(text.Split('\n'), line => line.StartsWith("#00113:", StringComparison.Ordinal));
    }

    // ── 되돌리기: 큰 차트에서 메모리 예산 ─────────────────────────────────────

    private static EditSnapshot Snapshot(int noteCount, string label = "편집")
    {
        var notes = new BmsNote[noteCount];
        for (var i = 0; i < noteCount; i++)
            notes[i] = new BmsNote { Measure = i / 16, LaneId = "13", Position = (i % 16) / 16.0, WavKey = "01" };

        return new EditSnapshot(notes, Array.Empty<BmsWavItem>(), new Dictionary<string, string>(), Array.Empty<int>(), null, label);
    }

    [Fact]
    public void History_TrimsByNoteBudget_ButKeepsMinimumDepth()
    {
        var history = new EditHistory();
        history.Reset(Snapshot(50_000));

        for (var i = 0; i < 60; i++)
            history.Record(Snapshot(50_000));

        // 50,000 × 12 = 600,000 → 예산 안에서 최대 11~12칸. 최소 10칸은 지킨다.
        Assert.InRange(history.UndoDepth, EditHistory.MinDepth, 12);
        Assert.True(history.UndoNoteTotal <= EditHistory.MaxTotalNotes);

        // 아주 큰 차트(예산을 혼자 넘음)도 최소 칸 수는 남긴다.
        var huge = new EditHistory();
        huge.Reset(Snapshot(200_000));
        for (var i = 0; i < 15; i++)
            huge.Record(Snapshot(200_000));

        Assert.Equal(EditHistory.MinDepth, huge.UndoDepth);
    }

    [Fact]
    public void History_SmallCharts_StillKeepMaxDepth_AndTotalsStayConsistent()
    {
        var history = new EditHistory();
        history.Reset(Snapshot(700));

        for (var i = 0; i < EditHistory.MaxDepth + 30; i++)
            history.Record(Snapshot(700));

        Assert.Equal(EditHistory.MaxDepth, history.UndoDepth);

        // 되돌리기·다시 하기를 오가도 합계가 칸들과 맞는다(음수로 새지 않는다).
        for (var i = 0; i < 40; i++)
            history.Undo();
        for (var i = 0; i < 20; i++)
            history.Redo();

        Assert.Equal(history.UndoDepth * 701L, history.UndoNoteTotal);

        history.Reset(Snapshot(10));
        Assert.Equal(0, history.UndoNoteTotal);
    }

    // ── 재생: 키음 구간 ──────────────────────────────────────────────────────

    [Fact]
    public void KeySoundWindow_NormalFrame_CoversWholeStep()
    {
        var (from, to) = MainWindowViewModel.KeySoundWindow(10.0, 10.016);
        Assert.Equal(10.0, from, 9);
        Assert.Equal(10.016, to, 9);
    }

    [Fact]
    public void KeySoundWindow_AfterStall_OnlyPlaysRecentSlice()
    {
        // UI 스레드가 2초 막혔다 풀림: 2초치 노트를 몰아 울리지 않는다.
        var (from, to) = MainWindowViewModel.KeySoundWindow(10.0, 12.0);
        Assert.Equal(12.0 - MainWindowViewModel.MaxKeySoundCatchUpSeconds, from, 9);
        Assert.Equal(12.0, to, 9);
    }

    [Theory]
    [InlineData(10.0, 9.99)]
    [InlineData(10.0, 10.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(1.0, double.PositiveInfinity)]
    public void KeySoundWindow_BackwardsOrInvalid_IsEmpty(double last, double current)
    {
        var (from, to) = MainWindowViewModel.KeySoundWindow(last, current);
        Assert.False(to > from);
    }

    // ── 파일 연결: 업데이트 뒤 끊긴 연결 되살리기 ──────────────────────────────

    [Theory]
    [InlineData("\"C:\\app\\old.exe\" \"%1\"", "C:\\app\\old.exe")]
    [InlineData("C:\\app\\old.exe \"%1\"", "C:\\app\\old.exe")]
    [InlineData("  \"D:\\a b\\x.exe\"  ", "D:\\a b\\x.exe")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("\"", null)]
    public void ParseCommandExePath_ReadsQuotedAndBarePaths(string? command, string? expected)
    {
        Assert.Equal(expected, FileAssociation.ParseCommandExePath(command));
    }

    [Fact]
    public void ShouldRepair_OnlyWhenRegisteredExeIsGone()
    {
        const string current = @"C:\new\muse dash 1 bms editer 2.exe";
        const string old = @"C:\old\muse dash bms editer 2.exe";

        // 옛 exe 가 사라짐 → 되살린다.
        Assert.True(FileAssociation.ShouldRepair($"\"{old}\" \"%1\"", current, _ => false));

        // 옛 exe 가 아직 있음(1번·2번 같이 쓰기 등) → 사용자의 선택이라 두다.
        Assert.False(FileAssociation.ShouldRepair($"\"{old}\" \"%1\"", current, _ => true));

        // 등록이 없음 → 사용자가 연결을 안 했다. 멋대로 등록하지 않는다.
        Assert.False(FileAssociation.ShouldRepair(null, current, _ => false));

        // 이미 지금 exe.
        Assert.False(FileAssociation.ShouldRepair($"\"{current}\" \"%1\"", current, _ => false));

        // dotnet run / 테스트 호스트로 띄운 경우는 등록하지 않는다.
        Assert.False(FileAssociation.ShouldRepair($"\"{old}\" \"%1\"", @"C:\Program Files\dotnet\dotnet.exe", _ => false));
        Assert.False(FileAssociation.ShouldRepair($"\"{old}\" \"%1\"", @"C:\x\testhost.exe", _ => false));
    }
}
