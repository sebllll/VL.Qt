"""Pad layout for VL.Qt patches (.vl), following the vvvv-pad-layout skill (Sebastian's IOBox staircase).

Usage: python tools/vl-pad-layout.py <file.vl> [...] [--no-add] [--check] [--prune-defaults|--prune-only] [--animate=Rectangle,Text] [--pads=Pin,Pin]

Per Qt node (category Qt) in every canvas:
  - Transform (Matrix) gets a TransformSRT node instead of a pad; with --animate=<Qt node names> a missing one is
    created, its Rotation driven by an LFO (Period 4 s); values sit directly in the pins, no IOBoxes.
  - removes pads on pins that are no longer visible (e.g. made optional in C#).
  - with --prune-defaults removes input pads whose value equals the C# default (only pads linked to this one pin;
    values that cannot be compared, e.g. colors, are kept). --prune-only does the same but moves nothing (for
    hand-laid-out patches).
  - adds missing pins (visible pins come from the Update() signatures in src/VL.Qt). IOBoxes only for the pins named in
    --pads=<Pin names>, inputs and outputs (input value = C# default, edit it afterwards; outputs only when not linked
    yet and worth watching), because an IOBox is the same as an attached value: it should sit only on pins whose value
    differs from the default and says something in the help patch. Never on `Interaction` (an IOBox there makes
    the element opaque to input; an existing one is removed). Not for Transform (an empty Matrix IOBox is not the
    identity), element inputs (Input/children) and outputs linked to nodes. Existing pads are kept.
  - places input pads as a staircase above the node (leftmost pin on top), output pads as a mirrored staircase
    below (rightmost output on top), wide ones (Error) included.
Then moves the nodes of a canvas row by row so the staircases do not overlap (pads of other nodes move along).
Nodes feeding an input pin of a Qt node (e.g. a Reactive Channel on a channel pin) keep their offset to that node,
their pads with them. Idempotent: a second run changes nothing (regression check: tools/test-vl-pad-layout.py).
Writes UTF-8 with BOM and CRLF like vvvv. --check only prints what would change and exits with 1 if anything would.
"""
import glob, os, random, re, string, sys
import xml.etree.ElementTree as ET

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
P = '{property}'
PAD_PINS = set()  # pins (inputs and outputs) that get a new IOBox (--pads=); empty by default
PAD_TO_NODE = 15.0  # lowest staircase pad: bottom edge this far above the node (measured in hand-tidied patches)
PRUNE_DEFAULTS = False  # --prune-defaults
PRUNE_ONLY = False  # --prune-only: like --prune-defaults, but nothing moves
HIDDEN = []  # pins hidden by --prune-defaults (counted as changes by --prune-only)
OPTIONAL = {}  # node -> {pin name: spec} of optional pins (no pad, unless its value differs from the default)
NO_PAD_PINS = {'Interaction'}  # never an IOBox: it would make the element opaque to input
ET.register_namespace('p', 'property')
ET.register_namespace('r', 'reflection')

# ---- pin specs from the C# signatures -------------------------------------------------------------------------

def _split_params(s):
    parts, depth, cur = [], 0, ''
    for ch in s:
        if ch in '(<[': depth += 1
        if ch in ')>]': depth -= 1
        if ch == ',' and depth == 0:
            parts.append(cur); cur = ''
        else:
            cur += ch
    parts.append(cur)
    return [' '.join(p.split()) for p in parts if p.strip()]

def _title(name):
    return re.sub(r'(?<=[a-z0-9])([A-Z])', r' \1', name[0].upper() + name[1:])

