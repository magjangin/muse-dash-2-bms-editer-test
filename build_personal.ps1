$ErrorActionPreference = "Stop"

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host " [1번: 개인용] Muse Dash 2 BMS Editor 패키징 (내장 exe)" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan

$targetDir = "publish/personal"
$exePath = Join-Path $targetDir "muse dash 2 bms editer 1.exe"

# 1. 기존 폴더 내용 정리
if (Test-Path $targetDir) {
    Write-Host "[정리] 기존 개인용 폴더 내용 정리 중..." -ForegroundColor Gray
    Remove-Item -Path $targetDir -Recurse -Force
}

# 2. 1번 개인용 빌드 실행 (EmbedNoteIcons=true, AssemblyName="muse dash 2 bms editer 1")
Write-Host "[빌드] 1번 개인용 exe (에셋 내장 + 1번 골드 아이콘) 패키징 중..." -ForegroundColor Yellow
dotnet publish "muse dash bms editer/muse dash bms editer.csproj" -p:PublishProfile=personal
if ($LASTEXITCODE -ne 0) {
    throw "개인용 exe 빌드 실패!"
}

# 3. AGENTS.md 1번 규칙 검증 (단일 파일만 존재, 외부 note_icons 폴더 금지)
if (-not (Test-Path $exePath)) {
    throw "검증 실패: $exePath 가 생성되지 않았습니다."
}
if (Test-Path (Join-Path $targetDir "note_icons")) {
    throw "검증 실패: 개인용 배포 폴더($targetDir)에 외부 note_icons 폴더가 존재하면 안 됩니다."
}

Write-Host "`n========================================================" -ForegroundColor Green
Write-Host " [1번: 개인용] 빌드 완료!" -ForegroundColor Green
Write-Host " 실행 파일: $exePath (단독 내장 exe)" -ForegroundColor Green
Write-Host " 앱 아이콘: Assets\icon_personal.ico (골드 앰버 + 1번 뱃지)" -ForegroundColor Green
Write-Host " (Git 추적 제외: .gitignore 적용됨)" -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Green
