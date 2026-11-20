# Copyright (c) 2026 The Typhon contributors.
# Licensed under the MIT License. See LICENSE file in the project root for full license information.

<#
.SYNOPSIS
    Benchmarks Typhon against CPython (pure Python programs) and CPython + NumPy (numpy programs): time and peak RAM.

.DESCRIPTION
    Every benchmarks/bench_*.ty file is valid Python *and* valid Typhon: the exact same source runs on both.
      * Typhon: each program is compiled once with `typhon build` (compile time reported separately), then the compiled
        app is run with `dotnet app.dll` — so time and RAM are the program's, not the compiler's.
      * CPython: the base interpreter is run directly (a Windows venv's python.exe is only a launcher that spawns a
        child process, which would hide the real memory use); the venv's site-packages are put on PYTHONPATH.
      * Each program times its own hot section with time.perf_counter() and prints "TIME <label> <ms> ms".
      * Each program prints a result line; the runner checks CPython and Typhon printed the same result.
      * Every configuration runs -Repeat times; medians are reported. RAM = peak working set of the process.
      * numpy programs (bench_np_*) run in two configurations per side, because threading decides those results:
        NumPy with default OpenBLAS threads and with OPENBLAS_NUM_THREADS=1; Typhon with default threads and with
        TYPHON_NUMPY_THREADS=1. "Best vs best" compares each side's faster configuration.
    Results are printed and written to benchmarks/RESULTS.md.

.EXAMPLE
    ./benchmarks/run.ps1                          # uses .venv (if present) or `python` from PATH
    ./benchmarks/run.ps1 -Repeat 5 -Filter np_    # only numpy programs, 5 runs each
#>
param(
    [string]$Python,
    [int]$Repeat = 3,
    [string]$Filter = "",
    [ValidateSet("all", "programs", "problems")][string]$Section = "all",
    [switch]$NoAot,
    [switch]$SkipBuild
)
$ErrorActionPreference = "Stop"
$inv = [Globalization.CultureInfo]::InvariantCulture
$root = Split-Path $PSScriptRoot -Parent

# Process runner that also reports peak memory (GetProcessMemoryInfo works on the handle after the process exited).
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class BenchProcess
{
    [StructLayout(LayoutKind.Sequential)]
    struct PMC
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                       QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool GetProcessMemoryInfo(IntPtr process, out PMC counters, uint size);

    public sealed class Result
    {
        public int ExitCode; public string Output; public double WallMs; public double PeakWorkingSetMB; public double PeakPrivateMB;
    }

    public static Result Run(string exe, string[] args, IDictionary<string, string> env)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        var sb = new StringBuilder();
        var gate = new object();
        var p = new Process { StartInfo = psi };
        try
        {
            p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (gate) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (gate) sb.AppendLine(e.Data); };
            var sw = Stopwatch.StartNew();
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            sw.Stop();
            PMC c;
            GetProcessMemoryInfo(p.Handle, out c, (uint)Marshal.SizeOf(typeof(PMC)));
            return new Result
            {
                ExitCode = p.ExitCode, Output = sb.ToString(), WallMs = sw.Elapsed.TotalMilliseconds,
                PeakWorkingSetMB = c.PeakWorkingSetSize.ToUInt64() / 1048576.0, PeakPrivateMB = c.PeakPagefileUsage.ToUInt64() / 1048576.0,
            };
        }
        finally { p.Dispose(); }
    }
}
"@

