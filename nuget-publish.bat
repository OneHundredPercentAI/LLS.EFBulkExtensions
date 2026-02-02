@echo off
echo ============================================
echo   Publishing NuGet Package
echo ============================================

REM Caminho do projeto
set PROJECT=src\LLS.EFBulkExtensions\LLS.EFBulkExtensions.csproj

REM Versão do pacote (opcional, se quiser sobrescrever)
set VERSION=0.2.0

REM Sua API Key do NuGet
set /p APIKEY=<nuget.key

REM URL do NuGet
set SOURCE=https://api.nuget.org/v3/index.json

echo.
echo Gerando pacote...
dotnet pack %PROJECT% -c Release -p:PackageVersion=%VERSION% -o src\nupkg

echo.
echo Publicando pacote principal (.nupkg)...
for %%f in (src\nupkg\*.nupkg) do dotnet nuget push "%%f" --api-key %APIKEY% --source %SOURCE% --skip-duplicate

echo.
echo Publicando pacote de símbolos (.snupkg)...
for %%f in (src\nupkg\*.snupkg) do dotnet nuget push "%%f" --api-key %APIKEY% --source %SOURCE% --skip-duplicate

echo.
echo Publicação concluída com sucesso!
pause
