@echo off
set "EXE=%~dp0publish\personal\muse dash 2 bms editer 1.exe"
if not exist "%EXE%" (
    echo [1번 개인용] exe가 없습니다. 먼저 빌드를 진행합니다...
    call "%~dp0build_personal.bat"
    if not exist "%EXE%" (
        echo [오류] 빌드에 실패하여 실행할 수 없습니다.
        pause
        exit /b 1
    )
)
start "" "%EXE%" %*
