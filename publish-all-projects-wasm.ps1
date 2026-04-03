# Publish demo projects to WebAssembly and collect outputs in a shared folder.

param(
    [Parameter(Mandatory = $false)]
    [ValidateSet("All", "BasicGraphics", "GPGPUDemos", "GraphicsTechniques", "WebGPUFeatures")]
    [string]$Category = "All",

    [Parameter(Mandatory = $false)]
    [string]$OutputRoot = "wasm-publish",

    [Parameter(Mandatory = $false)]
    [switch]$Clean
)

$basicGraphicsProjects = @(
    "BasicGraphics\HelloTriangle\HelloTriangle.csproj",
    "BasicGraphics\HelloTriangleMSAA\HelloTriangleMSAA.csproj",
    "BasicGraphics\RotatingCube\RotatingCube.csproj",
    "BasicGraphics\TwoCubes\TwoCubes.csproj",
    "BasicGraphics\TexturedCube\TexturedCube.csproj",
    "BasicGraphics\InstancedCube\InstancedCube.csproj",
    "BasicGraphics\FractalCube\FractalCube.csproj",
    "BasicGraphics\Cubemap\Cubemap.csproj"
)

$webGPUFeaturesProjects = @(
    "WebGPUFeatures\ReversedZ\ReversedZ.csproj",
    "WebGPUFeatures\RenderBundles\RenderBundles.csproj",
    "WebGPUFeatures\OcclusionQuery\OcclusionQuery.csproj",
    "WebGPUFeatures\SamplerParameters\SamplerParameters.csproj",
    "WebGPUFeatures\TimestampQuery\TimestampQuery.csproj"
)

$gpgpuDemosProjects = @(
    "GPGPUDemos\ComputeBoids\ComputeBoids.csproj",
    "GPGPUDemos\ConwaysGameOfLife\ConwaysGameOfLife.csproj",
    "GPGPUDemos\BitonicSort\BitonicSort.csproj"
)

$graphicsTechniquesProjects = @(
    "GraphicsTechniques\Cameras\Cameras.csproj",
    "GraphicsTechniques\NormalMap\NormalMap.csproj",
    "GraphicsTechniques\ShadowMapping\ShadowMapping.csproj",
    "GraphicsTechniques\DeferredRendering\DeferredRendering.csproj",
    "GraphicsTechniques\Particles\Particles.csproj",
    "GraphicsTechniques\Points\Points.csproj",
    "GraphicsTechniques\PrimitivePicking\PrimitivePicking.csproj",
    "GraphicsTechniques\ImageBlur\ImageBlur.csproj",
    "GraphicsTechniques\Cornell\Cornell.csproj",
    "GraphicsTechniques\ABuffer\ABuffer.csproj",
    "GraphicsTechniques\SkinnedMesh\SkinnedMesh.csproj",
    "GraphicsTechniques\StencilMask\StencilMask.csproj",
    "GraphicsTechniques\TextRenderingMsdf\TextRenderingMsdf.csproj",
    "GraphicsTechniques\VolumeRenderingTexture3D\VolumeRenderingTexture3D.csproj",
    "GraphicsTechniques\Wireframe\Wireframe.csproj"
)

function Get-ScriptRoot {
    if ($PSScriptRoot) {
        return $PSScriptRoot
    }

    return (Get-Location).Path
}

function Get-SelectedProjects {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SelectedCategory
    )

    switch ($SelectedCategory) {
        "BasicGraphics" { return $basicGraphicsProjects }
        "GPGPUDemos" { return $gpgpuDemosProjects }
        "GraphicsTechniques" { return $graphicsTechniquesProjects }
        "WebGPUFeatures" { return $webGPUFeaturesProjects }
        "All" { return $basicGraphicsProjects + $webGPUFeaturesProjects + $gpgpuDemosProjects + $graphicsTechniquesProjects }
        default { throw "Unknown category: $SelectedCategory" }
    }
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

function Convert-ToPlatformPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return $Path -replace '[\\/]', [System.IO.Path]::DirectorySeparatorChar
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

