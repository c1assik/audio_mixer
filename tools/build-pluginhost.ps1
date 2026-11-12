# Сборка Mixer.PluginHost.exe (C++, VST3 SDK) и примера AGain.
# Нужны Visual Studio 2022 или Build Tools с компонентами «C++ x64», «Windows SDK» и «CMake».
#   ./tools/build-pluginhost.ps1                 # Release в src/Mixer.PluginHost/build
#   ./tools/build-pluginhost.ps1 -Config Debug
# Результат: build/bin/Mixer.PluginHost.exe и build/VST3/<Config>/again.vst3
param(
    [ValidateSet('Release', 'Debug')] [string]$Config = 'Release',
    [string]$BuildDir = (Join-Path $PSScriptRoot '..\src\Mixer.PluginHost\build'),
    [switch]$NoAgain
)
$ErrorActionPreference = 'Stop'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
if (-not $cmake) {
    if (-not (Test-Path $vswhere)) { throw 'Не найден vswhere.exe: поставьте Visual Studio 2022 или Build Tools' }
    $vs = & $vswhere -products * -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw 'Не найдена Visual Studio с компонентом C++ x64' }
    $cmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
    if (-not (Test-Path $cmake)) { throw "Не найден CMake из Visual Studio: $cmake" }
}

$src = Resolve-Path (Join-Path $PSScriptRoot '..\src\Mixer.PluginHost')
$again = if ($NoAgain) { 'OFF' } else { 'ON' }

# cmake пишет прогресс в stderr: при Stop Windows PowerShell 5.1 считает это ошибкой, если вывод перенаправлен.
# Коды возврата проверяются явно.
$ErrorActionPreference = 'Continue'

# в SDK есть файлы примеров с путями длиннее MAX_PATH: длинные пути для git только в этом процессе
$env:GIT_CONFIG_COUNT = '1'
$env:GIT_CONFIG_KEY_0 = 'core.longpaths'
$env:GIT_CONFIG_VALUE_0 = 'true'

# генератор по умолчанию — самая новая Visual Studio на машине
& $cmake -S $src -B $BuildDir -A x64 "-DMIXER_BUILD_AGAIN=$again"
if ($LASTEXITCODE) { throw "cmake configure: код $LASTEXITCODE" }
& $cmake --build $BuildDir --config $Config --parallel -- /nologo /verbosity:minimal
if ($LASTEXITCODE) { throw "cmake build: код $LASTEXITCODE" }

$exe = Join-Path $BuildDir 'bin\Mixer.PluginHost.exe'
Write-Host "Готово: $exe"
