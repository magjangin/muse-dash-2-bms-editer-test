using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using bms_editer.Models;
using bms_editer.Services;
using bms_editer.Services.Downmix;
using bms_editer.Services.Holds;
using bms_editer.ViewModels;
using Xunit;

namespace bms_editer.Tests;

public sealed class AuditResilienceTests : IDisposable
{
    private readonly string _tempDir;

    public AuditResilienceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bms-audit-tests-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void ChartTimeline_HandlesNaNInfinityAndHugeValuesWithoutCrashing()
    {
        var timeline = new ChartTimeline(
            double.PositiveInfinity,
            new Dictionary<int, double> { [0] = double.PositiveInfinity, [1] = double.NaN },
            new[] { new BpmChange(0, 0.5, double.PositiveInfinity), new BpmChange(0, double.NaN, 150) });

        Assert.Equal(120.0, timeline.BaseBpm);
        Assert.Equal(0.0, timeline.SecondsAt(double.NaN));
        Assert.Equal(0.0, timeline.SecondsAt(double.NegativeInfinity));
        Assert.Equal(0.0, timeline.SecondsAt(double.PositiveInfinity));
        Assert.True(double.IsFinite(timeline.SecondsAt(1_000_000)));
        Assert.Equal(0.0, timeline.MeasurePositionAt(double.NaN));
        Assert.Equal(0.0, timeline.MeasurePositionAt(double.PositiveInfinity));
    }

    [Fact]
    public void ChartTimeline_IsThreadSafeUnderConcurrentQueries()
    {
        var timeline = ChartTimeline.Uniform(140.0);
        Parallel.For(0, 200, i =>
        {
            var sec = timeline.SecondsAt(i * 1.5);
            var pos = timeline.MeasurePositionAt(sec);
            Assert.True(Math.Abs(pos - (i * 1.5)) < 1e-6);
        });
    }

    [Fact]
    public void AppSettings_SanitizesNullRecentFilesAndOutOfRangeValues()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(path, """
            {
              "VerticalZoom": 9999,
              "PlaybackSpeed": -5,
              "KeySoundVolume": 99,
              "RecentFiles": null
            }
            """);

        var loaded = AppSettings.Load(path);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.InRange(loaded.VerticalZoom, 1.0, 32.0);
        Assert.InRange(loaded.PlaybackSpeed, 0.1, 2.0);
        Assert.InRange(loaded.KeySoundVolume, 0.0, 2.0);
        Assert.NotNull(loaded.RecentFiles);

