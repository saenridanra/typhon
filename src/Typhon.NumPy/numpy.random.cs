// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Typhon.Stdlib;

/// <summary>xoshiro256** PRNG (seeded through splitmix64). Deterministic for a given seed; not NumPy's stream.</summary>
internal sealed class Xoshiro256
{
    private ulong _s0, _s1, _s2, _s3;
    private bool _hasSpare;
    private double _spare;

    public Xoshiro256(ulong seed) => Seed(seed);

    public void Seed(ulong seed)
    {
        ulong x = seed;
        _s0 = SplitMix(ref x); _s1 = SplitMix(ref x); _s2 = SplitMix(ref x); _s3 = SplitMix(ref x);
        _hasSpare = false;
    }

    private static ulong SplitMix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong NextUInt64()
    {
        ulong result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0; _s3 ^= _s1; _s1 ^= _s2; _s0 ^= _s3;
        _s2 ^= t;
        _s3 = BitOperations.RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>Uniform in [0, 1) with 53 random bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>Standard normal (Marsaglia polar method).</summary>
    public double NextGaussian()
    {
        if (_hasSpare) { _hasSpare = false; return _spare; }
        double u, v, s;
        do
        {
            u = 2.0 * NextDouble() - 1.0;
            v = 2.0 * NextDouble() - 1.0;
            s = u * u + v * v;
        } while (s >= 1.0 || s == 0.0);
        double f = Math.Sqrt(-2.0 * Math.Log(s) / s);
        _spare = v * f;
        _hasSpare = true;
        return u * f;
    }

    /// <summary>Uniform integer in [0, bound) without modulo bias (Lemire).</summary>
    public ulong NextBounded(ulong bound)
    {
        if (bound == 0) return 0;
        ulong x = NextUInt64();
        UInt128 m = (UInt128)x * bound;
        ulong l = (ulong)m;
        if (l < bound)
        {
            ulong t = (0 - bound) % bound;
            while (l < t)
            {
                x = NextUInt64();
                m = (UInt128)x * bound;
                l = (ulong)m;
            }
        }
        return (ulong)(m >> 64);
    }

    public long NextInt(long low, long high)
    {
        if (high <= low) throw Err.Value("high <= low");
        return low + (long)NextBounded((ulong)(high - low));
    }

    public void FillUniform(Span<double> d, double low = 0.0, double high = 1.0)
    {
        double scale = high - low;
        if (low == 0.0 && scale == 1.0) for (int i = 0; i < d.Length; i++) d[i] = NextDouble();
        else for (int i = 0; i < d.Length; i++) d[i] = low + scale * NextDouble();
    }

    public void FillNormal(Span<double> d, double loc = 0.0, double scale = 1.0)
    {
        for (int i = 0; i < d.Length; i++) d[i] = loc + scale * NextGaussian();
    }
}

public static partial class numpy
{
    /// <summary>
    /// numpy.random (legacy global-state API) plus default_rng(). Deterministic for a given seed,
    /// but the stream differs from NumPy's Mersenne Twister / PCG64.
    /// <para>NOTE: C# forbids a member named like its enclosing type, so <c>np.random.random(...)</c>
    /// cannot exist here; use the numpy aliases <c>random_sample</c>/<c>sample</c>/<c>ranf</c>.</para>
    /// </summary>
    public static class random
    {
        internal static readonly Xoshiro256 Rng = new((ulong)Environment.TickCount64 ^ (ulong)Environment.ProcessId << 32);

        private static ndarray Make(int[] shape, Action<Span<double>> fill)
        {
            var s = (int[])shape.Clone();
            var d = new double[Kernels.Prod(s)];
            fill(d);
            return new ndarray(d, s);
        }

        /// <summary>Reseed the global generator.</summary>
        public static void seed(int seed) => Rng.Seed(unchecked((ulong)seed));
        public static void seed(long seed) => Rng.Seed(unchecked((ulong)seed));
        public static void seed() => Rng.Seed((ulong)DateTime.UtcNow.Ticks);

