namespace FUPlayer.Core.Dsp.Acceleration;

/// <summary>
/// The OpenCL C program behind the two accelerated filter stages. For the frequency domain: a batched radix-2
/// Stockham FFT for the inverse, the partition multiply-accumulate that convolves one phase, and the scatter that
/// interleaves the phases into output samples. This is exactly the arithmetic <c>FftPolyphaseStage</c> performs, so
/// either may produce a block. There is no forward transform here: the processor transforms every input block for
/// its own phases whether the device is given any or not, so the spectrum is handed over rather than computed
/// twice. For tap by tap: one multiply-add per tap per output sample, which is the plainest thing a filter can do
/// and the one a device built on thousands of independent lanes is best at.
/// </summary>
internal static class GpuKernels
{
    /// <summary>Compiled twice, with <c>REAL_IS_DOUBLE</c> for 64-bit devices and without it for 32-bit ones.</summary>
    public const string Source = """
        #ifdef REAL_IS_DOUBLE
        #pragma OPENCL EXTENSION cl_khr_fp64 : enable
        typedef double real_t;
        #else
        typedef float real_t;
        #endif

        // One pass of a radix-2 Stockham autosort FFT over `n` points, `count` transforms at a time.
        // Pass L works with s = 1 << L: the caller runs log2(n) of them, ping-ponging source and destination.
        // Twiddles come from a table so the arithmetic matches the CPU plan bit for bit on a well-behaved device.
        __kernel void fft_pass(__global const real_t* srcRe,
                               __global const real_t* srcIm,
                               __global real_t* dstRe,
                               __global real_t* dstIm,
                               __global const real_t* twiddleRe,
                               __global const real_t* twiddleIm,
                               const int srcOffset,
                               const int dstOffset,
                               const int n,
                               const int s,
                               const int logS,
                               const real_t sign)
        {
            int g = get_global_id(0);           // 0 .. n/2 - 1
            int batch = get_global_id(1);
            int m = (n >> 1) >> logS;           // points handled per group at this pass
            int q = g & (s - 1);
            int p = g >> logS;

            int src = srcOffset + batch * n;
            int dst = dstOffset + batch * n;
            int a = src + q + s * p;
            int b = src + q + s * (p + m);

            real_t ar = srcRe[a], ai = srcIm[a];
            real_t br = srcRe[b], bi = srcIm[b];
            real_t dr = ar - br, di = ai - bi;

            int t = p * s;
            real_t wr = twiddleRe[t];
            real_t wi = sign * twiddleIm[t];

            dstRe[dst + q + s * 2 * p] = ar + br;
            dstIm[dst + q + s * 2 * p] = ai + bi;
            dstRe[dst + q + s * (2 * p + 1)] = dr * wr - di * wi;
            dstIm[dst + q + s * (2 * p + 1)] = dr * wi + di * wr;
        }

        // Accumulates every filter partition of one phase against the matching stored input spectrum.
        // This is the work that grows with the filter length; everything else is fixed per block.
        // A launch covers the phases [phaseBase, phaseBase + lanes) and writes them packed from zero, so the
        // processor can be doing the rest of the phases of the same block at the same time.
        __kernel void accumulate(__global const real_t* filterRe,   // [phase][partition][n]
                                 __global const real_t* filterIm,
                                 __global const real_t* historyRe,  // [partition][n], a ring
                                 __global const real_t* historyIm,
                                 __global real_t* accRe,            // [lane][n]
                                 __global real_t* accIm,
                                 const int n,
                                 const int partitions,
                                 const int head,
                                 const int phaseBase)
        {
            int k = get_global_id(0);
            int lane = get_global_id(1);
            int phase = phaseBase + lane;
            real_t sr = (real_t)0;
            real_t si = (real_t)0;
            int filterBase = (phase * partitions) * n + k;

            for (int part = 0; part < partitions; part++)
            {
                int slot = head - part;
                slot += slot < 0 ? partitions : 0;
                real_t xr = historyRe[slot * n + k];
                real_t xi = historyIm[slot * n + k];
                real_t hr = filterRe[filterBase + part * n];
                real_t hi = filterIm[filterBase + part * n];
                sr += xr * hr - xi * hi;
                si += xr * hi + xi * hr;
            }

            accRe[lane * n + k] = sr;
            accIm[lane * n + k] = si;
        }

        // Packs the second half of each lane's result into consecutive output samples and applies the 1/n the
        // inverse transform still owes. The lanes are only the phases this device was given, so they are packed
        // `lanes` to a sample and the host spreads them into the phases the processor filled in beside them.
        __kernel void scatter(__global const real_t* accRe,
                              __global real_t* output,
                              const int n,
                              const int block,
                              const int lanes,
                              const real_t scale)
        {
            int i = get_global_id(0);
            int lane = get_global_id(1);
            output[i * lanes + lane] = accRe[lane * n + block + i] * scale;
        }

        // Tap by tap: output[j * up + phase] = sum over k of coefficients[phase][k] * signal[j + k].
        // One work item per output sample, one work group per run of consecutive samples of the same phase. The
        // group stages the coefficients through local memory a slice at a time, so the whole filter is read once
        // for the group rather than once for every sample in it, which is the same trick the processor path
        // uses, and for the same reason: past a few thousand taps the coefficients no longer fit near the lanes.
        __kernel void direct(__global const real_t* restrict coefficients,   // [phase][taps], oldest tap first
                             __global const real_t* restrict signal,         // first sample of output 0's window
                             __global real_t* restrict output,
                             __local real_t* tile,
                             const int taps,
                             const int lanes,
                             const int count,
                             const int phaseBase)
        {
            int lane = get_local_id(0);
            int width = get_local_size(0);
            int j = get_global_id(0);
            int slot = get_global_id(1);
            int phase = phaseBase + slot;

            // Items past the end still have to reach every barrier, so they do the work and drop the answer.
            __global const real_t* restrict c = coefficients + (size_t)phase * taps;
            __global const real_t* restrict x = signal + (j < count ? j : count - 1);

            real_t s0 = (real_t)0, s1 = (real_t)0, s2 = (real_t)0, s3 = (real_t)0;
            real_t s4 = (real_t)0, s5 = (real_t)0, s6 = (real_t)0, s7 = (real_t)0;
            for (int base = 0; base < taps; base += width)
            {
                barrier(CLK_LOCAL_MEM_FENCE);
                tile[lane] = base + lane < taps ? c[base + lane] : (real_t)0;
                barrier(CLK_LOCAL_MEM_FENCE);

                int n = min(width, taps - base);
                int k = 0;
                for (; k + 8 <= n; k += 8)
                {
                    s0 += tile[k] * x[base + k];
                    s1 += tile[k + 1] * x[base + k + 1];
                    s2 += tile[k + 2] * x[base + k + 2];
                    s3 += tile[k + 3] * x[base + k + 3];
                    s4 += tile[k + 4] * x[base + k + 4];
                    s5 += tile[k + 5] * x[base + k + 5];
                    s6 += tile[k + 6] * x[base + k + 6];
                    s7 += tile[k + 7] * x[base + k + 7];
                }

                for (; k < n; k++)
                {
                    s0 += tile[k] * x[base + k];
                }
            }

            if (j < count)
            {
                output[(size_t)j * lanes + slot] = ((s0 + s1) + (s2 + s3)) + ((s4 + s5) + (s6 + s7));
            }
        }
        """;
}
