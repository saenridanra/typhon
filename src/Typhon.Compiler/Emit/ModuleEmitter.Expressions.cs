// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    internal sealed partial class ModuleEmitter
    {
        // ───────────────────────────── names ─────────────────────────────

        static readonly HashSet<string> CSharpKeywords = new HashSet<string>
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal",
            "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
            "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
            "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte",
            "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
            "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
        };

        internal static string Esc(string name) => CSharpKeywords.Contains(name) ? "@" + name : name;

        static readonly Dictionary<string, string> ExceptionMap = new Dictionary<string, string>
        {
            ["BaseException"] = "global::System.Exception",
            ["ValueError"] = "global::System.ArgumentException",
            ["TypeError"] = "global::System.InvalidCastException",
            ["KeyError"] = "global::System.Collections.Generic.KeyNotFoundException",
            ["IndexError"] = "global::System.ArgumentOutOfRangeException",
            ["ZeroDivisionError"] = "global::System.DivideByZeroException",
            ["NotImplementedError"] = "global::System.NotImplementedException",
            ["RuntimeError"] = "global::System.InvalidOperationException",
            ["AttributeError"] = "global::System.MissingMemberException",
            ["FileNotFoundError"] = "global::System.IO.FileNotFoundException",
            ["OSError"] = "global::System.IO.IOException",
            ["IOError"] = "global::System.IO.IOException",
            ["OverflowError"] = "global::System.OverflowException",
            ["ArithmeticError"] = "global::System.ArithmeticException",
            ["TimeoutError"] = "global::System.TimeoutException",
            ["PermissionError"] = "global::System.UnauthorizedAccessException",
            ["StopIteration"] = "global::System.InvalidOperationException",
            ["AssertionError"] = "global::Typhon.Runtime.AssertionError",
        };
        static readonly Dictionary<string, string> ExceptionMetaMap = ExceptionMap.ToDictionary(kv => kv.Key, kv => kv.Value.Replace("global::", ""));

        /// <summary>When catching, some Python exceptions correspond to several unrelated .NET exceptions.</summary>
        static readonly Dictionary<string, string[]> CatchMap = new Dictionary<string, string[]>
        {
            // ArgumentOutOfRangeException derives from ArgumentException but is Python's IndexError
            ["ValueError"] = new[] { "(global::System.ArgumentException and not global::System.ArgumentOutOfRangeException)", "global::System.FormatException" },
            ["IndexError"] = new[] { "global::System.ArgumentOutOfRangeException", "global::System.IndexOutOfRangeException" },
            ["TypeError"] = new[] { "global::System.InvalidCastException", "global::Microsoft.CSharp.RuntimeBinder.RuntimeBinderException" },
            ["ArithmeticError"] = new[] { "global::System.ArithmeticException" },
            ["LookupError"] = new[] { "global::System.Collections.Generic.KeyNotFoundException", "global::System.ArgumentOutOfRangeException", "global::System.IndexOutOfRangeException" },
        };

        /// <summary>The .NET types an `except X` clause should catch.</summary>
        List<string> CatchTypes(Expr type, Node at)
        {
            var result = new List<string>();
            foreach (var t in type is TupleExpr tup ? tup.Elts : new List<Expr> { type })
            {
                if (t is NameExpr n && !IsShadowed(n.Id) && CatchMap.TryGetValue(n.Id, out var many)) result.AddRange(many);
                else result.Add(MapType(t, at));
            }
            return result;
        }

        bool IsLocal(string name)
        {
            foreach (var set in _lambdaParams) if (set.Contains(name)) return true;
            if (_renames.ContainsKey(name)) return true;
            for (var s = _scope; s != null; s = s.Parent)
            {
                if (s.Params.Contains(name)) return true;
                if (s.Locals != null && (s.Locals.AllLocals.Contains(name) || s.Locals.LocalFunctions.Contains(name))) return true;
            }
            return false;
        }

        bool HasSelfInScope()
        {
            for (var s = _scope; s != null; s = s.Parent) if (s.HasSelf) return true;
            return false;
        }

        string ClsNameInScope()
        {
            for (var s = _scope; s != null; s = s.Parent) if (s.ClsName != null) return s.ClsName;
            return null;
        }

        /// <summary>A module-level or imported name that is NOT a type (function / variable).</summary>
        bool IsValueName(string name)
        {
            if (_m.Functions.Contains(name) || _m.Globals.Contains(name)) return true;
            if (_importedFromModules.TryGetValue(name, out var imp)) return !imp.Key.Classes.ContainsKey(imp.Value);
            foreach (var w in _wildcardModules) if (w.Functions.Contains(name) || w.Globals.Contains(name)) return true;
            return false;
        }

        bool IsShadowed(string name) => IsLocal(name) || IsValueName(name) || _m.Classes.ContainsKey(name) || _importedFromModules.ContainsKey(name);

        string EmitName(NameExpr n)
        {
            string id = n.Id;
            if (_renames.TryGetValue(id, out var renamed)) return Esc(renamed);
            if (IsLocal(id)) return Esc(id);
            if (id == "self" && HasSelfInScope()) return "this";
            if (id == "cls" && ClsNameInScope() != null) return ClsNameInScope();
            if (id == "__name__") return CsString(_m.Name);
            if (ExceptionMap.TryGetValue(id, out var ex) && !IsShadowed(id)) return ex;
            return Esc(id);
        }

        // ───────────────────────────── type resolution ─────────────────────────────

        IEnumerable<string> NamespaceChain()
        {
            var ns = _m.Namespace;
            while (!string.IsNullOrEmpty(ns))
            {
                yield return ns;
                int dot = ns.LastIndexOf('.');
                ns = dot < 0 ? null : ns.Substring(0, dot);
            }
        }

        ModuleInfo ModuleRef(string dotted)
        {
            if (_moduleAliases.TryGetValue(dotted, out var m)) return m;
            if (IsLocal(dotted.Split('.')[0])) return null;
            return _p.FindModule(dotted);
        }

        /// <summary>Does the dotted name denote a type? (Python has no 'new', so calls need this to become constructions.)</summary>
        bool TryResolveType(string dotted, int arity, out string meta, out ClassDef pyClass)
        {
            meta = null; pyClass = null;
            var parts = dotted.Split('.');
            string first = parts[0];
            if (IsLocal(first) || first == "self") return false;
            string sfx = arity > 0 ? "`" + arity : "";

            if (parts.Length == 1)
            {
                if (_cls != null && _cls.Name == first) { pyClass = _cls.Def; return true; }
                if (_cls != null && _cls.TypeParams.Contains(first)) return false;
                if (IsValueName(first)) return false;
                if (_m.Classes.TryGetValue(first, out pyClass)) return true;
                if (_cls != null && _cls.Def.Body.OfType<ClassDef>().FirstOrDefault(c => c.Name == first) is ClassDef nested) { pyClass = nested; return true; }
                if (_importedFromModules.TryGetValue(first, out var imp)) return imp.Key.Classes.TryGetValue(imp.Value, out pyClass);
                foreach (var w in _wildcardModules) if (w.Classes.TryGetValue(first, out pyClass)) return true;
                if (_importedTypes.TryGetValue(first, out var full) && _r.TypeExists(full + sfx)) { meta = full + sfx; return true; }
                if (ExceptionMetaMap.TryGetValue(first, out var exMeta)) { meta = exMeta; return true; }
            }
            else
            {
                string prefix = string.Join(".", parts, 0, parts.Length - 1);
                var mod = ModuleRef(prefix);
                if (mod != null) return mod.Classes.TryGetValue(parts[parts.Length - 1], out pyClass);
            }

            string expanded = dotted;
            if (_dotnetAliases.TryGetValue(first, out var aliasTarget)) expanded = aliasTarget + dotted.Substring(first.Length);
            foreach (var cand in Candidates(expanded))
            {
                if (_r.TypeExists(cand + sfx)) { meta = cand + sfx; return true; }
                int lastDot = cand.LastIndexOf('.');
                if (lastDot > 0)
                {
                    string nestedName = cand.Substring(0, lastDot) + "+" + cand.Substring(lastDot + 1);
                    if (_r.TypeExists(nestedName + sfx)) { meta = nestedName + sfx; return true; }
                }
            }
            return false;
        }

        IEnumerable<string> Candidates(string dotted)
        {
            yield return dotted;
            foreach (var ns in _usingNamespaces) yield return ns + "." + dotted;
            foreach (var ns in NamespaceChain()) yield return ns + "." + dotted;
            foreach (var t in _staticTypes) yield return t + "+" + dotted;
        }

        static readonly HashSet<string> BuiltinConversionNames = new HashSet<string> { "int", "float", "str", "bool", "list", "dict", "set", "tuple", "bytes", "object", "frozenset" };

        static readonly Dictionary<string, string> PrimitiveTypes = new Dictionary<string, string>
        {
            ["int"] = "int", ["float"] = "double", ["str"] = "string", ["bool"] = "bool", ["object"] = "object", ["bytes"] = "byte[]",
            ["Any"] = "dynamic", ["typing.Any"] = "dynamic",
            ["float32"] = "float", ["float64"] = "double", ["int8"] = "sbyte", ["int16"] = "short", ["int32"] = "int", ["int64"] = "long",
            ["uint8"] = "byte", ["uint16"] = "ushort", ["uint32"] = "uint", ["uint64"] = "ulong",
            ["long"] = "long", ["double"] = "double", ["decimal"] = "decimal", ["char"] = "char", ["byte"] = "byte", ["sbyte"] = "sbyte",
            ["short"] = "short", ["ushort"] = "ushort", ["uint"] = "uint", ["ulong"] = "ulong", ["nint"] = "nint", ["nuint"] = "nuint",
            ["list"] = "List<dynamic>", ["dict"] = "Dictionary<dynamic, dynamic>", ["set"] = "HashSet<dynamic>",
            ["Callable"] = "Delegate", ["Iterator"] = "IEnumerable<dynamic>", ["Iterable"] = "IEnumerable<dynamic>",
        };

        static readonly HashSet<string> GenericAliases = new HashSet<string>
        {
            "list", "List", "dict", "Dict", "set", "Set", "frozenset", "FrozenSet", "tuple", "Tuple", "Optional", "Union", "Callable",
            "Iterator", "Iterable", "Generator", "AsyncIterator", "AsyncIterable", "AsyncGenerator", "Awaitable", "Coroutine",
            "Sequence", "MutableSequence", "Mapping", "MutableMapping", "Type", "type", "ClassVar", "Final", "Annotated", "Array",
        };

        static string StripTypingPrefix(string dotted)
        {
            foreach (var p in new[] { "typing.", "collections.abc.", "t." })
                if (dotted != null && dotted.StartsWith(p)) return dotted.Substring(p.Length);
            return dotted;
        }

        bool IsTypeParamInScope(string name)
        {
            if (_cls != null && _cls.TypeParams.Contains(name)) return true;
            return _currentFuncTypeParams.Contains(name);
        }

        readonly HashSet<string> _currentFuncTypeParams = new HashSet<string>();

        /// <summary>Maps a Python type annotation to a C# type.</summary>
        string MapType(Expr e, Node at, bool isReturn = false)
        {
            switch (e)
            {
                case null: return "dynamic";
                case ConstExpr c when c.Kind == ConstKind.None: return isReturn ? "void" : "object";
                case ConstExpr c when c.Kind == ConstKind.Str:
                    try { return MapType(Parser.ParseExpression(c.Str, c.Line, c.Col), at, isReturn); }
                    catch (SyntaxError) { Error(at, $"invalid type annotation '{c.Str}'"); return "dynamic"; }
                case NameExpr n:
                {
                    if (n.Id == "None") return isReturn ? "void" : "object";
                    if (n.Id == "Self" && _cls != null) return _cls.CsSelfType;
                    if (IsTypeParamInScope(n.Id)) return Esc(n.Id);
                    if (!IsShadowed(n.Id) && PrimitiveTypes.TryGetValue(n.Id, out var prim)) return prim;
                    if (ExceptionMap.TryGetValue(n.Id, out var ex) && !IsShadowed(n.Id)) return ex;
                    return Esc(n.Id);
                }
                case AttributeExpr a:
                {
                    string dotted = DottedName(a);
                    if (dotted == null) { Error(at, "invalid type annotation"); return "dynamic"; }
                    string stripped = StripTypingPrefix(dotted);
                    if (stripped != dotted) return MapType(new NameExpr { Id = stripped, Line = a.Line, Col = a.Col }, at, isReturn);
                    return string.Join(".", dotted.Split('.').Select(Esc));
                }
                case SubscriptExpr s:
                {
                    var args = s.Index is TupleExpr t ? t.Elts : new List<Expr> { s.Index };
                    string baseName = StripTypingPrefix(DottedName(s.Target));
                    if (baseName == null) { Error(at, "invalid generic type annotation"); return "dynamic"; }
                    string A(int i) => MapType(args[i], at);
                    switch (baseName)
                    {
                        case "list": case "List": return $"List<{A(0)}>";
                        case "dict": case "Dict": return $"Dictionary<{A(0)}, {A(1)}>";
                        case "set": case "Set": return $"HashSet<{A(0)}>";
                        case "frozenset": case "FrozenSet": return $"IReadOnlySet<{A(0)}>";
                        case "Sequence": return $"IReadOnlyList<{A(0)}>";
                        case "MutableSequence": return $"IList<{A(0)}>";
                        case "Mapping": return $"IReadOnlyDictionary<{A(0)}, {A(1)}>";
                        case "MutableMapping": return $"IDictionary<{A(0)}, {A(1)}>";
                        case "Iterator": case "Iterable": case "Generator": return $"IEnumerable<{A(0)}>";
                        case "AsyncIterator": case "AsyncIterable": case "AsyncGenerator": return $"IAsyncEnumerable<{A(0)}>";
                        case "Awaitable": case "Coroutine": return $"Task<{A(args.Count - 1)}>";
                        case "Type": case "type": return "global::System.Type";
                        case "ClassVar": case "Final": case "Annotated": return A(0);
                        case "Array": return $"{A(0)}[]";
                        case "Optional": return Nullable(A(0));
                        case "Union": return MapUnion(args, at);
                        case "tuple": case "Tuple":
                            if (args.Count == 2 && args[1] is ConstExpr el && el.Kind == ConstKind.Ellipsis) return $"IReadOnlyList<{A(0)}>";
                            if (args.Count == 1) return $"ValueTuple<{A(0)}>";
                            return "(" + string.Join(", ", args.Select(x => MapType(x, at))) + ")";
                        case "Callable":
                        {
                            var ps = args.Count > 0 && args[0] is ListExpr pl ? pl.Elts.Select(x => MapType(x, at)).ToList() : new List<string>();
                            string ret = args.Count > 1 ? MapType(args[1], at, isReturn: true) : "void";
                            if (ret == "void") return ps.Count == 0 ? "Action" : $"Action<{string.Join(", ", ps)}>";
                            ps.Add(ret);
                            return $"Func<{string.Join(", ", ps)}>";
                        }
                    }
                    string b = MapType(s.Target, at);
                    return $"{b}<{string.Join(", ", args.Select(x => MapType(x, at)))}>";
                }
                case BinOpExpr bin when bin.Op == "|":
                {
                    var members = new List<Expr>();
                    void Flatten(Expr x) { if (x is BinOpExpr bx && bx.Op == "|") { Flatten(bx.Left); Flatten(bx.Right); } else members.Add(x); }
                    Flatten(bin);
                    return MapUnion(members, at);
                }
                default:
                    Error(at, "unsupported type annotation");
                    return "dynamic";
            }
        }

        static string Nullable(string t) => t.EndsWith("?") || t == "dynamic" || t == "object" ? t : t + "?";

        string MapUnion(List<Expr> members, Node at)
        {
            bool hasNone = members.Any(m => (m is ConstExpr c && c.Kind == ConstKind.None) || (m is NameExpr n && n.Id == "None"));
            var rest = members.Where(m => !((m is ConstExpr c && c.Kind == ConstKind.None) || (m is NameExpr n && n.Id == "None"))).Select(m => MapType(m, at)).Distinct().ToList();
            if (rest.Count == 1) return hasNone ? Nullable(rest[0]) : rest[0];
            // TODO(.NET 11 / C# 15): lower to a canonical (sorted) generated union type instead of object.
            Report(at, "TY1002", Severity.Info, $"union type ({string.Join(" | ", rest)}) is represented as 'object' in this version; narrow it with isinstance()");
            return "object";
        }

        bool IsTypeish(Expr e)
        {
            switch (e)
            {
                case ConstExpr c: return c.Kind == ConstKind.None || c.Kind == ConstKind.Str;
                case NameExpr n: return PrimitiveTypes.ContainsKey(n.Id) || n.Id == "None" || IsTypeParamInScope(n.Id) || TryResolveType(n.Id, 0, out _, out _);
                case AttributeExpr a: { var d = DottedName(a); return d != null && TryResolveType(d, 0, out _, out _); }
                case SubscriptExpr s:
                {
                    string bn = StripTypingPrefix(DottedName(s.Target));
                    if (bn == null) return false;
                    var args = s.Index is TupleExpr t ? t.Elts : new List<Expr> { s.Index };
                    if (GenericAliases.Contains(bn)) return true;
                    return TryResolveType(bn, args.Count, out _, out _) && args.All(IsTypeish);
                }
                case BinOpExpr b when b.Op == "|": return IsTypeish(b.Left) && IsTypeish(b.Right);
                default: return false;
            }
        }

        /// <summary>If the expression names a type, returns the C# code for it (used for 'Foo(...)' → 'new Foo(...)').</summary>
        bool TryTypeExpr(Expr e, out string code)
        {
            code = null;
            switch (e)
            {
                case NameExpr n:
                    if (BuiltinConversionNames.Contains(n.Id) && !IsShadowed(n.Id)) return false;
                    if (!TryResolveType(n.Id, 0, out _, out _)) return false;
                    code = ExceptionMap.TryGetValue(n.Id, out var ex) && !IsShadowed(n.Id) ? ex : (_cls != null && _cls.Name == n.Id ? _cls.CsSelfType : Esc(n.Id));
                    return true;
                case AttributeExpr a:
                {
                    string dotted = DottedName(a);
                    if (dotted == null || !TryResolveType(dotted, 0, out _, out _)) return false;
                    code = string.Join(".", dotted.Split('.').Select(Esc));
                    return true;
                }
                case SubscriptExpr s:
                {
                    var args = s.Index is TupleExpr t ? t.Elts : new List<Expr> { s.Index };
                    if (!args.All(IsTypeish)) return false;
                    string bn = StripTypingPrefix(DottedName(s.Target));
                    if (bn == null) return false;
                    if (GenericAliases.Contains(bn) || TryResolveType(bn, args.Count, out _, out _)) { code = MapType(s, s); return true; }
                    return false;
                }
                default: return false;
            }
        }

        // ───────────────────────────── expressions ─────────────────────────────

        static string Paren(string code) => code.StartsWith("(") && code.EndsWith(")") && BalancedOuter(code) ? code : "(" + code + ")";

        static bool BalancedOuter(string code)
        {
            int depth = 0;
            bool inStr = false;
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; continue; }
                if (c == '"') { inStr = true; continue; }
                if (c == '(') depth++;
                else if (c == ')') { depth--; if (depth == 0 && i != code.Length - 1) return false; }
            }
            return true;
        }

        bool IsBoolish(Expr e)
        {
            switch (e)
            {
                case CompareExpr _: return true;
                case BoolOpExpr b: return b.Values.All(IsBoolish);
                case UnaryExpr u when u.Op == "not": return true;
                case ConstExpr c: return c.Kind == ConstKind.Bool;
                case CallExpr c when c.Func is NameExpr fn && !IsShadowed(fn.Id):
                    return fn.Id == "isinstance" || fn.Id == "bool" || fn.Id == "any" || fn.Id == "all" || fn.Id == "callable" || fn.Id == "hasattr";
                case CallExpr c when c.Func is AttributeExpr fa:
                    return fa.Name == "startswith" || fa.Name == "endswith" || fa.Name == "isdigit" || fa.Name == "isalpha" || fa.Name == "isspace" ||
                           fa.Name == "isupper" || fa.Name == "islower" || fa.Name == "isalnum" || fa.Name == "Contains" || fa.Name == "StartsWith" || fa.Name == "EndsWith";
                case NameExpr n: return KnownType(n.Id) == "bool";
                default: return false;
            }
        }

        /// <summary>A condition: bools stay bools, anything else gets Python truthiness.</summary>
        string Cond(Expr e)
        {
            if (e is ConstExpr c && c.Kind == ConstKind.Bool) return c.Raw == "True" ? "true" : "false";
            string code = EmitExpr(e);
            return IsBoolish(e) ? code : $"truthy({code})";
        }

        internal string EmitExpr(Expr e, bool targetTyped = false)
        {
            switch (e)
            {
                case null: return "default";
                case ConstExpr c: return EmitConst(c);
                case NameExpr n: return EmitName(n);
                case FStringExpr f: return EmitFString(f);
                case AttributeExpr a: return EmitAttribute(a);
                case CallExpr c: return EmitCall(c, targetTyped);
                case SubscriptExpr s: return EmitSubscript(s);
                case BinOpExpr b: return EmitBinOp(b);
                case UnaryExpr u:
                    if (u.Op == "not") return "!" + Paren(Cond(u.Operand));
                    if (u.Op == "-" && u.Operand is ConstExpr nc && (nc.Kind == ConstKind.Int || nc.Kind == ConstKind.Float)) return "-" + EmitConst(nc);
                    return u.Op + Paren(EmitExpr(u.Operand));
                case BoolOpExpr b:
                    return "(" + string.Join(b.Op == "and" ? " && " : " || ", b.Values.Select(v => Paren(Cond(v)))) + ")";
                case CompareExpr c: return EmitCompare(c);
                case IfExpr i: return $"({Cond(i.Test)} ? {EmitExpr(i.Body, targetTyped)} : {EmitExpr(i.OrElse, targetTyped)})";
                case LambdaExpr l:
                {
                    _lambdaParams.Push(new HashSet<string>(l.Params));
                    string body = EmitExpr(l.Body, targetTyped: true);
                    _lambdaParams.Pop();
                    string ps = l.Params.Count == 1 ? Esc(l.Params[0]) : "(" + string.Join(", ", l.Params.Select(Esc)) + ")";
                    return $"({ps} => {body})";
                }
                case TupleExpr t:
                    if (t.Elts.Any(x => x is StarredExpr)) { Unsupported(t, "starred expressions in tuples"); return "default"; }
                    if (t.Elts.Count == 0) return "ValueTuple.Create()";
                    if (t.Elts.Count == 1) return $"ValueTuple.Create({EmitExpr(t.Elts[0])})";
                    return "(" + string.Join(", ", t.Elts.Select(x => EmitExpr(x))) + ")";
                case ListExpr l: return EmitList(l, targetTyped);
                case SetExpr s: return $"__set({string.Join(", ", s.Elts.Select(x => EmitExpr(x)))})";
                case DictExpr d: return EmitDict(d, targetTyped);
                case ComprehensionExpr c: return EmitComprehension(c);
                case AwaitExpr a: return $"(await {EmitExpr(a.Value)})";
                case YieldExpr y: Unsupported(y, "using 'yield' as an expression"); return "default";
                case StarredExpr s: Unsupported(s, "starred expressions here"); return "default";
                case SliceExpr s: Error(s, "slice outside of a subscript"); return "default";
                default: Unsupported(e, e.GetType().Name); return "default";
            }
        }

        string EmitConst(ConstExpr c)
        {
            switch (c.Kind)
            {
                case ConstKind.None: return "null";
                case ConstKind.Bool: return c.Raw == "True" ? "true" : "false";
                case ConstKind.Str: return CsString(c.Str);
                case ConstKind.Ellipsis: return "default";
                case ConstKind.Float:
                {
                    string raw = c.Raw.Replace("_", "");
                    if (raw.EndsWith(".")) raw += "0";
                    raw = raw.Replace(".e", ".0e").Replace(".E", ".0E");
                    if (raw.StartsWith(".")) raw = "0" + raw;
                    if (!raw.Contains(".") && !raw.Contains("e") && !raw.Contains("E")) raw += ".0";
                    return raw;
                }
                case ConstKind.Int:
                {
                    string raw = c.Raw.Replace("_", "");
                    BigInteger value;
                    if (raw.StartsWith("0o") || raw.StartsWith("0O"))
                    {
                        value = 0;
                        foreach (char ch in raw.Substring(2)) value = value * 8 + (ch - '0');
                    }
                    else if (raw.StartsWith("0x") || raw.StartsWith("0X")) value = BigInteger.Parse("0" + raw.Substring(2), NumberStyles.HexNumber);
                    else if (raw.StartsWith("0b") || raw.StartsWith("0B")) { value = 0; foreach (char ch in raw.Substring(2)) value = value * 2 + (ch - '0'); }
                    else value = BigInteger.Parse(raw);
                    if (value > ulong.MaxValue) return $"global::System.Numerics.BigInteger.Parse(\"{value}\")";
                    if (value > long.MaxValue) return value + "UL";
                    return value.ToString();
                }
            }
            return "default";
        }

        internal static string CsString(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\0': sb.Append("\\0"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        string EmitFString(FStringExpr f)
        {
            var sb = new StringBuilder("$\"");
            foreach (var p in f.Parts)
            {
                if (p.Literal != null)
                {
                    string lit = CsString(p.Literal);
                    sb.Append(lit.Substring(1, lit.Length - 2).Replace("{", "{{").Replace("}", "}}"));
                    continue;
                }
                string code = EmitExpr(p.Expr);
                string hole;
                if (p.FormatSpec != null)
                {
                    if (p.FormatSpec.Contains("{")) Unsupported(f, "nested replacement fields in f-string format specs");
                    hole = $"format({code}, {CsString(p.FormatSpec)})";
                    if (p.Conversion == 'r') hole = $"format(repr({code}), {CsString(p.FormatSpec)})";
                }
                else hole = p.Conversion == 'r' ? $"repr({code})" : $"str({code})";
                sb.Append('{').Append(hole).Append('}');
            }
            return sb.Append('"').ToString();
        }

        string EmitAttribute(AttributeExpr a)
        {
            if (a.Target is CallExpr sc && sc.Func is NameExpr sn && sn.Id == "super" && sc.Args.Count == 0) return "base." + Esc(a.Name);
            // np.random.random(...): C# forbids a member named like its class, the numpy library exposes it as random_sample
            if (a.Name == "random" && a.Target is AttributeExpr inner && inner.Name == "random" && DottedName(inner.Target) is string mod &&
                (_dotnetAliases.TryGetValue(mod, out var target0) ? target0 : mod) == "Typhon.Stdlib.numpy")
                return EmitExpr(a.Target) + ".random_sample";
            // type(x).__name__, x.__class__, SomeClass.__name__
            if (a.Name == "__class__") return Paren(EmitExpr(a.Target)) + ".GetType()";
            if (a.Name == "__name__")
            {
                if (a.Target is CallExpr tc && tc.Func is NameExpr tn && tn.Id == "type" && !IsShadowed("type")) return EmitExpr(a.Target) + ".Name";
                if (a.Target is AttributeExpr ca && ca.Name == "__class__") return EmitExpr(a.Target) + ".Name";
                if (DottedName(a.Target) is string tdn && TryTypeExpr(a.Target, out _)) return CsString(tdn.Split('.').Last());
            }
            string target = EmitExpr(a.Target);
            if (a.Target is ConstExpr || a.Target is UnaryExpr || a.Target is BinOpExpr || a.Target is AwaitExpr) target = Paren(target);
            return target + "." + Esc(a.Name);
        }

        /// <summary>
        /// A module-level Typhon function passed as an argument (map(f, xs), sorted(xs, key=f)) becomes a lambda: a method
        /// group with dynamic or overloaded parameters cannot convert to Func&lt;int, ...&gt;, a lambda binds per call.
        /// The call inside the lambda has argument types inference cannot see, so the function keeps its dynamic version.
        /// </summary>
        string FunctionValueLambda(NameExpr n)
        {
            if (IsLocal(n.Id) || _renames.ContainsKey(n.Id)) return null;
            FunctionDef def = null; ModuleInfo owner = null;
            if (_m.Functions.Contains(n.Id)) { owner = _m; def = _m.Ast.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == n.Id); }
            else if (_importedFromModules.TryGetValue(n.Id, out var imp) && imp.Key.Functions.Contains(imp.Value))
            { owner = imp.Key; def = imp.Key.Ast.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == imp.Value); }
            if (def == null || def.TypeParams.Count > 0 || def.Params.Any(p => p.Kind != ParamKind.Normal)) return null;
            // fully annotated functions convert as method groups (and keep their exact delegate type)
            if (def.Params.All(p => p.Annotation != null)) return null;
            if (Obs != null && TargetOf(owner, null, def.Name) is InferenceTarget t) Obs.Calls.Add(new Observations.Call { Callee = t.Key, Types = null, Context = _ctxFallbackKey });
            var ps = def.Params.Where(p => p.Default == null).Select((p, i) => "__a" + i).ToList();
            string call = $"{EmitName(n)}({string.Join(", ", ps)})";
            return ps.Count == 1 ? $"({ps[0]} => {call})" : $"(({string.Join(", ", ps)}) => {call})";
        }

        sealed class IterableArgSpec { public int First; public bool All; public bool OnlyWhenSingle; }

        /// <summary>Builtins whose positional argument(s) are iterables: index of the first, whether all following ones are too.</summary>
        static readonly Dictionary<string, IterableArgSpec> IterableArgBuiltins = new Dictionary<string, IterableArgSpec>
        {
            ["list"] = new IterableArgSpec(), ["set"] = new IterableArgSpec(), ["frozenset"] = new IterableArgSpec(), ["tuple"] = new IterableArgSpec(),
            ["sorted"] = new IterableArgSpec(), ["sum"] = new IterableArgSpec(), ["any"] = new IterableArgSpec(), ["all"] = new IterableArgSpec(),
            ["enumerate"] = new IterableArgSpec(), ["reversed"] = new IterableArgSpec(),
            ["zip"] = new IterableArgSpec { All = true },
            ["map"] = new IterableArgSpec { First = 1, All = true }, ["filter"] = new IterableArgSpec { First = 1 },
            ["min"] = new IterableArgSpec { OnlyWhenSingle = true }, ["max"] = new IterableArgSpec { OnlyWhenSingle = true },
        };

        /// <summary>Calls whose result is never a dict, so iterating them needs no iterable() wrapper.</summary>
        static readonly HashSet<string> NonDictResults = new HashSet<string> { "range", "enumerate", "zip", "sorted", "reversed", "map", "filter", "list", "set", "tuple", "frozenset", "str", "open" };

        /// <summary>An expression in a position that iterates it: wrapped in iterable() unless it is certainly not a dict.</summary>
        string EmitIterable(Expr e)
        {
            string code = EmitExpr(e);
            if (e is ListExpr || e is TupleExpr || e is SetExpr || e is ComprehensionExpr || e is ConstExpr || e is FStringExpr) return code;
            if (e is CallExpr ce && ce.Func is NameExpr cn && !IsShadowed(cn.Id) && NonDictResults.Contains(cn.Id)) return code;
            if (e is CallExpr me && me.Func is AttributeExpr ma && (ma.Name == "items" || ma.Name == "keys" || ma.Name == "values" || ma.Name == "split" || ma.Name == "splitlines")) return code;
            string t = InferType(e);
            if (t != null && (t == "string" || t.EndsWith("[]") || t.StartsWith("List<") || t.StartsWith("HashSet<") || t.StartsWith("IEnumerable<") ||
                              t.StartsWith("IReadOnlyList<") || t.StartsWith("IList<") || t.StartsWith("(") || t.StartsWith("ValueTuple<")))
                return code;
            return $"iterable({code})";
        }

        string EmitArgs(List<Arg> args, System.Func<int, bool> iterableAt = null)
        {
            var parts = new List<string>();
            int position = 0;
            foreach (var a in args)
            {
                if (a.DoubleStar) { Unsupported(a.Value, "**kwargs arguments"); continue; }
                if (iterableAt != null && a.Name == null && !a.Star && iterableAt(position++))
                {
                    parts.Add(EmitIterable(a.Value));
                    continue;
                }
                // a builtin passed as a function (map(str, xs), key=len): C# cannot convert an overloaded method group whose
                // parameter is object/generic to Func<int, string>, but a lambda binds the right overload at the call site
                string v = a.Value is NameExpr vn && ValueBuiltins.Contains(vn.Id) && !IsShadowed(vn.Id) && !_renames.ContainsKey(vn.Id)
                    ? $"(__v => {EmitName(vn)}(__v))"
                    : a.Value is NameExpr fv && FunctionValueLambda(fv) is string lambda ? lambda
                    : EmitExpr(a.Value);
                parts.Add(a.Name != null ? $"{Esc(a.Name)}: {v}" : v); // *xs is passed as-is (works for params arrays)
            }
            return string.Join(", ", parts);
        }

        /// <summary>Overloaded single-argument builtins that are wrapped in a lambda when passed as a function value.</summary>
        static readonly HashSet<string> ValueBuiltins = new HashSet<string> { "str", "repr", "int", "float", "bool", "len", "abs", "ord", "chr", "hex", "bin", "oct", "round", "sorted", "sum", "min", "max", "list", "tuple", "set" };

        static readonly HashSet<string> SpecialBuiltins = new HashSet<string> { "isinstance", "cast", "type", "print", "object", "list", "dict", "set", "super" };

        string EmitCall(CallExpr c, bool targetTyped)
        {
            if (Obs != null) RecordCall(CallTarget(c, out _), c.Args);
            if (c.Func is NameExpr fn && SpecialBuiltins.Contains(fn.Id) && !IsShadowed(fn.Id))
            {
                switch (fn.Id)
                {
                    case "isinstance":
                    {
                        if (c.Args.Count != 2) break;
                        string v = EmitExpr(c.Args[0].Value);
                        if (c.Args[1].Value is TupleExpr types) return "(" + string.Join(" || ", types.Elts.Select(t => $"{v} is {MapType(t, c)}")) + ")";
                        return $"({v} is {MapType(c.Args[1].Value, c)})";
                    }
                    case "cast":
                        if (c.Args.Count != 2) break;
                        return $"(({MapType(c.Args[0].Value, c)}){Paren(EmitExpr(c.Args[1].Value))})";
                    case "type":
                        if (c.Args.Count != 1) break;
                        return $"{Paren(EmitExpr(c.Args[0].Value))}.GetType()";
                    case "print":
                        if (c.Args.Any(a => a.Name != null))
                        {
                            var pos = c.Args.Where(a => a.Name == null).Select(a => EmitExpr(a.Value));
                            var kws = c.Args.Where(a => a.Name == "sep" || a.Name == "end").Select(a => $"{a.Name}: {EmitExpr(a.Value)}");
                            return $"print_kw(new object?[] {{ {string.Join(", ", pos)} }}{string.Concat(kws.Select(k => ", " + k))})";
                        }
                        break;
                    case "object":
                        if (c.Args.Count == 0) return "new object()";
                        break;
                    case "list":
                    case "dict":
                    case "set":
                        if (c.Args.Count == 0)
                        {
                            if (targetTyped) return "new()";
                            string what = fn.Id == "list" ? "List<dynamic>" : fn.Id == "dict" ? "Dictionary<dynamic, dynamic>" : "HashSet<dynamic>";
                            ImplicitDynamic(c, $"the element type of '{fn.Id}()'");
                            return $"new {what}()";
                        }
                        break;
                    case "super":
                        if (c.Args.Count == 0) return "base";
                        break;
                }
            }

            if (TryTypeExpr(c.Func, out var typeCode)) return $"new {typeCode}({EmitArgs(c.Args)})";

            // "...".format(a, b): routed to a helper so it does not clash with the builtin format(value, spec)
            if (c.Func is AttributeExpr fa && fa.Name == "format" && (fa.Target is ConstExpr fc && fc.Kind == ConstKind.Str || InferType(fa.Target) == "string"))
                return $"str_format({EmitExpr(fa.Target)}{(c.Args.Count > 0 ? ", " : "")}{EmitArgs(c.Args)})";

            // generic method call with explicit type arguments: f[int](x)  →  f<int>(x)
            if (c.Func is SubscriptExpr gs && !(gs.Index is SliceExpr))
            {
                var targs = gs.Index is TupleExpr t ? t.Elts : new List<Expr> { gs.Index };
                if (targs.All(IsTypeish) && !(gs.Target is NameExpr gn && IsLocal(gn.Id)))
                    return $"{EmitExpr(gs.Target)}<{string.Join(", ", targs.Select(x => MapType(x, c)))}>({EmitArgs(c.Args)})";
            }

            // builtins that consume iterables: a dict argument must contribute its keys (sorted(d), list(d), ", ".join(d))
            if (c.Func is NameExpr ifn && !IsShadowed(ifn.Id) && IterableArgBuiltins.TryGetValue(ifn.Id, out var spec))
            {
                int positional = c.Args.Count(a => a.Name == null && !a.Star && !a.DoubleStar);
                if (!(spec.OnlyWhenSingle && positional != 1))
                    return $"{EmitName(ifn)}({EmitArgs(c.Args, i => i >= spec.First && (spec.All || i == spec.First))})";
            }
            if (c.Func is AttributeExpr ja && ja.Name == "join" && c.Args.Count == 1 && c.Args[0].Name == null &&
                (ja.Target is ConstExpr jc && jc.Kind == ConstKind.Str || InferType(ja.Target) == "string"))
                return $"{Paren(EmitExpr(ja.Target))}.join({EmitIterable(c.Args[0].Value)})";

            string func = c.Func is NameExpr cfn ? EmitName(cfn) : EmitExpr(c.Func);
            if (c.Func is LambdaExpr || c.Func is IfExpr) func = Paren(func);
            return $"{func}({EmitArgs(c.Args)})";
        }

        string EmitIndex(Expr e)
        {
            if (e is UnaryExpr u && u.Op == "-" && u.Operand is ConstExpr c && c.Kind == ConstKind.Int) return "^" + EmitConst(c);
            return EmitExpr(e);
        }

        string EmitSubscript(SubscriptExpr s)
        {
            // a generic type used as a value, e.g. Stack[str].of(...) or Dictionary[str, int] passed around
            if (!(s.Index is SliceExpr) && TryTypeExpr(s, out var genericType)) return genericType;
            string target = EmitExpr(s.Target);
            if (s.Target is ConstExpr || s.Target is BinOpExpr || s.Target is UnaryExpr || s.Target is AwaitExpr) target = Paren(target);
            if (s.Index is SliceExpr sl)
            {
                // Python slicing semantics (copies, clamps out-of-range bounds, negative indices, steps)
                string step = sl.Step != null ? ", " + EmitExpr(sl.Step) : "";
                return $"slice({target}, {(sl.Lower != null ? EmitExpr(sl.Lower) : "null")}, {(sl.Upper != null ? EmitExpr(sl.Upper) : "null")}{step})";
            }
            if (s.Index is TupleExpr t && !t.Parenthesized)
                return $"{target}[{string.Join(", ", t.Elts.Select(x => x is SliceExpr xs ? EmitRange(xs) : EmitIndex(x)))}]";
            return $"{target}[{EmitIndex(s.Index)}]";
        }

        string EmitRange(SliceExpr sl)
        {
            if (sl.Step != null) Unsupported(sl, "slice steps inside multi-dimensional subscripts");
            return (sl.Lower != null ? EmitIndex(sl.Lower) : "") + ".." + (sl.Upper != null ? EmitIndex(sl.Upper) : "");
        }

        bool IsSequenceTyped(Expr e) { var t = InferType(e); return t == "string" || (t != null && t.StartsWith("List<")); }

        static bool IsSequenceLiteral(Expr e) => e is ConstExpr c && c.Kind == ConstKind.Str || e is FStringExpr || e is ListExpr;

        string EmitBinOp(BinOpExpr b)
        {
            string l = EmitExpr(b.Left), r = EmitExpr(b.Right);
            switch (b.Op)
            {
                case "/": return $"truediv({l}, {r})";
                case "//": return $"floordiv({l}, {r})";
                case "**": return $"pow({l}, {r})";
                case "@": return $"{Paren(l)}.__matmul__({r})";
                case "*" when IsSequenceLiteral(b.Left) || IsSequenceLiteral(b.Right) || IsSequenceTyped(b.Left) || IsSequenceTyped(b.Right): return $"repeat({l}, {r})";
                // late-bound operands may be a str or list at run time: "ab" * 2 must repeat, not fail in the binder
                case "*" when InferType(b.Left) == "dynamic" || InferType(b.Right) == "dynamic": return $"mul({l}, {r})";
                case "+" when InferType(b.Left) == "dynamic" && InferType(b.Right) == "dynamic": return $"add({l}, {r})";
                case "+" when IsListy(b.Left) || IsListy(b.Right): return $"concat({l}, {r})";
                case "%" when b.Left is ConstExpr sc && sc.Kind == ConstKind.Str:
                    Unsupported(b, "'%' string formatting (use an f-string)");
                    return l;
                case "%": return $"mod({l}, {r})"; // Python modulo: result has the sign of the divisor
                default: return $"({l} {b.Op} {r})";
            }
        }

        string EmitCompare(CompareExpr c)
        {
            var parts = new List<string>();
            Expr left = c.Left;
            for (int i = 0; i < c.Ops.Count; i++)
            {
                parts.Add(EmitComparison(left, c.Ops[i], c.Comparators[i]));
                left = c.Comparators[i];
            }
            return parts.Count == 1 ? parts[0] : "(" + string.Join(" && ", parts) + ")";
        }

        static bool IsNone(Expr e) => e is ConstExpr c && c.Kind == ConstKind.None;

        string EmitComparison(Expr left, string op, Expr right)
        {
            string l = EmitExpr(left), r = EmitExpr(right);
            switch (op)
            {
                case "in": return $"contains({r}, {l})";
                case "not in": return $"!contains({r}, {l})";
                case "is": return IsNone(right) ? $"({l} is null)" : IsNone(left) ? $"({r} is null)" : $"ReferenceEquals({l}, {r})";
                case "is not": return IsNone(right) ? $"({l} is not null)" : IsNone(left) ? $"({r} is not null)" : $"!ReferenceEquals({l}, {r})";
                default: return $"({l} {op} {r})";
            }
        }

        string EmitList(ListExpr l, bool targetTyped)
        {
            if (l.Elts.Any(x => x is StarredExpr)) { Unsupported(l, "starred expressions in list literals"); return "default"; }
            var elts = l.Elts.Select(x => EmitExpr(x)).ToList();
            if (targetTyped) return "[" + string.Join(", ", elts) + "]";
            if (elts.Count == 0)
            {
                ImplicitDynamic(l, "the element type of an empty list (annotate the variable, e.g. 'xs: list[int] = []')");
                return "new List<dynamic>()";
            }
            return $"__list({string.Join(", ", elts)})";
        }

        string EmitDict(DictExpr d, bool targetTyped)
        {
            if (targetTyped)
                return d.Keys.Count == 0 ? "new()" : "new() { " + string.Join(", ", d.Keys.Select((k, i) => $"[{EmitExpr(k)}] = {EmitExpr(d.Values[i])}")) + " }";
            if (d.Keys.Count == 0)
            {
                ImplicitDynamic(d, "the key/value types of an empty dict (annotate the variable, e.g. 'd: dict[str, int] = {}')");
                return "new Dictionary<dynamic, dynamic>()";
            }
            return "__dict(" + string.Join(", ", d.Keys.Select((k, i) => $"({EmitExpr(k)}, {EmitExpr(d.Values[i])})")) + ")";
        }

        string EmitComprehension(ComprehensionExpr c)
        {
            string Gen(int i)
            {
                var g = c.Generators[i];
                if (g.IsAsync) Unsupported(c, "async comprehensions");
                string iter = EmitIterable(g.Iter);
                string param; string deconstruct = null;
                var bound = new HashSet<string>();
                AstWalker.VisitAll(g.Target, n => { if (n is NameExpr ne) bound.Add(ne.Id); });
                if (g.Target is NameExpr tn) param = Esc(tn.Id);
                else { param = NewTmp("c"); deconstruct = $"var {DeconstructionPattern(g.Target)} = {param};"; }
                _lambdaParams.Push(bound);
                string Lambda(string body) => deconstruct == null ? $"{param} => {body}" : $"{param} => {{ {deconstruct} return {body}; }}";
                string seq = iter;
                foreach (var cond in g.Ifs) seq = $"{Paren(seq)}.Where({Lambda(Cond(cond))})";
                string result;
                if (i == c.Generators.Count - 1)
                {
                    string elt = c.Kind == ComprehensionKind.Dict ? $"({EmitExpr(c.Elt)}, {EmitExpr(c.ValueElt)})" : EmitExpr(c.Elt);
                    result = $"{Paren(seq)}.Select({Lambda(elt)})";
                }
                else result = $"{Paren(seq)}.SelectMany({Lambda(Gen(i + 1))})";
                _lambdaParams.Pop();
                return result;
            }
            string q = Gen(0);
            switch (c.Kind)
            {
                case ComprehensionKind.List: return q + ".ToList()";
                case ComprehensionKind.Set: return q + ".ToHashSet()";
                case ComprehensionKind.Dict: return q + ".ToDictionary(__kv => __kv.Item1, __kv => __kv.Item2)";
                default: return q;
            }
        }

        /// <summary>Is the expression (statically) a Python list? Used for list + list and list += iterable.</summary>
        bool IsListy(Expr e)
        {
            if (e is ListExpr) return true;
            if (e is ComprehensionExpr c) return c.Kind == ComprehensionKind.List;
            if (e is BinOpExpr b && (b.Op == "+" || b.Op == "*")) return IsListy(b.Left) || IsListy(b.Right);
            var t = InferType(e);
            return t != null && t.StartsWith("List<");
        }

        // ───────────────────────────── inference ─────────────────────────────

        readonly Dictionary<string, string> _funcReturnTypes = new Dictionary<string, string>();
        readonly Dictionary<string, string> _moduleFieldTypes = new Dictionary<string, string>();

        /// <summary>Element type of iterating a value of C# type <paramref name="t"/> (dict: keys, str: char).</summary>
        static string ElementTypeOf(string t)
        {
            if (t == null || t == TypeInference.Pending) return t;
            if (t == "string") return "char";
            if (t.EndsWith("[]")) return t.Substring(0, t.Length - 2);
            foreach (var g in new[] { "List<", "IList<", "IReadOnlyList<", "IEnumerable<", "HashSet<", "ISet<", "IReadOnlySet<", "IReadOnlyCollection<" })
                if (t.StartsWith(g) && t.EndsWith(">")) return GenericArg(t);
            if ((t.StartsWith("Dictionary<") || t.StartsWith("IDictionary<") || t.StartsWith("IReadOnlyDictionary<")) && t.EndsWith(">"))
            {
                var targs = SplitTypeArgs(GenericArg(t));
                return targs.Count == 2 ? targs[0] : null;
            }
            return null;
        }

        /// <summary>Splits "K, List&lt;V&gt;" at top-level commas.</summary>
        static List<string> SplitTypeArgs(string inner)
        {
            var result = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < inner.Length; i++)
            {
                char ch = inner[i];
                if (ch == '<' || ch == '(') depth++;
                else if (ch == '>' || ch == ')') depth--;
                else if (ch == ',' && depth == 0) { result.Add(inner.Substring(start, i - start).Trim()); start = i + 1; }
            }
            result.Add(inner.Substring(start).Trim());
            return result;
        }

        bool IsMathModule(Expr e) =>
            e is NameExpr n && !IsLocal(n.Id) && (n.Id == "math" && !_m.Globals.Contains("math") || _dotnetAliases.TryGetValue(n.Id, out var target) && target == "Typhon.Stdlib.math_");

        static readonly HashSet<string> MathIntFunctions = new HashSet<string> { "floor", "ceil", "trunc", "factorial", "gcd", "lcm", "isqrt", "comb", "perm" };
        static readonly HashSet<string> MathBoolFunctions = new HashSet<string> { "isnan", "isinf", "isfinite", "isclose" };

        /// <summary>Result type of math.f(...) (Typhon's math module mirrors Python's: floor/ceil/... return int).</summary>
        string MathCall(AttributeExpr f)
        {
            if (!IsMathModule(f.Target)) return null;
            if (MathIntFunctions.Contains(f.Name)) return "int";
            if (MathBoolFunctions.Contains(f.Name)) return "bool";
            return "double";
        }

        string MathConstant(AttributeExpr a) => IsMathModule(a.Target) && (a.Name == "pi" || a.Name == "e" || a.Name == "tau" || a.Name == "inf" || a.Name == "nan") ? "double" : null;

        static readonly HashSet<string> NumericTypes = new HashSet<string> { "int", "long", "double", "float", "decimal" };

        /// <summary>Cheap local type inference, used where C# 'var' cannot be used (hoisted locals, fields).</summary>
        string InferType(Expr e)
        {
            switch (e)
            {
                case ConstExpr c:
                    switch (c.Kind)
                    {
                        case ConstKind.Int: { var code = EmitConst(c); return long.TryParse(code, out var v) ? (v <= int.MaxValue ? "int" : "long") : null; }
                        case ConstKind.Float: return "double";
                        case ConstKind.Str: return "string";
                        case ConstKind.Bool: return "bool";
                        default: return null;
                    }
                case FStringExpr _: return "string";
                case NameExpr n:
                {
                    var t = KnownType(n.Id);
                    if (t != null) return t;
                    if (!IsLocal(n.Id) && _moduleFieldTypes.TryGetValue(n.Id, out var mt)) return mt;
                    return null;
                }
                case AttributeExpr at when at.Target is NameExpr self && self.Id == "self" && _cls != null:
                    return _cls.FieldTypes.TryGetValue(at.Name, out var ft) ? ft : null;
                case AttributeExpr oa:
                {
                    var ot = InferType(oa.Target);
                    if (ot == TypeInference.Pending) return TypeInference.Pending;
                    if (ot == null) return MathConstant(oa);
                    return MemberType(ot, oa.Name, method: false);
                }
                case SubscriptExpr ix when !(ix.Index is SliceExpr):
                {
                    var t = InferType(ix.Target);
                    if (t == null || t == TypeInference.Pending) return t;
                    if (t == "string") return "string";
                    if (t.EndsWith("[]")) return t.Substring(0, t.Length - 2);
                    if ((t.StartsWith("List<") || t.StartsWith("IList<") || t.StartsWith("IReadOnlyList<")) && t.EndsWith(">")) return GenericArg(t);
                    if ((t.StartsWith("Dictionary<") || t.StartsWith("IDictionary<") || t.StartsWith("IReadOnlyDictionary<")) && t.EndsWith(">"))
                    {
                        var targs = SplitTypeArgs(GenericArg(t));
                        return targs.Count == 2 ? targs[1] : null;
                    }
                    return null;
                }
                case AwaitExpr aw:
                {
                    var t = InferType(aw.Value);
                    return t != null && t.StartsWith("Task<") && t.EndsWith(">") ? GenericArg(t) : null;
                }
                case SubscriptExpr sub when sub.Index is SliceExpr:
                {
                    var t = InferType(sub.Target);
                    return t != null && (t == "string" || t.StartsWith("List<") || t.EndsWith("[]")) ? t : null;
                }
                case CallExpr call:
                {
                    if (InferredCallType(call) is string inferredCall) return inferredCall;
                    if (call.Func is NameExpr known && _funcReturnTypes.TryGetValue(known.Id, out var rt) && (IsShadowed(known.Id) || IsLocal(known.Id)))
                        return rt == "void" ? null : rt;
                    if (call.Func is AttributeExpr mc && !(mc.Target is NameExpr mcs && mcs.Id == "self"))
                    {
                        if (MathCall(mc) is string mathType) return mathType;
                        var ot = InferType(mc.Target);
                        if (ot == TypeInference.Pending) return TypeInference.Pending;
                        if (ot != null && MemberType(ot, mc.Name, method: true) is string objRet) return objRet;
                    }
                    if (call.Func is AttributeExpr ma && ma.Target is NameExpr ms && ms.Id == "self" && _cls != null && _cls.MethodReturnTypes.TryGetValue(ma.Name, out var mrt))
                        return mrt == "void" ? null : mrt;
                    if (call.Func is NameExpr fn && !IsShadowed(fn.Id))
                    {
                        switch (fn.Id)
                        {
                            case "str": case "repr": case "input": case "chr": case "hex": case "bin": case "format": return "string";
                            case "int": case "len": case "ord": case "hash": return "int";
                            case "float": return "double";
                            case "bool": case "isinstance": case "any": case "all": return "bool";
                            case "round": return call.Args.Count == 1 ? "int" : "double";
                            case "abs": return call.Args.Count == 1 ? InferType(call.Args[0].Value) : null;
                            case "range": return "IEnumerable<int>";
                        }
                    }
                    if (TryTypeExpr(call.Func, out var code)) return code;
                    return null;
                }
                case BinOpExpr b:
                {
                    string l = InferType(b.Left), r = InferType(b.Right);
                    // a recursive call whose type is still being inferred contributes no information (optimistic inference)
                    if (l == TypeInference.Pending || r == TypeInference.Pending)
                    {
                        if (b.Op == "/") return "double";
                        if (l == TypeInference.Pending && r == TypeInference.Pending) return TypeInference.Pending;
                        return l == TypeInference.Pending ? r : l;
                    }
                    if (b.Op == "/") return (l == "decimal" && r == "decimal") ? "decimal" : (NumericTypes.Contains(l ?? "") && NumericTypes.Contains(r ?? "") ? "double" : null);
                    if (l == null || r == null) return null;
                    if (b.Op == "+" && l == "string" && r == "string") return "string";
                    if (b.Op == "*" && (l == "string" || r == "string")) return "string";
                    if (!NumericTypes.Contains(l) || !NumericTypes.Contains(r)) return l == r ? l : null;
                    if (b.Op == "**") return l == "int" && r == "int" ? "int" : "double";
                    if (l == r) return l;
                    if (l == "double" || r == "double") return "double";
                    if (l == "long" || r == "long") return "long";
                    return null;
                }
                case UnaryExpr u: return u.Op == "not" ? "bool" : InferType(u.Operand);
                case CompareExpr _: return "bool";
                case BoolOpExpr b: return b.Values.All(IsBoolish) ? "bool" : null;
                case IfExpr i:
                {
                    var a = InferType(i.Body); var o = InferType(i.OrElse);
                    if (a == TypeInference.Pending) return o;
                    if (o == TypeInference.Pending) return a;
                    return a != null && a == o ? a : null;
                }
                case ListExpr l:
                {
                    if (l.Elts.Count == 0) return null;
                    var ts = l.Elts.Select(InferType).Distinct().ToList();
                    if (ts.Count == 1 && ts[0] != null) return $"List<{ts[0]}>";
                    if (ts.All(t => t == "int" || t == "double")) return "List<double>";
                    return null;
                }
                case DictExpr d:
                {
                    if (d.Keys.Count == 0) return null;
                    var ks = d.Keys.Select(InferType).Distinct().ToList();
                    var vs = d.Values.Select(InferType).Distinct().ToList();
                    if (ks.Count == 1 && ks[0] != null && vs.Count == 1 && vs[0] != null) return $"Dictionary<{ks[0]}, {vs[0]}>";
                    return null;
                }
                case TupleExpr t:
                {
                    if (t.Elts.Count < 2) return null;
                    var ts = t.Elts.Select(InferType).ToList();
                    return ts.All(x => x != null) ? "(" + string.Join(", ", ts) + ")" : null;
                }
                default: return null;
            }
        }
    }
}
