@echo off
rem Builds TaskbarPill.exe with the C# compiler that ships with Windows (.NET Framework 4). Nothing to install.
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /out:"%~dp0TaskbarPill.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "%~dp0TaskbarPill.cs"
