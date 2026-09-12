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

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK is not available in PATH."
}

$psql = Get-Command psql -ErrorAction SilentlyContinue
if (-not $psql) {
    $candidate = Get-ChildItem "C:\Program Files\PostgreSQL" -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName "bin\psql.exe" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
    if ($candidate) { $psql = Get-Item $candidate }
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

$env:ASPNETCORE_ENVIRONMENT = "Demo"
$env:ASPNETCORE_URLS = "https://0.0.0.0:$Port"
$env:ConnectionStrings__RequestPortal = "Host=$PostgresHost;Port=$PostgresPort;Database=$Database;Username=$PostgresUser;Password=$pgPassword;Include Error Detail=true"

$certCheck = dotnet dev-certs https --check 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "Creating the local HTTPS development certificate..."
    dotnet dev-certs https --trust
    if ($LASTEXITCODE -ne 0) { throw "Unable to create/trust the local HTTPS certificate." }
}

$project = Join-Path $repoRoot "RequestPortal\RequestPortal.Web\RequestPortal.Web.csproj"
Write-Host "Building demo from local sources only..."
dotnet build $project --configuration Debug --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Demo build failed. Because the demo is designed for offline use, the launcher does not download NuGet packages. Restore the repository once while packages are available, then rerun Start-Demo.ps1."
}

Write-Host ""
Write-Host "Request Portal Management Demo"
Write-Host "  Laptop URL : https://localhost:$Port"
Write-Host "  Staff user : FWright"
Write-Host "  Password   : FFPDemo2026!"

try {
    $addresses = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
        Where-Object {
            $_.IPAddress -ne "127.0.0.1" -and
            $_.IPAddress -notlike "169.254.*" -and
            $_.AddressState -eq "Preferred"
        } |
        Select-Object -ExpandProperty IPAddress -Unique
    foreach ($address in $addresses) {
        Write-Host "  LAN URL    : https://${address}:$Port"
    }
} catch {
    Write-Host "  LAN URL    : unavailable; localhost remains fully functional offline"
}

Write-Host ""
Write-Host "The browser may ask you to trust the local development certificate on first use."
Write-Host "Press Ctrl+C here when the demonstration is complete."
Write-Host ""

dotnet run --project $project --configuration Debug --no-build --no-restore
