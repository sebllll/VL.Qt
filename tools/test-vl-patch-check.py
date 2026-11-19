"""Regression test for tools/vl-patch-check.py and tools/vl_pin_catalog.py.

Usage: python tools/test-vl-patch-check.py   (exit code 0 = ok)

Writes small fixture patches (UTF-8 with BOM, CRLF, like vvvv) to a temporary folder and runs the checker on them:
  - an IOBox on a create pin (V Sync of Renderer) and a hidden Bounds written by Save Bounds: no finding;
  - a pass-through pad (Measured Size -> Size): no finding, untouched;
  - a slot pad (SlotId) in a class linked to a pin: no finding, untouched;
  - an IOBox on Interaction: error;
  - a hidden pin with a non-default value: error, --fix makes it visible without other byte changes;
  - a linked pin the node no longer has (renamed): error;
  - default-iobox hint, --accept into the baseline (reasons kept) and a stale entry;
  - QtTexture of VL.Qt.Stride (src/VL.Qt.Stride/*Node.cs): pins in the catalog, no false missing-pin, IOBox on Interaction-like
    rules and hidden pins work like for the other nodes; the default run (no argument) includes VL.Qt.Stride/help/*.vl;
  - Video (VL.Audio) of VL.Qt.Audio (src/VL.Qt.Audio/*Node.cs): pins in the catalog (also with the name in parentheses),
    a hidden Audio Offset with a non-default value is an error; the default run includes VL.Qt.Audio/help/*.vl.
"""
import json, os, subprocess, sys, tempfile

TOOLS = os.path.dirname(os.path.abspath(__file__))
CHECK = os.path.join(TOOLS, 'vl-patch-check.py')
sys.path.insert(0, TOOLS)
import vl_pin_catalog as pc  # noqa: E402

FAILED = []


def expect(cond, msg):
    if not cond:
        FAILED.append(msg)
    print(('ok   ' if cond else 'FAIL ') + msg)


def node(nid, name, category, pins):
    pin_xml = ''.join(f'\n        <Pin Id="{pid}" Name="{pname}" Kind="{kind}"{extra} />' for pid, pname, kind, extra in pins)
    return f'''
      <Node Bounds="100,100,65,19" Id="{nid}">
        <p:NodeReference LastCategoryFullName="{category}" LastDependency="VL.Qt.dll">
          <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
          <Choice Kind="ProcessAppFlag" Name="{name}" />
        </p:NodeReference>
        <Pin Id="{nid}ctx" Name="Node Context" Kind="InputPin" IsHidden="true" />{pin_xml}
      </Node>'''


def pad(pid, value=None, comment='Pad', extra=''):
    v = f' Value="{value}"' if value is not None else ''
    return f'\n      <Pad Id="{pid}" Comment="{comment}" Bounds="10,10,35,15" ShowValueBox="true" isIOBox="true"{v}{extra} />'


def document(canvas, links, inner=''):
    link_xml = ''.join(f'\n    <Link Id="L{i}" Ids="{a},{b}" />' for i, (a, b) in enumerate(links))
    text = f'''<?xml version="1.0" encoding="utf-8"?>
<Document xmlns:p="property" xmlns:r="reflection" Id="Doc" LanguageVersion="2025.7.0" Version="0.128">
  <Patch Id="Root">
    <Canvas Id="Canvas" DefaultCategory="Main" CanvasType="FullCategory">{canvas}{inner}
    </Canvas>{link_xml}
  </Patch>
</Document>
'''
    return b'\xef\xbb\xbf' + text.replace('\n', '\r\n').encode('utf-8')


def run(*args):
    p = subprocess.run([sys.executable, CHECK, *args], capture_output=True, text=True, encoding='utf-8')
    return p.returncode, p.stdout + p.stderr


def lines_with(out, *words):
    return [line for line in out.splitlines() if all(w in line for w in words)]


