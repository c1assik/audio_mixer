# Выпуск: тесты, один Mixer.exe (self-contained, ReadyToRun), подпись, проверка запуска, контрольная сумма.
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\publish.ps1
#   ... -CertThumbprint <отпечаток>         подпись сертификатом из хранилища текущего пользователя
#   ... -Pfx cert.pfx -PfxPassword <пароль>  подпись сертификатом из файла
#   ... -SkipTests
#   ... -Version 1.2.3                       версия exe вместо <Version> из Directory.Build.props
# Результат: dist\Mixer.exe и dist\Mixer.exe.sha256.
param(
    [string]$CertThumbprint,
    [string]$Pfx,
    [string]$PfxPassword,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [string]$Version,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dist = Join-Path $root 'dist'
$exe = Join-Path $dist 'Mixer.exe'

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Run([string]$file, [string[]]$arguments) {
    & $file @arguments
    if ($LASTEXITCODE -ne 0) { throw "$file завершился с кодом $LASTEXITCODE" }
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
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe не найден: нужен Windows SDK (компонент «Windows SDK Signing Tools»)' }
    $sign = @('sign', '/fd', 'sha256', '/tr', $TimestampUrl, '/td', 'sha256', '/d', 'Микшер')
    if ($CertThumbprint) { $sign += @('/sha1', $CertThumbprint) }
    else {
        $sign += @('/f', $Pfx)
        if ($PfxPassword) { $sign += @('/p', $PfxPassword) }
    }
    Run $signtool.FullName ($sign + @($exe))
    Run $signtool.FullName @('verify', '/pa', $exe)
}
else {
    Write-Host "`nexe не подписан: SmartScreen предупредит при первом запуске (см. -CertThumbprint / -Pfx)" -ForegroundColor Yellow
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
}
finally {
    if (Test-Path $png) { Remove-Item $tmp -Recurse -Force }
}

Step 'контрольная сумма'
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$exe.sha256" -Value "$hash  Mixer.exe" -Encoding ascii
$size = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
$ver = (Get-Item $exe).VersionInfo.ProductVersion
Write-Host "`nготово: $exe ($size МБ, версия $ver)`nSHA-256: $hash" -ForegroundColor Green
