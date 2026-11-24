using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using bms_editer.Models;
using bms_editer.Services;
using bms_editer.Services.Holds;
using bms_editer.ViewModels;
using Xunit;

namespace bms_editer.Tests;

public sealed class ResilienceAndPerformanceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "md2-bms-resilience-" + Guid.NewGuid().ToString("N")[..8]);

    public ResilienceAndPerformanceTests()
    {
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

    [Theory]
    [InlineData("""{"id":"broken","holdRules":null}""")]
    [InlineData("""{"id":"broken","holdRules":[null]}""")]
    [InlineData("""{"id":"broken","holdRules":[{"kind":"HOLD","policy":"FifoQueue","role":null}]}""")]
    [InlineData("""{"id":"broken","holdRules":[{"kind":"HOLD","policy":"FifoQueue","role":{"type":"KeyValue","headKeys":null,"tailKeys":["02"]}}]}""")]
    [InlineData("""{"id":"broken","uidTable":{"pattern":null},"holdRules":[{"kind":"HOLD","policy":"Alternate","role":{"type":"FileNameUid"}}]}""")]
    public void 외부_프로파일의_null_필드는_크래시하지_않고_LoadErrors로_걸러진다(string brokenJson)
    {
        var dir = Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "broken.json"), brokenJson, Encoding.UTF8);

        var catalog = GameProfileCatalog.Load(dir);

        Assert.NotEmpty(catalog.LoadErrors);
        Assert.NotNull(catalog.Find("muse_dash"));
        Assert.Null(catalog.Find("broken"));
    }

    [Fact]
    public void 사용자_프로파일_폴더는_exe_옆_폴더보다_우선해_덮어쓴다()
    {
        var exeProfiles = Path.Combine(_tempDir, "exe_profiles");
        var userProfiles = Path.Combine(_tempDir, "user_profiles");
        Directory.CreateDirectory(exeProfiles);
        Directory.CreateDirectory(userProfiles);

        const string baseJson = """
            {
              "id": "muse_dash_2",
              "displayName": "FromExe",
              "holdRules": [
                { "kind": "HOLD", "policy": "FifoQueue", "role": { "type": "KeyValue", "headKeys": ["01"], "tailKeys": ["02"] } }
              ]
            }
            """;
        const string userJson = """
            {
              "id": "muse_dash_2",
              "displayName": "FromUser",
              "holdRules": [
                { "kind": "HOLD", "policy": "FifoQueue", "role": { "type": "KeyValue", "headKeys": ["01"], "tailKeys": ["02"] } }
              ]
            }
            """;

        File.WriteAllText(Path.Combine(exeProfiles, "muse_dash_2.json"), baseJson, Encoding.UTF8);
        File.WriteAllText(Path.Combine(userProfiles, "muse_dash_2.json"), userJson, Encoding.UTF8);

        var catalog = GameProfileCatalog.Load(exeProfiles, userProfiles);
        var loaded = catalog.Find("muse_dash_2");

        Assert.NotNull(loaded);
        Assert.Equal("FromUser", loaded!.DisplayName);
    }

    [Fact]
    public void 설정_파일에_null_RecentFiles와_주석이_있어도_안전하게_읽는다()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(path, """
            {
              // 사용자 편집 주석
              "RecentFiles": null,
              "VerticalZoom": 12.5,
            }
            """, Encoding.UTF8);

        var settings = AppSettings.Load(path);

        Assert.NotNull(settings.RecentFiles);
        Assert.Equal(12.5, settings.VerticalZoom);
        settings.AddRecentFile(@"C:\charts\test.bms");
        Assert.Single(settings.RecentFiles);

        settings.Save(path);
        var reloaded = AppSettings.Load(path);
        Assert.Single(reloaded.RecentFiles);
    }

    [Fact]
    public void 비정상_BPM과_불법_경로_문자가_있어도_파서와_타임라인이_깨지지_않는다()
    {
        var overflowNumber = new string('9', 310);
        var bmsPath = Path.Combine(_tempDir, "malformed.bms");
        File.WriteAllText(bmsPath,
            "#BPM 0\r\n" +
            $"#BPM01 {overflowNumber}\r\n" +
            "#BMSEDITER_OFFSET NaN\r\n" +
            "#WAV01 bad<name>|test?.wav\r\n" +
            $"#00102:{overflowNumber}\r\n" +
            "#00113:0100\r\n",
            Encoding.UTF8);

        var parsed = BmsParser.Parse(bmsPath);

        Assert.Equal(120.0, parsed.Chart.Header.Bpm);
        Assert.Empty(parsed.Chart.BpmTable);
        Assert.Empty(parsed.Chart.MeasureLengths);
        Assert.Equal(0.0, parsed.Chart.Header.AudioOffsetMs);
        Assert.Single(parsed.WavItems);
        Assert.Single(parsed.Chart.Notes);

        var timeline = ChartTimeline.FromChart(parsed.Chart, double.PositiveInfinity);
        Assert.Equal(120.0, timeline.BaseBpm);
        Assert.Equal(0.0, timeline.SecondsAt(double.NaN));
        Assert.Equal(0.0, timeline.MeasurePositionAt(double.PositiveInfinity));

        var written = BmsWriter.Write(parsed.Chart, "", "", "", double.NaN, 0, 2, "", parsed.WavItems, bmsPath);
        Assert.Contains("#BPM 120", written);
    }

    [Fact]
    public void 조건_블록_안의_주석과_확장_태그는_저장해도_블록_안에_남는다()
    {
        var bmsPath = Path.Combine(_tempDir, "branch_comment.bms");
        File.WriteAllText(bmsPath,
            "#BPM 120\n" +
            "#WAV01 a.wav\n" +
            "#RANDOM 2\n" +
            "#IF 1\n" +
            "* branch 1 comment\n" +
            "#MD2_EXT custom_flag\n" +
            "#00113:0100\n" +
            "#ENDIF\n",
            Encoding.UTF8);

        var parsed = BmsParser.Parse(bmsPath);
        var written = BmsWriter.Write(parsed.Chart, "", "", "", 120, 0, 2, "", parsed.WavItems, bmsPath);

        var ifIndex = written.IndexOf("#IF 1", StringComparison.Ordinal);
        var endIfIndex = written.IndexOf("#ENDIF", StringComparison.Ordinal);
        var commentIndex = written.IndexOf("* branch 1 comment", StringComparison.Ordinal);
        var extIndex = written.IndexOf("#MD2_EXT custom_flag", StringComparison.Ordinal);

        Assert.True(ifIndex >= 0 && endIfIndex > ifIndex);
        Assert.InRange(commentIndex, ifIndex, endIfIndex);
        Assert.InRange(extIndex, ifIndex, endIfIndex);
    }

    [Fact]
    public void 균일_BPM_타임라인_패스트패스가_초와_마디를_정확히_왕복한다()
    {
        var timeline = ChartTimeline.Uniform(150.0);

        Assert.False(timeline.HasBpmChanges);
        Assert.False(timeline.HasMeasureLengthChanges);

        var expectedSec = 12.5 * (240.0 / 150.0);
        Assert.Equal(expectedSec, timeline.SecondsAt(12.5), precision: 9);
        Assert.Equal(12.5, timeline.MeasurePositionAt(expectedSec), precision: 9);
    }

    [Fact]
    public void 키음_표_가져오기는_Dirty와_Undo_되돌리기를_지원한다()
    {
        var sourcePath = Path.Combine(_tempDir, "md_source.bms");
        File.WriteAllText(sourcePath,
            "#BPM 140\r\n" +
            "#WAV01 011001_일반 지상.wav\r\n" +
            "#WAV02 011004_일반 공중.wav\r\n" +
            "#WAV03 010201_홀드 지상 시작 노트.wav\r\n" +
            "#WAV04 010201_홀드 지상 끝 노트.wav\r\n" +
            "#WAV05 010204_홀드 공중 시작 노트.wav\r\n" +
            "#WAV06 010204_홀드 공중 끝 노트.wav\r\n" +
            "#00113:0100\r\n",
            Encoding.UTF8);

        using var vm = new MainWindowViewModel();
        Assert.False(vm.IsDirty);
        Assert.Empty(vm.WavList);

        var imported = vm.ImportKeysoundsFrom(sourcePath);

        Assert.Equal(6, imported);
        Assert.True(vm.IsDirty);
        Assert.True(vm.CanUndo);
        Assert.Equal(6, vm.WavList.Count);
        Assert.Equal(6, vm.Chart.WavTable.Count);

        vm.UndoCommand.Execute(null);

        Assert.Empty(vm.WavList);
        Assert.Empty(vm.Chart.WavTable);
    }
}
