"""Regression test for tools/vl-patch-migrate.py.

Usage: python tools/test-vl-patch-migrate.py   (exit code 0 = ok)

Writes small fixture patches (UTF-8 with BOM, CRLF, like vvvv), mapping files and a node-API snapshot to a temporary
folder and runs the script on them:
  - the shipped mapping file holds no row, and a run over the repository's patches changes nothing;
  - a swap in one step (Radius -> Set Radius, Get Radius -> Radius): only the two Name values change, a second run
    changes nothing, --check lists and writes nothing;
  - a pin name reused across two steps, and a patch whose nodes stand at different steps (as after a git merge);
  - a node rename with a PinReference and a pin row that names the node as it is called before the step;
  - category moves: same depth, a deeper chain rebuilt, a short chain, a type reference, one node by name;
  - errors (exit code 2, nothing written): conflicting rows, an unknown kind, rows of a step apart, a new name missing
    from the snapshot, a node whose names do not tell the steps apart;
  - all patches of the repository copied: a move applied, applied again (no change) and moved back in a second step
    gives the original bytes.
"""
import glob, os, shutil, subprocess, sys, tempfile

TOOLS = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(TOOLS)
SCRIPT = os.path.join(TOOLS, 'vl-patch-migrate.py')

FAILED = []


def expect(cond, msg):
    if not cond:
        FAILED.append(msg)
    print(('ok   ' if cond else 'FAIL ') + msg)


def run(*args):
    p = subprocess.run([sys.executable, SCRIPT, *args], capture_output=True, text=True, encoding='utf-8')
    return p.returncode, p.stdout + p.stderr


def lines_with(out, *words):
    return [line for line in out.splitlines() if all(w in line for w in words)]


def chain(names):
    """CategoryReference chain as vvvv writes it (inner name first)."""
    def build(i, ind):
        tag = 'CategoryReference' if i == 0 else 'p:OuterCategoryReference'
        attrs = f' Kind="Category" Name="{names[i]}" NeedsToBeDirectParent="true"'
        if i == len(names) - 1:
            return f'{ind}<{tag}{attrs} />'
        return f'{ind}<{tag}{attrs}>\n{build(i + 1, ind + "  ")}\n{ind}</{tag}>'

    return '\n' + build(0, '          ') if names else ''


def node(nid, name, category, pins, names=None, extra=''):
    pin_xml = ''.join(f'\n        <Pin Id="{pid}" Name="{pname}" Kind="{kind}" />' for pid, pname, kind in pins)
    return f'''
      <Node Bounds="100,100,65,19" Id="{nid}">
        <p:NodeReference LastCategoryFullName="{category}" LastDependency="VL.Qt.dll">
          <Choice Kind="NodeFlag" Name="Node" Fixed="true" />{chain(names or [])}
          <Choice Kind="ProcessAppFlag" Name="{name}" />{extra}
        </p:NodeReference>
        <Pin Id="{nid}ctx" Name="Node Context" Kind="InputPin" IsHidden="true" />{pin_xml}
      </Node>'''


def type_pad(pid, category, name):
    return f'''
      <Pad Id="{pid}" Bounds="10,10,35,15" ShowValueBox="true" isIOBox="true" Value="A &amp; B">
        <p:TypeAnnotation LastCategoryFullName="{category}" LastDependency="VL.Qt.dll">
          <Choice Kind="TypeFlag" Name="{name}" />
        </p:TypeAnnotation>
      </Pad>'''


def document(canvas):
    text = f'''<?xml version="1.0" encoding="utf-8"?>
<Document xmlns:p="property" xmlns:r="reflection" Id="Doc" LanguageVersion="2025.7.0" Version="0.128">
  <!-- a comment with <Pin Name="Radius" /> inside -->
  <Patch Id="Root">
    <Canvas Id="Canvas" DefaultCategory="Main" CanvasType="FullCategory">{canvas}
    </Canvas>
  </Patch>
</Document>
'''
    return b'\xef\xbb\xbf' + text.replace('\n', '\r\n').encode('utf-8')