function Assert-WasmWorkloadInstalled {
    $workloadOutput = dotnet workload list 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to inspect installed dotnet workloads. Output:`n$workloadOutput"
    }

    if ($workloadOutput -notmatch '(?im)^\s*(wasm-tools|wasm-experimental|emscripten)\b') {
        throw "No WebAssembly workload was detected. Install the required workload first, for example: dotnet workload install wasm-tools"
    }
}

function Get-ProjectGroupName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectRelativePath
    )

    return ($ProjectRelativePath -split '[\\/]')[0]
}

function Get-PublishDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectDirectory
    )

    $binDirectory = Join-Path $ProjectDirectory "bin"
    if (-not (Test-Path $binDirectory)) {
        return $null
    }

    return Get-ChildItem -Path $binDirectory -Directory -Recurse -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -eq "publish" -and $_.FullName -match '[\\/]browser-wasm[\\/]publish$'
        } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

function Get-PublishContentDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishDirectory,

        [Parameter(Mandatory = $true)]
        [string]$ProjectName
    )

    $childDirectories = @(Get-ChildItem -Path $PublishDirectory -Directory -Force -ErrorAction SilentlyContinue)
    $childFiles = @(Get-ChildItem -Path $PublishDirectory -File -Force -ErrorAction SilentlyContinue)

    if ($childDirectories.Count -eq 1 -and $childFiles.Count -eq 0 -and $childDirectories[0].Name -eq $ProjectName) {
        return $childDirectories[0].FullName
    }

    return $PublishDirectory
}

function Assert-PublishArtifacts {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishDirectory
    )

    $wasmFiles = Get-ChildItem -Path $PublishDirectory -Filter *.wasm -File -Recurse -ErrorAction SilentlyContinue
    if (-not $wasmFiles) {
        throw "Published output is missing a .wasm artifact: $PublishDirectory"
    }

    $browserBootstrap = Get-ChildItem -Path $PublishDirectory -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in @('.html', '.js') } |
        Select-Object -First 1

    if (-not $browserBootstrap) {
        throw "Published output is missing browser bootstrap files (.html or .js): $PublishDirectory"
    }
}

$ErrorActionPreference = "Stop"

try {
    Assert-CommandExists -CommandName "dotnet"
    Assert-WasmWorkloadInstalled

    $scriptDir = Get-ScriptRoot
    $projects = Get-SelectedProjects -SelectedCategory $Category
    $resolvedOutputRoot = Get-AbsolutePath -BasePath $scriptDir -Path $OutputRoot

    if ($Clean -and (Test-Path $resolvedOutputRoot)) {
        Write-Host "Cleaning existing output folder: $resolvedOutputRoot" -ForegroundColor DarkYellow
        Remove-Item -Path $resolvedOutputRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Path $resolvedOutputRoot -Force | Out-Null

    Write-Host "Starting WebAssembly publish..." -ForegroundColor Cyan
    Write-Host "Category: $Category" -ForegroundColor Cyan
    Write-Host "Total projects: $($projects.Count)" -ForegroundColor Cyan
    Write-Host "Output root: $resolvedOutputRoot" -ForegroundColor Cyan
    Write-Host ""

    $successCount = 0
    $publishResults = @()

    foreach ($project in $projects) {
        $normalizedProjectPath = Convert-ToPlatformPath -Path $project
        $projectPath = Join-Path $scriptDir $normalizedProjectPath
        $projectName = [System.IO.Path]::GetFileNameWithoutExtension($normalizedProjectPath)
        $projectDirectory = Split-Path $projectPath -Parent
        $projectGroup = Get-ProjectGroupName -ProjectRelativePath $project
        $destinationDirectory = Join-Path (Join-Path $resolvedOutputRoot $projectGroup) $projectName

        if (-not (Test-Path $projectPath)) {
            throw "Project file was not found: $projectPath"
        }

        Write-Host "========================================" -ForegroundColor Yellow
        Write-Host "Publishing: $projectName" -ForegroundColor Yellow
        Write-Host "Project: $project" -ForegroundColor Gray
        Write-Host "Destination: $destinationDirectory" -ForegroundColor Gray
        Write-Host "========================================" -ForegroundColor Yellow
        Write-Host ""

        dotnet publish $projectPath -r browser-wasm -c Release

        if ($LASTEXITCODE -ne 0) {
            Write-Host "" 
            Write-Host "========================================" -ForegroundColor Red
            Write-Host "ERROR: Publish failed!" -ForegroundColor Red
            Write-Host "Failed project: $projectName" -ForegroundColor Red
            Write-Host "Project path: $project" -ForegroundColor Red
            Write-Host "Exit code: $LASTEXITCODE" -ForegroundColor Red
            Write-Host "========================================" -ForegroundColor Red
            Write-Host ""
            Write-Host "Successfully published: $successCount/$($projects.Count) projects" -ForegroundColor Yellow
            exit $LASTEXITCODE
        }

        $publishDirectory = Get-PublishDirectory -ProjectDirectory $projectDirectory
        if (-not $publishDirectory) {
            throw "Could not locate browser-wasm publish output for project: $projectName"
        }

        $publishContentDirectory = Get-PublishContentDirectory -PublishDirectory $publishDirectory.FullName -ProjectName $projectName

        Assert-PublishArtifacts -PublishDirectory $publishContentDirectory

        if (Test-Path $destinationDirectory) {
            Remove-Item -Path $destinationDirectory -Recurse -Force
        }

        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        Copy-Item -Path (Join-Path $publishContentDirectory "*") -Destination $destinationDirectory -Recurse -Force

        Assert-PublishArtifacts -PublishDirectory $destinationDirectory

        $successCount++
        $publishResults += [PSCustomObject]@{
            Project = $projectName
            Group = $projectGroup
            Destination = $destinationDirectory
        }

        Write-Host ""
        Write-Host "Publish completed successfully ($successCount/$($projects.Count))" -ForegroundColor Green
        Write-Host "Copied output to: $destinationDirectory" -ForegroundColor Green
        Write-Host ""
    }

    Write-Host "========================================" -ForegroundColor Green
    Write-Host "All WebAssembly publishes completed successfully!" -ForegroundColor Green
    Write-Host "Total: $successCount/$($projects.Count)" -ForegroundColor Green
    Write-Host "Output root: $resolvedOutputRoot" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""

    foreach ($result in $publishResults) {
        Write-Host ("{0}/{1}: {2}" -f $result.Group, $result.Project, $result.Destination) -ForegroundColor Gray
    }
}
catch {
    Write-Host "" 
    Write-Host "========================================" -ForegroundColor Red
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "========================================" -ForegroundColor Red
    exit 1
}