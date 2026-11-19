"""Pin catalog of the VL.Qt process nodes, read from the C# sources.

Independent of the .vl file format: it only reads C#, so it can be reused with vvvv's next file format or behind an
MCP interface. `tools/vl-patch-check.py` is its first user.

Usage: python tools/vl_pin_catalog.py [--json]   (prints the catalog)

Reads every node class in src/VL.Qt/Nodes/**/*Node.cs, src/VL.Qt/Renderer.cs , src/VL.Qt.Stride/*Node.cs and src/VL.Qt.Audio/*Node.cs (classes with
[ProcessNode(...)]):
  - the pins of the public constructor (create pins) and of Update(...); the hidden NodeContext is skipped;
  - per pin: vvvv name ([Pin(Name = "...")], otherwise the C# name in title case with spaces), type, input/output,
    optional (PinVisibility.Optional), default ([DefaultValue] or the C# parameter default), pin group
    (PinGroupKind.Collection), create pin or not;
  - the return pin of Update ([return: Pin(Name = "...")], otherwise "Output");
  - forbidden-IOBox pins (`Interaction`: an IOBox there makes the element opaque to input);
  - pins the node writes into the document itself (`Bounds` of Renderer, Save Bounds).
"""
import glob, json, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
NODE_SOURCES = ('src/VL.Qt/Nodes/**/*Node.cs', 'src/VL.Qt/Renderer.cs', 'src/VL.Qt.Stride/*Node.cs', 'src/VL.Qt.Audio/*Node.cs')
# Folders whose constants and enums resolve pin defaults (Stride's own enums, like PixelFormat, are not here: their
# defaults are written as nameof(...) and need no lookup).
CONSTANT_SOURCES = ('src/VL.Qt/**/*.cs', 'src/VL.Qt.Stride/**/*.cs', 'src/VL.Qt.Audio/**/*.cs')
# An IOBox on these pins is an error: an IOBox on Interaction makes the element opaque to input.
FORBIDDEN_IOBOX = {'Interaction'}
# Pins whose value the node itself writes into the document (node name, pin name): Save Bounds of the Renderer stores
# the window geometry in the hidden Bounds pin (#143), so a value there is expected.
WRITTEN_BY_NODE = {('Renderer', 'Bounds')}
CATEGORIES = {  # src/VL.Qt/Categories.cs (QtTexture of VL.Qt.Stride uses Categories.Qt too)
    'Qt': 'Qt', 'Elements': 'Qt.Elements', 'Widgets': 'Qt.Widgets', 'Interaction': 'Qt.Interaction',
    'Effects': 'Qt.Effects', 'MultiEffect': 'Qt.Effects.MultiEffect', 'Paths': 'Qt.Paths',
}
# Value of a pin without a default, as vvvv writes it in an IOBox (None: unknown, never compared).
TYPE_DEFAULTS = {
    'bool': 'False', 'float': '0', 'double': '0', 'int': '0', 'long': '0', 'uint': '0', 'byte': '0',
    'string': '', 'Vector2': '0, 0', 'Vector3': '0, 0, 0', 'Vector4': '0, 0, 0, 0', 'Int2': '0, 0',
    'Color4': '0, 0, 0, 0', 'RectangleF': '0, 0, 0, 0',
}
TEXT_TYPES = {'string', 'Path'}  # compared as a whole, not split at commas


def title_case(name):
    """C# parameter name -> vvvv pin name: `vSync` -> `V Sync`, `strokeColor` -> `Stroke Color`."""
    name = name[0].upper() + name[1:]
    return re.sub(r'(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])', ' ', name)


# ---- C# reading ----------------------------------------------------------------------------------------------------

def strip_comments(src):
    """Removes // and /* */ comments, keeps string literals."""
    out, i, n = [], 0, len(src)
    while i < n:
        c = src[i]
        if c == '"':
            j = i + 1
            while j < n and src[j] != '"':
                j += 2 if src[j] == '\\' else 1
            out.append(src[i:j + 1]); i = j + 1
        elif src.startswith('//', i):
            j = src.find('\n', i)
            i = n if j < 0 else j
        elif src.startswith('/*', i):
            j = src.find('*/', i + 2)
            i = n if j < 0 else j + 2
        else:
            out.append(c); i += 1
    return ''.join(out)


