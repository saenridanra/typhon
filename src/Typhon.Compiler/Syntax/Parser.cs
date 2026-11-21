// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Text;

namespace Typhon.Compiler.Syntax
{
    /// <summary>Recursive-descent parser for the Typhon subset of Python 3.12 syntax.</summary>
    public sealed class Parser
    {
        readonly List<Token> _t;
        int _i;

        public Parser(List<Token> tokens) { _t = tokens; }

        public static ModuleAst ParseModule(string source, string path)
        {
            var p = new Parser(new Lexer(source).Tokenize());
            var m = new ModuleAst { Path = path };
            while (p.Cur.Kind != TokenKind.EndOfFile)
            {
                if (p.Cur.Kind == TokenKind.Newline) { p._i++; continue; }
                m.Body.AddRange(p.ParseStatement());
            }
            return m;
        }

        /// <summary>Parses a standalone expression (used for f-string holes).</summary>
        public static Expr ParseExpression(string source, int line, int col)
        {
            var tokens = new Lexer("(" + source + ")", line - 1).Tokenize();
            var p = new Parser(tokens);
            var e = p.ParseTestListOrStar();
            return e;
        }

        // ───────────────────────────── helpers ─────────────────────────────

        Token Cur => _t[_i];
        Token Peek(int n = 1) => _t[System.Math.Min(_i + n, _t.Count - 1)];

        bool IsOp(string op) => Cur.Kind == TokenKind.Op && Cur.Text == op;
        bool IsKw(string kw) => Cur.Kind == TokenKind.Name && Cur.Text == kw;

        bool AcceptOp(string op) { if (IsOp(op)) { _i++; return true; } return false; }
        bool AcceptKw(string kw) { if (IsKw(kw)) { _i++; return true; } return false; }

        Token ExpectOp(string op)
        {
            if (!IsOp(op)) throw Err("expected '" + op + "' but found " + Describe(Cur));
            return _t[_i++];
        }

        void ExpectKw(string kw)
        {
            if (!IsKw(kw)) throw Err("expected '" + kw + "' but found " + Describe(Cur));
            _i++;
        }

        string ExpectName()
        {
            if (Cur.Kind != TokenKind.Name || Keywords.Contains(Cur.Text)) throw Err("expected a name but found " + Describe(Cur));
            return _t[_i++].Text;
        }

        void ExpectNewline()
        {
            if (Cur.Kind == TokenKind.Newline) { _i++; return; }
            if (Cur.Kind == TokenKind.EndOfFile || Cur.Kind == TokenKind.Dedent) return;
            throw Err("expected end of line but found " + Describe(Cur));
        }

        static string Describe(Token t)
        {
            switch (t.Kind)
            {
                case TokenKind.Newline: return "end of line";
                case TokenKind.Indent: return "an indented block";
                case TokenKind.Dedent: return "end of block";
                case TokenKind.EndOfFile: return "end of file";
                default: return "'" + t.Text + "'";
            }
        }

        SyntaxError Err(string msg) => new SyntaxError(msg, Cur.Line, Cur.Col);

        T At<T>(T node, Token tok) where T : Node { node.Line = tok.Line; node.Col = tok.Col; return node; }

        static readonly HashSet<string> Keywords = new HashSet<string>
        {
            "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del",
            "elif", "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal",
            "not", "or", "pass", "raise", "return", "try", "while", "with", "yield",
        };

        // ───────────────────────────── statements ─────────────────────────────

        List<Stmt> ParseStatement()
        {
            var tok = Cur;
            if (IsOp("@")) return new List<Stmt> { ParseDecorated() };
            if (IsKw("def")) return new List<Stmt> { ParseFuncDef(new List<Expr>(), false) };
            if (IsKw("class")) return new List<Stmt> { ParseClassDef(new List<Expr>()) };
            if (IsKw("if")) return new List<Stmt> { ParseIf() };
            if (IsKw("while")) return new List<Stmt> { ParseWhile() };
            if (IsKw("for")) return new List<Stmt> { ParseFor(false) };
            if (IsKw("try")) return new List<Stmt> { ParseTry() };
            if (IsKw("with")) return new List<Stmt> { ParseWith(false) };
            if (IsKw("async"))
            {
                _i++;
                if (IsKw("def")) return new List<Stmt> { ParseFuncDef(new List<Expr>(), true) };
                if (IsKw("for")) return new List<Stmt> { ParseFor(true) };
                if (IsKw("with")) return new List<Stmt> { ParseWith(true) };
                throw Err("expected 'def', 'for' or 'with' after 'async'");
            }
            if (IsKw("match") && StartsMatchStatement()) return new List<Stmt> { ParseMatch() };
            return ParseSimpleStatements();
        }

        List<Stmt> ParseSimpleStatements()
        {
            var list = new List<Stmt> { ParseSmallStatement() };
            while (AcceptOp(";"))
            {
                if (Cur.Kind == TokenKind.Newline) break;
                list.Add(ParseSmallStatement());
            }
            ExpectNewline();
            return list;
        }

