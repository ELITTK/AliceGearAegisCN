$ErrorActionPreference = 'Stop'
$gameDir = Split-Path -Parent $PSScriptRoot
$managed = Join-Path $gameDir 'alice_main_Data\Managed'
$core = Join-Path $gameDir 'BepInEx\core'
$outputDir = Join-Path $gameDir 'BepInEx\plugins\AliceFontGuard'
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$references = @(
    (Join-Path $core 'BepInEx.dll'),
    (Join-Path $core '0Harmony.dll'),
    (Join-Path $managed 'netstandard.dll'),
    (Join-Path $managed 'UnityEngine.dll'),
    (Join-Path $managed 'UnityEngine.CoreModule.dll'),
    (Join-Path $managed 'UnityEngine.TextRenderingModule.dll'),
    (Join-Path $managed 'UnityEngine.UIModule.dll'),
    (Join-Path $managed 'UnityEngine.UI.dll')
)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$arguments = @('/nologo', '/target:library', '/optimize+', ('/out:' + (Join-Path $outputDir 'AliceFontGuard.dll')))
$arguments += $references | ForEach-Object { '/reference:' + $_ }
$arguments += Join-Path $PSScriptRoot 'AliceFontGuard.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Font guard compilation failed.' }
Get-Item (Join-Path $outputDir 'AliceFontGuard.dll') | Select-Object FullName, Length, LastWriteTime