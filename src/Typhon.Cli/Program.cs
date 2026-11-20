// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Typhon.Compiler;
using Typhon.Compiler.Semantics;

// typhon — command line front end for Typhon.
//   typhon run   <file.ty> [args...]   compile in memory and run (like `python file.py`)
//   typhon emit  <file.ty>             print the generated C#
//   typhon check <file.ty>             report diagnostics only
//   typhon build <file.ty> [-o dir]    compile to disk: dir/<name>.dll, run it with `dotnet dir/<name>.dll`
// Options: --strict | --permissive (type mode), --time (print compile/run timings to stderr)

var argList = args.ToList();
TypeMode mode = TypeMode.Warn;
bool timing = false;
if (argList.Remove("--strict")) mode = TypeMode.Strict;
if (argList.Remove("--permissive")) mode = TypeMode.Permissive;
if (argList.Remove("--time")) timing = true;
string? outDir = null;
int oIndex = argList.IndexOf("-o");
if (oIndex >= 0 && oIndex + 1 < argList.Count) { outDir = Path.GetFullPath(argList[oIndex + 1]); argList.RemoveRange(oIndex, 2); }

if (argList.Count < 2 || argList[0] is not ("run" or "emit" or "check" or "build"))
{
    Console.Error.WriteLine("usage: typhon (run|emit|check|build) <file.ty> [args...] [-o dir] [--strict|--permissive] [--time]");
    return 2;
}

string command = argList[0];
string entryPath = Path.GetFullPath(argList[1]);
string[] programArgs = argList.Skip(2).ToArray();
if (!File.Exists(entryPath)) { Console.Error.WriteLine($"typhon: can't open file '{entryPath}'"); return 2; }

var sw = Stopwatch.StartNew();

// References: the running .NET runtime's assemblies + any Typhon.* libraries next to the CLI (e.g. Typhon.NumPy).
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
    .Split(Path.PathSeparator)
    .Where(p => p.EndsWith(".dll"))
    .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
    .ToList();
foreach (var lib in Directory.GetFiles(AppContext.BaseDirectory, "Typhon.*.dll").Where(p => !p.Contains("Typhon.Compiler")))
    references.Add(MetadataReference.CreateFromFile(lib));

var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
var runtimeTree = CSharpSyntaxTree.ParseText(TyphonCompiler.RuntimeSource, parseOptions, path: "Typhon.Runtime.g.cs", encoding: System.Text.Encoding.UTF8);
var compilation = CSharpCompilation.Create(
    "typhon_" + Path.GetFileNameWithoutExtension(entryPath),
    new[] { runtimeTree },
    references,
    new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Release, allowUnsafe: true,
        nullableContextOptions: NullableContextOptions.Disable));

// The entry file is the __main__ module; sibling .ty files it imports (transitively) are compiled with it.
var project = new ProjectContext { TypeMode = mode, Resolver = new RoslynTypeResolver(compilation), MainModule = "__main__" };
var dir = Path.GetDirectoryName(entryPath)!;
var modules = new List<ModuleInfo>();
var pending = new Queue<(string Path, string Name)>();
pending.Enqueue((entryPath, "__main__"));
var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entryPath };
while (pending.Count > 0)
{
    var (path, name) = pending.Dequeue();
    var m = TyphonCompiler.Parse(path, File.ReadAllText(path), name, "");
    project.Modules[name] = m;
    modules.Add(m);
    foreach (var imported in ImportedModuleNames(m))
    {
        string candidate = Path.Combine(dir, imported.Replace('.', Path.DirectorySeparatorChar) + TyphonCompiler.FileExtension);
        if (File.Exists(candidate) && seen.Add(Path.GetFullPath(candidate))) pending.Enqueue((Path.GetFullPath(candidate), imported));
    }
}

static IEnumerable<string> ImportedModuleNames(ModuleInfo m)
{
    var names = new List<string>();
    void Visit(IEnumerable<Typhon.Compiler.Syntax.Stmt> body)
    {
        foreach (var s in body)
        {
            if (s is Typhon.Compiler.Syntax.ImportStmt i) names.AddRange(i.Names.Select(n => n.Name));
            else if (s is Typhon.Compiler.Syntax.ImportFromStmt f && f.Level == 0) names.Add(f.Module);
            else if (s is Typhon.Compiler.Syntax.FunctionDef fd) Visit(fd.Body);
            else if (s is Typhon.Compiler.Syntax.ClassDef cd) Visit(cd.Body);
            else if (s is Typhon.Compiler.Syntax.IfStmt ifs) { Visit(ifs.Body); Visit(ifs.OrElse); }
        }
    }
    Visit(m.Ast.Body);
    return names;
}