def load_specs():
    specs = {}
    files = glob.glob(os.path.join(ROOT, 'src/VL.Qt/Nodes/**/*Node.cs'), recursive=True) + [os.path.join(ROOT, 'src/VL.Qt/Renderer.cs')]
    for f in files:
        src = open(f, encoding='utf-8').read()
        node = re.search(r'ProcessNode\(Name\s*=\s*"([^"]+)"', src)  # e.g. "Row (Spectral)"
        node = node.group(1) if node else os.path.basename(f)[:-len('Node.cs')]
        m = re.search(r'public\s+\S+\s+Update\((.*?)\)\s*\{', src, re.S)
        if m is None:
            continue
        ins, outs = [], []
        if 'return: Pin(Name = "Output")' in src:
            outs.append(dict(name='Output', type='Element'))
        for p in _split_params(m.group(1)):
            dv = re.search(r'DefaultValue\((?:typeof\(\w+\),\s*nameof\(\w+\.(\w+)\)|"([^"]*)"|([^)]*))\)', p)
            default = None
            if dv:
                default = dv.group(1) or dv.group(2) or dv.group(3)
                if default in ('true', 'false'): default = default.capitalize()
                default = default.rstrip('f') if re.fullmatch(r'[\d.]+f', default or '') else default
            named = re.search(r'Name\s*=\s*"([^"]+)"', p)
            group = 'PinGroupKind.Collection' in p
            bare = re.sub(r'\[[^\]]*\]\s*', '', p)
            is_out = bare.startswith('out ')
            bare = bare[4:] if is_out else bare
            if '=' in bare:
                bare, eq = [x.strip() for x in bare.split('=', 1)]
                default = default or eq
            typ, name = bare.rsplit(' ', 1)
            if 'PinVisibility.Optional' in p:
                OPTIONAL.setdefault(node, {})[named.group(1) if named else _title(name)] = dict(type=typ.rstrip('?'), default=default)
                continue  # optional pins are hidden by default: no pad, not counted
            pin = dict(name=named.group(1) if named else _title(name), type=typ.rstrip('?'), default=default, group=group)
            (outs if is_out else ins).append(pin)
        specs[node] = (ins, outs)
    return specs

# ---- pad templates per type --------------------------------------------------------------------------------------

QT_ENUMS = {'ImageFillMode', 'VideoFillMode', 'FontWeight', 'TextWrap', 'TextElide', 'TextAutofit',
            'HorizontalAlignment', 'TextHorizontalAlignment', 'VerticalAlignment', 'Space'}
TYPES = {  # type: (annotation (category, dependency, name) or None, w, h)
    'bool': (('Primitive', 'CoreLibBasics.vl', 'Boolean'), 35, 35),
    'float': (('Primitive', 'CoreLibBasics.vl', 'Float32'), 35, 15),
    'int': (('Primitive', 'CoreLibBasics.vl', 'Integer32'), 35, 15),
    'Vector2': (('2D', 'VL.CoreLib.vl', 'Vector2'), 35, 28),
    'Color4': (('Color', 'VL.CoreLib.vl', 'RGBA'), 136, 15),
    'string': (('Primitive', 'CoreLibBasics.vl', 'String'), 240, 19),
    'Path': (('IO', 'VL.CoreLib.vl', 'Path'), 240, 19),
    'RectangleF': (('2D', 'VL.CoreLib.vl', 'Rectangle'), 190, 127),
    'RectangleAnchor': (('2D', 'VL.CoreLib.vl', 'RectangleAnchor'), 93, 15),
    'PerfStats': (None, 200, 15),
}
ERROR_SIZE = (420, 60)

def pad_size(pin):
    if pin['type'] == 'string' and pin['name'] == 'Error':
        return ERROR_SIZE
    if pin['type'] in QT_ENUMS:
        return (93, 15)
    t = TYPES.get(pin['type'])
    return (t[1], t[2]) if t else (35, 15)

def annotation(pin):
    t = pin['type']
    if t in QT_ENUMS:
        return ('VL.Qt' if t == 'Space' else 'VL.Qt.Elements', 'VL.Qt.vl', t)
    return TYPES[t][0] if t in TYPES else None

TYPE_DEFAULTS = {'bool': 'False', 'float': '0', 'int': '0', 'string': '', 'Vector2': '0, 0'}

