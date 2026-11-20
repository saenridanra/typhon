#!/usr/bin/env bash
# Copyright (c) 2026 The Typhon contributors.
# Licensed under the MIT License. See LICENSE file in the project root for full license information.

# Benchmarks CPython (+ NumPy), Codon, Pythran and Typhon (JIT and Native AOT) on Linux / WSL2: hot-section time,
# end-to-end process time and peak RAM. Writes benchmarks/RESULTS_LINUX.md.
#
# Protocol (the same as benchmarks/run.ps1):
#   * every benchmarks/bench_*.ty and benchmarks/problems/*_loops.ty / *_numpy.ty is valid Python and valid Typhon; each
#     times its own hot section with time.perf_counter() and prints one result line plus "TIME <label> <ms> ms";
#   * every compiler builds each program once before timing; compile times are reported separately;
#   * every configuration runs -n times and medians are reported; each result line is compared with CPython's;
#   * numpy programs: CPython + NumPy and Pythran also run with OPENBLAS_NUM_THREADS=1, Typhon (JIT) with
#     TYPHON_NUMPY_THREADS=1, and the faster configuration counts ("best vs best"). Codon and Typhon AOT run with their
#     default settings only;
#   * peak RAM is the max RSS and process time the wall-clock time of the whole process (via /usr/bin/time).
# Per tool:
#   * CPython: the venv's python runs the .ty file as is.
#   * Codon:   `codon build -release -exe` compiles the whole file (Codon ships its own NumPy implementation).
#   * Pythran: compiles exported top-level functions only (no classes, no module-level code). A file with
#              "#pythran export" comment lines is split into a kernel module <name>_pythran (the file without its
#              `if __name__ == "__main__":` block, compiled with pythran) and a driver (the original file with
#              `from <name>_pythran import <exported names>` inserted right before the main block) that the venv's
#              python runs, so the unchanged main block times the compiled functions exactly like the CPython run.
#   * Typhon:  `typhon build --strict` (--permissive for *_untyped*) run with `dotnet app.dll`; Native AOT via
#              `typhon build --aot` (needs clang + zlib1g-dev).
#
# Expected setup (override with the environment variables in brackets):
#   CPython venv with numpy and pythran   ~/bench-venv/bin/python     [PYTHON]
#   Codon                                 ~/.codon/bin/codon          [CODON]
#   .NET SDK                              ~/.dotnet                   [DOTNET_ROOT]
#   clang / clang++ (Pythran's compiler [PYTHRAN_CC, PYTHRAN_CXX], Native AOT linker), /usr/bin/time, rsync
#   The repository is copied to the Linux filesystem, ~/typhon [TYPHON_LINUX_SRC], and the Typhon CLI is built there;
#   all build outputs go to ~/benchwork/run [BENCH_WORK]. Nothing is built inside /mnt/*.
#
# usage: ./run_linux.sh [-n 3] [-f filter] [-s all|programs|problems] [-t run_timeout_s] [--build-timeout s]
#                       [--tools cpython,codon,pythran,typhon,aot] [--no-aot] [--build-only] [--reuse-builds] [--skip-sync]
#   -n               runs per configuration (median reported)
#   -f               only files whose name contains this text
#   -t               timeout per run in seconds (default 600); --build-timeout per build (default 900)
#   --build-only     only compile every program (records compile times); no measurements, no results file
#   --reuse-builds   reuse compiled programs from an earlier run when newer than their source (compile time from then)
#   --skip-sync      do not copy the repository to the Linux filesystem / rebuild the Typhon CLI
# A full run (-n 5) takes about 10 minutes; it can be split into `--build-only` followed by
# `-n 5 --reuse-builds --skip-sync` (about 5 minutes each).

set -uo pipefail

N=3; FILTER=""; SECTION=all; TIMEOUT=600; BUILD_TIMEOUT=900; TOOLS="cpython,codon,pythran,typhon,aot"; REUSE=0; SYNC=1; BUILD_ONLY=0

usage() { sed -n '/^# usage:/,/^$/p' "${BASH_SOURCE[0]}" | sed -e 's/^# \{0,1\}//'; }
while (( $# )); do
    case $1 in
        -n|--repeat) N=$2; shift 2 ;;
        -f|--filter) FILTER=$2; shift 2 ;;
        -s|--section) SECTION=$2; shift 2 ;;
        -t|--timeout) TIMEOUT=$2; shift 2 ;;
        --build-timeout) BUILD_TIMEOUT=$2; shift 2 ;;
        --tools) TOOLS=$2; shift 2 ;;
        --no-aot) TOOLS=${TOOLS//aot/}; shift ;;
        --reuse-builds) REUSE=1; shift ;;
        --build-only) BUILD_ONLY=1; shift ;;
        --skip-sync) SYNC=0; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done
case $SECTION in all|programs|problems) ;; *) echo "-s must be all, programs or problems" >&2; exit 2 ;; esac

HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO=$(cd "$HERE/../.." && pwd)
SRC=${TYPHON_LINUX_SRC:-$HOME/typhon}
WORK=${BENCH_WORK:-$HOME/benchwork/run}
PY=${PYTHON:-$HOME/bench-venv/bin/python}
PYTHRAN=${PYTHRAN:-$(dirname "$PY")/pythran}
CODON=${CODON:-$HOME/.codon/bin/codon}
export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet}
export PATH="$DOTNET_ROOT:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
DOTNET=$DOTNET_ROOT/dotnet
PYTHRAN_CC=${PYTHRAN_CC:-clang}
PYTHRAN_CXX=${PYTHRAN_CXX:-clang++}
PYTHRAN_FLAGS=${PYTHRAN_FLAGS:--O3 -march=native}
# g++ 15 needs >11 GB to compile some Pythran kernels at -O2/-O3 (OOM in WSL); clang++ needs ~250 MB. The cap keeps a
# runaway C++ compile from taking down the WSL VM.
PYTHRAN_VMEM_KB=${PYTHRAN_VMEM_KB:-6000000}
RESULTS=$REPO/benchmarks/RESULTS_LINUX.md
TMPD=$(mktemp -d)
trap 'rm -rf "$TMPD"' EXIT

log() { printf '%s\n' "$*" >&2; }
die() { log "error: $*"; exit 1; }
want() { [[ ",$TOOLS," == *",$1,"* ]]; }

declare -A HOT WALL RSS RES LABEL STATUS BUILD_MS SIZE USES_NUMPY MISSING
NOTES=()        # "file · tool: reason" lines for the n/a section
TOOL_IDS=(cpython codon pythran typhon aot)
declare -A TNAME=([cpython]="CPython" [codon]="Codon" [pythran]="Pythran" [typhon]="Typhon" [aot]="Typhon AOT")

# ── tools ────────────────────────────────────────────────────────────────────────────
[[ -x /usr/bin/time ]] || die "/usr/bin/time not found (apt install time)"
[[ -x $PY ]] || { log "warning: CPython not found at $PY"; MISSING[cpython]=1; MISSING[pythran]=1; }
[[ -z ${MISSING[pythran]:-} && ! -x $PYTHRAN ]] && { log "warning: pythran not found at $PYTHRAN"; MISSING[pythran]=1; }
[[ -x $CODON ]] || { log "warning: codon not found at $CODON"; MISSING[codon]=1; }
[[ -x $DOTNET ]] || { log "warning: dotnet not found at $DOTNET"; MISSING[typhon]=1; MISSING[aot]=1; }
command -v clang >/dev/null || { MISSING[aot]=1; log "warning: clang not found; Native AOT skipped"; }
command -v "$PYTHRAN_CXX" >/dev/null || { MISSING[pythran]=1; log "warning: $PYTHRAN_CXX not found; Pythran skipped"; }

if (( SYNC )) && [[ $REPO != "$SRC" ]]; then
    command -v rsync >/dev/null || die "rsync not found (apt install rsync)"
    log "copying $REPO -> $SRC"
    mkdir -p "$SRC"
    rsync -a --delete --exclude bin/ --exclude obj/ --exclude .venv/ --exclude artifacts/ --exclude .git/ \
        --exclude __pycache__/ "$REPO/" "$SRC/" || die "rsync failed"
fi
TYPHON=""
if [[ -z ${MISSING[typhon]:-} ]] && { want typhon || want aot; }; then
    if (( SYNC )); then
        log "building the Typhon CLI (Release) in $SRC"
        "$DOTNET" build "$SRC/src/Typhon.Cli" -c Release -nologo -v q >"$TMPD/cli.log" 2>&1 \
            || { cat "$TMPD/cli.log" >&2; die "building the Typhon CLI failed"; }
    fi
    TYPHON=$(find "$SRC/src/Typhon.Cli/bin/Release" -maxdepth 2 -type f -name typhon -perm -u+x | head -n1)
    [[ -n $TYPHON ]] || die "Typhon CLI not found under $SRC/src/Typhon.Cli/bin/Release (run without --skip-sync)"
fi
BENCH=$SRC/benchmarks
[[ -d $BENCH ]] || die "$BENCH not found (run without --skip-sync)"
mkdir -p "$WORK"

# ── helpers ──────────────────────────────────────────────────────────────────────────
median() { printf '%s\n' "$@" | LC_ALL=C sort -g | LC_ALL=C awk '{ a[NR] = $1 } END { if (NR == 0) exit; if (NR % 2) print a[(NR + 1) / 2]; else printf "%.3f\n", (a[NR / 2] + a[NR / 2 + 1]) / 2 }'; }
min_of() { if [[ -z ${1:-} ]]; then echo "${2:-}"; elif [[ -z ${2:-} ]]; then echo "$1"; else LC_ALL=C awk -v a="$1" -v b="$2" 'BEGIN { print (a + 0 < b + 0) ? a : b }'; fi; }
group() { LC_ALL=C awk -v s="$1" 'BEGIN { i = index(s, "."); ip = i ? substr(s, 1, i - 1) : s; fp = i ? substr(s, i) : ""; o = ""; while (length(ip) > 3) { o = "," substr(ip, length(ip) - 2) o; ip = substr(ip, 1, length(ip) - 3) } print ip o fp }'; }
fmt_ms() { [[ -z ${1:-} ]] && { echo "–"; return; }; echo "$(group "$(LC_ALL=C awk -v x="$1" 'BEGIN { printf "%.1f", x }')") ms"; }
fmt_mb() { [[ -z ${1:-} ]] && { echo "–"; return; }; echo "$(group "$(LC_ALL=C awk -v x="$1" 'BEGIN { printf "%.0f", x / 1024 }')") MB"; }
ratio() {  # baseline value -> "12.3x" (how many times faster than the baseline)
    [[ -z ${1:-} || -z ${2:-} ]] && { echo "–"; return; }
    LC_ALL=C awk -v a="$1" -v b="$2" 'BEGIN { if (b + 0 == 0) { print "–"; exit } r = a / b; printf (r < 0.1 ? "%.2fx" : "%.1fx"), r }'
}
oneline() { tr -d '\r' | tr '\n|' ' /' | sed -e 's/  */ /g' -e 's/^ //' -e 's/ $//' | cut -c1-220; }
result_of() { tr -d '\r' < "$1" | awk '!/^TIME / && NF { if (n++) printf " / "; printf "%s", $0 }'; }

