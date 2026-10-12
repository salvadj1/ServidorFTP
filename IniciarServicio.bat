@echo off
REM ============================================================
REM Inicia el servicio de Windows "ServidorFTP" y lo deja en inicio automatico.
REM Si el servicio no esta registrado, lo registra usando el .exe de esta carpeta.
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

REM --- Si el servicio no existe (error 1060) se registra apuntando al exe de esta carpeta
sc query %SERVICIO% >nul 2>&1
if %errorlevel% equ 1060 (
    set "RUTA=%~dp0"
    if "%RUTA:~0,2%"=="\\" (
        echo ERROR: no se puede registrar desde una ruta de red. Copia la carpeta al disco local ^(por ejemplo C:\ServidorFTP^) y ejecuta el .bat desde alli.
        pause
        exit /b 1
    )
    if not exist "%~dp0ServidorFTP.exe" (
        echo ERROR: no se encuentra ServidorFTP.exe junto a este .bat.
        pause
        exit /b 1
    )
    echo El servicio no existe. Registrandolo con %~dp0ServidorFTP.exe ...
    sc create %SERVICIO% binPath= "\"%~dp0ServidorFTP.exe\"" start= delayed-auto DisplayName= "Servidor FTP"
    echo.
)

echo Configurando inicio automatico con Windows (retrasado, tras arrancar la red)...
sc config %SERVICIO% start= delayed-auto
echo.
echo Iniciando el servicio %SERVICIO%...
sc start %SERVICIO%
timeout /t 2 /nobreak >nul
echo.
sc query %SERVICIO% | findstr /i "STATE"
echo.
pause
