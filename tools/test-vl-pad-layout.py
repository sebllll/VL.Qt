"""Regression check for tools/vl-pad-layout.py: the layout must be idempotent.

Usage: python tools/test-vl-pad-layout.py [<file.vl> ...]   (default: all help/Qt/*.vl)

Lays out every patch twice in memory (the files are not touched) and fails if the second run still changes something.
Also checks that nodes feeding a channel pin of a Qt node (Reactive Channel) keep their offset to that node.
Exit code 0 = ok.
"""
import glob, importlib.util, os, sys
import xml.etree.ElementTree as ET

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
spec = importlib.util.spec_from_file_location('vl_pad_layout', os.path.join(ROOT, 'tools/vl-pad-layout.py'))
tool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tool)

def channel_offsets(raw):
    """(channel node id, Qt node id) -> offset of the Channel node to the Qt node whose input pin it feeds."""
    root = ET.fromstring(raw.decode('utf-8-sig'))
    result = {}
    for patch in root.iter('Patch'):
        canvas = patch.find('Canvas')
        if canvas is None:
            continue
        owner = {pin.get('Id'): n for n in canvas.findall('Node') for pin in n.findall('Pin')}
        for link in patch.findall('Link'):
            a, b = link.get('Ids').split(',')[:2]
            src, dst = owner.get(a), owner.get(b)
            if src is None or dst is None or tool.node_type(dst) is None:
                continue
            ref = src.find(tool.P + 'NodeReference')
            if ref is None or ref.get('LastCategoryFullName') != 'Reactive':
                continue
            sx, sy = tool.bounds(src)[:2]
            dx, dy = tool.bounds(dst)[:2]
            result[(src.get('Id'), dst.get('Id'))] = (round(sx - dx), round(sy - dy))
    return result

def main():
    files = [a for a in sys.argv[1:]] or sorted(glob.glob(os.path.join(ROOT, 'help/Qt/*.vl')))
    specs = tool.load_specs()
    failed = []
    for f in files:
        raw = open(f, 'rb').read()
        first, once = tool.layout(raw, specs)
        second, twice = tool.layout(once, specs)
        moved = {k: (v, channel_offsets(once).get(k)) for k, v in channel_offsets(raw).items() if channel_offsets(once).get(k) != v}
        name = os.path.basename(f)
        if second or twice != once:
            failed.append(f'{name}: second run still changes {second} element(s)')
        if moved:
            failed.append(f'{name}: Channel node(s) moved relative to their Qt node: {moved}')
        print(f'{name}: first run {first}, second run {second} changes')
    for msg in failed:
        print('FAIL', msg)
    return 1 if failed else 0

if __name__ == '__main__':
    sys.exit(main())
