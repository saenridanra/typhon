// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using static Typhon.Stdlib.numpy;

namespace Typhon.Stdlib;

internal enum BinOp : byte
{
    Add, Sub, Mul, Div, Pow, Max, Min, FMax, FMin, Mod, FloorDiv, Atan2, Hypot,
    Lt, Le, Gt, Ge, Eq, Ne, And, Or, Xor,
}

internal enum UnOp : byte
{
    Neg, Abs, Sqrt, Square, Cbrt, Exp, Exp2, Expm1, Log, Log2, Log10, Log1p,
    Sin, Cos, Tan, Arcsin, Arccos, Arctan, Sinh, Cosh, Tanh, Arcsinh, Arccosh, Arctanh,
    Floor, Ceil, Trunc, Rint, Sign, Reciprocal, Deg2Rad, Rad2Deg,
    Not, IsNaN, IsInf, IsFinite,
}

internal enum RedOp : byte { Sum, Prod, Max, Min, Mean, Any, All, ArgMax, ArgMin }

/// <summary>Vectorized kernels shared by ndarray and the numpy module functions.</summary>
internal static class Kernels
{
    // ------------------------------------------------------------------ allocation / parallel helpers

    /// <summary>Uninitialized buffer (caller must overwrite every element).</summary>
    internal static double[] Alloc(int n) => n == 0 ? Array.Empty<double>() : GC.AllocateUninitializedArray<double>(n);

    internal static int Prod(int[] shape)
    {
        long p = 1;
        foreach (int d in shape)
        {
            if (d < 0) throw Err.Value("negative dimensions are not allowed");
            p *= d;
        }
        if (p > Array.MaxLength) throw new OutOfMemoryException("array is too big");
        return (int)p;
    }

    internal static bool SameShape(int[] a, int[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static bool Parallelize(long work) =>
        NumPyRuntime.MaxDegreeOfParallelism > 1 && work >= NumPyRuntime.ParallelThreshold;

    /// <summary>Runs body(start, count) over [0, n), split into chunks across threads when n is large.</summary>
    internal static void Chunked(int n, Action<int, int> body)
    {
        if (!Parallelize(n)) { body(0, n); return; }
        int workers = Math.Min(NumPyRuntime.MaxDegreeOfParallelism, Math.Max(1, n / 16384));
        if (workers <= 1) { body(0, n); return; }
        int chunks = workers * 2;
        int chunk = (n + chunks - 1) / chunks;
        chunk = (chunk + 7) & ~7; // keep chunk starts 64-byte aligned relative to the array start
        chunks = (n + chunk - 1) / chunk;
        Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = workers }, c =>
        {
            int s = c * chunk;
            int len = Math.Min(chunk, n - s);
            if (len > 0) body(s, len);
        });
    }

