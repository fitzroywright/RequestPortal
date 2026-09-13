param(
    [switch]$SkipValidation
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repoRoot

$branch = (git branch --show-current).Trim()
if ($branch -ne "demo") {
    throw "Flattening must be proven on the 'demo' branch first. Current branch: '$branch'."
}

$dirty = git status --porcelain
if ($dirty) {
    throw "Working tree is not clean. Commit or discard local changes before flattening RequestPortal."
}

$projects = @(
    "FfpManager.Bridge",
    "RequestPortal.Application",
    "RequestPortal.Common",
    "RequestPortal.Domain",
    "RequestPortal.Infrastructure",
    "RequestPortal.Web"
)

foreach ($project in $projects) {
    $source = Join-Path "RequestPortal" $project
    if (-not (Test-Path $source)) {
        throw "Expected source folder '$source' was not found. The repository may already be flattened or has an unexpected layout."
    }
    if (Test-Path $project) {
        throw "Target folder '$project' already exists. Nothing was moved."
    }
}

Write-Host "Flattening RequestPortal project folders..."
foreach ($project in $projects) {
    git mv (Join-Path "RequestPortal" $project) $project
    if ($LASTEXITCODE -ne 0) { throw "git mv failed for $project" }
}

if ((Test-Path "RequestPortal") -and -not (Get-ChildItem "RequestPortal" -Force)) {
    Remove-Item "RequestPortal" -Force
}

function Replace-Text {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Old,
        [Parameter(Mandatory)] [string]$New
    )

    if (-not (Test-Path $Path)) { return }
    $content = Get-Content $Path -Raw
    if ($content.Contains($Old)) {
        $content = $content.Replace($Old, $New)
        Set-Content -Path $Path -Value $content -NoNewline -Encoding UTF8
    }
}

# Solution paths.
foreach ($project in $projects) {
    Replace-Text -Path "RequestPortal.sln" -Old "RequestPortal\\$project\\" -New "$project\\"
}

# Project-local fallbacks now sit one level closer to the workspace Common folder.
foreach ($projectFile in @(
    "RequestPortal.Common\RequestPortal.Common.csproj",
    "RequestPortal.Web\RequestPortal.Web.csproj"
)) {
    Replace-Text -Path $projectFile -Old "..\\..\\..\\Common" -New "..\\..\\Common"
}

# Web project's linked configuration file is one level closer after the move.
Replace-Text -Path "RequestPortal.Web\RequestPortal.Web.csproj" -Old "../../configuration/" -New "../configuration/"

# Test project references now point directly to root-level application projects.
$testProjects = Get-ChildItem "RequestPortal.Tests" -Filter *.csproj -Recurse -File
foreach ($testProject in $testProjects) {
    Replace-Text -Path $testProject.FullName -Old "../../RequestPortal/RequestPortal." -New "../../RequestPortal."
    Replace-Text -Path $testProject.FullName -Old "../../RequestPortal/FfpManager.Bridge/" -New "../../FfpManager.Bridge/"
}

# CI paths.
Replace-Text -Path ".github\workflows\ci.yml" -Old "RequestPortal/RequestPortal.Web/" -New "RequestPortal.Web/"
Replace-Text -Path ".github\workflows\ci.yml" -Old "RequestPortal/FfpManager.Bridge/" -New "FfpManager.Bridge/"
Replace-Text -Path ".github\workflows\ci.yml" -Old "RequestPortal/RequestPortal.Infrastructure" -New "RequestPortal.Infrastructure"

# Demo launcher paths.
Replace-Text -Path "Start-Demo.ps1" -Old "RequestPortal\\RequestPortal.Web\\RequestPortal.Web.csproj" -New "RequestPortal.Web\\RequestPortal.Web.csproj"
Replace-Text -Path "Start-Demo.ps1" -Old "RequestPortal\\RequestPortal.Web\\obj\\project.assets.json" -New "RequestPortal.Web\\obj\\project.assets.json"

# Documentation/path examples.
foreach ($doc in @("README.md", "DEMO.md")) {
    foreach ($project in $projects) {
        Replace-Text -Path $doc -Old "RequestPortal/$project/" -New "$project/"
        Replace-Text -Path $doc -Old "RequestPortal\\$project\\" -New "$project\\"
    }
}

Write-Host ""
Write-Host "New repository layout:"
Write-Host "  FfpManager.Bridge\"
Write-Host "  RequestPortal.Application\"
Write-Host "  RequestPortal.Common\"
Write-Host "  RequestPortal.Domain\"
Write-Host "  RequestPortal.Infrastructure\"
Write-Host "  RequestPortal.Web\"
Write-Host "  RequestPortal.Tests\"
Write-Host "  RequestPortal.sln"
Write-Host ""

if (-not $SkipValidation) {
    Write-Host "Restoring and validating the flattened solution..."
    dotnet restore .\RequestPortal.sln
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed after flattening." }

    dotnet build .\RequestPortal.sln --configuration Debug --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed after flattening." }

    dotnet test .\RequestPortal.sln --configuration Debug --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed after flattening." }
}

Write-Host ""
Write-Host "Flattening completed locally. Review 'git status' and 'git diff' before committing."
Write-Host "This script does not push, merge, or modify main."