def _closing(src, i):
    """Index of the bracket closing the one at src[i]; skips string literals."""
    pairs = {'(': ')', '[': ']', '{': '}', '<': '>'}
    stack, j = [], i
    while j < len(src):
        c = src[j]
        if c == '"':
            j += 1
            while src[j] != '"':
                j += 2 if src[j] == '\\' else 1
        elif c in '([{':
            stack.append(pairs[c])
        elif c in ')]}':
            stack.pop()
            if not stack:
                return j
        j += 1
    raise ValueError('unbalanced brackets')


def split_top(s, sep=','):
    """Splits at `sep` outside brackets, generics and strings."""
    parts, depth, cur, i = [], 0, '', 0
    while i < len(s):
        c = s[i]
        if c == '"':
            j = i + 1
            while s[j] != '"':
                j += 2 if s[j] == '\\' else 1
            cur += s[i:j + 1]; i = j + 1; continue
        if c in '([{<':
            depth += 1
        elif c in ')]}>':
            depth -= 1
        if c == sep and depth == 0:
            parts.append(cur); cur = ''
        else:
            cur += c
        i += 1
    parts.append(cur)
    return [' '.join(p.split()) for p in parts if p.strip()]


def load_constants(root):
    """`const string Name = "..."` and enums of src/VL.Qt and src/VL.Qt.Stride, to resolve defaults like `WindowPlacement.DefaultBoundsString`."""
    consts, enums = {}, {}
    files = [f for pattern in CONSTANT_SOURCES for f in glob.glob(os.path.join(root, pattern), recursive=True)]
    for f in sorted(set(files)):
        src = strip_comments(open(f, encoding='utf-8-sig').read())
        for m in re.finditer(r'const\s+\w+\s+(\w+)\s*=\s*("(?:[^"\\]|\\.)*"|[^;]+);', src):
            consts[m.group(1)] = m.group(2).strip()
        for m in re.finditer(r'\benum\s+(\w+)\s*(?::\s*\w+)?\s*\{([^}]*)\}', src):
            members = [re.sub(r'\[[^\]]*\]', '', x).split('=')[0].strip() for x in m.group(2).split(',')]
            members = [x for x in members if x]
            if members:
                enums[m.group(1)] = members
    return consts, enums


def _literal(expr, consts):
    """C# default expression -> IOBox text, or None when unknown/null."""
    expr = expr.strip()
    if expr in ('null', 'default') or expr.startswith('default('):
        return None
    m = re.fullmatch(r'"((?:[^"\\]|\\.)*)"', expr)
    if m:
        return m.group(1).encode().decode('unicode_escape')
    if expr in ('true', 'false'):
        return expr.capitalize()
    m = re.fullmatch(r'(-?[\d.]+(?:[eE][-+]?\d+)?)[fFdDmMuUlL]*', expr)
    if m:
        return m.group(1)
    m = re.fullmatch(r'nameof\(([\w.]+)\)', expr)
    if m:
        return m.group(1).split('.')[-1]
    m = re.fullmatch(r'(?:[\w]+\.)*(\w+)', expr)
    if m:
        if m.group(1) in consts:
            return _literal(consts[m.group(1)], consts)
        if '.' in expr:  # enum member, e.g. Space.DIPTopLeft
            return m.group(1)
    return None


def _default_value(attrs, consts):
    """Value of [DefaultValue(...)] in an attribute list; (found, value)."""
    m = re.search(r'DefaultValue\(', attrs)
    if not m:
        return False, None
    start = m.end() - 1
    args = split_top(attrs[start + 1:_closing(attrs, start)])
    if len(args) == 2 and args[0].startswith('typeof('):
        args = args[1:]
    return True, _literal(args[0], consts) if args else None