def test_catalog():
    catalog = pc.load_catalog()
    renderer = catalog['Renderer']
    vsync = pc.find_pin(renderer, 'V Sync')
    expect(vsync is not None and vsync['create'] and vsync['optional'] and vsync['default'] == 'True',
           'catalog: V Sync is an optional create pin of Renderer, default True')
    expect(pc.find_pin(renderer, 'Bounds')['written_by_node'], 'catalog: Renderer Bounds is written by the node')
    expect(pc.find_pin(renderer, 'Node Context') is None, 'catalog: the hidden NodeContext is skipped')
    rect = catalog['Rectangle']
    expect(pc.find_pin(rect, 'Interaction')['forbid_iobox'], 'catalog: Interaction is a forbidden-IOBox pin')
    expect(pc.find_pin(rect, 'Anchor')['default'] == 'TopLeft', 'catalog: Anchor default from [DefaultValue(typeof, nameof)]')
    expect(pc.find_pin(rect, 'Output')['direction'] == 'out', 'catalog: return pin Output')
    expect(pc.find_pin(catalog['Column'], 'Input 3')['group'], 'catalog: Input 3 belongs to the pin group Input')
    expect('Column (Spectral)' in catalog, 'catalog: node names with parentheses')
    expect(pc.title_case('vSync') == 'V Sync' and pc.title_case('strokeColor') == 'Stroke Color', 'catalog: title case')
    expect(pc.is_default('0.5f', dict(default='0.5', type='float')) and pc.is_default(None, dict(default='0, 0', type='Vector2')),
           'catalog: value comparison')


def test_stride_catalog():
    catalog = pc.load_catalog()
    tex = catalog.get('QtTexture')
    expect(tex is not None and tex['category'] == 'Qt' and tex['source'] == 'src/VL.Qt.Stride/QtTextureNode.cs',
           'stride: QtTexture is read from src/VL.Qt.Stride, category Qt')
    if tex is None:
        return
    expect(pc.find_pin(tex, 'Size')['default'] == '1920, 1080', 'stride: Size default from [DefaultValue(typeof(Int2), ...)]')
    expect(pc.find_pin(tex, 'Format')['default'] == 'B8G8R8A8_UNorm_SRgb', 'stride: Format default from nameof(PixelFormat...)')
    expect(pc.find_pin(tex, 'Enabled')['default'] == 'True' and pc.find_pin(tex, 'Debug')['optional'],
           'stride: Enabled default and optional Debug')
    expect(all(pc.find_pin(tex, n)['direction'] == 'out' for n in ('Output', 'Error', 'Perf')), 'stride: out pins')
    expect(pc.find_pin(tex, 'Node Context') is None, 'stride: the hidden NodeContext is skipped')


def test_stride_patch(tmp):
    canvas = (node('T', 'QtTexture', 'Qt', [('TInput', 'Input', 'InputPin', ''), ('TSize', 'Size', 'InputPin', ''),
                                            ('TFormat', 'Format', 'InputPin', ''), ('TEnabled', 'Enabled', 'InputPin', ' IsHidden="true"'),
                                            ('TOutput', 'Output', 'OutputPin', ''), ('TError', 'Error', 'OutputPin', '')])
              + pad('PSize', '1280, 720', 'Size') + pad('PFormat', 'B8G8R8A8_UNorm_SRgb', 'Format') + pad('POut', None, 'Output'))
    links = [('PSize', 'TSize'), ('PFormat', 'TFormat'), ('TOutput', 'POut')]
    path = os.path.join(tmp, 'stride.vl')
    open(path, 'wb').write(document(canvas, links))
    code, out = run(path, f'--baseline={os.path.join(tmp, "base.json")}')
    expect(code == 0 and not lines_with(out, 'missing-pin') and not lines_with(out, 'PSize'),
           'stride: QtTexture pins give no false missing-pin, a non-default Size IOBox is fine')
    expect(len(lines_with(out, 'hint default-iobox', 'QtTexture.Format', 'PFormat')) == 1,
           'stride: IOBox at the default Format is a hint')
    renamed = document(canvas.replace('Name="Size"', 'Name="Texture Size"'), links)
    open(path, 'wb').write(renamed)
    code, out = run(path, f'--baseline={os.path.join(tmp, "base.json")}')
    expect(code == 1 and len(lines_with(out, 'error missing-pin', 'QtTexture.Texture Size', 'TSize')) == 1,
           'stride: a link to a pin QtTexture does not have is an error')


