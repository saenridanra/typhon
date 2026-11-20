# Typhon — Python syntax, compiled to .NET IL

Typhon is a proof of concept for a **statically typed, Python-syntax language for .NET**. A `.ty` file is (in practice)
valid Python 3.12 source; the Typhon compiler turns it into ordinary IL, so Typhon code is a first-class .NET citizen:
C# can call it, subclass it and reference it like any other assembly, and Typhon can import any .NET namespace.

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
| `strict` | compile error (TY1001) | libraries, hot paths, Native AOT (no DLR) |
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
entry point, top-level `await`.

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
compiles it to IL; the runner checks that both print the same result (they all do), reports the median of 5 runs
and the peak RAM of each process. Full analysis, method and caveats: **[benchmarks/REPORT.md](benchmarks/REPORT.md)**;
raw tables: [benchmarks/RESULTS.md](benchmarks/RESULTS.md). Measured on an Intel Core Ultra 9 285HX laptop, Windows 11,
.NET 11.0.100, NumPy 2.5.5 + OpenBLAS.

![Speed-up of Typhon over CPython and NumPy](benchmarks/img/speedup.png)

* **Typed pure-Python programs: 2–80x faster** than CPython, geometric mean **10.3x** (9 programs). Largest on numeric
  code over floats and small objects, smallest on `str`/`dict`/sorting, where CPython already runs C.
* **Type hints are the reason.** The same N-body source without annotations falls back to `dynamic` and is only 1.8x
  faster than CPython, 46x slower than the typed version.
* **NumPy programs: rough parity** (geometric mean 1.18x, each side in its faster thread setting). Typhon.NumPy wins on
  element loops and broadcasting (3.1–3.5x); NumPy wins dense linear algebra by 10x (LAPACK).

| Program | CPython | Typhon | Speed-up | RAM CPython | RAM Typhon |
|---|---|---|---|---|---|
| N-body | 3,948 ms | 49.2 ms | **80x** | 12 MB | 21 MB |
| Spectral norm | 498 ms | 10.9 ms | **46x** | 12 MB | 21 MB |
| Mandelbrot | 1,852 ms | 63.4 ms | **29x** | 12 MB | 20 MB |
| Binary trees | 886 ms | 91.9 ms | **9.6x** | 22 MB | 43 MB |
| fib(32) | 145 ms | 15.8 ms | **9.2x** | 12 MB | 20 MB |
| Fannkuch | 318 ms | 44.1 ms | **7.2x** | 12 MB | 40 MB |
| Prime sieve | 1,450 ms | 365 ms | **4.0x** | 50 MB | 37 MB |
| `sorted()` ints + strings | 530 ms | 220 ms | **2.4x** | 88 MB | 61 MB |
| Word count (str + dict) | 365 ms | 179 ms | **2.0x** | 12 MB | 41 MB |
| N-body without type hints | 4,154 ms | 2,283 ms | **1.8x** | 12 MB | 59 MB |

### Do you still need NumPy?

The same problem written as plain Python loops and as vectorized NumPy code, each run on both runtimes:

![Plain Typhon loops vs vectorized NumPy](benchmarks/img/problems.png)

| Problem | CPython loops | CPython + NumPy | Typhon loops | Typhon + NumPy |
|---|---|---|---|---|
| Mandelbrot 400 x 400 | 493 ms | 612 ms | **18.5 ms** | 514 ms |
| Pairwise distances, 1500 points | 136 ms | 40.9 ms | **4.9 ms** | 48.3 ms |
| Estimate π, 5M points | 432 ms | 217 ms | **50.1 ms** | 101 ms |
| Moving average, 5M samples | 341 ms | **39.6 ms** | 72.6 ms | 111 ms |
| Dot product, 10M floats | 157 ms | **3.0 ms** | 10.9 ms | 8.3 ms |
| Matrix multiply 200 x 200 | 212 ms | **0.7 ms** | 14.6 ms | 6.5 ms |

Plain loops in Typhon are 4.7–28x faster than plain loops in CPython, and beat vectorized NumPy on 3 of 6 problems,
where vectorizing forces extra work (Mandelbrot can't exit early per point; pairwise distances and π build large
temporary arrays). NumPy stays ahead where one tuned kernel does the whole job (BLAS for dot product and matmul).

**RAM.** Typhon starts ~7 MB higher (the .NET runtime) and its garbage collector frees memory in batches rather than
immediately, so small and allocation-heavy programs use more RAM. Typed lists are far denser than Python lists, so
programs holding big lists use 2.7–2.9x *less* (dot product 174 vs 468 MB). Large NumPy temporaries are the weak spot
(up to 5x NumPy's peak); [the report](benchmarks/REPORT.md#4-why-typhon-sometimes-uses-more-ram) breaks this down.

**Not measured:** PyPy, Cython, mypyc, Numba, CPython's experimental JIT. Compile time is excluded (median 1.8 s per
program with `typhon build`); a compiled Typhon program is still faster end-to-end than CPython on every pure-Python
benchmark.

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
   gradual typing keeps porting possible. `strict` mode doubles as "Native AOT compatible".
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
* Native AOT executables for `strict` programs (no JIT, no DLR: smaller startup and memory footprint)
* Expression fusion for NumPy code: reuse or fuse the temporaries of `np.sqrt(x) * 2.0 - x / 3.0` (the RAM weak spot)
* A dedicated MSBuild SDK and `dotnet new` templates; a language server reusing the Typhon front end
