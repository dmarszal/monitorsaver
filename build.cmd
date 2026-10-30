@echo off
rem Builds MonitorSaver.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize /target:winexe /out:MonitorSaver.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll MonitorSaver.cs
