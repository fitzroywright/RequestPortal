param(
    [int]$Port = 5443,
    [string]$PostgresHost = "127.0.0.1",
    [int]$PostgresPort = 5432,
    [string]$PostgresUser = "postgres",
    [string]$Database = "requestportal_demo"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repoRoot

$branch = (git branch --show-current).Trim()
if ($branch -ne "demo") {
    throw "Request Portal management demo must run from the 'demo' branch. Current branch: '$branch'."
}
if ($Database -notmatch '^[A-Za-z0-9_]+$') { throw "Demo database name contains invalid characters." }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK is not available in PATH."
}
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "Git is not available in PATH."
}

$commonRoot = Join-Path (Split-Path $repoRoot -Parent) "Common"
$commonRepositories = @(
    @{ Name = "Common.Diagnostics"; Url = "https://github.com/fitzroywright/Common.Diagnostics.git" },
    @{ Name = "Common.Security"; Url = "https://github.com/fitzroywright/Common.Security.git" },
    @{ Name = "Common.Secrets"; Url = "https://github.com/fitzroywright/Common.Secrets.git" },
    @{ Name = "Common.Messaging"; Url = "https://github.com/fitzroywright/Common.Messaging.git" },
    @{ Name = "Common.Storage"; Url = "https://github.com/fitzroywright/Common.Storage.git" }
)

$missingCommon = @($commonRepositories | Where-Object { -not (Test-Path (Join-Path $commonRoot $_.Name)) })
if ($missingCommon.Count -gt 0) {
    Write-Host "This clone is missing sibling Common.* source repositories required by Request Portal."
    Write-Host "Bootstrapping them once. Internet access is required only for this initial clone/restore step."
    New-Item -ItemType Directory -Path $commonRoot -Force | Out-Null

    foreach ($repo in $missingCommon) {
        $target = Join-Path $commonRoot $repo.Name
        Write-Host "  Cloning $($repo.Name)..."
        git clone --depth 1 $repo.Url $target
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to clone $($repo.Name). Restore Internet/GitHub access, remove any incomplete folder at '$target', and rerun Start-Demo.ps1."
        }
    }
}

foreach ($repo in $commonRepositories) {
    $projectFile = Join-Path (Join-Path $commonRoot $repo.Name) "$($repo.Name).csproj"
    if (-not (Test-Path $projectFile)) {
        throw "Required Common project is missing: $projectFile"
    }
}

$psql = Get-Command psql -ErrorAction SilentlyContinue
if (-not $psql) {
    $candidate = Get-ChildItem "C:\Program Files\PostgreSQL" -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName "bin\psql.exe" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
    if ($candidate) { $psql = Get-Command $candidate }
}
if (-not $psql) {
    throw "PostgreSQL psql.exe was not found. Install local PostgreSQL or add its bin folder to PATH."
}

$pgPassword = $env:REQUESTPORTAL_DEMO_PG_PASSWORD
if ([string]::IsNullOrWhiteSpace($pgPassword)) {
    $secure = Read-Host "Local PostgreSQL password for '$PostgresUser'" -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $pgPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}
if ([string]::IsNullOrWhiteSpace($pgPassword)) { throw "A local PostgreSQL password is required." }

$env:PGPASSWORD = $pgPassword
$psqlPath = $psql.Source

$ready = & $psqlPath -h $PostgresHost -p $PostgresPort -U $PostgresUser -d postgres -tAc "SELECT 1" 2>$null
if (($ready | Out-String).Trim() -ne "1") {
    throw "Local PostgreSQL is not reachable at ${PostgresHost}:${PostgresPort}. Start PostgreSQL and run this script again."
}

$exists = & $psqlPath -h $PostgresHost -p $PostgresPort -U $PostgresUser -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='$Database'"
if (($exists | Out-String).Trim() -ne "1") {
    Write-Host "Creating local demo database '$Database'..."
    & $psqlPath -h $PostgresHost -p $PostgresPort -U $PostgresUser -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE $Database;"
}

