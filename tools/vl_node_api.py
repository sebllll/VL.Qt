"""The nodes of VL.Qt and their pins, read from the node-API snapshot src/VL.Qt.Tests/Api/node-api.txt.

The snapshot (written by NodeApiSnapshotTests from the assemblies) is the one description of the API; the patch tools
read it and never the C# sources. One fact per line: `<assembly> | <type> | <member> | <fact> | ...`. Used by
`tools/vl-patch-check.py` (catalog of pins) and `tools/vl-patch-migrate.py` (lines, pin names).

Usage: python tools/vl_node_api.py [--json]   (prints the catalog)

From the snapshot the catalog takes every class with [ProcessNode(...)] and, per pin line of its constructor (create
pins) and of Update(...) (return pin included): the vvvv name, the type, input/output, the default (the value after
`=`; none: the default of the type, the member with value 0 for an enum), whether it is optional and the pin
group (PinGroupKind.Collection).
The NodeContext and hidden pins are left out.
"""
import json, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_API = os.path.join(ROOT, 'src', 'VL.Qt.Tests', 'Api', 'node-api.txt')
# Value of a pin without a default, as vvvv writes it in an IOBox (None: unknown, never compared).
TYPE_DEFAULTS = {
    'bool': 'False', 'float': '0', 'double': '0', 'int': '0', 'long': '0', 'uint': '0', 'byte': '0',
    'string': '', 'Vector2': '0, 0', 'Vector3': '0, 0, 0', 'Vector4': '0, 0, 0, 0', 'Int2': '0, 0',
    'Color4': '0, 0, 0, 0', 'RectangleF': '0, 0, 0, 0',
}
TEXT_TYPES = {'string', 'Path'}  # compared as a whole, not split at commas

_PIN_LINE = re.compile(r'(?P<dir>in|out) (?P<rest>.*?)(?: \| (?P<attrs>Pin\(.*\)))? \| \((?P<csharp>\w+)\)$')
_TYPE_DEFAULT = re.compile(r'(?P<type>.+?)(?: = (?P<default>.*))?$')


def title_case(name):
    """C# parameter name -> vvvv pin name: `vSync` -> `V Sync`, `strokeColor` -> `Stroke Color`."""
    name = name[0].upper() + name[1:]
    return re.sub(r'(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])', ' ', name)


def read_lines(path=DEFAULT_API):
    """The snapshot as lists of fields (a text default may contain ` | `: only the first fields are reliable)."""
    with open(path, encoding='utf-8') as f:
        return [line.rstrip('\n').split(' | ') for line in f if line.strip()]


def _default_text(raw, typ):
    """Default of a pin line -> IOBox text (a string is unquoted, a bool capitalized), or None."""
    if raw is None or raw == 'null':
        return None
    if raw.startswith('"'):
        return json.loads(raw)
    return raw.capitalize() if typ == 'bool' else raw


def _short(typ):
    return typ.rsplit('.', 1)[-1] if '<' not in typ else typ


def load_catalog(path=DEFAULT_API):
    """Node name -> node dict (name, category, csharp class, pins, overloads). Nodes of the same name and category
    (like the three ArcTo forms) are kept in `overloads` of the first one, see `all_forms`/`resolve_overload`."""
    lines = read_lines(path)
    enums = {}  # full type name -> member with value 0
    for f in lines:
        if len(f) >= 4 and f[2].startswith('member ') and f[2].endswith(' = 0') and f[3:] == [] or \
                len(f) == 3 and f[2].startswith('member ') and f[2].endswith(' = 0'):
            enums[f[1]] = f[2][len('member '):-len(' = 0')]
    classes = {}
    for f in lines:
        if len(f) >= 3 and f[2].startswith('attr ProcessNode('):
            name = re.search(r'Name\s*=\s*"([^"]+)"', f[2])
            cat = re.search(r'Category\s*=\s*"([^"]+)"', f[2])
            cls = f[1]
            classes[cls] = dict(name=name.group(1) if name else re.sub(r'Node$', '', cls.rsplit('.', 1)[-1]),
                                category=cat.group(1) if cat else 'Qt', csharp=cls, pins=[])
    for f in lines:
        node = classes.get(f[1]) if len(f) >= 4 else None
        if node is None or f[2] not in ('ctor', 'method Update') or not f[3].startswith('pin '):
            continue
        m = _PIN_LINE.fullmatch(' | '.join(f[4:]))
        if m is None:
            raise ValueError(f'{path}: cannot read the pin line {" | ".join(f)[:200]}')
        attrs = m.group('attrs') or ''
        td = _TYPE_DEFAULT.fullmatch(m.group('rest'))
        full = td.group('type')
        if full.rsplit('.', 1)[-1] == 'NodeContext' or 'PinVisibility.Hidden' in attrs:
            continue
        typ, direction = _short(full), m.group('dir')
        default = _default_text(td.group('default'), typ) if direction == 'in' else None
        if direction == 'in' and default is None:
            default = TYPE_DEFAULTS.get(typ, enums.get(full))
        node['pins'].append(dict(name=f[3][len('pin '):], type=typ, direction=direction, default=default,
                                 optional='PinVisibility.Optional' in attrs, group='PinGroupKind.Collection' in attrs,
                                 create=f[2] == 'ctor'))
    catalog = {}
    for node in classes.values():
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
            print(f"{node['name']} [{node['category']}]  ({node['csharp']})")
            for p in node['pins']:
                flags = [f for f, on in (('create', p['create']), ('optional', p['optional']), ('group', p['group'])) if on]
                default = f" = {p['default']!r}" if p['default'] is not None else ''
                print(f"  {p['direction']:3} {p['name']}: {p['type']}{default}{'  (' + ', '.join(flags) + ')' if flags else ''}")
