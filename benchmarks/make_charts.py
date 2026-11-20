# Copyright (c) 2026 The Typhon contributors.
# Licensed under the MIT License. See LICENSE file in the project root for full license information.

"""Renders benchmarks/img/*.png from benchmarks/RESULTS.md (written by run.ps1).

    .venv/Scripts/python benchmarks/make_charts.py      (needs: pip install matplotlib)

Chart 1 (speedup.png):  Typhon's speed-up over CPython per benchmark, log scale, centred on 1x (parity);
                        bars right of 1x = Typhon faster (orange), left = CPython/NumPy faster (blue).
Chart 2 (problems.png): the same problem written as plain loops and with NumPy; hot-section time on a log scale
                        for CPython loops (gray context), CPython + NumPy (blue) and Typhon loops (orange).
Chart 3 (compilers.png): Linux, Typhon vs Codon vs Pythran (from RESULTS_LINUX.md).
Chart 4 (oneoff.png):   running a script once, compile time included (from RESULTS_BATTERY.md).

nbody_untyped is left out of charts 1 and 3: those runs predate whole-program type inference, which now types that
program completely (see RESULTS_BATTERY.md).
"""
import math
import re
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.lines import Line2D
from matplotlib.patches import Patch
from matplotlib.ticker import FixedLocator, FuncFormatter, NullFormatter
from matplotlib.transforms import blended_transform_factory

HERE = Path(__file__).resolve().parent
OUT = HERE / "img"

# Palette: brand orange accent + blue (validated as a categorical pair, see dataviz validator), gray = context only.
SURFACE = "#fcfcfb"
INK = "#0b0b0b"
INK_2 = "#52514e"
MUTED = "#898781"
GRID = "#e1e0d9"
AXIS = "#c3c2b7"
TYPHON = "#FA9400"
PYTHON = "#2a78d6"
CONTEXT = "#898781"
JIT_GRAY = "#b4b2ab"   # de-emphasis gray for the JIT bars (context next to the orange AOT bars)

plt.rcParams.update({
    "font.family": ["Segoe UI", "DejaVu Sans"],
    "font.size": 11,
    "text.color": INK,
    "axes.labelcolor": INK_2,
    "xtick.color": MUTED,
    "ytick.color": INK,
    "axes.edgecolor": AXIS,
    "figure.facecolor": SURFACE,
    "axes.facecolor": SURFACE,
    "savefig.facecolor": SURFACE,
})

LABELS = {
    "nbody": "N-body simulation",
    "spectralnorm": "Spectral norm",
    "mandelbrot": "Mandelbrot",
    "fib": "Recursive fib(32)",
    "binarytrees": "Binary trees (allocation)",
    "fannkuch": "Fannkuch (int lists)",
    "primes": "Prime sieve",
    "wordcount": "Word count (str + dict)",
    "sort": "sorted() ints + strings",
    "nbody_untyped": "N-body without type hints",
    "startup": "Tiny script (start-up)",
    "np_loop": "Element loop over an ndarray",
    "np_broadcast": "Broadcasting (pairwise distances)",
    "np_reduce": "Reductions along axes",
    "np_ufuncs": "ufuncs (sqrt, exp, sin)",
    "np_matmul": "Matmul 1000 x 1000",
    "np_linalg": "linalg solve + inv 300 x 300",
}
PROBLEM_LABELS = {
    "dot": "Dot product, 10M floats",
    "movavg": "Moving average, 5M samples",
    "pairwise": "Pairwise distances, 1500 points",
    "mandelbrot": "Mandelbrot, 400 x 400",
    "matmul": "Matrix multiply 200 x 200",
    "pi": "Estimate pi, 5M points",
}


def parse_tables(md: str) -> dict[str, list[dict[str, str]]]:
    """Markdown tables keyed by the '## ' heading above them."""
    tables: dict[str, list[dict[str, str]]] = {}
    heading = ""
    header: list[str] = []
    for line in md.splitlines():
        if line.startswith("#"):
            heading, header = line.lstrip("#").strip(), []
        elif line.startswith("|") and heading:
            cells = [c.strip() for c in line.strip().strip("|").split("|")]
            if not header:
                header = cells
            elif not set("".join(cells)) <= set("-: "):
                tables.setdefault(heading, []).append(dict(zip(header, cells)))
    return tables


def ms(cell: str) -> float:
    m = re.match(r"([\d,\.]+)", cell)
    return float(m.group(1).replace(",", "")) if m else float("nan")