API = '''VL.Qt | VL.Qt.Nodes.RectangleNode | attr ProcessNode(Name = "Rectangle", Category = "Qt.Elements")
VL.Qt | VL.Qt.Nodes.RectangleNode | method Update | pin Set Radius | in float | (setRadius)
VL.Qt | VL.Qt.Nodes.RectangleNode | method Update | pin Radius | out float | (radius)
VL.Qt | VL.Qt.Nodes.RectangleNode | method Update | pin Size | in Stride.Core.Mathematics.Vector2 | (size)
VL.Qt | VL.Qt.Nodes.RectangleNode | method Update | pin Fill Color | in Stride.Core.Mathematics.Color4 | (fillColor)
VL.Qt | VL.Qt.Nodes.CornerArcNode | attr ProcessNode(Name = "CornerArc", Category = "Qt.Shapes")
VL.Qt | VL.Qt.Nodes.CornerArcNode | method Update | pin Corner Radius | in float | (cornerRadius)
VL.Qt | VL.Qt.Nodes.CornerArcNode | method Update | pin Corner | in float | (corner)
VL.Qt | VL.Qt.Nodes.BlurNode | attr ProcessNode(Name = "Blur", Category = "Qt.Blurs")
VL.Qt | VL.Qt.Nodes.TextNode | attr ProcessNode(Name = "Text", Category = "Qt.Text")
VL.Qt | Qt.Types.DragAxis | type enum
'''


class Fixture:
    def __init__(self, tmp):
        self.tmp = tmp
        self.api = os.path.join(tmp, 'node-api.txt')
        open(self.api, 'w', encoding='utf-8', newline='\n').write(API)
        self.n = 0

    def mapping(self, rows):
        self.n += 1
        path = os.path.join(self.tmp, f'mapping{self.n}.txt')
        open(path, 'w', encoding='utf-8', newline='\n').write('# test\n' + ''.join(r + '\n' for r in rows))
        return path

    def patch(self, name, canvas):
        path = os.path.join(self.tmp, name)
        open(path, 'wb').write(document(canvas))
        return path

    def migrate(self, mapping, *args):
        return run(f'--mapping={mapping}', f'--api={self.api}', *args)


def test_shipped_mapping():
    rows = [line for line in open(os.path.join(TOOLS, 'vl-patch-mapping.txt'), encoding='utf-8')
            if line.strip() and not line.startswith('#')]
    expect(rows == [], 'shipped mapping: header only, no rows')
    code, out = run('--check')
    expect(code == 0 and lines_with(out, '0 would change'), 'shipped mapping: --check over the repository changes nothing')


SWAP = ['W4-20 | pin | Qt.Elements/Rectangle | Radius | Set Radius',
        'W4-20 | pin | Qt.Elements/Rectangle | Get Radius | Radius']


def rect(nid, pins, category='Qt.Elements', names=('Elements', 'Qt')):
    return node(nid, 'Rectangle', category, [(f'{nid}{i}', p, 'InputPin') for i, p in enumerate(pins)], list(names))


def test_swap(fx):
    path = fx.patch('swap.vl', rect('R', ['Size', 'Radius', 'Get Radius']))
    before = open(path, 'rb').read()
    mapping = fx.mapping(SWAP)
    code, out = fx.migrate(mapping, path, '--check')
    expect(code == 1 and len(lines_with(out, 'Rectangle [R]: pin Radius -> Set Radius')) == 1
           and len(lines_with(out, 'Rectangle [R]: pin Get Radius -> Radius')) == 1,
           'swap: --check lists both renames, exit code 1')
    expect(open(path, 'rb').read() == before, 'swap: --check writes nothing')
    code, out = fx.migrate(mapping, path)
    after = open(path, 'rb').read()
    want = before.replace(b'Id="R1" Name="Radius"', b'Id="R1" Name="Set Radius"') \
                 .replace(b'Id="R2" Name="Get Radius"', b'Id="R2" Name="Radius"')
    expect(code == 0 and after == want, 'swap: only the two Name values change (BOM, CRLF, comment kept)')
    code, out = fx.migrate(mapping, path)
    expect(code == 0 and open(path, 'rb').read() == want and lines_with(out, '0 changed'), 'swap: a second run changes nothing')
    code, out = fx.migrate(mapping, path, '--check')
    expect(code == 0, 'swap: --check after the run: exit code 0')


