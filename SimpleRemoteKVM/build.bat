@echo off
cd /d "%~dp0"
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe

if not exist "%CSC%" (
    set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
)

echo [BUILD] Compilando SimpleRemoteKVM com Interface Grafica usando %CSC%...

"%CSC%" /target:winexe /out:SimpleRemoteKVM.exe /platform:anycpu /optimize+ /r:System.dll,System.Windows.Forms.dll,System.Drawing.dll NativeMethods.cs Protocol.cs InputHookManager.cs InputInjector.cs NetworkPeer.cs MainForm.cs Program.cs

if %ERRORLEVEL% EQU 0 (
    echo [BUILD SUCCESS] Executavel com Interface Grafica gerado com sucesso: SimpleRemoteKVM.exe
) else (
    echo [BUILD ERROR] Falha na compilacao. Codigo de erro: %ERRORLEVEL%
)