def is_default(value, s):
    """True if a pad value (None = no Value attribute = type default) equals the pin's C# default."""
    default = s['default'] if s['default'] is not None else TYPE_DEFAULTS.get(s['type'])
    if default is None:
        return False
    if value is None:
        value = TYPE_DEFAULTS.get(s['type'])
        if value is None:
            return False
    norm = lambda v: [x.strip() for x in re.sub(r'^new\s+\w+\((.*)\)$', r'', v.strip()).split(',')]
    a, b = norm(value), norm(default)
    if len(a) != len(b):
        return False
    for x, y in zip(a, b):
        x, y = x.rstrip('fF') if re.fullmatch(r'-?[\d.]+[fF]', x) else x, y.rstrip('fF') if re.fullmatch(r'-?[\d.]+[fF]', y) else y
        try:
            if abs(float(x) - float(y)) > 1e-6:
                return False
        except ValueError:
            if x.lower() != y.lower() and x.split('.')[-1] != y.split('.')[-1]:
                return False
    return True

# ---- helpers -----------------------------------------------------------------------------------------------------

_ALPHA = string.digits + string.ascii_letters
def new_id():
    return ''.join(random.choice(_ALPHA) for _ in range(22))

def bounds(e):
    b = [float(v) for v in e.get('Bounds').split(',')]
    return b + [0.0] * (4 - len(b))  # ControlPoints have only x,y

def set_bounds(e, b):
    n = len(e.get('Bounds', '0,0,0,0').split(','))
    e.set('Bounds', ','.join(str(int(round(v))) for v in b[:n]))

def node_type(n):
    ref = n.find(P + 'NodeReference')
    if ref is None or not re.fullmatch(r'Qt(\.\w+)*', ref.get('LastCategoryFullName') or ''):
        return None
    flags = [c.get('Name') for c in ref.findall('Choice') if c.get('Kind') == 'ProcessAppFlag']
    return flags[-1] if flags else None

# ---- per canvas ------------------------------------------------------------------------------------------------

LFO_XML = """<Node Bounds="0,0,65,19" Id="{id}">
  <p:NodeReference LastCategoryFullName="Animation" LastDependency="VL.CoreLib.vl">
    <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
    <Choice Kind="ProcessAppFlag" Name="LFO" />
  </p:NodeReference>
  <Pin Id="{ctx}" Name="Node Context" Kind="InputPin" IsHidden="true" />
  <Pin Id="{period}" Name="Period" Kind="InputPin" DefaultValue="4" />
  <Pin Id="{phase}" Name="Phase" Kind="OutputPin" />
</Node>"""
SRT_XML = """<Node Bounds="0,0,65,19" Id="{id}">
  <p:NodeReference LastCategoryFullName="2D.Transform" LastDependency="CoreLibBasics.vl">
    <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
    <Choice Kind="OperationCallFlag" Name="TransformSRT" />
    <CategoryReference Kind="Category" Name="Transform" NeedsToBeDirectParent="true">
      <p:OuterCategoryReference Kind="Category" Name="2D" NeedsToBeDirectParent="true" />
    </CategoryReference>
  </p:NodeReference>
  <Pin Id="{input}" Name="Input" Kind="InputPin" />
  <Pin Id="{scaling}" Name="Scaling" Kind="InputPin" />
  <Pin Id="{rotation}" Name="Rotation" Kind="InputPin" />
  <Pin Id="{translation}" Name="Translation" Kind="InputPin" />
  <Pin Id="{output}" Name="Output" Kind="OutputPin" />
</Node>"""
JOIN_XML = """<Node Bounds="0,0,45,19" Id="{id}">
  <p:NodeReference LastCategoryFullName="2D.Rectangle" LastDependency="VL.CoreLib.vl">
    <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
    <Choice Kind="OperationCallFlag" Name="Rectangle (Join)" />
  </p:NodeReference>
  <Pin Id="{position}" Name="Position" Kind="InputPin" />
  <Pin Id="{size}" Name="Size" Kind="InputPin" />
  <Pin Id="{anchor}" Name="Anchor" Kind="InputPin" DefaultValue="TopLeft" />
  <Pin Id="{output}" Name="Output" Kind="StateOutputPin" />
</Node>"""
VEC2_PAD = """<Pad Id="{id}" Comment="{name}" Bounds="0,0,35,28" ShowValueBox="true" isIOBox="true" Value="{value}">
  <p:TypeAnnotation LastCategoryFullName="2D" LastDependency="VL.CoreLib.vl">
    <Choice Kind="TypeFlag" Name="Vector2" />
  </p:TypeAnnotation>
</Pad>"""
CHAIN_RISE = 45  # LFO sits this far above the TransformSRT

