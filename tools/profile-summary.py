#!/usr/bin/env python3
"""Summarise Ultra profiles (Firefox Profiler format, *.json.gz) as compact Markdown.

Usage:
  python tools/profile-summary.py a.json.gz [b.json.gz ...]   Markdown per profile
  python tools/profile-summary.py --json a.json.gz [...]      machine readable summary (JSON on stdout)
  python tools/profile-summary.py --compare a b               difference table of the layers
                                                              (a, b: *.json.gz profile or a summary written by --json)

Per profile: CPU total and per thread, share per layer (exclusive = leaf frame, inclusive = layer anywhere
in the stack, counted once per sample), top functions per busy thread and the P/Invoke boundary
(managed frame directly calling a vlqt.dll frame).

Weights are the per-sample CPU time (samples.threadCPUDelta, ns). Without it every sample counts
as one sampling interval. Only the Python standard library is used.
"""

import argparse
import gzip
import json
import os
import re
import sys
from collections import defaultdict

LAYERS = [
    "VL.Qt managed",
    "vvvv / VL.* managed",
    ".NET runtime / JIT / GC",
    "vlqt.dll (QtNative)",
    "Qt",
    "GPU / D3D / DXGI",
    "Kernel / ntdll / Win32",
    "Rest",
]
L_QT, L_VL, L_NET, L_QTNATIVE, L_QTN, L_GPU, L_KERNEL, L_REST = range(len(LAYERS))

THREAD_MIN_SHARE = 0.01  # threads below this share are summarised in one row
TOP_THREAD_SHARE = 0.05  # threads from this share on get function tables
TOP_FUNCS = 15
TOP_PINVOKE = 10

_gpuRe = re.compile(
    r"^(d3d\d*|d3dcompiler.*|dxgi|dxcore|dxil.*|dxva2|dcomp|vulkan.*|opengl32|"
    r"nv(wgf2.*|d3d.*|oglv.*|api.*|ngx.*|cuda.*|ldumd.*|ldumdx.*)|"
    r"amd(xc.*|vlk.*|xx.*|ihk.*)|atid.*|atio.*|ig(d.*|c.*|9.*|xelp.*)|qc(dx.*|vulkan.*|gl.*)|"
    r"gfx.*|mesa.*|libgl.*|libegl.*|libvulkan.*|libnvidia.*)$")
_win32Re = re.compile(
    r"^(ntdll|kernel32|kernelbase|win32u|user32|gdi32|gdi32full|advapi32|sechost|rpcrt4|combase|"
    r"ole32|oleaut32|shell32|shcore|shlwapi|comdlg32|comctl32|imm32|msctf|uxtheme|crypt32|bcrypt.*|"
    r"ws2_32|mswsock|winmm|ucrtbase|msvcrt|msvcp\d*.*|vcruntime\d*.*|concrt\d*|ntoskrnl|win32k.*|"
    r"hal|wintypes|cfgmgr32|setupapi|version|dwmapi|propsys|windows\..*)$")
_kernelNameRe = re.compile(r"^(ntoskrnl|win32k\w*|hal|ntkrnl\w*)!")
_runtimeRe = re.compile(
    r"^(coreclr|clrjit|clrgc|clrgcexp|hostfxr|hostpolicy|hostcommon|mscorlib|mscordaccore|mscordbi|"
    r"netstandard|system\..*|microsoft\..*|icu|libicu.*|api-ms-win-.*|apphost)$")


def moduleKey(name):
    """'VL.Qt.dll' -> 'vl.qt', 'qt6core.dll' -> 'qt6core'."""
    if not name:
        return ""
    n = name.lower().replace("\\", "/").rsplit("/", 1)[-1]
    for ext in (".dll", ".exe", ".sys", ".so", ".dylib"):
        if n.endswith(ext):
            return n[: -len(ext)]
    return n


_bangRe = re.compile(r"^([A-Za-z][\w.\-]*)!0x")


def classify(funcName, module, category):
    r = moduleKey(module)
    m = _bangRe.match(funcName)
    if m and not r:
        r = moduleKey(m.group(1))  # unsymbolicated frame 'qwindows!0x...'
    if category == "Kernel" or _kernelNameRe.match(funcName):
        return L_KERNEL
    if r == "vl.qt" or funcName.startswith("VL.Qt."):
        return L_QT
    if r == "vlqt" or r == "libvlqt":
        return L_QTNATIVE
    if re.match(r"^((lib)?qt\d.*|qwindows|qminimal|qoffscreen|qdirect2d)$", r):
        return L_QTN
    if _gpuRe.match(r):
        return L_GPU
    if r.startswith(("vl.", "vvvv")) or funcName.startswith("VL."):
        return L_VL
    if category in (".NET GC", ".NET JIT", ".NET CLR") or _runtimeRe.match(r):
        return L_NET
    if _win32Re.match(r):
        return L_KERNEL
    return L_REST