# ── tools ────────────────────────────────────────────────────────────────────────────
if (-not $Python) {
    $venv = Join-Path $root ".venv/Scripts/python.exe"
    $Python = if (Test-Path $venv) { $venv } else { "python" }
}
$pyExe = $null; $pyEnv = @{}; $pythonVersion = $null; $numpyInfo = ""
if (Get-Command $Python -ErrorAction SilentlyContinue) {
    $v = (& $Python --version 2>&1 | Out-String).Trim()
    if ($v -match "^Python 3") {
        $pythonVersion = $v
        $info = & $Python -c "import sys, site, json; print(json.dumps({'exe': sys._base_executable, 'site': site.getsitepackages()}))" | ConvertFrom-Json
        $pyExe = $info.exe
        $pyEnv = @{ PYTHONPATH = ($info.site -join ';'); PYTHONUTF8 = "1" }
        $numpyInfo = (& $Python -c "import numpy; c = numpy.show_config(mode='dicts'); b = c['Build Dependencies']['blas']; print('NumPy', numpy.__version__, '+', b['name'], b.get('version', ''))" 2>&1 | Out-String).Trim()
    }
}
if (-not $pythonVersion) { Write-Warning "CPython not found ('$Python'); only Typhon results will be shown." }

if (-not $SkipBuild) {
    dotnet build (Join-Path $root "src/Typhon.Cli/Typhon.Cli.csproj") -c Release -nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "building the Typhon CLI failed" }
}
$typhon = (Get-ChildItem (Join-Path $root "src/Typhon.Cli/bin/Release") -Recurse -Filter "typhon.exe" | Select-Object -First 1).FullName
$buildRoot = Join-Path $root "artifacts/bench"

# ── helpers ──────────────────────────────────────────────────────────────────────────
function Invoke-Measured([string]$exe, [string[]]$arguments, [hashtable]$envVars) {
    $dict = [Collections.Generic.Dictionary[string, string]]::new()
    foreach ($k in $envVars.Keys) { $dict[$k] = [string]$envVars[$k] }
    $r = [BenchProcess]::Run($exe, $arguments, $dict)
    $times = @{}
    foreach ($m in [regex]::Matches($r.Output, "TIME (\S+) ([\d\.]+) ms")) { $times[$m.Groups[1].Value] = [double]::Parse($m.Groups[2].Value, $inv) }
    $result = (($r.Output -split "`r?`n") | Where-Object { $_ -and $_ -notmatch "^TIME " }) -join " / "
    [pscustomobject]@{ Ok = ($r.ExitCode -eq 0); Output = $r.Output; Times = $times; Result = $result.Trim(); WallMs = $r.WallMs; RamMB = $r.PeakWorkingSetMB }
}

function Median([double[]]$xs) {
    $s = @($xs | Sort-Object)
    if ($s.Count -eq 0) { return $null }
    if ($s.Count % 2) { return $s[[int][math]::Floor($s.Count / 2)] }
    return ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2
}

function Run-Series([string]$exe, [string[]]$arguments, [hashtable]$envVars = @{}) {
    $runs = @()
    for ($i = 0; $i -lt $Repeat; $i++) {
        $r = Invoke-Measured $exe $arguments $envVars
        if (-not $r.Ok) { Write-Warning "failed: $exe $($arguments -join ' ')`n$($r.Output)"; return $null }
        $runs += $r
    }
    $med = @{}
    foreach ($l in $runs[0].Times.Keys) { $med[$l] = Median ($runs | ForEach-Object { $_.Times[$l] }) }
    [pscustomobject]@{
        Times = $med; Result = $runs[0].Result
        WallMs = (Median ($runs | ForEach-Object { $_.WallMs })); RamMB = (Median ($runs | ForEach-Object { $_.RamMB }))
    }
}

function Fmt($ms) { if ($null -eq $ms) { "–" } else { [string]::Format($inv, "{0:N1} ms", $ms) } }
function FmtMB($mb) { if ($null -eq $mb) { "–" } else { [string]::Format($inv, "{0:N0} MB", $mb) } }
function Ratio($a, $b) {
    if ($null -eq $a -or $null -eq $b -or $b -eq 0) { return "–" }
    $r = $a / $b
    [string]::Format($inv, $(if ($r -lt 0.1) { "{0:N2}x" } else { "{0:N1}x" }), $r)
}
function MinOf($a, $b) { if ($null -eq $a) { $b } elseif ($null -eq $b) { $a } else { [math]::Min($a, $b) } }

# ── measurement of one program on both runtimes ──────────────────────────────────────
$compileMs = @(); $aotCompileMs = @()