# limited SECONDS GROUP OUT ERR [VAR=VALUE ...] -- cmd args...   runs cmd with VAR=VALUE exported and a watchdog;
# sets R_RC (124 on timeout) and R_WALL_MS. No `timeout`/`env` in the measured path: on Ubuntu 26.04 `timeout` adds
# ~100 ms to every process. GROUP=1 starts cmd in its own session (setsid) so a timeout kills the whole process tree
# (builds); GROUP=0 kills cmd's direct children (cmd is /usr/bin/time, its child the program).
limited() {
    local secs=$1 group=$2 out=$3 err=$4; shift 4
    local envs=() e pid spid done_pid="" rc t0 t1
    while [[ $1 != -- ]]; do envs+=("$1"); shift; done
    shift
    (( group )) && set -- setsid "$@"
    t0=${EPOCHREALTIME/[.,]/}
    ( for e in "${envs[@]}"; do export "$e"; done; exec "$@" ) >"$out" 2>"$err" &
    pid=$!
    sleep "$secs" &
    spid=$!
    wait -n -p done_pid "$pid" "$spid"
    rc=$?
    t1=${EPOCHREALTIME/[.,]/}
    if [[ $done_pid == "$pid" ]]; then
        kill "$spid" 2>/dev/null; wait "$spid" 2>/dev/null
        R_RC=$rc
    else
        if (( group )); then kill -TERM -- "-$pid" 2>/dev/null; sleep 3; kill -KILL -- "-$pid" 2>/dev/null
        else pkill -TERM -P "$pid"; sleep 3; pkill -KILL -P "$pid"; kill -KILL "$pid" 2>/dev/null; fi
        wait "$pid" 2>/dev/null
        R_RC=124
    fi
    R_WALL_MS=$(LC_ALL=C awk -v a="$t0" -v b="$t1" 'BEGIN { printf "%.3f", (b - a) / 1000 }')
}

# run_once OUT ERR [VAR=VALUE ...] -- cmd args...   sets R_RC R_WALL_MS R_RSS_KB
run_once() {
    local out=$1 err=$2; shift 2
    local envs=()
    while [[ $1 != -- ]]; do envs+=("$1"); shift; done
    shift
    rm -f "$TMPD/rss"
    limited "$TIMEOUT" 0 "$out" "$err" "${envs[@]}" -- /usr/bin/time -f '%M' -o "$TMPD/rss" "$@"
    R_RSS_KB=$(tail -n1 "$TMPD/rss" 2>/dev/null)
}

# series KEY [VAR=VALUE ...] -- cmd args...   N runs; medians into HOT/WALL/RSS, first result line into RES
series() {
    local key=$1; shift
    local hots=() walls=() rsss=() i h
    (( BUILD_ONLY )) && return 0
    for (( i = 0; i < N; i++ )); do
        run_once "$TMPD/out" "$TMPD/err" "$@"
        if (( R_RC != 0 )); then
            if (( R_RC == 124 || R_RC == 137 )); then STATUS[$key]="timeout"; note "$key" "timeout (> ${TIMEOUT} s per run)"
            else STATUS[$key]="failed"; note "$key" "exit code $R_RC: $( (tail -n 4 "$TMPD/err"; tail -n 2 "$TMPD/out") | oneline)"; fi
            log "   ${key#*|}: ${STATUS[$key]}"
            return 1
        fi
        h=$(tr -d '\r' < "$TMPD/out" | awk '/^TIME / { print $3; exit }')
        if [[ -z $h ]]; then STATUS[$key]="failed"; note "$key" "no TIME line in the output: $(oneline < "$TMPD/out")"; return 1; fi
        hots+=("$h"); walls+=("$R_WALL_MS"); rsss+=("$R_RSS_KB")
        if (( i == 0 )); then
            RES[$key]=$(result_of "$TMPD/out")
            LABEL[$key]=$(tr -d '\r' < "$TMPD/out" | awk '/^TIME / { print $2; exit }')
        fi
    done
    HOT[$key]=$(median "${hots[@]}"); WALL[$key]=$(median "${walls[@]}"); RSS[$key]=$(median "${rsss[@]}")
    log "   ${key#*|}: $(fmt_ms "${HOT[$key]}") (process $(fmt_ms "${WALL[$key]}"), $(fmt_mb "${RSS[$key]}"))"
}

# build KEY ARTIFACT LOG SOURCE -- cmd args...   compiles once (timed) into ARTIFACT
build() {
    local key=$1 art=$2 logf=$3 srcf=$4; shift 5
    if (( REUSE )) && [[ -e $art && -f $art.build_ms && $art -nt $srcf ]]; then
        BUILD_MS[$key]=$(<"$art.build_ms"); log "   ${key#*|}: reusing build"; return 0
    fi
    rm -f "$art" "$art.build_ms"
    mkdir -p "$(dirname "$logf")"
    limited "$BUILD_TIMEOUT" 1 "$logf" "$logf.err" -- "$@"
    cat "$logf.err" >> "$logf"; rm -f "$logf.err"
    if (( R_RC == 0 )) && [[ -e $art ]]; then
        BUILD_MS[$key]=$R_WALL_MS; echo "$R_WALL_MS" > "$art.build_ms"
        log "   ${key#*|}: built in $(fmt_ms "$R_WALL_MS")"
        return 0
    fi
    (( R_RC == 124 )) && echo "error: build timed out after ${BUILD_TIMEOUT} s" >> "$logf"
    return 1
}