def short(name, limit=100):
    p = name.find("(")
    if p > 0:
        name = name[:p] + "()"
    name = name.replace("|", "\\|").replace("`", "'")
    return name if len(name) <= limit else name[: limit - 1] + "…"


def loadJson(path):
    with open(path, "rb") as f:
        raw = f.read()
    if raw[:2] == b"\x1f\x8b":
        raw = gzip.decompress(raw)
    return json.loads(raw.decode("utf-8-sig"))


class ThreadStats:
    pass


def analyzeThread(thread, sharedStrings, categories, intervalNs):
    sa = thread.get("stringArray") or thread.get("stringTable") or sharedStrings or []
    samples = thread["samples"]
    stacks = samples["stack"]
    cpu = samples.get("threadCPUDelta")
    weights = samples.get("weight")
    st = thread["stackTable"]
    ft = thread["frameTable"]
    fu = thread["funcTable"]
    rt = thread["resourceTable"]

    def catName(idx):
        if idx is None or idx < 0 or idx >= len(categories):
            return ""
        return categories[idx]

    # per frame: (funcName, module, layer, managed)
    frameInfo = []
    frameFunc = ft["func"]
    for i in range(ft["length"]):
        f = frameFunc[i]
        nameIdx = fu["name"][f]
        name = sa[nameIdx] if nameIdx is not None and 0 <= nameIdx < len(sa) else "?"
        res = fu["resource"][f]
        module = ""
        if res is not None and res >= 0 and res < rt["length"]:
            n = rt["name"][res]
            module = sa[n] if n is not None and 0 <= n < len(sa) else ""
        cat = ft["category"][i] if ft.get("category") else None
        cname = catName(cat)
        layer = classify(name, module, cname)
        managed = cname == ".NET" or layer in (L_QT, L_VL)
        frameInfo.append((name, module, layer, managed))

    # weight per stack (ns)
    stackWeight = defaultdict(float)
    total = 0.0
    n = len(stacks)
    for i in range(n):
        s = stacks[i]
        if s is None:
            continue
        w = cpu[i] if cpu is not None and cpu[i] is not None else None
        if w is None:
            w = intervalNs * (weights[i] if weights is not None and weights[i] is not None else 1)
        stackWeight[s] += w
        total += w

    stackFrame = st["frame"]
    stackPrefix = st["prefix"]
    layerExcl = [0.0] * len(LAYERS)
    layerIncl = [0.0] * len(LAYERS)
    funcExcl = defaultdict(float)
    funcIncl = defaultdict(float)
    pinvoke = defaultdict(float)

    for s, w in stackWeight.items():
        chain = []  # leaf -> root, frame indices
        x = s
        while x is not None:
            chain.append(stackFrame[x])
            x = stackPrefix[x]
        if not chain:
            continue
        leaf = frameInfo[chain[0]]
        layerExcl[leaf[2]] += w
        funcExcl[(leaf[0], leaf[1])] += w
        layers = set()
        funcs = set()
        for fr in chain:
            info = frameInfo[fr]
            layers.add(info[2])
            funcs.add((info[0], info[1]))
        for ly in layers:
            layerIncl[ly] += w
        for fk in funcs:
            funcIncl[fk] += w
        # P/Invoke boundary: managed frame directly calling a vlqt frame
        pairs = set()
        for i in range(len(chain) - 1):
            callee = frameInfo[chain[i]]
            if callee[2] != L_QTNATIVE:
                continue
            j = i + 1
            while j < len(chain) and frameInfo[chain[j]][0].startswith("IL_STUB"):
                j += 1
            if j >= len(chain):
                continue
            caller = frameInfo[chain[j]]
            if caller[2] == L_QTNATIVE or not caller[3]:
                continue
            pairs.add(((caller[0], caller[1]), (callee[0], callee[1])))
        for p in pairs:
            pinvoke[p] += w

    t = ThreadStats()
    t.name = thread.get("name", "?")
    t.tid = thread.get("tid")
    t.total = total
    t.samples = sum(1 for s in stacks if s is not None)
    t.layerExcl = layerExcl
    t.layerIncl = layerIncl
    t.funcExcl = funcExcl
    t.funcIncl = funcIncl
    t.pinvoke = pinvoke
    return t