        Stmt ParseSmallStatement()
        {
            var tok = Cur;
            if (AcceptKw("pass")) return At(new PassStmt(), tok);
            if (AcceptKw("break")) return At(new BreakStmt(), tok);
            if (AcceptKw("continue")) return At(new ContinueStmt(), tok);
            if (AcceptKw("return"))
            {
                var r = At(new ReturnStmt(), tok);
                if (!AtStatementEnd()) r.Value = ParseTestListOrStar();
                return r;
            }
            if (AcceptKw("raise"))
            {
                var r = At(new RaiseStmt(), tok);
                if (!AtStatementEnd())
                {
                    r.Exc = ParseTest();
                    if (AcceptKw("from")) r.Cause = ParseTest();
                }
                return r;
            }
            if (IsKw("global") || IsKw("nonlocal"))
            {
                var g = At(new GlobalStmt { Nonlocal = Cur.Text == "nonlocal" }, tok);
                _i++;
                g.Names.Add(ExpectName());
                while (AcceptOp(",")) g.Names.Add(ExpectName());
                return g;
            }
            if (AcceptKw("assert"))
            {
                var a = At(new AssertStmt { Test = ParseTest() }, tok);
                if (AcceptOp(",")) a.Msg = ParseTest();
                return a;
            }
            if (AcceptKw("del"))
            {
                var d = At(new DelStmt(), tok);
                d.Targets.Add(ParseOrExpr());
                while (AcceptOp(",")) d.Targets.Add(ParseOrExpr());
                return d;
            }
            if (AcceptKw("import"))
            {
                var s = At(new ImportStmt(), tok);
                do
                {
                    var alias = new ImportAlias { Name = ParseDottedName() };
                    if (AcceptKw("as")) alias.AsName = ExpectName();
                    s.Names.Add(alias);
                } while (AcceptOp(","));
                return s;
            }
            if (AcceptKw("from"))
            {
                var s = At(new ImportFromStmt(), tok);
                while (IsOp(".") || IsOp("..."))
                {
                    s.Level += Cur.Text.Length;
                    _i++;
                }
                s.Module = IsKw("import") ? "" : ParseDottedName();
                ExpectKw("import");
                if (AcceptOp("*")) { s.Names.Add(new ImportAlias { Name = "*" }); return s; }
                bool paren = AcceptOp("(");
                do
                {
                    if (paren && IsOp(")")) break;
                    var alias = new ImportAlias { Name = ExpectName() };
                    if (AcceptKw("as")) alias.AsName = ExpectName();
                    s.Names.Add(alias);
                } while (AcceptOp(","));
                if (paren) ExpectOp(")");
                return s;
            }

            // expression statement / assignment
            var first = ParseTestListOrStar(allowYield: true);
            if (IsOp(":"))
            {
                _i++;
                var ann = At(new AnnAssignStmt { Target = first, Annotation = ParseTest() }, tok);
                if (AcceptOp("=")) ann.Value = ParseTestListOrStar(allowYield: true);
                return ann;
            }
            if (Cur.Kind == TokenKind.Op && Cur.Text.Length >= 2 && Cur.Text.EndsWith("=") && Cur.Text != "==" && Cur.Text != "<=" && Cur.Text != ">=" && Cur.Text != "!=")
            {
                string op = Cur.Text.Substring(0, Cur.Text.Length - 1);
                _i++;
                return At(new AugAssignStmt { Target = first, Op = op, Value = ParseTestListOrStar(allowYield: true) }, tok);
            }
            if (IsOp("="))
            {
                var assign = At(new AssignStmt(), tok);
                assign.Targets.Add(first);
                Expr value = null;
                while (AcceptOp("="))
                {
                    value = ParseTestListOrStar(allowYield: true);
                    if (IsOp("=")) assign.Targets.Add(value);
                }
                assign.Value = value;
                return assign;
            }
            return At(new ExprStmt { Value = first }, tok);
        }

        bool AtStatementEnd() => Cur.Kind == TokenKind.Newline || Cur.Kind == TokenKind.EndOfFile || IsOp(";");

        string ParseDottedName()
        {
            var sb = new StringBuilder(ExpectName());
            while (IsOp(".") && Peek().Kind == TokenKind.Name) { _i++; sb.Append('.').Append(ExpectName()); }
            return sb.ToString();
        }

        List<Stmt> ParseBlock()
        {
            ExpectOp(":");
            var body = new List<Stmt>();
            if (Cur.Kind != TokenKind.Newline)
            {
                body.AddRange(ParseSimpleStatements());
                return body;
            }
            _i++;
            if (Cur.Kind != TokenKind.Indent) throw Err("expected an indented block");
            _i++;
            while (Cur.Kind != TokenKind.Dedent && Cur.Kind != TokenKind.EndOfFile)
            {
                if (Cur.Kind == TokenKind.Newline) { _i++; continue; }
                body.AddRange(ParseStatement());
            }
            if (Cur.Kind == TokenKind.Dedent) _i++;
            return body;
        }

        Stmt ParseDecorated()
        {
            var decorators = new List<Expr>();
            while (AcceptOp("@"))
            {
                decorators.Add(ParseTest());
                ExpectNewline();
            }
            if (IsKw("def")) return ParseFuncDef(decorators, false);
            if (IsKw("async")) { _i++; return ParseFuncDef(decorators, true); }
            if (IsKw("class")) return ParseClassDef(decorators);
            throw Err("expected 'def' or 'class' after decorator");
        }

        List<TypeParam> ParseTypeParams()
        {
            var list = new List<TypeParam>();
            if (!AcceptOp("[")) return list;
            do
            {
                if (IsOp("]")) break;
                var tp = new TypeParam { Name = ExpectName() };
                if (AcceptOp(":")) tp.Bound = ParseTest();
                list.Add(tp);
            } while (AcceptOp(","));
            ExpectOp("]");
            return list;
        }

