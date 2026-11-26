using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using bms_editer.Services;
using Xunit;

namespace bms_editer.Tests;

// v0.1.8 감사에서 고친 것들의 회귀 테스트. 모두 임시 폴더에서만 돈다(사용자 데이터 폴더를 건드리지 않음).
public sealed class AuditV018Tests : IDisposable
{
    private readonly string _tempDir;

    public AuditV018Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bms-audit018-" + Guid.NewGuid().ToString("N"));
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

    // ── 키음: 잠깐 잠겨 있던 WAV 를 영구 실패로 기억하지 않음 ──────────────────────

    [Fact]
    public void KeySound_RetriesFile_ThatWasLockedDuringFirstDecode()
    {
        var wav = Path.Combine(_tempDir, "locked.wav");
        File.WriteAllBytes(wav, MinimalWav());

        using var player = new KeySoundPlayer();
        using (new FileStream(wav, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Null(player.GetOrLoad(wav));

        // 예전에는 첫 실패(null)가 캐시에 남아 차트를 다시 열 때까지 계속 null 이었다.
        Assert.NotNull(player.GetOrLoad(wav));
    }

    [Fact]
    public void KeySound_RetriesFile_ThatWasBrokenThenReplaced()
    {
        var wav = Path.Combine(_tempDir, "replaced.wav");
        File.WriteAllBytes(wav, new byte[] { 1, 2, 3 });

        using var player = new KeySoundPlayer();
        Assert.Null(player.GetOrLoad(wav));

        File.WriteAllBytes(wav, MinimalWav());
        Assert.NotNull(player.GetOrLoad(wav));
    }

    // ── 설정: 새 버전이 적은 칸을 옛 버전이 지우지 않음 ───────────────────────────

    [Fact]
    public void Settings_PreserveUnknownFields_AndNewerSchema_AcrossSave()
    {
        var file = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(file, """
            { "SchemaVersion": 7, "VerticalZoom": 12, "FutureOption": { "a": [1, 2] }, "FutureFlag": true }
            """);

        var settings = AppSettings.Load(file);
        Assert.Equal(12, settings.VerticalZoom);
        settings.VerticalZoom = 14;
        settings.Save(file);

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var root = doc.RootElement;
        Assert.Equal(7, root.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(14, root.GetProperty("VerticalZoom").GetDouble());
        Assert.True(root.GetProperty("FutureFlag").GetBoolean());
        Assert.Equal(2, root.GetProperty("FutureOption").GetProperty("a").GetArrayLength());
    }

    [Fact]
    public void Settings_Load_RemovesOnlyStaleTempFiles()
    {
        var file = Path.Combine(_tempDir, "settings.json");
        var stale = file + ".123.aaaa.tmp";
        var fresh = file + ".456.bbbb.tmp";
        File.WriteAllText(stale, "x");
        File.WriteAllText(fresh, "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        AppSettings.Load(file);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    // ── 저장: 곡 폴더에 남은 임시 파일 정리 ──────────────────────────────────────

    [Fact]
    public void SafeWriter_CleansStaleTempFiles_ButNotOthers()
    {
        var chart = Path.Combine(_tempDir, "song.bms");
        var stale = Path.Combine(_tempDir, $"song.bms.{Guid.NewGuid():N}.tmp");
        var fresh = Path.Combine(_tempDir, $"song.bms.{Guid.NewGuid():N}.tmp");
        var foreign = Path.Combine(_tempDir, "song.bms.mine.tmp");
        var otherChart = Path.Combine(_tempDir, $"other.bms.{Guid.NewGuid():N}.tmp");
        foreach (var f in new[] { stale, fresh, foreign, otherChart })
        {
            File.WriteAllText(f, "x");
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-1));
        }

        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow);

        SafeFileWriter.WriteAllText(chart, "#BPM 120\r\n", new UTF8Encoding(false));

        Assert.True(File.Exists(chart));
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(otherChart));
    }

    // ── 자동 저장: meta.json 이 없어도 작업을 살림 · 빈 폴더 정리 ─────────────────

    [Fact]
    public void AutoSave_RecoversChart_WhenMetaIsMissingOrBroken()
    {
        var store = new AutoSaveStore(_tempDir);
        var missing = Directory.CreateDirectory(Path.Combine(store.AutoSaveRoot, "nometa")).FullName;
        File.WriteAllText(Path.Combine(missing, "20261125-000000-000.bms"), "#BPM 120\r\n#00113:01\r\n");

        var broken = Directory.CreateDirectory(Path.Combine(store.AutoSaveRoot, "brokenmeta")).FullName;
        File.WriteAllText(Path.Combine(broken, "20261125-000001-000.bms"), "#BPM 120\r\n");
        File.WriteAllText(Path.Combine(broken, "meta.json"), "{ not json");

        var empty = Directory.CreateDirectory(Path.Combine(store.AutoSaveRoot, "emptymeta")).FullName;
        File.WriteAllText(Path.Combine(empty, "20261125-000002-000.bms"), "#BPM 120\r\n");
        File.WriteAllText(Path.Combine(empty, "meta.json"), "{}");

        var entries = store.FindRecoverable();

        Assert.Equal(3, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.Null(e.Meta.OriginalPath);
            Assert.Equal(-1, e.Meta.NoteCount);
            Assert.False(string.IsNullOrEmpty(e.Meta.BasePath));
        });
    }

    [Fact]
    public void AutoSave_PrunesOnlyOldFoldersWithoutChart()
    {
        var store = new AutoSaveStore(_tempDir);
        var old = Directory.CreateDirectory(Path.Combine(store.AutoSaveRoot, "old")).FullName;
        File.WriteAllText(Path.Combine(old, "session.lock"), "");
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));

        var recent = Directory.CreateDirectory(Path.Combine(store.AutoSaveRoot, "recent")).FullName;

        Assert.Empty(store.FindRecoverable());
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
    }

    // 44.1kHz 16bit 스테레오 무음 4프레임.
    private static byte[] MinimalWav()
    {
        const int frames = 4;
        const int dataSize = frames * 4;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataSize);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)2);
        w.Write(44100);
        w.Write(44100 * 4);
        w.Write((short)4);
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataSize);
        w.Write(new byte[dataSize]);
        w.Flush();
        return ms.ToArray();
    }
}
