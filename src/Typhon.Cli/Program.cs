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
//   typhon build <file.ty> --aot       Native AOT: a self-contained native executable (needs the C++ build tools)
// Options: --strict | --permissive (type mode), --time (print compile/run timings to stderr)

var argList = args.ToList();
TypeMode mode = TypeMode.Warn;
bool timing = false;
if (argList.Remove("--strict")) mode = TypeMode.Strict;
if (argList.Remove("--permissive")) mode = TypeMode.Permissive;
if (argList.Remove("--time")) timing = true;
bool aot = argList.Remove("--aot");
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

if (command == "build" && aot)
    return BuildNativeAot(compilation, trees, entryPath, outDir);

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

// Native AOT: check that nothing is late-bound, then let the .NET SDK's ILCompiler produce a native executable.
static int BuildNativeAot(CSharpCompilation compilation, List<SyntaxTree> trees, string entryPath, string? outDir)
{
    // 1. Native AOT cannot execute `dynamic` (the runtime binder generates code at run time). Strict mode removes the
    //    compiler's own fallbacks to dynamic, but libraries can still hand out dynamic values, so check every expression.
    var sites = new SortedSet<(string Path, int Line)>();
    foreach (var tree in trees)
    {
        var model = compilation.GetSemanticModel(tree);
        foreach (var expr in tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax>())
        {
            if (model.GetTypeInfo(expr).Type?.TypeKind != TypeKind.Dynamic) continue;
            var span = expr.GetLocation().GetMappedLineSpan();
            sites.Add((span.Path, span.StartLinePosition.Line + 1));
        }
    }
    if (sites.Count > 0)
    {
        foreach (var (path, line) in sites)
            Console.Error.WriteLine($"{path}({line}): error TY2001: a dynamic (late-bound) value is used here; Native AOT cannot run dynamic code. Annotate the value with a concrete type or avoid APIs that return dynamic.");
        return 1;
    }

    // 2. A throwaway project holding the generated C# + the Typhon runtime, published with PublishAot=true.
    string asmName = compilation.AssemblyName!;
    outDir ??= Path.Combine(Path.GetDirectoryName(entryPath)!, "bin", Path.GetFileNameWithoutExtension(entryPath) + "-aot");
    string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entryPath)))[..10];
    string work = Path.Combine(Path.GetTempPath(), "typhon-aot", asmName + "-" + hash);
    if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
    Directory.CreateDirectory(work);
    File.WriteAllText(Path.Combine(work, "Typhon.Runtime.g.cs"), TyphonCompiler.RuntimeSource);
    foreach (var tree in trees)
        File.WriteAllText(Path.Combine(work, Path.GetFileName(tree.FilePath)), tree.GetText().ToString());

    var v = Environment.Version;
    var libs = Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
        .Where(p => Path.GetFileName(p) is var n && ((n.StartsWith("Typhon.") && !n.StartsWith("Typhon.Compiler")) || n == "System.Numerics.Tensors.dll"))
        .Select(p => $"""    <Reference Include="{Path.GetFileNameWithoutExtension(p)}" HintPath="{p}" />""");
    File.WriteAllText(Path.Combine(work, asmName + ".csproj"), $"""
        <Project>
          <!-- Generated by typhon build (Native AOT). Isolated from any Directory.Build.props of the source tree. -->
          <PropertyGroup>
            <ImportDirectoryBuildProps>false</ImportDirectoryBuildProps>
            <ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets>
          </PropertyGroup>
          <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net{v.Major}.{v.Minor}</TargetFramework>
            <AssemblyName>{asmName}</AssemblyName>
            <PublishAot>true</PublishAot>
            <OptimizationPreference>Speed</OptimizationPreference>
            <InvariantGlobalization>true</InvariantGlobalization>
            <LangVersion>preview</LangVersion>
            <Nullable>disable</Nullable>
            <ImplicitUsings>disable</ImplicitUsings>
            <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
            <!-- the runtime's dynamic fallbacks are unreachable once the program passed the check above -->
            <NoWarn>$(NoWarn);CS0162;CS0168;CS0219;CS8321;CS1998;CS0105;CS0659;CS0660;CS0661;CS8981;IL2026;IL2057;IL2070;IL2072;IL2075;IL2104;IL3050;IL3053</NoWarn>
          </PropertyGroup>
          <ItemGroup>
        {string.Join("\n", libs)}
          </ItemGroup>
          <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
        </Project>
        """);

    string rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
    var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in new[] { "publish", Path.Combine(work, asmName + ".csproj"), "-c", "Release", "-r", rid, "-o", outDir, "--nologo", "-v", "q" })
        psi.ArgumentList.Add(a);
    var sw = Stopwatch.StartNew();
    using var proc = Process.Start(psi)!;
    string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
    proc.WaitForExit();
    if (proc.ExitCode != 0)
    {
        Console.Error.WriteLine(output);
        if (output.Contains("link.exe") || output.Contains("vswhere") || output.Contains("Desktop development with C++"))
            Console.Error.WriteLine("typhon: Native AOT needs the C++ build tools (Visual Studio 'Desktop development with C++').");
        return 1;
    }
    string exe = Path.Combine(outDir, asmName + (OperatingSystem.IsWindows() ? ".exe" : ""));
    Console.Error.WriteLine($"[typhon] native executable {exe} ({new FileInfo(exe).Length / 1048576.0:F1} MB, {sw.Elapsed.TotalSeconds:F0} s)");
    return 0;
}

static bool PrintTyDiagnostics(List<TyDiagnostic> diags, bool errorsOnly)
{
    foreach (var d in diags.Where(d => !errorsOnly || d.Severity == Severity.Error))
        Console.Error.WriteLine(d.ToString());
    return diags.Any(d => d.Severity == Severity.Error);
}
