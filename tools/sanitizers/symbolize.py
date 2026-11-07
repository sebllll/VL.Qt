#!/usr/bin/env python3
"""Symbolizes sanitizer reports offline: frames like `#3 <null> <null> (libvlqt.so+0x25e0c)` -> function file:line.

gcc 13's TSan does not symbolize inside the dotnet host (every frame `<null>`, also with external_symbolizer_path;
a plain C test program symbolizes fine), ASan does. addr2line on the module + offset works for both (#365).
Usage: symbolize.py <report> <dir>... ; writes <report>.sym next to it. Modules are looked up by file name in the
given dirs (first hit). Unknown modules (memfd:doublemapper = JIT code, stripped libs) stay as they are.
"""
import os
import re
import subprocess
import sys

FRAME = re.compile(r'^(\s*#\d+ )(?:<null> <null>|0x[0-9a-f]+ ) ?\(([^()+]+)\+(0x[0-9a-f]+)\)(.*)$')


def find(module, dirs, cache):
    if module not in cache:
        name = os.path.basename(module)
        hit = module if os.path.isabs(module) and os.path.isfile(module) else None
        for d in dirs:
            if hit:
                break
            p = os.path.join(d, name)
            if os.path.isfile(p):
                hit = p
        cache[module] = hit
    return cache[module]


def main():
    report, dirs = sys.argv[1], sys.argv[2:]
    lines = open(report, errors='replace').read().splitlines()
    cache = {}
    wanted = {}  # path -> set of offsets
    parsed = []
    for line in lines:
        m = FRAME.match(line)
        path = find(m.group(2), dirs, cache) if m else None
        parsed.append((m, path))
        if path:
            wanted.setdefault(path, set()).add(m.group(3))
    resolved = {}
    for path, offs in wanted.items():
        offs = sorted(offs)
        out = subprocess.run(['addr2line', '-f', '-C', '-e', path] + offs, capture_output=True, text=True).stdout
        out = out.splitlines()
        for i, off in enumerate(offs):
            fn, loc = (out[2 * i:2 * i + 2] + ['??', '??:0'])[:2]
            if fn != '??' or not loc.startswith('??'):
                resolved[(path, off)] = f'{fn} {loc}'
    with open(report + '.sym', 'w') as f:
        for line, (m, path) in zip(lines, parsed):
            sym = resolved.get((path, m.group(3))) if m and path else None
            f.write(f'{m.group(1)}{sym} ({os.path.basename(path)}+{m.group(3)}){m.group(4)}\n' if sym else line + '\n')


if __name__ == '__main__':
    main()
