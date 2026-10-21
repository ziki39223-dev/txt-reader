@echo off
rem TxtBar 一键编译：主程序 + 清洗工具 + 自检
setlocal
cd /d "%~dp0"

set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto nocsc

set "SRC=src\TxtBar.cs src\TxtBar.Bar.cs src\TxtBar.Find.cs src\TxtBar.Swap.cs src\TxtBar.Clean.cs src\AssemblyInfo.cs"
set "OPT=/nologo /utf8output /nowarn:0414,0649,0219 /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll"

echo [1/3] 编译 TxtBar.exe ...
"%CSC%" /target:winexe /main:TxtBar.Program /out:"TxtBar.exe" %OPT% %SRC% "src\TxtBar.SelfTest.cs"
if errorlevel 1 goto buildfail
echo       OK -^> %CD%\TxtBar.exe

echo.
echo [2/3] 编译 TxtClean.exe（源文本清洗工具）...
"%CSC%" /target:winexe /main:TxtBar.CleanEntry /out:"TxtClean.exe" %OPT% "src\TxtBar.cs" "src\TxtBar.Clean.cs" "src\AssemblyInfoClean.cs"
if errorlevel 1 goto buildfail
echo       OK -^> %CD%\TxtClean.exe

echo.
echo [3/3] 自检 ...
"%~dp0TxtBar.exe" --selftest
if errorlevel 1 goto testfail

echo.
echo 全部完成。
echo   双击 TxtBar.exe    开始逐行阅读
echo   双击 TxtClean.exe  清洗「书库\原始」里的 txt
goto end

:nocsc
echo [X] 找不到系统自带的 C# 编译器 csc.exe
echo     请确认是 Windows，且 .NET Framework 4.x 已安装。
goto end

:buildfail
echo [X] 编译失败，请看上面的错误信息。
goto end

:testfail
echo [!] 自检有失败项，请看上面的输出。
goto end

:end
endlocal