        /// <summary>Uniform [0, 1) scalar.</summary>
        public static double rand() => Rng.NextDouble();
        /// <summary>Uniform [0, 1) array of the given dimensions, e.g. rand(500, 500).</summary>
        public static ndarray rand(params int[] shape) => Make(shape, d => Rng.FillUniform(d));

        /// <summary>Standard normal scalar.</summary>
        public static double randn() => Rng.NextGaussian();
        /// <summary>Standard normal array of the given dimensions, e.g. randn(3, 4).</summary>
        public static ndarray randn(params int[] shape) => Make(shape, d => Rng.FillNormal(d));

        /// <summary>np.random.random() / random_sample(): uniform [0, 1).</summary>
        public static double random_sample() => Rng.NextDouble();
        /// <summary>np.random.random(size) / random_sample(size), size = int or tuple.</summary>
        public static ndarray random_sample(NumPyShape size) => Make(size.Dims, d => Rng.FillUniform(d));
        public static double sample() => random_sample();
        public static ndarray sample(NumPyShape size) => random_sample(size);
        public static double ranf() => random_sample();
        public static ndarray ranf(NumPyShape size) => random_sample(size);

        /// <summary>Uniform [low, high) scalar.</summary>
        public static double uniform(double low = 0.0, double high = 1.0) => low + (high - low) * Rng.NextDouble();
        /// <summary>Uniform [low, high) array.</summary>
        public static ndarray uniform(double low, double high, NumPyShape size) => Make(size.Dims, d => Rng.FillUniform(d, low, high));

        /// <summary>Normal(loc, scale) scalar.</summary>
        public static double normal(double loc = 0.0, double scale = 1.0) => loc + scale * Rng.NextGaussian();
        /// <summary>Normal(loc, scale) array.</summary>
        public static ndarray normal(double loc, double scale, NumPyShape size) => Make(size.Dims, d => Rng.FillNormal(d, loc, scale));

        public static double standard_normal() => Rng.NextGaussian();
        public static ndarray standard_normal(NumPyShape size) => Make(size.Dims, d => Rng.FillNormal(d));

        /// <summary>randint(high): integer in [0, high).</summary>
        public static int randint(int low) => (int)Rng.NextInt(0, low);
        /// <summary>randint(low, high): integer in [low, high).</summary>
        public static int randint(int low, int high) => (int)Rng.NextInt(low, high);
        /// <summary>randint(low, high, size): float64 array holding integers in [low, high).</summary>
        public static ndarray randint(int low, int high, NumPyShape size) =>
            Make(size.Dims, d => { for (int i = 0; i < d.Length; i++) d[i] = Rng.NextInt(low, high); });
        /// <summary>randint(high, size=...): integers in [0, high).</summary>
        public static ndarray randint(int low, NumPyShape size) => randint(0, low, size);

        /// <summary>Random element of a 1-D array.</summary>
        public static double choice(ndarray a)
        {
            if (a._data.Length == 0) throw Err.Value("'a' cannot be empty unless no samples are taken");
            return a._data[(int)Rng.NextBounded((ulong)a._data.Length)];
        }

        /// <summary>Random integer in [0, a).</summary>
        public static int choice(int a) => (int)Rng.NextInt(0, a);

        public static ndarray choice(ndarray a, NumPyShape size, bool replace = true) => ChoiceImpl(Rng, a._data, size.Dims, replace);
        public static ndarray choice(int a, NumPyShape size, bool replace = true) => ChoiceImpl(Rng, arange(a)._data, size.Dims, replace);

        internal static ndarray ChoiceImpl(Xoshiro256 rng, double[] pool, int[] shape, bool replace)
        {
            var s = (int[])shape.Clone();
            int n = Kernels.Prod(s);
            var d = new double[n];
            if (pool.Length == 0 && n > 0) throw Err.Value("'a' cannot be empty unless no samples are taken");
            if (replace)
            {
                for (int i = 0; i < n; i++) d[i] = pool[(int)rng.NextBounded((ulong)pool.Length)];
            }
            else
            {
                if (n > pool.Length) throw Err.Value("Cannot take a larger sample than population when 'replace=False'");
                var p = (double[])pool.Clone();
                for (int i = 0; i < n; i++)
                {
                    int j = i + (int)rng.NextBounded((ulong)(p.Length - i));
                    (p[i], p[j]) = (p[j], p[i]);
                    d[i] = p[i];
                }
            }
            return new ndarray(d, s);
        }

