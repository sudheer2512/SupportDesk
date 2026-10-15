<#
.SYNOPSIS
    Builds the web clients and publishes the SupportDesk API (with the desk + portal SPAs) and the worker host for IIS.

.DESCRIPTION
    Output layout (copy it to the server, then run Install-SupportDesk.ps1 there):
        <Output>\api     -> IIS site / application (web.config included)
        <Output>\worker  -> optional Windows service (SupportDesk Workers)

.EXAMPLE
    .\deploy\iis\Publish-SupportDesk.ps1 -Output D:\drops\supportdesk
#>
[CmdletBinding()]
param(
    [string] $Output = (Join-Path $PSScriptRoot '..\..\publish\iis'),
    [string] $Configuration = 'Release',
    # Skip "npm ci" when node_modules is already installed (CI caches, repeated publishes).
    [switch] $SkipNpmInstall,
    # Skip the Angular build when src/SupportDesk.Api/wwwroot already holds a current build.
    [switch] $SkipClientBuild
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Output = [System.IO.Path]::GetFullPath($Output)

function Invoke-Step([string] $title, [scriptblock] $action) {
    Write-Host "==> $title" -ForegroundColor Cyan
    & $action
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "$title failed (exit code $LASTEXITCODE)." }
}

if (-not $SkipClientBuild) {
    Push-Location (Join-Path $repo 'clients')
    try {
        if (-not $SkipNpmInstall) { Invoke-Step 'npm ci' { npm ci } }
        # Builds the desk into src/SupportDesk.Api/wwwroot and the portal into wwwroot/portal.
        Invoke-Step 'Build desk + portal' { npm run build }
    } finally { Pop-Location }
}

$index = Join-Path $repo 'src\SupportDesk.Api\wwwroot\index.html'
if (-not (Test-Path $index)) { throw "No SPA build found ($index). Run without -SkipClientBuild." }

Invoke-Step 'Publish API' { dotnet publish (Join-Path $repo 'src\SupportDesk.Api') -c $Configuration -o (Join-Path $Output 'api') --nologo }
Invoke-Step 'Publish worker host' { dotnet publish (Join-Path $repo 'src\SupportDesk.WorkerHost') -c $Configuration -o (Join-Path $Output 'worker') --nologo }

# Never ship development settings or local data to a server.
Get-ChildItem (Join-Path $Output 'api'), (Join-Path $Output 'worker') -Filter 'appsettings.Development.json' -ErrorAction SilentlyContinue | Remove-Item
foreach ($dir in 'data', 'logs') { Remove-Item (Join-Path $Output "api\$dir") -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ''
Write-Host "Published to $Output" -ForegroundColor Green
Write-Host "Next (on the IIS server, as administrator):"
Write-Host "  .\deploy\iis\Install-SupportDesk.ps1 -PublishPath $Output\api -ConnectionString '<sql>' -PublicUrl 'https://support.example.com'"
