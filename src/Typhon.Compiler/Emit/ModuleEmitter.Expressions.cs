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

        /// <summary>A Typhon runtime helper (mod, pow, slice, str, ...): qualified when a Python name in scope hides it (a parameter called 'mod').</summary>
        string Rt(string helper) => IsShadowed(helper) || _renames.ContainsKey(helper) || BuiltinClashes(helper) ? "global::Typhon.Runtime.Builtins." + helper : helper;

        /// <summary>
        /// Names imported from Typhon standard modules (from math import sqrt / *): the module's C# type. A call to such a name
        /// is qualified with it, and Python's builtin of the same name (pow) with Builtins, since 'using static' of the module
        /// brings all of its members into scope, not only the imported ones.
        /// </summary>
        readonly Dictionary<string, string> _stdlibImported = new Dictionary<string, string>();
        readonly List<string> _stdlibStar = new List<string>();
        readonly Dictionary<string, bool> _clashes = new Dictionary<string, bool>();

        static string CsTypeName(string meta) => "global::" + string.Join(".", meta.Replace('+', '.').Split('.').Select(Esc));

        string StdlibOwner(string name)
        {
            if (_stdlibImported.TryGetValue(name, out var meta)) return meta;
            foreach (var star in _stdlibStar) if (_r.GetMembers(star, name).Count > 0) return star;
            return null;
        }

        bool BuiltinClashes(string name)
        {
            if (_staticTypes.Count == 0 || StdlibOwner(name) != null) return false;
            if (!_clashes.TryGetValue(name, out bool clash))
                _clashes[name] = clash = _staticTypes.Any(st => st.StartsWith("Typhon.Stdlib.") && _r.GetMembers(st, name).Count > 0) &&
                                         _r.GetMembers("Typhon.Runtime.Builtins", name).Count > 0;
            return clash;
        }

        string EmitName(NameExpr n)
        {
            string id = n.Id;
            if (_renames.TryGetValue(id, out var renamed)) return Esc(renamed);
            if (IsLocal(id)) return Esc(id);
            if (id == "self" && HasSelfInScope()) return "this";
            if (id == "cls" && ClsNameInScope() != null) return ClsNameInScope();
            if (id == "__name__") return CsString(_m.Name);
            if (id == "__file__" && !IsShadowed(id)) return CsString(System.IO.Path.GetFullPath(_path));
            if (ExceptionMap.TryGetValue(id, out var ex) && !IsShadowed(id)) return ex;
            if (!IsShadowed(id))
            {
                if (StdlibOwner(id) is string owner && _r.GetMembers(owner, id).Count > 0) return CsTypeName(owner) + "." + Esc(id);
                if (BuiltinClashes(id)) return "global::Typhon.Runtime.Builtins." + Esc(id);
            }
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
            ["int"] = "long", ["float"] = "double", ["str"] = "string", ["bool"] = "bool", ["object"] = "object", ["bytes"] = "byte[]",
            ["Any"] = "dynamic", ["typing.Any"] = "dynamic",
            ["float32"] = "float", ["float64"] = "double", ["int8"] = "sbyte", ["int16"] = "short", ["int32"] = "int", ["int64"] = "long",
            ["uint8"] = "byte", ["uint16"] = "ushort", ["uint32"] = "uint", ["uint64"] = "ulong",
            ["long"] = "long", ["double"] = "double", ["decimal"] = "decimal", ["char"] = "char", ["byte"] = "byte", ["sbyte"] = "sbyte",
            ["short"] = "short", ["ushort"] = "ushort", ["uint"] = "uint", ["ulong"] = "ulong", ["nint"] = "nint", ["nuint"] = "nuint",
            ["list"] = "List<dynamic>", ["dict"] = "OrderedDictionary<dynamic, dynamic>", ["set"] = "HashSet<dynamic>",
            ["tuple"] = "global::System.Runtime.CompilerServices.ITuple", ["frozenset"] = "IReadOnlySet<dynamic>",
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

        /// <summary>The type a name stands for when it is a type alias of this module or imported from another Typhon module.</summary>
        Expr TypeAlias(string name)
        {
            if (IsLocal(name)) return null;
            if (_m.TypeAliases.TryGetValue(name, out var local)) return local;
            if (_importedFromModules.TryGetValue(name, out var imp) && imp.Key.TypeAliases.TryGetValue(imp.Value, out var imported)) return imported;
            return null;
        }

        /// <summary>Python classes of the standard library imported by name (Fraction, Path) → their Typhon runtime type.</summary>
        string StdlibClassType(string name)
        {
            if (IsShadowed(name) && !_importedFromModules.ContainsKey(name)) return null;
            if (name == "Fraction" && _staticTypes.Contains("Typhon.Stdlib.fractions")) return "global::Typhon.Runtime.Fraction";
            if (name == "Path" && _staticTypes.Contains("Typhon.Stdlib.pathlib")) return "global::Typhon.Runtime.Path";
            return null;
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
                    if (TypeAlias(n.Id) is Expr aliased) return MapType(aliased, at, isReturn);
                    if (StdlibClassType(n.Id) is string stdlibType) return stdlibType;
                    if (n.Id == "NDArray" && !IsShadowed("NDArray") && _staticTypes.Contains("Typhon.Stdlib.numpy+typing")) return "global::Typhon.Stdlib.numpy.ndarray";
                    if (_big && n.Id == "int" && !IsShadowed("int")) return IntT;
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
                        case "NDArray": case "npt.NDArray": case "numpy.typing.NDArray": return "global::Typhon.Stdlib.numpy.ndarray"; // numpy.typing
                        case "Counter": case "collections.Counter": return $"global::Typhon.Runtime.Counter<{A(0)}>";
                        case "deque": case "Deque": case "collections.deque": return $"global::Typhon.Runtime.deque<{A(0)}>";
                        case "defaultdict": case "DefaultDict": case "collections.defaultdict": return $"global::Typhon.Runtime.defaultdict<{A(0)}, {A(1)}>";
                        case "OrderedDict": case "collections.OrderedDict": return $"global::Typhon.Runtime.PyOrderedDict<{A(0)}, {A(1)}>";
                        case "list": case "List": return $"List<{A(0)}>";
                        case "dict": case "Dict": return $"OrderedDictionary<{A(0)}, {A(1)}>"; // insertion-ordered, like Python's dict
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
            // int | float: an int is acceptable wherever a float is (PEP 484's numeric tower)
            if (rest.Count == 2 && rest.Contains("double") && rest.Any(IsIntegral)) rest = new List<string> { "double" };
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
                case NameExpr n: return PrimitiveTypes.ContainsKey(n.Id) || n.Id == "None" || IsTypeParamInScope(n.Id) || TypeAlias(n.Id) != null || TryResolveType(n.Id, 0, out _, out _);
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
            // in a condition, and/or need only the truth of each operand
            if (e is BoolOpExpr bo) return "(" + string.Join(bo.Op == "and" ? " && " : " || ", bo.Values.Select(v => Paren(Cond(v)))) + ")";
            string code = EmitExpr(e);
            return IsBoolish(e) ? code : $"{Rt("truthy")}({code})";
        }

        internal string EmitExpr(Expr e, bool targetTyped = false)
        {
            switch (e)
            {
                case null: return "default";
                case ConstExpr c: return EmitConst(c);
                case NameExpr n when _narrowed.Contains(n.Id) && KnownTypeCore(n.Id) is string ot && ot.EndsWith("?"): return EmitName(n) + ".Value";
                case NameExpr n: return EmitName(n);
                case FStringExpr f: return EmitFString(f);
                case AttributeExpr a: return EmitAttribute(a);
                case CallExpr c: return EmitCall(c, targetTyped);
                case SubscriptExpr s: return EmitSubscript(s);
                case BinOpExpr b: return EmitBinOp(b);
                case UnaryExpr u:
                    if (u.Op == "not") return "!" + Paren(Cond(u.Operand));
                    if (u.Op == "-" && u.Operand is ConstExpr nc && (nc.Kind == ConstKind.Int || nc.Kind == ConstKind.Float)) return "-" + EmitConst(nc);
                    if (InferType(u.Operand) == "bool") return u.Op + $"({Paren(EmitExpr(u.Operand))} ? 1L : 0L)";
                    return u.Op + Paren(EmitExpr(u.Operand));
                case BoolOpExpr b:
                {
                    if (b.Values.All(IsBoolish)) return "(" + string.Join(b.Op == "and" ? " && " : " || ", b.Values.Select(v => Paren(Cond(v)))) + ")";
                    // x or y / x and y as values: the operand that decides (previous = previous or set(), name = name or "anon");
                    // operands of different types (0 or None, 2 and "yes") are late-bound
                    string bt = InferType(b);
                    bool late = bt == null || bt == "dynamic";
                    // (an empty {} / [] takes the other operand's type: counts = counts or {})
                    string Operand(Expr x, bool last = false) => late ? $"(dynamic){Paren(EmitExpr(x))}"
                        : IsEmptyLiteral(x) && !last && bt.EndsWith(">") ? $"new {bt}()" // ([] and xs: tested with 'is var', so it needs a type)
                        : EmitExpr(x, targetTyped || IsEmptyLiteral(x));
                    string code = Operand(b.Values[b.Values.Count - 1], last: true);
                    for (int i = b.Values.Count - 2; i >= 0; i--)
                    {
                        string t = NewTmp("v");
                        string test = IsBoolish(b.Values[i]) ? t : $"{Rt("truthy")}({t})";
                        code = b.Op == "or" ? $"({Operand(b.Values[i])} is var {t} && {test} ? {t} : {code})"
                                            : $"({Operand(b.Values[i])} is var {t} && !{test} ? {t} : {code})";
                    }
                    return code;
                }
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
                case NamedExpr ne: return $"({EmitName(ne.Target)} = {EmitValue(ne.Value)})";
                case TupleExpr t:
                    // (*a, b): a tuple of computed length
                    if (t.Elts.Any(x => x is StarredExpr)) return $"{Rt("tuple")}({EmitSpread(t.Elts, "List", t)})";
                    if (t.Elts.Count == 0) return "ValueTuple.Create()";
                    if (t.Elts.Count == 1) return $"ValueTuple.Create({EmitValue(t.Elts[0])})";
                    return "(" + string.Join(", ", t.Elts.Select(x => EmitValue(x))) + ")";
                case ListExpr l: return EmitList(l, targetTyped);
                case SetExpr s:
                    if (s.Elts.Any(x => x is StarredExpr)) return EmitSpread(s.Elts, "HashSet", s);
                    return $"{Rt("__set")}({string.Join(", ", s.Elts.Select(x => EmitValue(x)))})";
                case DictExpr d: return EmitDict(d, targetTyped);
                case ComprehensionExpr c: return EmitComprehension(c);
                case AwaitExpr a: return $"(await {EmitExpr(a.Value)})";
                case YieldExpr y: Unsupported(y, "using 'yield' as an expression"); return "default";
                case StarredExpr s: Unsupported(s, "starred expressions here"); return "default";
                case SliceExpr s: Error(s, "slice outside of a subscript"); return "default";
                default: Unsupported(e, e.GetType().Name); return "default";
            }
        }

        static bool IsIntLiteral(Expr e) =>
            e is ConstExpr c && c.Kind == ConstKind.Int || e is UnaryExpr u && u.Op == "-" && u.Operand is ConstExpr uc && uc.Kind == ConstKind.Int;

        /// <summary>A C# integer type narrower than long (from .NET APIs or explicit int32-style annotations).</summary>
        internal static bool IsSmallInt(string t) => t == "int" || t == "short" || t == "byte" || t == "sbyte" || t == "ushort" || t == "uint";

        /// <summary>The type a value has once it is a Python value: .NET's narrower integers widen to Python's int (long).</summary>
        internal string PyType(string t) => IsSmallInt(t) || _big && t == "long" ? IntT : t;

        /// <summary>
        /// Python's int is C#'s long, but a C# integer literal is an int: wherever a literal decides a type ('var', element
        /// types of collection literals, arithmetic) it gets the L suffix. Indexes and .NET arguments keep plain literals,
        /// which convert to int, long or whatever the parameter takes.
        /// </summary>
        string EmitValue(Expr e, bool targetTyped = false)
        {
            switch (e)
            {
                case ConstExpr c when c.Kind == ConstKind.Int: return IntLiteral(EmitConst(c));
                case UnaryExpr u when u.Op == "-" && u.Operand is ConstExpr uc && uc.Kind == ConstKind.Int: return "-" + IntLiteral(EmitConst(uc));
                case IfExpr i: return $"({Cond(i.Test)} ? {EmitValue(i.Body, targetTyped)} : {EmitValue(i.OrElse, targetTyped)})";
                default: return EmitExpr(e, targetTyped);
            }
        }

        /// <summary>An int literal where it decides the type: 5L, or in big-int mode a BigInteger.</summary>
        string IntLiteral(string code) =>
            !_big ? LongLiteral(code) : code.StartsWith("global::") ? code : $"(({TypeInference.BigInt}){code})";

        /// <summary>A compile-time integer as Python's int: a long constant, or in big-int mode a BigInteger of any size.</summary>
        string IntConstant(BigInteger v)
        {
            if (_big)
                return v >= long.MinValue && v <= long.MaxValue ? $"(({TypeInference.BigInt}){(v == long.MinValue ? "long.MinValue" : v + "L")})"
                                                                : $"{TypeInference.BigInt}.Parse(\"{v}\")";
            return v == long.MinValue ? "long.MinValue" : v.Sign < 0 ? $"({v}L)" : $"{v}L";
        }

        static string LongLiteral(string code) => code.Length > 0 && char.IsDigit(code[code.Length - 1]) ? code + "L" : code;

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
                    hole = $"{Rt("format")}({code}, {CsString(p.FormatSpec)})";
                    if (p.Conversion == 'r') hole = $"{Rt("format")}({Rt("repr")}({code}), {CsString(p.FormatSpec)})";
                }
                else hole = p.Conversion == 'r' ? $"{Rt("repr")}({code})" : $"{Rt("str")}({code})";
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
            string target = a.Target is ConstExpr ic && ic.Kind == ConstKind.Int ? EmitValue(a.Target) : EmitExpr(a.Target);
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
            // a fully annotated function becomes a lambda with typed parameters: generic callees (cmp_to_key, map, sorted) infer
            // their type arguments from it, which they cannot do from a method group
            if (def.Params.All(p => p.Annotation != null))
            {
                var required = def.Params.Where(p => p.Default == null).ToList();
                if (required.Count == 0) return null;
                var typed = required.Select((p, i) => $"{MapType(p.Annotation, p)} __a{i}");
                return $"(({string.Join(", ", typed)}) => {EmitName(n)}({string.Join(", ", required.Select((_, i) => "__a" + i))}))";
            }
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
            // iterating a str yields one-character strs
            if (e is ConstExpr sc && sc.Kind == ConstKind.Str || e is FStringExpr) return $"{Rt("chars")}({code})";
            // for x in (1, 2, 3): a tuple literal iterates as a list of its items
            if (e is TupleExpr te && te.Elts.Count > 0 && !te.Elts.Any(x => x is StarredExpr)) return EmitList(new ListExpr { Elts = te.Elts, Line = te.Line, Col = te.Col }, false);
            if (e is ListExpr || e is TupleExpr || e is SetExpr || e is ComprehensionExpr || e is ConstExpr) return code;
            // a late-bound value: a typed sequence of dynamic items (LINQ lambdas cannot take part in dynamic dispatch); the
            // argument is passed as object so that the call itself is bound statically
            if (InferType(e) == "dynamic") return $"{Rt("__iter_dyn")}((object){Paren(code)})";
            // str(n), "".join(...), s.lower(): a str-valued call iterates as one-character strs too
            if (e is CallExpr && InferType(e) == "string") return $"{Rt("chars")}({code})";
            if (e is CallExpr ce && ce.Func is NameExpr cn && !IsShadowed(cn.Id) && NonDictResults.Contains(cn.Id)) return code;
            if (e is CallExpr me && me.Func is AttributeExpr ma && (ma.Name == "items" || ma.Name == "keys" || ma.Name == "values" || ma.Name == "split" || ma.Name == "splitlines")) return code;
            string t = InferType(e);
            if (t == "string") return $"{Rt("chars")}({code})";
            // a late-bound value: a typed sequence of dynamic items (LINQ lambdas cannot take part in dynamic dispatch)
            if (t == "dynamic") return $"{Rt("__iter_dyn")}({code})";
            var fixedTuple = TupleElementTypes(t);
            if (fixedTuple != null) return $"{Rt("tuple_items")}<{(fixedTuple.Distinct().Count() == 1 ? fixedTuple[0] : "object")}>({code})";
            if (t == "global::Typhon.Runtime.PyRange") return code;
            if (t != null && (t.EndsWith("[]") || t.StartsWith("List<") || t.StartsWith("HashSet<") || t.StartsWith("IEnumerable<") ||
                              t.StartsWith("IReadOnlyList<") || t.StartsWith("IList<") || t.StartsWith("(") || t.StartsWith("ValueTuple<")))
                return code;
            return $"{Rt("iterable")}({code})";
        }

        /// <summary>
        /// The Typhon function or constructor a call goes to (by name, module attribute, self.method or Class.method), and
        /// how many of its parameters the call does not pass explicitly (self/cls); null for anything else.
        /// </summary>
        FunctionDef TyphonCallee(CallExpr c, out int implicitParams)
        {
            implicitParams = 0;
            ModuleInfo mod = null; string name = null;
            if (c.Func is NameExpr n && !IsLocal(n.Id))
            {
                if (_m.Functions.Contains(n.Id) || _m.Classes.ContainsKey(n.Id)) { mod = _m; name = n.Id; }
                else if (_importedFromModules.TryGetValue(n.Id, out var imp)) { mod = imp.Key; name = imp.Value; }
            }
            else if (c.Func is AttributeExpr a)
            {
                if (DottedName(a.Target) is string dotted && !IsLocal(dotted.Split('.')[0]) && ModuleRef(dotted) is ModuleInfo mref) { mod = mref; name = a.Name; }
                else if (a.Target is NameExpr sn && sn.Id == "self" && _cls != null)
                {
                    var m = _cls.Def.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == a.Name);
                    if (m != null) { implicitParams = HasDecorator(m.Decorators, "staticmethod") ? 0 : 1; return m; }
                    return null;
                }
                else if (a.Target is NameExpr cn && TryResolveType(cn.Id, 0, out _, out var pyCls) && pyCls != null)
                {
                    var m = pyCls.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == a.Name);
                    if (m != null) { implicitParams = HasDecorator(m.Decorators, "classmethod") ? 1 : 0; return m; }
                    return null;
                }
            }
            if (mod == null) return null;
            if (mod.Classes.TryGetValue(name, out var cls))
            {
                implicitParams = 1;
                return cls.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__init__");
            }
            return mod.Functions.Contains(name) ? mod.Ast.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == name) : null;
        }

        static bool IsSimpleOperand(Expr e) => e is NameExpr || e is AttributeExpr a && IsSimpleOperand(a.Target);

        /// <summary>
        /// f(a, *xs) into a Typhon function: the starred sequence fills the remaining fixed parameters (tuples by Item1..n, other
        /// sequences by index with Python's count check) or the function's own *args. Null when no star needs expanding.
        /// </summary>
        string ExpandStarArgs(CallExpr c)
        {
            if (!c.Args.Any(a => a.Star)) return null;
            var callee = TyphonCallee(c, out int implicitParams);
            if (callee == null || c.Args.Count(a => a.Star) != 1 || c.Args.Any(a => a.DoubleStar)) return null;
            var fixedParams = callee.Params.Skip(implicitParams).TakeWhile(p => p.Kind == ParamKind.Normal).ToList();
            bool hasVarArgs = callee.Params.Any(p => p.Kind == ParamKind.VarArgs);
            int starAt = c.Args.FindIndex(a => a.Star);
            var before = c.Args.Take(starAt).ToList();
            var after = c.Args.Skip(starAt + 1).ToList();
            if (before.Any(a => a.Name != null) || after.Any(a => a.Name == null)) return null;
            var star = c.Args[starAt].Value;
            if (hasVarArgs && before.Count >= fixedParams.Count)
                return null; // spreads into the function's own *args: the params array takes the sequence as is
            // the parameters the star fills: a tuple's arity is known; a list fills the remaining required parameters
            var remaining = fixedParams.Skip(before.Count).Where(p => !after.Any(a => a.Name == p.Name)).ToList();
            var tupleTypes = TupleElementTypes(InferType(star));
            int take = tupleTypes != null ? tupleTypes.Count : remaining.Count(p => p.Default == null);
            if (take > remaining.Count || take == 0) return null;
            string source = EmitExpr(star), prefix = null;
            if (!IsSimpleOperand(star))
            {
                string tmp = NewTmp("s");
                if (_lambdaParams.Count == 0) { _w.Line($"var {tmp} = {source};"); source = tmp; }
                else { prefix = $"{Paren(source)} switch {{ var {tmp} => "; source = tmp; }
            }
            var items = new List<string>();
            for (int i = 0; i < take; i++) items.Add(tupleTypes != null ? $"{source}.Item{i + 1}" : $"{Rt("__unpack")}({source}, {i}, {take})");
            var args = before.Select(a => EmitExpr(a.Value)).Concat(items).Concat(after.Select(a => $"{Esc(a.Name)}: {EmitExpr(a.Value)}"));
            string func = c.Func is NameExpr cfn ? EmitName(cfn) : EmitExpr(c.Func);
            string call = TryTypeExpr(c.Func, out var typeCode) ? $"new {typeCode}({string.Join(", ", args)})" : $"{func}({string.Join(", ", args)})";
            return prefix != null ? $"({prefix}{call} }})" : call;
        }

        static bool IsEmptyLiteral(Expr e) => e is ListExpr l && l.Elts.Count == 0 || e is DictExpr d && d.Keys.Count == 0 ||
                                              e is CallExpr c && c.Args.Count == 0 && c.Func is NameExpr n && (n.Id == "list" || n.Id == "dict" || n.Id == "set");

        /// <summary>Positions where a Typhon callee declares the parameter's type (an empty [] / {} there takes that type).</summary>
        System.Func<int, bool> TypedParamPositions(CallExpr c)
        {
            var callee = TyphonCallee(c, out int implicitParams);
            if (callee == null) return null;
            var ps = callee.Params.Skip(implicitParams).ToList();
            return i => i >= 0 && i < ps.Count && ps[i].Kind == ParamKind.Normal && ps[i].Annotation != null;
        }

        /// <summary>The C# type of a Typhon callee's annotated parameter (by position, or by name for a keyword argument), or null.</summary>
        string ParamTypeOf(CallExpr c, int pos, string name)
        {
            var callee = TyphonCallee(c, out int implicitParams);
            if (callee == null) return null;
            var ps = callee.Params.Skip(implicitParams).ToList();
            var p = name != null ? ps.FirstOrDefault(x => x.Name == name) : pos >= 0 && pos < ps.Count ? ps[pos] : null;
            return p?.Annotation != null && p.Kind == ParamKind.Normal ? MapType(p.Annotation, p) : null;
        }

        /// <summary>{} / set() / [] for a parameter of an interface type (Mapping → IReadOnlyDictionary, ...): a new concrete container, or null.</summary>
        static string ConcreteEmpty(Expr value, string paramType)
        {
            if (!paramType.EndsWith(">")) return null;
            string args = GenericArg(paramType);
            if (paramType.StartsWith("IReadOnlyDictionary<") || paramType.StartsWith("IDictionary<")) return $"new OrderedDictionary<{args}>()";
            if (paramType.StartsWith("IReadOnlySet<") || paramType.StartsWith("ISet<")) return $"new HashSet<{args}>()";
            return null;
        }

        /// <summary>Is the keyword argument's parameter of a Typhon callee annotated (so an empty literal takes its type)?</summary>
        bool TypedKeyword(CallExpr c, string name)
        {
            var callee = TyphonCallee(c, out _);
            return callee != null && callee.Params.Any(p => p.Name == name && p.Kind == ParamKind.Normal && p.Annotation != null);
        }

        string EmitArgs(List<Arg> args, System.Func<int, bool> iterableAt = null, IReadOnlyList<Semantics.MemberSig> overloads = null, System.Func<int, bool> targetTypedAt = null,
                        bool dynamicCall = false, System.Func<string, bool> typedKeywords = null, System.Func<int, string, string> paramTypeAt = null)
        {
            var parts = new List<string>();
            int position = 0, positional = 0;
            foreach (var a in args)
            {
                if (a.DoubleStar) { Unsupported(a.Value, "**kwargs arguments"); continue; }
                int argPos = a.Name == null && !a.Star ? positional++ : -1;
                if (iterableAt != null && a.Name == null && !a.Star && iterableAt(position++))
                {
                    parts.Add(EmitIterable(a.Value));
                    continue;
                }
                if (overloads != null && overloads.Count > 0 && !a.Star && NarrowingFor(overloads, args, a, argPos) is string narrowTo)
                {
                    string narrowed = $"checked(({narrowTo})({EmitExpr(a.Value)}))";
                    parts.Add(a.Name != null ? $"{Esc(a.Name)}: {narrowed}" : narrowed);
                    continue;
                }
                // big-int mode: where an overload keeps Python ints exact (factorial, pow, gcd, abs, ...), 64-bit values and
                // literals go to it as BigIntegers (C# would pick the long overload, which raises OverflowError)
                if (_big && overloads != null && overloads.Count > 0 && !a.Star && IsIntegral(InferType(a.Value)) && InferType(a.Value) != "bool" &&
                    (IsIntLiteral(a.Value) || InferType(a.Value) != TypeInference.BigInt) && BigOverloadAt(overloads, args, a, argPos))
                {
                    string promoted = IsIntLiteral(a.Value) ? EmitValue(a.Value) : $"(({TypeInference.BigInt}){Paren(EmitExpr(a.Value))})";
                    parts.Add(a.Name != null ? $"{Esc(a.Name)}: {promoted}" : promoted);
                    continue;
                }
                if (a.Name == null && !a.Star && targetTypedAt != null && targetTypedAt(argPos) && IsEmptyLiteral(a.Value))
                {
                    parts.Add(paramTypeAt?.Invoke(argPos, null) is string pt && ConcreteEmpty(a.Value, pt) is string concrete ? concrete : EmitExpr(a.Value, targetTyped: true));
                    continue;
                }
                if (a.Name != null && typedKeywords != null && IsEmptyLiteral(a.Value) && typedKeywords(a.Name))
                {
                    string ev = paramTypeAt?.Invoke(-1, a.Name) is string pt && ConcreteEmpty(a.Value, pt) is string concrete ? concrete : EmitExpr(a.Value, targetTyped: true);
                    parts.Add($"{Esc(a.Name)}: {ev}");
                    continue;
                }
                // a builtin passed as a function (map(str, xs), key=len): C# cannot convert an overloaded method group whose
                // parameter is object/generic to Func<int, string>, but a lambda binds the right overload at the call site
                string v = a.Value is NameExpr vn && ValueBuiltins.Contains(vn.Id) && !IsShadowed(vn.Id) && !_renames.ContainsKey(vn.Id)
                    ? (_big && vn.Id == "int" ? $"(__v => {Rt("__bigint")}(__v))" : $"(__v => {EmitName(vn)}(__v))")
                    // str.upper / str.lower / list.copy passed as a function: a lambda calling the method on its argument
                    : a.Value is AttributeExpr um && um.Target is NameExpr umt && BuiltinTypeNames.Contains(umt.Id) && !IsShadowed(umt.Id) && !BuiltinClassMethods.Contains(um.Name)
                    ? $"(__v => __v.{Esc(um.Name)}())"
                    : a.Value is NameExpr fv && FunctionValueLambda(fv) is string lambda ? lambda
                    : EmitExpr(a.Value);
                // a dynamically dispatched call cannot take a lambda as such: give it a delegate type of dynamic values
                if (dynamicCall && a.Value is LambdaExpr dl)
                    v = $"((Func<{string.Join(", ", Enumerable.Repeat("dynamic", dl.Params.Count + 1))}>){v})";
                else if (dynamicCall && v.StartsWith("(__v =>")) v = $"((Func<dynamic, dynamic>){v})";
                // *xs into a params array (print(*xs), .NET params methods): spread into an array of the parameter's element type
                if (a.Star && !(InferType(a.Value) is string at && at.EndsWith("[]"))) v = $"[.. {EmitIterable(a.Value)}]";
                parts.Add(a.Name != null ? $"{Esc(a.Name)}: {v}" : v);
            }
            return string.Join(", ", parts);
        }

        static readonly HashSet<string> BuiltinTypeNames = new HashSet<string> { "str", "list", "dict", "set", "int", "float", "tuple", "frozenset" };
        /// <summary>Class methods of built-in types (called on the type, not on a value).</summary>
        static readonly HashSet<string> BuiltinClassMethods = new HashSet<string> { "fromkeys", "maketrans" };

        /// <summary>Overloaded single-argument builtins that are wrapped in a lambda when passed as a function value.</summary>
        static readonly HashSet<string> ValueBuiltins = new HashSet<string> { "str", "repr", "int", "float", "bool", "len", "abs", "ord", "chr", "hex", "bin", "oct", "round", "sorted", "sum", "min", "max", "list", "tuple", "set" };

        static readonly HashSet<string> SpecialBuiltins = new HashSet<string> { "isinstance", "cast", "type", "print", "object", "list", "dict", "set", "super" };

        /// <summary>The inference key of a local / self field that started as an empty container, and its kind.</summary>
        string ElementKey(Expr receiver, out char kind)
        {
            kind = 'l';
            if (receiver is NameExpr n && !_renames.ContainsKey(n.Id) && _emptyContainers.TryGetValue(n.Id, out kind)) return _scopeKey + "|" + n.Id;
            if (receiver is AttributeExpr a && a.Target is NameExpr s && s.Id == "self" && _cls != null && _emptyFields.TryGetValue(a.Name, out kind))
                return TypeInference.ClassKey(_m, _cls.Name) + "|self." + a.Name;
            return null;
        }

        /// <summary>Records (in a dry run) what a call adds to a container that started empty: its element type, for its declaration.</summary>
        void RecordAdded(CallExpr c)
        {
            // d.setdefault(k, []).append(v): the dict's values are lists of v's type (likewise .add for sets)
            if (Obs != null && c.Func is AttributeExpr inner && (inner.Name == "append" || inner.Name == "add") && c.Args.Count == 1 &&
                inner.Target is CallExpr sd && sd.Func is AttributeExpr sda && sda.Name == "setdefault" && sd.Args.Count == 2 &&
                TypeInference.IsEmptyContainer(sd.Args[1].Value) && ElementKey(sda.Target, out char dk) is string dkey && (dk == 'd' || dk == 'o'))
            {
                string container = TypeInference.ContainerKind(sd.Args[1].Value) == 's' ? "HashSet" : "List";
                string et = PyType(InferType(c.Args[0].Value)) ?? TypeInference.Unknown;
                Obs.AddElement(dkey, (PyType(InferType(sd.Args[0].Value)) ?? TypeInference.Unknown) + "→" + $"{container}<{et}>");
            }
            // groups[k].append(v) on defaultdict(list) / defaultdict(set): its values are lists (sets) of v's type
            if (Obs != null && c.Func is AttributeExpr dd && (dd.Name == "append" || dd.Name == "add" || dd.Name == "extend") && c.Args.Count == 1 &&
                dd.Target is SubscriptExpr dsub && ElementKey(dsub.Target, out char ddk) is string ddkey && ddk == 'D')
            {
                string container = dd.Name == "add" ? "HashSet" : "List";
                string et = dd.Name == "extend" ? PyType(ElementTypeOf(InferType(c.Args[0].Value))) : PyType(InferType(c.Args[0].Value));
                Obs.AddElement(ddkey, (PyType(InferType(dsub.Index)) ?? TypeInference.Unknown) + "→" + $"{container}<{et ?? TypeInference.Unknown}>");
            }
            if (Obs == null || !(c.Func is AttributeExpr fa) || ElementKey(fa.Target, out char kind) is not string key) return;
            var args = c.Args.Where(a => a.Name == null && !a.Star).Select(a => a.Value).ToList();
            string T(Expr e) => PyType(InferType(e)) ?? TypeInference.Unknown;
            bool sequence = kind == 'l' || kind == 'q';
            switch (fa.Name)
            {
                case "append" when args.Count == 1 && sequence: Obs.AddElement(key, T(args[0])); break;
                case "appendleft" when args.Count == 1 && kind == 'q': Obs.AddElement(key, T(args[0])); break;
                case "insert" when args.Count == 2 && sequence: Obs.AddElement(key, T(args[1])); break;
                case "add" when args.Count == 1 && kind == 's': Obs.AddElement(key, T(args[0])); break;
                case "extend" when args.Count == 1 && sequence:
                case "extendleft" when args.Count == 1 && kind == 'q':
                case "update" when kind == 's' && args.Count >= 1:
                    foreach (var a in args) Obs.AddElement(key, PyType(ElementTypeOf(InferType(a)))); break;
                case "update" when kind == 'c' && args.Count == 1:
                    Obs.AddElement(key, (InferType(args[0]) == "string" ? "string" : PyType(ElementTypeOf(InferType(args[0]))) ?? TypeInference.Unknown) + "→long"); break;
                // setdefault(k, []) only says "a list": what is appended to it (below) says of what
                case "setdefault" when kind == 'd' && args.Count == 2 && !TypeInference.IsEmptyContainer(args[1]): Obs.AddElement(key, T(args[0]) + "→" + T(args[1])); break;
                case "update" when (kind == 'd' || kind == 'o') && args.Count == 1:
                {
                    var dt = InferType(args[0]);
                    var kv = IsDictType(dt) ? SplitTypeArgs(GenericArg(dt)) : null;
                    Obs.AddElement(key, kv != null ? PyType(kv[0]) + "→" + PyType(kv[1]) : TypeInference.Unknown + "→" + TypeInference.Unknown);
                    break;
                }
            }
        }

        string EmitCall(CallExpr c, bool targetTyped)
        {
            if (Obs != null) RecordCall(CallTarget(c, out _), c.Args);
            RecordAdded(c);
            // big-int mode: int(x) parses / truncates to a BigInteger
            if (_big && c.Func is NameExpr bi && bi.Id == "int" && !IsShadowed("int") && c.Args.Count >= 1 && c.Args.All(x => x.Name == null || x.Name == "base"))
                return $"{Rt("__bigint")}({string.Join(", ", c.Args.Select(x => InferType(x.Value) == "dynamic" ? $"(object){Paren(EmitExpr(x.Value))}" : EmitExpr(x.Value)))})";
            // int(x) / float(x) / str(x) / len(x) of a late-bound value: passed as object, the call binds statically and keeps
            // its result type (the runtime converts by the value's run-time type) instead of becoming dynamic itself
            if (c.Func is NameExpr scv && StaticConversions.Contains(scv.Id) && !IsShadowed(scv.Id) && c.Args.Count == 1 && c.Args[0].Name == null && !c.Args[0].Star &&
                InferType(c.Args[0].Value) == "dynamic")
                return $"{EmitName(scv)}((object){Paren(EmitExpr(c.Args[0].Value))})";
            if (c.Func is NameExpr fn && SpecialBuiltins.Contains(fn.Id) && !IsShadowed(fn.Id))
            {
                switch (fn.Id)
                {
                    case "isinstance":
                    {
                        if (c.Args.Count != 2) break;
                        string v = EmitExpr(c.Args[0].Value);
                        string vt = InferType(c.Args[0].Value);
                        // big-int mode: a value of unknown type that is a Python int may be a BigInteger or a 64-bit count (len())
                        string Is(Expr t)
                        {
                            string mt = MapType(t, c);
                            return mt == TypeInference.BigInt && IsUnknownType(vt) ? $"({v} is {mt} || {v} is long)" : $"{v} is {mt}";
                        }
                        if (c.Args[1].Value is TupleExpr types) return "(" + string.Join(" || ", types.Elts.Select(Is)) + ")";
                        return $"({Is(c.Args[1].Value)})";
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
                            var pos = c.Args.Where(a => a.Name == null).Select(a => a.Star ? ".. " + EmitIterable(a.Value) : EmitExpr(a.Value));
                            var kws = c.Args.Where(a => a.Name == "sep" || a.Name == "end").Select(a => $"{a.Name}: {EmitExpr(a.Value)}");
                            return $"{Rt("print_kw")}([{string.Join(", ", pos)}]{string.Concat(kws.Select(k => ", " + k))})";
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
                            string what = fn.Id == "list" ? "List<dynamic>" : fn.Id == "dict" ? "OrderedDictionary<dynamic, dynamic>" : "HashSet<dynamic>";
                            ImplicitDynamic(c, $"the element type of '{fn.Id}()'");
                            return $"new {what}()";
                        }
                        break;
                    case "super":
                        if (c.Args.Count == 0) return "base";
                        break;
                }
            }

            // int32(x), uint8(x), ...: explicit (checked) conversions to .NET's fixed-size numbers, for .NET APIs that need them
            if (c.Func is NameExpr cv && !IsShadowed(cv.Id) && NumericConversions.TryGetValue(cv.Id, out var convType) &&
                c.Args.Count == 1 && c.Args[0].Name == null && !c.Args[0].Star && !c.Args[0].DoubleStar)
                return $"checked(({convType})({EmitExpr(c.Args[0].Value)}))";

            if (ExpandStarArgs(c) is string expanded) return expanded;
            if (TryTypeExpr(c.Func, out var typeCode))
                return $"new {typeCode}({EmitArgs(c.Args, overloads: DotNetConstructors(c.Func), targetTypedAt: TypedParamPositions(c), typedKeywords: name => TypedKeyword(c, name), paramTypeAt: (i, name) => ParamTypeOf(c, i, name))})";

            // Counter() / deque() / OrderedDict() / defaultdict(f) where no declaration gives the element type
            if (TypeInference.IsEmptyContainer(c) && "cqoD".IndexOf(TypeInference.ContainerKind(c)) >= 0 && c.Func is NameExpr ecn && _staticTypes.Contains("Typhon.Stdlib.collections"))
            {
                char ek = TypeInference.ContainerKind(c);
                string d = ImplicitDynamic(c, $"the element type of '{ecn.Id}()'");
                string fv = ek == 'D' ? DefaultFactoryValueType(c) ?? (c.Args.Count > 0 && c.Args[0].Value is NameExpr f0 ? (f0.Id == "list" ? $"List<{d}>" : f0.Id == "set" ? $"HashSet<{d}>" : d) : d) : d;
                string type = ek == 'c' ? $"global::Typhon.Runtime.Counter<{d}>" : ek == 'q' ? $"global::Typhon.Runtime.deque<{d}>"
                    : ek == 'o' ? $"global::Typhon.Runtime.PyOrderedDict<{d}, {d}>" : $"global::Typhon.Runtime.defaultdict<{d}, {fv}>";
                return EmitEmptyContainer(c, type, ek);
            }

            // itertools.chain.from_iterable(xs): one function in Typhon's itertools
            if (c.Func is AttributeExpr cfi && cfi.Name == "from_iterable" && DottedName(cfi.Target) is string chainName && (chainName == "chain" || chainName.EndsWith(".chain")))
                return $"{(chainName.Contains('.') ? EmitExpr(((AttributeExpr)cfi.Target).Target) + "." : "")}chain_from_iterable({EmitArgs(c.Args)})";

            // methods called through a built-in type: dict.fromkeys(keys) / str.maketrans(a, b) are functions,
            // str.upper(s) / list.append(xs, x) are method calls on the first argument
            if (c.Func is AttributeExpr bt && bt.Target is NameExpr btn && BuiltinTypeNames.Contains(btn.Id) && !IsShadowed(btn.Id))
            {
                if (BuiltinClassMethods.Contains(bt.Name)) return $"{Rt(bt.Name)}({EmitArgs(c.Args)})";
                if (c.Args.Count >= 1 && c.Args[0].Name == null && !c.Args[0].Star)
                {
                    var rest = c.Args.Skip(1).ToList();
                    string recv = EmitExpr(c.Args[0].Value);
                    if (c.Args[0].Value is ConstExpr || c.Args[0].Value is BinOpExpr || c.Args[0].Value is UnaryExpr) recv = Paren(recv);
                    return $"{recv}.{Esc(bt.Name)}({EmitArgs(rest)})";
                }
            }

            // "...".format(a, b): routed to a helper so it does not clash with the builtin format(value, spec)
            if (c.Func is AttributeExpr fa && fa.Name == "format" && (fa.Target is ConstExpr fc && fc.Kind == ConstKind.Str || InferType(fa.Target) == "string"))
                return $"{Rt("str_format")}({EmitExpr(fa.Target)}{(c.Args.Count > 0 ? ", " : "")}{EmitArgs(c.Args)})";

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

            // xs.append(value) with a late-bound argument: C# dispatches the call dynamically, and dynamic dispatch never finds
            // extension methods, so Typhon's list/dict/set/str methods are called as the static methods they are
            if (c.Func is AttributeExpr em && RuntimeExtensionMethods.Contains(em.Name) && IsPythonContainer(InferType(em.Target)) &&
                c.Args.Any(a => InferType(a.Value) == "dynamic") && !(em.Name == "get" && c.Args.Count == 1))
            {
                string recv = EmitExpr(em.Target);
                var rest = EmitArgs(c.Args);
                return $"global::Typhon.Runtime.Builtins.{Esc(em.Name)}({recv}{(rest.Length > 0 ? ", " + rest : "")})";
            }
            // m.items() / keys() / values() / get(k, d) of a Mapping[K, V] (IReadOnlyDictionary)
            if (c.Func is AttributeExpr ro && InferType(ro.Target) is string rot && rot.StartsWith("IReadOnlyDictionary<") &&
                (ro.Name == "items" || ro.Name == "keys" || ro.Name == "values" || ro.Name == "get" && c.Args.Count == 2))
                return $"{Rt("__ro_" + ro.Name)}({EmitExpr(ro.Target)}{(c.Args.Count > 0 ? ", " + EmitArgs(c.Args) : "")})";

            // d.strip(), d.split(","), d.items() on a late-bound value: dynamic dispatch never finds extension methods, so a
            // Python value (str, list, dict, set, number) goes to the runtime's static method; anything else (a Typhon
            // object, a .NET object) keeps its own method
            if (c.Func is AttributeExpr dm && InferType(dm.Target) == "dynamic" && !c.Args.Any(a => a.Star || a.DoubleStar) && IsRuntimeMethod(dm.Name))
            {
                string r = NewTmp("r");
                string rest = EmitArgs(c.Args, dynamicCall: true);
                string sep = rest.Length > 0 ? ", " : "";
                return $"({Paren(EmitExpr(dm.Target))} is var {r} && {Rt("__is_pyvalue")}({r}) ? global::Typhon.Runtime.Builtins.{Esc(dm.Name)}({r}{sep}{rest}) : {r}.{Esc(dm.Name)}({rest}))";
            }
            string func = c.Func is NameExpr cfn ? EmitName(cfn) : EmitExpr(c.Func);
            if (c.Func is LambdaExpr || c.Func is IfExpr) func = Paren(func);
            var typedAt = TypedParamPositions(c);
            if (typedAt == null && c.Func is AttributeExpr pm && RuntimeExtensionMethods.Contains(pm.Name) && IsPythonContainer(InferType(pm.Target)) &&
                !InferType(pm.Target).Contains("dynamic"))
                typedAt = i => true;
            // standard-library functions are generic: an empty [] takes the element type the other arguments give
            if (typedAt == null && StdlibModuleOf(c, out _) != null) typedAt = i => true;
            // a call with a late-bound argument or receiver is dispatched dynamically: lambdas need a delegate type there
            bool dynamicCall = c.Args.Any(a => InferType(a.Value) == "dynamic") || c.Func is AttributeExpr da && InferType(da.Target) == "dynamic";
            return $"{func}({EmitArgs(c.Args, overloads: DotNetOverloads(c), targetTypedAt: typedAt, dynamicCall: dynamicCall, typedKeywords: name => TypedKeyword(c, name), paramTypeAt: (i, name) => ParamTypeOf(c, i, name))})";
        }

        static readonly HashSet<string> StaticConversions = new HashSet<string> { "int", "float", "str", "repr", "len" };

        /// <summary>Is the name a Python method the runtime implements as a static (extension) method (strip, split, items, ...)?</summary>
        bool IsRuntimeMethod(string name)
        {
            if (!_runtimeMethods.TryGetValue(name, out bool yes))
                _runtimeMethods[name] = yes = RuntimeExtensionMethods.Contains(name) ||
                    _r.GetMembers("Typhon.Runtime.Builtins", name).Any(m => m.IsMethod && m.IsStatic && m.Parameters.Count >= 1);
            return yes;
        }
        readonly Dictionary<string, bool> _runtimeMethods = new Dictionary<string, bool>();

        /// <summary>Python methods of str/list/dict/set that the Typhon runtime provides as extension methods.</summary>
        static readonly HashSet<string> RuntimeExtensionMethods = new HashSet<string>
        {
            "append", "extend", "insert", "pop", "remove", "index", "count", "sort", "reverse", "copy", "clear", "items", "keys", "values", "get",
            "setdefault", "update", "popitem", "add", "discard", "union", "difference", "intersection", "symmetric_difference", "issubset",
            "issuperset", "isdisjoint", "difference_update", "intersection_update", "symmetric_difference_update", "join", "split", "rsplit",
            "strip", "lstrip", "rstrip", "replace", "startswith", "endswith", "find", "rfind", "partition", "rpartition", "zfill", "center",
            "ljust", "rjust", "removeprefix", "removesuffix",
        };

        static bool IsPythonContainer(string t) => t != null && (t == "string" || t.StartsWith("List<") || IsSetType(t) || IsDictType(t));

        static readonly Dictionary<string, string> NumericConversions = new Dictionary<string, string>
        {
            ["int8"] = "sbyte", ["int16"] = "short", ["int32"] = "int", ["int64"] = "long",
            ["uint8"] = "byte", ["uint16"] = "ushort", ["uint32"] = "uint", ["uint64"] = "ulong", ["float32"] = "float", ["float64"] = "double",
        };

        // ───────────────────────────── .NET members: Python ints (long) where .NET takes int ─────────────────────────────

        /// <summary>Metadata name of the .NET type behind a C# type as the inference spells it; null for Typhon classes and unknown types.</summary>
        string DotNetMeta(string cs)
        {
            if (cs == null || cs == "dynamic" || cs == "object" || cs == TypeInference.Pending || cs.StartsWith("(")) return null;
            if (cs.EndsWith("?")) cs = cs.Substring(0, cs.Length - 1);
            if (cs.EndsWith("[]")) return "System.Array";
            switch (cs)
            {
                case "string": return "System.String";
                case "long": return "System.Int64";
                case "int": return "System.Int32";
                case "double": return "System.Double";
                case "bool": return "System.Boolean";
                case "char": return "System.Char";
                case "decimal": return "System.Decimal";
                case "float": return "System.Single";
            }
            if (cs.StartsWith("global::")) cs = cs.Substring("global::".Length);
            string name = cs; int arity = 0;
            int lt = cs.IndexOf('<');
            if (lt >= 0) { name = cs.Substring(0, lt); arity = SplitTypeArgs(GenericArg(cs)).Count; }
            return TryResolveType(name, arity, out var meta, out _) ? meta : null;
        }

        /// <summary>The .NET type a dotted name denotes when used as the receiver of a static member (Math, np, DateTime, ...).</summary>
        string DotNetStaticMeta(Expr target)
        {
            string dotted = DottedName(target);
            if (dotted == null || IsLocal(dotted.Split('.')[0]) || dotted == "self") return null;
            if (_moduleAliases.ContainsKey(dotted) || ModuleRef(dotted) != null) return null;
            return TryResolveType(dotted, 0, out var meta, out _) ? meta : null;
        }

        IReadOnlyList<Semantics.MemberSig> DotNetConstructors(Expr func)
        {
            Expr target = func is SubscriptExpr s ? s.Target : func;
            int arity = func is SubscriptExpr gs ? (gs.Index is TupleExpr t ? t.Elts.Count : 1) : 0;
            string dotted = DottedName(target);
            if (dotted == null || !TryResolveType(dotted, arity, out var meta, out _) || meta == null) return null;
            return _r.GetMembers(meta, ".ctor");
        }

        IReadOnlyList<Semantics.MemberSig> DotNetOverloads(CallExpr c)
        {
            if (c.Func is AttributeExpr fa)
            {
                if (DotNetStaticMeta(fa.Target) is string sm) return _r.GetMembers(sm, fa.Name).Where(m => m.IsMethod && m.IsStatic).ToList();
                if (fa.Target is NameExpr sn && sn.Id == "self") return null;
                var rt = InferType(fa.Target);
                if (ClassKeyOfType(rt, out _) != null) return null;
                if (DotNetMeta(rt) is string im) return _r.GetMembers(im, fa.Name).Where(m => m.IsMethod && !m.IsStatic).ToList();
                return null;
            }
            if (c.Func is NameExpr fn && !IsShadowed(fn.Id))
            {
                foreach (var st in _staticTypes)
                {
                    var ms = _r.GetMembers(st, fn.Id).Where(m => m.IsMethod && m.IsStatic).ToList();
                    if (ms.Count > 0) return ms;
                }
                // big-int mode: Python's builtins too (range(n) narrows a BigInteger, abs/pow/max keep it exact)
                if (_big)
                {
                    var bs = _r.GetMembers("Typhon.Runtime.Builtins", fn.Id).Where(m => m.IsMethod && m.IsStatic).ToList();
                    if (bs.Count > 0) return bs;
                }
            }
            return null;
        }

        /// <summary>Can the overload be called with these positional and named arguments (by count and names only)?</summary>
        static bool Applicable(Semantics.MemberSig m, int positional, List<string> named)
        {
            var ps = m.Parameters;
            bool hasParams = ps.Count > 0 && ps[ps.Count - 1].IsParams;
            int fixedCount = hasParams ? ps.Count - 1 : ps.Count;
            if (positional > fixedCount && !hasParams) return false;
            foreach (var n in named) if (!ps.Any(p => p.Name == n)) return false;
            for (int i = positional; i < fixedCount; i++) if (!ps[i].IsOptional && !named.Contains(ps[i].Name)) return false;
            return true;
        }

        static Semantics.ParamSig ParamAt(Semantics.MemberSig m, int i)
        {
            var ps = m.Parameters;
            if (i < ps.Count && !ps[i].IsParams) return ps[i];
            return ps.Count > 0 && ps[ps.Count - 1].IsParams && i >= ps.Count - 1 ? ps[ps.Count - 1] : null;
        }

        /// <summary>
        /// A Python int (C# long) argument passed to .NET: when no overload takes a long at that position but some take an
        /// int (or another narrower integer), the argument needs a checked narrowing conversion. Returns its type, or null.
        /// </summary>
        string NarrowingFor(IReadOnlyList<Semantics.MemberSig> overloads, List<Arg> args, Arg a, int argPos)
        {
            if (_big) return BigNarrowingFor(overloads, args, a, argPos);
            if (IsIntLiteral(a.Value) || InferType(a.Value) != "long") return null;
            int positional = args.Count(x => x.Name == null && !x.Star && !x.DoubleStar);
            var named = args.Where(x => x.Name != null).Select(x => x.Name).ToList();
            string narrowTo = null;
            foreach (var m in overloads)
            {
                if (!Applicable(m, positional, named)) continue;
                var p = a.Name != null ? m.Parameters.FirstOrDefault(x => x.Name == a.Name) : ParamAt(m, argPos);
                if (p == null) continue;
                if (p.Fit == Semantics.LongFit.Accepts) return null;
                if (p.Fit == Semantics.LongFit.Narrow && (narrowTo == null || p.NarrowTo == "int")) narrowTo = p.NarrowTo;
            }
            return narrowTo;
        }

        /// <summary>
        /// Big-int mode: a Python int argument (a BigInteger, or a 64-bit count from len()/a loop) passed to .NET or the runtime.
        /// No conversion when an overload takes a BigInteger (or object, a type parameter, ...); else an explicit, checked
        /// conversion to the numeric type the parameter has (long before int before the rest).
        /// </summary>
        string BigNarrowingFor(IReadOnlyList<Semantics.MemberSig> overloads, List<Arg> args, Arg a, int argPos)
        {
            if (IsIntLiteral(a.Value)) return null;
            string at = InferType(a.Value);
            if (at != TypeInference.BigInt && at != "long") return null;
            int positional = args.Count(x => x.Name == null && !x.Star && !x.DoubleStar);
            var named = args.Where(x => x.Name != null).Select(x => x.Name).ToList();
            string narrowTo = null;
            int Rank(string t) => t == "long" ? 0 : t == "int" ? 1 : t == "double" ? 2 : 3;
            foreach (var m in overloads)
            {
                if (!Applicable(m, positional, named)) continue;
                var p = a.Name != null ? m.Parameters.FirstOrDefault(x => x.Name == a.Name) : ParamAt(m, argPos);
                if (p == null) continue;
                // (a 64-bit count is converted like a BigInteger: a no-op for long, the usual checked narrowing for int)
                if (p.BigAccepts) return null;
                if (p.BigNarrowTo != null && (narrowTo == null || Rank(p.BigNarrowTo) < Rank(narrowTo))) narrowTo = p.BigNarrowTo;
            }
            return narrowTo;
        }

        /// <summary>Big-int mode: does some applicable overload take exactly a BigInteger at this argument's position?</summary>
        bool BigOverloadAt(IReadOnlyList<Semantics.MemberSig> overloads, List<Arg> args, Arg a, int argPos)
        {
            int positional = args.Count(x => x.Name == null && !x.Star && !x.DoubleStar);
            var named = args.Where(x => x.Name != null).Select(x => x.Name).ToList();
            foreach (var m in overloads)
            {
                if (!Applicable(m, positional, named)) continue;
                var p = a.Name != null ? m.Parameters.FirstOrDefault(x => x.Name == a.Name) : ParamAt(m, argPos);
                if (p != null && p.IsBigInteger) return true;
            }
            return false;
        }

        /// <summary>The member of a .NET receiver (static: a type name; instance: a value of a .NET type) named <paramref name="name"/>.</summary>
        IEnumerable<Semantics.MemberSig> DotNetMembers(Expr receiver, string name)
        {
            if (DotNetStaticMeta(receiver) is string sm) return _r.GetMembers(sm, name).Where(m => m.IsStatic);
            if (receiver is NameExpr sn && sn.Id == "self") return Enumerable.Empty<Semantics.MemberSig>();
            var rt = InferType(receiver);
            if (rt == null || ClassKeyOfType(rt, out _) != null) return Enumerable.Empty<Semantics.MemberSig>();
            return DotNetMeta(rt) is string im ? _r.GetMembers(im, name).Where(m => !m.IsStatic) : Enumerable.Empty<Semantics.MemberSig>();
        }

        /// <summary>Result type of a .NET property, field or method call when it is a special type all candidates agree on.</summary>
        string DotNetResultType(Expr receiver, string name, CallExpr call)
        {
            var ms = DotNetMembers(receiver, name).Where(m => m.IsMethod == (call != null)).ToList();
            if (call != null)
            {
                int positional = call.Args.Count(x => x.Name == null && !x.Star && !x.DoubleStar);
                var named = call.Args.Where(x => x.Name != null).Select(x => x.Name).ToList();
                ms = ms.Where(m => Applicable(m, positional, named)).ToList();
            }
            if (ms.Count == 0) return null;
            string t = ms[0].Type;
            return t != null && ms.All(m => m.Type == t) ? t : null;
        }

        /// <summary>A Python int assigned to a .NET property or field of a narrower integer type: the narrowing type, or null.</summary>
        string NarrowingForMember(AttributeExpr at, Expr value)
        {
            if (IsIntLiteral(value)) return null;
            string vt = InferType(value);
            if (_big && vt == TypeInference.BigInt)
                return DotNetMembers(at.Target, at.Name).FirstOrDefault(x => !x.IsMethod)?.ValueBigNarrowTo;
            if (vt != "long") return null;
            var m = DotNetMembers(at.Target, at.Name).FirstOrDefault(x => !x.IsMethod);
            return m != null && m.ValueFit == Semantics.LongFit.Narrow ? m.ValueNarrowTo : null;
        }

        string EmitIndex(Expr e)
        {
            if (e is UnaryExpr u && u.Op == "-" && u.Operand is ConstExpr c && c.Kind == ConstKind.Int) return "^" + EmitConst(c);
            return EmitExpr(e);
        }

        string EmitSubscript(SubscriptExpr s)
        {
            if (Obs != null && !(s.Index is SliceExpr) && ElementKey(s.Target, out char rk) is string rkey && (rk == 'D' || rk == 'c'))
                Obs.AddElement(rkey, (PyType(InferType(s.Index)) ?? TypeInference.Unknown) + "→" + (rk == 'c' ? "long" : TypeInference.Pending));
            // a generic type used as a value, e.g. Stack[str].of(...) or Dictionary[str, int] passed around
            if (!(s.Index is SliceExpr) && TryTypeExpr(s, out var genericType)) return genericType;
            string target = EmitExpr(s.Target);
            if (s.Target is ConstExpr || s.Target is BinOpExpr || s.Target is UnaryExpr || s.Target is AwaitExpr) target = Paren(target);
            if (s.Index is SliceExpr sl)
            {
                // Python slicing semantics (copies, clamps out-of-range bounds, negative indices, steps)
                string step = sl.Step != null ? ", " + SliceBound(sl.Step) : "";
                return $"{Rt("slice")}({target}, {(sl.Lower != null ? SliceBound(sl.Lower) : "null")}, {(sl.Upper != null ? SliceBound(sl.Upper) : "null")}{step})";
            }
            string targetType = InferType(s.Target);
            // s[i] is a one-character str
            if (targetType == "string" && !(s.Index is TupleExpr)) return $"{Rt("str_at")}({target}, {EmitPosition(s.Index, targetType)})";
            // t[i] of a tuple of fixed shape: ItemN for a constant index, else the ITuple indexer (items of one type)
            var tupleItems = TupleElementTypes(targetType);
            if (tupleItems != null && !(s.Index is SliceExpr) && !(s.Index is TupleExpr))
            {
                if (ConstIntValue(s.Index) is long k && k >= -tupleItems.Count && k < tupleItems.Count) return $"{target}.Item{(k < 0 ? k + tupleItems.Count : k) + 1}";
                string common = tupleItems.Distinct().Count() == 1 ? tupleItems[0] : "object";
                return $"{Rt("tuple_at")}<{common}>({target}, {SliceBound(s.Index)})";
            }
            if (s.Index is TupleExpr t && !t.Parenthesized)
                return $"{target}[{string.Join(", ", t.Elts.Select(x => x is SliceExpr xs ? EmitRange(xs, targetType) : EmitPosition(x, targetType)))}]";
            // a list or array position that may be negative counts from the end (Python's xs[-k], xs[i - 1] at i == 0)
            if (!IsIntLiteral(s.Index) && !IsNonNegative(s.Index) && targetType != null && !(s.Index is TupleExpr) && IsIntegral(InferType(s.Index)) &&
                (targetType.StartsWith("List<") || targetType.EndsWith("[]") || targetType.StartsWith("IList<") || targetType.StartsWith("IReadOnlyList<")))
            {
                string count = targetType.EndsWith("[]") ? "Length" : "Count";
                bool simple = s.Target is NameExpr || s.Target is AttributeExpr sa && (sa.Target is NameExpr || sa.Target is AttributeExpr sa2 && sa2.Target is NameExpr);
                if (targetType.StartsWith("List<") || targetType.EndsWith("[]")) return $"{Rt("__at")}({target}, {EmitIndex(s.Index)})";
                if (simple) return $"{target}[{Rt("__pos")}({EmitIndex(s.Index)}, {target}.{count})]";
            }
            return $"{target}[{EmitPosition(s.Index, targetType)}]";
        }

        /// <summary>
        /// Names whose value cannot be negative where they are read: counters of range loops from a non-negative start with a
        /// positive step that the body does not assign. Their positions need no count-from-the-end check.
        /// </summary>
        readonly HashSet<string> _nonNegative = new HashSet<string>();

        /// <summary>
        /// Locals of a function body that only ever hold non-negative ints: every assignment stores a non-negative value
        /// (literals, len(), sums and products of such locals, += of one, range counters from a non-negative start). Computed
        /// as a greatest fixpoint: assume all, drop those with an assignment that is not shown non-negative, repeat.
        /// </summary>
        HashSet<string> AlwaysNonNegative(List<Stmt> body, IEnumerable<string> parameters)
        {
            var assigns = new List<(string Name, Expr Value, string Op)>();
            var bad = new HashSet<string>(parameters);
            var all = new HashSet<string>();
            void Bind(Expr t, Expr v, string op)
            {
                switch (t)
                {
                    case NameExpr n: all.Add(n.Id); if (v == null) bad.Add(n.Id); else assigns.Add((n.Id, v, op)); break;
                    case TupleExpr te when op == "=" && v is TupleExpr vt && vt.Elts.Count == te.Elts.Count && !te.Elts.Any(x => x is StarredExpr):
                        for (int i = 0; i < te.Elts.Count; i++) Bind(te.Elts[i], vt.Elts[i], "="); break;
                    case TupleExpr te: foreach (var x in te.Elts) Bind(x, null, op); break;
                    case ListExpr le: foreach (var x in le.Elts) Bind(x, null, op); break;
                    case StarredExpr st: Bind(st.Value, null, op); break;
                }
            }
            var nonNegativeMarker = new ConstExpr { Kind = ConstKind.Int, Raw = "0" };
            void Visit(object o)
            {
                if (o == null) return;
                if (o is System.Collections.IEnumerable list && !(o is string)) { foreach (var i in list) Visit(i); return; }
                if (!(o is Node node)) return;
                switch (node)
                {
                    case FunctionDef fd: bad.Add(fd.Name); foreach (var g in NamesBoundIn(fd.Body, nonlocalOnly: true)) bad.Add(g); return;
                    case ClassDef cd: bad.Add(cd.Name); return;
                    case LambdaExpr _: return;
                    case ComprehensionExpr ce: foreach (var g in ce.Generators) Visit(g.Iter); return; // its targets are its own
                    case AssignStmt a: foreach (var t in a.Targets) Bind(t, a.Value, "="); break;
                    case AnnAssignStmt an: if (an.Value != null) Bind(an.Target, an.Value, "="); break;
                    case AugAssignStmt au: Bind(au.Target, au.Value, au.Op); break;
                    case NamedExpr ne: Bind(ne.Target, ne.Value, "="); break;
                    case ForStmt f:
                    {
                        // for i in range(a, b[, k > 0]): i >= a, so i is non-negative when a is
                        List<Expr> ra = null;
                        bool counter = f.Target is NameExpr && !f.IsAsync && IsBuiltinRange(f.Iter, out ra) && (ra.Count < 3 || ConstIntValue(ra[2]) is long st && st > 0);
                        Bind(f.Target, counter ? (ra.Count < 2 ? nonNegativeMarker : ra[0]) : null, "=");
                        break;
                    }
                    default:
                        foreach (var name in NamesBoundIn(new List<Stmt>(), node)) { all.Add(name); bad.Add(name); }
                        break;
                }
                foreach (var c in AstWalker.Children(node)) Visit(c);
            }
            Visit(body);
            var result = new HashSet<string>(all.Where(x => !bad.Contains(x)));
            bool changed = true;
            var saved = new HashSet<string>(_nonNegative);
            while (changed)
            {
                changed = false;
                _nonNegative.Clear(); _nonNegative.UnionWith(result);
                foreach (var (name, value, op) in assigns)
                {
                    if (!result.Contains(name)) continue;
                    // op: "=" for assignments, the operator of augmented ones (x += v is "+")
                    bool ok = op == "=" || op == "+" || op == "*" || op == "//" || op == ">>" ? IsNonNegative(value)
                            : op == "%" && value is ConstExpr mc && mc.Kind == ConstKind.Int && mc.Raw != "0";
                    if (!ok) { result.Remove(name); changed = true; }
                }
            }
            _nonNegative.Clear(); _nonNegative.UnionWith(saved);
            return result;
        }

        /// <summary>Names a statement binds other than by assignment (with/except/import/def/class/global/del/match), or nonlocal/global names of a nested body.</summary>
        static IEnumerable<string> NamesBoundIn(List<Stmt> body, Node node = null, bool nonlocalOnly = false)
        {
            if (nonlocalOnly)
            {
                var names = new List<string>();
                AstWalker.VisitAll(body, n => { if (n is GlobalStmt g) names.AddRange(g.Names); });
                return names;
            }
            switch (node)
            {
                case WithStmt w: return w.Items.Where(i => i.Var != null).SelectMany(i => NamesIn(i.Var));
                case ExceptHandler h when h.Name != null: return new[] { h.Name };
                case ImportStmt im: return im.Names.Select(a => a.AsName ?? a.Name.Split('.')[0]);
                case ImportFromStmt imf: return imf.Names.Select(a => a.AsName ?? a.Name);
                case GlobalStmt g: return g.Names;
                case DelStmt d: return d.Targets.SelectMany(NamesIn);
                case MatchStmt m: return m.Cases.SelectMany(c => LocalsAnalyzer.PatternCaptures(c.Pattern, null).Select(kv => kv.Key));
                default: return Enumerable.Empty<string>();
            }
        }

        static IEnumerable<string> NamesIn(Expr t)
        {
            switch (t)
            {
                case NameExpr n: yield return n.Id; break;
                case TupleExpr te: foreach (var x in te.Elts) foreach (var y in NamesIn(x)) yield return y; break;
                case ListExpr le: foreach (var x in le.Elts) foreach (var y in NamesIn(x)) yield return y; break;
                case StarredExpr s: foreach (var y in NamesIn(s.Value)) yield return y; break;
            }
        }

        /// <summary>Does the statement (anywhere inside, nested scopes included) bind the name?</summary>
        static bool BindsName(Stmt s, string name)
        {
            bool found = false;
            AstWalker.VisitAll(s, n =>
            {
                if (found) return;
                switch (n)
                {
                    case AssignStmt a: found = a.Targets.Any(t => NamesIn(t).Contains(name)); break;
                    case AugAssignStmt au: found = NamesIn(au.Target).Contains(name); break;
                    case AnnAssignStmt an: found = NamesIn(an.Target).Contains(name); break;
                    case ForStmt f: found = NamesIn(f.Target).Contains(name); break;
                    case NamedExpr ne: found = ne.Target.Id == name; break;
                    case FunctionDef fd: found = fd.Name == name; break;
                    case ClassDef cd: found = cd.Name == name; break;
                    default: found = NamesBoundIn(null, n).Contains(name); break;
                }
            });
            return found;
        }

        bool IsNonNegative(Expr e)
        {
            switch (e)
            {
                case ConstExpr c: return c.Kind == ConstKind.Int;
                case NameExpr n: return _nonNegative.Contains(n.Id);
                case BinOpExpr b when b.Op == "+" || b.Op == "*" || b.Op == "//": return IsNonNegative(b.Left) && IsNonNegative(b.Right);
                case BinOpExpr b when b.Op == "%": return IsNonNegative(b.Right) && b.Right is ConstExpr;
                case CallExpr c when c.Func is NameExpr f && f.Id == "len" && !IsShadowed("len"): return true;
                default: return false;
            }
        }

        /// <summary>A slice bound (or other 64-bit position argument of the runtime); in big-int mode a BigInteger is narrowed, checked.</summary>
        string SliceBound(Expr e) => _big && !IsIntLiteral(e) && InferType(e) == TypeInference.BigInt ? $"checked((long)({EmitExpr(e)}))" : EmitExpr(e);

        string EmitRange(SliceExpr sl, string targetType)
        {
            if (sl.Step != null) Unsupported(sl, "slice steps inside multi-dimensional subscripts");
            return (sl.Lower != null ? EmitPosition(sl.Lower, targetType) : "") + ".." + (sl.Upper != null ? EmitPosition(sl.Upper, targetType) : "");
        }

        /// <summary>
        /// An index into a value of type <paramref name="targetType"/>. Lists, arrays, strings (and .NET indexers in general)
        /// take an int position: a Python int (long) is narrowed, checked. Dicts and Typhon classes take Python values as is.
        /// </summary>
        string EmitPosition(Expr index, string targetType)
        {
            string code = EmitIndex(index);
            if (IsIntLiteral(index) || !IsPositional(targetType)) return code;
            string it = InferType(index);
            bool sequence = targetType != null && (targetType == "string" || targetType.EndsWith("[]") || targetType.StartsWith("List<") ||
                                                    targetType.StartsWith("IList<") || targetType.StartsWith("IReadOnlyList<"));
            // a list/array/str position is an int: narrow anything that is not already one (long, dynamic, unknown)
            bool narrow = sequence ? !IsSmallInt(it) : it == "long" || it == "ulong" || it == TypeInference.BigInt;
            return narrow ? $"checked((int)({code}))" : code;
        }

        bool IsPositional(string t)
        {
            if (t == null || t == "dynamic") return true;
            if (IsDictType(t)) return false;
            if (_cls != null && t == _cls.CsSelfType) return false;
            return ClassKeyOfType(t, out _) == null;
        }

        bool IsSequenceTyped(Expr e) { var t = InferType(e); return t == "string" || (t != null && t.StartsWith("List<")); }

        static bool IsSequenceLiteral(Expr e) => e is ConstExpr c && c.Kind == ConstKind.Str || e is FStringExpr || e is ListExpr;

        static readonly HashSet<string> ArithmeticOps = new HashSet<string> { "+", "-", "*", "/", "//", "%", "**", "<<", ">>" };

        internal static bool IsSetType(string t) => t != null && (t.StartsWith("HashSet<") || t.StartsWith("ISet<") || t.StartsWith("IReadOnlySet<"));
        /// <summary>Key and value type of a mapping type (a Counter's values are ints); null if not a mapping.</summary>
        internal static List<string> DictTypes(string t)
        {
            if (!IsDictType(t)) return null;
            var args = SplitTypeArgs(GenericArg(t));
            if (t.StartsWith("global::Typhon.Runtime.Counter<") && args.Count == 1) return new List<string> { args[0], "long" };
            return args.Count == 2 ? args : null;
        }

        /// <summary>A mapping type as the inference spells it (Python's dict is OrderedDictionary; .NET dictionaries count too).</summary>
        internal static bool IsDictType(string t) => t != null && t.EndsWith(">") && (t.StartsWith("OrderedDictionary<") || t.StartsWith("Dictionary<") || t.StartsWith("IDictionary<") ||
            t.StartsWith("global::Typhon.Runtime.Counter<") || t.StartsWith("global::Typhon.Runtime.defaultdict<") || t.StartsWith("global::Typhon.Runtime.PyOrderedDict<") ||
            t.StartsWith("IReadOnlyDictionary<") || t.StartsWith("SortedDictionary<") || t.StartsWith("global::System.Collections.Generic.Dictionary<") || t.StartsWith("global::System.Collections.Generic.OrderedDictionary<"));

        /// <summary>An operand of arithmetic: an int literal is a long, unless the other side is a narrower .NET integer.</summary>
        string Operand(Expr e, Expr other) => IsIntLiteral(e) && !IsSmallInt(InferType(other)) ? EmitValue(e) : EmitExpr(e);

        /// <summary>k when e is the int literal 2**k (k &gt;= 1), else -1.</summary>
        static int PowerOfTwoExponent(Expr e)
        {
            if (!(e is ConstExpr c) || c.Kind != ConstKind.Int || !long.TryParse(c.Raw.Replace("_", ""), out var v) || v < 2 || (v & (v - 1)) != 0) return -1;
            int k = 0; while ((1L << k) != v) k++;
            return k;
        }

        /// <summary>
        /// Python's value of an integer expression made only of literals (10**6, 50 * 10**6, 1 &lt;&lt; 20), computed at compile time,
        /// or null. Folding makes such values constants (C# default parameter values must be) and saves the pow() call.
        /// </summary>
        static BigInteger? FoldInt(Expr e)
        {
            switch (e)
            {
                case ConstExpr c when c.Kind == ConstKind.Int:
                    return long.TryParse(c.Raw.Replace("_", ""), out var lit) ? lit : (BigInteger?)null;
                case UnaryExpr u when u.Op == "-" || u.Op == "+" || u.Op == "~":
                {
                    var v = FoldInt(u.Operand);
                    if (v == null) return null;
                    return u.Op == "-" ? -v.Value : u.Op == "+" ? v.Value : -v.Value - 1;
                }
                case BinOpExpr b:
                {
                    var l = FoldInt(b.Left); if (l == null) return null;
                    var r = FoldInt(b.Right); if (r == null) return null;
                    BigInteger a = l.Value, d = r.Value;
                    switch (b.Op)
                    {
                        case "+": return a + d;
                        case "-": return a - d;
                        case "*": return a * d;
                        case "//": return d.IsZero ? (BigInteger?)null : FloorDiv(a, d);
                        case "%": return d.IsZero ? (BigInteger?)null : a - d * FloorDiv(a, d);
                        case "**": return d.Sign < 0 || d > 512 ? (BigInteger?)null : BigInteger.Pow(a, (int)d);
                        case "<<": return d.Sign < 0 || d > 512 ? (BigInteger?)null : a << (int)d;
                        case ">>": return d.Sign < 0 ? (BigInteger?)null : d > 512 ? (a.Sign < 0 ? BigInteger.MinusOne : BigInteger.Zero) : a >> (int)d;
                        case "&": return a & d;
                        case "|": return a | d;
                        case "^": return a ^ d;
                    }
                    return null;
                }
                default: return null;
            }
        }

        static BigInteger FloorDiv(BigInteger a, BigInteger b)
        {
            var q = BigInteger.DivRem(a, b, out var rem);
            return !rem.IsZero && (rem.Sign < 0) != (b.Sign < 0) ? q - 1 : q;
        }

        string EmitBinOp(BinOpExpr b)
        {
            // literal-only integer arithmetic is folded when the result fits 64 bits (otherwise it raises OverflowError at run time)
            if (FoldInt(b) is BigInteger folded && (_big || folded >= long.MinValue && folded <= long.MaxValue))
                return IntConstant(folded);
            string l = Operand(b.Left, b.Right), r = Operand(b.Right, b.Left);
            string lType = InferType(b.Left), rType = InferType(b.Right);
            if (_big && ArithmeticOps.Contains(b.Op))
            {
                const string Big = TypeInference.BigInt;
                // big-int mode: 64-bit operands (loop counters, len()) of an operator that can leave 64 bits are computed as
                // BigIntegers; a BigInteger next to a float is a float (C# has no implicit conversion between them)
                if (WideningOps.Contains(b.Op) && IsIntegral(lType) && IsIntegral(rType) && lType != Big && rType != Big && lType != "bool" && rType != "bool")
                    l = $"(({Big}){Paren(l)})";
                else if (lType == Big && (rType == "double" || rType == "float")) l = $"((double){Paren(l)})";
                else if (rType == Big && (lType == "double" || lType == "float")) r = $"((double){Paren(r)})";
            }
            // bool is an int in Python: True + True == 2
            if (ArithmeticOps.Contains(b.Op))
            {
                if (lType == "bool") { l = $"({Paren(l)} ? 1L : 0L)"; lType = "long"; }
                if (rType == "bool") { r = $"({Paren(r)} ? 1L : 0L)"; rType = "long"; }
            }
            // sets: - | & ^ are set algebra; dicts: | merges
            if ((b.Op == "-" || b.Op == "|" || b.Op == "&" || b.Op == "^") && (IsSetType(lType) || IsSetType(rType)))
                return $"{Rt(b.Op == "-" ? "set_sub" : b.Op == "|" ? "set_or" : b.Op == "&" ? "set_and" : "set_xor")}({l}, {r})";
            if (b.Op == "|" && IsDictType(lType) && IsDictType(rType)) return $"{Rt("dict_or")}({l}, {r})";
            // (1, 2) + (3,): tuples of fixed shape concatenate into a tuple of the combined shape
            if (b.Op == "+" && TupleElementTypes(lType) is List<string> lItems && TupleElementTypes(rType) is List<string> rItems)
            {
                var items = lItems.Select((_, i) => $"__ta.Item{i + 1}").Concat(rItems.Select((_, i) => $"__tb.Item{i + 1}"));
                return $"(({l}, {r}) switch {{ var (__ta, __tb) => ({string.Join(", ", items)}) }})";
            }
            if ((b.Op == "-" || b.Op == "|" || b.Op == "&" || b.Op == "^") && lType == "dynamic" && rType == "dynamic")
                return $"{Rt(b.Op == "-" ? "sub" : b.Op == "|" ? "or_" : b.Op == "&" ? "and_" : "xor_")}({l}, {r})";
            // x // 2**k and x % 2**k on integers are exactly an arithmetic shift and a mask in Python (floor semantics, any sign)
            if ((b.Op == "//" || b.Op == "%") && PowerOfTwoExponent(b.Right) is int k && k > 0 && IsIntegral(InferType(b.Left)))
                return b.Op == "//" ? $"({Paren(l)} >> {k})" : $"({Paren(l)} & {(1L << k) - 1})";
            switch (b.Op)
            {
                case "<<": return $"{Rt("lshift")}({l}, {r})";
                case ">>": return $"{Rt("rshift")}({l}, {r})";
                case "/": return $"{Rt("truediv")}({l}, {r})";
                case "//": return $"{Rt("floordiv")}({l}, {r})";
                case "**": return $"{Rt("pow")}({l}, {r})";
                case "@": return $"{Paren(l)}.__matmul__({r})";
                case "*" when IsSequenceLiteral(b.Left) || IsSequenceLiteral(b.Right) || IsSequenceTyped(b.Left) || IsSequenceTyped(b.Right): return $"{Rt("__repeat")}({l}, {r})";
                // late-bound operands may be a str or list at run time: "ab" * 2 must repeat, not fail in the binder
                case "*" when InferType(b.Left) == "dynamic" || InferType(b.Right) == "dynamic": return $"{Rt("mul")}({l}, {r})";
                case "+" when InferType(b.Left) == "dynamic" && InferType(b.Right) == "dynamic": return $"{Rt("add")}({l}, {r})";
                case "+" when IsListy(b.Left) || IsListy(b.Right): return $"{Rt("concat")}({l}, {r})";
                case "%" when b.Left is ConstExpr sc && sc.Kind == ConstKind.Str:
                    Unsupported(b, "'%' string formatting (use an f-string)");
                    return l;
                case "%": return $"{Rt("mod")}({l}, {r})"; // Python modulo: result has the sign of the divisor
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

        static readonly HashSet<string> NumericOrText = new HashSet<string> { "long", "int", "short", "byte", "sbyte", "ushort", "uint", "ulong", "double", "float", "decimal", "char", "bool", "string", TypeInference.BigInt };

        static bool PythonOrdered(string t) => t != null && (t == "string" || t.StartsWith("List<") || t.StartsWith("IList<") || t.StartsWith("IReadOnlyList<") ||
            IsSetType(t) || t.StartsWith("global::Typhon.Runtime.PyTuple<") || t.StartsWith("(") || t.StartsWith("ValueTuple<"));
        static bool IsUnknownType(string t) => t == null || t == "dynamic" || t == "object" || t == TypeInference.Pending;

        static bool IsNumeric(string t) => t != null && t != "char" && t != "bool" && t != "string" && NumericOrText.Contains(t);

        /// <summary>Do Python's == and C#'s == agree for these operand types? (numbers, str, bool, None, Typhon classes, enums)</summary>
        bool NativeEquality(Expr left, Expr right, string lt, string rt)
        {
            if (IsNone(left) || IsNone(right)) return true;
            if (lt != null && rt != null && NumericOrText.Contains(lt) && NumericOrText.Contains(rt))
                return lt == rt || IsNumeric(lt) && IsNumeric(rt);
            string known = lt ?? rt;
            if (known == null || known == "dynamic" || known == "object" || known == TypeInference.Pending) return false;
            bool isClass = ClassKeyOfType(known, out _) != null || (_cls != null && known == _cls.CsSelfType);
            return isClass && (lt == rt || lt == null || rt == null);
        }

        string EmitComparison(Expr left, string op, Expr right)
        {
            string l = EmitExpr(left), r = EmitExpr(right);
            string lt = InferType(left), rt = InferType(right);
            // big-int mode: an int compared with a float compares as floats (C# does not convert BigInteger implicitly)
            if (lt == TypeInference.BigInt && (rt == "double" || rt == "float")) l = $"((double){Paren(l)})";
            if (rt == TypeInference.BigInt && (lt == "double" || lt == "float")) r = $"((double){Paren(r)})";
            if (op == "==" || op == "!=")
            {
                // Python compares containers and tuples by contents, C# by reference: eq() unless both sides agree
                if (NativeEquality(left, right, lt, rt)) return $"({l} {op} {r})";
                return op == "==" ? $"{Rt("eq")}({l}, {r})" : $"!{Rt("eq")}({l}, {r})";
            }
            if (op == "<" || op == "<=" || op == ">" || op == ">=")
            {
                if (lt == "string" && rt == "string") return $"(string.CompareOrdinal({l}, {r}) {op} 0)";
                // Python's own containers order by Python's rules (sequences and tuples lexicographically, sets by inclusion);
                // values of unknown type decide at run time; every other type keeps its own operators (classes, ndarray, decimal)
                if (PythonOrdered(lt) || PythonOrdered(rt) || IsUnknownType(lt) || IsUnknownType(rt))
                {
                    string helper = op == "<" ? "lt" : op == "<=" ? "le" : op == ">" ? "gt" : "ge";
                    return $"{Rt(helper)}({l}, {r})";
                }
            }
            switch (op)
            {
                case "in": return $"{Rt("contains")}({(right is TupleExpr ? EmitIterable(right) : r)}, {l})";
                case "not in": return $"!{Rt("contains")}({(right is TupleExpr ? EmitIterable(right) : r)}, {l})";
                case "is": return IsNone(right) ? $"({l} is null)" : IsNone(left) ? $"({r} is null)" : $"ReferenceEquals({l}, {r})";
                case "is not": return IsNone(right) ? $"({l} is not null)" : IsNone(left) ? $"({r} is not null)" : $"!ReferenceEquals({l}, {r})";
                default: return $"({l} {op} {r})";
            }
        }

        /// <summary>
        /// [*a, x, *b] / {*a, x} / (*a, x): a C# collection expression with spreads, given its type by a cast (the element type
        /// comes from the items and the element types of the starred iterables).
        /// </summary>
        string EmitSpread(List<Expr> elts, string collection, Node at)
        {
            string elem = SpreadElementType(elts);
            if (elem == null) elem = ImplicitDynamic(at, "the element type of a collection with starred items");
            var parts = elts.Select(x => x is StarredExpr st ? ".. " + EmitIterable(st.Value) : EmitValue(x));
            return $"(({collection}<{elem}>)[{string.Join(", ", parts)}])";
        }

        string SpreadElementType(List<Expr> elts)
        {
            var ts = elts.Select(x => PyType(x is StarredExpr st ? ElementTypeOf(InferType(st.Value)) : InferType(x))).Distinct().ToList();
            if (ts.Contains(TypeInference.Pending)) return TypeInference.Pending;
            if (ts.Count == 1 && ts[0] != null && ts[0] != "dynamic") return ts[0];
            if (ts.All(t => IsIntegral(t) || t == "double")) return ts.Contains("double") ? "double" : IntT;
            return null;
        }

        string EmitList(ListExpr l, bool targetTyped)
        {
            if (l.Elts.Any(x => x is StarredExpr))
            {
                if (targetTyped) return "[" + string.Join(", ", l.Elts.Select(x => x is StarredExpr st ? ".. " + EmitIterable(st.Value) : EmitExpr(x))) + "]";
                return EmitSpread(l.Elts, "List", l);
            }
            if (targetTyped) return "[" + string.Join(", ", l.Elts.Select(x => EmitExpr(x))) + "]";
            var elts = l.Elts.Select(x => EmitValue(x)).ToList();
            if (elts.Count == 0)
            {
                ImplicitDynamic(l, "the element type of an empty list (annotate the variable, e.g. 'xs: list[int] = []')");
                return "new List<dynamic>()";
            }
            // elements of .NET's narrower integer types become Python ints
            string lt = InferType(l);
            if (lt == "List<long>" && l.Elts.Any(x => IsSmallInt(InferType(x)))) return $"{Rt("__list")}<long>({string.Join(", ", elts)})";
            if (lt == null && l.Elts.Count > 1 && MixedTypes(l.Elts))
                return $"{Rt("__list")}<{ImplicitDynamic(l, "the element type of a list with values of different types")}>({string.Join(", ", elts)})";
            return $"{Rt("__list")}({string.Join(", ", elts)})";
        }

        string EmitDict(DictExpr d, bool targetTyped)
        {
            // {**a, 'k': v, **b}: merged left to right
            if (d.Keys.Any(k => k == null))
            {
                var parts = new List<string>();
                var literal = new List<string>();
                void Flush() { if (literal.Count > 0) { parts.Add($"{Rt("__dict")}({string.Join(", ", literal)})"); literal.Clear(); } }
                for (int i = 0; i < d.Keys.Count; i++)
                {
                    if (d.Keys[i] == null) { Flush(); parts.Add(EmitExpr(d.Values[i])); }
                    else literal.Add($"({EmitValue(d.Keys[i])}, {EmitValue(d.Values[i])})");
                }
                Flush();
                // parts of different key/value types: merged into the unified types
                var kts = new List<string>(); var vts = new List<string>();
                for (int i = 0; i < d.Keys.Count; i++)
                {
                    if (d.Keys[i] != null) { kts.Add(PyType(InferType(d.Keys[i]))); vts.Add(PyType(InferType(d.Values[i]))); continue; }
                    var dt = InferType(d.Values[i]);
                    var kv = IsDictType(dt) ? SplitTypeArgs(GenericArg(dt)) : null;
                    kts.Add(kv?[0]); vts.Add(kv?[1]);
                }
                string Unify(List<string> ts, string what)
                {
                    var u = ts.Distinct().ToList();
                    if (u.Count == 1 && u[0] != null) return u[0];
                    if (u.All(t => IsIntegral(t) || t == "double")) return "double";
                    return ImplicitDynamic(d, what);
                }
                if (kts.Distinct().Count() > 1 || vts.Distinct().Count() > 1 || kts.Contains(null) || vts.Contains(null))
                    return $"{Rt("__dict_merge_as")}<{Unify(kts, "the key type of a merged dict")}, {Unify(vts, "the value type of a merged dict")}>({string.Join(", ", parts)})";
                return $"{Rt("__dict_merge")}({string.Join(", ", parts)})";
            }
            if (targetTyped)
                return d.Keys.Count == 0 ? "new()" : "new() { " + string.Join(", ", d.Keys.Select((k, i) => $"[{EmitExpr(k, targetTyped: true)}] = {EmitExpr(d.Values[i], targetTyped: true)}")) + " }";
            if (d.Keys.Count == 0)
            {
                ImplicitDynamic(d, "the key/value types of an empty dict (annotate the variable, e.g. 'd: dict[str, int] = {}')");
                return "new OrderedDictionary<dynamic, dynamic>()";
            }
            string typeArgs = "";
            if (d.Keys.Count > 1 && (MixedTypes(d.Keys) || MixedTypes(d.Values)))
            {
                // {"name": "Ada", "age": 36}: no common C# type, the differing side is dynamic
                string kt = MixedTypes(d.Keys) ? ImplicitDynamic(d, "the key type of a dict with keys of different types") : PyType(InferType(d.Keys[0]));
                string vt = MixedTypes(d.Values) ? ImplicitDynamic(d, "the value type of a dict with values of different types") : PyType(InferType(d.Values[0]));
                if (kt != null && vt != null && kt != TypeInference.Pending && vt != TypeInference.Pending) typeArgs = $"<{kt}, {vt}>";
            }
            return Rt("__dict") + typeArgs + "(" + string.Join(", ", d.Keys.Select((k, i) => $"({EmitValue(k)}, {EmitValue(d.Values[i])})")) + ")";
        }

        /// <summary>Types of the variables of the comprehensions being emitted (the lambdas' parameters), innermost last.</summary>
        readonly List<Scope> _comprehensionTypes = new List<Scope>();

        string EmitComprehension(ComprehensionExpr c, string targetType = null)
        {
            var bindings = new Scope();
            _comprehensionTypes.Add(bindings);
            try { return EmitComprehensionCore(c, bindings, targetType); }
            finally { _comprehensionTypes.Remove(bindings); }
        }

        /// <summary>The element of a comprehension for a declared element type: nested comprehensions follow it, an int becomes a float.</summary>
        string EmitElementAs(Expr elt, string targetElem)
        {
            if (targetElem == null) return EmitValue(elt);
            if (elt is ComprehensionExpr inner) return EmitComprehension(inner, targetElem);
            if (targetElem == "double" && IsIntegral(InferType(elt)) && InferType(elt) != TypeInference.BigInt) return $"(double){Paren(EmitValue(elt))}";
            // [0] for a list[float] element: a list of the declared type (a collection expression has no type in a lambda)
            if (elt is ListExpr le && targetElem.StartsWith("List<") && targetElem.EndsWith(">") && !le.Elts.Any(x => x is StarredExpr))
                return $"new {targetElem} {{ {string.Join(", ", le.Elts.Select(x => EmitElementAs(x, GenericArg(targetElem))))} }}";
            return EmitValue(elt);
        }

        string EmitComprehensionCore(ComprehensionExpr c, Scope bindings, string targetType)
        {
            string targetElem = targetType != null && targetType.EndsWith(">") && (targetType.StartsWith("List<") || targetType.StartsWith("HashSet<") ||
                                targetType.StartsWith("IEnumerable<") || targetType.StartsWith("IReadOnlyList<") || targetType.StartsWith("IList<")) &&
                                c.Kind != ComprehensionKind.Dict ? GenericArg(targetType) : null;
            string Gen(int i)
            {
                var g = c.Generators[i];
                if (g.IsAsync) Unsupported(c, "async comprehensions");
                string iter = EmitIterable(g.Iter);
                BindTargetTypes(bindings, g.Target, IterElementType(InferType(g.Iter)));
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
                    string elt = c.Kind == ComprehensionKind.Dict ? $"({EmitValue(c.Elt)}, {EmitValue(c.ValueElt)})" : EmitElementAs(c.Elt, targetElem);
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
                case ComprehensionKind.Dict: return $"{Rt("__to_dict")}({q})"; // later keys win, insertion order kept
                default: return q;
            }
        }

        /// <summary>Do the values have different (or unknown) types, so C# cannot infer one element type? (ints and floats unify)</summary>
        bool MixedTypes(List<Expr> values)
        {
            var ts = values.Select(x => x is ConstExpr c && c.Kind == ConstKind.None ? "None" : PyType(InferType(x))).Distinct().ToList();
            if (ts.Count <= 1 || ts.Contains(TypeInference.Pending)) return false;
            return !ts.All(t => IsIntegral(t) || t == "double");
        }

        /// <summary>The Typhon.Stdlib module a call goes to (from itertools import x; itertools.x), or null.</summary>
        string StdlibModuleOf(CallExpr call, out string name)
        {
            name = null;
            if (call.Func is NameExpr n && !IsShadowed(n.Id))
            {
                name = n.Id;
                foreach (var st in _staticTypes) if (st.StartsWith("Typhon.Stdlib.") && _r.GetMembers(st, n.Id).Any(m => m.IsMethod)) return st;
                return null;
            }
            if (call.Func is AttributeExpr a && DotNetStaticMeta(a.Target) is string meta && meta.StartsWith("Typhon.Stdlib.")) { name = a.Name; return meta; }
            return null;
        }

        /// <summary>
        /// Result types of standard-library functions whose C# signatures are generic (the element type follows the argument):
        /// permutations(xs) → tuples of xs's elements, product(a, b) → (A, B) pairs, Counter(xs) → a Counter of xs's elements, ...
        /// </summary>
        string StdlibCallType(CallExpr call)
        {
            string module = StdlibModuleOf(call, out string name);
            if (module == null) return null;
            var pos = call.Args.Where(a => a.Name == null && !a.Star).Select(a => a.Value).ToList();
            string Elem(int i) => i < pos.Count ? PyType(ElementTypeOf(InferType(pos[i]))) : null;
            string Wrap(string e, string fmt) => e == null || e == TypeInference.Pending ? e : string.Format(fmt, e);
            switch (module + "." + name)
            {
                case "Typhon.Stdlib.itertools.permutations": case "Typhon.Stdlib.itertools.combinations":
                case "Typhon.Stdlib.itertools.combinations_with_replacement": case "Typhon.Stdlib.itertools.batched":
                    return Wrap(Elem(0), "IEnumerable<global::Typhon.Runtime.PyTuple<{0}>>");
                case "Typhon.Stdlib.itertools.product":
                    if (call.Args.Any(a => a.Name == "repeat") || pos.Count > 3) return Wrap(Elem(0), "IEnumerable<global::Typhon.Runtime.PyTuple<{0}>>");
                    var es = Enumerable.Range(0, pos.Count).Select(Elem).ToList();
                    return es.Count >= 2 && es.All(e => e != null && e != TypeInference.Pending) ? $"IEnumerable<({string.Join(", ", es)})>" : es.Contains(TypeInference.Pending) ? TypeInference.Pending : null;
                case "Typhon.Stdlib.itertools.count":
                    return pos.Any(p => InferType(p) == "double") ? "IEnumerable<double>" : "IEnumerable<long>";
                case "Typhon.Stdlib.itertools.cycle": case "Typhon.Stdlib.itertools.islice": case "Typhon.Stdlib.itertools.chain": case "Typhon.Stdlib.itertools.accumulate":
                    return Wrap(Elem(0), "IEnumerable<{0}>");
                case "Typhon.Stdlib.itertools.takewhile": case "Typhon.Stdlib.itertools.dropwhile": case "Typhon.Stdlib.itertools.filterfalse":
                    return Wrap(Elem(1), "IEnumerable<{0}>");
                case "Typhon.Stdlib.itertools.compress": return Wrap(Elem(0), "IEnumerable<{0}>");
                case "Typhon.Stdlib.itertools.pairwise": return Wrap(Elem(0), "IEnumerable<({0}, {0})>");
                case "Typhon.Stdlib.itertools.repeat": return pos.Count > 0 ? Wrap(PyType(InferType(pos[0])), "IEnumerable<{0}>") : null;
                case "Typhon.Stdlib.itertools.groupby": return pos.Count == 1 ? Wrap(Elem(0), "IEnumerable<({0}, List<{0}>)>") : null;
                case "Typhon.Stdlib.collections.Counter": return Wrap(pos.Count == 1 && InferType(pos[0]) == "string" ? "string" : Elem(0), "global::Typhon.Runtime.Counter<{0}>");
                case "Typhon.Stdlib.collections.deque": return pos.Count >= 1 ? Wrap(InferType(pos[0]) == "string" ? "string" : Elem(0), "global::Typhon.Runtime.deque<{0}>") : null;
                case "Typhon.Stdlib.functools.reduce":
                    return pos.Count == 3 ? PyType(InferType(pos[2])) : Elem(1);
                case "Typhon.Stdlib.fractions.Fraction": return "global::Typhon.Runtime.Fraction";
                case "Typhon.Stdlib.pathlib.Path": return "global::Typhon.Runtime.Path";
            }
            return null;
        }

        static readonly HashSet<string> StrToStr = new HashSet<string>
        {
            "lower", "upper", "strip", "lstrip", "rstrip", "replace", "title", "capitalize", "join", "zfill", "center", "ljust", "rjust",
            "swapcase", "casefold", "removeprefix", "removesuffix", "expandtabs", "translate", "format",
        };
        static readonly HashSet<string> StrToBool = new HashSet<string>
        {
            "startswith", "endswith", "isdigit", "isalpha", "isalnum", "isspace", "isupper", "islower", "istitle", "isdecimal", "isnumeric",
            "isidentifier", "isprintable", "isascii",
        };

        /// <summary>The result type of a Python method of str, list, dict or set (the runtime's extension methods).</summary>
        string PythonMethodType(string recv, string name, CallExpr call)
        {
            if (recv == null) return null;
            if (recv == "string")
            {
                if (StrToStr.Contains(name)) return "string";
                if (StrToBool.Contains(name)) return "bool";
                switch (name)
                {
                    case "split": case "rsplit": case "splitlines": return "List<string>";
                    case "find": case "rfind": case "index": case "rindex": case "count": return "long";
                    case "partition": case "rpartition": return "(string, string, string)";
                }
                return null;
            }
            string elem = ElementTypeOf(recv);
            if (recv.StartsWith("List<"))
                switch (name) { case "pop": return elem; case "index": case "count": return "long"; case "copy": return recv; }
            if (IsSetType(recv))
                switch (name)
                {
                    case "union": case "difference": case "intersection": case "symmetric_difference": case "copy": return recv;
                    case "issubset": case "issuperset": case "isdisjoint": return "bool";
                    case "pop": return elem;
                }
            if (recv.StartsWith("global::Typhon.Runtime.deque<"))
                switch (name) { case "pop": case "popleft": return elem; case "index": case "count": return "long"; case "copy": return recv; }
            if (recv.StartsWith("global::Typhon.Runtime.Counter<"))
                switch (name)
                {
                    case "most_common": return $"List<({elem}, long)>";
                    case "elements": return $"IEnumerable<{elem}>";
                    case "total": return "long";
                }
            if (DictTypes(recv) is List<string> kv)
            {
                switch (name)
                {
                    case "get" when call.Args.Count == 2:
                        return kv[1] == TypeInference.Pending ? PyType(InferType(call.Args[1].Value)) ?? TypeInference.Pending : kv[1];
                    // d.get(k) / d.get(k, None) / d.pop(k, None) of value types: an optional value (None when missing)
                    case "get" when call.Args.Count == 1:
                    case "pop" when call.Args.Count == 2 && IsNone(call.Args[1].Value):
                        return IsValueTypeName(kv[1]) && !kv[1].EndsWith("?") ? kv[1] + "?" : kv[1];
                    case "pop": case "setdefault": return kv[1];
                    case "copy": return recv;
                    case "popitem": return $"({kv[0]}, {kv[1]})";
                }
            }
            return null;
        }

        /// <summary>
        /// The type of a comprehension: its generator targets are bound to the element types of their iterables (in a scope of
        /// their own, as in Python), then the element (and value) expression is typed.
        /// </summary>
        string ComprehensionType(ComprehensionExpr comp)
        {
            var saved = _scope;
            var inner = new Scope { Parent = _scope, IsFunctionBoundary = false };
            _scope = inner;
            try
            {
                foreach (var g in comp.Generators) BindTargetTypes(inner, g.Target, IterElementType(InferType(g.Iter)));
                string elt = InferType(comp.Elt);
                if (elt == TypeInference.Pending) return elt;
                if (elt == null || elt == "dynamic") return null;
                switch (comp.Kind)
                {
                    case ComprehensionKind.List: return $"List<{elt}>";
                    case ComprehensionKind.Set: return $"HashSet<{elt}>";
                    case ComprehensionKind.Generator: return $"IEnumerable<{elt}>";
                    default:
                        string v = InferType(comp.ValueElt);
                        return v == null || v == "dynamic" || v == TypeInference.Pending ? (v == TypeInference.Pending ? v : null) : $"OrderedDictionary<{elt}, {v}>";
                }
            }
            finally { _scope = saved; }
        }

        static void BindTargetTypes(Scope s, Expr target, string type)
        {
            if (target is NameExpr n) { s.Params.Add(n.Id); if (type != null && type != TypeInference.Pending) s.Types[n.Id] = type; }
            else if (target is TupleExpr tup)
            {
                var parts = TupleElementTypes(type);
                for (int i = 0; i < tup.Elts.Count; i++) BindTargetTypes(s, tup.Elts[i], parts != null && i < parts.Count ? parts[i] : null);
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
        static bool IsValueTypeName(string t) => t == "long" || t == "double" || t == "bool" || t == "char" || t == "decimal" || t == "float" ||
            t == TypeInference.BigInt || IsSmallInt(t) || t.StartsWith("(") || t.EndsWith("?");

        /// <summary>The type of the items a for loop or comprehension gets from an iterable of this type (late-bound for a late-bound iterable).</summary>
        static string IterElementType(string t) => t == "dynamic" ? "dynamic" : ElementTypeOf(t);

        static string ElementTypeOf(string t)
        {
            if (t == null || t == TypeInference.Pending) return t;
            if (t == "global::Typhon.Runtime.PyRange") return "long";
            if (t.StartsWith("(") && t.EndsWith(")") && TupleElementTypes(t) is List<string> items) return items.Distinct().Count() == 1 ? items[0] : "object";
            if (t == "string") return "string"; // a str's elements are strs (one character each)
            if (t.EndsWith("[]")) return t.Substring(0, t.Length - 2);
            foreach (var g in new[] { "List<", "IList<", "IReadOnlyList<", "IEnumerable<", "HashSet<", "ISet<", "IReadOnlySet<", "IReadOnlyCollection<", "global::Typhon.Runtime.PyIterator<", "global::Typhon.Runtime.PyTuple<" })
                if (t.StartsWith(g) && t.EndsWith(">")) return GenericArg(t);
            if (DictTypes(t) is List<string> kv0) return kv0[0];
            if (t.StartsWith("global::Typhon.Runtime.deque<") && t.EndsWith(">")) return GenericArg(t);
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
            if (MathIntFunctions.Contains(f.Name)) return _big && f.Name != "floor" && f.Name != "ceil" && f.Name != "trunc" ? IntT : "long";
            if (MathBoolFunctions.Contains(f.Name)) return "bool";
            return "double";
        }

        string MathConstant(AttributeExpr a) => IsMathModule(a.Target) && (a.Name == "pi" || a.Name == "e" || a.Name == "tau" || a.Name == "inf" || a.Name == "nan") ? "double" : null;

        static readonly HashSet<string> NumericTypes = new HashSet<string> { "int", "long", "double", "float", "decimal", TypeInference.BigInt };

        static bool IsIntegral(string t) => t == "long" || IsSmallInt(t) || t == TypeInference.BigInt;

        /// <summary>Big-int mode: the operators whose result can exceed 64 bits for 64-bit operands (computed as BigInteger).</summary>
        static readonly HashSet<string> WideningOps = new HashSet<string> { "+", "-", "*", "**", "<<" };

        /// <summary>Cheap local type inference, used where C# 'var' cannot be used (hoisted locals, fields).</summary>
        string InferType(Expr e)
        {
            switch (e)
            {
                case ConstExpr c:
                    switch (c.Kind)
                    {
                        case ConstKind.Int: { if (_big) return IntT; var code = EmitConst(c); return long.TryParse(code, out _) ? "long" : code.EndsWith("UL") ? "ulong" : null; }
                        case ConstKind.Float: return "double";
                        case ConstKind.Str: return "string";
                        case ConstKind.Bool: return "bool";
                        default: return null;
                    }
                case FStringExpr _: return "string";
                case NamedExpr ne: return InferType(ne.Value);
                case ComprehensionExpr comp: return ComprehensionType(comp);
                case NameExpr n when n.Id == "__file__" && !IsShadowed(n.Id): return "string";
                case NameExpr n:
                {
                    var t = KnownType(n.Id);
                    if (t != null) return t;
                    if (!IsLocal(n.Id) && _moduleFieldTypes.TryGetValue(n.Id, out var mt)) return mt;
                    return null;
                }
                case AttributeExpr at when at.Target is NameExpr self && self.Id == "self" && _cls != null:
                    return _cls.FieldTypes.TryGetValue(at.Name, out var ft) ? ft : null;
                case AttributeExpr mp when mp.Name.StartsWith(LocalsAnalyzer.MatchPositionPrefix) && mp.Target is CallExpr mc0:
                {
                    var names = MatchArgs(mc0.Func);
                    int pos = int.Parse(mp.Name.Substring(LocalsAnalyzer.MatchPositionPrefix.Length));
                    return names != null && pos < names.Count ? InferType(new AttributeExpr { Target = mp.Target, Name = names[pos] }) : null;
                }
                case AttributeExpr oa:
                {
                    var ot = InferType(oa.Target);
                    if (ot == TypeInference.Pending) return TypeInference.Pending;
                    if (ot == "dynamic") return "dynamic"; // a member of a late-bound value is late-bound
                    if (ot == null) return MathConstant(oa) ?? DotNetResultType(oa.Target, oa.Name, null);
                    return MemberType(ot, oa.Name, method: false) ?? DotNetResultType(oa.Target, oa.Name, null);
                }
                case SubscriptExpr ix when !(ix.Index is SliceExpr):
                {
                    var t = InferType(ix.Target);
                    if (t == null || t == TypeInference.Pending || t == "dynamic") return t;
                    if (t == "string") return "string";
                    var tupleItems = TupleElementTypes(t);
                    if (tupleItems != null)
                    {
                        if (ConstIntValue(ix.Index) is long k && k >= -tupleItems.Count && k < tupleItems.Count) return tupleItems[(int)(k < 0 ? k + tupleItems.Count : k)];
                        return tupleItems.Distinct().Count() == 1 ? tupleItems[0] : null;
                    }
                    if (t.EndsWith("[]")) return t.Substring(0, t.Length - 2);
                    if ((t.StartsWith("List<") || t.StartsWith("IList<") || t.StartsWith("IReadOnlyList<")) && t.EndsWith(">")) return GenericArg(t);
                    if (DictTypes(t) is List<string> dkv) return dkv[1];
                    if (t.StartsWith("global::Typhon.Runtime.deque<") && t.EndsWith(">")) return GenericArg(t);
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
                    return t != null && (t == "string" || t.StartsWith("List<") || t.EndsWith("[]") || t == "dynamic") ? t : null;
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
                        if (ot == "dynamic") return "dynamic"; // a method of a late-bound value returns a late-bound value
                        if (ot != null && MemberType(ot, mc.Name, method: true) is string objRet) return objRet;
                        if (DotNetResultType(mc.Target, mc.Name, call) is string netRet) return netRet;
                        if (PythonMethodType(ot, mc.Name, call) is string pyRet) return pyRet;
                        if (call.Args.Count == 0 && (mc.Name == "items" || mc.Name == "keys" || mc.Name == "values") && DictTypes(ot) is List<string> kv)
                            return mc.Name == "items" ? $"IEnumerable<({kv[0]}, {kv[1]})>" : $"IEnumerable<{(mc.Name == "keys" ? kv[0] : kv[1])}>";
                    }
                    if (call.Func is AttributeExpr ma && ma.Target is NameExpr ms && ms.Id == "self" && _cls != null && _cls.MethodReturnTypes.TryGetValue(ma.Name, out var mrt))
                        return mrt == "void" ? null : mrt;
                    if (StdlibCallType(call) is string stdlibRet) return stdlibRet;
                    if (call.Func is NameExpr fn && !IsShadowed(fn.Id))
                    {
                        if (NumericConversions.TryGetValue(fn.Id, out var conv)) return conv;
                        switch (fn.Id)
                        {
                            case "str": case "repr": case "input": case "chr": case "hex": case "bin": case "format": return "string";
                            case "int": return IntT;
                            case "len": case "ord": case "hash": return "long";
                            case "float": return "double";
                            case "bool": case "isinstance": case "any": case "all": return "bool";
                            case "round": return call.Args.Count == 1 ? "long" : "double";
                            case "abs": return call.Args.Count == 1 ? (_big && IsIntegral(InferType(call.Args[0].Value)) ? IntT : InferType(call.Args[0].Value)) : null;
                            case "range": return "global::Typhon.Runtime.PyRange";
                            case "open": return "global::Typhon.Runtime.PyFile";
                            case "iter":
                            {
                                if (call.Args.Count != 1) return null;
                                string elem = ElementTypeOf(InferType(call.Args[0].Value));
                                return elem == null || elem == TypeInference.Pending ? elem : $"global::Typhon.Runtime.PyIterator<{elem}>";
                            }
                            case "next":
                            {
                                if (call.Args.Count == 0) return null;
                                return ElementTypeOf(InferType(call.Args[0].Value));
                            }
                            case "tuple" when call.Args.Count == 1 && call.Args[0].Name == null && !call.Args[0].Star:
                            {
                                var at = InferType(call.Args[0].Value);
                                string elem = at == "string" ? "string" : ElementTypeOf(at);
                                return elem == null || elem == TypeInference.Pending ? elem : $"global::Typhon.Runtime.PyTuple<{elem}>";
                            }
                            case "list": case "sorted": case "set":
                            {
                                if (call.Args.Count == 0 || call.Args[0].Name != null || call.Args[0].Star) return null;
                                var at = InferType(call.Args[0].Value);
                                if (at == TypeInference.Pending) return at;
                                string elem = fn.Id == "list" && at == "string" ? "string" : ElementTypeOf(at);
                                if (elem == null || elem == TypeInference.Pending) return elem;
                                return (fn.Id == "set" ? "HashSet<" : "List<") + elem + ">";
                            }
                            case "enumerate":
                            {
                                if (call.Args.Count == 0 || call.Args[0].Name != null || call.Args[0].Star) return null;
                                string elem = ElementTypeOf(InferType(call.Args[0].Value));
                                return elem == null || elem == TypeInference.Pending ? elem : $"IEnumerable<(long, {elem})>";
                            }
                            case "sum":
                            {
                                if (call.Args.Count != 1 || call.Args[0].Name != null || call.Args[0].Star) return null;
                                string elem = ElementTypeOf(InferType(call.Args[0].Value));
                                return IsIntegral(elem) ? (elem == TypeInference.BigInt ? elem : "long") : elem == "double" ? "double" : elem == TypeInference.Pending ? elem : null;
                            }
                            case "min": case "max":
                            {
                                if (call.Args.Any(a => a.Name != null || a.Star)) return null;
                                var ts = call.Args.Count == 1 ? new List<string> { ElementTypeOf(InferType(call.Args[0].Value)) } : call.Args.Select(a => InferType(a.Value)).ToList();
                                if (ts.Contains(TypeInference.Pending)) return TypeInference.Pending;
                                if (ts.Any(t => t == null)) return null;
                                if (_big && ts.All(IsIntegral)) return call.Args.Count == 1 && ts[0] == "long" ? "long" : IntT;
                                if (ts.All(t => t == ts[0])) return ts[0];
                                if (ts.All(IsIntegral)) return "long";
                                return ts.All(t => IsIntegral(t) || t == "double") ? "double" : null;
                            }
                            case "divmod":
                            {
                                if (call.Args.Count != 2) return null;
                                string x = InferType(call.Args[0].Value), y = InferType(call.Args[1].Value);
                                return IsIntegral(x) && IsIntegral(y) ? $"({IntT}, {IntT})" : (x == "double" || y == "double") && (IsIntegral(x) || x == "double") && (IsIntegral(y) || y == "double") ? "(double, double)" : null;
                            }
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
                    if ((b.Op == "-" || b.Op == "|" || b.Op == "&" || b.Op == "^") && (IsSetType(l) || IsSetType(r))) return IsSetType(l) ? l : r;
                    if (b.Op == "+" && TupleElementTypes(l) is List<string> tl && TupleElementTypes(r) is List<string> tr) return "(" + string.Join(", ", tl.Concat(tr)) + ")";
                    if (b.Op == "|" && IsDictType(l) && IsDictType(r)) return l;
                    if (ArithmeticOps.Contains(b.Op)) { if (l == "bool") l = "long"; if (r == "bool") r = "long"; }
                    // a literal next to a narrower .NET integer stays that type (see Operand)
                    if (IsIntLiteral(b.Left) && IsSmallInt(r)) l = r;
                    if (IsIntLiteral(b.Right) && IsSmallInt(l)) r = l;
                    if (_big && IsIntegral(l) && IsIntegral(r) && (b.Op == "<<" || b.Op == ">>")) return b.Op == "<<" || l == IntT ? IntT : "long";
                    if (b.Op == "<<" || b.Op == ">>") return IsIntegral(l) && IsIntegral(r) ? "long" : (l == "dynamic" || r == "dynamic" ? "dynamic" : null);
                    if (b.Op == "+" && l == "string" && r == "string") return "string";
                    if (b.Op == "*" && (l == "string" || r == "string")) return "string";
                    // [0] * n is a list of the same type
                    if (b.Op == "*" && l.StartsWith("List<") && IsIntegral(r)) return l;
                    if (b.Op == "*" && r.StartsWith("List<") && IsIntegral(l)) return r;
                    if (b.Op == "+" && l.StartsWith("List<") && l == r) return l;
                    if (l == "dynamic" || r == "dynamic") return "dynamic";
                    if (!NumericTypes.Contains(l) || !NumericTypes.Contains(r)) return l == r ? l : null;
                    if (b.Op == "**") return IsIntegral(l) && IsIntegral(r) ? IntT : "double";
                    if (_big && IsIntegral(l) && IsIntegral(r))
                        return WideningOps.Contains(b.Op) || l == IntT || r == IntT ? IntT : (IsSmallInt(l) && IsSmallInt(r) && l == r ? l : "long");
                    if (_big && (l == IntT || r == IntT) && (l == "double" || r == "double")) return "double";
                    if (l == r) return l;
                    if (l == "double" || r == "double") return "double";
                    if (l == "long" || r == "long") return "long";
                    return null;
                }
                case UnaryExpr u: return u.Op == "not" ? "bool" : InferType(u.Operand) == "bool" ? "long" : InferType(u.Operand);
                case CompareExpr ce:
                {
                    // a comparison is a bool when Python's rules or numbers decide it; other types answer with their own
                    // operator's result (an ndarray compares element-wise and returns an array)
                    Expr left = ce.Left;
                    for (int i = 0; i < ce.Ops.Count; i++)
                    {
                        string op = ce.Ops[i];
                        if (op == "<" || op == "<=" || op == ">" || op == ">=")
                        {
                            string lt = InferType(left), rt = InferType(ce.Comparators[i]);
                            bool decided = (IsNumeric(lt) || lt == "bool" || lt == "char") && (IsNumeric(rt) || rt == "bool" || rt == "char") ||
                                           PythonOrdered(lt) || PythonOrdered(rt);
                            if (!decided && !(IsUnknownType(lt) && IsUnknownType(rt))) return null;
                        }
                        left = ce.Comparators[i];
                    }
                    return "bool";
                }
                case BoolOpExpr b:
                {
                    if (b.Values.All(IsBoolish)) return "bool";
                    var ts = b.Values.Select(InferType).ToList();
                    if (ts.Contains(TypeInference.Pending)) return ts.Where(t => t != TypeInference.Pending).Distinct().Count() == 1 ? ts.First(t => t != TypeInference.Pending) : TypeInference.Pending;
                    if (ts.All(t => t != null && t == ts[0])) return ts[0];
                    // x or None / None or x: the value's type (a reference type holds None as null); x or {}: x's type
                    var known = b.Values.Where(v => !IsNone(v) && !IsEmptyLiteral(v)).Select(InferType).Distinct().ToList();
                    if (known.Count == 1 && known[0] != null && b.Values.Any(IsEmptyLiteral) && !b.Values.Any(IsNone)) return known[0];
                    return known.Count == 1 && known[0] != null && !IsValueTypeName(known[0]) ? known[0] : ts.Contains("dynamic") ? "dynamic" : null;
                }
                case IfExpr i:
                {
                    var a = InferType(i.Body); var o = InferType(i.OrElse);
                    if (a == TypeInference.Pending) return o;
                    if (o == TypeInference.Pending) return a;
                    return a != null && a == o ? a : null;
                }
                case ListExpr l when l.Elts.Any(x => x is StarredExpr):
                {
                    var et = SpreadElementType(l.Elts);
                    return et == null || et == TypeInference.Pending ? et : $"List<{et}>";
                }
                case TupleExpr te when te.Elts.Any(x => x is StarredExpr):
                {
                    var et = SpreadElementType(te.Elts);
                    return et == null || et == TypeInference.Pending ? et : $"global::Typhon.Runtime.PyTuple<{et}>";
                }
                case SetExpr se when se.Elts.Any(x => x is StarredExpr):
                {
                    var et = SpreadElementType(se.Elts);
                    return et == null || et == TypeInference.Pending ? et : $"HashSet<{et}>";
                }
                case ListExpr l:
                {
                    if (l.Elts.Count == 0) return null;
                    var ts = l.Elts.Select(InferType).Distinct().ToList();
                    if (ts.Count == 1 && ts[0] != null) return $"List<{ts[0]}>";
                    if (ts.All(t => IsIntegral(t) || t == "double")) return ts.Contains("double") ? "List<double>" : "List<long>";
                    return null;
                }
                case SetExpr se:
                {
                    var ts = se.Elts.Select(x => PyType(InferType(x))).Distinct().ToList();
                    if (ts.Contains(TypeInference.Pending)) return TypeInference.Pending;
                    if (ts.Count == 1 && ts[0] != null) return $"HashSet<{ts[0]}>";
                    if (ts.All(t => IsIntegral(t) || t == "double")) return ts.Contains("double") ? "HashSet<double>" : "HashSet<long>";
                    return null;
                }
                case DictExpr d:
                {
                    if (d.Keys.Count == 0 || d.Keys.Any(k => k == null)) return null;
                    var ks = d.Keys.Select(InferType).Distinct().ToList();
                    var vs = d.Values.Select(InferType).Distinct().ToList();
                    if (ks.Count == 1 && ks[0] != null && vs.Count == 1 && vs[0] != null) return $"OrderedDictionary<{ks[0]}, {vs[0]}>";
                    return null;
                }
                case TupleExpr t:
                {
                    if (t.Elts.Count == 0) return null;
                    if (t.Elts.Count == 1) { var one = PyType(InferType(t.Elts[0])); return one == null || one == TypeInference.Pending ? one : $"ValueTuple<{one}>"; }
                    var ts = t.Elts.Select(InferType).ToList();
                    return ts.All(x => x != null) ? "(" + string.Join(", ", ts) + ")" : null;
                }
                default: return null;
            }
        }
    }
}