var diagnostics = new List<TyDiagnostic>();
foreach (var m in modules) diagnostics.AddRange(m.ParseDiagnostics);
var trees = new List<SyntaxTree>();
string? entryCode = null;
foreach (var m in modules.Where(m => m.ParseDiagnostics.Count == 0))
{
    string code = TyphonCompiler.Emit(m, project, diagnostics);
    if (m.Name == "__main__") entryCode = code;
    trees.Add(CSharpSyntaxTree.ParseText(code, parseOptions, path: m.Path + ".g.cs", encoding: System.Text.Encoding.UTF8));
}

if (command == "emit")
{
    Console.WriteLine(entryCode);
    PrintTyDiagnostics(diagnostics, errorsOnly: false);
    return diagnostics.Any(d => d.Severity == Severity.Error) ? 1 : 0;
}

compilation = compilation.AddSyntaxTrees(trees);
using var pe = new MemoryStream();
using var pdb = new MemoryStream();
var result = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));

bool hasErrors = PrintTyDiagnostics(diagnostics, errorsOnly: false);
foreach (var d in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error || (d.Severity == DiagnosticSeverity.Warning && !d.Location.GetMappedLineSpan().Path.EndsWith(".g.cs"))))
{
    var span = d.Location.GetMappedLineSpan();
    Console.Error.WriteLine($"{span.Path}({span.StartLinePosition.Line + 1}): {d.Severity.ToString().ToLowerInvariant()} {d.Id}: {d.GetMessage()}");
    hasErrors |= d.Severity == DiagnosticSeverity.Error;
}
if (!result.Success || hasErrors) return 1;
if (timing) Console.Error.WriteLine($"[typhon] compiled in {sw.ElapsedMilliseconds} ms");
if (command == "check") return 0;

if (command == "build")
{
    // A framework-dependent app: <name>.dll + .pdb + .runtimeconfig.json + the Typhon libraries it may use.
    outDir ??= Path.Combine(Path.GetDirectoryName(entryPath)!, "bin", Path.GetFileNameWithoutExtension(entryPath));
    Directory.CreateDirectory(outDir);
    string asmName = compilation.AssemblyName!;
    string dllPath = Path.Combine(outDir, asmName + ".dll");
    File.WriteAllBytes(dllPath, pe.ToArray());
    File.WriteAllBytes(Path.Combine(outDir, asmName + ".pdb"), pdb.ToArray());
    var v = Environment.Version;
    File.WriteAllText(Path.Combine(outDir, asmName + ".runtimeconfig.json"), $$"""
        {
          "runtimeOptions": {
            "tfm": "net{{v.Major}}.{{v.Minor}}",
            "framework": { "name": "Microsoft.NETCore.App", "version": "{{v.Major}}.{{v.Minor}}.0" }
          }
        }
        """);
    foreach (var lib in Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
                 .Where(p => Path.GetFileName(p) is var n && ((n.StartsWith("Typhon.") && !n.StartsWith("Typhon.Compiler")) || n == "System.Numerics.Tensors.dll")))
        File.Copy(lib, Path.Combine(outDir, Path.GetFileName(lib)), overwrite: true);
    Console.Error.WriteLine($"[typhon] built {dllPath} — run it with: dotnet \"{dllPath}\"");
    return 0;
}

pe.Position = 0; pdb.Position = 0;
var asm = AssemblyLoadContext.Default.LoadFromStream(pe, pdb);
var entry = asm.EntryPoint;
if (entry == null) { Console.Error.WriteLine("typhon: no entry point (add top-level statements or an `if __name__ == \"__main__\":` block)"); return 1; }
sw.Restart();
try
{
    var ret = entry.Invoke(null, entry.GetParameters().Length == 0 ? null : new object[] { programArgs });
    if (ret is Task t) t.GetAwaiter().GetResult();
}
catch (TargetInvocationException tie) when (tie.InnerException != null)
{
    var ex = tie.InnerException;
    Console.Error.WriteLine("Traceback (most recent call last):");
    foreach (var frame in new StackTrace(ex, true).GetFrames().Reverse())
    {
        string? file = frame.GetFileName();
        if (file == null || !file.EndsWith(TyphonCompiler.FileExtension)) continue;
        var method = frame.GetMethod();
        string fn = method?.Name ?? "?";
        // async/iterator state machines: <Main>d__12.MoveNext → Main
        if (fn == "MoveNext" && method?.DeclaringType?.Name is string sm && sm.StartsWith('<')) fn = sm[1..sm.IndexOf('>')];
        if (fn == "Main") fn = "<module>";
        Console.Error.WriteLine($"  File \"{file}\", line {frame.GetFileLineNumber()}, in {fn}");
    }
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}
if (timing) Console.Error.WriteLine($"[typhon] ran in {sw.ElapsedMilliseconds} ms");
return Environment.ExitCode;

static bool PrintTyDiagnostics(List<TyDiagnostic> diags, bool errorsOnly)
{
    foreach (var d in diags.Where(d => !errorsOnly || d.Severity == Severity.Error))
        Console.Error.WriteLine(d.ToString());
    return diags.Any(d => d.Severity == Severity.Error);
}
