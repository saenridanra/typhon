// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;

namespace Typhon.Stdlib;

/// <summary>
/// Dense float64 matrix multiplication.
/// Strategy: transpose B once so every output cell is a dot product of two contiguous rows, then
/// compute 2x4 output tiles with 8 independent Vector256 FMA accumulators. Columns of Bt are processed
/// in L2-sized blocks so they are reused across many rows of A, and row blocks run in parallel.
/// </summary>
internal static class MatMulKernels
{
    /// <summary>C (n x m) = A (n x k) * B (k x m); all row-major.</summary>
    internal static double[] MatMul(double[] A, double[] B, int n, int k, int m)
    {
        long cells = (long)n * m;
        if (cells > Array.MaxLength) throw new OutOfMemoryException("array is too big");
        var C = new double[cells];
        if (n == 0 || m == 0 || k == 0) return C;
        long work = cells * k;

        if (work <= 32L * 32 * 32 || !Vector256.IsHardwareAccelerated)
        {
            // Small (or no SIMD): i-p-j "axpy" order, vectorized across output row j.
            Kernels.ChunkedItems(n, (long)k * m, (i0, cnt) =>
            {
                for (int i = i0; i < i0 + cnt; i++)
                {
                    var crow = C.AsSpan(i * m, m);
                    for (int p = 0; p < k; p++)
                        TensorPrimitives.MultiplyAdd(B.AsSpan(p * m, m), A[i * k + p], crow, crow);
                }
            });
            return C;
        }

        double[] Bt = Kernels.Transpose2D(B, k, m); // m x k

        // Columns (rows of Bt) per block: about 256 KB of Bt, multiple of 4.
        int jBlock = Math.Max(4, ((256 * 1024 / 8) / k) & ~3);
        if (jBlock > m) jBlock = (m + 3) & ~3;

        const int RowsPerTask = 16;
        int tasks = (n + RowsPerTask - 1) / RowsPerTask;
        bool parallel = NumPyRuntime.MaxDegreeOfParallelism > 1 && work >= NumPyRuntime.MatMulParallelThreshold && tasks > 1;
        if (parallel)
        {
            Parallel.For(0, tasks, new ParallelOptions { MaxDegreeOfParallelism = NumPyRuntime.MaxDegreeOfParallelism },
                t => RowBlock(A, Bt, C, t * RowsPerTask, Math.Min(n, (t + 1) * RowsPerTask), k, m, jBlock));
        }
        else
        {
            for (int t = 0; t < tasks; t++)
                RowBlock(A, Bt, C, t * RowsPerTask, Math.Min(n, (t + 1) * RowsPerTask), k, m, jBlock);
        }
        return C;
    }

