// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using static Typhon.Stdlib.numpy;

namespace Typhon.Stdlib;

/// <summary>
/// Port of numpy's arrayprint for float64 / bool arrays with default print options
/// (precision=8, floatmode='maxprec', linewidth=75, threshold=1000, edgeitems=3).
/// </summary>
internal static class ArrayPrinter
{
    private const int LineWidth = 75;
    private const int Threshold = 1000;
    private const int EdgeItems = 3;
    private const int Precision = 8;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal static string Str(ndarray a)
    {
        if (a.ndim == 0)
            return a._kind == NdKind.Bool ? (a._data[0] != 0 ? "True" : "False") : PyFloatRepr(a._data[0]);
        if (a.size == 0) return "[]";
        return Format(a, " ", "", "");
    }

    internal static string Repr(ndarray a)
    {
        const string prefix = "array(";
        if (a.size == 0)
        {
            string dt = a._kind == NdKind.Bool ? "bool" : "float64";
            return a.ndim == 1 ? $"array([], dtype={dt})" : $"array([], shape={Err.ShapeStr(a._shape)}, dtype={dt})";
        }
        if (a.ndim == 0)
        {
            var f = MakeFormatter(a, new List<double> { a._data[0] });
            string s = a._kind == NdKind.Bool ? (a._data[0] != 0 ? "True" : "False") : f(a._data[0]);
            return prefix + s + ")";
        }
        return prefix + Format(a, ", ", prefix, ")") + ")";
    }

    private static string Format(ndarray a, string separator, string prefix, string suffix)
    {
        bool summarize = a.size > Threshold;
        var values = new List<double>();
        var strides = new int[a.ndim];
        int st = 1;
        for (int i = a.ndim - 1; i >= 0; i--) { strides[i] = st; st *= a._shape[i]; }
        CollectValues(a, 0, 0, strides, summarize, values);
        var fmt = MakeFormatter(a, values);
        string nextLinePrefix = " " + new string(' ', prefix.Length);
        int lineWidth = LineWidth - suffix.Length;
        return Recurse(a, fmt, strides, 0, 0, nextLinePrefix, lineWidth, separator, summarize);
    }

    private static void CollectValues(ndarray a, int axis, int offset, int[] strides, bool summarize, List<double> values)
    {
        if (axis == a.ndim) { values.Add(a._data[offset]); return; }
        int n = a._shape[axis];
        if (summarize && 2 * EdgeItems < n)
        {
            for (int i = 0; i < EdgeItems; i++) CollectValues(a, axis + 1, offset + i * strides[axis], strides, summarize, values);
            for (int i = n - EdgeItems; i < n; i++) CollectValues(a, axis + 1, offset + i * strides[axis], strides, summarize, values);
        }
        else
        {
            for (int i = 0; i < n; i++) CollectValues(a, axis + 1, offset + i * strides[axis], strides, summarize, values);
        }
    }

    private static string Recurse(ndarray a, Func<double, string> fmt, int[] strides, int axis, int offset,
        string hangingIndent, int currWidth, string separator, bool summarize)
    {
        int axesLeft = a.ndim - axis;
        if (axesLeft == 0) return fmt(a._data[offset]);

        string nextHangingIndent = hangingIndent + " ";
        int nextWidth = currWidth - 1; // len(']')
        int aLen = a._shape[axis];
        bool showSummary = summarize && 2 * EdgeItems < aLen;
        int leading = showSummary ? EdgeItems : 0;
        int trailing = showSummary ? EdgeItems : aLen;
        int stride = strides[axis];

        var s = new StringBuilder();
        if (axesLeft == 1)
        {
            int elemWidth = currWidth - Math.Max(separator.TrimEnd().Length, 1);
            string line = hangingIndent;
            for (int i = 0; i < leading; i++)
            {
                string word = Recurse(a, fmt, strides, axis + 1, offset + i * stride, nextHangingIndent, nextWidth, separator, summarize);
                line = ExtendLine(s, line, word, elemWidth, hangingIndent);
                line += separator;
            }
            if (showSummary)
            {
                line = ExtendLine(s, line, "...", elemWidth, hangingIndent);
                line += separator;
            }
            for (int i = trailing; i > 1; i--)
            {
                string word = Recurse(a, fmt, strides, axis + 1, offset + (aLen - i) * stride, nextHangingIndent, nextWidth, separator, summarize);
                line = ExtendLine(s, line, word, elemWidth, hangingIndent);
                line += separator;
            }
            string lastWord = Recurse(a, fmt, strides, axis + 1, offset + (aLen - 1) * stride, nextHangingIndent, nextWidth, separator, summarize);
            line = ExtendLine(s, line, lastWord, elemWidth, hangingIndent);
            s.Append(line);
        }
        else
        {
            string lineSep = separator.TrimEnd() + new string('\n', axesLeft - 1);
            for (int i = 0; i < leading; i++)
            {
                string nested = Recurse(a, fmt, strides, axis + 1, offset + i * stride, nextHangingIndent, nextWidth, separator, summarize);
                s.Append(hangingIndent).Append(nested).Append(lineSep);
            }
            if (showSummary) s.Append(hangingIndent).Append("...").Append(lineSep);
            for (int i = trailing; i > 1; i--)
            {
                string nested = Recurse(a, fmt, strides, axis + 1, offset + (aLen - i) * stride, nextHangingIndent, nextWidth, separator, summarize);
                s.Append(hangingIndent).Append(nested).Append(lineSep);
            }
            string lastNested = Recurse(a, fmt, strides, axis + 1, offset + (aLen - 1) * stride, nextHangingIndent, nextWidth, separator, summarize);
            s.Append(hangingIndent).Append(lastNested);
        }
        // remove the hanging indent, and wrap in []
        return "[" + s.ToString(hangingIndent.Length, s.Length - hangingIndent.Length) + "]";
    }