def test_audio_catalog():
    video = pc.load_catalog().get('Video (VL.Audio)')
    expect(video is not None and video['category'] == 'Qt.Elements' and video['source'] == 'src/VL.Qt.Audio/VideoAudioNode.cs',
           'audio: Video (VL.Audio) is read from src/VL.Qt.Audio, category Qt.Elements')
    if video is None:
        return
    offset = pc.find_pin(video, 'Audio Offset')
    expect(offset['direction'] == 'in' and offset['optional'] and offset['default'] == '0', 'audio: Audio Offset is an optional input, default 0')
    expect(pc.find_pin(video, 'Audio')['direction'] == 'out' and not pc.find_pin(video, 'Audio')['optional'], 'audio: Audio is an output')
    expect(pc.find_pin(video, 'Sync Error')['direction'] == 'out' and pc.find_pin(video, 'Sync Error')['optional'], 'audio: Sync Error is an optional output')
    expect(pc.find_pin(video, 'Output') is not None and pc.find_pin(video, 'Node Context') is None, 'audio: return pin Output, hidden NodeContext skipped')


def test_audio_patch(tmp):
    canvas = (node('A', 'Video (VL.Audio)', 'Qt.Elements', [('AOffset', 'Audio Offset', 'InputPin', ' DefaultValue="12" IsHidden="true"'),
                                                          ('AVolume', 'Volume', 'InputPin', ''), ('AAudio', 'Audio', 'OutputPin', '')])
              + pad('PVolume', '0.5', 'Volume'))
    path = os.path.join(tmp, 'audio.vl')
    open(path, 'wb').write(document(canvas, [('PVolume', 'AVolume')]))
    code, out = run(path, f'--baseline={os.path.join(tmp, "base.json")}')
    expect(code == 1 and len(lines_with(out, 'error hidden-non-default', 'Video (VL.Audio).Audio Offset', 'AOffset')) == 1,
           'audio: a hidden Audio Offset with a value is an error')
    expect(not lines_with(out, 'missing-pin') and not lines_with(out, 'PVolume'), 'audio: Volume IOBox with a non-default value is fine, no false missing-pin')


ARC_TO_SOURCE = '''
namespace Test
{
    [ProcessNode(Name = "ArcTo", Category = "Qt.Paths")]
    public sealed class ArcToTangentNode
    {
        public ArcToTangentNode([Pin(Visibility = PinVisibility.Hidden)] NodeContext nodeContext) { }
        public QtPath Update(QtPath input, Vector2 through, Vector2 target, float radius = 10f) => input;
    }

    [ProcessNode(Name = "ArcTo", Category = "Qt.Paths")]
    public sealed class ArcToOvalNode
    {
        public ArcToOvalNode([Pin(Visibility = PinVisibility.Hidden)] NodeContext nodeContext) { }
        public QtPath Update(QtPath input, Vector2 target, float rx = 10f, float ry = 10f, bool largeArc = false) => input;
    }

    [ProcessNode(Name = "Other", Category = "Qt.Paths")]
    public sealed class OtherNode
    {
        public OtherNode([Pin(Visibility = PinVisibility.Hidden)] NodeContext nodeContext) { }
        public QtPath Update(QtPath input, float length = 1f) => input;
    }
}
'''


def test_overloads(tmp):
    root = os.path.join(tmp, 'overloads')
    os.makedirs(os.path.join(root, 'src', 'VL.Qt', 'Nodes', 'Test'))
    open(os.path.join(root, 'src', 'VL.Qt', 'Nodes', 'Test', 'ArcToNode.cs'), 'w', encoding='utf-8').write(ARC_TO_SOURCE)
    catalog = pc.load_catalog(root)
    arc = catalog['ArcTo']
    forms = pc.all_forms(arc)
    expect(len(forms) == 2 and len(pc.all_forms(catalog['Other'])) == 1, 'overloads: two nodes of the same name are both kept, others stay single')
    expect([f['csharp'] for f in forms] == ['ArcToTangentNode', 'ArcToOvalNode'], 'overloads: the forms keep their order')
    expect(pc.resolve_overload(catalog['Other'], ['Input', 'Bogus']) is catalog['Other'], 'overloads: a single node is returned as it is')
    expect(pc.resolve_overload(arc, ['Input', 'Rx', 'Ry'])['csharp'] == 'ArcToOvalNode', 'overloads: most matching pin names wins')
    tie = pc.resolve_overload(arc, ['Input', 'Radius', 'Rx'])
    expect(all(pc.find_pin(tie, n) is not None for n in ('Radius', 'Rx', 'Large Arc', 'Through', 'Output')), 'overloads: a tie gives the union of the pins')

    import importlib.util
    spec = importlib.util.spec_from_file_location('vl_patch_check', CHECK)
    checker = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(checker)

    def missing(pin_names):
        pins = [(f'A{i}', n, 'OutputPin' if n == 'Output' else 'InputPin', '') for i, n in enumerate(pin_names)]
        canvas = node('A', 'ArcTo', 'Qt.Paths', pins) + pad('PIn', None, 'In')
        links = [('PIn', f'A{i}') for i, n in enumerate(pin_names) if n != 'Output']
        findings, _ = checker.check_patch(document(canvas, links), catalog)
        return sorted(f.pin for f in findings if f.rule == 'missing-pin')

    expect(missing(['Input', 'Rx', 'Ry', 'Large Arc', 'Output']) == [], 'overloads: pins of the second form give no missing-pin')
    expect(missing(['Input', 'Through', 'Radius', 'Output']) == [], 'overloads: pins of the first form give no missing-pin')
    expect(missing(['Input', 'Rx', 'Ry', 'Bogus']) == ['Bogus'], 'overloads: a wrong pin is a missing-pin')
    expect(missing(['Input', 'Radius', 'Rx', 'Bogus']) == ['Bogus'], 'overloads: a tie reports only pins no form has')
    expect(missing(['Input', 'Radius', 'Rx']) == [], 'overloads: a tie gives no false missing-pin')