def test_steps(fx):
    # Radius -> Set Radius in one step, Get Radius -> Radius in a later one: the reused name must not move again.
    mapping = fx.mapping(['A | pin | Rectangle | Radius | Set Radius', 'B | pin | Rectangle | Get Radius | Radius'])
    path = fx.patch('steps.vl', rect('R', ['Radius', 'Get Radius']) + rect('S', ['Set Radius', 'Get Radius'])
                    + rect('T', ['Set Radius', 'Radius']))
    code, out = fx.migrate(mapping, path)
    text = open(path, 'rb').read().decode('utf-8-sig')
    expect(code == 0 and 'Id="R0" Name="Set Radius"' in text and 'Id="R1" Name="Radius"' in text,
           'steps: a node before both steps gets both')
    expect('Id="S0" Name="Set Radius"' in text and 'Id="S1" Name="Radius"' in text and not lines_with(out, '[S]: pin Set'),
           'steps: a node after the first step (merged from another branch) gets only the second')
    expect(not lines_with(out, '[T]'), 'steps: a migrated node is left alone')
    code, out = fx.migrate(mapping, path)
    expect(code == 0 and lines_with(out, '0 changed'), 'steps: a second run changes nothing')
    # C# spelling in a row: fillColor is Fill Color
    mapping = fx.mapping(['C | pin | Rectangle | color | fillColor'])
    path = fx.patch('csharp.vl', rect('R', ['Color']))
    code, out = fx.migrate(mapping, path)
    expect(code == 0 and lines_with(out, 'pin Color -> Fill Color'), 'steps: pin names in C# spelling follow the catalog rule')


def test_node_rename(fx):
    mapping = fx.mapping(['N | node | Qt.Paths | ArcTo | CornerArc',
                          'N | category | ArcTo | Qt.Paths | Qt.Shapes',
                          'N | pin | Qt.Paths/ArcTo | Radius | Corner Radius'])
    arc = node('A', 'ArcTo', 'Qt.Paths', [('A1', 'Input', 'InputPin'), ('A2', 'Radius', 'InputPin')], ['Paths', 'Qt'],
               '\n          <PinReference Kind="InputPin" Name="Radius" />')
    other = node('O', 'AddRect', 'Qt.Paths', [('O1', 'Radius', 'InputPin')], ['Paths', 'Qt'])
    path = fx.patch('rename.vl', arc + other)
    code, out = fx.migrate(mapping, path)
    text = open(path, 'rb').read().decode('utf-8-sig')
    expect(code == 0 and 'Name="CornerArc"' in text and 'Id="A2" Name="Corner Radius"' in text
           and '<PinReference Kind="InputPin" Name="Corner Radius" />' in text,
           'node rename: name, pin and PinReference change together')
    expect('LastCategoryFullName="Qt.Shapes"' in text and 'Name="Shapes" NeedsToBeDirectParent' in text,
           'node rename: a category row for one node uses its name before the step')
    expect('Id="O1" Name="Radius"' in text and 'Name="AddRect"' in text, 'node rename: other nodes in the category keep their pins')
    code, out = fx.migrate(mapping, path)
    expect(code == 0 and lines_with(out, '0 changed'), 'node rename: a second run changes nothing')


