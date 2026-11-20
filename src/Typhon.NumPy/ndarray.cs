// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;

namespace Typhon.Stdlib;

public static partial class numpy
{
    /// <summary>
    /// numpy.ndarray: an N-dimensional float64 array stored as a contiguous row-major <c>double[]</c>
    /// plus an <c>int[]</c> shape.
    /// <para>Semantics that differ from numpy:</para>
    /// <list type="bullet">
    /// <item>Storage is always float64 (dtype "float64"); comparison results are float64 0/1 flagged as
    /// dtype "bool" for printing and mask indexing.</item>
    /// <item>Slices (<c>a[1:3]</c>, <c>a[:, 0]</c>), rows (<c>a[i]</c> on 2-D) and <c>.T</c> are copies, not views.
    /// <c>reshape</c>/<c>ravel</c> do share memory with the source (like numpy views).</item>
    /// <item><c>==</c>/<c>!=</c> are reference equality; use <c>np.equal</c>/<c>__eq__</c> for element-wise.</item>
    /// </list>
    /// </summary>
    public sealed partial class ndarray : IEnumerable,
        IAdditionOperators<ndarray, ndarray, ndarray>, IAdditionOperators<ndarray, double, ndarray>,
        ISubtractionOperators<ndarray, ndarray, ndarray>, ISubtractionOperators<ndarray, double, ndarray>,
        IMultiplyOperators<ndarray, ndarray, ndarray>, IMultiplyOperators<ndarray, double, ndarray>,
        IDivisionOperators<ndarray, ndarray, ndarray>, IDivisionOperators<ndarray, double, ndarray>,
        IModulusOperators<ndarray, ndarray, ndarray>, IModulusOperators<ndarray, double, ndarray>,
        IUnaryNegationOperators<ndarray, ndarray>, IUnaryPlusOperators<ndarray, ndarray>
    {
        internal readonly double[] _data;
        internal readonly int[] _shape;
        internal readonly NdKind _kind;

        internal ndarray(double[] data, int[] shape, NdKind kind = NdKind.Float)
        {
            _data = data;
            _shape = shape;
            _kind = kind;
        }

        internal static ndarray Scalar0d(double v) => new(new[] { v }, Array.Empty<int>());

        internal ndarray WithKind(NdKind kind) => kind == _kind ? this : new ndarray(_data, _shape, kind);

        // ================================================================== attributes

        /// <summary>Shape (a copy; numpy returns a tuple).</summary>
        public int[] shape => (int[])_shape.Clone();

        /// <summary>Total number of elements.</summary>
        public int size => _data.Length;

        /// <summary>Number of dimensions.</summary>
        public int ndim => _shape.Length;

        /// <summary>"float64" (or "bool" for comparison/mask results).</summary>
        public string dtype => _kind == NdKind.Bool ? "bool" : "float64";

        public int itemsize => 8;

        public long nbytes => 8L * _data.Length;

        /// <summary>Transpose (a copy).</summary>
        public ndarray T => transpose();

        /// <summary>Length of the first axis (Python len()).</summary>
        public int Count => __len__();

        public int __len__()
        {
            if (_shape.Length == 0) throw Err.Type("len() of unsized object");
            return _shape[0];
        }

        /// <summary>Direct access to the underlying contiguous row-major storage (interop/perf).</summary>
        public Span<double> AsSpan() => _data;

        // ================================================================== index helpers

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int Norm(int i, int axis)
        {
            int n = _shape[axis];
            if ((uint)i < (uint)n) return i;
            return NormSlow(i, axis, n);
        }

        private static int NormSlow(int i, int axis, int n)
        {
            int k = i < 0 ? i + n : i;
            if ((uint)k >= (uint)n) throw Err.Index($"index {i} is out of bounds for axis {axis} with size {n}");
            return k;
        }

        private int Norm(Index i, int axis)
        {
            int n = _shape[axis];
            int k = i.IsFromEnd ? n - i.Value : i.Value;
            if ((uint)k >= (uint)n)
                throw Err.Index($"index {(i.IsFromEnd ? -i.Value : i.Value)} is out of bounds for axis {axis} with size {n}");
            return k;
        }

        /// <summary>Python slice semantics: clamp instead of throwing.</summary>
        private (int start, int len) RangeOf(Range r, int axis)
        {
            int n = _shape[axis];
            int s = r.Start.IsFromEnd ? n - r.Start.Value : r.Start.Value;
            int e = r.End.IsFromEnd ? n - r.End.Value : r.End.Value;
            s = Math.Clamp(s, 0, n);
            e = Math.Clamp(e, 0, n);
            return (s, Math.Max(0, e - s));
        }

        private void RequireRank(int k, string what)
        {
            if (_shape.Length < k)
                throw Err.Index($"too many indices for array: array is {_shape.Length}-dimensional, but {k} were indexed");
            if (what != null && _shape.Length != k)
                throw Err.Index($"{what} requires a {k}-dimensional array (got {_shape.Length}-dimensional); use slices, e.g. a[i, :]");
        }

        private object Item1D(int k) => _kind == NdKind.Bool ? _data[k] != 0 : _data[k];

        private int RowSize()
        {
            int inner = 1;
            for (int i = 1; i < _shape.Length; i++) inner *= _shape[i];
            return inner;
        }

        internal ndarray SubArray(int k)
        {
            int inner = RowSize();
            var sub = Kernels.Alloc(inner);
            Array.Copy(_data, k * inner, sub, 0, inner);
            return new ndarray(sub, _shape[1..], _kind);
        }

        // ---- general basic indexing over leading axes: each spec is either an int (axis dropped) or a range.

        private ndarray GetBasic(int[] starts, int[] lens, bool[] drop)
        {
            int nd = _shape.Length, ns = starts.Length;
            var outShape = new List<int>(nd);
            for (int i = 0; i < ns; i++) if (!drop[i]) outShape.Add(lens[i]);
            for (int i = ns; i < nd; i++) outShape.Add(_shape[i]);
            var os = outShape.ToArray();
            int size = Kernels.Prod(os);
            var res = Kernels.Alloc(size);
            if (size > 0) CopyBasic(starts, lens, res, toArray: true);
            return new ndarray(res, os, _kind);
        }

        private void SetBasic(int[] starts, int[] lens, bool[] drop, ndarray value)
        {
            int nd = _shape.Length, ns = starts.Length;
            var tShape = new List<int>(nd);
            for (int i = 0; i < ns; i++) if (!drop[i]) tShape.Add(lens[i]);
            for (int i = ns; i < nd; i++) tShape.Add(_shape[i]);
            var ts = tShape.ToArray();
            if (Kernels.Prod(ts) == 0) return;
            var src = Kernels.BroadcastTo(value, ts);
            CopyBasic(starts, lens, src, toArray: false);
        }

        /// <summary>Copies the selected region to (toArray) or from (!toArray) a contiguous buffer.</summary>
        private void CopyBasic(int[] starts, int[] lens, double[] buf, bool toArray)
        {
            int nd = _shape.Length, ns = starts.Length;
            var st = new int[nd];
            var len = new int[nd];
            int stride = 1;
            var strides = new int[nd];
            for (int i = nd - 1; i >= 0; i--) { strides[i] = stride; stride *= _shape[i]; }
            for (int i = 0; i < nd; i++)
            {
                st[i] = i < ns ? starts[i] : 0;
                len[i] = i < ns ? lens[i] : _shape[i];
            }
            // Trailing axes that are taken whole form a contiguous run together with the last partial axis.
            int last = nd - 1;
            while (last >= 0 && st[last] == 0 && len[last] == _shape[last]) last--;
            if (last < 0)
            {
                if (toArray) Array.Copy(_data, buf, buf.Length); else Array.Copy(buf, _data, buf.Length);
                return;
            }
            int block = len[last] * strides[last];
            int outerDims = last;
            var idx = new int[Math.Max(1, outerDims)];
            int src = 0;
            for (int i = 0; i <= last; i++) src += st[i] * strides[i];
            int pos = 0;
            int outer = 1;
            for (int i = 0; i < outerDims; i++) outer *= len[i];
            for (int o = 0; o < outer; o++)
            {
                if (toArray) Array.Copy(_data, src, buf, pos, block); else Array.Copy(buf, pos, _data, src, block);
                pos += block;
                for (int k = outerDims - 1; k >= 0; k--)
                {
                    idx[k]++;
                    src += strides[k];
                    if (idx[k] < len[k]) break;
                    src -= strides[k] * len[k];
                    idx[k] = 0;
                }
            }
        }

        // ================================================================== indexers

        /// <summary>
        /// a[i]: a double (or bool for masks) on a 1-D array, a row copy (ndarray) on an N-D array.
        /// Negative indices count from the end. Setter accepts a scalar or an array broadcastable to the row.
        /// </summary>
        public dynamic this[int i]
        {
            get
            {
                RequireRank(1, null);
                int k = Norm(i, 0);
                return _shape.Length == 1 ? Item1D(k) : SubArray(k);
            }
            set
            {
                RequireRank(1, null);
                SetRow(Norm(i, 0), (object)value);
            }
        }

        /// <summary>a[^k]: like this[int] with from-end indices.</summary>
        public dynamic this[Index i]
        {
            get
            {
                RequireRank(1, null);
                int k = Norm(i, 0);
                return _shape.Length == 1 ? Item1D(k) : SubArray(k);
            }
            set
            {
                RequireRank(1, null);
                SetRow(Norm(i, 0), (object)value);
            }
        }

        private void SetRow(int k, object value)
        {
            if (_shape.Length == 1) { _data[k] = ToDouble(value); return; }
            int inner = RowSize();
            if (TryScalar(value, out double s)) { _data.AsSpan(k * inner, inner).Fill(s); return; }
            var src = Kernels.BroadcastTo(asarray(value), _shape[1..]);
            Array.Copy(src, 0, _data, k * inner, inner);
        }

        /// <summary>a[i, j] on a 2-D array.</summary>
        public double this[int i, int j]
        {
            get
            {
                if (_shape.Length != 2) RequireRank(2, "a[i, j] (scalar access)");
                return _data[Norm(i, 0) * _shape[1] + Norm(j, 1)];
            }
            set
            {
                if (_shape.Length != 2) RequireRank(2, "a[i, j] (scalar access)");
                _data[Norm(i, 0) * _shape[1] + Norm(j, 1)] = value;
            }
        }

        public double this[Index i, Index j]
        {
            get { if (_shape.Length != 2) RequireRank(2, "a[i, j] (scalar access)"); return _data[Norm(i, 0) * _shape[1] + Norm(j, 1)]; }
            set { if (_shape.Length != 2) RequireRank(2, "a[i, j] (scalar access)"); _data[Norm(i, 0) * _shape[1] + Norm(j, 1)] = value; }
        }

        public double this[int i, Index j]
        {
            get { if (_shape.Length != 2) RequireRank(2, "a[i, j] (scalar access)"); return _data[Norm(i, 0) * _shape[1] + Norm(j, 1)]; }
            set { if (_shape.Length != 2) RequireRank(2, "a[i, j] (scalar access)"); _data[Norm(i, 0) * _shape[1] + Norm(j, 1)] = value; }
        }

        public double this[Index i, int j]
        {
            get { if (_shape.Length != 2) RequireRank(2, "a[i, j] (scalar access)"); return _data[Norm(i, 0) * _shape[1] + Norm(j, 1)]; }
            set { if (_shape.Length != 2) RequireRank(2, "a[i, j] (scalar access)"); _data[Norm(i, 0) * _shape[1] + Norm(j, 1)] = value; }
        }

        /// <summary>a[i, j, k] on a 3-D array.</summary>
        public double this[int i, int j, int k]
        {
            get { if (_shape.Length != 3) RequireRank(3, "a[i, j, k] (scalar access)"); return _data[(Norm(i, 0) * _shape[1] + Norm(j, 1)) * _shape[2] + Norm(k, 2)]; }
            set { if (_shape.Length != 3) RequireRank(3, "a[i, j, k] (scalar access)"); _data[(Norm(i, 0) * _shape[1] + Norm(j, 1)) * _shape[2] + Norm(k, 2)] = value; }
        }

        /// <summary>a[start:stop] along axis 0 (copy). Setter broadcasts the value into the slice.</summary>
        public ndarray this[Range r]
        {
            get { RequireRank(1, null); var (s, n) = RangeOf(r, 0); return GetBasic(new[] { s }, new[] { n }, new[] { false }); }
            set { RequireRank(1, null); var (s, n) = RangeOf(r, 0); SetBasic(new[] { s }, new[] { n }, new[] { false }, value); }
        }

        /// <summary>a[r, j], e.g. a[:, 0] (column copy).</summary>
        public ndarray this[Range r, int j]
        {
            get { RequireRank(2, null); var (s, n) = RangeOf(r, 0); return GetBasic(new[] { s, Norm(j, 1) }, new[] { n, 1 }, new[] { false, true }); }
            set { RequireRank(2, null); var (s, n) = RangeOf(r, 0); SetBasic(new[] { s, Norm(j, 1) }, new[] { n, 1 }, new[] { false, true }, value); }
        }

        public ndarray this[Range r, Index j]
        {
            get { RequireRank(2, null); var (s, n) = RangeOf(r, 0); return GetBasic(new[] { s, Norm(j, 1) }, new[] { n, 1 }, new[] { false, true }); }
            set { RequireRank(2, null); var (s, n) = RangeOf(r, 0); SetBasic(new[] { s, Norm(j, 1) }, new[] { n, 1 }, new[] { false, true }, value); }
        }

        /// <summary>a[i, r], e.g. a[0, :] (row copy).</summary>
        public ndarray this[int i, Range r]
        {
            get { RequireRank(2, null); var (s, n) = RangeOf(r, 1); return GetBasic(new[] { Norm(i, 0), s }, new[] { 1, n }, new[] { true, false }); }
            set { RequireRank(2, null); var (s, n) = RangeOf(r, 1); SetBasic(new[] { Norm(i, 0), s }, new[] { 1, n }, new[] { true, false }, value); }
        }

        public ndarray this[Index i, Range r]
        {
            get { RequireRank(2, null); var (s, n) = RangeOf(r, 1); return GetBasic(new[] { Norm(i, 0), s }, new[] { 1, n }, new[] { true, false }); }
            set { RequireRank(2, null); var (s, n) = RangeOf(r, 1); SetBasic(new[] { Norm(i, 0), s }, new[] { 1, n }, new[] { true, false }, value); }
        }

        /// <summary>a[r1, r2], e.g. a[1:3, 1:3] (copy).</summary>
        public ndarray this[Range r1, Range r2]
        {
            get { RequireRank(2, null); var (s1, n1) = RangeOf(r1, 0); var (s2, n2) = RangeOf(r2, 1); return GetBasic(new[] { s1, s2 }, new[] { n1, n2 }, new[] { false, false }); }
            set { RequireRank(2, null); var (s1, n1) = RangeOf(r1, 0); var (s2, n2) = RangeOf(r2, 1); SetBasic(new[] { s1, s2 }, new[] { n1, n2 }, new[] { false, false }, value); }
        }

        /// <summary>
        /// a[mask] with a boolean array (e.g. a[a &gt; 0]) selects matching elements as a 1-D array;
        /// a[idx] with an integer array (e.g. a[[0, 2]]) gathers along axis 0.
        /// </summary>
        public ndarray this[ndarray index]
        {
            get
            {
                if (index._kind == NdKind.Bool) return MaskGet(index);
                return Take(index);
            }
            set
            {
                if (index._kind == NdKind.Bool) { MaskSet(index, value); return; }
                PutRows(index, value);
            }
        }

        private void CheckMask(ndarray mask)
        {
            if (!Kernels.SameShape(mask._shape, _shape))
                throw Err.Index($"boolean index did not match indexed array: shape {Err.ShapeStr(mask._shape)} vs {Err.ShapeStr(_shape)}");
        }

        private ndarray MaskGet(ndarray mask)
        {
            if (mask.ndim == 1 && _shape.Length > 1 && mask._shape[0] == _shape[0])
            {
                // select rows
                var rows = new List<int>();
                for (int i = 0; i < mask._data.Length; i++) if (mask._data[i] != 0) rows.Add(i);
                return TakeRows(rows);
            }
            CheckMask(mask);
            int count = 0;
            foreach (double m in mask._data) if (m != 0) count++;
            var res = Kernels.Alloc(count);
            int p = 0;
            for (int i = 0; i < _data.Length; i++) if (mask._data[i] != 0) res[p++] = _data[i];
            return new ndarray(res, new[] { count }, _kind);
        }

        private void MaskSet(ndarray mask, ndarray value)
        {
            CheckMask(mask);
            if (value._data.Length == 1)
            {
                double v = value._data[0];
                for (int i = 0; i < _data.Length; i++) if (mask._data[i] != 0) _data[i] = v;
                return;
            }
            int p = 0;
            int count = 0;
            foreach (double m in mask._data) if (m != 0) count++;
            if (value._data.Length != count)
                throw Err.Value($"NumPy boolean array indexing assignment cannot assign {value._data.Length} input values to the {count} output values where the mask is true");
            for (int i = 0; i < _data.Length; i++) if (mask._data[i] != 0) _data[i] = value._data[p++];
        }

        private ndarray Take(ndarray index)
        {
            RequireRank(1, null);
            var rows = new List<int>(index._data.Length);
            foreach (double v in index._data) rows.Add(Norm(ToIndex(v), 0));
            var r = TakeRows(rows);
            // result shape: index.shape + shape[1:]
            var shp = new int[index.ndim + _shape.Length - 1];
            index._shape.CopyTo(shp, 0);
            for (int i = 1; i < _shape.Length; i++) shp[index.ndim + i - 1] = _shape[i];
            return new ndarray(r._data, shp, _kind);
        }

        private ndarray TakeRows(List<int> rows)
        {
            int inner = RowSize();
            var res = Kernels.Alloc(rows.Count * inner);
            for (int i = 0; i < rows.Count; i++) Array.Copy(_data, rows[i] * inner, res, i * inner, inner);
            var shp = (int[])_shape.Clone();
            shp[0] = rows.Count;
            return new ndarray(res, shp, _kind);
        }

        private void PutRows(ndarray index, ndarray value)
        {
            RequireRank(1, null);
            int inner = RowSize();
            var tshape = new int[index.ndim + _shape.Length - 1];
            index._shape.CopyTo(tshape, 0);
            for (int i = 1; i < _shape.Length; i++) tshape[index.ndim + i - 1] = _shape[i];
            var src = Kernels.BroadcastTo(value, tshape);
            for (int i = 0; i < index._data.Length; i++)
                Array.Copy(src, i * inner, _data, Norm(ToIndex(index._data[i]), 0) * inner, inner);
        }

        private static int ToIndex(double v)
        {
            if (v != Math.Floor(v) || double.IsInfinity(v)) throw Err.Index("arrays used as indices must be of integer (or boolean) type");
            return (int)v;
        }

        // ================================================================== iteration

        /// <summary>Iterates over axis 0: doubles for a 1-D array, row copies (ndarray) otherwise.</summary>
        public IEnumerator<dynamic> GetEnumerator()
        {
            if (_shape.Length == 0) throw Err.Type("iteration over a 0-d array");
            int n = _shape[0];
            if (_shape.Length == 1)
            {
                for (int i = 0; i < n; i++) yield return Item1D(i);
            }
            else
            {
                for (int i = 0; i < n; i++) yield return SubArray(i);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public bool __contains__(double value)
        {
            foreach (double v in _data) if (v == value) return true;
            return false;
        }

        // ================================================================== operators

        public static ndarray operator +(ndarray a, ndarray b) => Kernels.Binary(a, b, BinOp.Add);
        public static ndarray operator +(ndarray a, double b) => Kernels.BinaryScalarRight(a, b, BinOp.Add);
        public static ndarray operator +(double a, ndarray b) => Kernels.BinaryScalarLeft(a, b, BinOp.Add);

        public static ndarray operator -(ndarray a, ndarray b) => Kernels.Binary(a, b, BinOp.Sub);
        public static ndarray operator -(ndarray a, double b) => Kernels.BinaryScalarRight(a, b, BinOp.Sub);
        public static ndarray operator -(double a, ndarray b) => Kernels.BinaryScalarLeft(a, b, BinOp.Sub);

        public static ndarray operator *(ndarray a, ndarray b) => Kernels.Binary(a, b, BinOp.Mul);
        public static ndarray operator *(ndarray a, double b) => Kernels.BinaryScalarRight(a, b, BinOp.Mul);
        public static ndarray operator *(double a, ndarray b) => Kernels.BinaryScalarLeft(a, b, BinOp.Mul);

        public static ndarray operator /(ndarray a, ndarray b) => Kernels.Binary(a, b, BinOp.Div);
        public static ndarray operator /(ndarray a, double b) => Kernels.BinaryScalarRight(a, b, BinOp.Div);
        public static ndarray operator /(double a, ndarray b) => Kernels.BinaryScalarLeft(a, b, BinOp.Div);

        /// <summary>Python modulo (result has the sign of the divisor).</summary>
        public static ndarray operator %(ndarray a, ndarray b) => Kernels.Binary(a, b, BinOp.Mod);
        public static ndarray operator %(ndarray a, double b) => Kernels.BinaryScalarRight(a, b, BinOp.Mod);
        public static ndarray operator %(double a, ndarray b) => Kernels.BinaryScalarLeft(a, b, BinOp.Mod);

        public static ndarray operator -(ndarray a) => Kernels.Unary(a, UnOp.Neg);
        public static ndarray operator +(ndarray a) => a.copy();

        // Comparisons return dtype=bool masks (usable as a[mask]).
        public static ndarray operator <(ndarray a, ndarray b) => Kernels.Binary(a, b, BinOp.Lt);
        public static ndarray operator <(ndarray a, double b) => Kernels.BinaryScalarRight(a, b, BinOp.Lt);
        public static ndarray operator <(double a, ndarray b) => Kernels.BinaryScalarLeft(a, b, BinOp.Lt);
        public static ndarray operator >(ndarray a, ndarray b) => Kernels.Binary(a, b, BinOp.Gt);
        public static ndarray operator >(ndarray a, double b) => Kernels.BinaryScalarRight(a, b, BinOp.Gt);
        public static ndarray operator >(double a, ndarray b) => Kernels.BinaryScalarLeft(a, b, BinOp.Gt);
        public static ndarray operator <=(ndarray a, ndarray b) => Kernels.Binary(a, b, BinOp.Le);
        public static ndarray operator <=(ndarray a, double b) => Kernels.BinaryScalarRight(a, b, BinOp.Le);
        public static ndarray operator <=(double a, ndarray b) => Kernels.BinaryScalarLeft(a, b, BinOp.Le);
        public static ndarray operator >=(ndarray a, ndarray b) => Kernels.Binary(a, b, BinOp.Ge);
        public static ndarray operator >=(ndarray a, double b) => Kernels.BinaryScalarRight(a, b, BinOp.Ge);
        public static ndarray operator >=(double a, ndarray b) => Kernels.BinaryScalarLeft(a, b, BinOp.Ge);

        // Logical ops on masks: (a > 0) & (a < 1)
        public static ndarray operator &(ndarray a, ndarray b) => Kernels.Binary(a, b, BinOp.And);
        public static ndarray operator |(ndarray a, ndarray b) => Kernels.Binary(a, b, BinOp.Or);
        public static ndarray operator ^(ndarray a, ndarray b) => Kernels.Binary(a, b, BinOp.Xor);

        public static ndarray operator ~(ndarray a)
        {
            if (a._kind != NdKind.Bool) throw Err.Type("ufunc 'invert' not supported for float64 arrays");
            return Kernels.Unary(a, UnOp.Not);
        }

        // ================================================================== conversions

        /// <summary>A Python float becomes a 0-d array where an array is expected (np.maximum(a, 0.0), a[1:3] = 0.0).</summary>
        public static implicit operator ndarray(double value) => Scalar0d(value);

        // Python list literals (emitted as List<T>) are array_like.
        public static implicit operator ndarray(List<double> list) => FromDoubles(list);
        public static implicit operator ndarray(List<int> list) => FromInts(list);
        public static implicit operator ndarray(List<long> list) => FromObject(list);
        public static implicit operator ndarray(List<float> list) => FromObject(list);
        public static implicit operator ndarray(List<bool> list) => FromObject(list);
        public static implicit operator ndarray(List<object> list) => FromObject(list);
        public static implicit operator ndarray(List<List<double>> list) => FromObject(list);
        public static implicit operator ndarray(List<List<int>> list) => FromObject(list);
        public static implicit operator ndarray(double[] values) => new((double[])values.Clone(), new[] { values.Length });

        /// <summary>float(a) for a size-1 array.</summary>
        public static explicit operator double(ndarray a) => a.item();

        // ================================================================== Python dunder protocol

        public ndarray __matmul__(ndarray other) => matmul(this, other);
        public ndarray __rmatmul__(ndarray other) => matmul(other, this);

        /// <summary>a ** exponent (fast paths: 2 = square, 0.5 = sqrt, 1, -1, 0, 3).</summary>
        public ndarray __pow__(double exponent) => Kernels.BinaryScalarRight(this, exponent, BinOp.Pow);
        public ndarray __pow__(ndarray exponent) => Kernels.Binary(this, exponent, BinOp.Pow);
        public ndarray __rpow__(double @base) => Kernels.BinaryScalarLeft(@base, this, BinOp.Pow);

        public ndarray __add__(ndarray other) => this + other;
        public ndarray __add__(double other) => this + other;
        public ndarray __radd__(double other) => other + this;
        public ndarray __sub__(ndarray other) => this - other;
        public ndarray __sub__(double other) => this - other;
        public ndarray __rsub__(double other) => other - this;
        public ndarray __mul__(ndarray other) => this * other;
        public ndarray __mul__(double other) => this * other;
        public ndarray __rmul__(double other) => other * this;
        public ndarray __truediv__(ndarray other) => this / other;
        public ndarray __truediv__(double other) => this / other;
        public ndarray __rtruediv__(double other) => other / this;
        public ndarray __floordiv__(ndarray other) => Kernels.Binary(this, other, BinOp.FloorDiv);
        public ndarray __floordiv__(double other) => Kernels.BinaryScalarRight(this, other, BinOp.FloorDiv);
        public ndarray __rfloordiv__(double other) => Kernels.BinaryScalarLeft(other, this, BinOp.FloorDiv);
        public ndarray __mod__(ndarray other) => this % other;
        public ndarray __mod__(double other) => this % other;
        public ndarray __rmod__(double other) => other % this;
        public ndarray __neg__() => -this;
        public ndarray __pos__() => copy();
        public ndarray __abs__() => Kernels.Unary(this, UnOp.Abs);
        public ndarray __eq__(ndarray other) => Kernels.Binary(this, other, BinOp.Eq);
        public ndarray __eq__(double other) => Kernels.BinaryScalarRight(this, other, BinOp.Eq);
        public ndarray __ne__(ndarray other) => Kernels.Binary(this, other, BinOp.Ne);
        public ndarray __ne__(double other) => Kernels.BinaryScalarRight(this, other, BinOp.Ne);
        public ndarray __lt__(ndarray other) => this < other;
        public ndarray __lt__(double other) => this < other;
        public ndarray __le__(ndarray other) => this <= other;
        public ndarray __le__(double other) => this <= other;
        public ndarray __gt__(ndarray other) => this > other;
        public ndarray __gt__(double other) => this > other;
        public ndarray __ge__(ndarray other) => this >= other;
        public ndarray __ge__(double other) => this >= other;
        public double __float__() => item();

        public bool __bool__()
        {
            if (_data.Length != 1)
                throw Err.Value("The truth value of an array with more than one element is ambiguous. Use a.any() or a.all()");
            return _data[0] != 0;
        }

        public override string ToString() => ArrayPrinter.Str(this);
        public string __str__() => ArrayPrinter.Str(this);
        public string __repr__() => ArrayPrinter.Repr(this);

        // ================================================================== reductions

        public double sum() => Kernels.ReduceFull(RedOp.Sum, _data);
        public ndarray sum(int axis) => Kernels.ReduceAxis(this, axis, RedOp.Sum);
        /// <summary>sum(axis=None|int, keepdims=...): double when axis is null (and !keepdims), else ndarray.</summary>
        public dynamic sum(int? axis = null, bool keepdims = false) => Reduce(axis, keepdims, RedOp.Sum);

        public double mean() => Kernels.ReduceFull(RedOp.Mean, _data);
        public ndarray mean(int axis) => Kernels.ReduceAxis(this, axis, RedOp.Mean);
        public dynamic mean(int? axis = null, bool keepdims = false) => Reduce(axis, keepdims, RedOp.Mean);

        public double prod() => Kernels.ReduceFull(RedOp.Prod, _data);
        public ndarray prod(int axis) => Kernels.ReduceAxis(this, axis, RedOp.Prod);
        public dynamic prod(int? axis = null, bool keepdims = false) => Reduce(axis, keepdims, RedOp.Prod);

        public double min() => Kernels.ReduceFull(RedOp.Min, _data);
        public ndarray min(int axis) => Kernels.ReduceAxis(this, axis, RedOp.Min);
        public dynamic min(int? axis = null, bool keepdims = false) => Reduce(axis, keepdims, RedOp.Min);

        public double max() => Kernels.ReduceFull(RedOp.Max, _data);
        public ndarray max(int axis) => Kernels.ReduceAxis(this, axis, RedOp.Max);
        public dynamic max(int? axis = null, bool keepdims = false) => Reduce(axis, keepdims, RedOp.Max);

        /// <summary>Index of the maximum in the flattened array.</summary>
        public int argmax() => (int)Kernels.ReduceFull(RedOp.ArgMax, _data);
        public ndarray argmax(int axis) => Kernels.ReduceAxis(this, axis, RedOp.ArgMax);

        public int argmin() => (int)Kernels.ReduceFull(RedOp.ArgMin, _data);
        public ndarray argmin(int axis) => Kernels.ReduceAxis(this, axis, RedOp.ArgMin);

        public double std() => Math.Sqrt(Kernels.VarFull(_data, 0));
        public ndarray std(int axis) => Kernels.VarAxis(this, axis, 0, std: true, keepdims: false);
        public dynamic std(int? axis = null, int ddof = 0, bool keepdims = false) => VarImpl(axis, ddof, true, keepdims);

        public double var() => Kernels.VarFull(_data, 0);
        public ndarray var(int axis) => Kernels.VarAxis(this, axis, 0, std: false, keepdims: false);
        public dynamic var(int? axis = null, int ddof = 0, bool keepdims = false) => VarImpl(axis, ddof, false, keepdims);

        public bool any() => Kernels.ReduceFull(RedOp.Any, _data) != 0;
        public ndarray any(int axis) => Kernels.ReduceAxis(this, axis, RedOp.Any);
        public bool all() => Kernels.ReduceFull(RedOp.All, _data) != 0;
        public ndarray all(int axis) => Kernels.ReduceAxis(this, axis, RedOp.All);

        public ndarray cumsum() => Kernels.Cumulative(this, null, false);
        public ndarray cumsum(int axis) => Kernels.Cumulative(this, axis, false);
        public ndarray cumprod() => Kernels.Cumulative(this, null, true);
        public ndarray cumprod(int axis) => Kernels.Cumulative(this, axis, true);

        internal dynamic Reduce(int? axis, bool keepdims, RedOp op)
        {
            if (axis.HasValue) return Kernels.ReduceAxis(this, axis.Value, op, keepdims);
            double v = Kernels.ReduceFull(op, _data);
            if (!keepdims) return v;
            var shp = new int[_shape.Length];
            Array.Fill(shp, 1);
            return new ndarray(new[] { v }, shp);
        }

        internal dynamic VarImpl(int? axis, int ddof, bool std, bool keepdims)
        {
            if (axis.HasValue) return Kernels.VarAxis(this, axis.Value, ddof, std, keepdims);
            double v = Kernels.VarFull(_data, ddof);
            if (std) v = Math.Sqrt(v);
            if (!keepdims) return v;
            var shp = new int[_shape.Length];
            Array.Fill(shp, 1);
            return new ndarray(new[] { v }, shp);
        }

        // ================================================================== shape manipulation

        /// <summary>Reshape (shares memory with this array). One dimension may be -1.</summary>
        public ndarray reshape(params int[] shape) => ReshapeImpl(shape);

        public ndarray reshape(NumPyShape shape) => ReshapeImpl(shape.Dims);

        internal ndarray ReshapeImpl(int[] shape)
        {
            var s = (int[])shape.Clone();
            int unknown = -1;
            long known = 1;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == -1)
                {
                    if (unknown >= 0) throw Err.Value("can only specify one unknown dimension");
                    unknown = i;
                }
                else if (s[i] < 0) throw Err.Value("negative dimensions not allowed");
                else known *= s[i];
            }
            if (unknown >= 0)
            {
                if (known == 0 || _data.Length % known != 0)
                    throw Err.Value($"cannot reshape array of size {_data.Length} into shape {Err.ShapeStr(shape)}");
                s[unknown] = (int)(_data.Length / known);
            }
            else if (known != _data.Length)
                throw Err.Value($"cannot reshape array of size {_data.Length} into shape {Err.ShapeStr(shape)}");
            return new ndarray(_data, s, _kind);
        }

        /// <summary>1-D copy.</summary>
        public ndarray flatten() => new((double[])_data.Clone(), new[] { _data.Length }, _kind);

        /// <summary>1-D view (shares memory).</summary>
        public ndarray ravel() => new(_data, new[] { _data.Length }, _kind);

        public ndarray copy() => new((double[])_data.Clone(), (int[])_shape.Clone(), _kind);

        /// <summary>Reverses the axes (copy). 1-D arrays are returned as a copy.</summary>
        public ndarray transpose()
        {
            if (_shape.Length < 2) return copy();
            var perm = new int[_shape.Length];
            for (int i = 0; i < perm.Length; i++) perm[i] = perm.Length - 1 - i;
            return Kernels.Permute(this, perm);
        }

        public ndarray transpose(params int[] axes) => axes == null || axes.Length == 0 ? transpose() : Kernels.Permute(this, axes);

        public ndarray swapaxes(int axis1, int axis2)
        {
            var perm = new int[_shape.Length];
            for (int i = 0; i < perm.Length; i++) perm[i] = i;
            int a1 = Kernels.NormAxis(axis1, _shape.Length), a2 = Kernels.NormAxis(axis2, _shape.Length);
            (perm[a1], perm[a2]) = (perm[a2], perm[a1]);
            return Kernels.Permute(this, perm);
        }

        /// <summary>Removes size-1 dimensions (shares memory).</summary>
        public ndarray squeeze()
        {
            var s = new List<int>();
            foreach (int d in _shape) if (d != 1) s.Add(d);
            return new ndarray(_data, s.ToArray(), _kind);
        }

        // ================================================================== misc methods

        /// <summary>Matrix/dot product; double for 1-D . 1-D, otherwise ndarray.</summary>
        public dynamic dot(ndarray other) => numpy.dot(this, other);

        /// <summary>Copy converted to the given dtype. Storage stays float64; integer dtypes truncate, bool maps to 0/1.</summary>
        public ndarray astype(object dtype)
        {
            switch (DtypeOf(dtype))
            {
                case DtypeCode.Int:
                    var r = Kernels.Alloc(_data.Length);
                    TensorPrimitives.Truncate<double>(_data, r);
                    return new ndarray(r, (int[])_shape.Clone());
                case DtypeCode.Bool:
                    return Kernels.BinaryScalarRight(this, 0.0, BinOp.Ne);
                default:
                    return new ndarray((double[])_data.Clone(), (int[])_shape.Clone());
            }
        }

        /// <summary>The single element as a Python float (size-1 arrays only).</summary>
        public double item()
        {
            if (_data.Length != 1) throw Err.Value("can only convert an array of size 1 to a Python scalar");
            return _data[0];
        }

        /// <summary>Element at a flat index.</summary>
        public double item(int index)
        {
            int n = _data.Length;
            int k = index < 0 ? index + n : index;
            if ((uint)k >= (uint)n) throw Err.Index($"index {index} is out of bounds for size {n}");
            return _data[k];
        }

        public void fill(double value) => _data.AsSpan().Fill(value);

        /// <summary>Nested lists: List&lt;double&gt; for 1-D, List&lt;List&lt;double&gt;&gt; for 2-D, List&lt;object&gt; deeper; double for 0-d.</summary>
        public dynamic tolist()
        {
            if (_shape.Length == 0) return _data[0];
            if (_shape.Length == 1)
            {
                if (_kind == NdKind.Bool)
                {
                    var bl = new List<bool>(_data.Length);
                    foreach (double v in _data) bl.Add(v != 0);
                    return bl;
                }
                return new List<double>(_data);
            }
            if (_shape.Length == 2 && _kind == NdKind.Float)
            {
                int rows = _shape[0], cols = _shape[1];
                var outer = new List<List<double>>(rows);
                for (int i = 0; i < rows; i++) outer.Add(new List<double>(new ArraySegment<double>(_data, i * cols, cols)));
                return outer;
            }
            var res = new List<object>(_shape[0]);
            for (int i = 0; i < _shape[0]; i++) res.Add((object)SubArray(i).tolist());
            return res;
        }

        /// <summary>Round to the given number of decimals (half to even).</summary>
        public ndarray round(int decimals = 0) => numpy.round(this, decimals);

        public ndarray clip(double a_min, double a_max) => numpy.clip(this, a_min, a_max);

        /// <summary>In-place sort along the last axis (NaNs last).</summary>
        public void sort()
        {
            if (_shape.Length == 0) throw Err.Value("Cannot sort a 0-d array");
            int n = _shape[^1];
            for (int o = 0; o + n <= _data.Length && n > 0; o += n) _data.AsSpan(o, n).Sort(NaNLastComparison);
        }

        public ndarray argsort() => numpy.argsort(this);

        public ndarray nonzero_flat()
        {
            var idx = new List<double>();
            for (int i = 0; i < _data.Length; i++) if (_data[i] != 0) idx.Add(i);
            return new ndarray(idx.ToArray(), new[] { idx.Count });
        }

        internal static readonly Comparison<double> NaNLastComparison = (x, y) =>
        {
            bool nx = double.IsNaN(x), ny = double.IsNaN(y);
            if (nx || ny) return nx == ny ? 0 : (nx ? 1 : -1);
            return x.CompareTo(y);
        };

        public double trace() => numpy.trace(this);

        public ndarray diagonal() => numpy.diag(this);
    }
}