        FunctionDef ParseFuncDef(List<Expr> decorators, bool isAsync)
        {
            var tok = Cur;
            ExpectKw("def");
            var f = At(new FunctionDef { Name = ExpectName(), Decorators = decorators, IsAsync = isAsync }, tok);
            f.TypeParams = ParseTypeParams();
            ExpectOp("(");
            while (!IsOp(")"))
            {
                var ptok = Cur;
                var p = At(new Param(), ptok);
                if (AcceptOp("**")) p.Kind = ParamKind.KwArgs;
                else if (AcceptOp("*"))
                {
                    p.Kind = ParamKind.VarArgs;
                    if (IsOp(",")) { _i++; continue; } // bare '*' (keyword-only marker) — ignored
                }
                else if (AcceptOp("/")) { if (!IsOp(")")) ExpectOp(","); continue; }
                p.Name = ExpectName();
                if (AcceptOp(":")) p.Annotation = ParseTest();
                if (AcceptOp("=")) p.Default = ParseTest();
                f.Params.Add(p);
                if (!AcceptOp(",")) break;
            }
            ExpectOp(")");
            if (AcceptOp("->")) f.Returns = ParseTest();
            f.Body = ParseBlock();
            return f;
        }

        ClassDef ParseClassDef(List<Expr> decorators)
        {
            var tok = Cur;
            ExpectKw("class");
            var c = At(new ClassDef { Name = ExpectName(), Decorators = decorators }, tok);
            c.TypeParams = ParseTypeParams();
            if (AcceptOp("("))
            {
                while (!IsOp(")"))
                {
                    if (Cur.Kind == TokenKind.Name && Peek().Is("=")) { _i += 2; ParseTest(); } // metaclass=..., ignored
                    else c.Bases.Add(ParseTest());
                    if (!AcceptOp(",")) break;
                }
                ExpectOp(")");
            }
            c.Body = ParseBlock();
            return c;
        }

        IfStmt ParseIf()
        {
            var tok = Cur;
            _i++; // 'if' or 'elif'
            var s = At(new IfStmt { Test = ParseNamedExprTest() }, tok);
            s.Body = ParseBlock();
            if (IsKw("elif")) s.OrElse.Add(ParseIf());
            else if (AcceptKw("else")) s.OrElse = ParseBlock();
            return s;
        }

        WhileStmt ParseWhile()
        {
            var tok = Cur;
            ExpectKw("while");
            var s = At(new WhileStmt { Test = ParseNamedExprTest() }, tok);
            s.Body = ParseBlock();
            if (AcceptKw("else")) s.OrElse = ParseBlock();
            return s;
        }

        ForStmt ParseFor(bool isAsync)
        {
            var tok = Cur;
            ExpectKw("for");
            var s = At(new ForStmt { IsAsync = isAsync }, tok);
            s.Target = ParseTargetList();
            ExpectKw("in");
            s.Iter = ParseTestListOrStar();
            s.Body = ParseBlock();
            if (AcceptKw("else")) s.OrElse = ParseBlock();
            return s;
        }

        Expr ParseTargetList()
        {
            var tok = Cur;
            var first = ParseOrExpr();
            if (!IsOp(",")) return first;
            var tuple = At(new TupleExpr(), tok);
            tuple.Elts.Add(first);
            while (AcceptOp(","))
            {
                if (IsKw("in") || IsOp("=")) break;
                tuple.Elts.Add(ParseOrExpr());
            }
            return tuple;
        }

        TryStmt ParseTry()
        {
            var tok = Cur;
            ExpectKw("try");
            var s = At(new TryStmt(), tok);
            s.Body = ParseBlock();
            while (IsKw("except"))
            {
                var htok = Cur;
                _i++;
                var h = At(new ExceptHandler(), htok);
                if (!IsOp(":"))
                {
                    var typeTok = Cur;
                    h.Type = ParseTest();
                    if (IsOp(","))
                    {
                        // Python 3.14 (PEP 758): `except A, B:` catches A or B, like `except (A, B):`
                        var types = At(new TupleExpr(), typeTok);
                        types.Elts.Add(h.Type);
                        while (AcceptOp(","))
                        {
                            if (IsOp(":") || IsKw("as")) break;
                            types.Elts.Add(ParseTest());
                        }
                        h.Type = types;
                    }
                    if (AcceptKw("as")) h.Name = ExpectName();
                }
                h.Body = ParseBlock();
                s.Handlers.Add(h);
            }
            if (AcceptKw("else")) s.OrElse = ParseBlock();
            if (AcceptKw("finally")) s.FinalBody = ParseBlock();
            return s;
        }

        WithStmt ParseWith(bool isAsync)
        {
            var tok = Cur;
            ExpectKw("with");
            var s = At(new WithStmt { IsAsync = isAsync }, tok);
            do
            {
                var item = new WithItem { Context = ParseTest() };
                if (AcceptKw("as")) item.Var = ParseOrExpr();
                s.Items.Add(item);
            } while (AcceptOp(","));
            s.Body = ParseBlock();
            return s;
        }

        // ───────────────────────────── expressions ─────────────────────────────

        /// <summary>namedexpr_test: an assignment expression `name := value`, or a test.</summary>
        Expr ParseNamedExprTest()
        {
            var tok = Cur;
            if (Cur.Kind == TokenKind.Name && !Keywords.Contains(Cur.Text) && Peek().Kind == TokenKind.Op && Peek().Text == ":=")
            {
                var target = At(new NameExpr { Id = ExpectName() }, tok);
                _i++; // ':='
                return At(new NamedExpr { Target = target, Value = ParseTest() }, tok);
            }
            var e = ParseTest();
            if (IsOp(":=")) throw Err("the target of ':=' must be a name");
            return e;
        }