def table(tables: dict, prefix: str) -> list[dict[str, str]]:
    return next(rows for name, rows in tables.items() if name.startswith(prefix))


def fmt_factor(f: float) -> str:
    return f"{f:.0f}x" if f >= 10 else f"{f:.1f}x"


def style_log_x(ax, lo: float, hi: float, ticks: list[float], fmt) -> None:
    ax.set_xscale("log")
    ax.set_xlim(lo, hi)
    ax.xaxis.set_major_locator(FixedLocator(ticks))
    ax.xaxis.set_major_formatter(FuncFormatter(fmt))
    ax.xaxis.set_minor_formatter(NullFormatter())
    ax.grid(axis="x", which="major", color=GRID, linewidth=0.8)
    ax.grid(axis="y", visible=False)
    ax.set_axisbelow(True)
    for side in ("top", "right", "left"):
        ax.spines[side].set_visible(False)
    ax.spines["bottom"].set_color(AXIS)
    ax.tick_params(axis="y", length=0, pad=8)
    ax.tick_params(axis="x", length=0, pad=6, labelsize=10)


# measured before whole-program type inference existed; their numbers no longer describe Typhon
PREDATES_INFERENCE = {"nbody_untyped"}


def speedup_chart(tables: dict) -> Path:
    """Windows: speed-up of Typhon over CPython / NumPy per program, as a Native AOT executable (orange) and with the JIT (gray)."""
    basic = table(tables, "Pure Python")
    numpy_rows = table(tables, "numpy")

    def best(r, *cols):
        vals = [ms(r[c]) for c in cols if c in r and not math.isnan(ms(r[c]))]
        return min(vals) if vals else float("nan")

    pure = [(r["Benchmark"], ms(r["CPython"]) / ms(r["Typhon"]), ms(r["CPython"]) / ms(r["Typhon AOT"])) for r in basic
            if r["Benchmark"] not in PREDATES_INFERENCE]
    nump = [(r["Benchmark"], best(r, "NumPy", "NumPy 1 BLAS thread") / best(r, "Typhon", "Typhon 1 thread"),
             best(r, "NumPy", "NumPy 1 BLAS thread") / ms(r["Typhon AOT"])) for r in numpy_rows]
    key = lambda x: x[2] if not math.isnan(x[2]) else x[1]
    pure.sort(key=key)
    nump.sort(key=key)

    y = 0.0
    rows, headers = [], []
    for title, group in (("NumPy programs: Typhon.NumPy vs NumPy", nump), ("Pure Python programs: Typhon vs CPython", pure)):
        for name, jit, aot in group:
            rows.append((y, LABELS.get(name, name), jit, aot))
            y += 1
        headers.append((y + 0.15, title))
        y += 1.6

    fig, ax = plt.subplots(figsize=(10, 9.2), dpi=180)
    fig.subplots_adjust(left=0.30, right=0.97, top=0.835, bottom=0.105)
    h = 0.34

    def bar(yy, f, color, label_color, weight):
        left, width = (1, f - 1) if f >= 1 else (f, 1 - f)
        ax.barh(yy, width, left=left, height=h, color=color, linewidth=0)
        if f >= 1:
            ax.text(f * 1.07, yy, fmt_factor(f), va="center", ha="left", fontsize=9.5, color=label_color, fontweight=weight)
        elif math.log10(1 / f) >= 0.6:
            # wide enough: label inside the bar (dark ink reads on both the orange and the gray fill)
            ax.text(f * 1.1, yy, f"NumPy {fmt_factor(1 / f)} faster", va="center", ha="left", fontsize=9, color=INK, fontweight=weight)
        else:
            ax.text(f / 1.07, yy, f"NumPy {fmt_factor(1 / f)} faster", va="center", ha="right", fontsize=9.5, color=label_color, fontweight=weight)

    for yy, _, jit, aot in rows:
        if math.isnan(aot):
            ax.text(1.07, yy + 0.19, "Native AOT n/a (dynamic code)", va="center", ha="left", fontsize=8.5, color=MUTED)
        else:
            bar(yy + 0.19, aot, TYPHON, INK, "bold")
        bar(yy - 0.19, jit, JIT_GRAY, INK_2, "normal")
    ax.axvline(1, color=INK_2, linewidth=1.2, zorder=3)
    ax.set_yticks([r[0] for r in rows], [r[1] for r in rows])
    header_tf = blended_transform_factory(fig.transFigure, ax.transData)
    for hy, title in headers:
        ax.text(0.03, hy, title, transform=header_tf, ha="left", va="bottom", fontsize=10.5, color=INK_2, fontweight="bold")
    ax.set_ylim(-0.8, y - 1.1)
    style_log_x(ax, 0.03, 250, [0.1, 0.3, 1, 3, 10, 30, 100], lambda v, _: f"{v:g}x")
    ax.set_xlabel("Speed-up over CPython 3.15 / NumPy 2.5 (hot section, median of 5 runs, log scale; right of 1x = Typhon faster)",
                  fontsize=10, color=INK_2, labelpad=8)

    fig.text(0.03, 0.958, "Same Python source, compiled to .NET", fontsize=19, fontweight="bold", color=INK)
    fig.text(0.03, 0.924, "Typhon vs CPython 3.15 and NumPy 2.5: every file runs unchanged on both, with identical output",
             fontsize=11, color=INK_2)
    fig.legend(handles=[Patch(color=TYPHON, label="Typhon, Native AOT executable"), Patch(color=JIT_GRAY, label="Typhon, .NET JIT")],
               loc="upper left", bbox_to_anchor=(0.025, 0.912), ncol=2, frameon=False, fontsize=10.5, handlelength=1.2)
    fig.text(0.03, 0.012, "Intel Core Ultra 9 285HX, Windows 11. Programs are compiled before the run (compile time: see oneoff.png);\n"
             "NumPy rows use each side's faster thread setting. github.com/saenridanra/typhon", fontsize=8.5, color=MUTED)
    out = OUT / "speedup.png"
    fig.savefig(out)
    plt.close(fig)
    return out


