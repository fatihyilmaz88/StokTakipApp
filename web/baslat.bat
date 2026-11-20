@echo off
cd /d %~dp0
REM set STOK_FIRMA=Firma Adiniz
if not exist venv (
  python -m venv venv
  venv\Scripts\pip install -r requirements.txt
)
venv\Scripts\python app.py
pause