        /// <summary>star_named_expression: `*x` or a named expression (list/set displays, parenthesized tuples).</summary>
        Expr ParseNamedExprOrStar()
        {
            var tok = Cur;
            if (AcceptOp("*")) return At(new StarredExpr { Value = ParseOrExpr() }, tok);
            return ParseNamedExprTest();
        }

        /// <summary>testlist_star_expr: comma separated tests, producing a tuple when there is a comma.</summary>
        Expr ParseTestListOrStar(bool allowYield = false)
        {
            var tok = Cur;
            if (allowYield && IsKw("yield"))
            {
                _i++;
                var y = At(new YieldExpr(), tok);
                if (AcceptKw("from")) { y.From = true; y.Value = ParseTest(); }
                else if (!AtStatementEnd() && !IsOp(")")) y.Value = ParseTestListOrStar();
                return y;
            }
            var first = ParseTestOrStar();
            if (!IsOp(",")) return first;
            var tuple = At(new TupleExpr(), tok);
            tuple.Elts.Add(first);
            while (AcceptOp(","))
            {
                if (!StartsExpression()) break;
                tuple.Elts.Add(ParseTestOrStar());
            }
            return tuple;
        }

        Expr ParseTestOrStar()
        {
            var tok = Cur;
            if (AcceptOp("*")) return At(new StarredExpr { Value = ParseOrExpr() }, tok);
            return ParseTest();
        }

        bool StartsExpression()
        {
            var c = Cur;
            if (c.Kind == TokenKind.Name) return !(c.Text == "in" || c.Text == "for" || c.Text == "if" || c.Text == "else" || c.Text == "and" || c.Text == "or" || c.Text == "as" || c.Text == "from" || c.Text == "import");
            if (c.Kind == TokenKind.Number || c.Kind == TokenKind.String) return true;
            if (c.Kind == TokenKind.Op) return c.Text == "(" || c.Text == "[" || c.Text == "{" || c.Text == "-" || c.Text == "+" || c.Text == "~" || c.Text == "*" || c.Text == "...";
            return false;
        }

        Expr ParseTest()
        {
            var tok = Cur;
            if (IsKw("lambda")) return ParseLambda();
            var e = ParseOrTest();
            if (IsKw("if") )
            {
                _i++;
                var test = ParseOrTest();
                ExpectKw("else");
                var orElse = ParseTest();
                return At(new IfExpr { Test = test, Body = e, OrElse = orElse }, tok);
            }
            return e;
        }

        Expr ParseLambda()
        {
            var tok = Cur;
            ExpectKw("lambda");
            var l = At(new LambdaExpr(), tok);
            while (!IsOp(":"))
            {
                l.Params.Add(ExpectName());
                if (IsOp("=")) throw Err("default values for lambda parameters are not supported in Typhon");
                if (!AcceptOp(",")) break;
            }
            ExpectOp(":");
            l.Body = ParseTest();
            return l;
        }

        Expr ParseOrTest()
        {
            var tok = Cur;
            var e = ParseAndTest();
            if (!IsKw("or")) return e;
            var b = At(new BoolOpExpr { Op = "or" }, tok);
            b.Values.Add(e);
            while (AcceptKw("or")) b.Values.Add(ParseAndTest());
            return b;
        }

        Expr ParseAndTest()
        {
            var tok = Cur;
            var e = ParseNotTest();
            if (!IsKw("and")) return e;
            var b = At(new BoolOpExpr { Op = "and" }, tok);
            b.Values.Add(e);
            while (AcceptKw("and")) b.Values.Add(ParseNotTest());
            return b;
        }

        Expr ParseNotTest()
        {
            var tok = Cur;
            if (AcceptKw("not")) return At(new UnaryExpr { Op = "not", Operand = ParseNotTest() }, tok);
            return ParseComparison();
        }

        Expr ParseComparison()
        {
            var tok = Cur;
            var left = ParseOrExpr();
            CompareExpr cmp = null;
            while (true)
            {
                string op = null;
                if (Cur.Kind == TokenKind.Op && (Cur.Text == "<" || Cur.Text == ">" || Cur.Text == "==" || Cur.Text == ">=" || Cur.Text == "<=" || Cur.Text == "!="))
                { op = Cur.Text; _i++; }
                else if (IsKw("in")) { op = "in"; _i++; }
                else if (IsKw("not") && Peek().Is("in")) { op = "not in"; _i += 2; }
                else if (IsKw("is"))
                {
                    _i++;
                    op = AcceptKw("not") ? "is not" : "is";
                }
                if (op == null) break;
                if (cmp == null) cmp = At(new CompareExpr { Left = left }, tok);
                cmp.Ops.Add(op);
                cmp.Comparators.Add(ParseOrExpr());
            }
            return (Expr)cmp ?? left;
        }

        Expr ParseOrExpr() => ParseBinary(0);

        static readonly string[][] BinaryLevels =
        {
            new[] { "|" },
            new[] { "^" },
            new[] { "&" },
            new[] { "<<", ">>" },
            new[] { "+", "-" },
            new[] { "*", "/", "//", "%", "@" },
        };

        Expr ParseBinary(int level)
        {
            if (level >= BinaryLevels.Length) return ParseFactor();
            var tok = Cur;
            var left = ParseBinary(level + 1);
            while (Cur.Kind == TokenKind.Op && System.Array.IndexOf(BinaryLevels[level], Cur.Text) >= 0)
            {
                var opTok = Cur;
                _i++;
                var right = ParseBinary(level + 1);
                left = At(new BinOpExpr { Op = opTok.Text, Left = left, Right = right }, tok);
            }
            return left;
        }