# Builds the program once with Typhon, then runs it on Typhon and CPython. Programs that use numpy are also run
# single-threaded on both sides (TYPHON_NUMPY_THREADS=1 / OPENBLAS_NUM_THREADS=1); "best" picks the faster config.
function Measure-Program([IO.FileInfo]$file) {
    $usesNumpy = (Get-Content $file.FullName -Raw) -match "(?m)^import numpy"
    $mode = if ($file.BaseName -like "*untyped*") { "--permissive" } else { "--strict" }
    $outDir = Join-Path $buildRoot ($file.Directory.Name + "_" + $file.BaseName)
    $build = Invoke-Measured $typhon @("build", $file.FullName, $mode, "-o", $outDir) @{}
    if (-not $build.Ok) { Write-Warning "Typhon build failed for $($file.Name):`n$($build.Output)"; return $null }
    $script:compileMs += $build.WallMs
    $dll = (Get-ChildItem $outDir -Filter "typhon_*.dll" | Select-Object -First 1).FullName

    $ty = Run-Series "dotnet" @($dll)
    if (-not $ty) { return $null }
    $cp = if ($pyExe) { Run-Series $pyExe @($file.FullName) $pyEnv } else { $null }
    $ty1 = $null; $cp1 = $null
    if ($usesNumpy) {
        $ty1 = Run-Series "dotnet" @($dll) @{ TYPHON_NUMPY_THREADS = "1" }
        $cp1 = if ($pyExe) { Run-Series $pyExe @($file.FullName) ($pyEnv + @{ OPENBLAS_NUM_THREADS = "1" }) } else { $null }
    }

    # Native AOT: same program as a self-contained native executable (no JIT). Programs that use dynamic values are
    # rejected by `typhon build --aot` (TY2001) and reported as n/a.
    $aot = $null; $aotStatus = "skipped"; $aotSizeMB = $null
    if (-not $NoAot) {
        $aotDir = Join-Path $buildRoot ("aot_" + $file.Directory.Name + "_" + $file.BaseName)
        $ab = Invoke-Measured $typhon @("build", $file.FullName, $mode, "--aot", "-o", $aotDir) @{}
        if ($ab.Ok) {
            $script:aotCompileMs += $ab.WallMs
            $exe = Get-ChildItem $aotDir -Filter "typhon_*.exe" | Select-Object -First 1
            $aotSizeMB = $exe.Length / 1MB
            $aot = Run-Series $exe.FullName @()
            $aotStatus = if ($aot) { "ok" } else { "failed" }
        } elseif ($ab.Output -match "TY2001") { $aotStatus = "dynamic" }
        else { Write-Warning "Native AOT build failed for $($file.Name):`n$($ab.Output)"; $aotStatus = "failed" }
    }
    [pscustomobject]@{ Ty = $ty; Ty1 = $ty1; Cp = $cp; Cp1 = $cp1; Aot = $aot; AotStatus = $aotStatus; AotSizeMB = $aotSizeMB; UsesNumpy = $usesNumpy }
}

# AOT cells: a value, or why there is none
function AotFmt($m, $value, [switch]$Ram) {
    switch ($m.AotStatus) {
        "ok" { if ($Ram) { FmtMB $value } else { Fmt $value } }
        "dynamic" { "n/a (dynamic)" }
        "skipped" { "–" }
        default { "build failed" }
    }
}

function Best($series, $series1, $label) { MinOf ($(if ($series) { $series.Times[$label] })) ($(if ($series1) { $series1.Times[$label] })) }

# ── 1. benchmark programs: bench_*.ty ───────────────────────────────────────────────
function SameResult($cp, [object[]]$others) {
    if (-not $cp) { return "–" }
    $bad = @($others | Where-Object { $_ -and $_.Result -ne $cp.Result })
    if ($bad.Count -eq 0) { "yes" } else { "NO: $($cp.Result) vs " + (($bad | ForEach-Object { $_.Result }) -join " / ") }
}

