# Roadmap ideas

The [status page](https://typhon-lang.com/status) explains the principles behind these and what they cost.

* C# 15 union types for `A | B` annotations (canonicalized, one generated union per member set)
* `out`/`ref` parameters as tuple returns
* A faster big-int mode: a hybrid int (64-bit fast path, BigInteger only on overflow) instead of BigInteger everywhere
* `decimal` with a precision context; running `doctest` examples; compiling more of CPython's pure-Python stdlib
* Grow the conformance suite (port relevant parts of CPython's own test suite)
* Ship the CLI as a `dotnet tool` with platform-specific ReadyToRun packages
* Re-run the Linux benchmark with type inference (the untyped N-body row there predates it)
* `sum(...)`/`any(...)` over generator expressions lowered to loops instead of LINQ
* A fully static Typhon.NumPy API (no `dynamic` return values, e.g. for `a[i]` and `np.dot`), so every NumPy program can be compiled with Native AOT
* Expression fusion for NumPy code: reuse or fuse the temporaries of `np.sqrt(x) * 2.0 - x / 3.0` (the RAM weak spot)
* A dedicated MSBuild SDK and `dotnet new` templates; a language server reusing the Typhon front end