# Demo mode must be controlled by this launcher, not by Visual Studio/launchSettings.json.
$env:ASPNETCORE_ENVIRONMENT = "Demo"
$env:ASPNETCORE_URLS = "https://0.0.0.0:$Port"
$env:ConnectionStrings__RequestPortal = "Host=$PostgresHost;Port=$PostgresPort;Database=$Database;Username=$PostgresUser;Password=$pgPassword;Include Error Detail=true"

$null = dotnet dev-certs https --check 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "Creating the local HTTPS development certificate..."
    dotnet dev-certs https --trust
    if ($LASTEXITCODE -ne 0) { throw "Unable to create/trust the local HTTPS certificate." }
}

$project = Join-Path $repoRoot "RequestPortal.Web\RequestPortal.Web.csproj"
$assets = Join-Path $repoRoot "RequestPortal.Web\obj\project.assets.json"
$mapAsset = Join-Path $repoRoot "RequestPortal.Web\wwwroot\img\maps\jamaica-parishes.svg"
$mapSource = "https://upload.wikimedia.org/wikipedia/commons/2/26/Jamaica%2C_administrative_divisions_%28parishes%29_-_en_-_monochrome.svg"

if (-not (Test-Path $mapAsset)) {
    $mapDirectory = Split-Path -Parent $mapAsset
    New-Item -ItemType Directory -Path $mapDirectory -Force | Out-Null
    Write-Host "Caching Jamaica parish map for offline demo..."
    try {
        Invoke-WebRequest -Uri $mapSource -OutFile $mapAsset -UseBasicParsing
        if ((Get-Item $mapAsset).Length -lt 1000) {
            Remove-Item $mapAsset -Force
            throw "Downloaded map file was unexpectedly small."
        }
        Write-Host "Jamaica parish map cached locally."
    }
    catch {
        throw "The Jamaica parish map is not cached and could not be downloaded. Connect to the Internet once, rerun Start-Demo.ps1, and subsequent demo runs will work offline. Details: $($_.Exception.Message)"
    }
}

if (-not (Test-Path $assets)) {
    Write-Host "This is the first run from this clone. Restoring NuGet packages once..."
    dotnet restore $project
    if ($LASTEXITCODE -ne 0) {
        throw "Initial NuGet restore failed. Internet/package-source access is needed only for this first restore. After it succeeds, future demo runs can operate offline."
    }
}

Write-Host "Building demo from local sources..."
dotnet build $project --configuration Debug --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Demo build failed. Fix the build errors shown above before starting the management demo."
}

Write-Host ""
Write-Host "Request Portal Management Demo"
Write-Host "  Laptop URL       : https://localhost:$Port"
Write-Host "  FFP staff user   : FWright"
Write-Host "  FFP staff pass   : FFPDemo2026!"
Write-Host "  Representative   : representative@demo.local"
Write-Host "  Representative pw: DemoUser2026!"
Write-Host "  Org admin         : orgadmin@demo.local"
Write-Host "  Org admin password: DemoUser2026!"

try {
    $addresses = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
        Where-Object {
            $_.IPAddress -ne "127.0.0.1" -and
            $_.IPAddress -notlike "169.254.*" -and
            $_.AddressState -eq "Preferred"
        } |
        Select-Object -ExpandProperty IPAddress -Unique
    foreach ($address in $addresses) {
        Write-Host "  LAN URL          : https://${address}:$Port"
    }
} catch {
    Write-Host "  LAN URL          : unavailable; localhost remains fully functional offline"
}

Write-Host ""
Write-Host "If Windows Firewall prompts, allow Private network access for LAN viewing."
Write-Host "A LAN browser may warn about the laptop development certificate; localhost remains trusted on this laptop."
Write-Host "After the first successful bootstrap/restore, Wi-Fi/Internet may disappear and https://localhost:$Port continues to work."
Write-Host "Press Ctrl+C here when the demonstration is complete."
Write-Host ""

# --no-launch-profile is intentional: launchSettings.json would otherwise override
# ASPNETCORE_ENVIRONMENT and ASPNETCORE_URLS, producing the wrong demo port/environment.
dotnet run --project $project --configuration Debug --no-build --no-restore --no-launch-profile