def _xml(tpl, **ids):
    return ET.fromstring('<r xmlns:p="property">' + tpl.format(**ids) + '</r>')[0]

def process_patch(patch, specs, add, animate=()):
    canvas = patch.find('Canvas')
    if canvas is None:
        return 0
    links = patch.findall('Link')
    nodes = [n for n in canvas.findall('Node') if n.get('Bounds') and len(n.get('Bounds').split(',')) == 4]
    pads = {p.get('Id'): p for p in canvas.findall('Pad') if p.get('Bounds')}
    pin_owner = {pin.get('Id'): n for n in nodes for pin in n.findall('Pin')}
    peers = {}
    for l in links:
        ids = l.get('Ids').split(',')
        for a in ids:
            peers.setdefault(a, []).extend(x for x in ids if x != a)
    changes = 0

    def add_link(src, dst):
        link = ET.Element('Link', {'Id': new_id(), 'Ids': f'{src},{dst}'})
        first_patch = patch.find('Patch')
        patch.insert(list(patch).index(first_patch) if first_patch is not None else len(patch), link)
        peers.setdefault(src, []).append(dst)
        peers.setdefault(dst, []).append(src)

    def remove_pad(pad):
        canvas.remove(pad)
        pads.pop(pad.get('Id'), None)
        for l in list(patch.findall('Link')):
            if pad.get('Id') in l.get('Ids').split(','):
                patch.remove(l)

    chain_nodes = set()

    plan = {}  # node -> list of (side, index, count, pad, pin spec)
    for n in nodes:
        t = node_type(n)
        if t not in specs:
            continue
        ins, outs = specs[t]
        xml_pins = {p.get('Name'): p for p in n.findall('Pin') if p.get('IsHidden') != 'true'}
        group_count = sum(1 for name in xml_pins if re.fullmatch(r'Input( \d+)?', name)) if any(p['group'] for p in ins) else 0
        vis_ins = []
        for s in ins:
            if s['group']:
                for k in range(max(group_count, 2)):
                    vis_ins.append(dict(s, name='Input' if k == 0 else f'Input {k + 1}'))
            else:
                vis_ins.append(s)
        # node width as vvvv draws it: 20 per input pin; a wider width in the XML was dragged by hand and stays
        nx, ny, nw, nh = bounds(n)
        set_bounds(n, [nx, ny, max(nw, 20 * (len(vis_ins) - 1) + 5), 19])
        # pads on pins that are no longer visible (e.g. now optional): remove pad, link and pin
        visible = {x['name'] for x in vis_ins + outs}
        for name, pin in list(xml_pins.items()):
            if name in visible:
                continue
            linked = peers.get(pin.get('Id'), [])
            opt = OPTIONAL.get(t, {}).get(name)
            if opt and any(not is_default(pads[x].get('Value'), opt) for x in linked if x in pads):
                continue  # an optional pin set to a non-default value keeps its pad
            if linked and all(x in pads for x in linked):
                for x in linked:
                    remove_pad(pads[x])
                n.remove(pin)
                del xml_pins[name]
                changes += 1
            elif PRUNE_DEFAULTS and opt and not linked and (pin.get('DefaultValue') is None or is_default(pin.get('DefaultValue'), opt)):
                pin.set('IsHidden', 'true')  # unused optional pin shown in the XML: hide it again
                del xml_pins[name]
                changes += 1
                HIDDEN.append(pin)
        entries = []
        for side, lst in (('in', vis_ins), ('out', outs)):
            for i, s in enumerate(lst):
                pin = xml_pins.get(s['name'])
                if side == 'in' and s['type'] == 'RectangleF' and add:
                    src = [pin_owner[x] for x in peers.get(pin.get('Id'), []) if x in pin_owner] if pin is not None else []
                    if not src:
                        if pin is None:
                            pin = ET.SubElement(n, 'Pin', {'Id': new_id(), 'Name': s['name'], 'Kind': 'InputPin'})
                            xml_pins[s['name']] = pin
                        value = s['default'] or '0, 0, 1, 1'
                        for x in [x for x in peers.get(pin.get('Id'), []) if x in pads]:
                            value = pads[x].get('Value') or value
                            remove_pad(pads[x])
                        if pin.get('DefaultValue'):
                            value = pin.get('DefaultValue')
                            del pin.attrib['DefaultValue']
                        v = [x.strip() for x in value.split(',')] + ['0'] * 4
                        ids = {k: new_id() for k in ('id', 'position', 'size', 'anchor', 'output')}
                        join = _xml(JOIN_XML, **ids)
                        canvas.append(join)
                        nodes.append(join)
                        for mp in join.findall('Pin'):
                            pin_owner[mp.get('Id')] = join
                        for key, val in (('position', f'{v[0]}, {v[1]}'), ('size', f'{v[2]}, {v[3]}')):
                            pad = _xml(VEC2_PAD, id=new_id(), name=key.capitalize(), value=val)
                            canvas.append(pad)
                            pads[pad.get('Id')] = pad
                            add_link(pad.get('Id'), ids[key])
                        add_link(ids['output'], pin.get('Id'))
                        changes += 1
                if side == 'in' and s['type'] in ('Matrix', 'RectangleF'):
                    # Transform: a Transform node (TransformSRT, fed by an LFO) instead of an IOBox
                    src = [pin_owner[x] for x in peers.get(pin.get('Id'), []) if x in pin_owner] if pin is not None else []
                    if s['type'] == 'Matrix' and not src and add and t in animate:
                        if pin is None:
                            pin = ET.SubElement(n, 'Pin', {'Id': new_id(), 'Name': s['name'], 'Kind': 'InputPin'})
                        ids = {k: new_id() for k in ('id', 'input', 'scaling', 'rotation', 'translation', 'output')}
                        srt = _xml(SRT_XML, **ids)
                        lids = {k: new_id() for k in ('id', 'ctx', 'period', 'phase')}
                        lfo = _xml(LFO_XML, **lids)
                        canvas.append(lfo)
                        canvas.append(srt)
                        for m in (lfo, srt):
                            nodes.append(m)
                            for mp in m.findall('Pin'):
                                pin_owner[mp.get('Id')] = m
                        add_link(lids['phase'], ids['rotation'])
                        add_link(ids['output'], pin.get('Id'))
                        src = [srt]
                        changes += 1
                    if src:
                        m = src[0]
                        in_pins = [mp.get('Id') for mp in m.findall('Pin') if mp.get('Kind') == 'InputPin']
                        feeders = []
                        for ip in in_pins:
                            for x in peers.get(ip, []):
                                if x in pads and pads[x] not in feeders:
                                    feeders.append(pads[x])
                                    continue
                                f = pin_owner.get(x)
                                if f is not None and f is not m and f not in feeders and node_type(f) is None:
                                    feeders.append(f)
                        chain_nodes.add(m)
                        chain_nodes.update(feeders)
                        entries.append((side, i, len(lst), ('chain', m, feeders), s))
                    else:
                        entries.append((side, i, len(lst), None, s))
                    continue
                linked_pads = [pads[x] for x in peers.get(pin.get('Id'), []) if x in pads] if pin is not None else []
                linked_other = pin is not None and any(x in pin_owner for x in peers.get(pin.get('Id'), []))
                if s['name'] in NO_PAD_PINS and side == 'in':
                    for p in linked_pads:
                        remove_pad(p)
                        changes += 1
                    linked_pads = []
                if PRUNE_DEFAULTS and side == 'in':
                    for p in list(linked_pads):
                        if len(peers.get(p.get('Id'), [])) == 1 and is_default(p.get('Value'), s):
                            remove_pad(p)
                            linked_pads.remove(p)
                            changes += 1
                pad = linked_pads[0] if linked_pads else None
                skip = s['type'] in ('Matrix', 'Element') or s['type'].startswith('Spread<Element') or s['group']
                if pad is None and add and not linked_other and not skip and s['name'] not in NO_PAD_PINS and s['name'] in PAD_PINS:
                    if pin is None:
                        pin = ET.SubElement(n, 'Pin', {'Id': new_id(), 'Name': s['name'], 'Kind': 'InputPin' if side == 'in' else 'OutputPin'})
                        xml_pins[s['name']] = pin
                    w, h = pad_size(s)
                    pad = ET.Element('Pad', {'Id': new_id(), 'Comment': s['name'], 'Bounds': f'0,0,{w},{h}', 'ShowValueBox': 'true', 'isIOBox': 'true'})
                    ann = annotation(s)
                    if side == 'in':
                        if s['default'] is not None:
                            pad.set('Value', s['default'])
                        elif s['type'] == 'bool':
                            pad.set('Value', 'False')
                        if ann:
                            ta = ET.SubElement(pad, P + 'TypeAnnotation', {'LastCategoryFullName': ann[0], 'LastDependency': ann[1]})
                            ET.SubElement(ta, 'Choice', {'Kind': 'TypeFlag', 'Name': ann[2]})
                        if s['type'] == 'bool':
                            vb = ET.SubElement(pad, P + 'ValueBoxSettings')
                            bm = ET.SubElement(vb, P + 'buttonmode', {P + 'Assembly': 'VL.UI.Forms', P + 'Type': 'VL.HDE.PatchEditor.Editors.ButtonModeEnum'})
                            bm.text = 'Toggle'
                    canvas.append(pad)
                    pads[pad.get('Id')] = pad
                    ids = f"{pad.get('Id')},{pin.get('Id')}" if side == 'in' else f"{pin.get('Id')},{pad.get('Id')}"
                    link = ET.Element('Link', {'Id': new_id(), 'Ids': ids})
                    first_patch = patch.find('Patch')
                    patch.insert(list(patch).index(first_patch) if first_patch is not None else len(patch), link)
                    changes += 1
                entries.append((side, i, len(lst), pad, s))
        plan[n] = entries

    # ---- staircases relative to the node (dx, dy) ----
    rel = {}  # element id -> (node, dx, dy)
    elems = dict(pads)
    for n, entries in plan.items():
        nx, ny, nw, nh = bounds(n)
        ins = [e for e in entries if e[0] == 'in']
        outs = [e for e in entries if e[0] == 'out']
        n_in = len(ins)
        step_in = 20.0
        top_prev = None
        gap = 0.0
        for side, i, cnt, pad, s in reversed(ins):
            if pad is None:
                gap = 30.0  # skipped pin: one extra stage
                continue
            if isinstance(pad, tuple):
                _, m, feeders = pad
                feeder_pads = [f for f in feeders if f.tag == 'Pad']
                rise = 0.0
                if feeder_pads:  # mini staircase: last pad PAD_TO_NODE above the chain node, each earlier one h + 15 higher
                    rise = PAD_TO_NODE + sum(bounds(f)[3] + 15 for f in feeder_pads[:-1])
                elif feeders:
                    rise = CHAIN_RISE
                h = 19 + rise
            else:
                _, _, w, h = bounds(pad)
            bottom = -PAD_TO_NODE - gap if top_prev is None else top_prev - 15 - gap
            top = bottom - h
            if top_prev is not None:
                top = min(top, top_prev - 30)  # min. stage 30
            x = 2 + i * step_in
            if isinstance(pad, tuple):
                elems[m.get('Id')] = m
                rel[m.get('Id')] = (n, x, top + h - 19)
                pin_index = {mp.get('Id'): k for k, mp in enumerate(q for q in m.findall('Pin') if q.get('Kind') == 'InputPin' and q.get('IsHidden') != 'true')}
                y_f = top
                for k, f in enumerate(feeders):
                    elems[f.get('Id')] = f
                    if f.tag == 'Pad':
                        target = next((pin_index[x] for x in peers.get(f.get('Id'), []) if x in pin_index), 0)
                        rel[f.get('Id')] = (n, x + 2 + 20 * target, y_f)
                        y_f += bounds(f)[3] + 15
                    else:
                        rel[f.get('Id')] = (n, x + 40 + 80 * k, top)  # LFO Phase above the Rotation pin
            else:
                rel[pad.get('Id')] = (n, x, top)
            top_prev = top
            gap = 0.0
        n_out = len(outs)
        step_out = (nw - 5) / (n_out - 1) if n_out > 1 else 0
        y = 40.0
        gap = 0.0
        for side, j, cnt, pad, s in reversed(outs):
            if pad is None:
                gap = 30.0
                continue
            _, _, w, h = bounds(pad)
            y += gap
            rel[pad.get('Id')] = (n, 2 + j * step_out, y)
            y += max(h + 15, 30)
            gap = 0.0

    # other nodes feeding an input pin of a Qt node (e.g. a Reactive Channel on a channel pin), and the nodes upstream of
    # them, keep the offset to that Qt node they were placed at, their pads with them; they get no row of their own
    feeders = {}
    for n in plan:
        todo = [n]
        while todo:
            m = todo.pop()
            for pin in m.findall('Pin'):
                if pin.get('Kind') != 'InputPin':
                    continue
                for x in peers.get(pin.get('Id'), []):
                    f = pin_owner.get(x)
                    if f is None or f in feeders or f in plan or f in chain_nodes or node_type(f) is not None:
                        continue
                    if any(e is not f and e.get('Bounds') for e in f.iter()):
                        continue  # regions keep their row (the row move takes their content along)
                    feeders[f] = n
                    todo.append(f)
    for f, q in feeders.items():
        qx, qy, _, _ = bounds(q)
        f_pins = {mp.get('Id') for mp in f.findall('Pin')}
        f_pads = [p for pid, p in pads.items() if pid not in rel and any(x in f_pins for x in peers.get(pid, []))]
        for e in [f] + f_pads:
            ex, ey, _, _ = bounds(e)
            elems[e.get('Id')] = e
            rel[e.get('Id')] = (q, ex - qx, ey - qy)
        chain_nodes.add(f)

    # pads of other nodes keep their offset to that node
    for p in pads.values():
        if p.get('Id') in rel:
            continue
        owners = [pin_owner[x] for x in peers.get(p.get('Id'), []) if x in pin_owner]
        if len(owners) == 1:
            px, py, _, _ = bounds(p)
            nx, ny, _, _ = bounds(owners[0])
            rel[p.get('Id')] = (owners[0], px - nx, py - ny)

    # ---- extents per node, then rows ----
    def extent(n):
        nx, ny, nw, nh = bounds(n)
        top, bottom, right = 0.0, nh, nw
        for pid, (o, dx, dy) in rel.items():
            if o is n:
                _, _, w, h = bounds(elems[pid])
                top = min(top, dy)
                bottom = max(bottom, dy + h)
                label = 0 if w >= 400 else 110
                right = max(right, dx + w + label)
        return top, bottom, right

    free = [p for pid, p in pads.items() if pid not in rel]
    nodes = [n for n in nodes if n not in chain_nodes]
    floor = max((bounds(p)[1] + bounds(p)[3] for p in free if bounds(p)[1] < min((bounds(n)[1] for n in nodes), default=0)), default=None)
    ordered = sorted(nodes, key=lambda n: (bounds(n)[1], bounds(n)[0]))
    rows = []
    for n in ordered:
        if rows and abs(bounds(n)[1] - bounds(rows[-1][0])[1]) < 40:
            rows[-1].append(n)
        else:
            rows.append([n])
    cur = (floor + 40) if floor is not None else (bounds(ordered[0])[1] + extent(ordered[0])[0] if ordered else 0)
    for row in rows:
        ext = {n: extent(n) for n in row}
        row_y = cur - min(e[0] for e in ext.values())
        x_min = None
        for n in sorted(row, key=lambda n: bounds(n)[0]):
            nx, ny, nw, nh = bounds(n)
            x = nx if x_min is None else max(nx, x_min)
            if [x, row_y] != [nx, ny]:
                set_bounds(n, [x, row_y, nw, nh]); changes += 1
                # regions (ForEach, If, ...): control points and inner nodes/pads move along
                for e in n.iter():
                    if e is not n and e.get('Bounds'):
                        b = bounds(e)
                        b[0] += x - nx; b[1] += row_y - ny
                        set_bounds(e, b)
            x_min = x + ext[n][2] + 30
        cur = row_y + max(e[1] for e in ext.values()) + 50

    for pid, (o, dx, dy) in rel.items():
        nx, ny, _, _ = bounds(o)
        p = elems[pid]
        _, _, w, h = bounds(p)
        nb = [nx + dx, ny + dy, w, h]
        if [round(v) for v in nb] != [round(v) for v in bounds(p)]:
            set_bounds(p, nb); changes += 1
    return changes

