using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace bms_editer.Services;

// 몬스터가 등장하는 방향. UID 끝 두 자리(yy)로 알 수 있다: 07·10 위에서, 13·16 아래에서.
// 속도 2·3 은 같은 모션의 다음 두 번호(08·09, 11·12, 14·15, 17·18)라 속도 1 번호로 바꿔서 본다.
public enum NoteDirection
{
    None,
    Up,
    Down,
}

// 보스 연출 노트(xx=01)가 하는 일. 보스 그림 위에 글자로 구분한다.
public enum BossMarker
{
    None,
    Enter,
    Exit,
    Start,
    End,
}

// 격자에 그릴 노트 그림 한 장. 그림은 이미 그림자와 빈 여백을 걷어낸 상태다.
public sealed class NoteIcon
{
    public NoteIcon(Bitmap image, double scale, NoteDirection direction = NoteDirection.None, BossMarker marker = BossMarker.None, int? speed = null)
    {
        Image = image;
        Scale = scale;
        Direction = direction;
        Marker = marker;
        Speed = speed;
    }

    // 보스 등장·퇴장·액션 시작·끝. 같은 보스 그림이라 구석에 글자를 얹어서 구분한다.
    public BossMarker Marker { get; }

    // 위/아래 등장 노트는 같은 그림이라, 그림 구석에 작은 삼각형을 얹어서 구분한다.
    public NoteDirection Direction { get; }

    // 속도(1~3). 속도가 없는 노트(씬 전환·보스 연출 등)는 null.
    public int? Speed { get; }

    public Bitmap Image { get; }

    // 기본 크기에 곱하는 값. 가로로 넓은 그림처럼 같은 칸에 넣으면 작아 보이는 그림을 키울 때 쓴다.
    public double Scale { get; }
}

// UID(파일명 앞 6자리) 로 노트 그림을 찾는다.
//
// 그림은 <UID>.png 로 찾는다. 사용자 폴더(NoteIconImporter.UserFolder, "노트 그림 가져오기"가 만든다)를 먼저,
// 다음으로 exe 옆 note_icons 폴더를 본다. 게임에서 뽑은 그림이라 배포 exe 에는 넣지 않는다.
// 폴더가 없거나 그림이 없는 노트는 호출하는 쪽이 단색 동그라미로 그린다.
public sealed class NoteIconCatalog : IDisposable
{
    // 같은 종류(UID 앞 4자리 = 씬+종류)는 그림 모양이 비슷해서 배율도 같게 둔다.
    // 종류 번호만으로 찾으면 다른 씬의 같은 번호(0106 구슬 ↔ 0206 블록)에 배율이 번져서 씬까지 같이 본다.
    // 6자리 전체(yy 까지)로 적은 항목이 있으면 그것이 먼저다.
    // 지금은 눈으로 보고 정한 값이다.
    private static readonly Dictionary<string, double> ScaleByKind = new()
    {
        // 0112 는 가로로 넓고 낮아서 30px 에서 작아 보였다.
        ["0112"] = 38.0 / 30.0,

        // 0106 은 구슬 오른쪽에 길게 꼬리 선이 달려 있어서, 폭에 맞추면 구슬이 18px 로 줄어든다.
        ["0106"] = 1.7,

        // 0211 은 낮고 넓은 날개 네모라 21px 높이로 작아 보였다.
        ["0211"] = 1.2,
    };

    private readonly string _folder;
    private readonly string? _userFolder;
    private readonly Dictionary<string, NoteIcon?> _byUid = new(StringComparer.Ordinal);

    // 그림은 백그라운드에서 읽는다. 사전과 Dispose 가 엇갈리지 않게 잠근다.
    private readonly object _gate = new();
    private bool _disposed;

    public NoteIconCatalog(string folder, string? userFolder = null)
    {
        _folder = folder;
        _userFolder = userFolder;
    }

