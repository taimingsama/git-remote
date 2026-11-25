[CmdletBinding()]
param(
    [switch]$Live,
    [string]$DotNet = 'dotnet',
    [string]$Settings
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'GitRemote.OneDrive.Prototype.IntegrationTests/GitRemote.OneDrive.Prototype.IntegrationTests.csproj'
$filter = if ($Live) { 'Category=OneDrivePrototypeLive' } else { 'Category=PrototypeSystem' }
$settingsArguments = if ($Settings) { @('--settings', $Settings) } else { @() }

& $DotNet test $project --filter $filter @settingsArguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
