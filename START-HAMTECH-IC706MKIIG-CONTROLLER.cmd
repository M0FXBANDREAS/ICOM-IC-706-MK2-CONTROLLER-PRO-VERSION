@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title HamTech M0FXB ICOM IC-706MKIIG Controller

echo HamTech M0FXB ICOM IC-706MKIIG Controller V1.17

where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo ERROR: .NET 8 SDK/runtime is required.
  pause
  exit /b 1
)

set "PROJECT=%~dp0FT857DControl\FT857DControl.csproj"
if not exist "%PROJECT%" (
  for /r "%~dp0" %%F in (FT857DControl.csproj) do if not defined FOUNDPROJECT set "FOUNDPROJECT=%%~fF"
  if defined FOUNDPROJECT set "PROJECT=%FOUNDPROJECT%"
)

if not exist "%PROJECT%" (
  echo.
  echo ERROR: FT857DControl.csproj could not be found anywhere under:
  echo %~dp0
  echo.
  echo Make sure the WHOLE ZIP is extracted before running this file.
  pause
  exit /b 1
)

echo Project: "%PROJECT%"
echo.
dotnet run --project "%PROJECT%" -c Release
if errorlevel 1 (
  echo.
  echo The build/run failed. Send HamTech a screenshot of the error above.
  pause
  exit /b 1
)
endlocal