        Expr ParseFactor()
        {
            var tok = Cur;
            if (Cur.Kind == TokenKind.Op && (Cur.Text == "-" || Cur.Text == "+" || Cur.Text == "~"))
            {
                _i++;
                return At(new UnaryExpr { Op = tok.Text, Operand = ParseFactor() }, tok);
            }
            return ParsePower();
        }

        Expr ParsePower()
        {
            var tok = Cur;
            Expr e;
            if (AcceptKw("await")) e = At(new AwaitExpr { Value = ParsePrimary() }, tok);
            else e = ParsePrimary();
            if (AcceptOp("**")) return At(new BinOpExpr { Op = "**", Left = e, Right = ParseFactor() }, tok);
            return e;
        }

        Expr ParsePrimary()
        {
            var e = ParseAtom();
            while (true)
            {
                var tok = Cur;
                if (AcceptOp("."))
                {
                    e = At(new AttributeExpr { Target = e, Name = ExpectNameOrKeyword() }, tok);
                }
                else if (IsOp("("))
                {
                    _i++;
                    var call = At(new CallExpr { Func = e }, tok);
                    call.Line = e.Line; call.Col = e.Col;
                    ParseCallArgs(call);
                    e = call;
                }
                else if (IsOp("["))
                {
                    _i++;
                    var sub = At(new SubscriptExpr { Target = e }, tok);
                    sub.Line = e.Line; sub.Col = e.Col;
                    sub.Index = ParseSubscriptList();
                    ExpectOp("]");
                    e = sub;
                }
                else return e;
            }
        }

        string ExpectNameOrKeyword()
        {
            if (Cur.Kind != TokenKind.Name) throw Err("expected an attribute name but found " + Describe(Cur));
            return _t[_i++].Text;
        }

        void ParseCallArgs(CallExpr call)
        {
            while (!IsOp(")"))
            {
                var arg = new Arg();
                if (AcceptOp("**")) { arg.DoubleStar = true; arg.Value = ParseTest(); }
                else if (AcceptOp("*")) { arg.Star = true; arg.Value = ParseTest(); }
                else if (Cur.Kind == TokenKind.Name && Peek().Is("=") && Peek().Kind == TokenKind.Op)
                {
                    arg.Name = Cur.Text; _i += 2; arg.Value = ParseTest();
                }
                else
                {
                    var tok = Cur;
                    var v = ParseNamedExprTest();
                    if (IsKw("for") || IsKw("async"))
                        v = ParseComprehensionTail(ComprehensionKind.Generator, v, null, tok);
                    arg.Value = v;
                }
                call.Args.Add(arg);
                if (!AcceptOp(",")) break;
            }
            ExpectOp(")");
        }

        Expr ParseSubscriptList()
        {
            var tok = Cur;
            var first = ParseSubscript();
            if (!IsOp(",")) return first;
            var tuple = At(new TupleExpr(), tok);
            tuple.Elts.Add(first);
            while (AcceptOp(","))
            {
                if (IsOp("]")) break;
                tuple.Elts.Add(ParseSubscript());
            }
            return tuple;
        }

        Expr ParseSubscript()
        {
            var tok = Cur;
            Expr lower = null;
            if (!IsOp(":")) { lower = ParseTest(); if (!IsOp(":")) return lower; }
            ExpectOp(":");
            var s = At(new SliceExpr { Lower = lower }, tok);
            if (!IsOp(":") && !IsOp("]") && !IsOp(",")) s.Upper = ParseTest();
            if (AcceptOp(":"))
            {
                if (!IsOp("]") && !IsOp(",")) s.Step = ParseTest();
            }
            return s;
        }

        Expr ParseAtom()
        {
            var tok = Cur;
            switch (tok.Kind)
            {
                case TokenKind.Number:
                    _i++;
                    bool isFloat = !tok.Text.StartsWith("0x") && !tok.Text.StartsWith("0X") &&
                                   (tok.Text.Contains(".") || tok.Text.Contains("e") || tok.Text.Contains("E"));
                    return At(new ConstExpr { Kind = isFloat ? ConstKind.Float : ConstKind.Int, Raw = tok.Text }, tok);
                case TokenKind.String:
                    return ParseStrings();
                case TokenKind.Name:
                    if (tok.Text == "None") { _i++; return At(new ConstExpr { Kind = ConstKind.None, Raw = "None" }, tok); }
                    if (tok.Text == "True" || tok.Text == "False") { _i++; return At(new ConstExpr { Kind = ConstKind.Bool, Raw = tok.Text }, tok); }
                    if (Keywords.Contains(tok.Text) && tok.Text != "await") throw Err("unexpected keyword '" + tok.Text + "'");
                    _i++;
                    return At(new NameExpr { Id = tok.Text }, tok);
                case TokenKind.Op:
                    if (tok.Text == "...") { _i++; return At(new ConstExpr { Kind = ConstKind.Ellipsis, Raw = "..." }, tok); }
                    if (tok.Text == "(") return ParseParenAtom();
                    if (tok.Text == "[") return ParseListAtom();
                    if (tok.Text == "{") return ParseBraceAtom();
                    break;
            }
            throw Err("unexpected " + Describe(tok));
        }