def test_default_run():
    code, out = run()
    expect(code == 0, 'default run: exit code 0')
    import importlib.util
    spec = importlib.util.spec_from_file_location('vl_patch_check', CHECK)
    checker = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(checker)
    n = len(checker.expand([]))
    expect('VL.Qt.Stride/help/*.vl' in checker.DEFAULT_PATCHES and f'{n} patch(es)' in out,
           f'default run: VL.Qt.Stride/help/*.vl is one of the default patterns ({n} patches checked)')
    expect('VL.Qt.Audio/help/*.vl' in checker.DEFAULT_PATCHES and any('VL.Qt.Audio' in f for f in checker.expand([])),
           'default run: VL.Qt.Audio/help/*.vl is one of the default patterns and finds its patch')


def test_clean(tmp):
    canvas = (node('R', 'Renderer', 'Qt', [('RBounds', 'Bounds', 'InputPin', ' DefaultValue="10, 20, 300, 200" IsHidden="true"'),
                                           ('RVSync', 'V Sync', 'InputPin', ''), ('RIn', 'Input', 'InputPin', '')])
              + pad('PVSync', 'False', 'V Sync')
              + node('T', 'Text', 'Qt.Elements', [('TMeasured', 'Measured Size', 'OutputPin', ''), ('TOut', 'Output', 'OutputPin', '')])
              + node('Q', 'Rectangle', 'Qt.Elements', [('QSize', 'Size', 'InputPin', ''), ('QDebug', 'Debug', 'InputPin', ''),
                                                       ('QOut', 'Output', 'OutputPin', '')])
              + pad('PMeasured', None, 'Measured Size'))
    inner = '\n      <Pad Id="PSlot" SlotId="Slot1" Bounds="20,20" />'
    links = [('PVSync', 'RVSync'), ('TMeasured', 'PMeasured'), ('PMeasured', 'QSize'), ('PSlot', 'QDebug'), ('QOut', 'RIn')]
    path = os.path.join(tmp, 'clean.vl')
    raw = document(canvas, links, inner)
    open(path, 'wb').write(raw)
    code, out = run(path, f'--baseline={os.path.join(tmp, "base.json")}')
    expect(code == 0 and '0 error(s), 0 hint(s)' in out, 'clean: VSync create-pin IOBox, Save Bounds, pass-through and slot pad give no finding')
    code, out = run(path, '--fix', f'--baseline={os.path.join(tmp, "base.json")}')
    expect(open(path, 'rb').read() == raw, 'clean: --fix leaves pass-through pad and slot pad untouched')


def error_patch():
    canvas = (node('Q', 'Rectangle', 'Qt.Elements', [('QInter', 'Interaction', 'InputPin', ''),
                                                     ('QRadius', 'Radius', 'InputPin', ' DefaultValue="0.1" IsHidden="true"'),
                                                     ('QStroke', 'Stroke Width', 'InputPin', ' DefaultValue="0" IsHidden="true"'),
                                                     ('QFill', 'Fill', 'InputPin', ''), ('QOut', 'Output', 'OutputPin', '')])
              + pad('PInter', None, 'Interaction') + pad('PFill', '1, 0, 0, 1', 'Fill'))
    return document(canvas, [('PInter', 'QInter'), ('PFill', 'QFill')])


