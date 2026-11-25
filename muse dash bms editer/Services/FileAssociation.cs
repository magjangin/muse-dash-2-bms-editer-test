using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace bms_editer.Services;

// 탐색기에서 .bms/.bme/.bml 을 더블클릭하면 이 에디터로 열리게 등록한다.
//
// HKCU 아래에만 쓴다. 관리자 권한이 필요 없고 다른 사용자 계정에는 영향이 없다.
// Windows 10/11 은 사용자가 직접 고른 기본 프로그램 기록(UserChoice)을 따로 보관하므로,
// 등록 직후에는 "연결 프로그램" 에서 한 번 골라 줘야 기본값이 바뀔 수 있다.
public static class FileAssociation
{
    public const string ProgId = "MuseDashBms.Chart";

    public static readonly string[] Extensions = { ".bms", ".bme", ".bml" };

    [SupportedOSPlatform("windows")]
    public static void Register(string exePath)
    {
        using (var progId = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId)!)
        {
            progId.SetValue("", "Muse Dash BMS 차트");

            using (var icon = progId.CreateSubKey("DefaultIcon")!)
                icon.SetValue("", $"\"{exePath}\",0");

            using (var command = progId.CreateSubKey(@"shell\open\command")!)
                command.SetValue("", $"\"{exePath}\" \"%1\"");
        }

        foreach (var extension in Extensions)
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + extension)!;
            key.SetValue("", ProgId);
        }
    }

    // 업데이트 뒤에 연결이 끊긴 것을 되살린다. 되살렸으면 true.
    //
    // 연결은 등록할 때의 exe 경로를 그대로 적어 둔다. 새 버전을 다른 폴더에 풀었거나
    // exe 이름이 바뀌면(v0.1.6 에서 `muse dash 1 bms editer N.exe` 로 바뀜) 더블클릭이
    // "파일을 찾을 수 없습니다" 로 끝나는데, 화면에는 아무 단서가 없다.
    // 그래서 시작할 때 **등록된 exe 가 사라졌을 때만** 지금 exe 로 다시 적는다.
    // 등록된 exe 가 멀쩡히 있으면(1번·2번을 같이 쓰는 경우 등) 사용자의 선택이므로 건드리지 않는다.
    [SupportedOSPlatform("windows")]
    public static bool RepairIfStale(string currentExePath)
    {
        string? command;
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ProgId + @"\shell\open\command"))
            command = key?.GetValue("") as string;

        if (!ShouldRepair(command, currentExePath, System.IO.File.Exists))
            return false;

        Register(currentExePath);
        return true;
    }

    // 등록된 명령(`"C:\...\x.exe" "%1"`)에서 exe 경로만 꺼낸다. 알아볼 수 없으면 null.
    public static string? ParseCommandExePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var text = command.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text.Substring(1, end - 1) : null;
        }

        var space = text.IndexOf(' ');
        return space > 0 ? text.Substring(0, space) : text;
    }

    // 등록이 없으면(사용자가 연결을 안 했음) 되살리지 않는다. 등록은 사용자가 메뉴로만 한다.
    public static bool ShouldRepair(string? registeredCommand, string currentExePath, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(currentExePath) || !IsDeployedExe(currentExePath))
            return false;

        var registered = ParseCommandExePath(registeredCommand);
        if (registered is null)
            return false;

        if (string.Equals(registered, currentExePath, StringComparison.OrdinalIgnoreCase))
            return false;

        return !fileExists(registered);
    }

    // `dotnet run`·테스트 호스트로 띄운 경우에는 등록하지 않는다. 그 경로는 곧 사라진다.
    public static bool IsDeployedExe(string exePath) =>
        exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && !System.IO.Path.GetFileName(exePath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
        && !System.IO.Path.GetFileName(exePath).StartsWith("testhost", StringComparison.OrdinalIgnoreCase);

    // 이 에디터가 등록한 연결만 지운다. 다른 프로그램으로 이미 바뀐 확장자는 건드리지 않는다.
    [SupportedOSPlatform("windows")]
    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, throwOnMissingSubKey: false);

        foreach (var extension in Extensions)
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + extension, writable: true);
            if (key is null)
                continue;

            if (string.Equals(key.GetValue("") as string, ProgId, StringComparison.Ordinal))
                key.DeleteValue("", throwOnMissingValue: false);
        }
    }
}