        Expr ParseParenAtom()
        {
            var tok = ExpectOp("(");
            if (AcceptOp(")")) return At(new TupleExpr { Parenthesized = true }, tok);
            if (IsKw("yield"))
            {
                var y = ParseTestListOrStar(allowYield: true);
                ExpectOp(")");
                return y;
            }
            var first = ParseNamedExprOrStar();
            if (IsKw("for") || IsKw("async"))
            {
                var g = ParseComprehensionTail(ComprehensionKind.Generator, first, null, tok);
                ExpectOp(")");
                return g;
            }
            if (AcceptOp(")")) return first; // grouping
            var tuple = At(new TupleExpr { Parenthesized = true }, tok);
            tuple.Elts.Add(first);
            while (AcceptOp(","))
            {
                if (IsOp(")")) break;
                tuple.Elts.Add(ParseNamedExprOrStar());
            }
            ExpectOp(")");
            return tuple;
        }

        Expr ParseListAtom()
        {
            var tok = ExpectOp("[");
            var list = At(new ListExpr(), tok);
            if (AcceptOp("]")) return list;
            var first = ParseNamedExprOrStar();
            if (IsKw("for") || IsKw("async"))
            {
                var c = ParseComprehensionTail(ComprehensionKind.List, first, null, tok);
                ExpectOp("]");
                return c;
            }
            list.Elts.Add(first);
            while (AcceptOp(","))
            {
                if (IsOp("]")) break;
                list.Elts.Add(ParseNamedExprOrStar());
            }
            ExpectOp("]");
            return list;
        }

        Expr ParseBraceAtom()
        {
            var tok = ExpectOp("{");
            if (AcceptOp("}")) return At(new DictExpr(), tok);
            if (IsOp("**")) return ParseDictDisplay(tok, null, null);
            var first = ParseNamedExprOrStar();
            if (!(first is StarredExpr) && AcceptOp(":"))
            {
                var value = ParseTest();
                if (IsKw("for") || IsKw("async"))
                {
                    var c = ParseComprehensionTail(ComprehensionKind.Dict, first, value, tok);
                    ExpectOp("}");
                    return c;
                }
                return ParseDictDisplay(tok, first, value);
            }
            if (IsKw("for") || IsKw("async"))
            {
                var c = ParseComprehensionTail(ComprehensionKind.Set, first, null, tok);
                ExpectOp("}");
                return c;
            }
            var set = At(new SetExpr(), tok);
            set.Elts.Add(first);
            while (AcceptOp(","))
            {
                if (IsOp("}")) break;
                set.Elts.Add(ParseNamedExprOrStar());
            }
            ExpectOp("}");
            return set;
        }

        /// <summary>The rest of a dict display after its first item; `**mapping` items are stored with a null key.</summary>
        Expr ParseDictDisplay(Token tok, Expr firstKey, Expr firstValue)
        {
            var dict = At(new DictExpr(), tok);
            if (firstKey != null) { dict.Keys.Add(firstKey); dict.Values.Add(firstValue); }
            else { ExpectOp("**"); dict.Keys.Add(null); dict.Values.Add(ParseOrExpr()); }
            while (AcceptOp(","))
            {
                if (IsOp("}")) break;
                if (AcceptOp("**")) { dict.Keys.Add(null); dict.Values.Add(ParseOrExpr()); continue; }
                dict.Keys.Add(ParseTest());
                ExpectOp(":");
                dict.Values.Add(ParseTest());
            }
            ExpectOp("}");
            return dict;
        }

        // ───────────────────────────── match (PEP 634) ─────────────────────────────

        /// <summary>'match' is a soft keyword: a statement when the logical line ends with ':' and an indented 'case' follows.</summary>
        bool StartsMatchStatement()
        {
            if (Peek().Kind == TokenKind.Newline || (Peek().Kind == TokenKind.Op && (Peek().Text == "=" || Peek().Text == "." || Peek().Text.EndsWith("=") && Peek().Text != "==")))
                return false;
            int j = _i + 1;
            while (j < _t.Count && _t[j].Kind != TokenKind.Newline && _t[j].Kind != TokenKind.EndOfFile) j++;
            if (j + 2 >= _t.Count || j == _i + 1) return false;
            return _t[j - 1].Kind == TokenKind.Op && _t[j - 1].Text == ":" && _t[j + 1].Kind == TokenKind.Indent &&
                   _t[j + 2].Kind == TokenKind.Name && _t[j + 2].Text == "case";
        }

        MatchStmt ParseMatch()
        {
            var tok = Cur;
            _i++; // 'match'
            var m = At(new MatchStmt { Subject = ParseTestListOrStar() }, tok);
            ExpectOp(":");
            if (Cur.Kind != TokenKind.Newline) throw Err("expected a new line after 'match ...:'");
            _i++;
            if (Cur.Kind != TokenKind.Indent) throw Err("expected an indented block of 'case' clauses");
            _i++;
            while (Cur.Kind != TokenKind.Dedent && Cur.Kind != TokenKind.EndOfFile)
            {
                if (Cur.Kind == TokenKind.Newline) { _i++; continue; }
                if (!IsKw("case")) throw Err("expected 'case' but found " + Describe(Cur));
                var ctok = Cur;
                _i++;
                var c = At(new MatchCase { Pattern = ParseOpenSequencePattern() }, ctok);
                if (AcceptKw("if")) c.Guard = ParseNamedExprTest();
                c.Body = ParseBlock();
                m.Cases.Add(c);
            }
            if (Cur.Kind == TokenKind.Dedent) _i++;
            if (m.Cases.Count == 0) throw Err("a 'match' statement needs at least one 'case'");
            return m;
        }

        /// <summary>A '*name' item of a sequence pattern while it is being parsed.</summary>
        sealed class StarPattern : Pattern { public string Name; }