        /// <summary>Shuffle in place along axis 0 (elements for 1-D, rows otherwise).</summary>
        public static void shuffle(ndarray x) => ShuffleImpl(Rng, x);

        internal static void ShuffleImpl(Xoshiro256 rng, ndarray x)
        {
            if (x.ndim == 0) throw Err.Type("object of type 'numpy.float64' has no len()");
            int n = x._shape[0];
            int inner = n == 0 ? 0 : x._data.Length / n;
            var tmp = inner > 1 ? new double[inner] : null;
            for (int i = n - 1; i > 0; i--)
            {
                int j = (int)rng.NextBounded((ulong)(i + 1));
                if (i == j) continue;
                if (inner == 1) (x._data[i], x._data[j]) = (x._data[j], x._data[i]);
                else
                {
                    Array.Copy(x._data, i * inner, tmp, 0, inner);
                    Array.Copy(x._data, j * inner, x._data, i * inner, inner);
                    Array.Copy(tmp, 0, x._data, j * inner, inner);
                }
            }
        }

        /// <summary>Random permutation of arange(n).</summary>
        public static ndarray permutation(int n)
        {
            var a = arange(n);
            shuffle(a);
            return a;
        }

        /// <summary>Shuffled copy of x (along axis 0).</summary>
        public static ndarray permutation(ndarray x)
        {
            var a = x.copy();
            shuffle(a);
            return a;
        }

        /// <summary>np.random.default_rng(seed): a new independent Generator.</summary>
        public static Generator default_rng(int? seed = null) =>
            new(seed.HasValue ? unchecked((ulong)seed.Value) : (ulong)DateTime.UtcNow.Ticks ^ Rng.NextUInt64());

        /// <summary>numpy.random.Generator (subset).</summary>
        public sealed class Generator
        {
            private readonly Xoshiro256 _rng;

            internal Generator(ulong seed) => _rng = new Xoshiro256(seed);

            private ndarray Make(int[] shape, Action<Span<double>> fill)
            {
                var s = (int[])shape.Clone();
                var d = new double[Kernels.Prod(s)];
                fill(d);
                return new ndarray(d, s);
            }

            public double random() => _rng.NextDouble();
            public ndarray random(NumPyShape size) => Make(size.Dims, d => _rng.FillUniform(d));

            public double uniform(double low = 0.0, double high = 1.0) => low + (high - low) * _rng.NextDouble();
            public ndarray uniform(double low, double high, NumPyShape size) => Make(size.Dims, d => _rng.FillUniform(d, low, high));

            public double normal(double loc = 0.0, double scale = 1.0) => loc + scale * _rng.NextGaussian();
            public ndarray normal(double loc, double scale, NumPyShape size) => Make(size.Dims, d => _rng.FillNormal(d, loc, scale));

            public double standard_normal() => _rng.NextGaussian();
            public ndarray standard_normal(NumPyShape size) => Make(size.Dims, d => _rng.FillNormal(d));

            /// <summary>integers(high) / integers(low, high): integer in [low, high).</summary>
            public long integers(long low) => _rng.NextInt(0, low);
            public long integers(long low, long high) => _rng.NextInt(low, high);
            public ndarray integers(long low, long high, NumPyShape size) =>
                Make(size.Dims, d => { for (int i = 0; i < d.Length; i++) d[i] = _rng.NextInt(low, high); });

            public double choice(ndarray a) => a._data[(int)_rng.NextBounded((ulong)a._data.Length)];
            public ndarray choice(ndarray a, NumPyShape size, bool replace = true) => ChoiceImpl(_rng, a._data, size.Dims, replace);

            public void shuffle(ndarray x) => ShuffleImpl(_rng, x);

            public ndarray permutation(int n)
            {
                var a = arange(n);
                ShuffleImpl(_rng, a);
                return a;
            }

            public ndarray permutation(ndarray x)
            {
                var a = x.copy();
                ShuffleImpl(_rng, a);
                return a;
            }

            public override string ToString() => "Generator(Xoshiro256)";
        }
    }
}
