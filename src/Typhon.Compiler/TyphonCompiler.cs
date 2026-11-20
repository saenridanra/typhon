// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Typhon.Compiler.Emit;
using Typhon.Compiler.Semantics;
using Typhon.Compiler.Syntax;

namespace Typhon.Compiler
{
    /// <summary>How the compiler treats types it cannot determine statically (TypeScript-style noImplicitAny).</summary>
    public enum TypeMode
    {
        /// <summary>Implicit 'dynamic' is a compile error. Explicit 'Any' is still allowed. AOT/trimming friendly.</summary>
        Strict,
        /// <summary>Implicit 'dynamic' compiles, with a warning per occurrence (default).</summary>
        Warn,
        /// <summary>Implicit 'dynamic' compiles silently. For porting existing Python code.</summary>
        Permissive,
    }

    public enum Severity { Info, Warning, Error }

    public sealed class TyDiagnostic
    {
        public string Id;
        public Severity Severity;
        public string Message;
        public string Path;
        public int Line;
        public int Col;
        public override string ToString() => $"{Path}({Line},{Col}): {Severity.ToString().ToLowerInvariant()} {Id}: {Message}";
    }

    public sealed class ModuleInfo
    {
        public string Name;          // dotted python module name, e.g. "utils.strings"
        public string ClassName;     // C# static class name, e.g. "strings"
        public string Namespace;     // C# namespace, e.g. "MyApp.utils"
        public string Path;
        public ModuleAst Ast;
        public readonly Dictionary<string, ClassDef> Classes = new Dictionary<string, ClassDef>();
        public readonly HashSet<string> Functions = new HashSet<string>();
        public readonly HashSet<string> Globals = new HashSet<string>();
        public readonly List<TyDiagnostic> ParseDiagnostics = new List<TyDiagnostic>();

        public string QualifiedClassName => "global::" + (string.IsNullOrEmpty(Namespace) ? "" : Namespace + ".") + ClassName;
        public bool IsMainModule => Name == "__main__" || Name.EndsWith(".__main__");
    }

    public sealed class ProjectContext
    {
        public string RootNamespace = "";
        public TypeMode TypeMode = TypeMode.Warn;
        public ITypeResolver Resolver = NullTypeResolver.Instance;
        /// <summary>If set, only this module's `if __name__ == "__main__":` block becomes the program entry point.</summary>
        public string MainModule;
        public readonly Dictionary<string, ModuleInfo> Modules = new Dictionary<string, ModuleInfo>();

        public ModuleInfo FindModule(string dotted) => Modules.TryGetValue(dotted, out var m) ? m : null;

        /// <summary>Find a Typhon class by simple name across modules (used for base-class lookups).</summary>
        public bool TryFindClass(ModuleInfo module, string className, out ClassDef cls, out ModuleInfo owner)
        {
            owner = module;
            if (module.Classes.TryGetValue(className, out cls)) return true;
            foreach (var m in Modules.Values)
                if (m.Classes.TryGetValue(className, out cls)) { owner = m; return true; }
            cls = null; owner = null;
            return false;
        }
    }

    public static class TyphonCompiler
    {
        public const string FileExtension = ".ty";

        /// <summary>Module name from the path relative to the project directory: "utils/strings.ty" → "utils.strings".</summary>
        public static string ModuleNameFromPath(string projectDir, string path)
        {
            string rel = path;
            if (!string.IsNullOrEmpty(projectDir))
            {
                string full = System.IO.Path.GetFullPath(path);
                string dir = System.IO.Path.GetFullPath(projectDir).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
                if (full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) rel = full.Substring(dir.Length);
                else rel = System.IO.Path.GetFileName(path);
            }
            else rel = System.IO.Path.GetFileName(path);
            rel = rel.Substring(0, rel.Length - System.IO.Path.GetExtension(rel).Length);
            var parts = rel.Split('\\', '/').Where(p => p.Length > 0).Select(Sanitize);
            return string.Join(".", parts);
        }

        static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }

        public static ModuleInfo Parse(string path, string source, string moduleName, string rootNamespace)
        {
            var info = new ModuleInfo { Name = moduleName, Path = path };
            int lastDot = moduleName.LastIndexOf('.');
            info.ClassName = lastDot < 0 ? moduleName : moduleName.Substring(lastDot + 1);
            string pkg = lastDot < 0 ? "" : moduleName.Substring(0, lastDot);
            info.Namespace = string.Join(".", new[] { rootNamespace, pkg }.Where(s => !string.IsNullOrEmpty(s)));
            try
            {
                info.Ast = Parser.ParseModule(source, path);
            }
            catch (SyntaxError e)
            {
                info.ParseDiagnostics.Add(new TyDiagnostic { Id = "TY0001", Severity = Severity.Error, Message = e.Message, Path = path, Line = e.Line, Col = e.Col });
                info.Ast = new ModuleAst { Path = path };
            }
            foreach (var s in info.Ast.Body)
            {
                switch (s)
                {
                    case ClassDef c: info.Classes[c.Name] = c; break;
                    case FunctionDef f: info.Functions.Add(f.Name); break;
                    case AssignStmt a: foreach (var t in a.Targets) if (t is NameExpr n) info.Globals.Add(n.Id); break;
                    case AnnAssignStmt a: if (a.Target is NameExpr an) info.Globals.Add(an.Id); break;
                }
            }
            return info;
        }

        public static string Emit(ModuleInfo module, ProjectContext project, List<TyDiagnostic> diagnostics)
        {
            var emitter = new ModuleEmitter(module, project, diagnostics);
            return emitter.Emit();
        }

        /// <summary>C# source of the Typhon runtime (builtins + stdlib shims) that is compiled into every Typhon assembly.</summary>
        public static string RuntimeSource
        {
            get
            {
                var asm = typeof(TyphonCompiler).Assembly;
                using (var s = asm.GetManifestResourceStream("Typhon.Compiler.Runtime.Builtins.cs"))
                using (var r = new StreamReader(s)) return r.ReadToEnd();
            }
        }

        public static TypeMode ParseTypeMode(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "strict": return TypeMode.Strict;
                case "permissive": return TypeMode.Permissive;
                default: return TypeMode.Warn;
            }
        }
    }
}