        /// <summary>case a, b, *rest: (a sequence pattern without brackets) or a single pattern.</summary>
        Pattern ParseOpenSequencePattern()
        {
            var tok = Cur;
            var first = ParseMaybeStarPattern();
            if (!IsOp(","))
            {
                if (first is StarPattern) throw Err("a star pattern must be inside a sequence pattern");
                return first;
            }
            var seq = At(new SequencePattern(), tok);
            AddSequenceItem(seq, first);
            while (AcceptOp(","))
            {
                if (IsOp(":") || IsKw("if")) break;
                AddSequenceItem(seq, ParseMaybeStarPattern());
            }
            return seq;
        }

        void AddSequenceItem(SequencePattern seq, Pattern p)
        {
            if (p is StarPattern star)
            {
                if (seq.StarIndex >= 0) throw new SyntaxError("multiple starred names in a sequence pattern", p.Line, p.Col);
                seq.StarIndex = seq.Items.Count;
                seq.StarName = star.Name;
            }
            else seq.Items.Add(p);
        }

        Pattern ParseMaybeStarPattern()
        {
            var tok = Cur;
            if (AcceptOp("*"))
            {
                string name = ExpectName();
                return At(new StarPattern { Name = name == "_" ? null : name }, tok);
            }
            return ParsePattern();
        }

        Pattern ParsePattern()
        {
            var tok = Cur;
            var p = ParseOrPattern();
            if (AcceptKw("as")) return At(new AsPattern { Inner = p, Name = ExpectName() }, tok);
            return p;
        }

        Pattern ParseOrPattern()
        {
            var tok = Cur;
            var first = ParseClosedPattern();
            if (!IsOp("|")) return first;
            var or = At(new OrPattern(), tok);
            or.Alternatives.Add(first);
            while (AcceptOp("|")) or.Alternatives.Add(ParseClosedPattern());
            return or;
        }

        Pattern ParseClosedPattern()
        {
            var tok = Cur;
            if (Cur.Kind == TokenKind.Number || Cur.Kind == TokenKind.String || IsKw("None") || IsKw("True") || IsKw("False"))
                return At(new ValuePattern { Value = ParseAtom() }, tok);
            if (IsOp("-") && Peek().Kind == TokenKind.Number)
            {
                _i++;
                return At(new ValuePattern { Value = At(new UnaryExpr { Op = "-", Operand = ParseAtom() }, tok) }, tok);
            }
            if (AcceptOp("("))
            {
                if (AcceptOp(")")) return At(new SequencePattern(), tok);
                var first = ParseMaybeStarPattern();
                if (AcceptOp(")"))
                {
                    if (first is StarPattern) { var one = At(new SequencePattern(), tok); AddSequenceItem(one, first); return one; }
                    return first; // group
                }
                var seq = At(new SequencePattern(), tok);
                AddSequenceItem(seq, first);
                while (AcceptOp(","))
                {
                    if (IsOp(")")) break;
                    AddSequenceItem(seq, ParseMaybeStarPattern());
                }
                ExpectOp(")");
                return seq;
            }
            if (AcceptOp("["))
            {
                var seq = At(new SequencePattern(), tok);
                while (!IsOp("]"))
                {
                    AddSequenceItem(seq, ParseMaybeStarPattern());
                    if (!AcceptOp(",")) break;
                }
                ExpectOp("]");
                return seq;
            }
            if (AcceptOp("{"))
            {
                var map = At(new MappingPattern(), tok);
                while (!IsOp("}"))
                {
                    if (AcceptOp("**")) { map.Rest = ExpectName(); AcceptOp(","); break; }
                    var keyTok = Cur;
                    Expr key = Cur.Kind == TokenKind.Name && !IsKw("None") && !IsKw("True") && !IsKw("False") ? ParseDottedValue() : ((ValuePattern)ParseClosedPattern()).Value;
                    ExpectOp(":");
                    map.Keys.Add(key);
                    map.Values.Add(ParsePattern());
                    if (!AcceptOp(",")) break;
                }
                ExpectOp("}");
                return map;
            }
            if (Cur.Kind == TokenKind.Name && !Keywords.Contains(Cur.Text))
            {
                var value = ParseDottedValue();
                if (AcceptOp("("))
                {
                    var cls = At(new ClassPattern { Cls = value }, tok);
                    while (!IsOp(")"))
                    {
                        if (Cur.Kind == TokenKind.Name && Peek().Kind == TokenKind.Op && Peek().Text == "=")
                        {
                            cls.KeywordNames.Add(Cur.Text);
                            _i += 2;
                            cls.KeywordPatterns.Add(ParsePattern());
                        }
                        else
                        {
                            if (cls.KeywordNames.Count > 0) throw Err("positional patterns follow keyword patterns");
                            cls.Positional.Add(ParsePattern());
                        }
                        if (!AcceptOp(",")) break;
                    }
                    ExpectOp(")");
                    return cls;
                }
                if (value is NameExpr n) return At(new CapturePattern { Name = n.Id == "_" ? null : n.Id }, tok);
                return At(new ValuePattern { Value = value }, tok); // a dotted name is a value (Color.RED)
            }
            throw Err("unsupported pattern starting with " + Describe(Cur));
        }

        Expr ParseDottedValue()
        {
            var tok = Cur;
            Expr e = At(new NameExpr { Id = ExpectName() }, tok);
            while (IsOp(".") && Peek().Kind == TokenKind.Name) { _i++; e = At(new AttributeExpr { Target = e, Name = ExpectNameOrKeyword() }, tok); }
            return e;
        }

