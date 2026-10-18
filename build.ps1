[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Position = 0)]
    [ValidateSet('Build', 'Test', 'Demo', 'Smoke', 'Bench', 'Capture')]
    [string]$Task = 'Test',
    [switch]$DebugBuild,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ExtraArguments
)
$ErrorActionPreference = 'Stop'
$Configuration = if ($DebugBuild) { 'Debug' } else { 'Release' }
$localSdk = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
$dotnetExe = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet).Source }
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.tools/packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Push-Location (Join-Path $PSScriptRoot 'engine')
try {
    & $dotnetExe build VoxelBrutal.slnx -c $Configuration --nologo -m:1
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
    switch ($Task) {
        'Test' { & $dotnetExe run --project VoxelBrutal.Tests -c $Configuration --no-build -- @ExtraArguments }
        'Bench' { & $dotnetExe run --project VoxelBrutal.Tests -c $Configuration --no-build -- --benchmark @ExtraArguments }
        'Demo' { & $dotnetExe run --project VoxelBrutal.Demo -c $Configuration --no-build -- @ExtraArguments }
        'Smoke' { & $dotnetExe run --project VoxelBrutal.Demo -c $Configuration --no-build -- --smoke @ExtraArguments }
        'Capture' {
            $previousLayers = $env:VK_INSTANCE_LAYERS
            $previousFrames = $env:VK_SCREENSHOT_FRAMES
            $previousDirectory = $env:VK_SCREENSHOT_DIR
            try {
                $captureDirectory = Join-Path $PSScriptRoot '.artifacts/screenshots'
                New-Item -ItemType Directory -Path $captureDirectory -Force | Out-Null
                $env:VK_INSTANCE_LAYERS = 'VK_LAYER_LUNARG_screenshot'
                $env:VK_SCREENSHOT_FRAMES = '200'
                $env:VK_SCREENSHOT_DIR = $captureDirectory
                & $dotnetExe run --project VoxelBrutal.Demo -c $Configuration --no-build -- --smoke @ExtraArguments
            } finally {
                $env:VK_INSTANCE_LAYERS = $previousLayers
                $env:VK_SCREENSHOT_FRAMES = $previousFrames
                $env:VK_SCREENSHOT_DIR = $previousDirectory
            }
        }
    }
    if ($LASTEXITCODE -ne 0) { throw "$Task failed ($LASTEXITCODE)" }
} finally { Pop-Location }
