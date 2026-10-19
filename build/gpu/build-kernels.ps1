param([string]$Python = 'python', [string]$Nvcc = 'nvcc', [string]$Glslang = 'glslangValidator')
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
& $Python (Join-Path $PSScriptRoot 'prepare-kernels.py')
if ($LASTEXITCODE -ne 0) { throw 'Kernel preparation failed' }
$taskOutput = Join-Path $taskRoot 'src\FUPlayer.Core\Dsp\Acceleration\Kernels'
foreach ($taskBits in @(32, 64)) {
    $taskDefines = @()
    if ($taskBits -eq 64) { $taskDefines = @('-DREAL_IS_DOUBLE') }
    & $Nvcc --fatbin --fmad=false -Xcompiler=/utf-8 @taskDefines '-gencode=arch=compute_52,code=sm_52' '-gencode=arch=compute_60,code=sm_60' '-gencode=arch=compute_70,code=sm_70' '-gencode=arch=compute_80,code=sm_80' '-gencode=arch=compute_90,code=sm_90' '-gencode=arch=compute_100,code=sm_100' '-gencode=arch=compute_120,code=sm_120' '-gencode=arch=compute_52,code=compute_52' -o (Join-Path $taskOutput "cuda$taskBits.fatbin") (Join-Path $taskOutput 'convolution.cu')
    if ($LASTEXITCODE -ne 0) { throw "CUDA $taskBits compilation failed" }
    foreach ($taskKernel in @('fft_pass', 'accumulate', 'scatter', 'direct')) {
        & $Glslang -V --target-env vulkan1.1 @taskDefines -S comp -o (Join-Path $taskOutput "$taskKernel$taskBits.spv") (Join-Path $taskOutput "$taskKernel.comp")
        if ($LASTEXITCODE -ne 0) { throw "Vulkan $taskKernel $taskBits compilation failed" }
    }
}
