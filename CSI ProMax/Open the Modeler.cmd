@echo off
rem Starts the CSI ProMax Modeler with no CSI program on this PC. It runs CsiProMax.dll on Microsoft's .NET 8.
setlocal
set "HERE=%~dp0"
set "DOTNET="
if defined DOTNET_ROOT if exist "%DOTNET_ROOT%\dotnet.exe" set "DOTNET=%DOTNET_ROOT%\dotnet.exe"
if not defined DOTNET if exist "%ProgramW6432%\dotnet\dotnet.exe" set "DOTNET=%ProgramW6432%\dotnet\dotnet.exe"
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" set "DOTNET=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
if not defined DOTNET if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if not defined DOTNET for /d %%D in ("%ProgramFiles%\Computers and Structures\ETABS*") do if exist "%%D\dotnet.exe" set "DOTNET=%%D\dotnet.exe"
if not defined DOTNET (
  echo CSI ProMax needs Microsoft's ".NET Desktop Runtime 8" ^(x64^), which is free:
  echo   https://dotnet.microsoft.com/download/dotnet/8.0
  echo Install it once, then open this file again.
  pause
  exit /b 1
)
start "CSI ProMax Modeler" /min "%DOTNET%" "%HERE%CsiProMax.dll" --modeler %*