        loaded.AddRecentFile("test.bms");
        Assert.Single(loaded.RecentFiles);
    }

    [Fact]
    public void AppSettings_BacksUpCorruptJsonAndFallsBackToDefaults()
    {
        var path = Path.Combine(_tempDir, "corrupt.json");
        File.WriteAllText(path, "{ invalid json !!!");

        var loaded = AppSettings.Load(path);
        Assert.Equal(10.0, loaded.VerticalZoom);
        Assert.True(File.Exists(path + ".corrupt.bak"));
    }

    [Fact]
    public void BmsParser_SurvivesInvalidMediaPathCharactersAndNonPositiveBpm()
    {
        var path = Path.Combine(_tempDir, "bad_header.bms");
        File.WriteAllText(path, """
            #TITLE Test
            #BPM 0
            #WAV01 bad|path<>name.wav
            #00113:01
            """);

        var parsed = BmsParser.Parse(path);
        Assert.Equal(120.0, parsed.Chart.Header.Bpm);
        Assert.Single(parsed.Chart.Notes);
        Assert.True(parsed.Chart.WavTable.ContainsKey("01"));
    }

    [Fact]
    public void BmsParser_DecodesUtf32LeBomWithoutMisidentifyingAsUtf16()
    {
        var path = Path.Combine(_tempDir, "utf32.bms");
        var content = "#TITLE UTF32테스트\n#BPM 150\n#00113:01\n";
        File.WriteAllText(path, content, Encoding.UTF32);

        var parsed = BmsParser.Parse(path);
        Assert.Equal("UTF32테스트", parsed.Chart.Header.Title);
        Assert.Equal(150.0, parsed.Chart.Header.Bpm);
    }

    [Fact]
    public void GameProfileCatalog_RejectsNullFieldsGracefullyWithoutCrashingLoad()
    {
        var profileDir = Path.Combine(_tempDir, "profiles");
        Directory.CreateDirectory(profileDir);
        File.WriteAllText(Path.Combine(profileDir, "broken.json"), """
            {
              "id": "broken",
              "holdRules": null,
              "pathHints": null
            }
            """);

        var catalog = GameProfileCatalog.Load(profileDir);
        Assert.NotEmpty(catalog.Profiles);
        Assert.Contains(catalog.LoadErrors, e => e.Contains("broken.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WavDecoder_DecodesValidWavWhenDataChunkPrecedesFmtChunk()
    {
        var path = Path.Combine(_tempDir, "data_before_fmt.wav");
        using (var fs = File.Create(path))
        using (var bw = new BinaryWriter(fs))
        {
            byte[] pcm = { 0x00, 0x10, 0x00, 0x20 }; // 2 samples (16-bit mono)
            var riffSize = 4 + (8 + pcm.Length) + (8 + 16);
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(riffSize);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));

            // data chunk first
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(pcm.Length);
            bw.Write(pcm);

            // fmt chunk second
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);      // PCM
            bw.Write((short)1);      // Mono
            bw.Write(44100);         // SampleRate
            bw.Write(44100 * 2);     // ByteRate
            bw.Write((short)2);      // BlockAlign
            bw.Write((short)16);     // BitsPerSample
        }

        var decoded = WavDecoder.Decode(path);
        Assert.NotNull(decoded);
        Assert.Equal(2, decoded!.FrameCount);
    }

    [Fact]
    public void WavDecoder_RejectsAbsurdlyLowSampleRateToPreventOom()
    {
        var path = Path.Combine(_tempDir, "low_rate.wav");
        using (var fs = File.Create(path))
        using (var bw = new BinaryWriter(fs))
        {
            byte[] pcm = new byte[400];
            var riffSize = 4 + (8 + 16) + (8 + pcm.Length);
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(riffSize);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)1);
            bw.Write(1); // 1 Hz -> 리샘플링 OOM 공격 시뮬레이션
            bw.Write(2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(pcm.Length);
            bw.Write(pcm);
        }

        var decoded = WavDecoder.Decode(path);
        Assert.NotNull(decoded);
        Assert.Empty(decoded!.Samples);
    }

    [Fact]
    public void KeyboardChartReader_IgnoresMeasureNumbersAboveLimit()
    {
        var path = Path.Combine(_tempDir, "huge_measure.bms");
        File.WriteAllText(path, """
            #TITLE HugeMeasure
            #BPM 120
            #WAV01 a.wav
            #00111:01
            #99999911:01
            """);

        var (chart, _) = KeyboardChartReader.ReadWithChart(path);
        Assert.Single(chart.Notes);
        Assert.True(chart.MeasureCount <= BmsParser.MaxMeasureNumber + 1);
    }

    [Fact]
    public void AutoSaveStore_TruncatesLongBackupFolderName()
    {
        using var store = new AutoSaveStore(_tempDir);
        var longName = new string('a', 120) + ".bms";
        var folder = store.BackupFolderFor(Path.Combine(_tempDir, longName));
        var leaf = Path.GetFileName(folder);
        Assert.True(leaf.Length <= 50, $"백업 폴더 이름이 너무 깁니다: {leaf.Length}");
    }

    [Fact]
    public void MainWindowViewModel_GuardsBpmAndStaleNoteReplacement()
    {
        using var vm = new MainWindowViewModel();
        vm.Bpm = double.NaN;
        Assert.Equal(120.0, vm.Bpm);

        vm.Bpm = -10;
        Assert.Equal(120.0, vm.Bpm);

        var orphan = new BmsNote { Measure = 0, LaneId = "13", Position = 0, WavKey = "01" };
        var replaced = vm.ReplaceWavKey(new[] { orphan }, "02");
        Assert.Equal(0, replaced);
        Assert.Equal("01", orphan.WavKey);
    }
}