def parse_param(p, consts, enums):
    """One C# parameter -> pin dict, or None for hidden pins (NodeContext)."""
    attrs = ''
    while p.startswith('['):
        end = _closing(p, 0)
        attrs += p[:end + 1]
        p = p[end + 1:].strip()
    if 'PinVisibility.Hidden' in attrs:
        return None
    default_expr = None
    if '=' in p:
        p, default_expr = [x.strip() for x in p.split('=', 1)]
    words = p.split()
    direction = 'in'
    while words and words[0] in ('out', 'ref', 'in', 'params', 'this', 'scoped'):
        if words[0] == 'out':
            direction = 'out'
        words = words[1:]
    typ, cs_name = ' '.join(words[:-1]), words[-1]
    typ = typ.rstrip('?')
    if typ.split('.')[-1] == 'NodeContext':
        return None
    named = re.search(r'\bPin\([^)]*\bName\s*=\s*"([^"]+)"', attrs)
    has_attr, default = _default_value(attrs, consts)
    if not has_attr and default_expr is not None:
        default = _literal(default_expr, consts)
    if default is None:
        default = TYPE_DEFAULTS.get(typ)
        if default is None and typ in enums:
            default = enums[typ][0]
    return dict(
        name=named.group(1) if named else title_case(cs_name),
        csharp=cs_name,
        type=typ,
        direction=direction,
        optional='PinVisibility.Optional' in attrs,
        default=default if direction == 'in' else None,
        group='PinGroupKind.Collection' in attrs,
        forbid_iobox=False,
    )


def _method(src, start, end, pattern):
    """First match of `pattern` (ending at the open paren) in src[start:end] -> (match, parameter text)."""
    m = re.compile(pattern, re.S).search(src, start, end)
    if m is None:
        return None, None
    open_paren = m.end() - 1
    return m, src[open_paren + 1:_closing(src, open_paren)]


def parse_file(path, consts, enums):
    """All [ProcessNode] classes of one C# file -> list of node dicts."""
    src = strip_comments(open(path, encoding='utf-8-sig').read())
    nodes = []
    attrs = list(re.finditer(r'\[ProcessNode\(', src))
    for k, a in enumerate(attrs):
        args = src[a.end():_closing(src, a.end() - 1)]
        name = re.search(r'Name\s*=\s*"([^"]+)"', args)
        cat = re.search(r'Category\s*=\s*(?:Categories\.(\w+)|"([^"]+)")', args)
        cls = re.compile(r'\bclass\s+(\w+)').search(src, a.end())
        if cls is None:
            continue
        end = attrs[k + 1].start() if k + 1 < len(attrs) else len(src)
        node_name = name.group(1) if name else re.sub(r'Node$', '', cls.group(1))
        category = (CATEGORIES.get(cat.group(1), cat.group(1)) if cat.group(1) else cat.group(2)) if cat else 'Qt'
        pins = []
        _, ctor = _method(src, cls.end(), end, r'\bpublic\s+' + cls.group(1) + r'\s*\(')
        for p in split_top(ctor or ''):
            pin = parse_param(p, consts, enums)
            if pin:
                pin['create'] = True
                pins.append(pin)
        upd, params = _method(src, cls.end(), end, r'((?:\[[^\]]*\]\s*)*)public\s+([\w.<>,?\[\] ]+?)\s+Update\s*\(')
        if upd is not None:
            for p in split_top(params):
                pin = parse_param(p, consts, enums)
                if pin:
                    pin['create'] = False
                    pins.append(pin)
            ret_type = upd.group(2).strip()
            if ret_type != 'void':
                ret = re.search(r'return:\s*Pin\([^)]*\bName\s*=\s*"([^"]+)"', upd.group(1))
                pins.append(dict(name=ret.group(1) if ret else 'Output', csharp='return', type=ret_type.rstrip('?'),
                                 direction='out', optional=False, default=None, group=False, forbid_iobox=False,
                                 create=False))
        for pin in pins:
            pin['forbid_iobox'] = pin['direction'] == 'in' and pin['name'] in FORBIDDEN_IOBOX
            pin['written_by_node'] = (node_name, pin['name']) in WRITTEN_BY_NODE
        nodes.append(dict(name=node_name, category=category, csharp=cls.group(1),
                          source=os.path.relpath(path, ROOT).replace(os.sep, '/'), pins=pins))
    return nodes


