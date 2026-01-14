# Run all demo projects sequentially
# Stops if any project exits with an error

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("All", "BasicGraphics", "GPGPUDemos", "GraphicsTechniques", "WebGPUFeatures")]
    [string]$Category = "All"
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
    "GraphicsTechniques\Cornell\Cornell.csproj"
)

# Select projects based on category
$projects = switch ($Category) {
    "BasicGraphics" { $basicGraphicsProjects }
    "GPGPUDemos" { $gpgpuDemosProjects }
    "GraphicsTechniques" { $graphicsTechniquesProjects }
    "WebGPUFeatures" { $webGPUFeaturesProjects }
    "All" { $basicGraphicsProjects + $webGPUFeaturesProjects + $gpgpuDemosProjects + $graphicsTechniquesProjects  }
}

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Get-Location
}

Write-Host "Starting sequential project execution..." -ForegroundColor Cyan
Write-Host "Category: $Category" -ForegroundColor Cyan
$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Get-Location
}

Write-Host "Starting sequential project execution..." -ForegroundColor Cyan
Write-Host "Total projects: $($projects.Count)" -ForegroundColor Cyan
Write-Host ""

$successCount = 0

foreach ($project in $projects) {
    $projectPath = Join-Path $scriptDir $project
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($project)
    
    Write-Host "========================================" -ForegroundColor Yellow
    Write-Host "Running: $projectName" -ForegroundColor Yellow
    Write-Host "Project: $project" -ForegroundColor Gray
    Write-Host "========================================" -ForegroundColor Yellow
    Write-Host ""
    
    # Run the project and wait for it to complete
    dotnet run --project $projectPath
    
    # Check the exit code
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "========================================" -ForegroundColor Red
        Write-Host "ERROR: Project failed!" -ForegroundColor Red
        Write-Host "Failed project: $projectName" -ForegroundColor Red
        Write-Host "Project path: $project" -ForegroundColor Red
        Write-Host "Exit code: $LASTEXITCODE" -ForegroundColor Red
        Write-Host "========================================" -ForegroundColor Red
        Write-Host ""
        Write-Host "Successfully completed: $successCount/$($projects.Count) projects" -ForegroundColor Yellow
        exit $LASTEXITCODE
    }
    
    $successCount++
    Write-Host ""
    Write-Host "Project completed successfully ($successCount/$($projects.Count))" -ForegroundColor Green
    Write-Host ""
}

Write-Host "========================================" -ForegroundColor Green
Write-Host "All projects completed successfully!" -ForegroundColor Green
Write-Host "Total: $successCount/$($projects.Count)" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green