def problems_chart(tables: dict) -> Path:
    rows = table(tables, "Problems")
    rows = sorted(rows, key=lambda r: ms(r["CPython + NumPy"]) / ms(r["Typhon loops"]))

    fig, ax = plt.subplots(figsize=(10, 6.4), dpi=180)
    fig.subplots_adjust(left=0.27, right=0.80, top=0.78, bottom=0.13)
    series = [("CPython, plain loops", "CPython loops", CONTEXT, "o"),
              ("CPython + NumPy (vectorized)", "CPython + NumPy", PYTHON, "s"),
              ("Typhon, plain loops", "Typhon loops", TYPHON, "D")]
    for i, r in enumerate(rows):
        vals = [ms(r[col]) for _, col, _, _ in series]
        ax.plot([min(vals), max(vals)], [i, i], color=AXIS, linewidth=1.2, zorder=1, solid_capstyle="butt")
        for (_, col, color, marker), v in zip(series, vals):
            ax.plot(v, i, marker=marker, markersize=10 if marker != "D" else 9.5, color=color,
                    markeredgecolor=SURFACE, markeredgewidth=2, zorder=3, linestyle="none")
        f = ms(r["CPython + NumPy"]) / ms(r["Typhon loops"])
        verdict = f"{fmt_factor(f)} faster" if f >= 1 else f"{fmt_factor(1 / f)} slower"
        ax.text(1.02, i, verdict, transform=ax.get_yaxis_transform(), va="center", ha="left",
                fontsize=11, color=INK, fontweight="bold")
    ax.text(1.02, len(rows) - 0.35, "Typhon loops\nvs NumPy", transform=ax.get_yaxis_transform(), va="bottom", ha="left",
            fontsize=9.5, color=INK_2)
    ax.set_yticks(range(len(rows)), [PROBLEM_LABELS.get(r["Problem"], r["Problem"]) for r in rows])
    ax.set_ylim(-0.6, len(rows) - 0.4)
    style_log_x(ax, 0.6, 5000, [1, 3, 10, 30, 100, 300, 1000, 3000], lambda v, _: f"{v:g} ms")
    ax.set_xlabel("Time of the hot section (median of 5 runs, log scale, lower is better)", fontsize=10, color=INK_2, labelpad=8)

    fig.text(0.03, 0.94, "Do you still need NumPy?", fontsize=19, fontweight="bold", color=INK)
    fig.text(0.03, 0.895, "The same problem written as plain loops and with NumPy. Plain loops compiled by Typhon vs the vectorized "
             "code\nPython developers write for speed.", fontsize=11, color=INK_2, va="top")
    fig.legend(handles=[Line2D([], [], marker=m, color=c, linestyle="none", markersize=9, markeredgecolor=SURFACE,
                               markeredgewidth=1.5, label=lab) for lab, _, c, m in series],
               loc="upper left", bbox_to_anchor=(0.025, 0.835), ncol=3, frameon=False, fontsize=10.5, handletextpad=0.3)
    fig.text(0.03, 0.03, "Intel Core Ultra 9 285HX, Windows 11, CPython 3.15.2, NumPy 2.5.5 (faster of default / 1 BLAS thread), "
             ".NET 11. github.com/saenridanra/typhon", fontsize=8.5, color=MUTED)
    out = OUT / "problems.png"
    fig.savefig(out)
    plt.close(fig)
    return out


