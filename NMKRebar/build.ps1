param(
  [switch]$Full,
  [switch]$SkipCopy
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "NMKRebar.csproj"
$configs = if ($Full) {
  @("D2022", "D2026", "D2027", "R2022", "R2026", "R2027")
} else {
  @("D2022", "D2026", "D2027")
}

$extra = @()
if ($SkipCopy) {
  $extra += "-p:SkipCopyToRevitAddins=true"
}

foreach ($config in $configs) {
  Write-Host "=== Building $config ===" -ForegroundColor Cyan
  & dotnet build $project -c $config -v minimal @extra
  if ($LASTEXITCODE -ne 0) {
    throw "Build failed: $config"
  }
}

Write-Host "All versions built: $($configs -join ', ')" -ForegroundColor Green