def test_categories(fx):
    mapping = fx.mapping(['M | category | * | Qt.Effects.MultiEffect | Qt.Blurs',
                          'M | category | * | Qt.Typo | Qt.Text',
                          'M | category | * | VL.Qt.Elements | Qt.Types'])
    blur = node('B', 'Blur', 'Qt.Effects.MultiEffect', [], ['MultiEffect', 'Effects', 'Qt'])
    short = node('S', 'Blur', 'Qt.Effects.MultiEffect', [], ['MultiEffect'])
    text_node = node('T', 'Text', 'Qt.Typo', [], ['Typo', 'Qt'])
    bare = node('U', 'Text', 'Qt.Typo', [])
    path = fx.patch('category.vl', blur + short + text_node + bare + type_pad('P', 'VL.Qt.Elements', 'DragAxis'))
    before = open(path, 'rb').read()
    code, out = fx.migrate(mapping, path)
    after = open(path, 'rb').read()
    want_blur = document(node('B', 'Blur', 'Qt.Blurs', [], ['Blurs', 'Qt']))
    expect(code == 0 and want_blur.split(b'<Node ', 1)[1].split(b'</Node>')[0] in after,
           'category: a deeper chain is written again in the form vvvv saves it')
    expect(b'<CategoryReference Kind="Category" Name="Blurs" NeedsToBeDirectParent="true" />' in after,
           'category: a short chain keeps its depth')
    expect(after.count(b'LastCategoryFullName="Qt.Text"') == 2 and b'Name="Text" NeedsToBeDirectParent' in after,
           'category: same depth renames in place; a node without chain changes its LastCategoryFullName only')
    expect(b'<p:TypeAnnotation LastCategoryFullName="Qt.Types" LastDependency="VL.Qt.dll">' in after
           and b'Value="A &amp; B"' in after, 'category: a type reference moves with its category')
    expect(after.count(b'\r\n') == after.count(b'\n') and after.startswith(b'\xef\xbb\xbf'), 'category: CRLF and BOM kept')
    code, out = fx.migrate(mapping, path)
    expect(code == 0 and lines_with(out, '0 changed'), 'category: a second run changes nothing')
    back = fx.mapping(['M | category | * | Qt.Effects.MultiEffect | Qt.Blurs',
                       'M | category | * | Qt.Typo | Qt.Text',
                       'M | category | * | VL.Qt.Elements | Qt.Types',
                       'N | category | * | Qt.Blurs | Qt.Effects.MultiEffect',
                       'N | category | * | Qt.Text | Qt.Typo',
                       'N | category | * | Qt.Types | VL.Qt.Elements'])
    code, out = fx.migrate(back, path, '--no-api-check')
    expect(code == 0 and open(path, 'rb').read() == before, 'category: a second step moving back gives the original bytes')