CODON = "#2a78d6"
PYTHRAN = "#1baf7a"


def compilers_chart(tables: dict) -> Path:
    """Linux: speed-up over CPython for three ahead-of-time compilers of Python-syntax code."""
    tools = [("Typhon (Native AOT)", "Typhon AOT", TYPHON, "D"), ("Codon", "Codon", CODON, "s"), ("Pythran", "Pythran", PYTHRAN, "o")]
    groups = []
    for heading, base, title in (("numpy programs", "CPython + NumPy", "NumPy programs"), ("Pure Python", "CPython", "Pure Python programs")):
        rows = []
        for r in table(tables, heading):
            if r["Benchmark"] == "startup" or r["Benchmark"] in PREDATES_INFERENCE:
                continue
            ref = ms(r[base])
            vals = [ref / ms(r[col]) if not math.isnan(ms(r[col])) else float("nan") for _, col, _, _ in tools]
            rows.append((r["Benchmark"], vals))
        rows.sort(key=lambda x: max((v for v in x[1] if not math.isnan(v)), default=0))
        groups.append((title, rows))

    fig, ax = plt.subplots(figsize=(10, 8.6), dpi=180)
    fig.subplots_adjust(left=0.30, right=0.97, top=0.82, bottom=0.115)
    y = 0.0
    ys, names, headers = [], [], []
    for title, rows in groups:
        for name, vals in rows:
            finite = [v for v in vals if not math.isnan(v)]
            if finite:
                ax.plot([min(finite + [1]), max(finite + [1])], [y, y], color=AXIS, linewidth=1.1, zorder=1)
            # dodge the three tools vertically so equal results never hide each other
            for offset, z, ((_, _, color, marker), v) in zip((-0.3, 0.0, 0.3), (5, 4, 3), zip(tools, vals)):
                if not math.isnan(v):
                    ax.plot(v, y + offset, marker=marker, markersize=8 if marker == "D" else 8.5, color=color,
                            markeredgecolor=SURFACE, markeredgewidth=1.5, zorder=z, linestyle="none")
            ys.append(y); names.append(LABELS.get(name, name))
            y += 1
        headers.append((y + 0.15, title))
        y += 1.6
    ax.axvline(1, color=INK_2, linewidth=1.2, zorder=2)
    ax.set_yticks(ys, names)
    header_tf = blended_transform_factory(fig.transFigure, ax.transData)
    for hy, title in headers:
        ax.text(0.03, hy, title, transform=header_tf, ha="left", va="bottom", fontsize=10.5, color=INK_2, fontweight="bold")
    ax.set_ylim(-0.8, y - 1.1)
    style_log_x(ax, 0.03, 600, [0.1, 0.3, 1, 3, 10, 30, 100, 300], lambda v, _: f"{v:g}x")
    ax.set_xlabel("Speed-up over CPython 3.15 (hot section, median of 5 runs, log scale; right of 1x = faster)", fontsize=10, color=INK_2, labelpad=8)

    fig.text(0.03, 0.955, "Three ways to compile Python", fontsize=19, fontweight="bold", color=INK)
    fig.text(0.03, 0.918, "Typhon (Native AOT), Codon and Pythran on the same, unchanged source files, compared with CPython",
             fontsize=11, color=INK_2)
    fig.legend(handles=[Line2D([], [], marker=m, color=c, linestyle="none", markersize=9, markeredgecolor=SURFACE,
                               markeredgewidth=1.5, label=lab) for lab, _, c, m in tools],
               loc="upper left", bbox_to_anchor=(0.025, 0.905), ncol=3, frameon=False, fontsize=10.5, handletextpad=0.3)
    fig.text(0.03, 0.014, "Linux (Ubuntu 26.04 on WSL2), Intel Core Ultra 9 285HX. A missing marker means the tool cannot compile that program\n"
             "(Pythran: no classes; Codon: string type annotations; Typhon AOT: dynamic values). Details: benchmarks/REPORT.md", fontsize=8.5, color=MUTED)
    out = OUT / "compilers.png"
    fig.savefig(out)
    plt.close(fig)
    return out


