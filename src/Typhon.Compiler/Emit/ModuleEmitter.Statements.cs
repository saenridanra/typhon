// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Linq;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler.Emit
{
    internal sealed partial class ModuleEmitter
    {
        readonly Dictionary<string, string> _renames = new Dictionary<string, string>();

        string NewTmp(string hint = "t") => "__" + hint + (_tmp++);

        bool IsInline(Node stmt, string name) => _scope?.Locals != null && _scope.Locals.IsInline(stmt, name);

        void EmitBlockStatements(List<Stmt> stmts)
        {
            foreach (var s in stmts) EmitStmt(s);
        }

        void EmitBlock(List<Stmt> stmts, string prologue = null)
        {
            _w.Open();
            if (prologue != null) _w.Line(prologue);
            EmitBlockStatements(stmts);
            _w.Close();
        }

        void EmitStmt(Stmt s)
        {
            LineDirective(s);
            switch (s)
            {
                case ExprStmt e: EmitExprStmt(e); break;
                case AssignStmt a: EmitAssign(a); break;
                case AugAssignStmt a: EmitAugAssign(a); break;
                case AnnAssignStmt a: EmitAnnAssign(a); break;
                case PassStmt _: break;
                case BreakStmt _: _w.Line("break;"); break;
                case ContinueStmt _: _w.Line("continue;"); break;
                case ReturnStmt r: EmitReturn(r); break;
                case RaiseStmt r: EmitRaise(r); break;
                case GlobalStmt _: break;
                case ImportStmt _: break;
                case ImportFromStmt _: break;
                case AssertStmt a:
                    _w.Line($"if (!{Paren(Cond(a.Test))}) throw new global::Typhon.Runtime.AssertionError({(a.Msg != null ? "str(" + EmitExpr(a.Msg) + ")" : "\"assertion failed\"")});");
                    break;
                case DelStmt d: Unsupported(d, "'del'"); break;
                case IfStmt i: EmitIf(i, isElif: false); break;
                case WhileStmt w:
                    if (w.OrElse.Count > 0) Unsupported(w, "'while ... else'");
                    _w.Line($"while ({Cond(w.Test)})");
                    EmitBlock(w.Body);
                    break;
                case ForStmt f: EmitFor(f); break;
                case TryStmt t: EmitTry(t); break;
                case WithStmt w: EmitWith(w); break;
                case FunctionDef f: EmitLocalFunction(f); break;
                case ClassDef c: Unsupported(c, "classes defined inside functions"); break;
                default: Unsupported(s, s.GetType().Name); break;
            }
        }

        void EmitExprStmt(ExprStmt e)
        {
            switch (e.Value)
            {
                case ConstExpr c when c.Kind == ConstKind.Str || c.Kind == ConstKind.Ellipsis: return; // docstring / ...
                case YieldExpr y:
                    if (y.From)
                    {
                        string t = NewTmp("y");
                        _w.Line($"foreach (var {t} in {EmitExpr(y.Value)}) yield return {t};");
                    }
                    else _w.Line($"yield return {(y.Value != null ? EmitExpr(y.Value, targetTyped: _scope?.ReturnType != "dynamic") : "default")};");
                    return;
                case CallExpr _:
                case AwaitExpr _:
                    _w.Line(StripParens(EmitExpr(e.Value)) + ";");
                    return;
                default:
                    _w.Line($"_ = {EmitExpr(e.Value)};");
                    return;
            }
        }

        static string StripParens(string code)
        {
            if (code.StartsWith("(await ") && code.EndsWith(")")) return code.Substring(1, code.Length - 2);
            return code;
        }

        // ───────────────────────────── assignment ─────────────────────────────

        string KnownType(string name)
        {
            for (var s = _scope; s != null; s = s.Parent)
                if (s.Types.TryGetValue(name, out var t)) return t;
            return null;
        }

        void SetType(string name, string type)
        {
            if (_scope != null && type != null) _scope.Types[name] = type;
        }

        void EmitAssign(AssignStmt a)
        {
            if (a.Value is YieldExpr) { Unsupported(a, "using 'yield' as an expression"); return; }
            if (a.Targets.Count == 1) { EmitAssignTo(a.Targets[0], a.Value, a); return; }
            string tmp = NewTmp();
            _w.Line($"var {tmp} = {EmitExpr(a.Value)};");
            foreach (var t in a.Targets) EmitAssignTo(t, new NameExpr { Id = tmp, Line = a.Line, Col = a.Col }, a, rawValue: tmp);
        }

        void EmitAssignTo(Expr target, Expr value, Stmt stmt, string rawValue = null)
        {
            switch (target)
            {
                case NameExpr n:
                {
                    string name = _renames.TryGetValue(n.Id, out var rn) ? rn : n.Id;
                    if (IsInline(stmt, n.Id))
                    {
                        string declType = null;
                        if (_scope.Locals.Annotations.TryGetValue(n.Id, out var ann)) declType = MapType(ann, stmt);
                        string inferred = rawValue != null ? null : InferType(value);
                        if (declType != null)
                        {
                            _w.Line($"{declType} {Esc(name)} = {rawValue ?? EmitExpr(value, targetTyped: declType != "dynamic")};");
                            SetType(n.Id, declType);
                        }
                        else if (rawValue == null && NeedsExplicitType(value))
                        {
                            string t = inferred ?? ImplicitDynamic(stmt, $"variable '{n.Id}'");
                            _w.Line($"{t} {Esc(name)} = {EmitExpr(value, targetTyped: t != "dynamic")};");
                            SetType(n.Id, t);
                        }
                        else
                        {
                            _w.Line($"var {Esc(name)} = {rawValue ?? EmitExpr(value)};");
                            SetType(n.Id, inferred);
                        }
                    }
                    else
                    {
                        string known = KnownType(n.Id);
                        _w.Line($"{EmitName(n)} = {rawValue ?? EmitExpr(value, targetTyped: known != null && known != "dynamic")};");
                    }
                    return;
                }
                case TupleExpr _:
                case ListExpr _:
                {
                    var elts = target is TupleExpr te ? te.Elts : ((ListExpr)target).Elts;
                    bool allInline = elts.All(e => e is NameExpr ne && IsInline(stmt, ne.Id) && !(_scope.Locals.Annotations.ContainsKey(ne.Id)));
                    string valueCode = rawValue ?? EmitExpr(value);
                    if (allInline) _w.Line($"var ({string.Join(", ", elts.Select(e => Esc(((NameExpr)e).Id)))}) = {valueCode};");
                    else _w.Line($"{DeconstructionTarget(target, stmt)} = {valueCode};");
                    if (value is TupleExpr vt && vt.Elts.Count == elts.Count)
                        for (int i = 0; i < elts.Count; i++)
                            if (elts[i] is NameExpr ne && IsInline(stmt, ne.Id)) SetType(ne.Id, InferType(vt.Elts[i]));
                    return;
                }
                case AttributeExpr at:
                {
                    string ft = null;
                    if (at.Target is NameExpr tn && tn.Id == "self" && _cls != null) _cls.FieldTypes.TryGetValue(at.Name, out ft);
                    _w.Line($"{EmitExpr(at)} = {rawValue ?? EmitExpr(value, targetTyped: ft != null && ft != "dynamic")};");
                    return;
                }
                case SubscriptExpr sub:
                    _w.Line($"{EmitExpr(sub)} = {rawValue ?? EmitExpr(value)};");
                    return;
                case StarredExpr _:
                    Unsupported(target, "starred assignment targets");
                    return;
                default:
                    Error(target, "cannot assign to this expression");
                    return;
            }
        }

        /// <summary>'var x = null' / 'var x = []' have no natural type in C#.</summary>
        static bool NeedsExplicitType(Expr value)
        {
            if (value is ConstExpr c && c.Kind == ConstKind.None) return true;
            if (value is LambdaExpr) return true;
            if (value is ListExpr l && l.Elts.Count == 0) return true;
            if (value is DictExpr d && d.Keys.Count == 0) return true;
            return false;
        }

        string DeconstructionTarget(Expr target, Stmt stmt)
        {
            switch (target)
            {
                case NameExpr n:
                    if (IsInline(stmt, n.Id))
                    {
                        if (_scope.Locals.Annotations.TryGetValue(n.Id, out var ann)) { var t = MapType(ann, stmt); SetType(n.Id, t); return t + " " + Esc(n.Id); }
                        return "var " + Esc(n.Id);
                    }
                    return EmitName(n);
                case TupleExpr t: return "(" + string.Join(", ", t.Elts.Select(e => DeconstructionTarget(e, stmt))) + ")";
                case ListExpr l: return "(" + string.Join(", ", l.Elts.Select(e => DeconstructionTarget(e, stmt))) + ")";
                default: return EmitExpr(target);
            }
        }

        static readonly Dictionary<string, string> AugHelpers = new Dictionary<string, string> { ["/"] = "truediv", ["//"] = "floordiv", ["**"] = "pow", ["%"] = "mod" };

        void EmitAugAssign(AugAssignStmt a)
        {
            string target = EmitExpr(a.Target);
            string value = EmitExpr(a.Value);
            if (AugHelpers.TryGetValue(a.Op, out var helper)) _w.Line($"{target} = {helper}({target}, {value});");
            else if (a.Op == "@") _w.Line($"{target} = {target}.__matmul__({value});");
            else if (a.Op == "+" && IsListy(a.Target)) _w.Line($"{target}.extend({value});"); // list += iterable mutates in place
            else _w.Line($"{target} {a.Op}= {value};");
        }

        void EmitAnnAssign(AnnAssignStmt a)
        {
            if (a.Target is NameExpr n)
            {
                string type = MapType(a.Annotation, a);
                if (IsInline(a, n.Id))
                {
                    _w.Line($"{type} {Esc(n.Id)} = {(a.Value != null ? EmitExpr(a.Value, targetTyped: type != "dynamic") : "default")};");
                    SetType(n.Id, type);
                }
                else if (a.Value != null) _w.Line($"{EmitName(n)} = {EmitExpr(a.Value, targetTyped: type != "dynamic")};");
                return;
            }
            if (a.Target is AttributeExpr at)
            {
                if (a.Value != null) _w.Line($"{EmitExpr(at)} = {EmitExpr(a.Value, targetTyped: true)};");
                return;
            }
            Unsupported(a, "this annotated assignment target");
        }

        // ───────────────────────────── control flow ─────────────────────────────

        void EmitReturn(ReturnStmt r)
        {
            if (_scope != null && _scope.IsGenerator)
            {
                if (r.Value != null) Unsupported(r, "returning a value from a generator");
                _w.Line("yield break;");
                return;
            }
            if (r.Value == null) { _w.Line("return;"); return; }
            string rt = _scope?.ReturnType;
            _w.Line($"return {EmitExpr(r.Value, targetTyped: rt != null && rt != "dynamic" && rt != "void")};");
        }

        void EmitRaise(RaiseStmt r)
        {
            if (r.Exc == null) { _w.Line("throw;"); return; }
            if (r.Exc is NameExpr || r.Exc is AttributeExpr)
            {
                if (TryTypeExpr(r.Exc, out var typeCode)) { _w.Line($"throw new {typeCode}();"); return; }
            }
            _w.Line($"throw {EmitExpr(r.Exc)};");
        }

        void EmitIf(IfStmt i, bool isElif)
        {
            _w.Line($"{(isElif ? "else if" : "if")} ({Cond(i.Test)})");
            EmitBlock(i.Body);
            if (i.OrElse.Count == 1 && i.OrElse[0] is IfStmt elif)
            {
                LineDirective(elif);
                EmitIf(elif, isElif: true);
            }
            else if (i.OrElse.Count > 0)
            {
                _w.Line("else");
                EmitBlock(i.OrElse);
            }
        }

        bool IsBuiltinRange(Expr iter, out List<Expr> args)
        {
            args = null;
            if (!(iter is CallExpr c) || !(c.Func is NameExpr fn) || fn.Id != "range" || IsShadowed("range")) return false;
            if (c.Args.Count < 1 || c.Args.Count > 3 || c.Args.Any(a => a.Name != null || a.Star || a.DoubleStar)) return false;
            args = c.Args.Select(a => a.Value).ToList();
            return true;
        }

        static bool AssignsName(List<Stmt> body, string name)
        {
            bool found = false;
            void CheckTarget(Expr t)
            {
                if (t is NameExpr ne && ne.Id == name) found = true;
                else if (t is TupleExpr tup) foreach (var e in tup.Elts) CheckTarget(e);
                else if (t is ListExpr list) foreach (var e in list.Elts) CheckTarget(e);
            }
            AstWalker.VisitAll(body, n =>
            {
                if (n is AssignStmt a) foreach (var t in a.Targets) CheckTarget(t);
                if (n is AugAssignStmt aa) CheckTarget(aa.Target);
                if (n is AnnAssignStmt an) CheckTarget(an.Target);
                if (n is ForStmt f) CheckTarget(f.Target);
                if (n is WithStmt w) foreach (var item in w.Items) if (item.Var != null) CheckTarget(item.Var);
            });
            return found;
        }

        void EmitFor(ForStmt f)
        {
            if (f.OrElse.Count > 0) Unsupported(f, "'for ... else'");

            // Fast path: for i in range(...) → a plain counting loop (no enumerator allocation).
            if (!f.IsAsync && f.Target is NameExpr tn && IsBuiltinRange(f.Iter, out var args))
            {
                string start = args.Count >= 2 ? EmitExpr(args[0]) : "0";
                string stop = EmitExpr(args.Count >= 2 ? args[1] : args[0]);
                string step = args.Count == 3 ? EmitExpr(args[2]) : "1";
                bool inline = IsInline(f, tn.Id);
                bool direct = inline && !AssignsName(f.Body, tn.Id);
                string iv = direct ? Esc(tn.Id) : NewTmp("i");
                string end = NewTmp("end");
                string cond, incr;
                var stepConst = args.Count == 3 ? ConstIntValue(args[2]) : 1;
                if (stepConst.HasValue)
                {
                    cond = stepConst.Value > 0 ? $"{iv} < {end}" : $"{iv} > {end}";
                    incr = stepConst.Value == 1 ? $"{iv}++" : stepConst.Value == -1 ? $"{iv}--" : $"{iv} += {step}";
                }
                else
                {
                    // unknown step sign: precompute the trip count like Python's range does, keeping the loop test trivial
                    string st = NewTmp("step"), first = NewTmp("start"), k = NewTmp("k");
                    _w.Line($"int {first} = {start}, {st} = {step};");
                    _w.Line($"for (int {k} = 0, {end} = range_len({first}, {stop}, {st}), {iv} = {first}; {k} < {end}; {k}++, {iv} += {st})");
                    cond = null; incr = null;
                }
                if (cond != null) _w.Line($"for (int {iv} = {start}, {end} = {stop}; {cond}; {incr})");
                if (direct) { SetType(tn.Id, "int"); EmitBlock(f.Body); }
                else
                {
                    string prologue = inline ? $"var {Esc(tn.Id)} = {iv};" : $"{EmitName(tn)} = {iv};";
                    if (inline) SetType(tn.Id, "int");
                    EmitBlock(f.Body, prologue);
                }
                return;
            }

            string iter = EmitExpr(f.Iter);
            string kw = f.IsAsync ? "await foreach" : "foreach";
            if (f.Target is NameExpr n)
            {
                if (IsInline(f, n.Id)) { _w.Line($"{kw} (var {Esc(n.Id)} in {iter})"); EmitBlock(f.Body); }
                else
                {
                    string t = NewTmp();
                    _w.Line($"{kw} (var {t} in {iter})");
                    EmitBlock(f.Body, $"{EmitName(n)} = {t};");
                }
                return;
            }
            if (f.Target is TupleExpr tup)
            {
                bool allInline = tup.Elts.All(e => e is NameExpr ne && IsInline(f, ne.Id));
                if (allInline)
                {
                    _w.Line($"{kw} (var {DeconstructionPattern(tup)} in {iter})");
                    EmitBlock(f.Body);
                }
                else
                {
                    string t = NewTmp();
                    _w.Line($"{kw} (var {t} in {iter})");
                    EmitBlock(f.Body, $"{DeconstructionTarget(tup, f)} = {t};");
                }
                return;
            }
            Unsupported(f.Target, "this loop target");
        }

        string DeconstructionPattern(Expr e)
        {
            if (e is NameExpr n) return Esc(n.Id);
            if (e is TupleExpr t) return "(" + string.Join(", ", t.Elts.Select(DeconstructionPattern)) + ")";
            Unsupported(e, "this loop target");
            return "_";
        }

        static long? ConstIntValue(Expr e)
        {
            if (e is ConstExpr c && c.Kind == ConstKind.Int && long.TryParse(c.Raw.Replace("_", ""), out var v)) return v;
            if (e is UnaryExpr u && u.Op == "-" && ConstIntValue(u.Operand) is long inner) return -inner;
            return null;
        }

        void EmitTry(TryStmt t)
        {
            bool hasElse = t.OrElse.Count > 0;
            bool hasFinally = t.FinalBody.Count > 0;
            string ok = null;
            if (hasElse)
            {
                // try: A except: B else: C finally: D  →  try { ok=false; try { A; ok=true } catch {B}; if (ok) C } finally { D }
                if (hasFinally) { _w.Line("try"); _w.Open(); }
                ok = NewTmp("ok");
                _w.Line($"bool {ok} = false;");
            }
            _w.Line("try");
            _w.Open();
            EmitBlockStatements(t.Body);
            if (ok != null) _w.Line($"{ok} = true;");
            _w.Close();
            foreach (var h in t.Handlers)
            {
                LineDirective(h);
                string name = h.Name;
                string csName = name;
                bool renamed = false;
                if (name != null && (IsLocal(name) || _renames.ContainsKey(name))) { csName = NewTmp("e"); renamed = true; }
                if (h.Type == null) _w.Line(name == null ? "catch" : $"catch (Exception {Esc(csName)})");
                else
                {
                    var types = CatchTypes(h.Type, h);
                    if (types.Count == 1) _w.Line(name == null ? $"catch ({types[0]})" : $"catch ({types[0]} {Esc(csName)})");
                    else
                    {
                        string v = csName ?? NewTmp("e");
                        _w.Line($"catch (Exception {Esc(v)}) when ({string.Join(" || ", types.Select(x => $"{Esc(v)} is {x}"))})");
                    }
                }
                string saved = null;
                if (renamed) { _renames.TryGetValue(name, out saved); _renames[name] = csName; }
                EmitBlock(h.Body);
                if (renamed) { if (saved != null) _renames[name] = saved; else _renames.Remove(name); }
            }
            if (hasElse)
            {
                if (t.Handlers.Count == 0) { _w.Line("finally"); _w.Open(); _w.Close(); }
                _w.Line($"if ({ok})");
                EmitBlock(t.OrElse);
                if (hasFinally)
                {
                    _w.Close();
                    _w.Line("finally");
                    EmitBlock(t.FinalBody);
                }
                return;
            }
            if (hasFinally)
            {
                _w.Line("finally");
                EmitBlock(t.FinalBody);
            }
            if (t.Handlers.Count == 0 && !hasFinally) { _w.Line("finally"); _w.Open(); _w.Close(); }
        }

        void EmitWith(WithStmt w)
        {
            string kw = w.IsAsync ? "await using" : "using";
            foreach (var item in w.Items)
            {
                string ctx = EmitExpr(item.Context);
                if (item.Var == null) _w.Line($"{kw} ({ctx})");
                else if (item.Var is NameExpr n)
                {
                    if (IsInline(w, n.Id)) _w.Line($"{kw} (var {Esc(n.Id)} = {ctx})");
                    else _w.Line($"{kw} ({EmitName(n)} = {ctx})");
                }
                else Unsupported(item.Var, "this 'with ... as' target");
            }
            EmitBlock(w.Body);
        }

        void EmitLocalFunction(FunctionDef f)
        {
            if (f.Decorators.Count > 0) Unsupported(f, "decorators on nested functions");
            foreach (var tp in f.TypeParams) _currentFuncTypeParams.Add(tp.Name);
            var sig = BuildSignature(f, dropFirst: false);
            sig.Scope.HasSelf = false;
            if (!f.IsAsync && !sig.IsGenerator) _funcReturnTypes[f.Name] = sig.ReturnType;
            string asyncKw = f.IsAsync ? "async " : "";
            _w.Line($"{asyncKw}{sig.ReturnType} {Esc(f.Name)}{TypeParamList(f.TypeParams)}({sig.Params}){TypeParamConstraints(f.TypeParams, f)}");
            EmitFunctionBody(f.Body, sig.Scope, null, sig.Locals);
            foreach (var tp in f.TypeParams) _currentFuncTypeParams.Remove(tp.Name);
        }
    }
}
