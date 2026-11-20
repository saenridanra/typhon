// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    /// <summary>A function or constructor whose missing annotations the inference fills in.</summary>
    internal sealed class InferenceTarget
    {
        public string Key;              // "module.func" or "module.Class.method"
        public ModuleInfo Module;
        public ClassDef Class;          // null for module-level functions
        public FunctionDef Def;
        public int FirstParam;          // 1 when the first parameter is self
        /// <summary>Unannotated parameters are specialised per call signature (one overload per argument-type tuple).</summary>
        public bool InferParams;
        public List<Param> Params => Def.Params.Skip(FirstParam).ToList();
    }

    /// <summary>What one dry run of the emitter saw.</summary>
    internal sealed class Observations
    {
        public sealed class Call
        {
            public string Callee;
            public string[] Types;
            /// <summary>Key of the inference target whose dynamic fallback body contains the call (null: code that always runs).</summary>
            public string Context;
        }

        public readonly List<Call> Calls = new List<Call>();
        public readonly Dictionary<string, List<string>> Returns = new Dictionary<string, List<string>>();
        public readonly Dictionary<string, Dictionary<string, string>> Fields = new Dictionary<string, Dictionary<string, string>>();
        public readonly Dictionary<string, Dictionary<string, string>> MethodReturns = new Dictionary<string, Dictionary<string, string>>();

        public void AddReturn(string key, string type)
        {
            if (!Returns.TryGetValue(key, out var list)) Returns[key] = list = new List<string>();
            list.Add(type);
        }
    }

    /// <summary>
    /// Whole-program type inference for unannotated Typhon code.
    ///
    /// The emitter already infers local variables (C# <c>var</c>) and simple expressions. What C# cannot infer are the
    /// types it needs spelled out: parameters, return types and fields. This pass runs the emitter in a dry mode that
    /// records, for every call of a project function or constructor, the argument types at the call site, and for every
    /// <c>return</c> the type of the returned value. Each distinct, fully known argument-type tuple becomes one typed
    /// overload of the function (monomorphisation, like Codon), so <c>f(1)</c> and <c>f(2.5)</c> keep Python's int/float
    /// behaviour. The untyped <c>dynamic</c> version is kept only when a call site that can actually run passes values
    /// of unknown type, so the program stays correct, and fully inferred programs contain no dynamic code (Native AOT).
    ///
    /// It is an optimistic fixpoint: pass k emits with the signatures derived from pass k-1. A recursive call whose
    /// return type is still being inferred contributes "no information" (<see cref="Pending"/>). If the signatures do not
    /// settle within <see cref="MaxPasses"/> passes, the inference is dropped and the code is emitted as written.
    /// </summary>
    internal sealed class TypeInference
    {
        public const string Unknown = "?";
        public const string Pending = "⊥";
        public const string NoneType = "None";
        /// <summary>Placeholder in a signature tuple for an annotated parameter (its declared type is used).</summary>
        public const string Declared = "@";
        const int MaxPasses = 8, MaxOverloads = 6;

        public readonly Dictionary<string, InferenceTarget> Targets = new Dictionary<string, InferenceTarget>();
        public Dictionary<string, List<string[]>> Signatures = new Dictionary<string, List<string[]>>();
        public HashSet<string> Fallback = new HashSet<string>();
        public Dictionary<string, string> Returns = new Dictionary<string, string>();
        public Dictionary<string, Dictionary<string, string>> Fields = new Dictionary<string, Dictionary<string, string>>();
        public Dictionary<string, Dictionary<string, string>> MethodReturns = new Dictionary<string, Dictionary<string, string>>();
        public Observations Obs;
        public int Passes;
        public bool Converged;

        public static string FuncKey(ModuleInfo m, ClassDef c, string name) => m.Name + "." + (c != null ? c.Name + "." : "") + name;
        public static string ClassKey(ModuleInfo m, string className) => m.Name + "." + className;
        public static string SigKey(string[] types) => types == null ? "" : string.Join(",", types);
        public static string ReturnKey(string key, string sig) => key + "|" + sig;
        public static bool IsUnknown(string t) => t == null || t == Unknown || t == Pending || t == "dynamic" || t == "object" || t == "object?";

        public static void Run(ProjectContext p)
        {
            var ti = new TypeInference();
            p.Inference = ti;
            ti.CollectTargets(p);
            if (ti.Targets.Count == 0) { ti.Converged = true; return; }
            for (int pass = 0; pass < MaxPasses; pass++)
            {
                ti.Passes = pass + 1;
                ti.Obs = new Observations();
                foreach (var m in p.Modules.Values)
                {
                    if (m.ParseDiagnostics.Count > 0) continue;
                    try { new ModuleEmitter(m, p, new List<TyDiagnostic>(), dryRun: true).Emit(); }
                    catch (Exception) { /* the real emission reports the problem */ }
                }
                bool changed = ti.Derive();
                ti.Obs = null;
                if (!changed) { ti.Converged = true; break; }
            }
            if (!ti.Converged)
            {
                ti.Signatures.Clear(); ti.Fallback.Clear(); ti.Returns.Clear(); ti.Fields.Clear(); ti.MethodReturns.Clear();
            }
        }

        // ───────────────────────────── targets ─────────────────────────────

        void CollectTargets(ProjectContext p)
        {
            var usedAsValue = NamesUsedAsValues(p);
            foreach (var m in p.Modules.Values)
            {
                if (m.ParseDiagnostics.Count > 0) continue;
                foreach (var f in m.Ast.Body.OfType<FunctionDef>())
                    Add(m, null, f, allowParams: !usedAsValue.Contains(f.Name) && f.Decorators.Count == 0);
                foreach (var c in m.Classes.Values)
                {
                    if (IsSpecialClass(c)) continue;
                    foreach (var f in c.Body.OfType<FunctionDef>())
                    {
                        if (f.Name == "__init__") { Add(m, c, f, allowParams: true); continue; }
                        // other methods: return types only, and only when no other Typhon class overrides or is overridden by them
                        if (f.Name.StartsWith("__") || f.Decorators.Count > 0 || IsOverridden(p, c, f.Name)) continue;
                        Add(m, c, f, allowParams: false);
                    }
                }
            }
        }

        void Add(ModuleInfo m, ClassDef c, FunctionDef f, bool allowParams)
        {
            if (f.TypeParams.Count > 0 || f.Params.Any(x => x.Kind != ParamKind.Normal)) return;
            int first = c != null ? 1 : 0;
            if (c != null && f.Params.Count == 0) return;
            bool unannotated = f.Params.Skip(first).Any(x => x.Annotation == null);
            var locals = LocalsAnalyzer.Analyze(f.Body, f.Params.Select(x => x.Name), null);
            bool inferReturn = f.Returns == null && locals.HasValueReturn && !locals.HasYield;
            bool inferParams = allowParams && unannotated;
            if (!inferParams && !inferReturn) return;
            var key = FuncKey(m, c, f.Name);
            Targets[key] = new InferenceTarget { Key = key, Module = m, Class = c, Def = f, FirstParam = first, InferParams = inferParams };
        }

        static bool IsSpecialClass(ClassDef c) => c.Bases.Any(b =>
        {
            var n = (b is SubscriptExpr s ? s.Target : b) is NameExpr ne ? ne.Id : (b is AttributeExpr ae ? ae.Name : null);
            return n == "Protocol" || n == "Enum" || n == "IntEnum";
        }) || c.Decorators.Any(d => (d is NameExpr dn && dn.Id == "dataclass") || (d is CallExpr dc && dc.Func is NameExpr dcn && dcn.Id == "dataclass"));

        /// <summary>Does another Typhon class define the same method in an inheritance relation with this one?</summary>
        static bool IsOverridden(ProjectContext p, ClassDef c, string method)
        {
            var all = p.Modules.Values.SelectMany(m => m.Classes.Values).ToList();
            bool Defines(ClassDef k) => k.Body.OfType<FunctionDef>().Any(f => f.Name == method);
            IEnumerable<string> BaseNames(ClassDef k) => k.Bases.Select(b => (b is SubscriptExpr s ? s.Target : b) is NameExpr n ? n.Id : (b is AttributeExpr a ? a.Name : null)).Where(n => n != null);
            bool Derives(ClassDef k, ClassDef from, int depth)
            {
                if (depth > 20) return false;
                foreach (var bn in BaseNames(k))
                {
                    if (bn == from.Name) return true;
                    var bc = all.FirstOrDefault(x => x.Name == bn);
                    if (bc != null && Derives(bc, from, depth + 1)) return true;
                }
                return false;
            }
            // a .NET base class might declare the method virtual (ABC, object and Generic[T] declare nothing)
            if (BaseNames(c).Any(bn => bn != "ABC" && bn != "object" && bn != "Generic" && all.All(x => x.Name != bn))) return true;
            foreach (var other in all)
            {
                if (other == c || !Defines(other)) continue;
                if (Derives(other, c, 0) || Derives(c, other, 0)) return true;
            }
            return false;
        }

        /// <summary>Function names that appear anywhere other than in call position (passed as callbacks, stored, ...).</summary>
        static HashSet<string> NamesUsedAsValues(ProjectContext p)
        {
            var result = new HashSet<string>();
            foreach (var m in p.Modules.Values)
            {
                if (m.ParseDiagnostics.Count > 0) continue;
                // callees, and functions passed directly as call arguments (those become lambdas that call them)
                var callees = new HashSet<Node>();
                AstWalker.VisitAll(m.Ast.Body, n =>
                {
                    if (!(n is CallExpr c)) return;
                    callees.Add(c.Func);
                    foreach (var a in c.Args) if (a.Value is NameExpr) callees.Add(a.Value);
                });
                AstWalker.VisitAll(m.Ast.Body, n => { if (n is NameExpr ne && !callees.Contains(ne)) result.Add(ne.Id); });
            }
            return result;
        }

        // ───────────────────────────── fixpoint step ─────────────────────────────

        /// <summary>Derives signatures, fallbacks, return and field types from this pass's observations. True if anything changed.</summary>
        bool Derive()
        {
            // Which dynamic fallback bodies can run? Code outside fallback bodies always can; a fallback can when a running
            // call site passes unknown types, or when the function has no typed signature at all (then it is the function).
            var live = new HashSet<string>();
            Dictionary<string, List<string[]>> sigs = null;
            for (int iter = 0; iter < 20; iter++)
            {
                sigs = new Dictionary<string, List<string[]>>();
                var unknownCall = new HashSet<string>();
                foreach (var call in Obs.Calls)
                {
                    if (call.Context != null && !live.Contains(call.Context)) continue;
                    if (!Targets.TryGetValue(call.Callee, out var t) || !t.InferParams) continue;
                    if (call.Types == null || call.Types.Any(IsUnknown)) { unknownCall.Add(call.Callee); continue; }
                    if (!sigs.TryGetValue(call.Callee, out var list)) sigs[call.Callee] = list = new List<string[]>();
                    if (!list.Any(s => SigKey(s) == SigKey(call.Types))) list.Add(call.Types);
                }
                foreach (var kv in sigs.ToList())
                    if (kv.Value.Count > MaxOverloads) { sigs.Remove(kv.Key); unknownCall.Add(kv.Key); }
                var newLive = new HashSet<string>(Targets.Values.Where(t => t.InferParams && (unknownCall.Contains(t.Key) || !sigs.ContainsKey(t.Key))).Select(t => t.Key));
                if (newLive.SetEquals(live)) break;
                live = newLive;
            }

            var returns = new Dictionary<string, string>();
            foreach (var t in Targets.Values)
            {
                var variants = t.InferParams ? (sigs.TryGetValue(t.Key, out var s) ? s.Select(SigKey) : Enumerable.Empty<string>()) : new[] { "" };
                foreach (var v in variants)
                {
                    // a body emitted with this signature decides its return type for good: a type, or "dynamic" when the
                    // returns disagree. Only a signature not emitted yet stays open (Pending), so the optimism of a dry
                    // run never outlives the fixpoint.
                    string rk = ReturnKey(t.Key, v);
                    if (Obs.Returns.TryGetValue(rk, out var seen)) returns[rk] = Unify(seen) ?? "dynamic";
                }
            }

            bool changed = !SameSigs(sigs, Signatures) || !live.SetEquals(Fallback) || !SameMap(returns, Returns) ||
                           !SameNested(Obs.Fields, Fields) || !SameNested(Obs.MethodReturns, MethodReturns);
            Signatures = sigs;
            Fallback = live;
            Returns = returns;
            Fields = Obs.Fields;
            MethodReturns = Obs.MethodReturns;
            return changed;
        }

        /// <summary>The single type of all returned values; None mixed with a type T gives T (reference) or T? (value type).</summary>
        static string Unify(List<string> seen)
        {
            var known = seen.Where(x => x != Pending).ToList();
            if (known.Count == 0 || known.Any(x => IsUnknown(x) && x != Pending)) return null;
            bool hasNone = known.RemoveAll(x => x == NoneType) > 0;
            var distinct = known.Distinct().ToList();
            if (distinct.Count != 1) return null;
            string t = distinct[0];
            if (!hasNone || t.EndsWith("?")) return t;
            return IsValueType(t) ? t + "?" : t;
        }

        static bool IsValueType(string t) => t == "int" || t == "long" || t == "double" || t == "float" || t == "bool" || t == "char" || t == "decimal" || t.StartsWith("(");

        static bool SameSigs(Dictionary<string, List<string[]>> a, Dictionary<string, List<string[]>> b) =>
            a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var o) && kv.Value.Select(SigKey).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(o.Select(SigKey).OrderBy(x => x, StringComparer.Ordinal)));

        static bool SameMap(Dictionary<string, string> a, Dictionary<string, string> b) =>
            a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var o) && o == kv.Value);

        static bool SameNested(Dictionary<string, Dictionary<string, string>> a, Dictionary<string, Dictionary<string, string>> b) =>
            a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var o) && SameMap(kv.Value, o));
    }
}
