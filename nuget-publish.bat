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
dotnet nuget push "src/nupkg/LLS.EFBulkExtensions.%VERSION%.nupkg" --api-key %APIKEY% --source %SOURCE%

echo.
echo Publicando pacote de símbolos (.snupkg)...
dotnet nuget push "src/nupkg/LLS.EFBulkExtensions.%VERSION%.snupkg" --api-key %APIKEY% --source %SOURCE%

echo.
echo Publicação concluída com sucesso!
pause