$basicRows = @(); $numpyRows = @(); $startupRows = @()
if ($Section -in "all", "programs") {
    foreach ($file in Get-ChildItem $PSScriptRoot -Filter "bench_*.ty" | Where-Object Name -like "*$Filter*" | Sort-Object Name) {
        Write-Host "running $($file.Name) ($Repeat runs per configuration) ..." -ForegroundColor DarkGray
        $m = Measure-Program $file
        if (-not $m) { continue }
        $ty = $m.Ty; $cp = $m.Cp; $aot = $m.Aot
        if ($file.BaseName -eq "bench_startup") {
            # the hot section is ~0 ms here; what matters is the whole process
            $dllDir = Join-Path $buildRoot ($file.Directory.Name + "_" + $file.BaseName)
            $jitSize = (Get-ChildItem $dllDir -Filter "typhon_*.dll" | Measure-Object Length -Sum).Sum / 1MB
            $startupRows += [pscustomobject]@{ Configuration = "CPython"; "Process time" = Fmt $cp.WallMs; "Peak RAM" = FmtMB $cp.RamMB; "What you ship" = "the .py file (needs a Python installation)" }
            $startupRows += [pscustomobject]@{ Configuration = "Typhon (JIT)"; "Process time" = Fmt $ty.WallMs; "Peak RAM" = FmtMB $ty.RamMB; "What you ship" = [string]::Format($inv, "{0:N2} MB .dll (needs the .NET runtime)", $jitSize) }
            $startupRows += [pscustomobject]@{ Configuration = "Typhon (Native AOT)"; "Process time" = (AotFmt $m $(if ($aot) { $aot.WallMs })); "Peak RAM" = (AotFmt $m $(if ($aot) { $aot.RamMB }) -Ram); "What you ship" = $(if ($m.AotSizeMB) { [string]::Format($inv, "{0:N1} MB self-contained .exe", $m.AotSizeMB) } else { "–" }) }
            continue
        }
        foreach ($label in $ty.Times.Keys | Sort-Object) {
            $same = SameResult $cp @($ty, $aot)
            $aotMs = if ($aot) { $aot.Times[$label] } else { $null }
            if ($m.UsesNumpy) {
                $numpyRows += [pscustomobject]@{
                    Benchmark = $label
                    "NumPy" = Fmt $cp.Times[$label]; "NumPy 1 BLAS thread" = Fmt $m.Cp1.Times[$label]
                    "Typhon" = Fmt $ty.Times[$label]; "Typhon 1 thread" = Fmt $m.Ty1.Times[$label]
                    "Typhon AOT" = AotFmt $m $aotMs
                    "Speed-up (best vs best)" = Ratio (Best $cp $m.Cp1 $label) (Best $ty $m.Ty1 $label)
                    "RAM NumPy" = FmtMB $cp.RamMB; "RAM Typhon" = FmtMB $ty.RamMB; "RAM Typhon AOT" = AotFmt $m $(if ($aot) { $aot.RamMB }) -Ram
                    "Same result" = $same
                }
            } else {
                $basicRows += [pscustomobject]@{
                    Benchmark = $label
                    CPython = Fmt $cp.Times[$label]; "Typhon" = Fmt $ty.Times[$label]; "Typhon AOT" = AotFmt $m $aotMs
                    "Speed-up" = Ratio $cp.Times[$label] $ty.Times[$label]; "Speed-up AOT" = $(if ($aotMs) { Ratio $cp.Times[$label] $aotMs } else { "–" })
                    "RAM CPython" = FmtMB $cp.RamMB; "RAM Typhon" = FmtMB $ty.RamMB; "RAM Typhon AOT" = AotFmt $m $(if ($aot) { $aot.RamMB }) -Ram
                    "Process CPython" = Fmt $cp.WallMs; "Process Typhon" = Fmt $ty.WallMs; "Process Typhon AOT" = AotFmt $m $(if ($aot) { $aot.WallMs })
                    "Same result" = $same
                }
            }
        }
    }
}