        ComprehensionExpr ParseComprehensionTail(ComprehensionKind kind, Expr elt, Expr valueElt, Token tok)
        {
            var c = At(new ComprehensionExpr { Kind = kind, Elt = elt, ValueElt = valueElt }, tok);
            while (IsKw("for") || IsKw("async"))
            {
                var g = new CompFor();
                if (AcceptKw("async")) g.IsAsync = true;
                ExpectKw("for");
                g.Target = ParseTargetList();
                ExpectKw("in");
                g.Iter = ParseOrTest();
                while (IsKw("if"))
                {
                    _i++;
                    g.Ifs.Add(ParseOrTest());
                }
                c.Generators.Add(g);
            }
            return c;
        }

        Expr ParseStrings()
        {
            var tok = Cur;
            bool anyF = false, anyBytes = false;
            var pieces = new List<Token>();
            while (Cur.Kind == TokenKind.String)
            {
                if (Cur.Prefix.IndexOf('f') >= 0) anyF = true;
                if (Cur.Prefix.IndexOf('b') >= 0) anyBytes = true;
                pieces.Add(Cur);
                _i++;
            }
            if (anyBytes)
            {
                // b"..." (adjacent bytes literals concatenate; mixing them with str literals is an error, as in Python)
                if (pieces.Exists(x => x.Prefix.IndexOf('b') < 0)) throw new SyntaxError("cannot mix bytes and nonbytes literals", tok.Line, tok.Col);
                var bytesText = new StringBuilder();
                foreach (var p in pieces) bytesText.Append(Lexer.Decode(p.Text, p.Prefix.IndexOf('r') >= 0));
                foreach (char ch in bytesText.ToString())
                    if (ch > 0xff) throw new SyntaxError("bytes can only contain ASCII literal characters", tok.Line, tok.Col);
                return At(new ConstExpr { Kind = ConstKind.Bytes, Str = bytesText.ToString() }, tok);
            }
            if (!anyF)
            {
                var sb = new StringBuilder();
                foreach (var p in pieces) sb.Append(Lexer.Decode(p.Text, p.Prefix.IndexOf('r') >= 0));
                return At(new ConstExpr { Kind = ConstKind.Str, Str = sb.ToString() }, tok);
            }
            var f = At(new FStringExpr(), tok);
            foreach (var p in pieces)
            {
                bool raw = p.Prefix.IndexOf('r') >= 0;
                if (p.Prefix.IndexOf('f') < 0) { f.Parts.Add(new FStringPart { Literal = Lexer.Decode(p.Text, raw) }); continue; }
                ParseFStringBody(p, raw, f.Parts);
            }
            return f;
        }

        static void ParseFStringBody(Token tok, bool raw, List<FStringPart> parts)
        {
            string s = tok.Text;
            var lit = new StringBuilder();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '{' && i + 1 < s.Length && s[i + 1] == '{') { lit.Append('{'); i += 2; continue; }
                if (c == '}' && i + 1 < s.Length && s[i + 1] == '}') { lit.Append('}'); i += 2; continue; }
                if (c != '{') { lit.Append(c); i++; continue; }

                if (lit.Length > 0) { parts.Add(new FStringPart { Literal = Lexer.Decode(lit.ToString(), raw) }); lit.Clear(); }
                // find the end of the hole, tracking nesting and quotes
                int depth = 0; int j = i + 1; char quote = '\0';
                int exprEnd = -1, convPos = -1, specPos = -1;
                for (; j < s.Length; j++)
                {
                    char d = s[j];
                    if (quote != '\0') { if (d == quote) quote = '\0'; continue; }
                    if (d == '\'' || d == '"') { quote = d; continue; }
                    if (d == '(' || d == '[' || d == '{') { depth++; continue; }
                    if ((d == ')' || d == ']' || d == '}') && depth > 0) { depth--; continue; }
                    if (depth == 0)
                    {
                        if (d == '}') break;
                        if (d == '!' && specPos < 0 && convPos < 0 && j + 1 < s.Length && s[j + 1] != '=') { convPos = j; if (exprEnd < 0) exprEnd = j; }
                        else if (d == ':' && specPos < 0) { specPos = j; if (exprEnd < 0) exprEnd = j; }
                    }
                }
                if (j >= s.Length) throw new SyntaxError("f-string: expecting '}'", tok.Line, tok.Col);
                if (exprEnd < 0) exprEnd = j;
                string exprText = s.Substring(i + 1, exprEnd - i - 1);
                var part = new FStringPart();
                string trimmed = exprText.TrimEnd();
                bool selfDocumenting = trimmed.EndsWith("=") && !(trimmed.Length >= 2 && "=!<>".IndexOf(trimmed[trimmed.Length - 2]) >= 0);
                if (selfDocumenting)
                {
                    // f"{expr = }" prints the expression text as written, then the value: repr() unless a conversion or spec is given
                    parts.Add(new FStringPart { Literal = exprText });
                    exprText = trimmed.Substring(0, trimmed.Length - 1);
                }
                part.Expr = ParseExpression(exprText, tok.Line, tok.Col);
                if (convPos >= 0) part.Conversion = s[convPos + 1];
                if (specPos >= 0) part.FormatSpec = s.Substring(specPos + 1, j - specPos - 1);
                if (selfDocumenting && convPos < 0 && specPos < 0) part.Conversion = 'r';
                parts.Add(part);
                i = j + 1;
            }
            if (lit.Length > 0) parts.Add(new FStringPart { Literal = Lexer.Decode(lit.ToString(), raw) });
        }
    }
}