def load_catalog(root=ROOT):
    """Node name -> node dict (name, category, csharp class, source, pins, overloads). Nodes of the same name and
    category (like the three ArcTo forms) are kept in `overloads` of the first one, see `all_forms`/`resolve_overload`."""
    consts, enums = load_constants(root)
    files = []
    for pattern in NODE_SOURCES:
        files += sorted(glob.glob(os.path.join(root, pattern), recursive=True))
    catalog = {}
    for f in files:
        for node in parse_file(f, consts, enums):
            node['overloads'] = []
            first = catalog.get(node['name'])
            if first is not None and first['category'] == node['category']:
                first['overloads'].append(node)  # same name and category: an overload, told apart by its pins
            else:
                catalog[node['name']] = node
    return catalog


def all_forms(node):
    """The node and its overloads (nodes of the same name and category that differ in their pins)."""
    return [node] + node.get('overloads', [])


def resolve_overload(node, pin_names):
    """Node-like dict whose `pins` fit the pin names found in a patch: the overload with the most matching names;
    on a tie, the union of the tied overloads' pins (first one wins per name), so no pin is reported missing wrongly."""
    forms = all_forms(node)
    if len(forms) == 1:
        return node
    scores = [sum(find_pin(f, n) is not None for n in pin_names) for f in forms]
    best = [f for f, sc in zip(forms, scores) if sc == max(scores)]
    if len(best) == 1:
        return best[0]
    pins, seen = [], set()
    for f in best:
        for pin in f['pins']:
            if pin['name'] not in seen:
                seen.add(pin['name'])
                pins.append(pin)
    return dict(best[0], pins=pins)


def find_pin(node, pin_name):
    """Catalog pin for a pin name as vvvv writes it (`Input 3` belongs to the pin group `Input`), or None."""
    for pin in node['pins']:
        if pin['name'] == pin_name:
            return pin
    m = re.fullmatch(r'(.+) \d+', pin_name)
    if m:
        for pin in node['pins']:
            if pin['group'] and pin['name'] == m.group(1):
                return pin
    return None


# ---- values --------------------------------------------------------------------------------------------------------

def _num(x):
    x = x.strip()
    x = re.sub(r'(?<=\d)[fFdDmM]$', '', x)
    return float(x)


def same_value(a, b, typ):
    """True if two IOBox texts mean the same value of type `typ` (numbers with tolerance, enums by member name)."""
    if a is None or b is None:
        return False
    if typ in TEXT_TYPES:
        return a == b
    xs, ys = [x.strip() for x in a.split(',')], [y.strip() for y in b.split(',')]
    if len(xs) != len(ys):
        return False
    for x, y in zip(xs, ys):
        try:
            if abs(_num(x) - _num(y)) > 1e-6:
                return False
        except ValueError:
            if x.split('.')[-1].lower() != y.split('.')[-1].lower():
                return False
    return True


def is_default(value, pin):
    """True/False if the IOBox text `value` (None: no value written = type default) is the pin's default; None if
    the default is unknown (then the caller compares nothing)."""
    if pin['default'] is None:
        return None
    if value is None:
        value = TYPE_DEFAULTS.get(pin['type'])
        if value is None:
            return None
    return same_value(value, pin['default'], pin['type'])


if __name__ == '__main__':
    catalog = load_catalog()
    if '--json' in sys.argv:
        json.dump(catalog, sys.stdout, indent=2)
        print()
    else:
        for node in (form for first in catalog.values() for form in all_forms(first)):
            print(f"{node['name']} [{node['category']}]  ({node['source']})")
            for p in node['pins']:
                flags = [f for f, on in (('create', p['create']), ('optional', p['optional']), ('group', p['group']),
                                         ('no IOBox', p['forbid_iobox']),
                                         ('written by node', p['written_by_node'])) if on]
                default = f" = {p['default']!r}" if p['default'] is not None else ''
                print(f"  {p['direction']:3} {p['name']}: {p['type']}{default}{'  (' + ', '.join(flags) + ')' if flags else ''}")
