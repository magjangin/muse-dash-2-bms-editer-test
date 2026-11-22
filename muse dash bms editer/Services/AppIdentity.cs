namespace bms_editer.Services;

// 이 에디터를 원본(뮤즈 대시 1용 "muse dash bms editer")과 가르는 이름을 한 곳에 모은다.
//
// 원본 저장소를 upstream 으로 두고 계속 받아 오는 포크라서, 이름이 코드 곳곳에 박혀 있으면
// 받아 올 때마다 충돌이 난다. 그리고 두 에디터를 한 PC 에 같이 깔기 때문에 이 값이 원본과
// 같으면 설정·자동 저장·백업·노트 그림·크래시 기록·.bms 파일 연결을 서로 덮어쓴다.
public static class AppIdentity
{
    // %LocalAppData% 아래 사용자 데이터 폴더 이름. 원본은 "muse dash bms editer".
    public const string DataFolderName = "muse dash 2 bms editer";

    // 창 제목에 붙는 이름.
    public const string DisplayName = "muse dash 2 bms editer";

    // .bms 파일 연결의 ProgId. 원본은 "MuseDashBms.Chart".
    public const string FileProgId = "MuseDash2Bms.Chart";

    public const string FileTypeName = "Muse Dash 2 BMS 차트";

    // 단일 인스턴스용 뮤텍스·파이프 이름의 앞부분. 원본은 "MuseDashBmsEditer".
    // 원본과 같으면 뮤즈 대시 1 에디터가 떠 있을 때 이 에디터를 켜도 창이 안 뜨고
    // 차트가 뮤즈 대시 1 에디터로 넘어간다.
    public const string SingleInstanceName = "MuseDash2BmsEditer";
}
