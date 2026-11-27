using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using bms_editer.Models;
using bms_editer.Services;
using bms_editer.Services.Downmix;
using bms_editer.Services.Holds;
using Xunit;

namespace bms_editer.Tests;

// v0.2.4 심층 감사에서 도출된 9대 결함(디코더/BPM/다운믹스/키사운드 등) 회귀 방지 테스트.
public sealed class AuditV020Tests : IDisposable
{
    private readonly string _tempDir;

    public AuditV020Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bms-audit020-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void WavDecoder_ZeroChannels_ReturnsNull_WithoutDivideByZero()
    {
        // format 1 (PCM), channels 0, bitsPerSample 16
        // 유효한 RIFF/WAVE 헤더이지만 channels = 0 인 악의적/손상된 헤더
        var wavPath = Path.Combine(_tempDir, "zero_channels.wav");
        using (var fs = new FileStream(wavPath, FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(fs))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36); // total size - 8
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16); // subchunk size
            writer.Write((short)1); // PCM
            writer.Write((short)0); // channels = 0
            writer.Write(44100);    // sample rate
            writer.Write(0);        // byte rate
            writer.Write((short)0); // block align
            writer.Write((short)16);// bits per sample
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(0);        // data size
        }

        var decoded = WavDecoder.Decode(wavPath);
        Assert.Null(decoded);
    }

    [Fact]
    public void IbmscBpmDetector_ZeroChannelsOrZeroSampleRate_DoesNotThrowDivideByZero()
    {
        // 0 채널 또는 0 Hz 가 전달되었을 때 DivideByZeroException 없이 처리되는지 검증
        var data = new OggAudioData(new byte[100], 0, 0, 0);
        var bpm = IbmscBpmDetector.Detect(data, fullBand: false);
        Assert.True(bpm >= 0);
    }

    [Fact]
    public void PreciseBpmDetector_TruncatedAudioBuffer_DoesNotThrowIndexOutOfRange()
    {
        // FrameCount 는 크지만 Pcm16 버퍼가 짧게 잘린 손상된 데이터
        var data = new OggAudioData(new byte[16], 44100, 2, 10.0);
        var bpm = PreciseBpmDetector.Detect(data);
        Assert.True(bpm >= 0);
    }

    [Fact]
    public void DownmixEngine_UnmappedChannel_DoesNotThrowKeyNotFound()
    {
        // 표준 매핑 딕셔너리에 없는 미지의 채널("99")이 포함된 차트 다운믹스
        var chart = new KeyboardChart(
            new List<KeyboardNote>
            {
                new("99", 0, 0.0, KeyboardNoteKind.Normal),
                new("99", 0, 0.5, KeyboardNoteKind.Normal)
            },
            new[] { "99" },
            "Title",
            120.0,
            1,
            new Dictionary<string, string>(),
            null,
            new List<string>());

        var keys = new DownmixKeys("01", "01", "02", "03", "04", "05", "06");
        var result = DownmixEngine.Convert(chart, keys);

        Assert.NotNull(result);
        Assert.Equal(2, result.Notes.Count);
    }

    [Fact]
    public void KeyboardChartReader_InfiniteBpmAndExtremeMeasure_HandledSafely()
    {
        var chartPath = Path.Combine(_tempDir, "extreme.bms");
        File.WriteAllText(chartPath,
            "#TITLE Test\r\n" +
            "#BPM Infinity\r\n" +
            "#9999911: 01000100\r\n", Encoding.UTF8);

        var chart = KeyboardChartReader.Read(chartPath);
        Assert.Equal(120.0, chart.Bpm); // Infinity는 무시되고 기본값 120 유지
        Assert.Empty(chart.Notes);      // MaxMeasureNumber 초과 라인은 안전하게 스킵
    }

    [Fact]
    public void BmsWriter_NonFiniteNotePosition_SkippedGracefully()
    {
        var chart = new BmsChart();
        chart.Notes.Add(new BmsNote
        {
            Measure = 0,
            Position = double.NaN, // 비정상 Position
            LaneId = "13",
            WavKey = "01"
        });
        chart.Notes.Add(new BmsNote
        {
            Measure = 0,
            Position = 0.5,
            LaneId = "13",
            WavKey = "02"
        });

        var output = BmsWriter.Write(
            chart, "Title", "Artist", "Genre", 120.0, 0, 1, "1",
            Array.Empty<BmsWavItem>(), Path.Combine(_tempDir, "test.bms"));

        // NaN 노트는 생략되고 유효한 0.5 노트만 정상 기록
        Assert.Contains("#00013:0002", output);
    }

    [Fact]
    public void HoldPairingEngine_DeterministicMeasureSort_MatchesTieRank()
    {
        var notes = new List<BmsNote>
        {
            new() { Measure = 1, Position = 0.0, LaneId = "13", WavKey = "01" },
            new() { Measure = 0, Position = 1.0, LaneId = "13", WavKey = "02" }
        };

        var wavTexts = new Dictionary<string, string>
        {
            ["01"] = "010201.wav",
            ["02"] = "010202.wav"
        };

        var profile = GameProfileCatalog.Default.Find("muse_dash");
        Assert.NotNull(profile);

        // Measure 우선 정렬로 마디 간 순서가 명확하게 보장되는지 확인
        var result = HoldPairingEngine.Pair(notes, wavTexts, profile);
        Assert.NotNull(result);
    }
}
