#!/usr/bin/env python3
"""Sorts the LeakSanitizer part of an ASan report into QtNative leaks and foreign leaks (#365).

LSan suppressions match any frame of a stack. Under dotnet every allocation on a managed thread has libcoreclr.so
frames somewhere below QtNative, so `leak:libcoreclr.so` in lsan.supp would hide QtNative leaks too. Instead:
- owner = whichever comes first from the top of the stack: a .NET runtime frame (libcoreclr.so/libclrjit.so) or a
  QtNative frame (src/VL.Qt.Native/, libvlqt.so). Libraries in between (Qt, ICU, libstdc++) do not decide.
- foreign: owner is the runtime (also when it allocates inside a callback QtNative invoked, e.g. the log callback,
  ICU collator for string compare), or there is no QtNative frame at all (.NET host, Qt/Mesa/ICU internals).
- QtNative leak: owner is QtNative (also QtNative -> Qt -> malloc; known Qt-internal ones are in lsan.supp).
Usage: lsan-filter.py <report>... ; prints QtNative leaks, exit 1 if there are any.
"""
import re
import sys

RUNTIME = ('libcoreclr.so', 'libclrjit.so')
QTNATIVE = ('/src/VL.Qt.Native/', 'libvlqt.so')


def main():
    qt_native, foreign, foreign_bytes = [], 0, 0
    for path in sys.argv[1:]:
        text = open(path, errors='replace').read()
        for block in re.split(r'\n(?=(?:Direct|Indirect) leak of )', text):
            m = re.match(r'(?:Direct|Indirect) leak of (\d+) byte', block)
            if not m:
                continue
            frames = [l for l in block.splitlines() if re.match(r'\s+#\d+ ', l)]
            frames = [l for l in frames if 'libsanitizer' not in l and 'libasan.so' not in l]
            owner = next((l for l in frames if any(x in l for x in RUNTIME + QTNATIVE)), '')
            if not any(s in owner for s in QTNATIVE):
                foreign += 1
                foreign_bytes += int(m.group(1))
            else:
                qt_native.append(block.strip())
    print(f'lsan: {len(qt_native)} QtNative leak(s), {foreign} foreign leak(s) ignored ({foreign_bytes} bytes)')
    for b in qt_native:
        print(b + '\n')
    return 1 if qt_native else 0


if __name__ == '__main__':
    sys.exit(main())