def layout(raw, specs, add=True, animate=()):
    """Lays out the patch bytes `raw`; returns (number of changes, new file bytes)."""
    text = raw.decode('utf-8-sig')
    root = ET.fromstring(text, parser=ET.XMLParser(target=ET.TreeBuilder(insert_comments=True)))
    before = {e.get('Id'): e.get('Bounds') for e in root.iter() if e.get('Id') and e.get('Bounds')}
    changes = sum(process_patch(patch, specs, add, animate) for patch in list(root.iter('Patch')))
    if PRUNE_ONLY:  # keep every position, only the removed pads count
        after = {e.get('Id'): e for e in root.iter() if e.get('Id') and e.get('Bounds')}
        for i, e in after.items():
            if i in before:
                e.set('Bounds', before[i])
        changes = sum(1 for i in before if i not in after) + len(HIDDEN)
        HIDDEN.clear()
    ET.indent(root, space='  ')
    out = ET.tostring(root, encoding='unicode')
    out = out.replace(' />', ' />').replace('"/>', '" />')
    out = '<?xml version="1.0" encoding="utf-8"?>\n' + out + '\n'
    out = out.replace('\r\n', '\n').replace('\n', '\r\n')
    out = out.replace('&#13;', '&#xD;').replace('&#10;', '&#xA;')  # line breaks in values as vvvv writes them
    return changes, b'\xef\xbb\xbf' + out.encode('utf-8')

def process_file(path, specs, add, check, animate=()):
    changes, out = layout(open(path, 'rb').read(), specs, add, animate)
    print(f'{os.path.basename(path)}: {changes} changes')
    if changes and not check:
        open(path, 'wb').write(out)
    return changes

if __name__ == '__main__':
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    animate = next((a.split('=', 1)[1].split(',') for a in sys.argv if a.startswith('--animate=')), [])
    PRUNE_ONLY = '--prune-only' in sys.argv
    PRUNE_DEFAULTS = PRUNE_ONLY or '--prune-defaults' in sys.argv
    PAD_PINS.update(next((a.split('=', 1)[1].split(',') for a in sys.argv if a.startswith('--pads=')), []))
    specs = load_specs()
    changed = [f for f in args if process_file(f, specs, '--no-add' not in sys.argv, '--check' in sys.argv, animate)]
    if '--check' in sys.argv and changed:
        sys.exit(1)  # --check: exit code 1 = the layout of at least one file would change
