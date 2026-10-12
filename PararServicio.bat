@echo off
REM ============================================================
REM Para el servicio de Windows "ServidorFTP" y quita su inicio automatico.
REM Si el servicio se registro con otro nombre, cambia SERVICIO.
REM Se eleva a administrador automaticamente si hace falta.
REM ============================================================
set SERVICIO=ServidorFTP

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Se necesitan permisos de administrador. Solicitando elevacion...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo Quitando el inicio automatico (pasa a manual)...
sc config %SERVICIO% start= demand
echo.
echo Parando el servicio %SERVICIO%...
sc stop %SERVICIO%
timeout /t 3 /nobreak >nul
echo.
sc query %SERVICIO% | findstr /i "STATE"
echo.
pause
