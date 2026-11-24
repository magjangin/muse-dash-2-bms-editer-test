using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace bms_editer.Services;

// 켤 때마다 기본값으로 돌아가면 곤란한 값들. 파일에 남는 것은 HEADER 와 오프셋뿐이라 따로 둔다.
//
// 차트 내용은 담지 않는다. 읽지 못해도 기본값으로 돌아갈 뿐이라 예외를 밖으로 내지 않는다.
public sealed class AppSettings
{
    public const int MaxRecentFiles = 10;

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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string DefaultPath => Path.Combine(AutoSaveStore.DefaultDataRoot, "settings.json");

    public void AddRecentFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        RecentFiles ??= new List<string>();
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);

        if (RecentFiles.Count > MaxRecentFiles)
            RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
    }

    public static AppSettings Load(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            if (File.Exists(file))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), JsonOptions) ?? new AppSettings();
                loaded.RecentFiles ??= new List<string>();
                loaded.RecentFiles.RemoveAll(string.IsNullOrWhiteSpace);
                return loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
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
            var file = Path.GetFullPath(path ?? DefaultPath);
            var directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            temp = $"{file}.{Guid.NewGuid().ToString("N")[..8]}.tmp";
            var json = JsonSerializer.Serialize(this, JsonOptions);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, System.Text.Encoding.UTF8))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, file, overwrite: true);
            temp = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
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
}
