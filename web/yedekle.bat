@echo off
REM Veritabani + dosyalari tarihli olarak yedekler. Gorev Zamanlayici ile her gece calistirin.
cd /d %~dp0
if not exist yedek mkdir yedek
for /f %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmm"') do set TS=%%i
venv\Scripts\python -c "import sqlite3;s=sqlite3.connect('stok.db');d=sqlite3.connect('yedek/stok_%TS%.db');s.backup(d);d.close()"
if exist uploads robocopy uploads yedek\uploads /E /NFL /NDL /NJH /NJS >nul
forfiles /p yedek /m *.db /d -60 /c "cmd /c del @path" 2>nul
