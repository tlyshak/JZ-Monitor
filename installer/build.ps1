# Збирає інсталятор JZ Monitor: publish (Release, win-x64, без .NET усередині, один exe) → WiX → out\JzMonitor-<версія>-win-x64.msi
# Запуск з кореня репозиторію:  powershell -ExecutionPolicy Bypass -File installer\build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$proj = 'src\JzMonitor\JzMonitor.csproj'
$baseVersion = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $baseVersion) { throw "No <Version> in $proj" }
# Версія інсталятора: <major>.<minor> з csproj + кількість комітів — кожна нова збірка більша за попередню,
# тож інсталятор оновлює встановлену програму без ручної зміни версії (MSI порівнює перші три числа)
$parts = $baseVersion.Split('.')
$commits = (git rev-list --count HEAD).Trim()
$version = "$($parts[0]).$($parts[1]).$commits"

$publish = Join-Path $root 'out\publish'
$msi = Join-Path $root "out\JzMonitor-$version-win-x64.msi"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

# Окремі obj/bin (--artifacts-path), щоб не заважала відкрита програма чи Visual Studio
dotnet publish $proj -c Release -r win-x64 --self-contained false -p:Version=$version `
    -p:PublishSingleFile=true -p:DebugType=None -o $publish --artifacts-path (Join-Path $root 'out\build')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

dotnet tool restore | Out-Null
dotnet wix build installer\JzMonitor.wxs -arch x64 -o $msi `
    -d "Version=$version" -d "PublishDir=$publish" -d "IconPath=$(Join-Path $root 'src\JzMonitor\Assets\JzMonitor.ico')"
if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }

Write-Host "Installer: $msi ($([math]::Round((Get-Item $msi).Length / 1KB)) KB)"
