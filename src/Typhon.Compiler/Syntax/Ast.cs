// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;

namespace Typhon.Compiler.Syntax
{
    public abstract class Node
    {
        public int Line;
        public int Col;
    }

    // ───────────────────────────── Expressions ─────────────────────────────

    public abstract class Expr : Node { }

    public sealed class NameExpr : Expr { public string Id; }

    public enum ConstKind { Int, Float, Str, Bool, None, Ellipsis }

    public sealed class ConstExpr : Expr
    {
        public ConstKind Kind;
        public string Raw;    // numbers: source text; bool: "True"/"False"
        public string Str;    // decoded string value
    }

    public sealed class FStringPart
    {
        public string Literal;     // either a literal...
        public Expr Expr;          // ...or an interpolation hole
        public char Conversion;    // 'r', 's', 'a' or '\0'
        public string FormatSpec;  // null if none
    }

    public sealed class FStringExpr : Expr { public List<FStringPart> Parts = new List<FStringPart>(); }

    public sealed class AttributeExpr : Expr { public Expr Target; public string Name; }

    public sealed class Arg
    {
        public string Name;       // keyword name, or null
        public Expr Value;
        public bool Star;         // *args
        public bool DoubleStar;   // **kwargs
    }

    public sealed class CallExpr : Expr { public Expr Func; public List<Arg> Args = new List<Arg>(); }

    public sealed class SubscriptExpr : Expr { public Expr Target; public Expr Index; }

    public sealed class SliceExpr : Expr { public Expr Lower, Upper, Step; }

    public sealed class BinOpExpr : Expr { public string Op; public Expr Left, Right; }

    public sealed class UnaryExpr : Expr { public string Op; public Expr Operand; }

    public sealed class BoolOpExpr : Expr { public string Op; public List<Expr> Values = new List<Expr>(); }

    public sealed class CompareExpr : Expr
    {
        public Expr Left;
        public List<string> Ops = new List<string>();
        public List<Expr> Comparators = new List<Expr>();
    }

    public sealed class IfExpr : Expr { public Expr Test, Body, OrElse; }

    public sealed class LambdaExpr : Expr { public List<string> Params = new List<string>(); public Expr Body; }

    public sealed class TupleExpr : Expr { public List<Expr> Elts = new List<Expr>(); public bool Parenthesized; }

    public sealed class ListExpr : Expr { public List<Expr> Elts = new List<Expr>(); }

    public sealed class SetExpr : Expr { public List<Expr> Elts = new List<Expr>(); }

    public sealed class DictExpr : Expr { public List<Expr> Keys = new List<Expr>(); public List<Expr> Values = new List<Expr>(); }

    public enum ComprehensionKind { List, Generator, Set, Dict }

    public sealed class CompFor { public Expr Target; public Expr Iter; public List<Expr> Ifs = new List<Expr>(); public bool IsAsync; }

    public sealed class ComprehensionExpr : Expr
    {
        public ComprehensionKind Kind;
        public Expr Elt;        // for dicts: the key
        public Expr ValueElt;   // dicts only
        public List<CompFor> Generators = new List<CompFor>();
    }

    public sealed class AwaitExpr : Expr { public Expr Value; }

    public sealed class YieldExpr : Expr { public Expr Value; public bool From; }

    public sealed class StarredExpr : Expr { public Expr Value; }

    // ───────────────────────────── Statements ─────────────────────────────

    public abstract class Stmt : Node { }

    public sealed class ExprStmt : Stmt { public Expr Value; }

    public sealed class AssignStmt : Stmt { public List<Expr> Targets = new List<Expr>(); public Expr Value; }

    public sealed class AugAssignStmt : Stmt { public Expr Target; public string Op; public Expr Value; }

    public sealed class AnnAssignStmt : Stmt { public Expr Target; public Expr Annotation; public Expr Value; }

    public sealed class PassStmt : Stmt { }
    public sealed class BreakStmt : Stmt { }
    public sealed class ContinueStmt : Stmt { }

    public sealed class ReturnStmt : Stmt { public Expr Value; }

    public sealed class RaiseStmt : Stmt { public Expr Exc; public Expr Cause; }

    public sealed class GlobalStmt : Stmt { public List<string> Names = new List<string>(); public bool Nonlocal; }

    public sealed class AssertStmt : Stmt { public Expr Test; public Expr Msg; }

    public sealed class DelStmt : Stmt { public List<Expr> Targets = new List<Expr>(); }

    public sealed class ImportAlias { public string Name; public string AsName; }

    public sealed class ImportStmt : Stmt { public List<ImportAlias> Names = new List<ImportAlias>(); }

    public sealed class ImportFromStmt : Stmt
    {
        public string Module;   // dotted, may be empty for "from . import x"
        public int Level;       // number of leading dots
        public List<ImportAlias> Names = new List<ImportAlias>(); // Name "*" for wildcard
    }

    public sealed class IfStmt : Stmt { public Expr Test; public List<Stmt> Body = new List<Stmt>(); public List<Stmt> OrElse = new List<Stmt>(); }

    public sealed class WhileStmt : Stmt { public Expr Test; public List<Stmt> Body = new List<Stmt>(); public List<Stmt> OrElse = new List<Stmt>(); }

    public sealed class ForStmt : Stmt
    {
        public Expr Target; public Expr Iter; public bool IsAsync;
        public List<Stmt> Body = new List<Stmt>(); public List<Stmt> OrElse = new List<Stmt>();
    }

    public sealed class ExceptHandler : Node { public Expr Type; public string Name; public List<Stmt> Body = new List<Stmt>(); }

    public sealed class TryStmt : Stmt
    {
        public List<Stmt> Body = new List<Stmt>();
        public List<ExceptHandler> Handlers = new List<ExceptHandler>();
        public List<Stmt> OrElse = new List<Stmt>();
        public List<Stmt> FinalBody = new List<Stmt>();
    }

    public sealed class WithItem { public Expr Context; public Expr Var; }

    public sealed class WithStmt : Stmt { public List<WithItem> Items = new List<WithItem>(); public List<Stmt> Body = new List<Stmt>(); public bool IsAsync; }

    public enum ParamKind { Normal, VarArgs, KwArgs }

    public sealed class Param : Node { public string Name; public Expr Annotation; public Expr Default; public ParamKind Kind; }

    public sealed class TypeParam { public string Name; public Expr Bound; }

    public sealed class FunctionDef : Stmt
    {
        public string Name;
        public List<TypeParam> TypeParams = new List<TypeParam>();
        public List<Param> Params = new List<Param>();
        public Expr Returns;
        public List<Stmt> Body = new List<Stmt>();
        public List<Expr> Decorators = new List<Expr>();
        public bool IsAsync;
    }

    public sealed class ClassDef : Stmt
    {
        public string Name;
        public List<TypeParam> TypeParams = new List<TypeParam>();
        public List<Expr> Bases = new List<Expr>();
        public List<Stmt> Body = new List<Stmt>();
        public List<Expr> Decorators = new List<Expr>();
    }

    public sealed class ModuleAst
    {
        public string Path;
        public List<Stmt> Body = new List<Stmt>();
    }
}