    private static string ExtendLine(StringBuilder s, string line, string word, int lineWidth, string nextLinePrefix)
    {
        bool needsWrap = line.Length + word.Length > lineWidth;
        if (line.Length <= nextLinePrefix.Length) needsWrap = false;
        if (needsWrap)
        {
            s.Append(line.TrimEnd()).Append('\n');
            line = nextLinePrefix;
        }
        return line + word;
    }

    // ====================================================================== element formatters

    private static Func<double, string> MakeFormatter(ndarray a, List<double> values)
    {
        if (a._kind == NdKind.Bool)
        {
            string trueStr = a.ndim == 0 ? "True" : " True";
            return v => v != 0 ? trueStr : "False";
        }
        return new FloatFormat(values).Format;
    }

    /// <summary>numpy.core.arrayprint.FloatingFormat with floatmode='maxprec', sign='-'.</summary>
    private sealed class FloatFormat
    {
        private readonly bool _exp;
        private readonly int _padLeft, _padRight, _precision, _expSize;

        public FloatFormat(List<double> data)
        {
            var finite = new List<double>(data.Count);
            bool anyNonFinite = false, negInf = false;
            foreach (double v in data)
            {
                if (double.IsFinite(v)) finite.Add(v);
                else
                {
                    anyNonFinite = true;
                    if (double.IsNegativeInfinity(v)) negInf = true;
                }
            }

            double maxVal = 0, minVal = double.MaxValue;
            bool anyNonZero = false;
            foreach (double v in finite)
            {
                double av = Math.Abs(v);
                if (av == 0) continue;
                anyNonZero = true;
                if (av > maxVal) maxVal = av;
                if (av < minVal) minVal = av;
            }
            if (anyNonZero && (maxVal >= 1e8 || minVal < 0.0001 || maxVal / minVal > 1000.0)) _exp = true;

            if (finite.Count == 0)
            {
                _padLeft = 0; _padRight = 0; _expSize = -1; _exp = false;
            }
            else if (_exp)
            {
                int maxFrac = 0, maxInt = 0, maxExp = 0;
                foreach (double v in finite)
                {
                    var (mant, exp) = Scientific(v, Precision);
                    int dot = mant.IndexOf('.');
                    maxInt = Math.Max(maxInt, dot);
                    maxFrac = Math.Max(maxFrac, mant.Length - dot - 1);
                    maxExp = Math.Max(maxExp, ExpString(exp, 2).Length);
                }
                _expSize = maxExp - 1;
                _precision = maxFrac;
                _padLeft = maxInt;
                _padRight = _expSize + 2 + _precision;
            }
            else
            {
                int maxFrac = 0, maxInt = 0;
                foreach (double v in finite)
                {
                    string s = Positional(v, Precision);
                    int dot = s.IndexOf('.');
                    maxInt = Math.Max(maxInt, dot);
                    maxFrac = Math.Max(maxFrac, s.Length - dot - 1);
                }
                _padLeft = maxInt;
                _padRight = maxFrac;
                _expSize = -1;
            }

            if (anyNonFinite)
            {
                int offset = _padRight + 1;
                _padLeft = Math.Max(_padLeft, Math.Max(3 - offset, 3 + (negInf ? 1 : 0) - offset));
            }
        }

