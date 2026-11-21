// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Numerics.Tensors;

namespace Typhon.Stdlib;

public static partial class numpy
{
    /// <summary>numpy.typing: annotations only (NDArray[...] is an ndarray, see the Typhon compiler's MapType).</summary>
    public static class typing { }

    /// <summary>numpy.linalg (subset): norm, det, inv, solve, matrix_power.</summary>
    public static class linalg
    {
        /// <summary>Frobenius norm of a matrix / 2-norm of a vector (of the flattened array).</summary>
        public static double norm(ndarray x) => TensorPrimitives.Norm<double>(x._data);

        /// <summary>Vector 2-norms along an axis.</summary>
        public static ndarray norm(ndarray x, int axis)
        {
            var sq = x * x;
            return sqrt(sq.sum(axis));
        }

        /// <summary>Determinant (LU with partial pivoting).</summary>
        public static double det(ndarray a)
        {
            int n = SquareSize(a);
            var lu = (double[])a._data.Clone();
            var piv = new int[n];
            int swaps = LuDecompose(lu, n, piv, out bool singular);
            if (singular) return 0.0;
            double d = swaps % 2 == 0 ? 1.0 : -1.0;
            for (int i = 0; i < n; i++) d *= lu[i * n + i];
            return d;
        }

        /// <summary>Matrix inverse.</summary>
        public static ndarray inv(ndarray a)
        {
            int n = SquareSize(a);
            return solve(a, eye(n));
        }

        /// <summary>Solve a @ x = b for x (b: vector or matrix).</summary>
        public static ndarray solve(ndarray a, ndarray b)
        {
            int n = SquareSize(a);
            var lu = (double[])a._data.Clone();
            var piv = new int[n];
            LuDecompose(lu, n, piv, out bool singular);
            if (singular) throw new ArithmeticException("Singular matrix");
            int nrhs;
            if (b.ndim == 1)
            {
                if (b._shape[0] != n) throw Err.Value("solve: Input operand 1 has a mismatch in its core dimension 0");
                nrhs = 1;
            }
            else if (b.ndim == 2)
            {
                if (b._shape[0] != n) throw Err.Value("solve: Input operand 1 has a mismatch in its core dimension 0");
                nrhs = b._shape[1];
            }
            else throw new NotSupportedException("solve: b must be 1-D or 2-D");

            var x = new double[n * nrhs];
            var col = new double[n];
            for (int c = 0; c < nrhs; c++)
            {
                for (int i = 0; i < n; i++) col[i] = b._data[piv[i] * nrhs + c];
                // forward substitution (L has unit diagonal)
                for (int i = 0; i < n; i++)
                {
                    double s = col[i];
                    for (int k = 0; k < i; k++) s -= lu[i * n + k] * col[k];
                    col[i] = s;
                }
                // back substitution
                for (int i = n - 1; i >= 0; i--)
                {
                    double s = col[i];
                    for (int k = i + 1; k < n; k++) s -= lu[i * n + k] * col[k];
                    col[i] = s / lu[i * n + i];
                }
                for (int i = 0; i < n; i++) x[i * nrhs + c] = col[i];
            }
            return new ndarray(x, b.ndim == 1 ? new[] { n } : new[] { n, nrhs });
        }

        /// <summary>a raised to an integer power (repeated squaring; negative uses inv).</summary>
        public static ndarray matrix_power(ndarray a, int n)
        {
            int size = SquareSize(a);
            if (n == 0) return eye(size);
            var b = n < 0 ? inv(a) : a.copy();
            int p = Math.Abs(n);
            ndarray result = null;
            while (p > 0)
            {
                if ((p & 1) != 0) result = result == null ? b : matmul(result, b);
                p >>= 1;
                if (p > 0) b = matmul(b, b);
            }
            return result;
        }

        private static int SquareSize(ndarray a)
        {
            if (a.ndim != 2 || a._shape[0] != a._shape[1])
                throw Err.Value($"Last 2 dimensions of the array must be square (got shape {Err.ShapeStr(a._shape)})");
            return a._shape[0];
        }

        /// <summary>In-place Doolittle LU with partial pivoting; piv maps result row -> original row. Returns swap count.</summary>
        private static int LuDecompose(double[] lu, int n, int[] piv, out bool singular)
        {
            singular = false;
            int swaps = 0;
            for (int i = 0; i < n; i++) piv[i] = i;
            for (int k = 0; k < n; k++)
            {
                int p = k;
                double maxAbs = Math.Abs(lu[k * n + k]);
                for (int i = k + 1; i < n; i++)
                {
                    double v = Math.Abs(lu[i * n + k]);
                    if (v > maxAbs) { maxAbs = v; p = i; }
                }
                if (maxAbs == 0) { singular = true; continue; }
                if (p != k)
                {
                    for (int j = 0; j < n; j++) (lu[k * n + j], lu[p * n + j]) = (lu[p * n + j], lu[k * n + j]);
                    (piv[k], piv[p]) = (piv[p], piv[k]);
                    swaps++;
                }
                double pivot = lu[k * n + k];
                for (int i = k + 1; i < n; i++)
                {
                    double f = lu[i * n + k] / pivot;
                    lu[i * n + k] = f;
                    if (f != 0)
                    {
                        var row = lu.AsSpan(i * n + k + 1, n - k - 1);
                        TensorPrimitives.MultiplyAdd(lu.AsSpan(k * n + k + 1, n - k - 1), -f, row, row);
                    }
                }
            }
            return swaps;
        }
    }
}
