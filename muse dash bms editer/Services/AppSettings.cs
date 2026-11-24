using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace bms_editer.Services;

// 켤 때마다 기본값으로 돌아가면 곤란한 값들. 파일에 남는 것은 HEADER 와 오프셋뿐이라 따로 둔다.
//
// 차트 내용은 담지 않는다. 읽지 못해도 기본값으로 돌아갈 뿐이라 예외를 밖으로 내지 않는다.
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxRecentFiles = 10;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public double VerticalZoom { get; set; } = 10.0;
    public double HorizontalZoom { get; set; } = 2.0;
    public double WaveformHorizontalZoom { get; set; } = 1.0;
    public bool IsHorizontalView { get; set; }
    public bool IsCircleNoteShape { get; set; }
    public bool HideUnusedLanes { get; set; }
    public bool ShowNoteSpeed { get; set; }
    public bool SnapToGrid { get; set; } = true;
    public bool FollowPlaybackCursor { get; set; } = true;
    public double PlaybackSpeed { get; set; } = 1.0;
    public bool IsKeySoundEnabled { get; set; } = true;
    public double KeySoundVolume { get; set; } = 1.0;
    public int WavPaletteViewModeIndex { get; set; } = 2;

    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool IsWindowMaximized { get; set; }

    public List<string> RecentFiles { get; set; } = new();

    public static string DefaultPath => Path.Combine(AutoSaveStore.DefaultDataRoot, "settings.json");

    public void AddRecentFile(string path)
    {
        RecentFiles ??= new List<string>();
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);

        if (RecentFiles.Count > MaxRecentFiles)
            RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
    }

    // 손상되거나 구버전/외부 편집으로 비정상 값이 들어와도 앱이 깨지지 않도록 보정한다.
    public AppSettings Sanitize()
    {
        SchemaVersion = CurrentSchemaVersion;
        VerticalZoom = double.IsFinite(VerticalZoom) ? Math.Clamp(VerticalZoom, 1.0, 32.0) : 10.0;
        HorizontalZoom = double.IsFinite(HorizontalZoom) ? Math.Clamp(HorizontalZoom, 0.5, 8.0) : 2.0;
        WaveformHorizontalZoom = double.IsFinite(WaveformHorizontalZoom) ? Math.Clamp(WaveformHorizontalZoom, 0.5, 8.0) : 1.0;
        PlaybackSpeed = double.IsFinite(PlaybackSpeed) ? Math.Clamp(PlaybackSpeed, 0.1, 2.0) : 1.0;
        KeySoundVolume = double.IsFinite(KeySoundVolume) ? Math.Clamp(KeySoundVolume, 0.0, 2.0) : 1.0;
        WavPaletteViewModeIndex = Math.Clamp(WavPaletteViewModeIndex, 0, 3);

        if (WindowWidth is { } w && (!double.IsFinite(w) || w < 400 || w > 16000))
            WindowWidth = null;
        if (WindowHeight is { } h && (!double.IsFinite(h) || h < 300 || h > 16000))
            WindowHeight = null;

        RecentFiles = (RecentFiles ?? new List<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxRecentFiles)
            .ToList();

        return this;
    }

    public static AppSettings Load(string? path = null)
    {
        var file = path ?? DefaultPath;
        try
        {
            if (File.Exists(file))
                return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), ReadOptions) ?? new AppSettings()).Sanitize();
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"설정 파싱 실패: {ex.Message}");
            TryBackupCorruptSettings(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine($"설정 읽기 실패: {ex.Message}");
        }

        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        string? temp = null;
        try
        {
            var file = path ?? DefaultPath;
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            temp = $"{file}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            var json = JsonSerializer.Serialize(this, WriteOptions);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, file, overwrite: true);
            temp = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine($"설정 저장 실패: {ex.Message}");
        }
        finally
        {
            if (temp is not null)
            {
                try { File.Delete(temp); }
                catch { /* 임시 파일 정리 실패 무시 */ }
            }
        }
    }

    private static void TryBackupCorruptSettings(string file)
    {
        try
        {
            if (File.Exists(file))
                File.Copy(file, file + ".corrupt.bak", overwrite: true);
        }
        catch
        {
            // 백업 실패는 무시하고 기본 설정으로 복귀
        }
    }
}
