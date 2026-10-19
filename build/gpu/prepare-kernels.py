"""Translate the project's convolution kernels to CUDA C and GLSL without third-party shader sources."""
from pathlib import Path
import re

root = Path(__file__).resolve().parents[2]
destination = root / 'src/FUPlayer.Core/Dsp/Acceleration/Kernels'
destination.mkdir(exist_ok=True)
source = (root / 'src/FUPlayer.Core/Dsp/Acceleration/GpuKernels.cs').read_text(encoding='utf-8-sig')
source = source.split('public const string Source = """', 1)[1].split('""";', 1)[0]
source = '\n'.join(line[8:] if line.startswith('        ') else line for line in source.splitlines())
source = source.replace('#pragma OPENCL EXTENSION cl_khr_fp64 : enable', '')
cuda = source.replace('__kernel void', 'extern "C" __global__ void').replace('__global ', '').replace('restrict ', '')
cuda = cuda.replace('__local real_t* tile', 'real_t* tile')
cuda = cuda.replace('int lane = get_local_id(0);', 'extern __shared__ real_t sharedTile[];\n    tile = sharedTile;\n    int lane = get_local_id(0);')
cuda = cuda.replace('barrier(CLK_LOCAL_MEM_FENCE)', '__syncthreads()')
cuda = '''// Project kernels, derived from GpuKernels.cs by build/gpu/prepare-kernels.py.
#define get_global_id(d) ((d) == 0 ? blockIdx.x * blockDim.x + threadIdx.x : blockIdx.y)
#define get_local_id(d) (threadIdx.x)
#define get_local_size(d) (blockDim.x)
''' + cuda
(destination / 'convolution.cu').write_text(cuda, encoding='utf-8')

for match in re.finditer(r'__kernel void (\w+)\((.*?)\)\s*\{', source, re.S):
    name, parameters = match.group(1, 2)
    start = match.end()
    depth, end = 1, start
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    body = source[start:end-1]
    parameters = re.sub(r'//[^\n]*', '', parameters)
    buffers, declarations = [], []
    for index, parameter in enumerate(parameters.split(',')):
        parameter = parameter.strip()
        argname = parameter.split()[-1].lstrip('*')
        if '__global' in parameter:
            binding = len(buffers)
            buffers.append(f'layout(set=0, binding={binding}, std430) buffer Buffer{binding} {{ real_t {argname}[]; }};')
        elif '__local' not in parameter:
            if 'int ' in parameter:
                declarations.append(f'int {argname} = int(p.args[{index}].x);')
            else:
                declarations.append(f'real_t {argname} = REAL_ARG({index});')
    if name == 'direct':
        body = re.sub(r'__global const real_t\* restrict c = coefficients \+ \(size_t\)phase \* taps;', 'int coefficientBase = phase * taps;', body)
        body = re.sub(r'__global const real_t\* restrict x = signal \+ \(j < count \? j : count - 1\);', 'int signalBase = j < count ? j : count - 1;', body)
        body = re.sub(r'\bc\[([^\]]+)\]', r'coefficients[coefficientBase + \1]', body)
        body = re.sub(r'\bx\[([^\]]+)\]', r'signal[signalBase + \1]', body)
    body = body.replace('(size_t)', '')
    body = body.replace('get_global_id(0)', 'int(gl_GlobalInvocationID.x)').replace('get_global_id(1)', 'int(gl_GlobalInvocationID.y)')
    body = body.replace('get_local_id(0)', 'int(gl_LocalInvocationID.x)').replace('get_local_size(0)', 'int(gl_WorkGroupSize.x)')
    body = body.replace('barrier(CLK_LOCAL_MEM_FENCE)', 'barrier()')
    body = re.sub(r'\(real_t\)([0-9]+)', r'real_t(\1)', body)
    body = 'if (gl_GlobalInvocationID.x >= p.extent.x || gl_GlobalInvocationID.y >= p.extent.y) return;\n' + '\n'.join(declarations) + body
    header = '''// Project kernels, derived from GpuKernels.cs by build/gpu/prepare-kernels.py.
#version 450
#ifdef REAL_IS_DOUBLE
#extension GL_ARB_gpu_shader_fp64 : require
#define real_t double
#define REAL_ARG(i) packDouble2x32(p.args[i])
#else
#define real_t float
#define REAL_ARG(i) uintBitsToFloat(p.args[i].x)
#endif
layout(local_size_x_id=0, local_size_y=1, local_size_z=1) in;
layout(push_constant, std430) uniform Arguments { uvec2 args[12]; uvec2 extent; } p;
'''
    if name == 'direct':
        header += 'shared real_t tile[256];\n'
    shader = header + '\n'.join(buffers) + '\nvoid main() {\n' + body + '\n}\n'
    shader = re.sub(r'\boutput\b', 'resultBuffer', shader)
    (destination / (name + '.comp')).write_text(shader, encoding='utf-8')