# ── 2. problems: problems/<name>_loops.ty vs problems/<name>_numpy.ty ────────────────
# The same problem written twice — as plain Python loops and vectorized with NumPy — each run on both runtimes.
$problemRows = @(); $problemRamRows = @()
if ($Section -in "all", "problems") {
    foreach ($loopsFile in Get-ChildItem (Join-Path $PSScriptRoot "problems") -Filter "*_loops.ty" | Where-Object Name -like "*$Filter*" | Sort-Object Name) {
        $name = $loopsFile.BaseName -replace "_loops$", ""
        $numpyFile = Get-Item (Join-Path $loopsFile.DirectoryName "$($name)_numpy.ty")
        Write-Host "running problem '$name' (loops + numpy, $Repeat runs per configuration) ..." -ForegroundColor DarkGray
        $L = Measure-Program $loopsFile
        $N = Measure-Program $numpyFile
        if (-not $L -or -not $N) { continue }

        $cpLoops = $L.Cp.Times[$name]; $tyLoops = $L.Ty.Times[$name]
        $cpNumpy = Best $N.Cp $N.Cp1 $name; $tyNumpy = Best $N.Ty $N.Ty1 $name
        $aotLoops = if ($L.Aot) { $L.Aot.Times[$name] } else { $null }
        $aotNumpy = if ($N.Aot) { $N.Aot.Times[$name] } else { $null }
        $results = @($L.Cp.Result, $L.Ty.Result, $N.Cp.Result, $N.Ty.Result, $(if ($L.Aot) { $L.Aot.Result }), $(if ($N.Aot) { $N.Aot.Result })) | Where-Object { $_ }
        $agree = if (($results | Select-Object -Unique).Count -eq 1) { "all identical" }
                 elseif ($L.Cp.Result -eq $L.Ty.Result -and $N.Cp.Result -eq $N.Ty.Result) { "per implementation" }
                 else { "NO: " + ($results -join " | ") }
        $problemRows += [pscustomobject]@{
            Problem = $name
            "CPython loops" = Fmt $cpLoops; "CPython + NumPy" = Fmt $cpNumpy
            "Typhon loops" = Fmt $tyLoops; "Typhon loops AOT" = AotFmt $L $aotLoops
            "Typhon + NumPy" = Fmt $tyNumpy; "Typhon + NumPy AOT" = AotFmt $N $aotNumpy
            "Typhon loops vs CPython loops" = Ratio $cpLoops $tyLoops
            "Typhon loops vs CPython + NumPy" = Ratio $cpNumpy $tyLoops
            "Typhon + NumPy vs CPython + NumPy" = Ratio $cpNumpy $tyNumpy
            "Results agree" = $agree
        }
        $problemRamRows += [pscustomobject]@{
            Problem = $name
            "CPython loops" = FmtMB $L.Cp.RamMB; "CPython + NumPy" = FmtMB $N.Cp.RamMB
            "Typhon loops" = FmtMB $L.Ty.RamMB; "Typhon loops AOT" = AotFmt $L $(if ($L.Aot) { $L.Aot.RamMB }) -Ram
            "Typhon + NumPy" = FmtMB $N.Ty.RamMB; "Typhon + NumPy AOT" = AotFmt $N $(if ($N.Aot) { $N.Aot.RamMB }) -Ram
        }
    }
}

