# Typhon — Python syntax, compiled to .NET IL

Typhon is a proof of concept for a **statically typed, Python-syntax language for .NET**. A `.ty` file is (in practice)
valid Python 3.12 source; the Typhon compiler turns it into ordinary IL, so Typhon code is a first-class .NET citizen:
C# can call it, subclass it and reference it like any other assembly, and Typhon can import any .NET namespace.

**The name:** Typhon comes from **ty**ped **Python** → **Ty**p**hon**. Type hints are what make it work: they let the
compiler produce fast, statically typed .NET code instead of interpreting Python's dynamic objects. The file extension
`.ty` follows from the same idea.

```python
from System.Text import StringBuilder          # import .NET like Python modules
from dataclasses import dataclass

@dataclass
class Point:                                    # → a real CLR class with typed properties
    x: float
    y: float

def total_length(points: list[Point]) -> float: # → static double total_length(List<Point> points)
    sb = StringBuilder()
    ...
```

It is **not** IronPython: IronPython keeps Python's dynamic semantics and therefore runs at roughly CPython speed.
Typhon requires/infers types and compiles to the same kind of IL C# produces, so tight loops run at C# speed.

## Project status: vibe-coded experiment

Typhon was fully "vibe-coded": an AI coding agent wrote the code as a proof of concept in a short amount of time, as part
of a raffle at the **Agentic Engineering Days 2026**. Its quality has not been measured or reviewed yet. Expect rough
edges, missing features and bugs, and no stability or support promises.

* **Model:** Claude Opus 5.5 (`claude-opus-5-5`) in Claude Code, including one sub-agent (same model) that wrote
  Typhon.NumPy; Claude Code used Claude Haiku 5.5 for small internal tasks.