def analyzeProfile(path):
    prof = loadJson(path)
    meta = prof.get("meta", {})
    shared = prof.get("shared") or {}
    sharedStrings = shared.get("stringArray")
    categories = [c.get("name", "") for c in meta.get("categories", [])]
    intervalNs = float(meta.get("interval", 1.0)) * 1e6
    threads = [analyzeThread(t, sharedStrings, categories, intervalNs) for t in prof.get("threads", [])]
    wallMs = float(meta.get("profilingEndTime", 0) or 0) - float(meta.get("profilingStartTime", 0) or 0)
    if wallMs <= 0:
        wallMs = max((sum(t.total for t in threads) / 1e6), 1.0)
    total = sum(t.total for t in threads)
    layerExcl = [0.0] * len(LAYERS)
    layerIncl = [0.0] * len(LAYERS)
    pinvoke = defaultdict(float)
    for t in threads:
        for i in range(len(LAYERS)):
            layerExcl[i] += t.layerExcl[i]
            layerIncl[i] += t.layerIncl[i]
        for k, w in t.pinvoke.items():
            pinvoke[k] += w
    return {
        "file": os.path.basename(path),
        "product": meta.get("product", ""),
        "abi": meta.get("abi", ""),
        "cpus": meta.get("logicalCPUs"),
        "wall_ms": wallMs,
        "cpu_ms": total / 1e6,
        "cores": (total / 1e6) / wallMs if wallMs else 0.0,
        "samples": sum(t.samples for t in threads),
        "_threads": threads,
        "_layerExcl": layerExcl,
        "_layerIncl": layerIncl,
        "_pinvoke": pinvoke,
    }


def funcRows(table, limit, base):
    rows = sorted(table.items(), key=lambda kv: -kv[1])[:limit]
    return [
        {"function": k[0], "module": k[1], "ms": w / 1e6, "share": (w / base) if base else 0.0}
        for k, w in rows
    ]


def toSummary(p):
    """JSON-able summary of an analyzed profile."""
    total = p["cpu_ms"] * 1e6
    threads = sorted(p["_threads"], key=lambda t: -t.total)
    out = {k: p[k] for k in ("file", "product", "abi", "cpus", "wall_ms", "cpu_ms", "cores", "samples")}
    out["layers"] = {
        LAYERS[i]: {
            "exclusive_ms": p["_layerExcl"][i] / 1e6,
            "exclusive_share": p["_layerExcl"][i] / total if total else 0.0,
            "inclusive_ms": p["_layerIncl"][i] / 1e6,
            "inclusive_share": p["_layerIncl"][i] / total if total else 0.0,
        }
        for i in range(len(LAYERS))
    }
    out["threads"] = []
    for t in threads:
        entry = {"name": t.name, "tid": t.tid, "cpu_ms": t.total / 1e6, "share": t.total / total if total else 0.0}
        if total and t.total / total >= TOP_THREAD_SHARE:
            entry["top_exclusive"] = funcRows(t.funcExcl, TOP_FUNCS, t.total)
            entry["top_inclusive"] = funcRows(t.funcIncl, TOP_FUNCS, t.total)
        out["threads"].append(entry)
    pv = sorted(p["_pinvoke"].items(), key=lambda kv: -kv[1])[:TOP_PINVOKE]
    out["pinvoke"] = [
        {
            "caller": c[0],
            "caller_module": c[1],
            "callee": e[0],
            "ms": w / 1e6,
            "share": w / total if total else 0.0,
        }
        for (c, e), w in pv
    ]
    return out


def pct(x):
    return f"{x * 100:.1f}%"


