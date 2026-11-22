using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace bms_editer.Services;

// 탐색기에서 .bms/.bme/.bml 을 열 수 있도록 이 에디터를 등록한다.
//
// HKCU 아래에만 쓴다. 관리자 권한이 필요 없고 다른 사용자 계정에는 영향이 없다.
// 뮤즈 대시 1 에디터 등 기존 프로그램과 충돌하지 않도록 확장자의 기본값은
// 비어 있을 때만 채우고, OpenWithProgids 에 항상 등록해 '연결 프로그램' 목록에서 고를 수 있게 한다.
public static class FileAssociation
{
    public const string ProgId = AppIdentity.FileProgId;

    public static readonly string[] Extensions = { ".bms", ".bme", ".bml" };

    [SupportedOSPlatform("windows")]
    public static void Register(string exePath)
    {
        using var classesRoot = Registry.CurrentUser.CreateSubKey(@"Software\Classes")!;
        Register(classesRoot, exePath);
    }

    [SupportedOSPlatform("windows")]
    public static void Register(RegistryKey classesRoot, string exePath)
    {
        using (var progId = classesRoot.CreateSubKey(ProgId)!)
        {
            progId.SetValue("", AppIdentity.FileTypeName);

            using (var icon = progId.CreateSubKey("DefaultIcon")!)
                icon.SetValue("", $"\"{exePath}\",0");

            using (var command = progId.CreateSubKey(@"shell\open\command")!)
                command.SetValue("", $"\"{exePath}\" \"%1\"");
        }

        foreach (var extension in Extensions)
        {
            using var key = classesRoot.CreateSubKey(extension)!;
            var currentDefault = key.GetValue("") as string;
            if (string.IsNullOrWhiteSpace(currentDefault))
                key.SetValue("", ProgId);

            using var openWith = key.CreateSubKey("OpenWithProgids")!;
            openWith.SetValue(ProgId, "", RegistryValueKind.String);
        }
    }

    // 이 에디터가 등록한 연결만 지운다. 다른 프로그램으로 이미 바뀐 확장자는 건드리지 않는다.
    [SupportedOSPlatform("windows")]
    public static void Unregister()
    {
        using var classesRoot = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
        if (classesRoot is not null)
            Unregister(classesRoot);
    }

    [SupportedOSPlatform("windows")]
    public static void Unregister(RegistryKey classesRoot)
    {
        classesRoot.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);

        foreach (var extension in Extensions)
        {
            using var key = classesRoot.OpenSubKey(extension, writable: true);
            if (key is null)
                continue;

            if (string.Equals(key.GetValue("") as string, ProgId, StringComparison.Ordinal))
                key.DeleteValue("", throwOnMissingValue: false);

            // 등록할 때 '연결 프로그램' 목록에도 넣었으므로 같이 뺀다.
            // 남겨 두면 지워진 ProgId 를 가리키는 빈 항목이 목록에 남는다.
            using var openWith = key.OpenSubKey("OpenWithProgids", writable: true);
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
        }
    }
}