        public string Format(double x)
        {
            if (!double.IsFinite(x))
            {
                string ret = double.IsNaN(x) ? "nan" : (x < 0 ? "-inf" : "inf");
                return ret.PadLeft(_padLeft + _padRight + 1);
            }
            if (_exp)
            {
                var (mant, exp) = Scientific(x, Precision);
                int dot = mant.IndexOf('.');
                string intPart = mant[..dot];
                string frac = mant[(dot + 1)..].PadRight(_precision, '0');
                return intPart.PadLeft(_padLeft) + "." + frac + "e" + ExpString(exp, _expSize);
            }
            else
            {
                string s = Positional(x, Precision);
                int dot = s.IndexOf('.');
                return s[..dot].PadLeft(_padLeft) + "." + s[(dot + 1)..].PadRight(_padRight);
            }
        }
    }

    private static string ExpString(int exp, int digits) =>
        (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString(Inv).PadLeft(Math.Max(digits, 1), '0');

    // ====================================================================== digit generation

    /// <summary>Shortest round-trip digits of |v| (v finite, non-zero): value = 0.DIGITS * 10^point.</summary>
    private static (string digits, int point) ShortestDigits(double v)
    {
        string r = Math.Abs(v).ToString("R", Inv);
        int ePos = r.IndexOfAny(new[] { 'E', 'e' });
        int exp = 0;
        string mant = r;
        if (ePos >= 0)
        {
            exp = int.Parse(r[(ePos + 1)..], NumberStyles.AllowLeadingSign, Inv);
            mant = r[..ePos];
        }
        int dot = mant.IndexOf('.');
        string intPart = dot >= 0 ? mant[..dot] : mant;
        string frac = dot >= 0 ? mant[(dot + 1)..] : "";
        string digits = intPart + frac;
        int point = intPart.Length + exp;
        int lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') { lead++; point--; }
        digits = digits[lead..].TrimEnd('0');
        if (digits.Length == 0) { digits = "0"; point = 1; }
        return (digits, point);
    }

    /// <summary>dragon4_positional(x, precision, unique=True, trim='.') e.g. "1.", "-0.5", "0.33333333".</summary>
    internal static string Positional(double x, int precision)
    {
        bool neg = x < 0 || (x == 0 && double.IsNegative(x));
        string sign = neg ? "-" : "";
        if (x == 0) return sign + "0.";
        var (digits, point) = ShortestDigits(x);
        int fracDigits = digits.Length - point;
        if (fracDigits > precision)
        {
            // more digits than the precision allows: round the exact binary value (like Dragon4 with a cutoff)
            return sign + Math.Abs(x).ToString("F" + precision.ToString(Inv), Inv).TrimEnd('0');
        }
        string intPart, fracPart;
        if (point <= 0)
        {
            intPart = "0";
            fracPart = new string('0', -point) + digits;
        }
        else if (point >= digits.Length)
        {
            intPart = digits + new string('0', point - digits.Length);
            fracPart = "";
        }
        else
        {
            intPart = digits[..point];
            fracPart = digits[point..];
        }
        return sign + intPart + "." + fracPart;
    }

    /// <summary>dragon4_scientific(x, precision, unique=True, trim='.'): mantissa like "-1.5" and the exponent.</summary>
    internal static (string mantissa, int exp) Scientific(double x, int precision)
    {
        bool neg = x < 0 || (x == 0 && double.IsNegative(x));
        string sign = neg ? "-" : "";
        if (x == 0) return (sign + "0.", 0);
        var (digits, point) = ShortestDigits(x);
        int exp = point - 1;
        if (digits.Length - 1 > precision)
        {
            string e = Math.Abs(x).ToString("E" + precision.ToString(Inv), Inv); // d.ddddddddE+xxx
            int ePos = e.IndexOf('E');
            string m = e[..ePos].TrimEnd('0');
            exp = int.Parse(e[(ePos + 1)..], NumberStyles.AllowLeadingSign, Inv);
            if (!m.Contains('.')) m += ".";
            return (sign + m, exp);
        }
        return (sign + digits[0] + "." + digits[1..], exp);
    }

    /// <summary>Python repr() of a float: "2.0", "0.1", "1e-05", "1e+16", "nan", "inf".</summary>
    internal static string PyFloatRepr(double x)
    {
        if (double.IsNaN(x)) return "nan";
        if (double.IsPositiveInfinity(x)) return "inf";
        if (double.IsNegativeInfinity(x)) return "-inf";
        bool neg = x < 0 || (x == 0 && double.IsNegative(x));
        string sign = neg ? "-" : "";
        if (x == 0) return sign + "0.0";
        var (digits, point) = ShortestDigits(x);
        if (point <= -4 || point > 16)
        {
            int exp = point - 1;
            string m = digits.Length > 1 ? digits[0] + "." + digits[1..] : digits;
            return sign + m + "e" + ExpString(exp, 2);
        }
        if (point <= 0) return sign + "0." + new string('0', -point) + digits;
        if (point >= digits.Length) return sign + digits + new string('0', point - digits.Length) + ".0";
        return sign + digits[..point] + "." + digits[point..];
    }
}