    private static void RowBlock(double[] A, double[] Bt, double[] C, int i0, int i1, int k, int m, int jBlock)
    {
        for (int j0 = 0; j0 < m; j0 += jBlock)
        {
            int j1 = Math.Min(m, j0 + jBlock);
            int i = i0;
            for (; i + 1 < i1; i += 2)
            {
                int j = j0;
                for (; j + 3 < j1; j += 4)
                    Kernel2x4(A, i * k, Bt, j * k, k, C, i * m + j, m);
                for (; j < j1; j++)
                {
                    var bt = Bt.AsSpan(j * k, k);
                    C[i * m + j] = TensorPrimitives.Dot(A.AsSpan(i * k, k), bt);
                    C[(i + 1) * m + j] = TensorPrimitives.Dot(A.AsSpan((i + 1) * k, k), bt);
                }
            }
            if (i < i1)
            {
                var arow = A.AsSpan(i * k, k);
                for (int j = j0; j < j1; j++)
                    C[i * m + j] = TensorPrimitives.Dot(arow, Bt.AsSpan(j * k, k));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> Fmadd(Vector256<double> a, Vector256<double> b, Vector256<double> c) =>
        Fma.IsSupported ? Fma.MultiplyAdd(a, b, c) : a * b + c;

    /// <summary>
    /// C[i..i+1, j..j+3] = A[i..i+1, :] . Bt[j..j+3, :]^T. Offsets are element offsets; rows of A and Bt
    /// are k long, rows of C are m long. Caller guarantees all indices are in range.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Kernel2x4(double[] A, int aOff, double[] Bt, int bOff, int k, double[] C, int cOff, int m)
    {
        ref double a0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(A), aOff);
        ref double a1 = ref Unsafe.Add(ref a0, k);
        ref double b0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bt), bOff);
        ref double b1 = ref Unsafe.Add(ref b0, k);
        ref double b2 = ref Unsafe.Add(ref b1, k);
        ref double b3 = ref Unsafe.Add(ref b2, k);

        Vector256<double> c00 = default, c01 = default, c02 = default, c03 = default;
        Vector256<double> c10 = default, c11 = default, c12 = default, c13 = default;

        int p = 0;
        for (; p <= k - 4; p += 4)
        {
            nuint up = (nuint)p;
            var x0 = Vector256.LoadUnsafe(ref a0, up);
            var x1 = Vector256.LoadUnsafe(ref a1, up);
            var y = Vector256.LoadUnsafe(ref b0, up);
            c00 = Fmadd(x0, y, c00); c10 = Fmadd(x1, y, c10);
            y = Vector256.LoadUnsafe(ref b1, up);
            c01 = Fmadd(x0, y, c01); c11 = Fmadd(x1, y, c11);
            y = Vector256.LoadUnsafe(ref b2, up);
            c02 = Fmadd(x0, y, c02); c12 = Fmadd(x1, y, c12);
            y = Vector256.LoadUnsafe(ref b3, up);
            c03 = Fmadd(x0, y, c03); c13 = Fmadd(x1, y, c13);
        }

        double s00 = Vector256.Sum(c00), s01 = Vector256.Sum(c01), s02 = Vector256.Sum(c02), s03 = Vector256.Sum(c03);
        double s10 = Vector256.Sum(c10), s11 = Vector256.Sum(c11), s12 = Vector256.Sum(c12), s13 = Vector256.Sum(c13);
        for (; p < k; p++)
        {
            double x0 = Unsafe.Add(ref a0, p), x1 = Unsafe.Add(ref a1, p);
            double y0 = Unsafe.Add(ref b0, p), y1 = Unsafe.Add(ref b1, p), y2 = Unsafe.Add(ref b2, p), y3 = Unsafe.Add(ref b3, p);
            s00 += x0 * y0; s01 += x0 * y1; s02 += x0 * y2; s03 += x0 * y3;
            s10 += x1 * y0; s11 += x1 * y1; s12 += x1 * y2; s13 += x1 * y3;
        }

        C[cOff] = s00; C[cOff + 1] = s01; C[cOff + 2] = s02; C[cOff + 3] = s03;
        int c1 = cOff + m;
        C[c1] = s10; C[c1 + 1] = s11; C[c1 + 2] = s12; C[c1 + 3] = s13;
    }

    /// <summary>y (n) = A (n x k) * x (k).</summary>
    internal static double[] MatVec(double[] A, double[] x, int n, int k)
    {
        var y = new double[n];
        Kernels.ChunkedItems(n, k, (i0, cnt) =>
        {
            for (int i = i0; i < i0 + cnt; i++) y[i] = TensorPrimitives.Dot(A.AsSpan(i * k, k), x);
        });
        return y;
    }

    /// <summary>y (m) = x (k) * B (k x m).</summary>
    internal static double[] VecMat(double[] x, double[] B, int k, int m)
    {
        var y = new double[m];
        for (int p = 0; p < k; p++) TensorPrimitives.MultiplyAdd(B.AsSpan(p * m, m), x[p], y, y);
        return y;
    }
}