    public static string DefaultFolder
    {
        get
        {
            var baseDir = Path.Combine(AppContext.BaseDirectory, "note_icons");
            if (Directory.Exists(baseDir))
                return baseDir;

            var cwdDir = Path.Combine(Environment.CurrentDirectory, "note_icons");
            if (Directory.Exists(cwdDir))
                return cwdDir;

            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (var depth = 0; depth < 4 && dir is not null; depth++)
                {
                    var candidate = Path.Combine(dir.FullName, "note_icons");
                    if (Directory.Exists(candidate))
                        return candidate;

                    var subCandidate = Path.Combine(dir.FullName, "muse dash bms editer", "note_icons");
                    if (Directory.Exists(subCandidate))
                        return subCandidate;

                    dir = dir.Parent;
                }
            }
            catch
            {
                // 경로 탐색 중 예외 무시
            }

            return baseDir;
        }
    }

    // #WAV 에 적힌 글자(경로여도 된다)의 파일명이 여섯 자리 숫자로 시작하면 그 여섯 자리.
    public static string? UidOf(string wavText) => SceneUid.Of(wavText);

    // 해머(종류 15). 줄이 비스듬하거나 가로로 길게 달려서 줄까지 두면 머리가 작아진다.
    // 속도 2·3 노트(yy 02·03, 05·06 ... 17·18)는 속도 1 노트와 모양이 같다. 속도 1 번호로 돌려준다.
    // 씬 전환(0004yy)과 보스 연출(xx=01)은 yy 가 속도가 아니라서 그대로 둔다.
    public static string SpeedOneOf(string uid) => NoteSpeed.WithSpeed(uid, 1);

    private static bool IsHammer(string uid) => uid.Length == 6 && uid.AsSpan(2, 2).SequenceEqual("15");

    // 씬 번호가 00 이 아니고 종류가 01 이면 보스 연출 노트다. 끝 두 자리 07·08·09·10 은 방향이 아니라 액션 번호다.
    private static bool IsBossAction(string uid) =>
        uid.Length == 6 && !uid.AsSpan(0, 2).SequenceEqual("00") && uid.AsSpan(2, 2).SequenceEqual("01");

    public static BossMarker BossMarkerOf(string uid)
    {
        if (!IsBossAction(uid))
            return BossMarker.None;

        return uid.AsSpan(4, 2) switch
        {
            "01" => BossMarker.Enter,
            "02" => BossMarker.Exit,
            "07" or "09" => BossMarker.Start,
            "08" or "10" => BossMarker.End,
            _ => BossMarker.None,
        };
    }

    // 씬 전환(0004yy). yy 는 바꿀 씬 번호라 방향이 아니다.
    private static bool IsSceneChange(string uid) => uid.StartsWith("0004", StringComparison.Ordinal);

    public static NoteDirection DirectionOf(string uid)
    {
        // 씬 전환을 빼먹어서 7번·10번 씬 전환(000407·000410)에 "위에서 등장" 삼각형이 붙었다.
        if (uid.Length != 6 || IsBossAction(uid) || IsSceneChange(uid))
            return NoteDirection.None;

        uid = SpeedOneOf(uid);
        return uid.AsSpan(4, 2) switch
        {
            "07" or "10" => NoteDirection.Up,
            "13" or "16" => NoteDirection.Down,
            _ => NoteDirection.None,
        };
    }

    private const double AirDoubleNoteScale = 1.3;

    // 보스는 몸집이 커서 노트 크기로는 작게 보인다. 보스 연출 노트(종류 01)는 키워서 그린다.
    private const double BossScale = 2.0;

    // 종류가 05 이고 공중(yy 04, 위 10, 아래 16)인 그림.
    private static bool IsAirDoubleNote(string uid) =>
        uid.AsSpan(2, 2).SequenceEqual("05") && uid.AsSpan(4, 2) is "04" or "10" or "16";

    public static double ScaleFor(string uid)
    {
        if (uid.Length != 6)
            return 1.0;

        uid = SpeedOneOf(uid);

        if (ScaleByKind.TryGetValue(uid, out var exact))
            return exact;

        if (ScaleByKind.TryGetValue(uid.Substring(0, 4), out var scale))
            return scale;

        // 복선(05)의 공중은 어느 씬이든 위로 길쭉한 막대가 달려 몸통이 작아 보인다.
        if (IsBossAction(uid))
            return BossScale;

        return IsAirDoubleNote(uid) ? AirDoubleNoteScale : 1.0;
    }

    // 키음 번호 → 그림. 그림이 있는 번호만 담는다.
    public IReadOnlyDictionary<string, NoteIcon> Build(IEnumerable<KeyValuePair<string, string>> wavTable)
    {
        var result = new Dictionary<string, NoteIcon>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, text) in wavTable)
        {
            var uid = UidOf(text);
            if (uid is null)
                continue;

            var icon = Find(uid);
            if (icon is not null)
                result[key] = icon;
        }

        return result;
    }

    private NoteIcon? Find(string uid)
    {
        lock (_gate)
        {
            if (_disposed)
                return null;

            if (_byUid.TryGetValue(uid, out var cached))
                return cached;
        }

        // 디스크 읽기와 픽셀 처리는 락 밖에서 수행해 Dispose/다른 조회를 블로킹하지 않는다.
        Bitmap? ownPrepared = null;
        NoteIcon? icon = null;
        var baseUid = SpeedOneOf(uid);
        try
        {
            // 속도별 그림 파일이 따로 있으면 그것을, 없으면 속도 1 그림(Bitmap)을 공유해서 쓴다.
            Bitmap? prepared = null;
            using (var ownSource = OpenSource(uid))
            {
                if (ownSource is not null)
                {
                    ownPrepared = NoteIconImage.Prepare(ownSource, IsHammer(uid));
                    prepared = ownPrepared;
                }
            }

            if (prepared is null && baseUid != uid)
                prepared = Find(baseUid)?.Image;

            if (prepared is not null)
                icon = new NoteIcon(prepared, ScaleFor(baseUid), DirectionOf(baseUid), BossMarkerOf(uid), NoteSpeed.Of(uid));
        }
        catch (Exception)
        {
            // 깨진 그림 한 장이 에디터를 멈추게 하지 않는다. 그 노트는 단색으로 그려진다.
            ownPrepared?.Dispose();
            ownPrepared = null;
            icon = null;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                ownPrepared?.Dispose();
                return null;
            }

            if (_byUid.TryGetValue(uid, out var existing))
            {
                ownPrepared?.Dispose();
                return existing;
            }

            _byUid[uid] = icon;
            return icon;
        }
    }

    // 사용자 폴더, exe 옆 폴더 순으로 쓰고, 없으면 exe 안에 넣어 둔 그림(로컬/개인용 빌드, EmbedNoteIcons=true)을 쓴다.
    private Bitmap? OpenSource(string uid)
    {
        if (_userFolder is not null)
        {
            var userPath = Path.Combine(_userFolder, uid + ".png");
            if (File.Exists(userPath))
                return new Bitmap(userPath);
        }

        var path = Path.Combine(_folder, uid + ".png");
        if (File.Exists(path))
            return new Bitmap(path);

        using var stream = typeof(NoteIconCatalog).Assembly.GetManifestResourceStream($"note_icons/{uid}.png");
        return stream is null ? null : new Bitmap(stream);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;

            var disposed = new HashSet<Bitmap>();
            foreach (var icon in _byUid.Values)
            {
                if (icon is not null && disposed.Add(icon.Image))
                    icon.Image.Dispose();
            }

            _byUid.Clear();
        }
    }
}
