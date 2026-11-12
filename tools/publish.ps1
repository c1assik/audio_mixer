# Выпуск: хост плагинов, тесты, один Mixer.exe (self-contained, ReadyToRun, хост вшит), подпись обоих exe одним
# сертификатом, проверка запуска, контрольная сумма.
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\publish.ps1
#   ... -CertThumbprint <отпечаток>         подпись сертификатом из хранилища текущего пользователя
#   ... -Pfx cert.pfx -PfxPassword <пароль>  подпись сертификатом из файла
#   ... -SkipTests
#   ... -SkipHostBuild                       не пересобирать Mixer.PluginHost.exe (уже собран, например в CI)
#   ... -Version 1.2.3                       версия exe вместо <Version> из Directory.Build.props
# Результат: dist\Mixer.exe и dist\Mixer.exe.sha256.
param(
    [string]$CertThumbprint,
    [string]$Pfx,
    [string]$PfxPassword,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [string]$Version,
    [switch]$SkipTests,
    [switch]$SkipHostBuild
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dist = Join-Path $root 'dist'
$exe = Join-Path $dist 'Mixer.exe'
$hostExe = Join-Path $root 'src\Mixer.PluginHost\build\bin\Mixer.PluginHost.exe'

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Run([string]$file, [string[]]$arguments) {
    & $file @arguments
    if ($LASTEXITCODE -ne 0) { throw "$file завершился с кодом $LASTEXITCODE" }
}
function Sign([string]$file) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe не найден: нужен Windows SDK (компонент «Windows SDK Signing Tools»)' }
    $sign = @('sign', '/fd', 'sha256', '/tr', $TimestampUrl, '/td', 'sha256', '/d', 'Микшер')
    if ($CertThumbprint) { $sign += @('/sha1', $CertThumbprint) }
    else {
        $sign += @('/f', $Pfx)
        if ($PfxPassword) { $sign += @('/p', $PfxPassword) }
    }
    Run $signtool.FullName ($sign + @($file))
    Run $signtool.FullName @('verify', '/pa', $file)
}

# хост плагинов вшивается в Mixer.exe при сборке Mixer.Plugins: собрать и подписать его нужно до тестов и publish
if (-not $SkipHostBuild) {
    Step 'хост плагинов'
    Run 'powershell' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'build-pluginhost.ps1'))
}
if (-not (Test-Path $hostExe)) { throw "нет $hostExe — соберите tools\build-pluginhost.ps1 (нужны Visual Studio C++ и CMake)" }
if ($CertThumbprint -or $Pfx) {
    Step 'подпись хоста плагинов'
    Sign $hostExe
}

if (-not $SkipTests) {
    Step 'тесты'
    Run 'dotnet' @('test', (Join-Path $root 'tests\Mixer.Tests'), '-c', 'Release', '-nologo')
}

Step 'сборка'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
$publish = @('publish', (Join-Path $root 'src\Mixer.App'), '-c', 'Release', '-p:PublishProfile=win-x64', '-nologo')
if ($Version) { $publish += "-p:Version=$Version" }
Run 'dotnet' $publish
# рядом с exe не должно остаться ничего, кроме него самого
Get-ChildItem $dist -File | Where-Object { $_.Name -ne 'Mixer.exe' } | Remove-Item -Force
if (-not (Test-Path $exe)) { throw "нет $exe" }

if ($CertThumbprint -or $Pfx) {
    Step 'подпись'
    Sign $exe
}
else {
    Write-Host "`nexe не подписаны: SmartScreen предупредит при первом запуске, антивирус может придраться к хосту плагинов (см. -CertThumbprint / -Pfx)" -ForegroundColor Yellow
}

Step 'проверка запуска (без звука)'
# --no-audio: устройства не открываются, привязки не трогаются, mutex не берётся
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("mixer-smoke-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
try {
    $png = Join-Path $tmp 'window.png'
    $p = Start-Process $exe -ArgumentList @('--no-audio', '--graph', "`"$tmp\graph.json`"", '--log-dir', "`"$tmp\logs`"", '--snapshot', "`"$png`"") -PassThru
    if (-not $p.WaitForExit(60000)) { $p.Kill(); throw 'Mixer.exe не закрылся за 60 с' }
    if (-not (Test-Path $png)) { throw "Mixer.exe не снял окно; журнал: $tmp\logs" }
    Write-Host 'окно открылось и закрылось'

    # хост плагинов вшит: распаковывается в %LocalAppData%\Mixer\bin\<версия>\, запускается и считает блок
    $p = Start-Process $exe -ArgumentList @('--check-plugin-host', '--log-dir', "`"$tmp\logs-host`"") -PassThru
    if (-not $p.WaitForExit(60000)) { $p.Kill(); throw 'проверка хоста плагинов не закончилась за 60 с' }
    if ($p.ExitCode -ne 0) { $png = $null; throw "хост плагинов не работает в собранном exe; журнал: $tmp\logs-host" }
    Write-Host 'хост плагинов распаковался и обработал блок'
}
finally {
    if ($png -and (Test-Path $png)) { Remove-Item $tmp -Recurse -Force }
}

Step 'контрольная сумма'
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$exe.sha256" -Value "$hash  Mixer.exe" -Encoding ascii
$size = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
$ver = (Get-Item $exe).VersionInfo.ProductVersion
Write-Host "`nготово: $exe ($size МБ, версия $ver)`nSHA-256: $hash" -ForegroundColor Green
