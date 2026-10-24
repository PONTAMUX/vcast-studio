@echo off
chcp 65001 >nul
cd /d "%~dp0"
if not exist "runtime\obs\bin\64bit\obs.dll" (
  echo runtime\obs が見つかりません。OBS Studio 32.1.2 の Program Files\obs-studio を runtime\obs にコピーしてください。
  pause
  exit /b 1
)
dotnet build src\VCast.App -c Release -v q || (pause & exit /b 1)
start "" "src\VCast.App\bin\Release\net8.0-windows\VCastStudio.exe"
