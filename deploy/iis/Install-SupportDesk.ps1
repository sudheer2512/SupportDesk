#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs or updates SupportDesk on IIS - as its own site or as an application (virtual directory) of an existing
    site - and optionally the "SupportDesk Workers" Windows service.

.DESCRIPTION
    Prerequisites on the server: IIS with "IIS Management Scripts and Tools", the ASP.NET Core 10 Hosting Bundle (installed
    after IIS), and preferably the "WebSocket Protocol" and "Application Initialization" features. The database must be a
    SQL Server instance the app pool identity can reach (LocalDB does not work under IIS).

    The script is idempotent: run it again with a new publish folder to upgrade. It
      1. checks the prerequisites;
      2. takes the app offline (app_offline.htm) and mirrors the published files (keeping appsettings.Production.json and logs);
      3. on the first install writes appsettings.Production.json (connection string, generated master key, data path,
         public URL), readable only by administrators, SYSTEM and the app pool;
      4. creates/updates a "No Managed Code" app pool that is always running (the app hosts background workers) and
         recycles at 03:00;
      5. creates/updates the site or application with preload enabled and grants folder permissions;
      6. stops IIS from logging /hubs requests (SignalR passes the short-lived access token in the query string) and
         removes the WebDAV module for the app when WebDAV is installed (both sections are locked for web.config);
      7. optionally installs the worker host as a Windows service and turns the in-app jobs/recurring tasks off;
      8. starts the app and calls /health/ready.

.EXAMPLE
    # Own site with a host name (add the HTTPS binding and certificate in IIS Manager afterwards)
    .\Install-SupportDesk.ps1 -PublishPath D:\drops\supportdesk\api -HostName support.example.com `
        -ConnectionString 'Server=sql01;Database=SupportDesk;Integrated Security=True;TrustServerCertificate=True' `
        -PublicUrl 'https://support.example.com'

