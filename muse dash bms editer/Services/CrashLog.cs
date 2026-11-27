using System;
using System.Globalization;
using System.IO;

namespace bms_editer.Services;

// 예기치 못한 오류를 파일로 남긴다.
//
// 예전에는 exe 옆 crash.log 에 썼다. Program Files 처럼 쓰기 금지된 곳이면 그 쓰기가
// catch 안에서 다시 예외를 던져 원래 오류까지 가렸고, 매번 덮어써서 한 번치만 남았다.
// 기록을 남기다 실패해도 원래 오류를 가리면 안 되므로 여기서 나는 예외는 전부 삼킨다.
public static class CrashLog
{
    // 원본의 이름을 그대로 두어 upstream 코드가 바뀌지 않고 이 포크의 폴더를 가리키게 한다.
    public const string AppDataFolderName = AppIdentity.DataFolderName;
    public const int MaxLogFiles = 20;

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppIdentity.DataFolderName,
        "logs");

    // 쓴 파일의 경로. 쓰지 못했으면 null.
    public static string? Write(Exception exception, string source)
    {
        try
        {
            Directory.CreateDirectory(Folder);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var path = Path.Combine(Folder, $"crash-{stamp}.log");
            var version = typeof(CrashLog).Assembly.GetName().Version;

            File.AppendAllText(path, $"[{source}] v{version}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            TrimOldLogs();
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // 오래된 기록 정리는 덤이다. 여기서 실패했다고(다른 창이 그 파일을 열고 있음 등)
    // 방금 쓴 기록까지 "못 썼다"(null)로 알리면 사용자에게 경로가 안 보인다.
    // 그래서 정리 실패는 Write 의 결과에 섞지 않고, 한 파일이 안 지워져도 나머지는 지운다.
    private static void TrimOldLogs()
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(Folder, "crash-*.log");
        }
        catch (Exception)
        {
            return;
        }

        if (files.Length <= MaxLogFiles)
            return;

        Array.Sort(files, StringComparer.Ordinal);
        for (var i = 0; i < files.Length - MaxLogFiles; i++)
        {
            try
            {
                if (File.Exists(files[i]))
                {
                    var attr = File.GetAttributes(files[i]);
                    if ((attr & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(files[i], attr & ~FileAttributes.ReadOnly);
                    File.Delete(files[i]);
                }
            }
            catch (Exception) { /* 다음 파일로 */ }
        }
    }
}