# ── report ───────────────────────────────────────────────────────────────────────────
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$mem = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
$os = (Get-CimInstance Win32_OperatingSystem).Caption
$envLines = @(
    "- CPU: $($cpu.Name.Trim()) ($($cpu.NumberOfCores) cores / $($cpu.NumberOfLogicalProcessors) threads), $mem GB RAM",
    "- OS: $os",
    "- Typhon: .NET SDK $(& dotnet --version); each program compiled once with ``typhon build --strict`` (``--permissive`` for *_untyped*) and run with ``dotnet app.dll``",
    "- CPython: $(if ($pythonVersion) { $pythonVersion } else { 'not installed' })",
    "- NumPy: $(if ($numpyInfo) { $numpyInfo } else { '–' }); Typhon uses Typhon.NumPy (TensorPrimitives SIMD)",
    "- Method: median of $Repeat runs per configuration. Times are the hot section, measured with time.perf_counter() inside each program. RAM is the peak working set of the whole process. Process is end-to-end wall-clock time (startup + program).",
    "- Typhon compile time (``typhon build``, not included above): median $(Fmt (Median $compileMs)) per program",
    "- Typhon Native AOT: ``typhon build --aot`` (.NET ILCompiler, OptimizationPreference=Speed, InvariantGlobalization); median build time $(Fmt (Median $aotCompileMs)) per program. Programs that use dynamic values cannot be compiled ahead of time and show n/a (dynamic).",
    "- Date: $(Get-Date -Format 'yyyy-MM-dd')"
)

function To-Markdown($rows) {
    if (-not $rows) { return "" }
    $cols = @($rows[0].PSObject.Properties.Name)
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("| " + ($cols -join " | ") + " |")
    $lines.Add("|" + (($cols | ForEach-Object { "---" }) -join "|") + "|")
    foreach ($r in $rows) { $lines.Add("| " + (($cols | ForEach-Object { $r.$_ }) -join " | ") + " |") }
    $lines -join "`n"
}

$md = @(
    "# Benchmark results: CPython vs Typhon",
    "",
    "Generated by ``benchmarks/run.ps1``. The same source file runs on both runtimes; ""Same result"" confirms both printed identical output.",
    "",
    ($envLines -join "`n"),
    "",
    "## Startup and footprint (bench_startup.ty)",
    "",
    (To-Markdown $startupRows),
    "",
    "## Pure Python: CPython vs Typhon",
    "",
    (To-Markdown $basicRows),
    "",
    "## numpy: CPython + NumPy vs Typhon + Typhon.NumPy",
    "",
    "Threading decides these results, so both sides run in two configurations. NumPy's element-wise ufuncs are single-threaded; its matmul and linear algebra use OpenBLAS threads (OPENBLAS_NUM_THREADS=1 makes NumPy fully single-threaded). Typhon.NumPy parallelizes element-wise work above 131,072 elements and its matmul (TYPHON_NUMPY_THREADS=1 makes it single-threaded). ""Best vs best"" divides NumPy's faster configuration by Typhon's faster configuration. RAM is measured in the default configurations.",
    "",
    (To-Markdown $numpyRows),
    "",
    "## Problems: plain loops vs NumPy, on CPython and on Typhon",
    "",
    "Each problem is implemented twice (``problems/<name>_loops.ty`` and ``problems/<name>_numpy.ty``) and each implementation runs on both runtimes. NumPy columns show the faster of the default and single-threaded configurations. ""Typhon loops vs CPython + NumPy"" answers: does a plain loop in Typhon match the vectorized NumPy code a Python developer would write for speed?",
    "",
    (To-Markdown $problemRows),
    "",
    "### Peak RAM per configuration",
    "",
    (To-Markdown $problemRamRows)
) -join "`n"
Set-Content -Path (Join-Path $PSScriptRoot "RESULTS.md") -Value $md -Encoding utf8

Write-Host ""
$envLines | ForEach-Object { Write-Host $_ }
Write-Host "`nPure Python: CPython vs Typhon" -ForegroundColor Cyan
$basicRows | Format-Table -AutoSize | Out-String -Width 250 | Write-Host
Write-Host "numpy: CPython + NumPy vs Typhon + Typhon.NumPy" -ForegroundColor Cyan
$numpyRows | Format-Table -AutoSize | Out-String -Width 250 | Write-Host
Write-Host "Problems: plain loops vs NumPy, on CPython and on Typhon" -ForegroundColor Cyan
$problemRows | Format-Table -AutoSize | Out-String -Width 300 | Write-Host
Write-Host "Startup and footprint" -ForegroundColor Cyan
$startupRows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
$problemRamRows | Format-Table -AutoSize | Out-String -Width 250 | Write-Host
Write-Host "written: benchmarks/RESULTS.md"