.EXAMPLE
    # Application under the Default Web Site: https://server.example.com/supportdesk
    .\Install-SupportDesk.ps1 -PublishPath D:\drops\supportdesk\api -ParentSite 'Default Web Site' -ApplicationPath /supportdesk `
        -ConnectionString '...' -PublicUrl 'https://server.example.com/supportdesk' -WorkerPublishPath D:\drops\supportdesk\worker
#>
[CmdletBinding(DefaultParameterSetName = 'Site')]
param(
    [Parameter(Mandatory = $true)] [string] $PublishPath,
    # Public address of the app including any virtual directory, used in e-mail links (no trailing slash).
    [Parameter(Mandatory = $true)] [string] $PublicUrl,
    # Required on the first install (stored in appsettings.Production.json).
    [string] $ConnectionString,
    # Base64 32-byte key for secret encryption. Generated on the first install when empty - back it up!
    [string] $MasterKey,
    [string] $InstallPath = 'C:\inetpub\supportdesk',
    [string] $DataPath = (Join-Path $env:ProgramData 'SupportDesk'),
    [string] $AppPoolName = 'SupportDesk',

    [Parameter(ParameterSetName = 'Site')] [string] $SiteName = 'SupportDesk',
    [Parameter(ParameterSetName = 'Site')] [string] $HostName = '',
    [Parameter(ParameterSetName = 'Site')] [int] $Port = 80,

    [Parameter(ParameterSetName = 'Application', Mandatory = $true)] [string] $ParentSite,
    [Parameter(ParameterSetName = 'Application')] [string] $ApplicationPath = '/supportdesk',

    # Publish folder of SupportDesk.WorkerHost: installs/updates the "SupportDeskWorkers" Windows service.
    [string] $WorkerPublishPath,
    [string] $WorkerInstallPath = (Join-Path $env:ProgramFiles 'SupportDesk\Workers')
)

$ErrorActionPreference = 'Stop'
$inetsrv = Join-Path $env:windir 'System32\inetsrv'
$poolIdentity = "IIS AppPool\$AppPoolName"
$adminsSid = '*S-1-5-32-544'
$systemSid = '*S-1-5-18'

function Write-Step([string] $text) { Write-Host "==> $text" -ForegroundColor Cyan }

function Invoke-Robocopy([string] $from, [string] $to, [string[]] $extra) {
    $arguments = @($from, $to, '/MIR', '/R:2', '/W:2', '/NFL', '/NDL', '/NJH', '/NJS', '/NP') + $extra
    & robocopy @arguments | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy $from -> $to failed (exit code $LASTEXITCODE)." }
    $global:LASTEXITCODE = 0
}

function Grant-Access([string] $path, [string] $identity, [string] $rights) {
    & icacls $path /grant "${identity}:$rights" /T /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed for $identity on $path." }
}

function Protect-File([string] $file, [string[]] $readers) {
    # Settings files hold the connection string and master key: no inheritance; admins/SYSTEM full, the app read-only.
    $grants = @("${adminsSid}:F", "${systemSid}:F") + ($readers | ForEach-Object { "${_}:R" })
    & icacls $file /inheritance:r /grant:r @grants /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed to protect $file." }
}

# ---------------------------------------------------------------- 1. Prerequisites
Write-Step 'Checking prerequisites'
if (-not (Get-Service W3SVC -ErrorAction SilentlyContinue)) { throw 'IIS (W3SVC) is not installed.' }
Import-Module WebAdministration -ErrorAction Stop   # feature "IIS Management Scripts and Tools" (Web-Scripting-Tools)
if (-not (Test-Path (Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'))) {
    throw 'The ASP.NET Core Module V2 is missing. Install the ASP.NET Core 10 Hosting Bundle (after IIS) and run "iisreset".'
}
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path $dotnet) -or -not ((& $dotnet --list-runtimes) -match '^Microsoft\.AspNetCore\.App 10\.')) {
    throw 'The ASP.NET Core 10 runtime is missing. Install the ASP.NET Core 10 Hosting Bundle.'
}
if (-not (Test-Path (Join-Path $inetsrv 'iiswsock.dll'))) {
    Write-Warning 'IIS "WebSocket Protocol" is not installed: realtime updates fall back to long polling (Install-WindowsFeature Web-WebSockets).'
}
$hasAppInit = Test-Path (Join-Path $inetsrv 'warmup.dll')
if (-not $hasAppInit) {
    Write-Warning 'IIS "Application Initialization" is not installed: after a recycle the app and its background workers start on the first request (Install-WindowsFeature Web-AppInit).'
}
$PublishPath = (Resolve-Path $PublishPath).Path
if (-not (Test-Path (Join-Path $PublishPath 'SupportDesk.Api.dll')) -or -not (Test-Path (Join-Path $PublishPath 'web.config'))) {
    throw "$PublishPath is not a SupportDesk API publish folder (SupportDesk.Api.dll and web.config expected)."
}
if (-not (Test-Path (Join-Path $PublishPath 'wwwroot\index.html'))) { Write-Warning 'The publish folder has no SPA build (wwwroot\index.html): only the API will be served.' }

# ---------------------------------------------------------------- 2. Files
Write-Step "Copying files to $InstallPath"
New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
$offline = Join-Path $InstallPath 'app_offline.htm'
if (Test-Path (Join-Path $InstallPath 'SupportDesk.Api.dll')) {
    # The ASP.NET Core Module stops the app and serves this page while the files are replaced.
    Set-Content -Path $offline -Encoding UTF8 -Value '<!doctype html><title>Updating</title><p style="font-family:sans-serif">SupportDesk is being updated. Please try again in a minute.</p>'
    Start-Sleep -Seconds 5
}
Invoke-Robocopy $PublishPath $InstallPath @('/XF', 'appsettings.Production.json', 'app_offline.htm', '/XD', 'logs', 'data')

# ---------------------------------------------------------------- 3. Settings
$settingsFile = Join-Path $InstallPath 'appsettings.Production.json'
$generatedKey = $false
if (-not (Test-Path $settingsFile)) {
    Write-Step 'Writing appsettings.Production.json'
    if (-not $ConnectionString) { throw '-ConnectionString is required on the first install.' }
    if (-not $MasterKey) {
        $bytes = New-Object byte[] 32
        [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $MasterKey = [Convert]::ToBase64String($bytes)
        $generatedKey = $true
    }
    $settings = [ordered]@{
        ConnectionStrings = [ordered]@{ SupportDesk = $ConnectionString }
        Security          = [ordered]@{ MasterKey = $MasterKey }
        Storage           = [ordered]@{ DataPath = $DataPath }
        App               = [ordered]@{ PublicUrl = $PublicUrl.TrimEnd('/') }
        Seed              = [ordered]@{ Demo = $false }
    }
    $settings | ConvertTo-Json -Depth 5 | Set-Content -Path $settingsFile -Encoding UTF8
} else {
    Write-Step 'Keeping the existing appsettings.Production.json'
}

# ---------------------------------------------------------------- 4. Application pool
Write-Step "Configuring application pool '$AppPoolName'"
$poolPath = "IIS:\AppPools\$AppPoolName"
if (-not (Test-Path $poolPath)) { New-WebAppPool -Name $AppPoolName | Out-Null }
Set-ItemProperty $poolPath -Name managedRuntimeVersion -Value ''            # "No Managed Code": the ASP.NET Core Module hosts .NET
Set-ItemProperty $poolPath -Name managedPipelineMode -Value 'Integrated'
Set-ItemProperty $poolPath -Name startMode -Value 'AlwaysRunning'           # background workers run in the app
Set-ItemProperty $poolPath -Name processModel.identityType -Value 'ApplicationPoolIdentity'
Set-ItemProperty $poolPath -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
# Recycle once a night instead of every 29 hours (agents' realtime connections reconnect on their own).
Set-ItemProperty $poolPath -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
Clear-ItemProperty $poolPath -Name recycling.periodicRestart.schedule
New-ItemProperty $poolPath -Name recycling.periodicRestart.schedule -Value @{ value = '03:00:00' } | Out-Null

# ---------------------------------------------------------------- 5. Site or application
if ($PSCmdlet.ParameterSetName -eq 'Site') {
    Write-Step "Configuring site '$SiteName'"
    $sitePath = "IIS:\Sites\$SiteName"
    if (-not (Test-Path $sitePath)) {
        $nextId = 1 + [int](@(Get-Website | ForEach-Object { $_.Id }) + 0 | Measure-Object -Maximum).Maximum
        New-Website -Name $SiteName -Id $nextId -PhysicalPath $InstallPath -ApplicationPool $AppPoolName -Port $Port -HostHeader $HostName | Out-Null
    } else {
        Set-ItemProperty $sitePath -Name physicalPath -Value $InstallPath
        Set-ItemProperty $sitePath -Name applicationPool -Value $AppPoolName
    }
    if ($hasAppInit) { Set-ItemProperty $sitePath -Name applicationDefaults.preloadEnabled -Value $true }
    $appLocation = $SiteName
} else {
    $name = $ApplicationPath.Trim('/')
    Write-Step "Configuring application '/$name' under '$ParentSite'"
    if (-not (Test-Path "IIS:\Sites\$ParentSite")) { throw "Site '$ParentSite' was not found." }
    $appPath = "IIS:\Sites\$ParentSite\$name"
    if (-not (Get-WebApplication -Site $ParentSite -Name $name)) {
        New-WebApplication -Site $ParentSite -Name $name -PhysicalPath $InstallPath -ApplicationPool $AppPoolName | Out-Null
    } else {
        Set-ItemProperty $appPath -Name physicalPath -Value $InstallPath
        Set-ItemProperty $appPath -Name applicationPool -Value $AppPoolName
    }
    if ($hasAppInit) { Set-ItemProperty $appPath -Name preloadEnabled -Value $true }
    $appLocation = "$ParentSite/$name"
}

Write-Step 'Granting folder permissions'
New-Item -ItemType Directory -Force -Path (Join-Path $InstallPath 'logs'), $DataPath | Out-Null
Grant-Access $InstallPath $poolIdentity '(OI)(CI)RX'
Grant-Access (Join-Path $InstallPath 'logs') $poolIdentity '(OI)(CI)M'   # stdout logs when enabled in web.config
Grant-Access $DataPath $poolIdentity '(OI)(CI)M'                         # files, signing key, mail drops
Protect-File $settingsFile @($poolIdentity)

# ---------------------------------------------------------------- 6. Server-level settings
# These sections are locked for web.config by default (using them there fails every request with 500.19), so they are
# set for the app in applicationHost.config.
# SignalR WebSocket/long-polling URLs carry the access token in the query string: keep them out of the IIS logs.
Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location "$appLocation/hubs" -Filter 'system.webServer/httpLogging' -Name dontLog -Value $true
# WebDAV (when installed) answers PUT/DELETE with 405: web.config removes its handler, the module is removed here.
if (Get-WebGlobalModule -Name WebDAVModule -ErrorAction SilentlyContinue) {
    $webDav = Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $appLocation -Filter "system.webServer/modules/add[@name='WebDAVModule']" -Name name
    if ($webDav) {
        Remove-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $appLocation -Filter 'system.webServer/modules' -Name '.' -AtElement @{ name = 'WebDAVModule' }
    }
}

# ---------------------------------------------------------------- 7. Worker service (optional)
if ($WorkerPublishPath) {
    $serviceName = 'SupportDeskWorkers'
    $serviceAccount = "NT SERVICE\$serviceName"
    Write-Step "Installing the '$serviceName' Windows service"
    $service = Get-Service $serviceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') { Stop-Service $serviceName -Force }
    New-Item -ItemType Directory -Force -Path $WorkerInstallPath | Out-Null
    Invoke-Robocopy (Resolve-Path $WorkerPublishPath).Path $WorkerInstallPath @('/XF', 'appsettings.Production.json')
    # Same database, master key and data path as the web app - minus the web app's "Workers" switches (set below).
    $workerSettings = Join-Path $WorkerInstallPath 'appsettings.Production.json'
    $json = Get-Content $settingsFile -Raw | ConvertFrom-Json
    $json.PSObject.Properties.Remove('Workers')
    $json | ConvertTo-Json -Depth 10 | Set-Content -Path $workerSettings -Encoding UTF8
    if (-not $service) {
        $exe = Join-Path $WorkerInstallPath 'SupportDesk.WorkerHost.exe'
        New-Service -Name $serviceName -DisplayName 'SupportDesk Workers' -BinaryPathName "`"$exe`"" -StartupType Automatic `
            -Description 'SupportDesk background jobs and recurring tasks (SLA monitor, mailbox polling, notifications, analytics, retention).' | Out-Null
        & sc.exe config $serviceName obj= $serviceAccount | Out-Null     # virtual service account (no password)
    }
    & sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
    Grant-Access $DataPath $serviceAccount '(OI)(CI)M'
    Grant-Access $WorkerInstallPath $serviceAccount '(OI)(CI)RX'
    Protect-File $workerSettings @($serviceAccount)

    # The service runs jobs and recurring tasks; the web app keeps the outbox dispatcher (realtime pushes start there).
    $json = Get-Content $settingsFile -Raw | ConvertFrom-Json
    $json | Add-Member -NotePropertyName Workers -NotePropertyValue ([pscustomobject]@{ Jobs = $false; Recurring = $false }) -Force
    $json | ConvertTo-Json -Depth 10 | Set-Content -Path $settingsFile -Encoding UTF8
    Protect-File $settingsFile @($poolIdentity)
    Start-Service $serviceName
}