def test_errors(tmp):
    path = os.path.join(tmp, 'errors.vl')
    raw = error_patch()
    open(path, 'wb').write(raw)
    base = f'--baseline={os.path.join(tmp, "base.json")}'
    code, out = run(path, base)
    expect(code == 1, 'errors: exit code 1')
    expect(len(lines_with(out, 'error forbidden-iobox', 'Rectangle.Interaction', 'PInter')) == 1, 'errors: IOBox on Interaction')
    expect(len(lines_with(out, 'error hidden-non-default', 'Rectangle.Radius', 'QRadius')) == 1, 'errors: hidden Radius 0.1')
    expect(not lines_with(out, 'QStroke'), 'errors: hidden pin with its default value is fine')
    expect(len(lines_with(out, 'error missing-pin', 'Rectangle.Fill', 'QFill')) == 1, 'errors: renamed pin Fill')
    expect(open(path, 'rb').read() == raw, 'errors: without --fix no file changes')
    code, out = run(path, '--fix', base)
    after = open(path, 'rb').read()
    expected = raw.replace(b'Name="Radius" Kind="InputPin" DefaultValue="0.1" IsHidden="true"',
                           b'Name="Radius" Kind="InputPin" DefaultValue="0.1"')
    expect(after == expected and after != raw, '--fix: makes Radius visible, no other byte changes')
    expect(len(lines_with(out, 'fixed hidden-non-default', 'QRadius')) == 1 and code == 1,
           '--fix: reports fixed, the other errors stay')
    code, out = run(path, base)
    expect(not lines_with(out, 'hidden-non-default'), '--fix: second run finds no hidden pin')


def test_baseline(tmp):
    def patch(with_pad):
        canvas = node('Q', 'Rectangle', 'Qt.Elements', [('QAnchor', 'Anchor', 'InputPin', ''), ('QSize', 'Size', 'InputPin', '')])
        canvas += pad('PSize', '2, 1', 'Size')
        links = [('PSize', 'QSize')]
        if with_pad:
            canvas += pad('PAnchor', 'TopLeft', 'Anchor')
            links.append(('PAnchor', 'QAnchor'))
        return document(canvas, links)
    path = os.path.join(tmp, 'baseline.vl')
    basefile = os.path.join(tmp, 'padlint.json')
    base = f'--baseline={basefile}'
    open(path, 'wb').write(patch(True))
    code, out = run(path, base)
    expect(code == 0 and len(lines_with(out, 'hint default-iobox', 'Rectangle.Anchor', 'PAnchor')) == 1,
           'baseline: default-iobox is a hint, exit code 0')
    expect(not lines_with(out, 'PSize'), 'baseline: an IOBox with a non-default value is fine')
    code, out = run('--accept', path, base, '--reason=shows the pin')
    entries = json.load(open(basefile, encoding='utf-8'))['entries']
    expect(code == 0 and entries == [dict(patch='baseline.vl', id='PAnchor', rule='default-iobox', reason='shows the pin')],
           '--accept: adds the finding with patch, Id, rule, reason')
    code, out = run(path, base)
    expect(code == 0 and not lines_with(out, 'PAnchor'), 'baseline: accepted finding is not reported')
    data = json.load(open(basefile, encoding='utf-8'))
    data['entries'][0]['reason'] = 'edited by hand'
    json.dump(data, open(basefile, 'w', encoding='utf-8'))
    run('--accept', path, base)
    entries = json.load(open(basefile, encoding='utf-8'))['entries']
    expect(entries[0]['reason'] == 'edited by hand', '--accept: keeps existing reasons')
    open(path, 'wb').write(patch(False))
    code, out = run(path, base)
    expect(code == 1 and len(lines_with(out, 'error stale-baseline', 'PAnchor', 'no longer exists')) == 1,
           'baseline: entry of a removed element is reported as stale')


def main():
    test_catalog()
    test_stride_catalog()
    test_audio_catalog()
    test_default_run()
    with tempfile.TemporaryDirectory() as tmp:
        test_overloads(tmp)
        test_clean(tmp)
        test_errors(tmp)
        test_baseline(tmp)
        test_stride_patch(tmp)
        test_audio_patch(tmp)
    print(f'{len(FAILED)} failed' if FAILED else 'all passed')
    return 1 if FAILED else 0


if __name__ == '__main__':
    sys.exit(main())