def renderMarkdown(s):
    L = []
    title = s["product"] or s["file"]
    L.append(f"## {title} ({s['file']}, {s['abi']})")
    L.append("")
    L.append(
        f"**CPU:** {s['cpu_ms']:.0f} ms over {s['wall_ms'] / 1000:.1f} s = {s['cores']:.2f} cores on average "
        f"({s['samples']:,} samples, {s['cpus']} logical CPUs)"
    )
    L.append("")
    L.append("### Threads")
    L.append("| Thread | CPU ms | Share |")
    L.append("|---|---:|---:|")
    small = [t for t in s["threads"] if t["share"] < THREAD_MIN_SHARE]
    for t in s["threads"]:
        if t["share"] >= THREAD_MIN_SHARE:
            L.append(f"| {t['name']} | {t['cpu_ms']:.0f} | {pct(t['share'])} |")
    if small:
        L.append(
            f"| {len(small)} threads < {THREAD_MIN_SHARE * 100:.0f} % | "
            f"{sum(t['cpu_ms'] for t in small):.1f} | {pct(sum(t['share'] for t in small))} |"
        )
    L.append("")
    L.append("### Layers (exclusive = leaf frame, inclusive = anywhere in the stack)")
    L.append("| Layer | Excl ms | Excl % | Incl ms | Incl % |")
    L.append("|---|---:|---:|---:|---:|")
    for name, v in s["layers"].items():
        L.append(
            f"| {name} | {v['exclusive_ms']:.0f} | {pct(v['exclusive_share'])} | "
            f"{v['inclusive_ms']:.0f} | {pct(v['inclusive_share'])} |"
        )
    L.append("")
    for t in s["threads"]:
        if "top_exclusive" not in t:
            continue
        for key, label in (("top_exclusive", "exclusive"), ("top_inclusive", "inclusive")):
            L.append(f"### {t['name']} – top {TOP_FUNCS} {label} ({t['cpu_ms']:.0f} ms)")
            L.append("| ms | % thread | Function | Module |")
            L.append("|---:|---:|---|---|")
            for r in t[key]:
                L.append(f"| {r['ms']:.1f} | {pct(r['share'])} | `{short(r['function'])}` | {r['module']} |")
            L.append("")
    L.append("### P/Invoke boundary (managed → vlqt)")
    if s["pinvoke"]:
        L.append("| ms | % CPU | Managed caller | → vlqt function |")
        L.append("|---:|---:|---|---|")
        for r in s["pinvoke"]:
            L.append(f"| {r['ms']:.1f} | {pct(r['share'])} | `{short(r['caller'])}` | `{short(r['callee'])}` |")
    else:
        L.append("No managed frame calls into vlqt.dll in this profile.")
    L.append("")
    return "\n".join(L)


def loadSummary(path):
    """Accepts a profile (*.json.gz) or a summary written by --json (single object or list)."""
    with open(path, "rb") as f:
        head = f.read(2)
    if head != b"\x1f\x8b":
        data = loadJson(path)
        if isinstance(data, list):
            data = data[0]
        if isinstance(data, dict) and "layers" in data and "libs" not in data:
            return data
    return toSummary(analyzeProfile(path))


def renderCompare(a, b):
    L = []
    L.append(f"## Compare: {a['file']} → {b['file']}")
    L.append("")
    L.append("| | A | B | Δ |")
    L.append("|---|---:|---:|---:|")
    L.append(
        f"| CPU ms | {a['cpu_ms']:.0f} | {b['cpu_ms']:.0f} | {b['cpu_ms'] - a['cpu_ms']:+.0f} |"
    )
    L.append(
        f"| Wall s | {a['wall_ms'] / 1000:.1f} | {b['wall_ms'] / 1000:.1f} | "
        f"{(b['wall_ms'] - a['wall_ms']) / 1000:+.1f} |"
    )
    L.append(f"| Cores (avg) | {a['cores']:.3f} | {b['cores']:.3f} | {b['cores'] - a['cores']:+.3f} |")
    L.append("")
    for key, label in (("exclusive", "Exclusive"), ("inclusive", "Inclusive")):
        L.append(f"### Layers – {label}")
        L.append("| Layer | A ms | B ms | Δ ms | A cores | B cores | Δ cores |")
        L.append("|---|---:|---:|---:|---:|---:|---:|")
        for name in LAYERS:
            av = a["layers"][name][key + "_ms"]
            bv = b["layers"][name][key + "_ms"]
            ac = av / a["wall_ms"] if a["wall_ms"] else 0.0
            bc = bv / b["wall_ms"] if b["wall_ms"] else 0.0
            L.append(f"| {name} | {av:.0f} | {bv:.0f} | {bv - av:+.0f} | {ac:.3f} | {bc:.3f} | {bc - ac:+.3f} |")
        L.append("")
    return "\n".join(L)


def main(argv):
    ap = argparse.ArgumentParser(description="Summarise Ultra profiles (*.json.gz) as Markdown.")
    ap.add_argument("profiles", nargs="*", help="profile(s) *.json.gz")
    ap.add_argument("--json", action="store_true", help="machine readable output (JSON)")
    ap.add_argument("--compare", nargs=2, metavar=("A", "B"), help="difference table of the layers of two profiles")
    args = ap.parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    if args.compare:
        a, b = (loadSummary(p) for p in args.compare)
        if args.json:
            json.dump({"a": a, "b": b}, sys.stdout, indent=1)
            sys.stdout.write("\n")
        else:
            sys.stdout.write(renderCompare(a, b) + "\n")
        return 0
    if not args.profiles:
        ap.error("pass at least one profile (or --compare A B)")
    summaries = [toSummary(analyzeProfile(p)) for p in args.profiles]
    if args.json:
        json.dump(summaries[0] if len(summaries) == 1 else summaries, sys.stdout, indent=1)
        sys.stdout.write("\n")
    else:
        sys.stdout.write("\n".join(renderMarkdown(s) for s in summaries) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
