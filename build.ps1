param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "src\WitchTrialsTheme"
$out  = Join-Path $root "dist\WitchTrialsTheme"

dotnet build $proj -c $Configuration -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败" }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null
New-Item -ItemType Directory -Force (Join-Path $out "assets") | Out-Null

$bin = Join-Path $proj "bin\$Configuration\netstandard2.0"
Copy-Item (Join-Path $bin "WitchTrialsTheme.dll") $out -Force
Copy-Item (Join-Path $proj "WitchTrialsTheme.json") $out -Force
Copy-Item (Join-Path $root "icon.png") $out -Force
Copy-Item (Join-Path $proj "assets\*.png") (Join-Path $out "assets") -Force

Write-Host "OK -> $out"
Get-ChildItem $out -Recurse -File | ForEach-Object {
    Write-Host ("  {0,-40} {1,10}" -f $_.FullName.Substring($out.Length + 1), $_.Length)
}