    /// <summary>Runs body(start, count) over `count` independent items each costing `workPerItem` elements.</summary>
    internal static void ChunkedItems(int count, long workPerItem, Action<int, int> body)
    {
        long total = (long)count * Math.Max(1, workPerItem);
        if (count < 2 || !Parallelize(total)) { body(0, count); return; }
        int workers = Math.Min(NumPyRuntime.MaxDegreeOfParallelism, count);
        int chunks = Math.Min(count, workers * 4);
        int chunk = (count + chunks - 1) / chunks;
        chunks = (count + chunk - 1) / chunk;
        Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = workers }, c =>
        {
            int s = c * chunk;
            int len = Math.Min(chunk, count - s);
            if (len > 0) body(s, len);
        });
    }

    // ------------------------------------------------------------------ unary

    internal static NdKind UnaryKind(UnOp op) => op is UnOp.Not or UnOp.IsNaN or UnOp.IsInf or UnOp.IsFinite ? NdKind.Bool : NdKind.Float;

    internal static ndarray Unary(ndarray a, UnOp op)
    {
        var src = a._data;
        var r = Alloc(src.Length);
        Chunked(r.Length, (s, n) => UnarySpan(op, src.AsSpan(s, n), r.AsSpan(s, n)));
        return new ndarray(r, (int[])a._shape.Clone(), UnaryKind(op));
    }

    internal static void UnarySpan(UnOp op, ReadOnlySpan<double> x, Span<double> r)
    {
        switch (op)
        {
            case UnOp.Neg: TensorPrimitives.Negate(x, r); return;
            case UnOp.Abs: TensorPrimitives.Abs(x, r); return;
            case UnOp.Sqrt: TensorPrimitives.Sqrt(x, r); return;
            case UnOp.Square: TensorPrimitives.Multiply(x, x, r); return;
            case UnOp.Cbrt: TensorPrimitives.Cbrt(x, r); return;
            case UnOp.Exp: TensorPrimitives.Exp(x, r); return;
            case UnOp.Exp2: TensorPrimitives.Exp2(x, r); return;
            case UnOp.Expm1: TensorPrimitives.ExpM1(x, r); return;
            case UnOp.Log: TensorPrimitives.Log(x, r); return;
            case UnOp.Log2: TensorPrimitives.Log2(x, r); return;
            case UnOp.Log10: TensorPrimitives.Log10(x, r); return;
            case UnOp.Log1p: TensorPrimitives.LogP1(x, r); return;
            case UnOp.Sin: TensorPrimitives.Sin(x, r); return;
            case UnOp.Cos: TensorPrimitives.Cos(x, r); return;
            case UnOp.Tan: TensorPrimitives.Tan(x, r); return;
            case UnOp.Arcsin: TensorPrimitives.Asin(x, r); return;
            case UnOp.Arccos: TensorPrimitives.Acos(x, r); return;
            case UnOp.Arctan: TensorPrimitives.Atan(x, r); return;
            case UnOp.Sinh: TensorPrimitives.Sinh(x, r); return;
            case UnOp.Cosh: TensorPrimitives.Cosh(x, r); return;
            case UnOp.Tanh: TensorPrimitives.Tanh(x, r); return;
            case UnOp.Arcsinh: TensorPrimitives.Asinh(x, r); return;
            case UnOp.Arccosh: TensorPrimitives.Acosh(x, r); return;
            case UnOp.Arctanh: TensorPrimitives.Atanh(x, r); return;
            case UnOp.Floor: TensorPrimitives.Floor(x, r); return;
            case UnOp.Ceil: TensorPrimitives.Ceiling(x, r); return;
            case UnOp.Trunc: TensorPrimitives.Truncate(x, r); return;
            case UnOp.Rint: TensorPrimitives.Round(x, MidpointRounding.ToEven, r); return;
            case UnOp.Reciprocal: TensorPrimitives.Divide(1.0, x, r); return;
            case UnOp.Deg2Rad: TensorPrimitives.Multiply(x, Math.PI / 180.0, r); return;
            case UnOp.Rad2Deg: TensorPrimitives.Multiply(x, 180.0 / Math.PI, r); return;
            default:
                for (int i = 0; i < x.Length; i++) r[i] = UnaryScalar(op, x[i]);
                return;
        }
    }

    internal static double UnaryScalar(UnOp op, double v) => op switch
    {
        UnOp.Neg => -v,
        UnOp.Abs => Math.Abs(v),
        UnOp.Sqrt => Math.Sqrt(v),
        UnOp.Square => v * v,
        UnOp.Cbrt => Math.Cbrt(v),
        UnOp.Exp => Math.Exp(v),
        UnOp.Exp2 => Math.Pow(2.0, v),
        UnOp.Expm1 => double.ExpM1(v),
        UnOp.Log => Math.Log(v),
        UnOp.Log2 => Math.Log2(v),
        UnOp.Log10 => Math.Log10(v),
        UnOp.Log1p => double.LogP1(v),
        UnOp.Sin => Math.Sin(v),
        UnOp.Cos => Math.Cos(v),
        UnOp.Tan => Math.Tan(v),
        UnOp.Arcsin => Math.Asin(v),
        UnOp.Arccos => Math.Acos(v),
        UnOp.Arctan => Math.Atan(v),
        UnOp.Sinh => Math.Sinh(v),
        UnOp.Cosh => Math.Cosh(v),
        UnOp.Tanh => Math.Tanh(v),
        UnOp.Arcsinh => Math.Asinh(v),
        UnOp.Arccosh => Math.Acosh(v),
        UnOp.Arctanh => Math.Atanh(v),
        UnOp.Floor => Math.Floor(v),
        UnOp.Ceil => Math.Ceiling(v),
        UnOp.Trunc => Math.Truncate(v),
        UnOp.Rint => Math.Round(v, MidpointRounding.ToEven),
        UnOp.Sign => v > 0 ? 1.0 : v < 0 ? -1.0 : v == 0 ? 0.0 : double.NaN,
        UnOp.Reciprocal => 1.0 / v,
        UnOp.Deg2Rad => v * (Math.PI / 180.0),
        UnOp.Rad2Deg => v * (180.0 / Math.PI),
        UnOp.Not => v == 0 ? 1.0 : 0.0,
        UnOp.IsNaN => double.IsNaN(v) ? 1.0 : 0.0,
        UnOp.IsInf => double.IsInfinity(v) ? 1.0 : 0.0,
        UnOp.IsFinite => double.IsFinite(v) ? 1.0 : 0.0,
        _ => throw new NotSupportedException(op.ToString()),
    };

    // ------------------------------------------------------------------ binary

    internal static NdKind BinaryKind(BinOp op) => op >= BinOp.Lt ? NdKind.Bool : NdKind.Float;

    internal static double PyMod(double a, double b)
    {
        double r = a % b; // C# % is C fmod (sign of the dividend); Python's mod takes the sign of the divisor
        if (double.IsNaN(r)) return r;
        if (r != 0) { if ((b < 0) != (r < 0)) r += b; }
        else r = Math.CopySign(0.0, b);
        return r;
    }

    internal static double Scalar(BinOp op, double a, double b) => op switch
    {
        BinOp.Add => a + b,
        BinOp.Sub => a - b,
        BinOp.Mul => a * b,
        BinOp.Div => a / b,
        BinOp.Pow => Math.Pow(a, b),
        BinOp.Max => Math.Max(a, b),
        BinOp.Min => Math.Min(a, b),
        BinOp.FMax => double.MaxNumber(a, b),
        BinOp.FMin => double.MinNumber(a, b),
        BinOp.Mod => PyMod(a, b),
        BinOp.FloorDiv => Math.Floor(a / b),
        BinOp.Atan2 => Math.Atan2(a, b),
        BinOp.Hypot => double.Hypot(a, b),
        BinOp.Lt => a < b ? 1.0 : 0.0,
        BinOp.Le => a <= b ? 1.0 : 0.0,
        BinOp.Gt => a > b ? 1.0 : 0.0,
        BinOp.Ge => a >= b ? 1.0 : 0.0,
        BinOp.Eq => a == b ? 1.0 : 0.0,
        BinOp.Ne => a != b ? 1.0 : 0.0,
        BinOp.And => (a != 0 && b != 0) ? 1.0 : 0.0,
        BinOp.Or => (a != 0 || b != 0) ? 1.0 : 0.0,
        BinOp.Xor => ((a != 0) ^ (b != 0)) ? 1.0 : 0.0,
        _ => throw new NotSupportedException(op.ToString()),
    };

    /// <summary>r = x op y (element-wise, same length).</summary>
    internal static void SS(BinOp op, ReadOnlySpan<double> x, ReadOnlySpan<double> y, Span<double> r)
    {
        switch (op)
        {
            case BinOp.Add: TensorPrimitives.Add(x, y, r); return;
            case BinOp.Sub: TensorPrimitives.Subtract(x, y, r); return;
            case BinOp.Mul: TensorPrimitives.Multiply(x, y, r); return;
            case BinOp.Div: TensorPrimitives.Divide(x, y, r); return;
            case BinOp.Pow: TensorPrimitives.Pow(x, y, r); return;
            case BinOp.Max: TensorPrimitives.Max(x, y, r); return;
            case BinOp.Min: TensorPrimitives.Min(x, y, r); return;
            case BinOp.FMax: TensorPrimitives.MaxNumber(x, y, r); return;
            case BinOp.FMin: TensorPrimitives.MinNumber(x, y, r); return;
            case BinOp.Atan2: TensorPrimitives.Atan2(x, y, r); return;
            case BinOp.Hypot: TensorPrimitives.Hypot(x, y, r); return;
            default:
                for (int i = 0; i < r.Length; i++) r[i] = Scalar(op, x[i], y[i]);
                return;
        }
    }

    /// <summary>r = x op y where y is a scalar.</summary>
    internal static void SV(BinOp op, ReadOnlySpan<double> x, double y, Span<double> r)
    {
        switch (op)
        {
            case BinOp.Add: TensorPrimitives.Add(x, y, r); return;
            case BinOp.Sub: TensorPrimitives.Subtract(x, y, r); return;
            case BinOp.Mul: TensorPrimitives.Multiply(x, y, r); return;
            case BinOp.Div: TensorPrimitives.Divide(x, y, r); return;
            case BinOp.Pow:
                // numpy's fast_scalar_power special cases
                if (y == 2.0) TensorPrimitives.Multiply(x, x, r);
                else if (y == 0.5) TensorPrimitives.Sqrt(x, r);
                else if (y == 1.0) x.CopyTo(r);
                else if (y == -1.0) TensorPrimitives.Divide(1.0, x, r);
                else if (y == 0.0) r.Fill(1.0);
                else if (y == 3.0) { TensorPrimitives.Multiply(x, x, r); TensorPrimitives.Multiply(r, x, r); }
                else TensorPrimitives.Pow(x, y, r);
                return;
            case BinOp.Max: TensorPrimitives.Max(x, y, r); return;
            case BinOp.Min: TensorPrimitives.Min(x, y, r); return;
            case BinOp.FMax: TensorPrimitives.MaxNumber(x, y, r); return;
            case BinOp.FMin: TensorPrimitives.MinNumber(x, y, r); return;
            default:
                for (int i = 0; i < r.Length; i++) r[i] = Scalar(op, x[i], y);
                return;
        }
    }

    /// <summary>r = x op y where x is a scalar.</summary>
    internal static void VS(BinOp op, double x, ReadOnlySpan<double> y, Span<double> r)
    {
        switch (op)
        {
            case BinOp.Add: TensorPrimitives.Add(y, x, r); return;
            case BinOp.Sub: TensorPrimitives.Subtract(x, y, r); return;
            case BinOp.Mul: TensorPrimitives.Multiply(y, x, r); return;
            case BinOp.Div: TensorPrimitives.Divide(x, y, r); return;
            case BinOp.Pow: TensorPrimitives.Pow(x, y, r); return;
            case BinOp.Max:
            case BinOp.Min:
            case BinOp.FMax:
            case BinOp.FMin:
            case BinOp.Hypot:
            case BinOp.Eq:
            case BinOp.Ne:
            case BinOp.And:
            case BinOp.Or:
            case BinOp.Xor:
                SV(op, y, x, r); return; // commutative
            default:
                for (int i = 0; i < r.Length; i++) r[i] = Scalar(op, x, y[i]);
                return;
        }
    }

    internal static ndarray BinaryScalarRight(ndarray a, double b, BinOp op)
    {
        var src = a._data;
        var r = Alloc(src.Length);
        Chunked(r.Length, (s, n) => SV(op, src.AsSpan(s, n), b, r.AsSpan(s, n)));
        return new ndarray(r, (int[])a._shape.Clone(), BinaryKind(op));
    }

    internal static ndarray BinaryScalarLeft(double a, ndarray b, BinOp op)
    {
        var src = b._data;
        var r = Alloc(src.Length);
        Chunked(r.Length, (s, n) => VS(op, a, src.AsSpan(s, n), r.AsSpan(s, n)));
        return new ndarray(r, (int[])b._shape.Clone(), BinaryKind(op));
    }

    internal static int[] BroadcastShapes(int[] a, int[] b)
    {
        int nd = Math.Max(a.Length, b.Length);
        var r = new int[nd];
        for (int i = 0; i < nd; i++)
        {
            int da = i < nd - a.Length ? 1 : a[i - (nd - a.Length)];
            int db = i < nd - b.Length ? 1 : b[i - (nd - b.Length)];
            if (da == db || db == 1) r[i] = da;
            else if (da == 1) r[i] = db;
            else throw Err.Value($"operands could not be broadcast together with shapes {Err.ShapeStr(a)} {Err.ShapeStr(b)}");
        }
        return r;
    }

    /// <summary>Element strides of `shape` right-aligned to `target` rank; 0 for broadcast (size-1) dims.</summary>
    internal static int[] PaddedStrides(int[] shape, int[] target)
    {
        int nd = target.Length, off = nd - shape.Length;
        var st = new int[nd];
        int stride = 1;
        for (int i = nd - 1; i >= 0; i--)
        {
            int d = i < off ? 1 : shape[i - off];
            st[i] = d == 1 ? 0 : stride;
            stride *= d;
        }
        return st;
    }

    /// <summary>Element-wise binary op with full numpy broadcasting.</summary>
    internal static ndarray Binary(ndarray a, ndarray b, BinOp op)
    {
        var kind = BinaryKind(op);
        if (SameShape(a._shape, b._shape))
        {
            var xa = a._data; var xb = b._data;
            var r = Alloc(xa.Length);
            Chunked(r.Length, (s, n) => SS(op, xa.AsSpan(s, n), xb.AsSpan(s, n), r.AsSpan(s, n)));
            return new ndarray(r, (int[])a._shape.Clone(), kind);
        }

        int[] rs = BroadcastShapes(a._shape, b._shape);
        if (b._data.Length == 1 && SameShape(rs, a._shape)) return BinaryScalarRight(a, b._data[0], op);
        if (a._data.Length == 1 && SameShape(rs, b._shape)) return BinaryScalarLeft(a._data[0], b, op);

        int nd = rs.Length;
        int size = Prod(rs);
        var res = Alloc(size);
        if (size == 0) return new ndarray(res, rs, kind);
        int[] sa = PaddedStrides(a._shape, rs), sb = PaddedStrides(b._shape, rs);

        // Find the longest trailing block in which each operand is either fully contiguous or constant.
        // cat: 3 = both vary, 1 = only a varies, 2 = only b varies.
        int d = nd - 1, cat = 0, inner = 1;
        while (d >= 0)
        {
            if (rs[d] == 1) { d--; continue; }
            int c = (sa[d] != 0 ? 1 : 0) | (sb[d] != 0 ? 2 : 0);
            if (cat == 0) cat = c;
            else if (c != cat) break;
            inner *= rs[d];
            d--;
        }
        if (cat == 0) cat = 3;
        int outerDims = d + 1;
        int outer = size / inner;
        var da = a._data; var db = b._data;

        ChunkedItems(outer, inner, (o0, cnt) =>
        {
            Span<int> idx = stackalloc int[Math.Max(1, outerDims)];
            int ao = 0, bo = 0, rem = o0;
            for (int k = outerDims - 1; k >= 0; k--)
            {
                idx[k] = rem % rs[k];
                rem /= rs[k];
                ao += idx[k] * sa[k];
                bo += idx[k] * sb[k];
            }
            for (int o = o0; o < o0 + cnt; o++)
            {
                var rspan = res.AsSpan(o * inner, inner);
                switch (cat)
                {
                    case 3: SS(op, da.AsSpan(ao, inner), db.AsSpan(bo, inner), rspan); break;
                    case 1: SV(op, da.AsSpan(ao, inner), db[bo], rspan); break;
                    default: VS(op, da[ao], db.AsSpan(bo, inner), rspan); break;
                }
                for (int k = outerDims - 1; k >= 0; k--)
                {
                    idx[k]++;
                    ao += sa[k];
                    bo += sb[k];
                    if (idx[k] < rs[k]) break;
                    ao -= sa[k] * rs[k];
                    bo -= sb[k] * rs[k];
                    idx[k] = 0;
                }
            }
        });
        return new ndarray(res, rs, kind);
    }

    /// <summary>Materializes `src` broadcast to `shape` as a contiguous buffer (may return src._data itself).</summary>
    internal static double[] BroadcastTo(ndarray src, int[] shape)
    {
        if (SameShape(src._shape, shape)) return src._data;
        var rs = BroadcastShapesForAssign(src._shape, shape);
        int size = Prod(rs);
        var res = Alloc(size);
        if (size == 0) return res;
        if (src._data.Length == 1) { res.AsSpan().Fill(src._data[0]); return res; }
        int nd = rs.Length;
        int[] st = PaddedStrides(src._shape, rs);
        int d = nd - 1, cat = -1, inner = 1;
        while (d >= 0)
        {
            if (rs[d] == 1) { d--; continue; }
            int c = st[d] != 0 ? 1 : 0;
            if (cat == -1) cat = c;
            else if (c != cat) break;
            inner *= rs[d];
            d--;
        }
        int outerDims = d + 1, outer = size / inner;
        var idx = new int[Math.Max(1, outerDims)];
        int so = 0;
        var sd = src._data;
        for (int o = 0; o < outer; o++)
        {
            var dst = res.AsSpan(o * inner, inner);
            if (cat == 1) sd.AsSpan(so, inner).CopyTo(dst); else dst.Fill(sd[so]);
            for (int k = outerDims - 1; k >= 0; k--)
            {
                idx[k]++;
                so += st[k];
                if (idx[k] < rs[k]) break;
                so -= st[k] * rs[k];
                idx[k] = 0;
            }
        }
        return res;
    }

    private static int[] BroadcastShapesForAssign(int[] src, int[] target)
    {
        // numpy allows leading size-1 dims of the source to be dropped when assigning.
        int s = 0;
        while (src.Length - s > target.Length && src[s] == 1) s++;
        var trimmed = s == 0 ? src : src[s..];
        int[] rs;
        try { rs = BroadcastShapes(trimmed, target); }
        catch (ArgumentException) { rs = null; }
        if (rs == null || !SameShape(rs, target))
            throw Err.Value($"could not broadcast input array from shape {Err.ShapeStr(src)} into shape {Err.ShapeStr(target)}");
        return rs;
    }

    // ------------------------------------------------------------------ reductions

    internal static int NormAxis(int axis, int ndim)
    {
        int a = axis < 0 ? axis + ndim : axis;
        if ((uint)a >= (uint)ndim) throw Err.Value($"axis {axis} is out of bounds for array of dimension {ndim}");
        return a;
    }

    internal static (int outer, int n, int inner) SplitAxis(int[] shape, int axis)
    {
        int outer = 1, inner = 1;
        for (int i = 0; i < axis; i++) outer *= shape[i];
        for (int i = axis + 1; i < shape.Length; i++) inner *= shape[i];
        return (outer, shape[axis], inner);
    }

    internal static int[] RemoveAxis(int[] shape, int axis, bool keepdims)
    {
        if (keepdims) { var k = (int[])shape.Clone(); k[axis] = 1; return k; }
        var r = new int[shape.Length - 1];
        for (int i = 0, j = 0; i < shape.Length; i++) if (i != axis) r[j++] = shape[i];
        return r;
    }

    private static string RedName(RedOp op) => op switch
    {
        RedOp.Max => "maximum",
        RedOp.Min => "minimum",
        RedOp.ArgMax => "argmax",
        RedOp.ArgMin => "argmin",
        _ => op.ToString().ToLowerInvariant(),
    };

    internal static double ReduceFull(RedOp op, ReadOnlySpan<double> x)
    {
        switch (op)
        {
            case RedOp.Sum: return x.Length == 0 ? 0.0 : TensorPrimitives.Sum(x);
            case RedOp.Mean: return x.Length == 0 ? double.NaN : TensorPrimitives.Sum(x) / x.Length;
            case RedOp.Prod: return x.Length == 0 ? 1.0 : TensorPrimitives.Product(x);
            case RedOp.Any:
                foreach (double v in x) if (v != 0) return 1.0;
                return 0.0;
            case RedOp.All:
                foreach (double v in x) if (v == 0) return 0.0;
                return 1.0;
        }
        if (x.Length == 0) throw Err.Value($"zero-size array to reduction operation {RedName(op)} which has no identity");
        return op switch
        {
            RedOp.Max => TensorPrimitives.Max(x),
            RedOp.Min => TensorPrimitives.Min(x),
            RedOp.ArgMax => TensorPrimitives.IndexOfMax(x),
            RedOp.ArgMin => TensorPrimitives.IndexOfMin(x),
            _ => throw new NotSupportedException(op.ToString()),
        };
    }

    internal static ndarray ReduceAxis(ndarray a, int axis, RedOp op, bool keepdims = false)
    {
        axis = NormAxis(axis, a.ndim);
        var (outer, n, inner) = SplitAxis(a._shape, axis);
        var outShape = RemoveAxis(a._shape, axis, keepdims);
        var res = new double[outer * inner];
        var kind = op is RedOp.Any or RedOp.All ? NdKind.Bool : NdKind.Float;
        if (res.Length == 0) return new ndarray(res, outShape, kind);
        var data = a._data;
        if (n == 0 && op is RedOp.Max or RedOp.Min or RedOp.ArgMax or RedOp.ArgMin)
            throw Err.Value($"zero-size array to reduction operation {RedName(op)} which has no identity");

        if (inner == 1)
        {
            ChunkedItems(outer, n, (o0, cnt) =>
            {
                for (int o = o0; o < o0 + cnt; o++) res[o] = ReduceFull(op, data.AsSpan(o * n, n));
            });
            return new ndarray(res, outShape, kind);
        }

        ChunkedItems(outer, (long)n * inner, (o0, cnt) =>
        {
            double[] best = op is RedOp.ArgMax or RedOp.ArgMin ? new double[inner] : null;
            for (int o = o0; o < o0 + cnt; o++)
            {
                var acc = res.AsSpan(o * inner, inner);
                int baseOff = o * n * inner;
                switch (op)
                {
                    case RedOp.Sum:
                    case RedOp.Mean:
                        acc.Clear();
                        for (int k = 0; k < n; k++) TensorPrimitives.Add(acc, data.AsSpan(baseOff + k * inner, inner), acc);
                        if (op == RedOp.Mean) TensorPrimitives.Divide(acc, n, acc);
                        break;
                    case RedOp.Prod:
                        acc.Fill(1.0);
                        for (int k = 0; k < n; k++) TensorPrimitives.Multiply(acc, data.AsSpan(baseOff + k * inner, inner), acc);
                        break;
                    case RedOp.Max:
                        data.AsSpan(baseOff, inner).CopyTo(acc);
                        for (int k = 1; k < n; k++) TensorPrimitives.Max(acc, data.AsSpan(baseOff + k * inner, inner), acc);
                        break;
                    case RedOp.Min:
                        data.AsSpan(baseOff, inner).CopyTo(acc);
                        for (int k = 1; k < n; k++) TensorPrimitives.Min(acc, data.AsSpan(baseOff + k * inner, inner), acc);
                        break;
                    case RedOp.Any:
                    case RedOp.All:
                        {
                            bool any = op == RedOp.Any;
                            for (int j = 0; j < inner; j++)
                            {
                                bool v = !any;
                                for (int k = 0; k < n; k++)
                                {
                                    bool nz = data[baseOff + k * inner + j] != 0;
                                    if (any && nz) { v = true; break; }
                                    if (!any && !nz) { v = false; break; }
                                }
                                acc[j] = v ? 1.0 : 0.0;
                            }
                            break;
                        }
                    case RedOp.ArgMax:
                    case RedOp.ArgMin:
                        {
                            bool isMax = op == RedOp.ArgMax;
                            data.AsSpan(baseOff, inner).CopyTo(best);
                            acc.Clear();
                            for (int k = 1; k < n; k++)
                            {
                                int off = baseOff + k * inner;
                                for (int j = 0; j < inner; j++)
                                {
                                    double v = data[off + j], b = best[j];
                                    if (double.IsNaN(b)) continue;
                                    if (double.IsNaN(v) || (isMax ? v > b : v < b)) { best[j] = v; acc[j] = k; }
                                }
                            }
                            break;
                        }
                }
            }
        });
        return new ndarray(res, outShape, kind);
    }

    /// <summary>Variance of a flat span (two-pass).</summary>
    internal static double VarFull(ReadOnlySpan<double> x, int ddof)
    {
        int n = x.Length;
        if (n - ddof <= 0) return double.NaN;
        double mean = TensorPrimitives.Sum(x) / n;
        const int Block = 4096;
        Span<double> tmp = n < Block ? stackalloc double[n] : new double[Block];
        double acc = 0;
        for (int s = 0; s < n; s += Block)
        {
            int len = Math.Min(Block, n - s);
            var t = tmp[..len];
            TensorPrimitives.Subtract(x.Slice(s, len), mean, t);
            acc += TensorPrimitives.SumOfSquares<double>(t);
        }
        return acc / (n - ddof);
    }

    internal static ndarray VarAxis(ndarray a, int axis, int ddof, bool std, bool keepdims)
    {
        axis = NormAxis(axis, a.ndim);
        var (outer, n, inner) = SplitAxis(a._shape, axis);
        var outShape = RemoveAxis(a._shape, axis, keepdims);
        var res = new double[outer * inner];
        var data = a._data;
        if (res.Length == 0) return new ndarray(res, outShape);
        if (inner == 1)
        {
            ChunkedItems(outer, n, (o0, cnt) =>
            {
                for (int o = o0; o < o0 + cnt; o++)
                {
                    double v = VarFull(data.AsSpan(o * n, n), ddof);
                    res[o] = std ? Math.Sqrt(v) : v;
                }
            });
            return new ndarray(res, outShape);
        }
        ChunkedItems(outer, (long)n * inner, (o0, cnt) =>
        {
            var mean = new double[inner];
            var tmp = new double[inner];
            for (int o = o0; o < o0 + cnt; o++)
            {
                int baseOff = o * n * inner;
                Array.Clear(mean);
                for (int k = 0; k < n; k++) TensorPrimitives.Add(mean, data.AsSpan(baseOff + k * inner, inner), mean);
                TensorPrimitives.Divide(mean, n, mean);
                var acc = res.AsSpan(o * inner, inner);
                acc.Clear();
                for (int k = 0; k < n; k++)
                {
                    TensorPrimitives.Subtract(data.AsSpan(baseOff + k * inner, inner), mean, tmp);
                    TensorPrimitives.MultiplyAdd(tmp, tmp, acc, acc);
                }
                double denom = n - ddof;
                if (denom <= 0) acc.Fill(double.NaN);
                else TensorPrimitives.Divide(acc, denom, acc);
                if (std) TensorPrimitives.Sqrt(acc, acc);
            }
        });
        return new ndarray(res, outShape);
    }

    /// <summary>Cumulative sum / product along an axis (axis == null: flattened).</summary>
    internal static ndarray Cumulative(ndarray a, int? axis, bool product)
    {
        if (axis == null)
        {
            var r = new double[a._data.Length];
            double acc = product ? 1.0 : 0.0;
            for (int i = 0; i < r.Length; i++) { acc = product ? acc * a._data[i] : acc + a._data[i]; r[i] = acc; }
            return new ndarray(r, new[] { r.Length });
        }
        int ax = NormAxis(axis.Value, a.ndim);
        var (outer, n, inner) = SplitAxis(a._shape, ax);
        var res = (double[])a._data.Clone();
        for (int o = 0; o < outer; o++)
        {
            int baseOff = o * n * inner;
            for (int k = 1; k < n; k++)
            {
                var prev = res.AsSpan(baseOff + (k - 1) * inner, inner);
                var cur = res.AsSpan(baseOff + k * inner, inner);
                if (product) TensorPrimitives.Multiply(prev, cur, cur); else TensorPrimitives.Add(prev, cur, cur);
            }
        }
        return new ndarray(res, (int[])a._shape.Clone());
    }

    // ------------------------------------------------------------------ layout

    /// <summary>Cache-blocked 2-D transpose: src is rows x cols, result is cols x rows.</summary>
    internal static double[] Transpose2D(double[] src, int rows, int cols)
    {
        var dst = Alloc(rows * cols);
        const int B = 32;
        int rowBlocks = (rows + B - 1) / B;
        ChunkedItems(rowBlocks, (long)B * cols, (b0, cnt) =>
        {
            for (int rb = b0; rb < b0 + cnt; rb++)
            {
                int i0 = rb * B, i1 = Math.Min(rows, i0 + B);
                for (int j0 = 0; j0 < cols; j0 += B)
                {
                    int j1 = Math.Min(cols, j0 + B);
                    for (int i = i0; i < i1; i++)
                    {
                        int srow = i * cols;
                        for (int j = j0; j < j1; j++) dst[j * rows + i] = src[srow + j];
                    }
                }
            }
        });
        return dst;
    }

    /// <summary>General axis permutation (result axis i = source axis perm[i]).</summary>
    internal static ndarray Permute(ndarray a, int[] perm)
    {
        int nd = a.ndim;
        if (perm.Length != nd) throw Err.Value("axes don't match array");
        var seen = new bool[nd];
        var p = new int[nd];
        for (int i = 0; i < nd; i++)
        {
            p[i] = NormAxis(perm[i], nd);
            if (seen[p[i]]) throw Err.Value("repeated axis in transpose");
            seen[p[i]] = true;
        }
        var newShape = new int[nd];
        for (int i = 0; i < nd; i++) newShape[i] = a._shape[p[i]];
        if (nd == 2 && p[0] == 1)
            return new ndarray(Transpose2D(a._data, a._shape[0], a._shape[1]), newShape, a._kind);

        var srcStrides = new int[nd];
        int st = 1;
        for (int i = nd - 1; i >= 0; i--) { srcStrides[i] = st; st *= a._shape[i]; }
        var permStrides = new int[nd];
        for (int i = 0; i < nd; i++) permStrides[i] = srcStrides[p[i]];
        var res = Alloc(a._data.Length);
        if (res.Length == 0) return new ndarray(res, newShape, a._kind);
        var idx = new int[nd];
        int so = 0;
        for (int o = 0; o < res.Length; o++)
        {
            res[o] = a._data[so];
            for (int k = nd - 1; k >= 0; k--)
            {
                idx[k]++;
                so += permStrides[k];
                if (idx[k] < newShape[k]) break;
                so -= permStrides[k] * newShape[k];
                idx[k] = 0;
            }
        }
        return new ndarray(res, newShape, a._kind);
    }
}
