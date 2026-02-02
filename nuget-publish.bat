@echo off
echo ============================================
echo   Publishing NuGet Package
echo ============================================

REM Caminho do projeto
set PROJECT=src\LLS.EFBulkExtensions\LLS.EFBulkExtensions.csproj

REM Lê a versão do .csproj usando MSBuild
for /f %%v in ('
    powershell -NoLogo -NoProfile -Command ^
    "(Select-Xml -Path '%PROJECT%' -XPath '//Version').Node.InnerText.Trim()"
') do set VERSION=%%v

REM Remove qualquer caractere inválido (incluindo +, BOM, tabs, etc.)
for /f "delims=0123456789." %%a in ("%VERSION%") do (
    set VERSION=%VERSION:%%a=%
)

echo Versão detectada: %VERSION%

REM API Key do NuGet
set /p APIKEY=<nuget.key

REM URL do NuGet
set SOURCE=https://api.nuget.org/v3/index.json

echo.
echo Limpando pasta src\nupkg...
if exist src\nupkg (
    del /q src\nupkg\*.*
)

echo.
echo Gerando pacote...
dotnet pack %PROJECT% -c Release -p:PackageVersion=%VERSION% -o src\nupkg

echo.
echo Publicando pacote principal (.nupkg)...
for %%f in (src\nupkg\*.nupkg) do (
    dotnet nuget push "%%f" --api-key %APIKEY% --source %SOURCE% --skip-duplicate
)

echo.
echo Publicando pacote de símbolos (.snupkg)...
for %%f in (src\nupkg\*.snupkg) do (
    dotnet nuget push "%%f" --api-key %APIKEY% --source %SOURCE% --skip-duplicate
)

echo.
echo Publicação concluída com sucesso!
pause