* **Usage** (snapshot from Claude Code's usage screen about one hour into the session; the full session used more):
  Claude Opus 5.5 596.3k output tokens, 37.9M cached input tokens read, 729.5k cache-write tokens, 1.3k uncached
  input tokens; Claude Haiku 5.5 80.3k input / 5.0k output tokens. Estimated cost $24.47, 1 h 03 min wall-clock,
  11,061 lines added.

## Quick start (.NET 11 SDK)

```powershell
dotnet build Typhon.slnx                         # compiler, CLI, numpy library, samples

# 1. A pure Typhon console app — a normal .csproj whose sources are *.ty
dotnet run --project samples/HelloTyphon

# 2. A C# app using a Typhon class library *and* mixing .cs/.ty in one project
dotnet run --project samples/CSharpApp

# 3. Python-style scripting: compile in memory and run
dotnet run --project src/Typhon.Cli -c Release -- run samples/scripts/tour.ty
dotnet run --project src/Typhon.Cli -c Release -- emit samples/scripts/tour.ty   # show the generated C#
dotnet run --project src/Typhon.Cli -c Release -- build samples/scripts/tour.ty -o out/tour   # → dotnet out/tour/typhon_tour.dll
dotnet run --project src/Typhon.Cli -c Release -- build samples/scripts/tour.ty --aot -o out/tour-aot   # → native out/tour-aot/typhon_tour.exe

# 4. Benchmarks: the same files under CPython (+ NumPy) and Typhon, time and peak RAM
python -m venv .venv; .venv\Scripts\pip install numpy     # once, for the CPython side
./benchmarks/run.ps1
```

### Adding Typhon to any C# project

`.ty` files are compiled like `.cs` files — just put them in the project. In this repo the wiring lives in
`samples/Directory.Build.props`; for an external project it is one package reference (`dotnet pack src/Typhon.Compiler`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <TyphonTypeMode>strict</TyphonTypeMode>   <!-- strict | warn (default) | permissive -->
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Typhon" Version="0.1.0" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

`**/*.ty` is included automatically (set `EnableDefaultTyphonItems=false` and add `<TyphonCompile Include="..." />`
items to control it yourself). Nothing in the .NET SDK, MSBuild or Roslyn is modified.

## How it works

```
 .ty ──► Lexer (INDENT/DEDENT) ──► Parser ──► AST ──► scope analysis ──► C# + #line ──► Roslyn ──► IL (.dll/.exe)
                                                         + type resolution          ▲
                                                           (Roslyn symbols)         │ same compilation as the
                                                                                    │ project's own .cs files
```

* **Front end** (`src/Typhon.Compiler/Syntax`): a hand-written tokenizer and recursive-descent parser for the Python
  subset. No dependency on CPython.
* **Lowering** (`src/Typhon.Compiler/Emit`): Typhon → C#. The interesting parts:
  * *Python has no `new`.* `Foo(1)` is a call or a construction depending on what `Foo` is, so the compiler resolves
    names against Typhon modules **and** the Roslyn compilation (referenced assemblies + C# code in the same project).
  * *Python locals are function-scoped, C# locals are block-scoped.* `LocalsAnalyzer` computes, per variable, the
    lowest common block of all uses; variables are declared inline with `var` (so C# infers the type) where possible,
    split into independent variables where their uses don't interact, and hoisted otherwise.
  * Python protocol methods (`__add__`, `__eq__`, `__str__`, `__getitem__`, `__iter__`, `__len__`, ...) are kept and
    bridged to .NET operators, `Equals`, `ToString`, indexers and `IEnumerable<T>`.
* **Back end = Roslyn.** The generated C# is compiled by Roslyn, which gives us overload resolution, generic inference,
  lambdas, async state machines, iterators, closures, PDBs and IL emission for free. `#line` directives map every
  error, warning, stack-trace line and breakpoint back to the `.ty` file.
* **Build integration** (`src/Typhon.Compiler/Generator`): an incremental Roslyn **source generator** receives the
  `.ty` files as `AdditionalFiles`. That is why Typhon and C# can live in one project and reference each other in
  both directions, and why Typhon types show up in C# IntelliSense.
* **Runtime** (`src/Typhon.Compiler/Runtime/Builtins.cs`): Python builtins (`print`, `len`, `range`, `sorted`, `format`,
  str/list/dict methods, ...) and small stdlib shims (`math`, `time`, `random`, `sys`, `os`, `asyncio`), compiled
  *into* every Typhon assembly as internal types — no runtime dependency.
* **numpy** (`src/Typhon.NumPy`): a numpy-compatible API (float64 `ndarray`, ufuncs, `@`, `linalg`, `random`) written
  in C# on top of `System.Numerics.Tensors.TensorPrimitives` (SIMD).

## The language

### Types: gradual, TypeScript-style

| Python annotation | C# / CLR type | | Python annotation | C# / CLR type |
|---|---|---|---|---|
| `int` | `int` (System.Int32) | | `list[T]` | `List<T>` |
| `float` | `double` | | `dict[K, V]` | `Dictionary<K, V>` |
| `str` | `string` | | `set[T]` | `HashSet<T>` |
| `bool` | `bool` | | `tuple[A, B]` | `(A, B)` (ValueTuple) |
| `None` (return) | `void` | | `Optional[T]`, `T \| None` | `T?` |
| `Any` | `dynamic` | | `Callable[[A], R]` | `Func<A, R>` / `Action<A>` |
| `Iterator[T]` | `IEnumerable<T>` | | `Array[T]` (Typhon) | `T[]` |
| `decimal`, `long`, `float32`, ... | `decimal`, `long`, `float`, ... | | any .NET type | itself |

Locals are inferred (`x = 1.5` → `var x = 1.5`). Where the compiler cannot determine a type — an unannotated
parameter, an empty `[]`, a variable first assigned in a nested block — it falls back to C# `dynamic`, and
`TyphonTypeMode` decides what that means:

| Mode | Implicit `dynamic` | Use for |
|---|---|---|
| `strict` | compile error (TY1001) | libraries, hot paths, a prerequisite for Native AOT |
| `warn` (default) | compiles, warning per site | normal code, gradual migration |
| `permissive` | compiles silently | porting existing Python |

An explicit `Any` annotation is always allowed. The cost is real: `benchmarks/bench_nbody.ty` and its
annotation-free twin `bench_nbody_untyped.ty` differ by 46x (see below).

### Semantics: Python syntax, CLR types, Python-flavoured operators

Typhon deliberately uses **CLR semantics for types and objects** (fixed class layouts, nominal types, 32-bit `int`,
reference equality unless `__eq__` is defined) and **Python semantics where it is cheap and expected**:

* `/` is true division, `//` floor division, `%` takes the sign of the divisor, `**` is power
* truthiness in conditions (`if items:`), `and`/`or`/`not`, chained comparisons, `in`/`not in`, `is None`
* slicing with clamping, negative indices and steps (`s[::-1]`), `[0] * n`, `"-" * 40`, `list + list`
* `print`/`str`/`repr`/f-strings format like CPython (`True`, `None`, `[1, 2]`, `{'a': 1}`, `1.0`, `Color.RED`)
* Python exceptions map to .NET ones (`ValueError` → `ArgumentException`/`FormatException`, `KeyError` →
  `KeyNotFoundException`, ...), so `except ValueError` catches what .NET throws and C# can catch what Typhon raises

### Supported (PoC)

Modules & packages (folders → namespaces), `import`/`from ... import` of Typhon modules, .NET namespaces/types and the
Typhon stdlib, functions with defaults/`*args`/keyword arguments, PEP 695 generics (`def f[T]`, `class Box[T]`, bounds),
classes with inheritance, `super()`, properties (+setters), `@staticmethod`/`@classmethod`, `@abstractmethod`/`ABC`,
`@override`, `@final`, `@dataclass`, `Enum`, `Protocol` (→ interface, implemented structurally *or* explicitly),
operator overloading, any other decorator → .NET attribute (`@Obsolete("...")`, `@Fact`), docstrings → XML docs,
`if/elif/else`, `while`, `for` (counting loops over `range` compile to plain `for` loops), `try/except/else/finally`,
`raise`, `with` (→ `using`), `async def`/`await`/`async for`/`async with` (→ `Task`), generators (`yield`, `yield from`),
lambdas, closures, `global`/`nonlocal`, list/dict/set comprehensions and generator expressions (→ LINQ), f-strings with
the Python format-spec mini-language, tuple unpacking, `if __name__ == "__main__":` (or a `__main__.ty` file) as the
entry point, `asyncio.run(...)` to call async code from synchronous code.

### Not supported yet / known gaps

`match`, walrus `:=`, `**kwargs`, `del`, `while/for ... else`, bytes, complex, `%`-formatting, metaclasses,
`eval`/`exec`/`setattr` and other runtime reflection on Python objects, multiple inheritance of classes, tuple indexing
(`t[0]`; use unpacking), static methods on generic classes need explicit type arguments (`Stack[int].of(...)`),
.NET `out`/`ref` parameters (planned: tuple returns like IronPython), arbitrary-precision `int` (`int` is 32-bit),
iterating a `str` yields `char` (which has the common str methods). Union types other than `X | None` are `object`
for now (see roadmap).

**numpy (Typhon.NumPy)** covers the common API (creation, ufuncs with broadcasting, reductions with `axis`, `@`,
`linalg`, `random`, slicing/masks) but: float64 only, slices and `.T` are copies (`reshape`/`ravel` are views),
`a[i]` returns `dynamic` (annotate the target, `v: float = a[i]`, to get back to static code), `==` compares references
(use `np.equal`), random streams are deterministic per seed but differ from NumPy's.

## Benchmark results

Every benchmark is a single file that is valid Python *and* valid Typhon. CPython 3.15.2 runs it directly, Typhon
compiles it (JIT or Native AOT); the runners check that every tool prints the same result (they all do) and report the
median of 5 runs plus the peak RAM of each process. Full analysis, method and caveats:
**[benchmarks/REPORT.md](benchmarks/REPORT.md)**; raw tables: [RESULTS.md](benchmarks/RESULTS.md) (Windows) and
[RESULTS_LINUX.md](benchmarks/RESULTS_LINUX.md) (Linux, incl. Codon and Pythran). Measured on an Intel Core Ultra 9
285HX laptop, .NET 11.0.100, CPython 3.15.2, NumPy 2.5.5 + OpenBLAS.

![Speed-up of Typhon over CPython and NumPy](benchmarks/img/speedup.png)

* **Typed pure-Python programs: 1.9–75x faster** than CPython; geometric mean **11.1x with the JIT, 13.6x as a Native
  AOT executable** (9 programs). Largest on numeric code over floats and small objects, smallest on `str`/`dict`/sorting,
  where CPython already runs C.
* **Type hints are the reason.** The same N-body source without annotations falls back to `dynamic` and is only 1.5x
  faster than CPython, 54x slower than the typed version.
* **Native AOT**: one self-contained 1.5–1.9 MB executable, **9 MB of RAM** for small programs (CPython 12 MB, JIT 20 MB);
  on Linux a tiny program starts in **1.8 ms with 4 MB**, the fastest and leanest of every tool tested.
* **NumPy programs: rough parity** (geometric mean 1.04x JIT, 1.13x AOT, each side in its faster thread setting).
  Typhon.NumPy wins element loops and broadcasting; NumPy wins matrix multiply and dense linear algebra (BLAS/LAPACK).

### Pure Python programs (Windows)

| Program | CPython | Typhon JIT | Typhon AOT | Speed-up JIT | Speed-up AOT | RAM CPython | RAM JIT | RAM AOT |
|---|---|---|---|---|---|---|---|---|
| N-body | 3,024 ms | 40.3 ms | 43.8 ms | **75x** | **69x** | 12 MB | 21 MB | 9 MB |
| Spectral norm | 469 ms | 9.7 ms | 8.6 ms | **48x** | **54x** | 12 MB | 21 MB | 9 MB |
| Mandelbrot | 2,067 ms | 51.1 ms | 51.9 ms | **40x** | **40x** | 12 MB | 20 MB | 9 MB |
| fib(32) | 134 ms | 9.0 ms | 4.7 ms | **15x** | **29x** | 12 MB | 20 MB | 9 MB |
| Binary trees | 783 ms | 75.3 ms | 55.5 ms | **10x** | **14x** | 22 MB | 43 MB | 32 MB |
| Fannkuch | 324 ms | 39.1 ms | 28.4 ms | **8.3x** | **11x** | 12 MB | 41 MB | 27 MB |
| Prime sieve | 989 ms | 358 ms | 199 ms | **2.8x** | **5.0x** | 50 MB | 37 MB | 23 MB |
| Word count (str + dict) | 413 ms | 161 ms | 140 ms | **2.6x** | **3.0x** | 12 MB | 41 MB | 27 MB |
| `sorted()` ints + strings | 353 ms | 182 ms | 218 ms | **1.9x** | **1.6x** | 88 MB | 61 MB | 54 MB |
| N-body without type hints | 3,286 ms | 2,168 ms | n/a (dynamic) | **1.5x** | – | 12 MB | 59 MB | – |

### NumPy programs (Windows)

Typhon runs these against Typhon.NumPy, a numpy-compatible library in C# (NumPy itself is C/Fortran and is not
compiled). Each side in its faster thread setting.

| Program | NumPy | Typhon JIT | Typhon AOT | Speed-up (best Typhon) | RAM NumPy | RAM JIT | RAM AOT |
|---|---|---|---|---|---|---|---|
| Element loop `for i in range(n): a[i]` | 271 ms | 54.0 ms | n/a (dynamic) | **5.0x** | 43 MB | 61 MB | – |
| Broadcasting (pairwise distances) | 268 ms | 95.9 ms | 52.9 ms | **5.1x** | 211 MB | 516 MB | 578 MB |
| ufuncs (`sqrt`, `exp`, `sin`) | 1,184 ms | 1,089 ms | 522 ms | **2.3x** | 74 MB | 362 MB | 225 MB |
| Reductions along axes | 414 ms | 340 ms | 224 ms | **1.8x** | 89 MB | 91 MB | 72 MB |
| Matmul 1000 x 1000 | 17.4 ms | 24.4 ms | 40.3 ms | **NumPy 1.4x faster** | 64 MB | 89 MB | 56 MB |
| `linalg.solve` + `inv` 300 x 300 | 31.7 ms | 325 ms | 159 ms | **NumPy 5.0x faster** | 37 MB | 37 MB | 16 MB |

### Startup and footprint

A tiny program (`bench_startup.ty`), measured from start to exit:

| | Windows: time | Windows: RAM | Linux: time | Linux: RAM | What you ship |
|---|---|---|---|---|---|
| CPython | 19.7 ms | 12 MB | 9.2 ms | 12 MB | the `.py` file + a Python installation |
| Codon | – | – | 8.3 ms | 10 MB | a small executable + Codon's runtime libraries |
| Typhon JIT | 34.5 ms | 20 MB | 20.0 ms | 30 MB | a 0.05 MB `.dll` + the .NET runtime |
| **Typhon Native AOT** | **21.3 ms** | **9 MB** | **1.8 ms** | **4 MB** | **one 1.5–1.9 MB self-contained executable** |

### Typhon vs Codon vs Pythran (Linux)

The same files compiled with two other Python compilers, [Codon](https://github.com/exaloop/codon) (whole programs to
native code via LLVM) and [Pythran](https://github.com/serge-sans-paille/pythran) (numeric functions to C++), on Linux
(WSL2, same machine), each compared with CPython on Linux:

![Typhon, Codon and Pythran compared](benchmarks/img/compilers.png)

Speed-up over CPython (Linux):

| Program | Codon | Pythran | Typhon JIT | Typhon AOT |
|---|---|---|---|---|
| N-body | 37x | n/a (classes) | **62x** | 54x |
| Spectral norm | 39x | 39x | **41x** | 28x |
| Mandelbrot | 16x | 26x | **28x** | **28x** |
| fib(32) | 14x | 25x | 14x | **28x** |
| Binary trees | n/a (string annotation) | n/a (classes) | 8.2x | **13x** |
| Fannkuch | 9.5x | **15x** | 6.6x | 9.3x |
| Prime sieve | 12x | **15x** | 2.8x | 5.1x |
| `sorted()` | **3.9x** | n/a (not in a function) | 2.0x | 1.6x |
| Word count | 2.5x | 1.2x | 1.9x | **2.6x** |
| N-body without type hints | **29x** | n/a (classes) | 1.1x | n/a (dynamic) |
| NumPy element loop | 19x | **31x** | 4.4x | n/a (dynamic) |
| NumPy broadcasting | 1.1x | **5.1x** | 1.4x | 2.4x |
| NumPy ufuncs | 1.5x | **2.3x** | 0.8x | 1.9x |
| NumPy matmul 1000 x 1000 | 0.4x | n/a (not in a function) | **0.6x** | 0.3x |

On the 6 programs all of them can compile: **Pythran 13.6x, Typhon AOT 11.8x, Codon 11.6x, Typhon JIT 9.2x** (geometric
means). Typhon leads on float- and object-heavy code, Codon and Pythran on integer/list code and NumPy-style code; only
Codon stays fast without type hints, because it infers types for the whole program.

Peak RAM (Linux, max resident set size):

| Program | CPython | Codon | Pythran | Typhon JIT | Typhon AOT |
|---|---|---|---|---|---|
| Startup (tiny program) | 12 MB | 10 MB | n/a | 30 MB | **4 MB** |
| fib, Mandelbrot, N-body, spectral norm | 12 MB | 10 MB | 29–30 MB | 29–30 MB | **4 MB** |
| Fannkuch | 12 MB | **10 MB** | 29 MB | 50 MB | 22 MB |
| Word count | 12 MB | **11 MB** | 29 MB | 51 MB | 23 MB |
| Prime sieve | 50 MB | **15 MB** | 30 MB | 46 MB | 19 MB |
| `sorted()` | 87 MB | 53 MB | n/a | 71 MB | **49 MB** |
| NumPy broadcasting | 214 MB | 165 MB | **30 MB** | 522 MB | 462 MB |
| NumPy ufuncs | 76 MB | 96 MB | **37 MB** | 365 MB | 104 MB |

Typhon AOT is the leanest for compute-heavy code; Codon stays lean in allocation-heavy code too (Typhon's garbage
collector frees memory in batches); Pythran has a ~29 MB floor (every Pythran module imports NumPy) but is by far the
leanest on NumPy code, because its C++ expression templates fuse whole expressions into one loop without temporary
arrays.

**Platforms.** Typhon's JIT build is a single IL `.dll` that runs unchanged on every OS and CPU with a .NET runtime
(tested here on Windows and Linux); its Native AOT build is a native executable per target OS. Codon has no Windows
support (Linux and macOS) and its executables need Codon's runtime libraries. Pythran builds a C++ extension module per
OS *and* Python version and always needs CPython to run (on Windows it needs a C++ compiler such as clang-cl).

### Do you still need NumPy?

The same problem written as plain Python loops and as vectorized NumPy code, each run on both runtimes (Windows):

![Plain Typhon loops vs vectorized NumPy](benchmarks/img/problems.png)

| Problem | CPython loops | CPython + NumPy | Typhon loops (JIT) | Typhon loops (AOT) | Typhon + NumPy (AOT) |
|---|---|---|---|---|---|
| Mandelbrot 400 x 400 | 496 ms | 415 ms | 14.2 ms | **11.4 ms** | 234 ms |
| Pairwise distances, 1500 points | 148 ms | 30.3 ms | 3.9 ms | **3.3 ms** | 8.1 ms |
| Estimate π, 5M points | 388 ms | 160 ms | 45.0 ms | 43.5 ms | **32.9 ms** |
| Moving average, 5M samples | 329 ms | **31.3 ms** | 56.0 ms | 56.3 ms | n/a (dynamic) |
| Dot product, 10M floats | 152 ms | **1.4 ms** | 6.8 ms | 6.5 ms | n/a (dynamic) |
| Matrix multiply 200 x 200 | 206 ms | **0.4 ms** | 12.3 ms | 7.4 ms | 0.7 ms |

Plain loops in Typhon are 6–38x faster than plain loops in CPython and beat vectorized NumPy on 3 of 6 problems, where
vectorizing forces extra work (Mandelbrot can't exit early per point; pairwise distances and π build large temporary
arrays). NumPy stays ahead where one tuned kernel does the whole job (BLAS for dot product and matmul).

**RAM.** With the JIT, Typhon starts ~8 MB higher (the .NET runtime) and its garbage collector frees memory in batches
rather than immediately, so small and allocation-heavy programs use more RAM. Native AOT removes the runtime floor
(9 MB vs CPython's 12 MB). Typed lists are far denser than Python lists, so programs holding big lists use 2.7–2.9x
*less* (dot product 175 vs 468 MB). Large NumPy temporaries are the weak spot (up to 5x NumPy's peak);
[the report](benchmarks/REPORT.md#6-why-typhon-sometimes-uses-more-ram) breaks this down.

**Not measured:** PyPy, Cython, mypyc, Numba, CPython's experimental JIT. Compile time is excluded (median per program:
Typhon 1.3 s, Typhon AOT 4.8 s, Codon 4.3 s, Pythran 1.6 s); a compiled Typhon program is still faster end-to-end than
CPython on every pure-Python benchmark.

## Design notes and important decisions

1. **Transpile through C# instead of emitting IL directly.** Emitting IL ourselves would mean re-implementing C#'s
   overload resolution, generic inference, lambdas/closures, async and iterator state machines, `dynamic` call sites,
   and PDB generation. Going through Roslyn gives all of that, guarantees "fully compatible with C#" by construction,
   and still produces plain IL. The front end is backend-agnostic, so a direct IL backend remains possible.
2. **Source generator, not a separate compiler.** It plugs into every C# project without touching the SDK, works in
   the IDE, and lets C# and Typhon reference each other inside one assembly. The price: a pure Typhon project is still a
   `.csproj`, and generators run on every IDE keystroke (they need to stay fast). A dedicated MSBuild SDK
   (`<Project Sdk="Typhon.Sdk">`) can come later.
3. **Static by default, dynamic as an explicit escape hatch.** Speed and C# interop both come from static types;
   gradual typing keeps porting possible. `typhon build --aot` builds a native executable; it additionally rejects dynamic values that come from libraries (TY2001), because Native AOT cannot run late-bound code.
4. **"Valid Python" as a design constraint.** Never add syntax CPython can't parse. You get editor support, formatters
   and linters for free, and — as the benchmarks show — the same file can run on both runtimes, which is also the
   cheapest possible conformance test (diff the outputs).
5. **Names are kept as written** (`calculate_area` stays `calculate_area` for C# callers). Leading `_` → `internal`
   / `protected internal`, `__x` → `private`. A PascalCase-mapping option is easy to add if C# consumers want it.
6. **Python protocols are bridged, not emulated.** `__eq__` becomes `Equals` + `==`, `__iter__` becomes
   `IEnumerable<T>`, `__getitem__` an indexer — so Typhon objects behave natively in C#, LINQ and data binding.

## License

Typhon is licensed under the [MIT License](LICENSE), the same license as .NET (runtime, Roslyn, SDK). The Typhon runtime
(`Builtins.cs`) is compiled into every Typhon assembly and is covered by the same license; code generated from your
`.ty` files is yours.

## Roadmap ideas

* C# 15 union types for `A | B` annotations (canonicalized, one generated union per member set)
* `out`/`ref` parameters as tuple returns, `**kwargs`, `match` → C# pattern matching
* A conformance suite: run every test file under CPython and Typhon, diff the output
* `sum(...)`/`any(...)` over generator expressions lowered to loops instead of LINQ
* A fully static Typhon.NumPy API (no `dynamic` return values, e.g. for `a[i]` and `np.dot`), so every NumPy program can be compiled with Native AOT
* Expression fusion for NumPy code: reuse or fuse the temporaries of `np.sqrt(x) * 2.0 - x / 3.0` (the RAM weak spot)
* A dedicated MSBuild SDK and `dotnet new` templates; a language server reusing the Typhon front end