def test_errors(fx):
    path = fx.patch('errors.vl', rect('R', ['Radius']))
    before = open(path, 'rb').read()
    cases = [
        (['X | pin | Rectangle | Radius | Set Radius', 'X | pin | Rectangle | Radius | Size'], 'renames pin "Radius"', 'conflicting rows'),
        (['X | pin | Rectangle | Size | Radius', 'X | pin | Rectangle | Radius | Radius'], 'old and new are the same', 'old == new'),
        (['X | pin | Rectangle | Size | Radius', 'X | pin | Rectangle | Fill | Radius'], 'become "Radius"', 'two olds, one new'),
        (['X | thing | Rectangle | Size | Radius'], 'unknown kind', 'unknown kind'),
        (['X | pin | Rectangle | Size'], 'expected 5 fields', 'missing field'),
        (['X | pin | Rectangle | Size | Fill Color', 'Y | pin | Rectangle | Fill | Size', 'X | pin | Rectangle | Radius | Set Radius'],
         'are not together', 'rows of a step apart'),
        (['X | pin | Rectangle | Radius | Corner'], 'unknown pin "Corner" of Rectangle', 'unknown pin'),
        (['X | node | * | Rect | Rectangel'], 'unknown node "Rectangel"', 'unknown node'),
        (['X | category | * | Qt.Elements | Qt.Nowhere'], 'unknown category "Qt.Nowhere"', 'unknown category'),
        (SWAP, 'cannot tell which steps', 'a node with only Radius cannot tell the swap'),
    ]
    for rows, message, what in cases:
        code, out = fx.migrate(fx.mapping(rows), path)
        expect(code == 2 and lines_with(out, message) and open(path, 'rb').read() == before,
               f'errors: {what}: exit code 2, nothing written')
    other = fx.patch('errors-ok.vl', rect('R', ['Radius', 'Get Radius']))
    code, out = fx.migrate(fx.mapping(SWAP), other, path)
    expect(code == 2 and open(other, 'rb').read() == document(rect('R', ['Radius', 'Get Radius'])),
           'errors: one bad patch keeps every other patch unwritten')
    mixed = fx.patch('mixed.vl', rect('R', ['Set Radius', 'Get Radius', 'Radius']))
    code, out = fx.migrate(fx.mapping(['A | pin | Rectangle | Get Radius | Set Radius']), mixed)
    expect(code == 2 and lines_with(out, 'no step of the mapping fits'), 'errors: old and new names on one node are reported')


def test_repository(fx):
    copy = os.path.join(fx.tmp, 'repo')
    files = [os.path.relpath(f, ROOT) for f in glob.glob(os.path.join(ROOT, '**', '*.vl'), recursive=True)
             if not {'bin', 'obj', 'build'} & set(os.path.relpath(f, ROOT).split(os.sep))]
    for f in files:
        os.makedirs(os.path.join(copy, os.path.dirname(f)), exist_ok=True)
        shutil.copyfile(os.path.join(ROOT, f), os.path.join(copy, f))
    pattern = os.path.join(copy, '**', '*.vl')
    rows = ['T1 | category | * | Qt.Elements | Qt.Shapes', 'T1 | category | * | Qt.Effects.MultiEffect | Qt.Blurs',
            'T1 | category | * | VL.Qt.Elements | Qt.Types', 'T1 | pin | * | Interaction | Input Handler']
    code, out = fx.migrate(fx.mapping(rows), pattern, '--no-api-check')
    changed = [f for f in files if open(os.path.join(copy, f), 'rb').read() != open(os.path.join(ROOT, f), 'rb').read()]
    expect(code == 0 and len(changed) > 40, f'repository: a move over {len(files)} copied patches applies ({len(changed)} changed)')
    code, out = fx.migrate(fx.mapping(rows), pattern, '--no-api-check')
    expect(code == 0 and lines_with(out, '0 changed'), 'repository: a second run changes nothing')
    back = rows + ['T2 | category | * | Qt.Shapes | Qt.Elements', 'T2 | category | * | Qt.Blurs | Qt.Effects.MultiEffect',
                   'T2 | category | * | Qt.Types | VL.Qt.Elements', 'T2 | pin | * | Input Handler | Interaction']
    code, out = fx.migrate(fx.mapping(back), pattern, '--no-api-check')
    same = all(open(os.path.join(copy, f), 'rb').read() == open(os.path.join(ROOT, f), 'rb').read() for f in files)
    expect(code == 0 and same, 'repository: moving back in a second step gives the original bytes')


def main():
    test_shipped_mapping()
    with tempfile.TemporaryDirectory() as tmp:
        fx = Fixture(tmp)
        test_swap(fx)
        test_steps(fx)
        test_node_rename(fx)
        test_categories(fx)
        test_errors(fx)
        test_repository(fx)
    print(f'{len(FAILED)} failed' if FAILED else 'all passed')
    return 1 if FAILED else 0


if __name__ == '__main__':
    sys.exit(main())