# ---------------------------------------------------------------- 8. Start
Write-Step 'Starting'
Remove-Item $offline -ErrorAction SilentlyContinue
if ((Get-WebAppPoolState -Name $AppPoolName).Value -eq 'Started') { Restart-WebAppPool -Name $AppPoolName } else { Start-WebAppPool -Name $AppPoolName }
if ($PSCmdlet.ParameterSetName -eq 'Site' -and (Get-WebsiteState -Name $SiteName).Value -ne 'Started') { Start-Website -Name $SiteName }

$health = "$($PublicUrl.TrimEnd('/'))/health/ready"
try {
    $response = Invoke-WebRequest -Uri $health -UseBasicParsing -TimeoutSec 300
    Write-Host "Health check $health -> $($response.StatusCode)" -ForegroundColor Green
} catch {
    Write-Warning "Health check $health failed: $($_.Exception.Message). Check DNS/bindings, the database login below, and the Application event log (or set stdoutLogEnabled=""true"" in web.config)."
}

Write-Host ''
Write-Host 'SupportDesk is installed.' -ForegroundColor Green
Write-Host "  App folder : $InstallPath"
Write-Host "  Data folder: $DataPath"
Write-Host "  Settings   : $settingsFile"
Write-Host ''
Write-Host 'If the connection string uses Windows authentication, give the app pool (and the worker service) a database login:'
Write-Host "  CREATE LOGIN [IIS APPPOOL\$AppPoolName] FROM WINDOWS;  -- remote SQL Server: use the computer account DOMAIN\SERVER`$"
Write-Host "  USE [SupportDesk]; CREATE USER [IIS APPPOOL\$AppPoolName] FOR LOGIN [IIS APPPOOL\$AppPoolName]; ALTER ROLE db_owner ADD MEMBER [IIS APPPOOL\$AppPoolName];"
if ($WorkerPublishPath) { Write-Host '  (same for [NT SERVICE\SupportDeskWorkers])' }
Write-Host 'Add an HTTPS binding with a certificate in IIS Manager; HSTS is sent automatically outside Development.'
if ($generatedKey) {
    Write-Host ''
    Write-Warning "A new master key was generated and stored in $settingsFile. Back it up in a password vault: without it, stored channel and integration secrets cannot be decrypted."
}
