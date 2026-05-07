# Build the static site and publish all demo projects into the site's output folder.

$Category = "All"
$CustomHtmlFileName = "index"
$CopySourcesFromMetaJson = $true
$Clean = $true

function Get-ScriptRoot {
    if ($PSScriptRoot) {
        return $PSScriptRoot
    }

    return (Get-Location).Path
}

function Get-AbsolutePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BasePath,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $BasePath $Path
}

function Assert-CommandExists {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CommandName
    )

    if (-not (Get-Command $CommandName -ErrorAction SilentlyContinue)) {
        throw "Required command '$CommandName' was not found in PATH."
    }
}

function Invoke-ExternalCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $false)]
        [string[]]$Arguments = @(),

        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    & $FilePath @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage Exit code: $LASTEXITCODE"
    }
}

$ErrorActionPreference = "Stop"

try {
    $scriptDir = Get-ScriptRoot
    $siteDirectory = Get-AbsolutePath -BasePath $scriptDir -Path "Site"
    $siteOutputDirectory = Join-Path $siteDirectory "out"
    $publishScriptPath = Get-AbsolutePath -BasePath $scriptDir -Path "publish-all-projects-wasm.ps1"

    Assert-CommandExists -CommandName "dotnet"
    Assert-CommandExists -CommandName "npm"

    if (-not (Test-Path $siteDirectory)) {
        throw "Site directory was not found: $siteDirectory"
    }

    if (-not (Test-Path $publishScriptPath)) {
        throw "Publish script was not found: $publishScriptPath"
    }

    if ($Clean -and (Test-Path $siteOutputDirectory)) {
        Write-Host "Cleaning existing site output folder: $siteOutputDirectory" -ForegroundColor DarkYellow
        Remove-Item -Path $siteOutputDirectory -Recurse -Force
    }

    Write-Host "Starting site build and WebAssembly publish..." -ForegroundColor Cyan
    Write-Host "Category: $Category" -ForegroundColor Cyan
    Write-Host "Site directory: $siteDirectory" -ForegroundColor Cyan
    Write-Host "Site output: $siteOutputDirectory" -ForegroundColor Cyan
    if (-not [string]::IsNullOrWhiteSpace($CustomHtmlFileName)) {
        Write-Host "Custom HTML file name: $CustomHtmlFileName" -ForegroundColor Cyan
    }
    Write-Host "Copy sources from meta.json: $CopySourcesFromMetaJson" -ForegroundColor Cyan
    Write-Host ""

    Push-Location $siteDirectory
    try {
        Write-Host "========================================" -ForegroundColor Yellow
        Write-Host "Building site" -ForegroundColor Yellow
        Write-Host "========================================" -ForegroundColor Yellow
        Write-Host ""

        Invoke-ExternalCommand -FilePath "npm" -Arguments @("run", "build") -FailureMessage "Site build failed."
    }
    finally {
        Pop-Location
    }

    if (-not (Test-Path $siteOutputDirectory)) {
        throw "Site build did not produce the expected output folder: $siteOutputDirectory"
    }

    $publishScriptArguments = @{
        Category = $Category
        OutputRoot = $siteOutputDirectory
    }

    if (-not [string]::IsNullOrWhiteSpace($CustomHtmlFileName)) {
        $publishScriptArguments.CustomHtmlFileName = $CustomHtmlFileName
    }

    if ($CopySourcesFromMetaJson) {
        $publishScriptArguments.CopySourcesFromMetaJson = $true
    }

    Write-Host "" 
    Write-Host "========================================" -ForegroundColor Yellow
    Write-Host "Publishing WebAssembly demos into the built site" -ForegroundColor Yellow
    Write-Host "========================================" -ForegroundColor Yellow
    Write-Host ""

    & $publishScriptPath @publishScriptArguments

    if ($LASTEXITCODE -ne 0) {
        throw "WebAssembly publish failed. Exit code: $LASTEXITCODE"
    }

    Write-Host "" 
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "Site build and WebAssembly publish completed successfully!" -ForegroundColor Green
    Write-Host "Site output: $siteOutputDirectory" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
}
catch {
    Write-Host "" 
    Write-Host "========================================" -ForegroundColor Red
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "========================================" -ForegroundColor Red
    exit 1
}