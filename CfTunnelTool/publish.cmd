@echo off
rem Build a single-file exe: publish\CfTunnelTool.exe (target PC does not need .NET installed)
cd /d "%~dp0"
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
if errorlevel 1 (
  echo.
  echo BUILD FAILED
  exit /b 1
)
echo.
echo Done: %~dp0publish\CfTunnelTool.exe