def oneoff_chart(tables: dict) -> Path:
    """One-off use: end-to-end time of CPython and of `typhon run` on a first run and with the warm compile server."""
    rows = []
    for r in table(tables, "One-off"):
        rows.append((r["Program"], ms(r["CPython"]), ms(r["typhon run, first run"]), ms(r["typhon run, compile server"])))
    rows.sort(key=lambda x: x[1])

    fig, ax = plt.subplots(figsize=(10, 8.4), dpi=180)
    fig.subplots_adjust(left=0.29, right=0.80, top=0.82, bottom=0.12)
    series = [("CPython (python script.py)", PYTHON, "o", PYTHON), ("typhon run, first run (compiles)", TYPHON, "D", SURFACE),
              ("typhon run, warm compile server", TYPHON, "D", TYPHON)]
    for y, (name, cp, first, server) in enumerate(rows):
        vals = (cp, first, server)
        ax.plot([min(vals), max(vals)], [y, y], color=AXIS, linewidth=1.1, zorder=1)
        # CPython slightly above, Typhon slightly below the row line, so equal times never hide each other
        for (_, color, marker, face), v, z, dy in zip(series, vals, (4, 3, 5), (0.16, -0.16, -0.16)):
            ax.plot(v, y + dy, marker=marker, markersize=8.5, color=color, markerfacecolor=face,
                    markeredgecolor=color if face == SURFACE else SURFACE, markeredgewidth=1.8 if face == SURFACE else 1.4,
                    zorder=z, linestyle="none")
        f = cp / server
        if 0.95 <= f <= 1.05: text, color = "level", MUTED
        elif f > 1: text, color = f"{fmt_factor(f)} faster", INK
        else: text, color = f"CPython {fmt_factor(1 / f)} faster", MUTED
        ax.text(1.02, y, text, transform=blended_transform_factory(ax.transAxes, ax.transData), va="center", ha="left", fontsize=9.5, color=color)
    ax.text(1.02, len(rows) - 0.25, "With the server", transform=blended_transform_factory(ax.transAxes, ax.transData),
            va="bottom", ha="left", fontsize=9.5, color=INK_2, fontweight="bold")
    ax.set_yticks(range(len(rows)), [LABELS.get(r[0], r[0]) for r in rows])
    ax.set_ylim(-0.7, len(rows) - 0.3)
    style_log_x(ax, 25, 5000, [30, 100, 300, 1000, 3000], lambda v, _: f"{v / 1000:g} s" if v >= 1000 else f"{v:g} ms")
    ax.set_xlabel("End-to-end process time, start to exit (median of 5 runs, log scale)", fontsize=10, color=INK_2, labelpad=8)

    fig.text(0.03, 0.955, "Running a script: CPython vs typhon run", fontsize=19, fontweight="bold", color=INK)
    fig.text(0.03, 0.918, "The first run compiles in the process; after that a warm compile server (or the script cache) does it",
             fontsize=11, color=INK_2)
    fig.legend(handles=[Line2D([], [], marker=m, color=c, markerfacecolor=f, markeredgecolor=c if f == SURFACE else SURFACE,
                               markeredgewidth=1.8 if f == SURFACE else 1.4, linestyle="none", markersize=9, label=lab)
                        for lab, c, m, f in series],
               loc="upper left", bbox_to_anchor=(0.025, 0.905), ncol=3, frameon=False, fontsize=9.5, handletextpad=0.3, columnspacing=1.2)
    fig.text(0.03, 0.014, "Intel Core Ultra 9 285HX, Windows 11, laptop on battery; Typhon CLI published with ReadyToRun.\n"
             "A cached, unchanged script also skips the server's compile step. Raw numbers: benchmarks/RESULTS_BATTERY.md", fontsize=8.5, color=MUTED)
    out = OUT / "oneoff.png"
    fig.savefig(out)
    plt.close(fig)
    return out


if __name__ == "__main__":
    OUT.mkdir(exist_ok=True)
    tables = parse_tables((HERE / "RESULTS.md").read_text(encoding="utf-8-sig"))
    paths = [speedup_chart(tables), problems_chart(tables)]
    linux = HERE / "RESULTS_LINUX.md"
    if linux.exists():
        paths.append(compilers_chart(parse_tables(linux.read_text(encoding="utf-8-sig"))))
    battery = HERE / "RESULTS_BATTERY.md"
    if battery.exists():
        paths.append(oneoff_chart(parse_tables(battery.read_text(encoding="utf-8-sig"))))
    for path in paths:
        print("written:", path.relative_to(HERE.parent))
