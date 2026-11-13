@echo off
setlocal
cd /d "%~dp0"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set WPF=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF
set XAML=C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll
set WORK=%TEMP%\sfx_build
if not exist "%WORK%" mkdir "%WORK%"

echo [1/3] compile main app
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /main:JellyAll ^
 /win32manifest:"DesktopJelly.manifest" /win32icon:"app.ico" ^
 /reference:"%WPF%\PresentationCore.dll" ^
 /reference:"%WPF%\PresentationFramework.dll" ^
 /reference:"%WPF%\WindowsBase.dll" ^
 /reference:"%XAML%" ^
 /out:"%WORK%\app_res.exe" ^
 DesktopJelly.cs JellyLayer.cs PerformanceLog.cs LocalAudioPlayer.cs WaveAudio.cs ^
 JellyAll.cs DesktopHost.cs WpfVideo.cs VideoWindows.cs CursorFollow.cs IconWindow.cs PopWindows.cs CmdColor.cs NetGdiFx.cs FrameStage.cs KeySpam.cs Benchmark.cs ScreenFx.cs MsgBoxPop.cs VictimWindows.cs Intro.cs WindowGuard.cs SetupGate.cs
if errorlevel 1 goto fail
copy /y "NetGDI.dll" "%WORK%\NetGDI.dll" >nul
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ ^
 /reference:"%WORK%\NetGDI.dll" ^
/out:"%WORK%\ngdi.exe" ^
 NgdiHost.cs
if errorlevel 1 goto fail


echo [2/3] copy payload
copy /y "..\reference-audio.wav" "%WORK%\audio.wav" >nul
copy /y "..\41834646747-1-192.mp4" "%WORK%\video.mp4" >nul
copy /y "..\victim.jpg" "%WORK%\victim.jpg" >nul
copy /y "..\ending.wav" "%WORK%\ending.wav" >nul

copy /y "..\TGÆµµÀÔ¤ÀÀ\TgPreview.exe" "%WORK%\tgpv.exe" >nul
copy /y "..\Ô´Âë\NetGDI.dll" "%WORK%\ngdi.dll" >nul
if not exist "%WORK%\ngdi.dll" copy /y "..\NetGDI.dll" "%WORK%\ngdi.dll" >nul
copy /y "..\frames.dat" "%WORK%\frames.dat" >nul

echo [3/3] pack self-extract exe
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /win32icon:"app.ico" ^
 /out:"%WORK%\sfx_out.exe" ^
 /resource:"%WORK%\app_res.exe",app.exe ^
 /resource:"%WORK%\audio.wav",audio.wav ^
 /resource:"%WORK%\video.mp4",video.mp4 ^
 /resource:"%WORK%\victim.jpg",victim.jpg ^
 /resource:"%WORK%\ending.wav",ending.wav ^
 /resource:"%WORK%\tgpv.exe",tgpv.exe ^
 /resource:"%WORK%\ngdi.dll",ngdi.dll ^
 /resource:"%WORK%\ngdi.exe",ngdi.exe ^
 /resource:"%WORK%\frames.dat",frames.dat ^
 Sfx.cs
if errorlevel 1 goto fail

echo BUILD_OK
goto done

:fail
echo BUILD_FAIL
exit /b 1

:done
endlocal
exit /b 0
