// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Typhon.Compiler.Semantics;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    internal sealed class CodeWriter
    {
        readonly StringBuilder _sb = new StringBuilder();
        int _indent;
        public void Line(string s = "") { if (s.Length > 0) _sb.Append(' ', _indent * 4); _sb.Append(s).Append('\n'); }
        public void Open() { Line("{"); _indent++; }
        public void Close(string suffix = "") { _indent--; Line("}" + suffix); }
        public void Raw(string s) { _sb.Append(s).Append('\n'); }
        public int Indent { get => _indent; set => _indent = value; }
        public override string ToString() => _sb.ToString();
    }

    /// <summary>Per-function emission state.</summary>
    internal sealed class Scope
    {
        public Scope Parent;
        public LocalsResult Locals;
        public readonly HashSet<string> Params = new HashSet<string>();
        public readonly Dictionary<string, string> Types = new Dictionary<string, string>();
        public string ReturnType;        // element type for generators, awaited type for async; "void" for none
        public bool IsGenerator;
        public bool IsAsync;
        public bool HasSelf;             // instance method: 'self' → 'this'
        public string ClsName;           // classmethod: 'cls' → class name
        public bool IsFunctionBoundary = true;
    }

    internal sealed class ClassContext
    {
        public ClassDef Def;
        public string Name;
        public string CsSelfType;        // Name<T,...>
        public bool IsInterface;
        public bool IsSealed;
        public readonly HashSet<string> Members = new HashSet<string>();
        public readonly Dictionary<string, string> FieldTypes = new Dictionary<string, string>();
        public readonly HashSet<string> TypeParams = new HashSet<string>();
        public string IterElementType;
        public readonly Dictionary<string, string> MethodReturnTypes = new Dictionary<string, string>();
    }

    internal sealed partial class ModuleEmitter
    {
        readonly ModuleInfo _m;
        readonly ProjectContext _p;
        readonly List<TyDiagnostic> _diags;
        readonly ITypeResolver _r;
        readonly CodeWriter _w = new CodeWriter();

        readonly List<string> _usingLines = new List<string>();
        readonly List<string> _usingNamespaces = new List<string> { "System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks" };
        readonly List<string> _staticTypes = new List<string>();
        readonly Dictionary<string, ModuleInfo> _moduleAliases = new Dictionary<string, ModuleInfo>();
        readonly Dictionary<string, KeyValuePair<ModuleInfo, string>> _importedFromModules = new Dictionary<string, KeyValuePair<ModuleInfo, string>>();
        readonly List<ModuleInfo> _wildcardModules = new List<ModuleInfo>();
        readonly Dictionary<string, string> _dotnetAliases = new Dictionary<string, string>();
        readonly Dictionary<string, string> _importedTypes = new Dictionary<string, string>();

        Scope _scope;
        ClassContext _cls;
        readonly Stack<HashSet<string>> _lambdaParams = new Stack<HashSet<string>>();
        int _tmp;
        string _path;

        public ModuleEmitter(ModuleInfo module, ProjectContext project, List<TyDiagnostic> diags, bool dryRun = false)
        {
            _m = module; _p = project; _diags = diags; _r = project.Resolver; _path = module.Path; _dry = dryRun;
        }

        // ───────────────────────────── whole-program inference hooks ─────────────────────────────

        /// <summary>A dry run only records observations for <see cref="TypeInference"/>; its output is discarded.</summary>
        readonly bool _dry;
        TypeInference Ti => _p.Inference;
        Observations Obs => _dry ? Ti?.Obs : null;
        /// <summary>Return-type key of the body being emitted (its returns are recorded), or null.</summary>
        string _retKey;
        /// <summary>Key of the inference target whose dynamic fallback body is being emitted (its calls might never run).</summary>
        string _ctxFallbackKey;

        InferenceTarget TargetOf(ModuleInfo m, ClassDef c, string name) =>
            Ti != null && Ti.Targets.TryGetValue(TypeInference.FuncKey(m, c, name), out var t) ? t : null;

        /// <summary>The __init__ that constructing <paramref name="c"/> runs (its own, or the nearest Typhon base class's).</summary>
        InferenceTarget InitTarget(ModuleInfo m, ClassDef c, int depth = 0)
        {
            if (m == null || c == null || depth > 20) return null;
            if (c.Body.OfType<FunctionDef>().Any(f => f.Name == "__init__")) return TargetOf(m, c, "__init__") is InferenceTarget t && t.Class == c ? t : null;
            foreach (var b in c.Bases)
                if ((b is SubscriptExpr s ? s.Target : b) is NameExpr bn && _p.TryFindClass(m, bn.Id, out var bc, out var owner))
                    return InitTarget(owner, bc, depth + 1);
            return null;
        }

        /// <summary>The project function or constructor a call resolves to (by name, imported name or module attribute).</summary>
        InferenceTarget CallTarget(CallExpr call, out bool isConstructor)
        {
            isConstructor = false;
            if (Ti == null) return null;
            ModuleInfo mod = null; string name = null;
            if (call.Func is NameExpr n)
            {
                if (IsLocal(n.Id)) return null;
                if (_m.Functions.Contains(n.Id) || _m.Classes.ContainsKey(n.Id)) { mod = _m; name = n.Id; }
                else if (_importedFromModules.TryGetValue(n.Id, out var imp)) { mod = imp.Key; name = imp.Value; }
            }
            else if (call.Func is AttributeExpr a && DottedName(a.Target) is string dotted && !IsLocal(dotted.Split('.')[0]) && ModuleRef(dotted) is ModuleInfo mref)
            {
                mod = mref; name = a.Name;
            }
            if (mod == null) return null;
            if (mod.Classes.TryGetValue(name, out var cls)) { isConstructor = true; return InitTarget(mod, cls); }
            return mod.Functions.Contains(name) ? TargetOf(mod, null, name) : null;
        }

        /// <summary>Argument types of a call, one per parameter (defaults fill omitted ones); null if the shape is unknown.</summary>
        string[] ArgTypes(InferenceTarget t, List<Arg> args)
        {
            var ps = t.Params;
            var types = new string[ps.Count];
            int pos = 0;
            foreach (var a in args)
            {
                if (a.Star || a.DoubleStar) return null;
                int idx = a.Name == null ? pos++ : ps.FindIndex(p => p.Name == a.Name);
                if (idx < 0 || idx >= ps.Count) return null;
                types[idx] = InferType(a.Value) ?? TypeInference.Unknown;
            }
            for (int i = 0; i < ps.Count; i++)
            {
                if (ps[i].Annotation != null) { types[i] = TypeInference.Declared; continue; }
                if (types[i] == null) types[i] = ps[i].Default != null ? (InferType(ps[i].Default) ?? TypeInference.Unknown) : null;
                if (types[i] == null) return null;
                // a type written in another module's terms might not resolve there: only portable types cross modules
                if (t.Module != _m && !IsPortableType(types[i])) types[i] = TypeInference.Unknown;
            }
            return types;
        }

        static readonly HashSet<string> PortableNames = new HashSet<string>
        {
            "int", "long", "double", "float", "bool", "string", "char", "decimal", "List", "Dictionary", "HashSet", "IEnumerable",
            "IReadOnlyList", "IList", "IDictionary", "IReadOnlyDictionary", "ValueTuple", "global",
        };

        /// <summary>Built from primitives and generic collections only (or fully qualified), so it means the same in every module.</summary>
        static bool IsPortableType(string t)
        {
            if (t.StartsWith("global::")) return true;
            foreach (var part in t.Split(new[] { '<', '>', ',', ' ', '(', ')', '[', ']', '?' }, StringSplitOptions.RemoveEmptyEntries))
                if (!PortableNames.Contains(part)) return false;
            return true;
        }

        void RecordCall(InferenceTarget t, List<Arg> args)
        {
            if (Obs == null || t == null || !t.InferParams) return;
            Obs.Calls.Add(new Observations.Call { Callee = t.Key, Types = ArgTypes(t, args), Context = _ctxFallbackKey });
        }

        /// <summary>The inferred return type of a call to an unannotated project function (Pending while still unknown in a dry run).</summary>
        string InferredCallType(CallExpr call)
        {
            var t = CallTarget(call, out bool isCtor);
            if (t == null || isCtor || t.Def.Returns != null || t.Def.IsAsync) return null;
            string sig = "";
            if (t.InferParams)
            {
                var types = ArgTypes(t, call.Args);
                if (types == null || types.Any(TypeInference.IsUnknown)) return null;
                sig = TypeInference.SigKey(types);
                if (!(Ti.Signatures.TryGetValue(t.Key, out var sigs) && sigs.Any(s => TypeInference.SigKey(s) == sig))) return _dry ? TypeInference.Pending : null;
            }
            if (Ti.Returns.TryGetValue(TypeInference.ReturnKey(t.Key, sig), out var rt))
                return t.Module == _m || IsPortableType(rt) ? rt : null;
            return _dry ? TypeInference.Pending : null;
        }

        /// <summary>Typhon class key for a C# type name visible in this module (same module, or imported by name).</summary>
        string ClassKeyOfType(string csType, out bool sameModule)
        {
            sameModule = false;
            if (csType == null) return null;
            if (_m.Classes.ContainsKey(csType)) { sameModule = true; return TypeInference.ClassKey(_m, csType); }
            if (_importedFromModules.TryGetValue(csType, out var imp) && imp.Key.Classes.ContainsKey(imp.Value)) return TypeInference.ClassKey(imp.Key, imp.Value);
            return null;
        }

        /// <summary>The type of member <paramref name="member"/> (field, or method return when <paramref name="method"/>) of a Typhon class.</summary>
        string MemberType(string csType, string member, bool method)
        {
            if (_cls != null && csType == _cls.CsSelfType)
            {
                if (!method && _cls.FieldTypes.TryGetValue(member, out var own)) return own;
                if (method && _cls.MethodReturnTypes.TryGetValue(member, out var ownRet)) return ownRet == "void" ? null : ownRet;
            }
            var key = ClassKeyOfType(csType, out bool same);
            if (key == null || Ti == null) return null;
            var table = method ? Ti.MethodReturns : Ti.Fields;
            if (!table.TryGetValue(key, out var members) || !members.TryGetValue(member, out var t) || t == "void" || t == "dynamic") return null;
            return same || IsPortableType(t) ? t : null;
        }

        // ───────────────────────────── diagnostics ─────────────────────────────

        void Report(Node at, string id, Severity sev, string msg)
        {
            _diags.Add(new TyDiagnostic { Id = id, Severity = sev, Message = msg, Path = _path, Line = at?.Line ?? 1, Col = at?.Col ?? 1 });
        }

        void Error(Node at, string msg) => Report(at, "TY0002", Severity.Error, msg);
        void Unsupported(Node at, string what) => Report(at, "TY0003", Severity.Error, what + " is not supported in Typhon yet");

        /// <summary>Every implicit fallback to 'dynamic' goes through here (TypeScript-style noImplicitAny).</summary>
        string ImplicitDynamic(Node at, string what)
        {
            if (_p.TypeMode == TypeMode.Strict)
                Report(at, "TY1001", Severity.Error, $"cannot infer the type of {what}; add a type annotation (or annotate it as 'Any' to opt into dynamic typing)");
            else if (_p.TypeMode == TypeMode.Warn)
                Report(at, "TY1001", Severity.Warning, $"{what} is implicitly dynamic (late-bound, slower); add a type annotation");
            return "dynamic";
        }

        void LineDirective(Node n)
        {
            if (n != null && n.Line > 0) _w.Raw("#line " + n.Line + " \"" + _path + "\"");
        }

        // ───────────────────────────── module ─────────────────────────────

        public string Emit()
        {
            foreach (var n in AllNodes(_m.Ast.Body))
            {
                if (n is ImportStmt i) ProcessImport(i);
                else if (n is ImportFromStmt f) ProcessImportFrom(f);
            }

            _w.Raw("// <auto-generated>");
            _w.Raw("//   Generated by the Typhon compiler from " + System.IO.Path.GetFileName(_path) + ". Do not edit.");
            _w.Raw("// </auto-generated>");
            _w.Raw("#pragma warning disable CS1718, CS0162, CS0164, CS0168, CS0219, CS8321, CS1998, CS0105, CS0659, CS0660, CS0661, CS8981, CS1591");
            _w.Raw("#nullable enable annotations");
            _w.Line("using System;");
            _w.Line("using System.Collections.Generic;");
            _w.Line("using System.Linq;");
            _w.Line("using System.Threading.Tasks;");
            _w.Line("using static global::Typhon.Runtime.Builtins;");
            _w.Line("using static global::Typhon.Runtime.DictGetStruct;");
            _w.Line("using static global::Typhon.Runtime.DictGetClass;");
            foreach (var u in _usingLines.Distinct()) _w.Line(u);
            _w.Line();

            // return types of module-level functions feed the local type inference
            foreach (var f in _m.Ast.Body.OfType<FunctionDef>())
                if (f.Returns != null && !f.IsAsync && f.TypeParams.Count == 0 && !AstWalker.ContainsOutsideNestedScopes(f.Body, n => n is YieldExpr))
                    _funcReturnTypes[f.Name] = MapType(f.Returns, f, isReturn: true);

            bool hasNs = !string.IsNullOrEmpty(_m.Namespace);
            if (hasNs) { _w.Line("namespace " + _m.Namespace); _w.Open(); }

            var body = _m.Ast.Body;
            EmitDocComment(body);
            LineDirective(body.FirstOrDefault());
            _w.Line("public static partial class " + Esc(_m.ClassName));
            _w.Open();
            EmitModuleBody(body);
            _w.Close();

            if (hasNs) _w.Close();
            _w.Raw("#line default");
            return _w.ToString();
        }

        static IEnumerable<Node> AllNodes(object root)
        {
            var list = new List<Node>();
            AstWalker.VisitAll(root, list.Add);
            return list;
        }

        static bool IsMainGuard(Stmt s)
        {
            if (!(s is IfStmt i) || !(i.Test is CompareExpr c) || c.Ops.Count != 1 || c.Ops[0] != "==") return false;
            bool IsName(Expr e) => e is NameExpr n && n.Id == "__name__";
            bool IsMain(Expr e) => e is ConstExpr k && k.Kind == ConstKind.Str && k.Str == "__main__";
            return (IsName(c.Left) && IsMain(c.Comparators[0])) || (IsMain(c.Left) && IsName(c.Comparators[0]));
        }

        void EmitModuleBody(List<Stmt> body)
        {
            bool isMainModule = _m.IsMainModule;

            // free variables of functions/classes in this module → must be static fields, not Main locals
            var usedInDefs = new HashSet<string>();
            foreach (var s in body.Where(s => s is FunctionDef || s is ClassDef)) CollectFreeNames(s, usedInDefs);

            var fields = new Dictionary<string, string>();      // name → C# type
            var execStmts = new List<Stmt>();                     // static ctor (regular) / Main (main module)
            var mainBlocks = new List<Stmt>();

            foreach (var s in body)
            {
                if (s is FunctionDef || s is ClassDef || s is ImportStmt || s is ImportFromStmt || s is PassStmt) continue;
                if (s is ExprStmt es && es.Value is ConstExpr ce && (ce.Kind == ConstKind.Str || ce.Kind == ConstKind.Ellipsis)) continue;
                if (IsMainGuard(s)) { mainBlocks.AddRange(((IfStmt)s).Body); continue; }

                string fieldName = null; Expr ann = null, value = null;
                if (s is AssignStmt a && a.Targets.Count == 1 && a.Targets[0] is NameExpr an) { fieldName = an.Id; value = a.Value; }
                else if (s is AnnAssignStmt aa && aa.Target is NameExpr aan) { fieldName = aan.Id; ann = aa.Annotation; value = aa.Value; }

                // remember the type of every module-level name (field or Main local) for later inference
                if (fieldName != null && !_moduleFieldTypes.ContainsKey(fieldName))
                {
                    string t = ann != null ? MapType(ann, s) : InferType(value);
                    if (t != null) _moduleFieldTypes[fieldName] = t;
                }

                bool makeField = fieldName != null && !fields.ContainsKey(fieldName) && (!isMainModule || usedInDefs.Contains(fieldName));
                if (makeField)
                {
                    string type = ann != null ? MapType(ann, s) : (InferType(value) ?? ImplicitDynamic(s, $"module variable '{fieldName}'"));
                    fields[fieldName] = type;
                    _moduleFieldTypes[fieldName] = type;
                    string vis = fieldName.StartsWith("_") ? "internal" : "public";
                    LineDirective(s);
                    if (!isMainModule && value != null)
                        _w.Line($"{vis} static {type} {Esc(fieldName)} = {EmitExpr(value, targetTyped: type != "dynamic")};");
                    else
                    {
                        _w.Line($"{vis} static {type} {Esc(fieldName)};");
                        if (value != null) execStmts.Add(s);
                    }
                    continue;
                }
                execStmts.Add(s);
            }
            if (fields.Count > 0) _w.Line();

            foreach (var s in body)
            {
                if (s is FunctionDef f) { EmitFunction(f, isModuleLevel: true); _w.Line(); }
                else if (s is ClassDef c) { EmitClass(c); _w.Line(); }
            }

            if (isMainModule)
            {
                var all = new List<Stmt>(execStmts);
                all.AddRange(mainBlocks);
                EmitMain(all, fields.Keys, body.FirstOrDefault());
            }
            else
            {
                if (execStmts.Count > 0)
                {
                    _w.Line("static " + Esc(_m.ClassName) + "()");
                    EmitFunctionBody(execStmts, new Scope { ReturnType = "void" }, fields.Keys);
                    _w.Line();
                }
                if (mainBlocks.Count > 0 && (_p.MainModule == null || _p.MainModule == _m.Name)) EmitMain(mainBlocks, fields.Keys, body.First(IsMainGuard));
            }
        }

        static void CollectFreeNames(Stmt s, HashSet<string> into)
        {
            if (s is FunctionDef f)
            {
                var loc = LocalsAnalyzer.Analyze(f.Body, f.Params.Select(p => p.Name), null);
                var bound = new HashSet<string>(loc.AllLocals);
                bound.UnionWith(f.Params.Select(p => p.Name));
                bound.UnionWith(loc.LocalFunctions);
                foreach (var n in AllNodes(f.Body))
                    if (n is NameExpr ne && (!bound.Contains(ne.Id) || loc.Globals.Contains(ne.Id))) into.Add(ne.Id);
                foreach (var p in f.Params) AstWalker.VisitAll(p.Default, n => { if (n is NameExpr ne) into.Add(ne.Id); });
            }
            else if (s is ClassDef c)
            {
                foreach (var st in c.Body)
                {
                    if (st is FunctionDef || st is ClassDef) CollectFreeNames(st, into);
                    else AstWalker.VisitAll(st, n => { if (n is NameExpr ne) into.Add(ne.Id); });
                }
            }
        }

        void EmitMain(List<Stmt> stmts, IEnumerable<string> moduleFields, Node at)
        {
            var scope = new Scope { ReturnType = "void" };
            var locals = LocalsAnalyzer.Analyze(stmts, Enumerable.Empty<string>(), moduleFields);
            bool isAsync = locals.HasAwait;
            LineDirective(at);
            _w.Line(isAsync ? "public static async Task Main(string[] args)" : "public static void Main(string[] args)");
            scope.IsAsync = isAsync;
            EmitFunctionBody(stmts, scope, moduleFields, locals, prologue: "global::Typhon.Stdlib.sys_.__init__(args);");
        }

        // ───────────────────────────── imports ─────────────────────────────

        ModuleInfo ResolvePsModule(string dotted)
        {
            var m = _p.FindModule(dotted);
            if (m != null) return m;
            // relative to this module's package
            int lastDot = _m.Name.LastIndexOf('.');
            if (lastDot > 0) return _p.FindModule(_m.Name.Substring(0, lastDot) + "." + dotted);
            return null;
        }

        string ResolveRelative(string module, int level)
        {
            if (level == 0) return module;
            var parts = _m.Name.Split('.').ToList();
            parts.RemoveAt(parts.Count - 1);
            for (int i = 1; i < level && parts.Count > 0; i++) parts.RemoveAt(parts.Count - 1);
            if (!string.IsNullOrEmpty(module)) parts.Add(module);
            return string.Join(".", parts);
        }

        static readonly HashSet<string> IgnoredModules = new HashSet<string> { "typing", "abc", "dataclasses", "enum", "__future__", "collections.abc", "typing_extensions", "clr" };

        void ProcessImport(ImportStmt s)
        {
            foreach (var alias in s.Names)
            {
                string name = alias.Name, local = alias.AsName;
                if (IgnoredModules.Contains(name)) continue;
                var mod = ResolvePsModule(name);
                if (mod != null)
                {
                    _moduleAliases[local ?? name] = mod;
                    if (local != null) _usingLines.Add($"using {Esc(local)} = {mod.QualifiedClassName};");
                    continue;
                }
                string stdlib = StdlibType(name);
                if (stdlib != null)
                {
                    string l = local ?? name.Split('.')[0];
                    if (local == null && name.Contains(".")) { Unsupported(s, "'import a.b' of a Typhon standard module (use 'from a import b')"); continue; }
                    _usingLines.Add($"using {Esc(l)} = global::{stdlib.Replace('+', '.')};");
                    _dotnetAliases[l] = stdlib;
                    continue;
                }
                if (_r.TypeExists(name))
                {
                    if (local != null) { _usingLines.Add($"using {Esc(local)} = global::{name};"); _dotnetAliases[local] = name; }
                    continue;
                }
                if (_r.NamespaceExists(name))
                {
                    if (local != null) { _usingLines.Add($"using {Esc(local)} = global::{name};"); _dotnetAliases[local] = name; }
                    else { _usingLines.Add($"using {name};"); _usingNamespaces.Add(name); }
                    continue;
                }
                Report(s, "TY0101", Severity.Error, $"No module named '{name}' (not a Typhon module in this project, a Typhon standard module, or a .NET namespace/type in the referenced assemblies)");
            }
        }

        void ProcessImportFrom(ImportFromStmt s)
        {
            string modName = ResolveRelative(s.Module, s.Level);
            if (IgnoredModules.Contains(modName)) return;

            var mod = ResolvePsModule(modName);
            if (mod != null)
            {
                _usingLines.Add($"using static {mod.QualifiedClassName};");
                foreach (var a in s.Names)
                {
                    if (a.Name == "*") { _wildcardModules.Add(mod); continue; }
                    if (!mod.Classes.ContainsKey(a.Name) && !mod.Functions.Contains(a.Name) && !mod.Globals.Contains(a.Name))
                    { Report(s, "TY0102", Severity.Error, $"cannot import name '{a.Name}' from '{modName}'"); continue; }
                    _importedFromModules[a.AsName ?? a.Name] = new KeyValuePair<ModuleInfo, string>(mod, a.Name);
                    if (a.AsName != null)
                    {
                        if (mod.Classes.ContainsKey(a.Name)) _usingLines.Add($"using {Esc(a.AsName)} = {mod.QualifiedClassName}.{Esc(a.Name)};");
                        else Unsupported(s, $"renaming the function or variable '{a.Name}' on import");
                    }
                }
                return;
            }

            string stdlib = StdlibType(modName);
            if (stdlib != null || _r.TypeExists(modName))
            {
                string meta = stdlib ?? modName;
                string csName = meta.Replace('+', '.');
                _usingLines.Add($"using static global::{csName};");
                _staticTypes.Add(meta);
                foreach (var a in s.Names)
                    if (a.AsName != null)
                    {
                        if (_r.TypeExists(meta + "+" + a.Name)) _usingLines.Add($"using {Esc(a.AsName)} = global::{csName}.{a.Name};");
                        else Unsupported(s, $"renaming the member '{a.Name}' on import");
                    }
                return;
            }

            if (_r.NamespaceExists(modName))
            {
                foreach (var a in s.Names)
                {
                    if (a.Name == "*") { _usingLines.Add($"using {modName};"); _usingNamespaces.Add(modName); continue; }
                    string full = modName + "." + a.Name;
                    if (_r.NamespaceExists(full))
                    {
                        string l = a.AsName ?? a.Name;
                        _usingLines.Add($"using {Esc(l)} = global::{full};");
                        _dotnetAliases[l] = full;
                        continue;
                    }
                    if (!AnyArity(full, out _)) { Report(s, "TY0102", Severity.Error, $"cannot import name '{a.Name}' from '{modName}' (no such .NET type or namespace)"); continue; }
                    if (a.AsName != null && _r.TypeExists(full))
                    {
                        _usingLines.Add($"using {Esc(a.AsName)} = global::{full};");
                        _importedTypes[a.AsName] = full;
                    }
                    else
                    {
                        _usingLines.Add($"using {modName};");
                        if (!_usingNamespaces.Contains(modName)) _usingNamespaces.Add(modName);
                        _importedTypes[a.Name] = full;
                    }
                }
                return;
            }
            Report(s, "TY0101", Severity.Error, $"No module named '{modName}'");
        }

        /// <summary>
        /// Typhon standard modules are .NET types in namespace Typhon.Stdlib: "math" → Typhon.Stdlib.math_ (built in),
        /// "numpy" → Typhon.Stdlib.numpy (Typhon.NumPy package). The trailing '_' avoids C#'s rule that a member
        /// cannot share its class's name (time.time, random.random). Submodules are nested types (os.path → os_+path).
        /// </summary>
        string StdlibType(string dotted)
        {
            var parts = dotted.Split('.');
            foreach (var head in new[] { parts[0], parts[0] + "_" })
            {
                string meta = "Typhon.Stdlib." + head + (parts.Length > 1 ? "+" + string.Join("+", parts.Skip(1)) : "");
                if (_r.TypeExists(meta)) return meta;
            }
            return null;
        }

        bool AnyArity(string meta, out int arity)
        {
            for (arity = 0; arity <= 8; arity++)
                if (_r.TypeExists(arity == 0 ? meta : meta + "`" + arity)) return true;
            return false;
        }

        // ───────────────────────────── functions ─────────────────────────────

        static string DecoratorName(Expr d)
        {
            if (d is CallExpr c) d = c.Func;
            if (d is NameExpr n) return n.Id;
            if (d is AttributeExpr a) return DottedName(a);
            return null;
        }

        static bool HasDecorator(List<Expr> decorators, string name) => decorators.Any(d => DecoratorName(d) == name || DecoratorName(d) == "typing." + name || DecoratorName(d) == "abc." + name || DecoratorName(d) == "dataclasses." + name);

        static readonly HashSet<string> KnownDecorators = new HashSet<string>
        {
            "staticmethod", "classmethod", "property", "abstractmethod", "override", "final", "dataclass", "overload",
            "typing.override", "typing.final", "typing.overload", "abc.abstractmethod", "dataclasses.dataclass",
        };

        void EmitAttributes(List<Expr> decorators, Func<string, bool> isHandled = null)
        {
            foreach (var d in decorators)
            {
                string name = DecoratorName(d);
                if (name == null) { Unsupported(d, "this decorator form"); continue; }
                if (KnownDecorators.Contains(name) || name.EndsWith(".setter") || name.EndsWith(".getter") || (isHandled != null && isHandled(name))) continue;
                // any other decorator is treated as a .NET attribute: @Obsolete("x") → [Obsolete("x")]
                if (d is CallExpr c)
                {
                    var args = c.Args.Select(a => a.Name != null ? $"{a.Name} = {EmitExpr(a.Value)}" : EmitExpr(a.Value));
                    _w.Line($"[{name}({string.Join(", ", args)})]");
                }
                else _w.Line($"[{name}]");
            }
        }

        static string Visibility(string name, bool topLevel)
        {
            if (name.StartsWith("__") && !name.EndsWith("__")) return "private";
            if (name.StartsWith("_") && !name.EndsWith("__")) return topLevel ? "internal" : "protected internal";
            return "public";
        }

        string TypeParamList(List<TypeParam> tps) => tps.Count == 0 ? "" : "<" + string.Join(", ", tps.Select(t => Esc(t.Name))) + ">";

        string TypeParamConstraints(List<TypeParam> tps, Node at)
        {
            var sb = new StringBuilder();
            foreach (var t in tps.Where(t => t.Bound != null))
            {
                var bounds = t.Bound is TupleExpr tup ? tup.Elts : new List<Expr> { t.Bound };
                sb.Append($" where {Esc(t.Name)} : {string.Join(", ", bounds.Select(b => MapType(b, at)))}");
            }
            return sb.ToString();
        }

        sealed class Signature
        {
            public string ReturnType;      // full C# return type incl. Task/IEnumerable wrapping
            public string ElementType;     // yield element type / awaited type / plain type
            public string Params;
            public bool IsGenerator, IsAsync;
            public LocalsResult Locals;
            public Scope Scope;
        }

        Signature BuildSignature(FunctionDef f, bool dropFirst, string selfName = null, string[] paramTypes = null, string retKey = null)
        {
            var sig = new Signature();
            var locals = LocalsAnalyzer.Analyze(f.Body, f.Params.Select(p => p.Name), null);
            sig.Locals = locals;
            sig.IsGenerator = locals.HasYield;
            sig.IsAsync = f.IsAsync;
            var scope = new Scope { Locals = locals, IsGenerator = sig.IsGenerator, IsAsync = f.IsAsync, Parent = _scope };
            sig.Scope = scope;

            var ps = new List<string>();
            var defaults = new List<string>();
            int pi = 0;
            for (int i = 0; i < f.Params.Count; i++)
            {
                var p = f.Params[i];
                if (i == 0 && dropFirst) continue;
                scope.Params.Add(p.Name);
                if (p.Kind == ParamKind.KwArgs) { Unsupported(p, "**kwargs"); continue; }
                string inferred = paramTypes != null && pi < paramTypes.Length ? paramTypes[pi] : null;
                pi++;
                string type = p.Annotation != null ? MapType(p.Annotation, p)
                    : inferred != null && inferred != TypeInference.Declared ? inferred
                    : ImplicitDynamic(p, $"parameter '{p.Name}'");
                if (type == "void") type = "object";
                scope.Types[p.Name] = type;
                string decl;
                if (p.Kind == ParamKind.VarArgs)
                {
                    if (p.Annotation == null) type = "object";
                    decl = $"params {type}[] {Esc(p.Name)}";
                    scope.Types[p.Name] = type + "[]";
                }
                else decl = $"{type} {Esc(p.Name)}";
                // an inferred overload keeps a default only if the default has the overload's parameter type
                bool keepDefault = p.Default != null && (paramTypes == null || p.Annotation != null || InferType(p.Default) == type);
                ps.Add(decl);
                defaults.Add(keepDefault ? " = " + EmitExpr(p.Default, targetTyped: true) : null);
            }
            // C#: optional parameters must come last
            for (int i = ps.Count - 1, required = 0; i >= 0; i--)
            {
                if (defaults[i] == null) required = 1;
                else if (required == 0) ps[i] += defaults[i];
            }
            sig.Params = string.Join(", ", ps);

            string elem;
            if (f.Returns != null)
            {
                elem = MapType(f.Returns, f, isReturn: true);
                if (sig.IsGenerator)
                {
                    // Iterator[T] → IEnumerable<T>: the element type is T
                    if (elem.StartsWith("IEnumerable<") || elem.StartsWith("IAsyncEnumerable<")) { sig.ReturnType = elem; elem = GenericArg(elem); }
                    else sig.ReturnType = "IEnumerable<" + elem + ">";
                }
            }
            else if (sig.IsGenerator) elem = ImplicitDynamic(f, $"the values yielded by '{f.Name}'");
            else if (locals.HasValueReturn)
                elem = retKey != null && Ti != null && Ti.Returns.TryGetValue(retKey, out var inferredReturn) && inferredReturn != "dynamic" ? inferredReturn
                     : ImplicitDynamic(f, $"the return type of '{f.Name}'");
            else elem = "void";

            sig.ElementType = elem;
            if (sig.IsGenerator)
            {
                if (sig.ReturnType == null) sig.ReturnType = (f.IsAsync ? "IAsyncEnumerable<" : "IEnumerable<") + elem + ">";
            }
            else if (f.IsAsync) sig.ReturnType = elem == "void" ? "Task" : "Task<" + elem + ">";
            else sig.ReturnType = elem;
            scope.ReturnType = elem;
            return sig;
        }

        static string GenericArg(string t)
        {
            int lt = t.IndexOf('<');
            return t.Substring(lt + 1, t.Length - lt - 2);
        }

        void EmitFunction(FunctionDef f, bool isModuleLevel)
        {
            var t = isModuleLevel ? TargetOf(_m, null, f.Name) : null;
            if (t != null && t.InferParams && Ti.Signatures.TryGetValue(t.Key, out var sigs))
            {
                for (int i = 0; i < sigs.Count; i++)
                {
                    EmitFunctionVariant(f, sigs[i], TypeInference.ReturnKey(t.Key, TypeInference.SigKey(sigs[i])), null);
                    if (i < sigs.Count - 1 || Ti.Fallback.Contains(t.Key)) _w.Line();
                }
                if (Ti.Fallback.Contains(t.Key)) EmitFunctionVariant(f, null, null, t.Key);
                return;
            }
            EmitFunctionVariant(f, null, t != null && !t.InferParams ? TypeInference.ReturnKey(t.Key, "") : null, t != null && t.InferParams ? t.Key : null);
        }

        /// <summary>One C# method for a Typhon function: typed by an inferred signature, or as written (dynamic fallback).</summary>
        void EmitFunctionVariant(FunctionDef f, string[] paramTypes, string retKey, string fallbackKey)
        {
            var savedRet = _retKey; var savedCtx = _ctxFallbackKey;
            _retKey = retKey; _ctxFallbackKey = fallbackKey;
            foreach (var tp in f.TypeParams) _currentFuncTypeParams.Add(tp.Name);
            var sig = BuildSignature(f, dropFirst: false, paramTypes: paramTypes, retKey: retKey);
            EmitDocComment(f.Body);
            LineDirective(f);
            EmitAttributes(f.Decorators);
            string vis = Visibility(f.Name, topLevel: true);
            string asyncKw = f.IsAsync && !sig.IsGenerator ? "async " : (f.IsAsync ? "async " : "");
            _w.Line($"{vis} static {asyncKw}{sig.ReturnType} {Esc(f.Name)}{TypeParamList(f.TypeParams)}({sig.Params}){TypeParamConstraints(f.TypeParams, f)}");
            EmitFunctionBody(f.Body, sig.Scope, null, sig.Locals);
            foreach (var tp in f.TypeParams) _currentFuncTypeParams.Remove(tp.Name);
            _retKey = savedRet; _ctxFallbackKey = savedCtx;
        }

        /// <summary>Emits "{ ... }" for a function-like body with Python scoping rules.</summary>
        void EmitFunctionBody(List<Stmt> body, Scope scope, IEnumerable<string> excluded, LocalsResult locals = null, string prologue = null, Action extraPrologue = null)
        {
            var saved = _scope;
            scope.Parent = saved;
            scope.Locals = locals ?? LocalsAnalyzer.Analyze(body, scope.Params, excluded);
            _scope = scope;
            _w.Open();
            if (prologue != null) _w.Line(prologue);
            extraPrologue?.Invoke();
            foreach (var h in scope.Locals.Hoisted)
            {
                string type;
                if (h.Value != null) type = MapType(h.Value, body.FirstOrDefault());
                else
                {
                    scope.Locals.HoistedFirstValue.TryGetValue(h.Key, out var fv);
                    type = (fv != null ? InferType(fv) : null) ?? ImplicitDynamic(body.FirstOrDefault(), $"local variable '{h.Key}' (assigned in a nested block)");
                }
                scope.Types[h.Key] = type;
                _w.Line($"{type} {Esc(h.Key)} = default;");
            }
            EmitBlockStatements(body);
            _w.Close();
            _scope = saved;
        }

        // ───────────────────────────── classes ─────────────────────────────

        static string DottedName(Expr e)
        {
            switch (e)
            {
                case NameExpr n: return n.Id;
                case AttributeExpr a: var t = DottedName(a.Target); return t == null ? null : t + "." + a.Name;
                default: return null;
            }
        }

        void EmitClass(ClassDef c)
        {
            var bases = new List<Expr>();
            bool isInterface = false, isEnum = false, isAbstract = false;
            foreach (var b in c.Bases)
            {
                string bn = DottedName(b is SubscriptExpr bs ? bs.Target : b);
                if (bn == "Protocol" || bn == "typing.Protocol") { isInterface = true; continue; }
                if (bn == "Enum" || bn == "IntEnum" || bn == "enum.Enum" || bn == "enum.IntEnum") { isEnum = true; continue; }
                if (bn == "ABC" || bn == "abc.ABC") { isAbstract = true; continue; }
                if (bn == "object" || bn == "Generic" || bn == "typing.Generic") continue;
                bases.Add(b);
            }
            if (isEnum) { EmitEnum(c); return; }

            var ctx = new ClassContext { Def = c, Name = c.Name, IsInterface = isInterface, IsSealed = HasDecorator(c.Decorators, "final") };
            foreach (var tp in c.TypeParams) ctx.TypeParams.Add(tp.Name);
            ctx.CsSelfType = Esc(c.Name) + TypeParamList(c.TypeParams);
            bool hasAbstractMethods = c.Body.OfType<FunctionDef>().Any(f => HasDecorator(f.Decorators, "abstractmethod"));
            if (hasAbstractMethods) isAbstract = true;

            var savedCls = _cls; var savedScope = _scope;
            _cls = ctx; _scope = null;

            EmitDocComment(c.Body);
            LineDirective(c);
            EmitAttributes(c.Decorators);
            string vis = Visibility(c.Name, topLevel: true);
            string kind = isInterface ? "partial interface" : (isAbstract ? "abstract partial class" : (ctx.IsSealed ? "sealed partial class" : "partial class"));
            var baseTypes = bases.Select(b => MapType(b, c)).ToList();
            if (!isInterface) baseTypes.AddRange(StructuralProtocols(c, bases));
            // def __iter__(self) -> Iterator[T]  ⇒  the class implements IEnumerable<T> (foreach, LINQ, comprehensions)
            var iter = c.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__iter__");
            string iterElem = null;
            if (iter != null && !isInterface)
            {
                string rt = iter.Returns != null ? MapType(iter.Returns, iter, isReturn: true) : null;
                iterElem = rt != null && rt.StartsWith("IEnumerable<") ? GenericArg(rt) : "object";
                baseTypes.Add($"global::System.Collections.Generic.IEnumerable<{iterElem}>");
            }
            ctx.IterElementType = iterElem;
            // def __lt__(self, other: Self) -> bool  ⇒  IComparable<Self>, so sorted()/min()/max()/List.Sort order the objects
            if (!isInterface && LtComparesSelf(c, ctx)) baseTypes.Add($"global::System.IComparable<{ctx.CsSelfType}>");
            // def __len__(self) -> int  ⇒  IPyLen, so len(obj) works without reflection
            if (!isInterface && c.Body.OfType<FunctionDef>().Any(f => f.Name == "__len__" && f.Params.Count == 1 && f.Returns is NameExpr rn && rn.Id == "int"))
                baseTypes.Add("global::Typhon.Runtime.IPyLen");
            string baseList = baseTypes.Count > 0 ? " : " + string.Join(", ", baseTypes) : "";
            _w.Line($"{vis} {kind} {Esc(c.Name)}{TypeParamList(c.TypeParams)}{baseList}{TypeParamConstraints(c.TypeParams, c)}");
            _w.Open();

            if (isInterface) EmitInterfaceBody(c);
            else EmitClassBody(c, ctx, bases, isAbstract);

            _w.Close();
            _cls = savedCls; _scope = savedScope;
        }

        bool LtComparesSelf(ClassDef c, ClassContext ctx)
        {
            var lt = c.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__lt__");
            return lt != null && lt.Params.Count == 2 && lt.Params[1].Annotation != null && MapType(lt.Params[1].Annotation, lt) == ctx.CsSelfType;
        }

        static bool IsProtocol(ClassDef c) => c.Bases.Any(b => { var n = DottedName(b is SubscriptExpr s ? s.Target : b); return n == "Protocol" || n == "typing.Protocol"; });

        /// <summary>
        /// Python Protocols are structural, CLR interfaces are nominal. Bridge the gap inside the project: a Typhon class
        /// that has every member of a Typhon Protocol (same names, same arity) implements that interface automatically.
        /// </summary>
        IEnumerable<string> StructuralProtocols(ClassDef c, List<Expr> explicitBases)
        {
            var explicitNames = new HashSet<string>(explicitBases.Select(b => DottedName(b)?.Split('.').Last()).Where(n => n != null));
            var members = new Dictionary<string, int>();
            foreach (var s in c.Body)
            {
                if (s is FunctionDef f) members[f.Name] = f.Params.Count;
                else if (s is AnnAssignStmt a && a.Target is NameExpr n) members[n.Id] = -1;
            }
            foreach (var kv in CollectImplicitFields(c)) if (!members.ContainsKey(kv.Key)) members[kv.Key] = -1;

            foreach (var mod in _p.Modules.Values)
                foreach (var proto in mod.Classes.Values.Where(IsProtocol))
                {
                    if (proto.Name == c.Name || explicitNames.Contains(proto.Name) || proto.TypeParams.Count > 0) continue;
                    bool matches = true;
                    foreach (var s in proto.Body)
                    {
                        if (s is FunctionDef pf) matches &= members.TryGetValue(pf.Name, out var arity) && (arity == pf.Params.Count || HasDecorator(pf.Decorators, "property"));
                        else if (s is AnnAssignStmt pa && pa.Target is NameExpr pn) matches &= members.ContainsKey(pn.Id);
                        if (!matches) break;
                    }
                    if (matches && proto.Body.Any(s => s is FunctionDef || s is AnnAssignStmt))
                        yield return mod == _m ? Esc(proto.Name) : $"{mod.QualifiedClassName}.{Esc(proto.Name)}";
                }
        }

        void EmitEnum(ClassDef c)
        {
            EmitDocComment(c.Body);
            LineDirective(c);
            _w.Line($"{Visibility(c.Name, true)} enum {Esc(c.Name)}");
            _w.Open();
            foreach (var s in c.Body)
            {
                if (s is AssignStmt a && a.Targets[0] is NameExpr n)
                {
                    bool auto = a.Value is CallExpr call && DottedName(call.Func)?.EndsWith("auto") == true;
                    _w.Line(auto ? Esc(n.Id) + "," : $"{Esc(n.Id)} = {EmitExpr(a.Value)},");
                }
                else if (!(s is PassStmt) && !(s is ExprStmt)) Unsupported(s, "methods or annotations inside an Enum");
            }
            _w.Close();
        }

        void EmitInterfaceBody(ClassDef c)
        {
            foreach (var s in c.Body)
            {
                switch (s)
                {
                    case AnnAssignStmt a when a.Target is NameExpr n:
                        LineDirective(s);
                        _w.Line($"{MapType(a.Annotation, s)} {Esc(n.Id)} {{ get; set; }}");
                        break;
                    case FunctionDef f:
                        var sig = BuildSignature(f, dropFirst: !HasDecorator(f.Decorators, "staticmethod"));
                        EmitDocComment(f.Body);
                        LineDirective(f);
                        if (HasDecorator(f.Decorators, "property")) _w.Line($"{sig.ReturnType} {Esc(f.Name)} {{ get; }}");
                        else _w.Line($"{sig.ReturnType} {Esc(f.Name)}{TypeParamList(f.TypeParams)}({sig.Params});");
                        break;
                    case ExprStmt _:
                    case PassStmt _:
                        break;
                    default: Unsupported(s, "this statement inside a Protocol"); break;
                }
            }
        }

        /// <summary>Resolves a base class expression to a Typhon class, or a .NET metadata name.</summary>
        bool ResolveBase(Expr b, out ClassDef pyClass, out string meta)
        {
            pyClass = null; meta = null;
            int arity = 0;
            var target = b;
            if (b is SubscriptExpr s) { target = s.Target; arity = s.Index is TupleExpr t ? t.Elts.Count : 1; }
            string dotted = DottedName(target);
            if (dotted == null) return false;
            return TryResolveType(dotted, arity, out meta, out pyClass);
        }

        bool BaseHasMember(List<Expr> bases, string member, bool requireOverridable, int depth = 0)
        {
            if (depth > 20) return false;
            foreach (var b in bases)
            {
                if (!ResolveBase(b, out var py, out var meta)) continue;
                if (py != null)
                {
                    if (py.Body.Any(s => (s is FunctionDef f && (f.Name == member || DunderBridge(f.Name) == member)) ||
                                         (!requireOverridable && s is AnnAssignStmt a && a.Target is NameExpr n && n.Id == member)))
                        return true;
                    if (!requireOverridable && CollectImplicitFields(py).ContainsKey(member)) return true;
                    if (BaseHasMember(py.Bases, member, requireOverridable, depth + 1)) return true;
                }
                else if (meta != null)
                {
                    if (requireOverridable ? _r.IsOverridable(meta, member) : _r.HasMember(meta, member)) return true;
                }
            }
            return false;
        }

        static string DunderBridge(string name)
        {
            switch (name)
            {
                case "__str__": return "ToString";
                case "__eq__": return "Equals";
                case "__hash__": return "GetHashCode";
                default: return null;
            }
        }

        /// <summary>self.x = ... assignments inside methods declare instance attributes (Python style).</summary>
        static Dictionary<string, KeyValuePair<Expr, KeyValuePair<FunctionDef, Expr>>> CollectImplicitFields(ClassDef c)
        {
            var result = new Dictionary<string, KeyValuePair<Expr, KeyValuePair<FunctionDef, Expr>>>(); // name → (annotation, (method, value))
            foreach (var f in c.Body.OfType<FunctionDef>())
            {
                if (f.Params.Count == 0 || HasDecorator(f.Decorators, "staticmethod")) continue;
                string self = f.Params[0].Name;
                AstWalker.VisitAll(f.Body, n =>
                {
                    Expr target = null, ann = null, value = null;
                    if (n is AssignStmt a) foreach (var t in a.Targets) { target = t; value = a.Targets.Count == 1 ? a.Value : null; Check(); }
                    else if (n is AnnAssignStmt aa) { target = aa.Target; ann = aa.Annotation; value = aa.Value; Check(); }
                    else if (n is AugAssignStmt ag) { target = ag.Target; Check(); }

                    void Check()
                    {
                        if (target is AttributeExpr at && at.Target is NameExpr tn && tn.Id == self)
                        {
                            if (!result.TryGetValue(at.Name, out var existing) || (existing.Key == null && ann != null) || (existing.Value.Value == null && value != null))
                                result[at.Name] = new KeyValuePair<Expr, KeyValuePair<FunctionDef, Expr>>(ann ?? existing.Key, new KeyValuePair<FunctionDef, Expr>(f, value ?? existing.Value.Value));
                        }
                        if (target is TupleExpr tup) foreach (var e in tup.Elts) { target = e; Check(); }
                    }
                });
            }
            return result;
        }

        void EmitClassBody(ClassDef c, ClassContext ctx, List<Expr> bases, bool isAbstract)
        {
            var methods = c.Body.OfType<FunctionDef>().ToList();
            var declared = new HashSet<string>();
            foreach (var m in methods)
                if (m.Returns != null && !m.IsAsync && m.TypeParams.Count == 0 && !AstWalker.ContainsOutsideNestedScopes(m.Body, n => n is YieldExpr))
                    ctx.MethodReturnTypes[m.Name] = MapType(m.Returns, m, isReturn: true);
            foreach (var m in methods)
                if (m.Returns == null && TargetOf(_m, c, m.Name) is InferenceTarget mt && mt.Class == c && !mt.InferParams &&
                    Ti.Returns.TryGetValue(TypeInference.ReturnKey(mt.Key, ""), out var mrt) && mrt != "dynamic")
                    ctx.MethodReturnTypes[m.Name] = mrt;
            if (Obs != null && _m.Classes.TryGetValue(c.Name, out var topCls) && topCls == c)
                Obs.MethodReturns[TypeInference.ClassKey(_m, c.Name)] = new Dictionary<string, string>(ctx.MethodReturnTypes);
            bool isDataclass = HasDecorator(c.Decorators, "dataclass");
            var dataFields = new List<AnnAssignStmt>();

            // 1. class-level declarations
            foreach (var s in c.Body)
            {
                switch (s)
                {
                    case AnnAssignStmt a when a.Target is NameExpr n:
                    {
                        bool isClassVar = a.Annotation is SubscriptExpr sub && (DottedName(sub.Target) == "ClassVar" || DottedName(sub.Target) == "typing.ClassVar");
                        string type = MapType(isClassVar ? ((SubscriptExpr)a.Annotation).Index : a.Annotation, s);
                        declared.Add(n.Id);
                        ctx.Members.Add(n.Id);
                        ctx.FieldTypes[n.Id] = type;
                        LineDirective(s);
                        string init = a.Value != null ? " = " + EmitExpr(a.Value, targetTyped: true) + ";" : "";
                        if (isClassVar) _w.Line($"{Visibility(n.Id, false)} static {type} {Esc(n.Id)}{(init.Length > 0 ? init : ";")}");
                        else
                        {
                            _w.Line($"{Visibility(n.Id, false)} {type} {Esc(n.Id)} {{ get; set; }}{init}");
                            if (isDataclass) dataFields.Add(a);
                        }
                        break;
                    }
                    case AssignStmt a when a.Targets.Count == 1 && a.Targets[0] is NameExpr n:
                    {
                        string type = InferType(a.Value) ?? ImplicitDynamic(s, $"class variable '{n.Id}'");
                        declared.Add(n.Id);
                        ctx.Members.Add(n.Id);
                        LineDirective(s);
                        _w.Line($"{Visibility(n.Id, false)} static {type} {Esc(n.Id)} = {EmitExpr(a.Value, targetTyped: type != "dynamic")};");
                        break;
                    }
                    case FunctionDef f: ctx.Members.Add(f.Name); break;
                    case ClassDef _: break;
                    case ExprStmt e when e.Value is ConstExpr: break; // docstring / ...
                    case PassStmt _: break;
                    default: Unsupported(s, "this statement in a class body"); break;
                }
            }

            // 2. attributes created via self.x = ... in methods
            foreach (var kv in CollectImplicitFields(c))
            {
                string name = kv.Key;
                if (declared.Contains(name) || ctx.Members.Contains(name)) continue;
                if (BaseHasMember(bases, name, requireOverridable: false)) continue;
                var ann = kv.Value.Key; var method = kv.Value.Value.Key; var value = kv.Value.Value.Value;
                string type;
                if (ann != null) type = MapType(ann, method);
                else
                {
                    // infer with the method's parameter types in scope: annotations, or every inferred constructor signature
                    List<string[]> variants = null;
                    if (method.Name == "__init__" && TargetOf(_m, c, "__init__") is InferenceTarget it && it.Class == c && it.InferParams &&
                        Ti.Signatures.TryGetValue(it.Key, out var initSigs) && !Ti.Fallback.Contains(it.Key))
                        variants = initSigs;
                    var candidates = new HashSet<string>();
                    foreach (var variant in (IEnumerable<string[]>)variants ?? new string[][] { null })
                    {
                        var tmpScope = new Scope();
                        int vi = 0;
                        foreach (var p in method.Params.Skip(1))
                        {
                            string pt = p.Annotation != null ? MapType(p.Annotation, p) : variant != null && variant[vi] != TypeInference.Declared ? variant[vi] : null;
                            vi++;
                            if (pt != null) { tmpScope.Params.Add(p.Name); tmpScope.Types[p.Name] = pt; }
                        }
                        var saved = _scope; _scope = tmpScope;
                        candidates.Add(value != null ? InferType(value) : null);
                        _scope = saved;
                    }
                    type = candidates.Count == 1 ? candidates.First() : null;
                    if (TypeInference.IsUnknown(type) || type == TypeInference.Pending) type = null;
                    type = type ?? ImplicitDynamic(method, $"attribute 'self.{name}'");
                }
                declared.Add(name);
                ctx.Members.Add(name);
                ctx.FieldTypes[name] = type;
                LineDirective(method);
                _w.Line($"{Visibility(name, false)} {type} {Esc(name)} {{ get; set; }}");
            }
            if (declared.Count > 0) _w.Line();
            if (Obs != null && _m.Classes.TryGetValue(c.Name, out var topLevel) && topLevel == c)
                Obs.Fields[TypeInference.ClassKey(_m, c.Name)] = new Dictionary<string, string>(ctx.FieldTypes);

            // 3. dataclass constructor / repr / equality
            if (isDataclass && !methods.Any(m => m.Name == "__init__"))
            {
                var ps = dataFields.Select(a => $"{ctx.FieldTypes[((NameExpr)a.Target).Id]} {Esc(((NameExpr)a.Target).Id)}" + (a.Value != null ? " = " + EmitExpr(a.Value, targetTyped: true) : ""));
                _w.Line($"public {Esc(c.Name)}({string.Join(", ", ps)})");
                _w.Open();
                foreach (var a in dataFields) { var n = Esc(((NameExpr)a.Target).Id); _w.Line($"this.{n} = {n};"); }
                _w.Close();
                if (dataFields.Count > 0) _w.Line($"public {Esc(c.Name)}() {{ }}");
                if (!methods.Any(m => m.Name == "__repr__" || m.Name == "__str__"))
                {
                    var parts = dataFields.Select(a => { var n = ((NameExpr)a.Target).Id; return $"{n}={{repr(this.{Esc(n)})}}"; });
                    _w.Line($"public override string ToString() => $\"{c.Name}({string.Join(", ", parts)})\";");
                }
                if (!methods.Any(m => m.Name == "__eq__"))
                {
                    var cmp = dataFields.Count == 0 ? "true" : string.Join(" && ", dataFields.Select(a => { var n = Esc(((NameExpr)a.Target).Id); return $"Equals(this.{n}, __o.{n})"; }));
                    _w.Line($"public override bool Equals(object? obj) => obj is {ctx.CsSelfType} __o && {cmp};");
                    _w.Line($"public static bool operator ==({ctx.CsSelfType}? a, {ctx.CsSelfType}? b) => a is null ? b is null : a.Equals(b);");
                    _w.Line($"public static bool operator !=({ctx.CsSelfType}? a, {ctx.CsSelfType}? b) => !(a == b);");
                    var hashFields = dataFields.Take(8).Select(a => "this." + Esc(((NameExpr)a.Target).Id));
                    _w.Line(dataFields.Count == 0 ? "public override int GetHashCode() => 0;" : $"public override int GetHashCode() => HashCode.Combine({string.Join(", ", hashFields)});");
                }
                _w.Line();
            }

            // 4. methods (properties are grouped by name)
            var propertyDone = new HashSet<string>();
            foreach (var f in methods)
            {
                if (f.Name == "__init__") { EmitConstructor(f, ctx, bases); _w.Line(); continue; }
                bool isProp = HasDecorator(f.Decorators, "property") || f.Decorators.Any(d => DecoratorName(d)?.EndsWith(".setter") == true);
                if (isProp)
                {
                    if (propertyDone.Add(f.Name)) { EmitProperty(methods.Where(m => m.Name == f.Name).ToList(), ctx, bases); _w.Line(); }
                    continue;
                }
                EmitMethod(f, ctx, bases);
                _w.Line();
            }
            if (!isDataclass && !methods.Any(m => m.Name == "__init__")) EmitInheritedConstructors(ctx, bases);
            EmitDunderBridges(methods, ctx);

            // 5. nested classes
            foreach (var nested in c.Body.OfType<ClassDef>()) EmitClass(nested);
        }

        string MethodModifiers(FunctionDef f, ClassContext ctx, List<Expr> bases, out bool isStatic, out bool isAbstract)
        {
            isStatic = HasDecorator(f.Decorators, "staticmethod") || HasDecorator(f.Decorators, "classmethod");
            isAbstract = HasDecorator(f.Decorators, "abstractmethod");
            string vis = Visibility(f.Name, false);
            if (f.Name.StartsWith("__") && f.Name.EndsWith("__")) vis = "public";
            var mods = new List<string> { vis };
            if (isStatic) mods.Add("static");
            else if (isAbstract) mods.Add("abstract");
            else if (vis != "private")
            {
                bool explicitOverride = HasDecorator(f.Decorators, "override");
                if (explicitOverride || BaseHasMember(bases, f.Name, requireOverridable: true)) mods.Add("override");
                else if (!ctx.IsSealed && !HasDecorator(f.Decorators, "final")) mods.Add("virtual");
                if (HasDecorator(f.Decorators, "final") && mods.Contains("override")) mods.Add("sealed");
            }
            if (f.IsAsync) mods.Add("async");
            return string.Join(" ", mods);
        }

        void EmitMethod(FunctionDef f, ClassContext ctx, List<Expr> bases)
        {
            bool isStaticDecl = HasDecorator(f.Decorators, "staticmethod");
            bool isClassMethod = HasDecorator(f.Decorators, "classmethod");
            if (!isStaticDecl && f.Params.Count == 0) { Error(f, $"method '{f.Name}' needs a 'self' parameter (or use @staticmethod)"); return; }
            foreach (var tp in f.TypeParams) _currentFuncTypeParams.Add(tp.Name);
            try { EmitMethodCore(f, ctx, bases, isStaticDecl, isClassMethod); }
            finally { foreach (var tp in f.TypeParams) _currentFuncTypeParams.Remove(tp.Name); }
        }

        void EmitMethodCore(FunctionDef f, ClassContext ctx, List<Expr> bases, bool isStaticDecl, bool isClassMethod)
        {
            var target = TargetOf(_m, ctx.Def, f.Name);
            string retKey = target != null && target.Class == ctx.Def && !target.InferParams && !BaseHasMember(bases, f.Name, requireOverridable: false)
                ? TypeInference.ReturnKey(target.Key, "") : null;
            var savedRet = _retKey; _retKey = retKey;
            try { EmitMethodTyped(f, ctx, bases, isStaticDecl, isClassMethod, retKey); }
            finally { _retKey = savedRet; }
        }

        void EmitMethodTyped(FunctionDef f, ClassContext ctx, List<Expr> bases, bool isStaticDecl, bool isClassMethod, string retKey)
        {
            var sig = BuildSignature(f, dropFirst: !isStaticDecl, retKey: retKey);
            string mods = MethodModifiers(f, ctx, bases, out bool isStatic, out bool isAbstract);
            if (!isStatic) { sig.Scope.HasSelf = true; sig.Scope.Params.Remove(f.Params[0].Name); }
            if (isClassMethod) { sig.Scope.ClsName = ctx.CsSelfType; sig.Scope.Params.Remove(f.Params[0].Name); }
            if (f.IsAsync && sig.IsGenerator) mods = mods.Replace("async", "async");

            EmitDocComment(f.Body);
            LineDirective(f);
            EmitAttributes(f.Decorators);
            string header = $"{mods} {sig.ReturnType} {Esc(f.Name)}{TypeParamList(f.TypeParams)}({sig.Params}){TypeParamConstraints(f.TypeParams, f)}";
            if (isAbstract) { _w.Line(header + ";"); return; }
            _w.Line(header);
            EmitFunctionBody(f.Body, sig.Scope, null, sig.Locals);
        }

        /// <summary>
        /// Python inherits __init__, C# does not inherit constructors: a class without __init__ gets one constructor per
        /// constructor of its base (the nearest Typhon __init__, or the .NET base's public/protected constructors).
        /// </summary>
        void EmitInheritedConstructors(ClassContext ctx, List<Expr> bases, int depth = 0)
        {
            foreach (var b in bases)
            {
                if (depth > 20 || !ResolveBase(b, out var py, out var meta)) continue;
                if (py != null)
                {
                    if (IsProtocol(py)) continue;
                    var init = py.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__init__");
                    if (init == null)
                    {
                        if (HasDecorator(py.Decorators, "dataclass")) return;
                        EmitInheritedConstructors(ctx, py.Bases, depth + 1);
                        return;
                    }
                    if (_p.TryFindClass(_m, py.Name, out var initCls, out var initOwner) && InitTarget(initOwner, initCls) is InferenceTarget it &&
                        it.InferParams && Ti.Signatures.TryGetValue(it.Key, out var baseSigs))
                    {
                        foreach (var types in baseSigs)
                        {
                            var tps = it.Params.Select((p, i) => $"{(types[i] == TypeInference.Declared ? MapType(p.Annotation, init) : types[i])} {Esc(p.Name)}");
                            _w.Line($"public {Esc(ctx.Name)}({string.Join(", ", tps)}) : base({string.Join(", ", it.Params.Select(p => Esc(p.Name)))}) {{ }}");
                        }
                        _w.Line();
                        if (!Ti.Fallback.Contains(it.Key)) return;
                    }
                    var ps = new List<string>(); var names = new List<string>();
                    foreach (var p in init.Params.Skip(1))
                    {
                        if (p.Kind != ParamKind.Normal) return; // *args/**kwargs: no faithful C# mirror, write an __init__
                        string type = p.Annotation != null ? MapType(p.Annotation, init) : "dynamic";
                        ps.Add($"{type} {Esc(p.Name)}" + (p.Default != null ? " = " + EmitExpr(p.Default, targetTyped: true) : ""));
                        names.Add(Esc(p.Name));
                    }
                    _w.Line($"public {Esc(ctx.Name)}({string.Join(", ", ps)}) : base({string.Join(", ", names)}) {{ }}");
                    _w.Line();
                    return;
                }
                if (meta != null)
                {
                    var ctors = _r.GetConstructors(meta);
                    foreach (var c in ctors)
                    {
                        var ps = c.Parameters.Select(kv => $"{kv.Key} {Esc(kv.Value)}");
                        _w.Line($"{(c.IsProtected ? "protected" : "public")} {Esc(ctx.Name)}({string.Join(", ", ps)}) : base({string.Join(", ", c.Parameters.Select(kv => Esc(kv.Value)))}) {{ }}");
                    }
                    if (ctors.Count > 0) _w.Line();
                    return;
                }
            }
        }

        void EmitConstructor(FunctionDef f, ClassContext ctx, List<Expr> bases)
        {
            var t = TargetOf(_m, ctx.Def, "__init__");
            if (t != null && t.Class != ctx.Def) t = null;
            if (t != null && t.InferParams && Ti.Signatures.TryGetValue(t.Key, out var sigs))
            {
                foreach (var types in sigs) { EmitConstructorVariant(f, ctx, bases, types, null); _w.Line(); }
                if (Ti.Fallback.Contains(t.Key)) EmitConstructorVariant(f, ctx, bases, null, t.Key);
                return;
            }
            EmitConstructorVariant(f, ctx, bases, null, t != null && t.InferParams ? t.Key : null);
        }

        void EmitConstructorVariant(FunctionDef f, ClassContext ctx, List<Expr> bases, string[] paramTypes, string fallbackKey)
        {
            var savedCtx = _ctxFallbackKey; _ctxFallbackKey = fallbackKey;
            try { EmitConstructorCore(f, ctx, bases, paramTypes); }
            finally { _ctxFallbackKey = savedCtx; }
        }

        void EmitConstructorCore(FunctionDef f, ClassContext ctx, List<Expr> bases, string[] paramTypes)
        {
            var sig = BuildSignature(f, dropFirst: true, paramTypes: paramTypes);
            sig.Scope.HasSelf = true;
            sig.Scope.Params.Remove(f.Params[0].Name);
            var body = f.Body;
            string baseCall = "";
            int first = body.Count > 0 && body[0] is ExprStmt ds && ds.Value is ConstExpr dc && dc.Kind == ConstKind.Str ? 1 : 0;
            if (body.Count > first && body[first] is ExprStmt es && es.Value is CallExpr call && call.Func is AttributeExpr at && at.Name == "__init__" &&
                at.Target is CallExpr sc && sc.Func is NameExpr sn && sn.Id == "super")
            {
                var saved = _scope; _scope = sig.Scope;
                baseCall = " : base(" + EmitArgs(call.Args) + ")";
                if (Obs != null && bases.Count > 0 && ResolveBase(bases[0], out var basePy, out _) && basePy != null &&
                    _p.TryFindClass(_m, basePy.Name, out var bcls, out var bowner))
                    RecordCall(InitTarget(bowner, bcls), call.Args);
                _scope = saved;
                body = body.Where((s, i) => i != first).ToList();
                sig.Locals = LocalsAnalyzer.Analyze(body, f.Params.Select(p => p.Name), null);
            }
            EmitDocComment(f.Body);
            LineDirective(f);
            EmitAttributes(f.Decorators);
            _w.Line($"{Visibility(f.Name, false).Replace("private", "public")} {Esc(ctx.Name)}({sig.Params}){baseCall}");
            EmitFunctionBody(body, sig.Scope, null, sig.Locals);
        }

        void EmitProperty(List<FunctionDef> defs, ClassContext ctx, List<Expr> bases)
        {
            var getter = defs.FirstOrDefault(d => HasDecorator(d.Decorators, "property"));
            var setter = defs.FirstOrDefault(d => d.Decorators.Any(x => DecoratorName(x)?.EndsWith(".setter") == true));
            if (getter == null) { Error(defs[0], "a property setter needs a @property getter"); return; }
            var gsig = BuildSignature(getter, dropFirst: true);
            gsig.Scope.HasSelf = true;
            gsig.Scope.Params.Remove(getter.Params[0].Name);
            string mods = MethodModifiers(getter, ctx, bases, out _, out bool isAbstract);
            EmitDocComment(getter.Body);
            LineDirective(getter);
            EmitAttributes(getter.Decorators);
            _w.Line($"{mods} {gsig.ReturnType} {Esc(getter.Name)}");
            _w.Open();
            if (isAbstract) { _w.Line(setter != null ? "get; set;" : "get;"); _w.Close(); return; }
            _w.Line("get");
            EmitFunctionBody(getter.Body, gsig.Scope, null, gsig.Locals);
            if (setter != null)
            {
                var ssig = BuildSignature(setter, dropFirst: true);
                ssig.Scope.HasSelf = true;
                ssig.Scope.Params.Remove(setter.Params[0].Name);
                string valueName = setter.Params.Count > 1 ? setter.Params[1].Name : "value";
                LineDirective(setter);
                _w.Line("set");
                EmitFunctionBody(setter.Body, ssig.Scope, null, ssig.Locals,
                    prologue: valueName != "value" ? $"var {Esc(valueName)} = value;" : null);
            }
            _w.Close();
        }

        static readonly Dictionary<string, string> BinaryDunders = new Dictionary<string, string>
        {
            ["__add__"] = "+", ["__sub__"] = "-", ["__mul__"] = "*", ["__truediv__"] = "/", ["__mod__"] = "%",
            ["__and__"] = "&", ["__or__"] = "|", ["__xor__"] = "^", ["__lt__"] = "<", ["__gt__"] = ">", ["__le__"] = "<=", ["__ge__"] = ">=",
        };
        static readonly Dictionary<string, string> ReflectedDunders = new Dictionary<string, string>
        {
            ["__radd__"] = "+", ["__rsub__"] = "-", ["__rmul__"] = "*", ["__rtruediv__"] = "/",
        };
        static readonly Dictionary<string, string> ComparisonPairs = new Dictionary<string, string> { ["<"] = ">", [">"] = "<", ["<="] = ">=", [">="] = "<=" };

        /// <summary>Python protocol methods (__str__, __add__, __getitem__, ...) are kept as methods and bridged to .NET members.</summary>
        void EmitDunderBridges(List<FunctionDef> methods, ClassContext ctx)
        {
            var self = ctx.CsSelfType;
            string ParamType(FunctionDef f, int i) => f.Params.Count > i && f.Params[i].Annotation != null ? MapType(f.Params[i].Annotation, f) : "dynamic";
            string RetType(FunctionDef f) => f.Returns != null ? MapType(f.Returns, f, isReturn: true) : "dynamic";
            var names = new HashSet<string>(methods.Select(m => m.Name));
            var definedOps = new HashSet<string>();

            foreach (var f in methods)
            {
                switch (f.Name)
                {
                    case "__str__": _w.Line("public override string ToString() => __str__();"); break;
                    case "__repr__": if (!names.Contains("__str__")) _w.Line("public override string ToString() => __repr__();"); break;
                    case "__hash__": _w.Line("public override int GetHashCode() => __hash__();"); break;
                    case "__eq__":
                    {
                        string pt = ParamType(f, 1);
                        if (pt == "object" || pt == "dynamic") _w.Line("public override bool Equals(object? obj) => __eq__(obj);");
                        else
                        {
                            _w.Line($"public override bool Equals(object? obj) => obj is {pt} __o && __eq__(__o);");
                            if (pt == self)
                            {
                                _w.Line($"public static bool operator ==({self}? a, {self}? b) => a is null ? b is null : a.Equals(b);");
                                _w.Line($"public static bool operator !=({self}? a, {self}? b) => !(a == b);");
                            }
                        }
                        break;
                    }
                    case "__neg__": _w.Line($"public static {RetType(f)} operator -({self} self) => self.__neg__();"); break;
                    case "__getitem__":
                    {
                        string kt = ParamType(f, 1);
                        var setter = methods.FirstOrDefault(m => m.Name == "__setitem__");
                        string set = setter != null ? " set => __setitem__(key, value);" : "";
                        _w.Line($"public {RetType(f)} this[{kt} key] {{ get => __getitem__(key);{set} }}");
                        break;
                    }
                    case "__len__": if (!ctx.Members.Contains("Count")) _w.Line("public int Count => __len__();"); break;
                    case "__iter__":
                        _w.Line($"public IEnumerator<{ctx.IterElementType}> GetEnumerator() => __iter__().GetEnumerator();");
                        _w.Line("global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();");
                        break;
                    default:
                        if (BinaryDunders.TryGetValue(f.Name, out var op))
                        {
                            definedOps.Add(op);
                            _w.Line($"public static {RetType(f)} operator {op}({self} self, {ParamType(f, 1)} other) => self.{f.Name}(other);");
                        }
                        else if (ReflectedDunders.TryGetValue(f.Name, out var rop))
                            _w.Line($"public static {RetType(f)} operator {rop}({ParamType(f, 1)} other, {self} self) => self.{f.Name}(other);");
                        break;
                }
            }
            var ltDef = methods.FirstOrDefault(m => m.Name == "__lt__");
            if (ltDef != null && ltDef.Params.Count == 2 && ParamType(ltDef, 1) == self)
                _w.Line($"public int CompareTo({self}? other) => other is null ? 1 : __lt__(other) ? -1 : other.__lt__(this) ? 1 : 0;");
            // C# requires comparison operators in pairs: derive the missing partner by swapping operands
            foreach (var f in methods)
                if (BinaryDunders.TryGetValue(f.Name, out var op) && ComparisonPairs.TryGetValue(op, out var partner) && !definedOps.Contains(partner))
                {
                    string pt = ParamType(f, 1);
                    if (pt == self) _w.Line($"public static {RetType(f)} operator {partner}({self} a, {self} b) => b.{f.Name}(a);");
                }
        }

        // ───────────────────────────── doc comments ─────────────────────────────

        void EmitDocComment(List<Stmt> body)
        {
            if (body.Count == 0 || !(body[0] is ExprStmt es) || !(es.Value is ConstExpr c) || c.Kind != ConstKind.Str) return;
            var lines = c.Str.Replace("\r", "").Trim('\n').Split('\n');
            int indent = lines.Skip(1).Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
            _w.Line("/// <summary>");
            for (int i = 0; i < lines.Length; i++)
            {
                string l = i == 0 ? lines[i].Trim() : (lines[i].Length >= indent ? lines[i].Substring(indent) : lines[i].Trim());
                _w.Line("/// " + System.Security.SecurityElement.Escape(l));
            }
            _w.Line("/// </summary>");
        }
    }
}