declare -A DISPLAY  # id -> file path shown in notes
note() { local id=${1%%|*} tool=${1#*|}; tool=${tool%1}; NOTES+=("${DISPLAY[$id]} · ${TNAME[$tool]}$([[ ${1#*|} == *1 ]] && echo ' (1 thread)'): $2"); }
na() { STATUS[$1]="n/a ($2)"; note "$1" "$3"; log "   ${1#*|}: n/a ($2)"; }

codon_reason() {  # LOG SOURCE -> "line 11: expected type expression at `"Node"`"
    local line ln c1 c2 msg src
    line=$(sed 's/\x1b\[[0-9;]*m//g' "$1" | grep -m1 'error:')
    if [[ $line =~ :([0-9]+)\ \(([0-9]+)-([0-9]+)\):\ error:\ (.*)$ ]]; then
        ln=${BASH_REMATCH[1]}; c1=${BASH_REMATCH[2]}; c2=${BASH_REMATCH[3]}; msg=${BASH_REMATCH[4]}
        src=$(sed -n "${ln}p" "$2" | tr -d '\r')
        echo "compile error at line $ln: $msg at \`${src:c1-1:c2-c1}\` (in \`$(echo "$src" | sed 's/^ *//' | cut -c1-90)\`)"
    else
        echo "compile error: $(sed 's/\x1b\[[0-9;]*m//g' "$1" | { grep -m1 -i 'error' || tail -n 2 "$1"; } | oneline)"
    fi
}

aot_dynamic_reason() {  # LOG SOURCE -> where typhon build --aot found late-bound code
    local lines n l out=""
    lines=$(grep -oP '\(\K[0-9]+(?=\): error TY2001)' "$1" | sort -nu)
    n=$(grep -c 'error TY2001' "$1")
    if (( $(echo "$lines" | grep -c .) <= 3 )); then
        for l in $lines; do out+="${out:+; }line $l \`$(sed -n "${l}p" "$2" | tr -d '\r' | sed 's/^ *//' | cut -c1-80)\`"; done
        echo "Native AOT cannot run late-bound (dynamic) code; typhon build --aot reports TY2001 at $out"
    else
        echo "Native AOT cannot run late-bound (dynamic) code; typhon build --aot reports TY2001 at $n places on $(echo "$lines" | grep -c .) lines (unannotated program)"
    fi
}

pythran_reason() {  # SOURCE BASENAME -> why there is no "#pythran export" in this file
    case $2 in
        bench_np_linalg) echo "np.linalg|numpy.linalg.solve / numpy.linalg.inv are not supported by Pythran (\"Attribute 'solve' unknown\")"; return ;;
    esac
    local classes
    classes=$(grep -oP '^class \K\w+' "$1" | paste -sd, - | sed 's/,/, /g')
    if [[ -n $classes ]]; then echo "classes|uses classes ($classes); Pythran compiles top-level functions only"
    else echo "main block|the hot section is inline in the \`if __name__ == \"__main__\":\` block; there is no top-level function to export"; fi
}

# ── measurement of one file with every tool ──────────────────────────────────────────
measure_file() {
    local f=$1 base dir id wd mode numpy=0 key
    base=$(basename "$f" .ty); dir=$(basename "$(dirname "$f")")
    id=${dir}_$base; wd=$WORK/$id
    DISPLAY[$id]=${f#"$SRC"/}
    grep -q '^import numpy' "$f" && numpy=1
    USES_NUMPY[$id]=$numpy
    mode=--strict; [[ $base == *untyped* ]] && mode=--permissive
    log "== ${DISPLAY[$id]}"
    for t in "${TOOL_IDS[@]}"; do [[ -n ${MISSING[$t]:-} ]] && STATUS[$id|$t]="not installed"; done

    if want cpython && [[ -z ${MISSING[cpython]:-} ]]; then
        series "$id|cpython" -- "$PY" "$f"
        (( numpy )) && series "$id|cpython1" OPENBLAS_NUM_THREADS=1 -- "$PY" "$f"
    fi

    if want codon && [[ -z ${MISSING[codon]:-} ]]; then
        local cexe=$wd/codon/$base
        mkdir -p "$wd/codon"
        if build "$id|codon" "$cexe" "$wd/codon/build.log" "$f" -- "$CODON" build -release -exe -o "$cexe" "$f"; then
            SIZE[$id|codon]=$(stat -c %s "$cexe")
            series "$id|codon" -- "$cexe"
        else
            na "$id|codon" "compile error" "$(codon_reason "$wd/codon/build.log" "$f")"
        fi
    fi

    if want pythran && [[ -z ${MISSING[pythran]:-} ]]; then
        local names pd=$wd/pythran mod=${base}_pythran r
        names=$(tr -d '\r' < "$f" | grep -oP '^#pythran export \K\w+' | sort -u | paste -sd, - | sed 's/,/, /g')
        if [[ -z $names ]]; then
            r=$(pythran_reason "$f" "$base"); na "$id|pythran" "${r%%|*}" "${r#*|}"
        else
            mkdir -p "$pd"
            sed '/^if __name__ == "__main__":/,$d' "$f" > "$pd/$mod.py"
            awk -v imp="from $mod import $names" '/^if __name__ == "__main__":/ { print imp } { print }' "$f" > "$pd/${base}_driver.py"
            if build "$id|pythran" "$pd/$mod.so" "$pd/build.log" "$f" -- bash -c \
                'cd "$1" && ulimit -v "$2" && export CC="$3" CXX="$4" && exec "$5" $6 "$7.py" -o "$7.so"' \
                _ "$pd" "$PYTHRAN_VMEM_KB" "$PYTHRAN_CC" "$PYTHRAN_CXX" "$PYTHRAN" "$PYTHRAN_FLAGS" "$mod"; then
                series "$id|pythran" -- "$PY" "$pd/${base}_driver.py"
                (( numpy )) && series "$id|pythran1" OPENBLAS_NUM_THREADS=1 -- "$PY" "$pd/${base}_driver.py"
            else
                na "$id|pythran" "build failed" "pythran build failed: $( (grep -m1 -E ' error: |E: ' "$pd/build.log" || tail -n 2 "$pd/build.log") | oneline)"
            fi
        fi
    fi

    if want typhon && [[ -n $TYPHON ]]; then
        local dll=$wd/typhon/typhon_$base.dll
        if build "$id|typhon" "$dll" "$wd/typhon.build.log" "$f" -- "$TYPHON" build "$f" "$mode" -o "$wd/typhon"; then
            SIZE[$id|typhon]=$(stat -c %s "$dll")
            series "$id|typhon" -- "$DOTNET" "$dll"
            (( numpy )) && series "$id|typhon1" TYPHON_NUMPY_THREADS=1 -- "$DOTNET" "$dll"
        else
            na "$id|typhon" "build failed" "typhon build failed: $(grep -m1 -E 'error' "$wd/typhon.build.log" | oneline)"
        fi
    fi

    if want aot && [[ -n $TYPHON && -z ${MISSING[aot]:-} ]]; then
        local aexe=$wd/aot/typhon_$base
        if build "$id|aot" "$aexe" "$wd/aot.build.log" "$f" -- "$TYPHON" build "$f" "$mode" --aot -o "$wd/aot"; then
            SIZE[$id|aot]=$(stat -c %s "$aexe")
            series "$id|aot" -- "$aexe"
        elif grep -q TY2001 "$wd/aot.build.log"; then
            na "$id|aot" "dynamic" "$(aot_dynamic_reason "$wd/aot.build.log" "$f")"
        else
            na "$id|aot" "build failed" "typhon build --aot failed: $(grep -m1 -E 'error' "$wd/aot.build.log" | oneline)"
        fi
    fi
}

# ── table cells ──────────────────────────────────────────────────────────────────────
status_of() { echo "${STATUS[$1]:-${STATUS[${1%1}]:-–}}"; }   # a 1-thread configuration inherits its tool's status
best_hot() { min_of "${HOT[$1|$2]:-}" "${HOT[$1|${2}1]:-}"; }          # faster of default / 1-thread configuration
label_of() { local t; for t in cpython typhon codon pythran aot; do [[ -n ${LABEL[$1|$t]:-} ]] && { echo "${LABEL[$1|$t]}"; return; }; done; echo "${1#*_}"; }
tcell() {  # ID TOOL [BASELINE_MS]: best hot time (+ speed-up vs baseline)
    local v; v=$(best_hot "$1" "$2")
    [[ -z $v ]] && { status_of "$1|$2"; return; }
    if [[ -n ${3:-} ]]; then echo "$(fmt_ms "$v") ($(ratio "$3" "$v"))"; else fmt_ms "$v"; fi
}
tcell1() { [[ -n ${HOT[$1|$2]:-} ]] && fmt_ms "${HOT[$1|$2]}" || status_of "$1|$2"; }    # one configuration, no "best"
wcell() { [[ -n ${WALL[$1|$2]:-} ]] && fmt_ms "${WALL[$1|$2]}" || status_of "$1|$2"; }
mcell() { [[ -n ${RSS[$1|$2]:-} ]] && fmt_mb "${RSS[$1|$2]}" || status_of "$1|$2"; }

same_result() {  # ID: "yes" when every configuration printed CPython's result line
    local id=$1 ref=${RES[$1|cpython]:-} bad=() t k
    [[ -z $ref ]] && { echo "–"; return; }
    for t in "${TOOL_IDS[@]}"; do
        for k in "$id|$t" "$id|${t}1"; do
            [[ -n ${RES[$k]+x} && ${RES[$k]} != "$ref" ]] || continue
            bad+=("${TNAME[$t]}$([[ $k == *1 ]] && echo ' 1 thread') \`${RES[$k]}\`")
        done
    done
    if (( ${#bad[@]} == 0 )); then echo "yes"; else local IFS=';'; echo "NO: CPython \`$ref\` vs ${bad[*]}"; fi
}

agree() {  # LOOPS_ID NUMPY_ID
    local all=() lo=() np=() t k
    for t in "${TOOL_IDS[@]}"; do
        for k in "$1|$t" "$1|${t}1"; do [[ -n ${RES[$k]+x} ]] && { all+=("${RES[$k]}"); lo+=("${RES[$k]}"); }; done
        for k in "$2|$t" "$2|${t}1"; do [[ -n ${RES[$k]+x} ]] && { all+=("${RES[$k]}"); np+=("${RES[$k]}"); }; done
    done
    uniq_count() { printf '%s\n' "$@" | sort -u | grep -c .; }
    if (( ${#all[@]} == 0 )); then echo "–"
    elif (( $(uniq_count "${all[@]}") == 1 )); then echo "all identical"
    elif (( $(uniq_count "${lo[@]}") == 1 && $(uniq_count "${np[@]}") == 1 )); then echo "per implementation"
    else
        local d="" r
        for t in "${TOOL_IDS[@]}"; do
            for k in "$1|$t" "$2|$t" "$1|${t}1" "$2|${t}1"; do
                [[ -n ${RES[$k]+x} ]] || continue
                r="${TNAME[$t]} $([[ ${k%%|*} == "$1" ]] && echo loops || echo numpy)$([[ $k == *1 ]] && echo ' 1 thread'): \`${RES[$k]}\`"
                d+="${d:+; }$r"
            done
        done
        echo "NO: $d"
    fi
}

US=$'\x1f'   # cell separator between row() and md_table
md_table() {  # header cells as arguments; rows (from row) on stdin
    local h line c cells
    printf '|'; for h in "$@"; do printf ' %s |' "$h"; done; printf '\n|'
    for h in "$@"; do printf -- '---|'; done; printf '\n'
    while IFS= read -r line; do
        IFS=$US read -r -a cells <<< "$line"
        printf '|'; for c in "${cells[@]}"; do printf ' %s |' "$c"; done; printf '\n'
    done
}
row() { local IFS=$US; echo "$*"; }

# ── 1. programs: bench_*.ty ──────────────────────────────────────────────────────────
PROGRAMS=()
if [[ $SECTION == all || $SECTION == programs ]]; then
    for f in "$BENCH"/bench_*.ty; do
        [[ -e $f && $(basename "$f") == *"$FILTER"* ]] || continue
        measure_file "$f"
        PROGRAMS+=("benchmarks_$(basename "$f" .ty)")
    done
fi

# ── 2. problems: problems/<name>_loops.ty vs problems/<name>_numpy.ty ──────────────
PROBLEMS=()
if [[ $SECTION == all || $SECTION == problems ]]; then
    for f in "$BENCH"/problems/*_loops.ty; do
        [[ -e $f && $(basename "$f") == *"$FILTER"* ]] || continue
        name=$(basename "$f" _loops.ty)
        [[ -e $BENCH/problems/${name}_numpy.ty ]] || { log "warning: ${name}_numpy.ty missing"; continue; }
        measure_file "$f"
        measure_file "$BENCH/problems/${name}_numpy.ty"
        PROBLEMS+=("$name")
    done
fi

# ── report ───────────────────────────────────────────────────────────────────────────
median_build() { local v=() k; for k in "${!BUILD_MS[@]}"; do [[ ${k#*|} == "$1" ]] && v+=("${BUILD_MS[$k]}"); done; (( ${#v[@]} )) && echo "$(fmt_ms "$(median "${v[@]}")") (${#v[@]} programs)" || echo "–"; }
if (( BUILD_ONLY )); then
    log ""
    log "build only; median compile time per program: Codon $(median_build codon) · Pythran $(median_build pythran) · Typhon $(median_build typhon) · Typhon AOT $(median_build aot)"
    if (( ${#NOTES[@]} )); then log "not built:"; printf -- '  - %s\n' "${NOTES[@]}" >&2; fi
    log "next: ./run_linux.sh -n 5 --reuse-builds --skip-sync"
    exit 0
fi
log "writing $RESULTS"
ver() { "$@" 2>/dev/null | head -n1; }
cpu=$(lscpu | sed -n 's/^Model name:[[:space:]]*//p' | head -n1)
mem_gb=$(free -g | awk '/^Mem:/ { print $2 }')
os=$(. /etc/os-release && echo "$PRETTY_NAME")
grep -qi microsoft /proc/version && os+=" on WSL2"
py_ver=$( [[ -z ${MISSING[cpython]:-} ]] && "$PY" -c 'import sys; print("Python", sys.version.split()[0])' 2>/dev/null)
np_info=$( [[ -z ${MISSING[cpython]:-} ]] && "$PY" -c "import numpy; c = numpy.show_config(mode='dicts'); b = c['Build Dependencies']['blas']; print('NumPy', numpy.__version__, '+', b['name'], b.get('version', ''))" 2>/dev/null)
pythran_ver=$( [[ -z ${MISSING[pythran]:-} ]] && "$PY" -c 'import pythran; print(pythran.__version__)' 2>/dev/null)
codon_ver=$( [[ -z ${MISSING[codon]:-} ]] && ver "$CODON" --version)
codon_blas=$( [[ -z ${MISSING[codon]:-} ]] && strings "$(dirname "$(readlink -f "$CODON")")/../lib/codon/libcodonrt.so" 2>/dev/null | grep -m1 -oE 'OpenBLAS [0-9.]+')
dotnet_sdk=$( [[ -x $DOTNET ]] && (cd "$SRC" && "$DOTNET" --version 2>/dev/null))
dotnet_rt=$( [[ -x $DOTNET ]] && "$DOTNET" --list-runtimes 2>/dev/null | awk '/Microsoft.NETCore.App/ { v = $2 } END { print v }')
typhon_rev=$(git -c safe.directory='*' -C "$REPO" rev-parse --short HEAD 2>/dev/null)
[[ -n $typhon_rev && -n $(git -c safe.directory='*' -C "$REPO" status --porcelain -- src 2>/dev/null | head -n1) ]] && typhon_rev+=" + local changes in src/"
clang_ver=$(ver clang --version)

{
echo "# Benchmark results on Linux: CPython, Codon, Pythran and Typhon"
echo
echo "Generated by \`benchmarks/linux/run_linux.sh\`. The same source files as \`benchmarks/run.ps1\` (Windows); \"Same result\" confirms every tool printed CPython's result line."
echo
echo "- CPU: $cpu ($(nproc) logical CPUs visible), $mem_gb GB RAM visible to Linux"
echo "- OS: $os, kernel $(uname -r)"
echo "- CPython: ${py_ver:-not installed}; ${np_info:-NumPy not installed}"
echo "- Codon: ${codon_ver:-not installed}; \`codon build -release -exe\` (LLVM, targets the host CPU; default \`--numerics=c\`); Codon's own NumPy implementation, BLAS/LAPACK: ${codon_blas:-OpenBLAS} statically linked into libcodonrt.so (ILP64, default threads)"
echo "- Pythran: ${pythran_ver:-not installed}; kernels compiled with \`pythran $PYTHRAN_FLAGS\` using CXX=$PYTHRAN_CXX ($(ver "$PYTHRAN_CXX" --version)) plus Python's own compile flags; driver runs on the same CPython"
echo "- Typhon: commit ${typhon_rev:-unknown}; .NET SDK ${dotnet_sdk:-–}, runtime ${dotnet_rt:-–}; \`typhon build --strict\` (\`--permissive\` for *_untyped*) run with \`dotnet app.dll\`; Typhon.NumPy (TensorPrimitives SIMD)"
echo "- Typhon Native AOT: \`typhon build --aot\` (.NET ILCompiler, OptimizationPreference=Speed), linked with ${clang_ver:-clang}; default configuration only. Programs that use dynamic values show n/a (dynamic)."
echo "- Method: median of $N runs per configuration. Times are the hot section, measured with time.perf_counter() inside each program. RAM is the peak resident set (max RSS, \`/usr/bin/time -f %M\`) of the whole process. Process is end-to-end wall-clock time of the whole process (startup + program). Build steps run once per program before timing and are not included."
echo "- Compile time per program (median, not included above): Codon $(median_build codon) · Pythran $(median_build pythran) · Typhon $(median_build typhon) · Typhon AOT $(median_build aot)"
echo "- Date: $(date +%Y-%m-%d)"
echo
echo "Cells show the hot-section time and, in parentheses, the speed-up versus CPython (\"4.0x\" = four times faster). n/a cells are explained at the end."

if (( ${#PROGRAMS[@]} )); then
    id=benchmarks_bench_startup
    if [[ " ${PROGRAMS[*]} " == *" $id "* ]]; then
        echo; echo "## Startup and footprint (bench_startup.ty)"; echo
        echo "The hot section is ~0 ms here; what matters is how long the whole process takes and how much memory it needs."
        echo
        mbsize() { [[ -n ${SIZE[$1]:-} ]] && LC_ALL=C awk -v b="${SIZE[$1]}" 'BEGIN { printf "%.2f MB", b / 1048576 }' || echo "–"; }
        {
            row "CPython" "$(wcell $id cpython)" "$(mcell $id cpython)" "the .py file (needs a Python installation)"
            row "Codon" "$(wcell $id codon)" "$(mcell $id codon)" "$(mbsize "$id|codon") executable (needs the Codon runtime libraries, libcodonrt.so etc.)"
            row "Typhon (JIT)" "$(wcell $id typhon)" "$(mcell $id typhon)" "$(mbsize "$id|typhon") .dll (needs the .NET runtime)"
            row "Typhon (Native AOT)" "$(wcell $id aot)" "$(mcell $id aot)" "$(mbsize "$id|aot") self-contained executable"
        } | md_table "Configuration" "Process time" "Peak RAM" "What you ship"
        echo; echo "Pythran: n/a (no function to compile; it would be the CPython row plus an extension module)."
    fi

    echo; echo "## Pure Python programs"; echo
    for id in "${PROGRAMS[@]}"; do
        [[ $id == benchmarks_bench_startup || ${USES_NUMPY[$id]} == 1 ]] && continue
        b=${HOT[$id|cpython]:-}
        row "$(label_of $id)" "$(tcell $id cpython)" "$(tcell $id codon "$b")" "$(tcell $id pythran "$b")" "$(tcell $id typhon "$b")" "$(tcell $id aot "$b")" "$(same_result $id)"
    done | md_table "Benchmark" "CPython" "Codon" "Pythran" "Typhon" "Typhon AOT" "Same result"

    echo; echo "## numpy programs"; echo
    echo "CPython + NumPy and Pythran show the faster of default OpenBLAS threads and OPENBLAS_NUM_THREADS=1, Typhon the faster of default threads and TYPHON_NUMPY_THREADS=1 (see the threading table below). Codon (its own NumPy implementation and bundled OpenBLAS) and Typhon AOT run with default settings. Speed-ups are versus the faster CPython + NumPy configuration."
    echo
    for id in "${PROGRAMS[@]}"; do
        [[ ${USES_NUMPY[$id]} == 1 ]] || continue
        b=$(best_hot $id cpython)
        row "$(label_of $id)" "$(tcell $id cpython)" "$(tcell $id codon "$b")" "$(tcell $id pythran "$b")" "$(tcell $id typhon "$b")" "$(tcell $id aot "$b")" "$(same_result $id)"
    done | md_table "Benchmark" "CPython + NumPy" "Codon" "Pythran" "Typhon" "Typhon AOT" "Same result"

    echo; echo "### Threading detail (hot section, both configurations)"; echo
    for id in "${PROGRAMS[@]}"; do
        [[ ${USES_NUMPY[$id]} == 1 ]] || continue
        row "$(label_of $id)" "$(tcell1 $id cpython)" "$(tcell1 $id cpython1)" "$(tcell1 $id pythran)" "$(tcell1 $id pythran1)" "$(tcell1 $id typhon)" "$(tcell1 $id typhon1)"
    done | md_table "Benchmark" "NumPy" "NumPy 1 BLAS thread" "Pythran" "Pythran 1 BLAS thread" "Typhon" "Typhon 1 thread"

    echo; echo "## Programs: process time (end to end, default configuration)"; echo
    for id in "${PROGRAMS[@]}"; do
        row "$(label_of $id)" "$(wcell $id cpython)" "$(wcell $id codon)" "$(wcell $id pythran)" "$(wcell $id typhon)" "$(wcell $id aot)"
    done | md_table "Benchmark" "CPython" "Codon" "Pythran" "Typhon" "Typhon AOT"

    echo; echo "## Programs: peak RAM (max RSS, default configuration)"; echo
    for id in "${PROGRAMS[@]}"; do
        row "$(label_of $id)" "$(mcell $id cpython)" "$(mcell $id codon)" "$(mcell $id pythran)" "$(mcell $id typhon)" "$(mcell $id aot)"
    done | md_table "Benchmark" "CPython" "Codon" "Pythran" "Typhon" "Typhon AOT"
fi

if (( ${#PROBLEMS[@]} )); then
    echo; echo "## Problems: plain loops vs NumPy, per tool"; echo
    echo "Each problem is implemented twice (\`problems/<name>_loops.ty\` and \`problems/<name>_numpy.ty\`) and each implementation runs on every tool. NumPy columns show the faster of the default and single-threaded configurations where a tool has both. Speed-ups are versus CPython loops."
    echo
    for p in "${PROBLEMS[@]}"; do
        L=problems_${p}_loops; M=problems_${p}_numpy; b=${HOT[$L|cpython]:-}
        row "$p" "$(tcell $L cpython)" "$(tcell $M cpython "$b")" "$(tcell $L codon "$b")" "$(tcell $M codon "$b")" \
            "$(tcell $L pythran "$b")" "$(tcell $M pythran "$b")" "$(tcell $L typhon "$b")" "$(tcell $M typhon "$b")" \
            "$(tcell $L aot "$b")" "$(tcell $M aot "$b")" "$(agree $L $M)"
    done | md_table "Problem" "CPython loops" "CPython + NumPy" "Codon loops" "Codon + NumPy" "Pythran loops" "Pythran + NumPy" "Typhon loops" "Typhon + NumPy" "Typhon AOT loops" "Typhon AOT + NumPy" "Results agree"

    echo; echo "### Problems: peak RAM (max RSS, default configuration)"; echo
    for p in "${PROBLEMS[@]}"; do
        L=problems_${p}_loops; M=problems_${p}_numpy
        row "$p" "$(mcell $L cpython)" "$(mcell $M cpython)" "$(mcell $L codon)" "$(mcell $M codon)" "$(mcell $L pythran)" "$(mcell $M pythran)" \
            "$(mcell $L typhon)" "$(mcell $M typhon)" "$(mcell $L aot)" "$(mcell $M aot)"
    done | md_table "Problem" "CPython loops" "CPython + NumPy" "Codon loops" "Codon + NumPy" "Pythran loops" "Pythran + NumPy" "Typhon loops" "Typhon + NumPy" "Typhon AOT loops" "Typhon AOT + NumPy"
fi

echo; echo "## Not applicable and failures"; echo
if (( ${#NOTES[@]} )); then printf -- '- %s\n' "${NOTES[@]}"; else echo "None."; fi

echo; echo "## Notes on comparability"; echo
echo "- Pythran compiles only the exported kernel; the driver, setup code and timing run on CPython. Arguments and return values cross the Python/C++ boundary on every call: Python lists are converted to C++ containers (and returned lists back), which is included in the hot-section time (dot, movavg: 10M / 5M-element lists; matmul: a 200x200 result). Every Pythran extension module imports NumPy when it loads, so Pythran's process time and RAM include a NumPy import even for pure-Python kernels."
echo "- Codon compiles the whole program with 64-bit machine integers (no arbitrary precision; overflow wraps) and C numerics by default (\`--numerics=c\`). None of these programs overflows, and every result line is compared with CPython's."
echo "- BLAS: CPython + NumPy uses NumPy's bundled scipy-openblas, Codon its own statically linked OpenBLAS, Typhon.NumPy no BLAS (managed TensorPrimitives code). Pythran's NumPy support is its own C++ expression templates (none of the Pythran kernels here calls BLAS)."
echo "- Code generation targets: Codon and Pythran (-march=native) use the host CPU's full instruction set; the .NET JIT also targets the host CPU; Native AOT targets the ILCompiler's default instruction-set baseline."
} > "$RESULTS"

echo >&2
sed -n '/^## /,$p' "$RESULTS" >&2
log ""
if grep -q '| NO: ' "$RESULTS"; then log "RESULT MISMATCHES:"; grep '| NO: ' "$RESULTS" >&2; fi
log "written: $RESULTS"
