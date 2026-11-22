using System;
using System.Runtime.Versioning;
using Microsoft.Win32;
using bms_editer.Services;
using Xunit;

namespace bms_editer.Tests;

public sealed class FileAssociationTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void 기존_기본값이_있으면_덮어쓰지_않고_OpenWithProgids에만_추가한다()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var tempKeyName = @"Software\_Test_MuseDash2BmsEditer_" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(tempKeyName)!;
            using (var bmsKey = root.CreateSubKey(".bms")!)
            {
                bmsKey.SetValue("", "MuseDashBms.Chart");
            }

            FileAssociation.Register(root, @"C:\dummy\muse dash 2 bms editer 1.exe");

            // .bms 는 기존 뮤즈 대시 1 에디터 연결을 유지한다.
            using (var bmsKey = root.OpenSubKey(".bms")!)
            {
                Assert.Equal("MuseDashBms.Chart", bmsKey.GetValue("") as string);
                using var openWith = bmsKey.OpenSubKey("OpenWithProgids")!;
                Assert.Equal("", openWith.GetValue(FileAssociation.ProgId) as string);
            }

            // 기본값이 비어 있던 .bme / .bml 은 이 에디터로 연결되고 OpenWithProgids 에도 등록된다.
            using (var bmeKey = root.OpenSubKey(".bme")!)
            {
                Assert.Equal(FileAssociation.ProgId, bmeKey.GetValue("") as string);
                using var openWith = bmeKey.OpenSubKey("OpenWithProgids")!;
                Assert.Equal("", openWith.GetValue(FileAssociation.ProgId) as string);
            }

            // ProgId 정의도 정상 생성된다.
            using (var progIdKey = root.OpenSubKey(FileAssociation.ProgId)!)
            {
                Assert.Equal(AppIdentity.FileTypeName, progIdKey.GetValue("") as string);
                using var commandKey = progIdKey.OpenSubKey(@"shell\open\command")!;
                Assert.Equal("\"C:\\dummy\\muse dash 2 bms editer 1.exe\" \"%1\"", commandKey.GetValue("") as string);
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(tempKeyName, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void 해제하면_이_에디터가_넣은_것만_지우고_다른_연결은_남긴다()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var tempKeyName = @"Software\_Test_MuseDash2BmsEditer_" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(tempKeyName, writable: true)!;
            using (var bmsKey = root.CreateSubKey(".bms")!)
            {
                bmsKey.SetValue("", "MuseDashBms.Chart");
                using var openWith = bmsKey.CreateSubKey("OpenWithProgids")!;
                openWith.SetValue("MuseDashBms.Chart", "", RegistryValueKind.String);
            }

            FileAssociation.Register(root, @"C:\dummy\muse dash 2 bms editer 1.exe");
            FileAssociation.Unregister(root);

            Assert.Null(root.OpenSubKey(FileAssociation.ProgId));

            using (var bmsKey = root.OpenSubKey(".bms")!)
            {
                Assert.Equal("MuseDashBms.Chart", bmsKey.GetValue("") as string);
                using var openWith = bmsKey.OpenSubKey("OpenWithProgids")!;
                Assert.Null(openWith.GetValue(FileAssociation.ProgId));
                Assert.Equal("", openWith.GetValue("MuseDashBms.Chart") as string);
            }

            using (var bmeKey = root.OpenSubKey(".bme")!)
            {
                Assert.Null(bmeKey.GetValue(""));
                using var openWith = bmeKey.OpenSubKey("OpenWithProgids")!;
                Assert.Null(openWith.GetValue(FileAssociation.ProgId));
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(tempKeyName, throwOnMissingSubKey: false);
        }
    }
}
