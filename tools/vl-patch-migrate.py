"""Migrates the VL.Qt references in vvvv patches (.vl) by the rows of one mapping file: renames nodes and pins and
moves categories, nothing else.

Usage:
  python tools/vl-patch-migrate.py [<patch.vl|glob> ...]     apply the mapping (default: every patch of the repository)
  python tools/vl-patch-migrate.py --check [...]             list what would change, write nothing
Options: --mapping=<file> (default tools/vl-patch-mapping.txt), --api=<file> (default src/VL.Qt.Tests/Api/node-api.txt),
         --no-api-check (do not look the new names up in the snapshot), --quiet (no summary line).
Exit codes: 0 nothing to do, or applied; 1 with --check: patches would change; 2 an error (a bad, conflicting or unknown
row, a node whose state cannot be told, a patch that cannot be read). On an error nothing is written.

Default patches: help/**/*.vl, the .vl files in the repository root (VL.Qt.vl, VL.Qt.HDE.vl), VL.Qt.Stride/**/*.vl,
VL.Qt.Audio/**/*.vl and src/**/*.vl (without bin/ and obj/): every patch that can use a VL.Qt node.

The mapping file (format in its header) holds rows `step | kind | scope | old | new`. The rows of one step are applied
together, so a swap like Radius -> Set Radius plus Get Radius -> Radius works; steps are applied in the order of the
file. Only references to VL.Qt (LastDependency VL.Qt*) are touched: the node name (Choice), its pins (Pin, PinReference),
the category (LastCategoryFullName, the CategoryReference chain, FullNameCategoryReference) and, for category moves, the
type references (TypeAnnotation, TypeReference). Everything else, including CRLF, the BOM, Ids and positions, stays byte
for byte.

Which steps a node has seen is read from the node itself; nothing is recorded. Every name a row mentions has a lifetime
over the steps: an old name lives until its step, a new name from its step on, a name that is old and new in one step
lives on. For each version v (v steps applied) the script checks whether the node's identity (category and name) and its
pin names are all alive at v; it applies the steps after v. When every fitting version gives the same result, that
result is written; when none fits or two give different results, the node is reported and nothing is written. So a run
over migrated patches changes nothing, a patch can mix nodes of different versions (after a git merge), and a .vl file
is derived from the mapping: on a conflict, merge the mapping file, take either side of the patch and run the script.
Rows are never edited or removed once merged, since the lifetimes are read from them.
"""
import glob, html, os, re, sys
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOOLS = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, TOOLS)
import vl_node_api as node_api  # noqa: E402

DEFAULT_MAPPING = os.path.join(TOOLS, 'vl-patch-mapping.txt')
DEFAULT_API = node_api.DEFAULT_API
DEFAULT_PATCHES = ('help/**/*.vl', '*.vl', 'VL.Qt.Stride/**/*.vl', 'VL.Qt.Audio/**/*.vl', 'src/**/*.vl')
SKIP_DIRS = {'bin', 'obj'}
QT_DEPENDENCY = re.compile(r'VL\.Qt(\.|$)')
KINDS = ('node', 'pin', 'category')
BOM = b'\xef\xbb\xbf'


class MigrateError(Exception):
    pass


def pin_name(name):
    """Pin name of a row: a C# parameter name (fillColor) becomes the vvvv name (Fill Color) by the rule of the snapshot test."""
    return node_api.title_case(name) if name[:1].islower() else name


def esc(value):
    return escape(value, {'"': '&quot;'})


# ---- mapping -------------------------------------------------------------------------------------------------------

class Row:
    def __init__(self, line, step, kind, scope, old, new):
        self.line, self.step, self.kind, self.scope, self.old, self.new = line, step, kind, scope, old, new
        # pin rows: (category or None, node or None); node rows: category or None; category rows: symbol or None
        if kind == 'pin':
            cat, _, node = scope.rpartition('/') if '/' in scope else ('*', '', scope)
            self.where = (None if cat == '*' else cat, None if node == '*' else node)
        else:
            self.where = None if scope == '*' else scope


class Step:
    """The rows of one step, applied together."""

    def __init__(self, sid, rows):
        self.id = sid
        self.cats = [r for r in rows if r.kind == 'category']
        self.nodes = [r for r in rows if r.kind == 'node']
        self.pins = [r for r in rows if r.kind == 'pin']

    def category(self, kind, cat, name):
        best, rank = None, None
        for r in self.cats:
            if r.where is not None:
                if r.where != name or cat != r.old:
                    continue
                rk = (1, len(r.old))
            elif cat == r.old or cat.startswith(r.old + '.'):
                rk = (0, len(r.old))
            else:
                continue
            if rank is None or rk > rank:
                best, rank = r, rk
        return cat if best is None else best.new + cat[len(best.old):]

    def name(self, kind, cat, name):
        if kind != 'node':
            return name
        best = None
        for r in self.nodes:
            if r.old == name and r.where in (None, cat) and (best is None or best.where is None):
                best = r
        return name if best is None else best.new

    def apply(self, ident):
        kind, cat, name = ident
        return kind, self.category(kind, cat, name), self.name(kind, cat, name)

    def preimages(self, ident):
        """Identities other than `ident` that this step turns into `ident`."""
        kind, cat, name = ident
        cats = {cat} | {r.old + cat[len(r.new):] for r in self.cats if cat == r.new or cat.startswith(r.new + '.')}
        names = {name} | ({r.old for r in self.nodes if r.new == name} if kind == 'node' else set())
        return {(kind, c, n) for c in cats for n in names if (kind, c, n) != ident and self.apply((kind, c, n)) == ident}

    def pin_map(self, ident):
        """Old pin name -> new pin name for a node identity; the most specific scope wins."""
        kind, cat, name = ident
        if kind != 'node':
            return {}
        best = {}
        for r in self.pins:
            rcat, rnode = r.where
            if (rnode is not None and rnode != name) or (rcat is not None and rcat != cat):
                continue
            spec = (rnode is not None) + (rcat is not None)
            if r.old not in best or spec > best[r.old][0]:
                best[r.old] = (spec, r.new)
        return {old: new for old, (_, new) in best.items()}


def load_mapping(path):
    """Steps of the mapping file and the errors in it."""
    rows, errors = [], []
    where = display(path)
    for no, line in enumerate(open(path, encoding='utf-8'), 1):
        text = line.strip()
        if not text or text.startswith('#'):
            continue
        fields = [f.strip() for f in text.split('|')]
        at = f'{where}:{no}'
        if len(fields) != 5:
            errors.append(f'{at}: expected 5 fields (step | kind | scope | old | new), found {len(fields)}')
            continue
        step, kind, scope, old, new = fields
        if not all(fields):
            errors.append(f'{at}: empty field')
            continue
        if kind not in KINDS:
            errors.append(f'{at}: unknown kind "{kind}" (node, pin or category)')
            continue
        if kind == 'pin':
            old, new = pin_name(old), pin_name(new)
            if scope.count('/') > 1:
                errors.append(f'{at}: pin scope is Category/Node, Node or *')
                continue
        if kind == 'category':
            bad = [c for c in (old, new) if not re.fullmatch(r'[^.\s]+(\.[^.\s]+)*', c)]
            if bad:
                errors.append(f'{at}: "{bad[0]}" is not a category')
                continue
        if old == new:
            errors.append(f'{at}: old and new are the same')
            continue
        rows.append(Row(no, step, kind, scope, old, new))

    order, by_step, prev = [], {}, None
    for r in rows:
        if r.step != prev and r.step in by_step:
            errors.append(f'{where}:{r.line}: the rows of step {r.step} are not together (a merge put them apart)')
        if r.step not in by_step:
            order.append(r.step)
        by_step.setdefault(r.step, []).append(r)
        prev = r.step
    for sid in order:
        olds, news = {}, {}
        for r in by_step[sid]:
            key = (r.kind, r.scope, r.old)
            if key in olds:
                errors.append(f'{where}:{r.line}: conflicts with line {olds[key].line}: step {sid} renames {r.kind} '
                              f'"{r.old}" ({r.scope}) twice')
            olds.setdefault(key, r)
            if r.kind != 'category':
                key = (r.kind, r.scope, r.new)
                if key in news:
                    errors.append(f'{where}:{r.line}: conflicts with line {news[key].line}: two {r.kind}s of step '
                                  f'{sid} become "{r.new}" ({r.scope})')
                news.setdefault(key, r)
    return [Step(sid, by_step[sid]) for sid in order], errors


# ---- node-API snapshot ---------------------------------------------------------------------------------------------

def import_category(attribute, ns):
    """Category of the types of namespace ns under [assembly: ImportAsIs(Namespace, Category)], computed as VL.Core does
    (ImportAsIsAttribute.GetCategory); None for a namespace the attribute does not import."""
    root, cat = attribute
    if root and not (ns == root or ns.startswith(root + '.')):
        return None
    rest = ns[len(root) + 1:] if root else ns
    return '.'.join(x for x in (cat, rest) if x)


def load_api(path):
    """Node name -> pin names (process nodes, generated nodes, operations), the categories of the snapshot and the
    (category, name) of its process nodes and the pin names per (category, name) where the category is known."""
    lines = [line.rstrip('\n').split(' | ') for line in open(path, encoding='utf-8')]
    nodes, cats, process, imports, processes = {}, set(), {}, {}, set()
    catpins, typecat = {}, {}
    for f in lines:
        if len(f) >= 3 and f[1] == 'assembly' and f[2].startswith('attr ImportAsIs'):
            ns = re.search(r'Namespace\s*=\s*"([^"]*)"', f[2])
            cat = re.search(r'Category\s*=\s*"([^"]*)"', f[2])
            imports[f[0]] = (ns.group(1) if ns else '', cat.group(1) if cat else '')
    for f in lines:
        if len(f) >= 3 and f[2].startswith('attr ProcessNode('):
            name = re.search(r'Name\s*=\s*"([^"]+)"', f[2])
            cat = re.search(r'Category\s*=\s*"([^"]+)"', f[2])
            process[f[1]] = name.group(1) if name else re.sub(r'Node$', '', f[1].rsplit('.', 1)[-1])
            cats.add(cat.group(1) if cat else 'Qt')
            typecat[f[1]] = cat.group(1) if cat else 'Qt'
            processes.add((typecat[f[1]], process[f[1]]))
    for f in lines:
        if f[0].startswith('factory '):
            if f[1].startswith('default category '):
                cats.add(f[1][len('default category '):])
            elif f[1].startswith('node ') and len(f) >= 3:
                pins = nodes.setdefault(f[1][len('node '):], set())
                if f[2].startswith('category '):
                    cats.add(f[2][len('category '):])
                elif f[2].startswith('pin '):
                    pins.add(f[2][len('pin '):])
            continue
        if len(f) < 3:
            continue
        typ = f[1]
        if f[2].startswith('attr Category('):
            # [Category] of a static class of operations (UniformNodes): the category of its nodes.
            typecat[typ] = re.search(r'"([^"]*)"', f[2]).group(1)
            cats.add(typecat[typ])
        if f[2].startswith('type '):
            ns, _, name = typ.rpartition('.')
            cat = import_category(imports.get(f[0], ('', '')), ns)
            if cat is not None:
                cats.add(cat)
                cats.add(f'{cat}.{name}' if cat else name)
        if typ in process:
            if (f[2] == 'ctor' or f[2] == 'method Update') and len(f) >= 4:
                pins = nodes.setdefault(process[typ], set())
                if f[3].startswith('pin '):
                    pins.add(f[3][len('pin '):])
                    catpins.setdefault((typecat[typ], process[typ]), set()).add(f[3][len('pin '):])
            continue
        if f[2].startswith('method ') and len(f) >= 4:
            pins = nodes.setdefault(f[2][len('method '):], set())
            if f[3].startswith('pin '):
                pins.add(f[3][len('pin '):])
                if typ in typecat:
                    catpins.setdefault((typecat[typ], f[2][len('method '):]), set()).add(f[3][len('pin '):])
    for name in process.values():
        nodes.setdefault(name, set())
    return nodes, cats, processes, catpins


def check_api(steps, path, mapping_path):
    """Errors for rows whose new name, carried through the later steps, is not in the snapshot."""
    if not os.path.isfile(path):
        return [f'{display(path)}: node-API snapshot not found (or use --no-api-check)']
    nodes, cats, _, _ = load_api(path)
    errors = []

    def node_after(step, name):
        return next((r.new for r in step.nodes if r.old == name), name)

    def category_after(step, cat):
        rows = [r for r in step.cats if r.where is None and (cat == r.old or cat.startswith(r.old + '.'))]
        best = max(rows, key=lambda r: len(r.old), default=None)
        return cat if best is None else best.new + cat[len(best.old):]

    for i, step in enumerate(steps):
        at = lambda r: f'{display(mapping_path)}:{r.line}'  # noqa: E731
        for r in step.nodes:
            final = r.new
            for later in steps[i + 1:]:
                final = node_after(later, final)
            if final not in nodes:
                errors.append(f'{at(r)}: unknown node "{final}": not in {display(path)}')
        for r in step.pins:
            # The scope names the node before the step; later pin rows name it as it is called before theirs.
            final_node = node_after(step, r.where[1]) if r.where[1] else None
            final = r.new
            for later in steps[i + 1:]:
                final = next((x.new for x in later.pins if x.old == final and x.where[1] in (None, final_node)), final)
                final_node = node_after(later, final_node) if final_node else None
            if final_node is not None:
                if final_node not in nodes:
                    errors.append(f'{at(r)}: unknown node "{final_node}": not in {display(path)}')
                elif final not in nodes[final_node]:
                    errors.append(f'{at(r)}: unknown pin "{final}" of {final_node}: not in {display(path)}')
            elif not any(final in pins for pins in nodes.values()):
                errors.append(f'{at(r)}: unknown pin "{final}": no node in {display(path)} has it')
        for r in step.cats:
            final = r.new
            for later in steps[i + 1:]:
                final = category_after(later, final)
            if final not in cats and not any(c.startswith(final + '.') for c in cats):
                errors.append(f'{at(r)}: unknown category "{final}": not in {display(path)}')
    return sorted(errors, key=lambda e: int(re.search(r':(\d+): ', e).group(1)))


# ---- versions ------------------------------------------------------------------------------------------------------

def alive(events, v):
    """True if a name lives at version v; events[k] = (renamed away in step k+1, introduced in step k+1)."""
    for k in range(v - 1, -1, -1):
        gone, born = events[k]
        if gone or born:
            return born
    for k in range(v, len(events)):
        gone, born = events[k]
        if gone or born:
            return gone
    return True


def lineage(steps, ident, v):
    """Possible identities at each version 0..n, given `ident` at version v."""
    lin = [None] * (len(steps) + 1)
    lin[v] = {ident}
    for k in range(v + 1, len(steps) + 1):
        lin[k] = {steps[k - 1].apply(i) for i in lin[k - 1]}
    for k in range(v, 0, -1):
        prev = set()
        for i in lin[k]:
            prev |= steps[k - 1].preimages(i) or {i}
        lin[k - 1] = prev
    return lin


def rename_pin(name, mapping):
    if name in mapping:
        return mapping[name]
    m = re.fullmatch(r'(.+) (\d+)', name)  # a pin of a pin group: Input 3
    return f'{mapping[m.group(1)]} {m.group(2)}' if m and m.group(1) in mapping else name


def evaluate(steps, ident, pins):
    """Version -> (identity, pin names) after the pending steps, for every version the node fits."""
    n = len(steps)
    ident_events = [(s.apply(ident) != ident, bool(s.preimages(ident))) for s in steps]
    results = {}
    for v in range(n + 1):
        if not alive(ident_events, v):
            continue
        lin = lineage(steps, ident, v)
        maps = [[steps[k].pin_map(i) for i in lin[k]] for k in range(n)]
        gone = [set().union(*(m.keys() for m in ms)) for ms in maps]
        born = [set().union(*(m.values() for m in ms)) for ms in maps]
        mentioned = set().union(*gone, *born)
        fits = True
        for p in set(pins):
            g = re.fullmatch(r'(.+) \d+', p)
            base = p if p in mentioned else g.group(1) if g and g.group(1) in mentioned else None
            if base is not None and not alive([(base in gone[k], base in born[k]) for k in range(n)], v):
                fits = False
                break
        if not fits:
            continue
        cur, names = ident, list(pins)
        for k in range(v, n):
            mapping = steps[k].pin_map(cur)
            names = [rename_pin(p, mapping) for p in names]
            cur = steps[k].apply(cur)
        results[v] = (cur, tuple(names))
    return results


# ---- .vl text ------------------------------------------------------------------------------------------------------

TOKEN = re.compile(r'<!--.*?-->|<!\[CDATA\[.*?\]\]>|<\?.*?\?>|<!DOCTYPE[^>]*>'
                   r'|<(/?)([^\s/>!?]+)((?:\s+[^\s=/>]+\s*=\s*(?:"[^"]*"|\'[^\']*\'))*)\s*(/?)>', re.S)
ATTR = re.compile(r'([^\s=/>]+)\s*=\s*(?:"([^"]*)"|\'([^\']*)\')')


class Element:
    """An element of the .vl text with the offsets of its tag and attribute values (the text is never re-serialized)."""

    def __init__(self, tag, start, parent):
        self.tag, self.start, self.end, self.parent = tag, start, None, parent
        self.attrs, self.children = {}, []

    def attr(self, name):
        a = self.attrs.get(name)
        return None if a is None else html.unescape(a[0])

    def kids(self, tag):
        return [c for c in self.children if c.tag == tag]

    def iter(self):
        yield self
        for c in self.children:
            yield from c.iter()

    def ident_id(self):
        el = self
        while el is not None and el.attr('Id') is None:
            el = el.parent
        return el.attr('Id') if el is not None else '-'


def parse(text):
    root = Element('#document', 0, None)
    stack = [root]
    for m in TOKEN.finditer(text):
        if m.group(2) is None:
            continue
        if m.group(1):
            el = stack.pop()
            if el.tag != m.group(2) or el is root:
                raise MigrateError(f'unbalanced tag </{m.group(2)}> at offset {m.start()}')
            el.end = m.end()
            continue
        el = Element(m.group(2), m.start(), stack[-1])
        stack[-1].children.append(el)
        for a in ATTR.finditer(m.group(3)):
            g = 2 if a.group(2) is not None else 3
            el.attrs[a.group(1)] = (a.group(g), m.start(3) + a.start(g), m.start(3) + a.end(g))
        if m.group(4):
            el.end = m.end()
        else:
            stack.append(el)
    if len(stack) != 1:
        raise MigrateError(f'unclosed tag <{stack[-1].tag}>')
    root.end = len(text)
    return root


def attr_edit(el, name, value):
    _, start, end = el.attrs[name]
    return start, end, esc(value)


def chain_edits(ref, old_cat, new_cat, text, nl):
    """Edits of the Kind="Category" CategoryReference chain and of FullNameCategoryReference for a category move."""
    edits = []
    for f in ref.kids('FullNameCategoryReference'):
        if f.attr('ID') == old_cat:
            edits.append(attr_edit(f, 'ID', new_cat))
    heads = [c for c in ref.kids('CategoryReference') if c.attr('Kind') == 'Category']
    if not heads:
        return edits
    if len(heads) > 1:
        raise MigrateError('more than one category reference')
    chain, el = [], heads[0]
    while el is not None:
        chain.append(el)
        outer = el.kids('p:OuterCategoryReference')
        if len(outer) > 1:
            raise MigrateError('a category reference with two outer references')
        el = outer[0] if outer else None
    names = [c.attr('Name') for c in chain]
    k = len([x for x in names if x is not None])
    old_segs, new_segs = old_cat.split('.'), new_cat.split('.')
    if None in names[:k] or k > len(old_segs) or names[:k] != old_segs[::-1][:k]:
        raise MigrateError(f'category reference {".".join(str(x) for x in reversed(names))} does not fit {old_cat}')
    k2 = len(new_segs) if k == len(old_segs) else min(k, len(new_segs))
    want = new_segs[::-1][:k2]
    if k2 == k:
        edits += [attr_edit(c, 'Name', w) for c, w in zip(chain, want) if c.attr('Name') != w]
        return edits
    # The depth changes: write the chain again in the form vvvv saves it.
    head = heads[0]
    line_start = text.rfind('\n', 0, head.start) + 1
    indent = text[line_start:head.start]
    unit = '  '
    if len(chain) > 1:
        inner_start = text.rfind('\n', 0, chain[1].start) + 1
        unit = text[inner_start:chain[1].start][len(indent):] or unit
    direct = head.attr('NeedsToBeDirectParent') == 'true'
    items = want + [None] * (len(chain) - k)

    def build(i, ind):
        tag = 'CategoryReference' if i == 0 else 'p:OuterCategoryReference'
        attrs = ' Kind="Category"' + (f' Name="{esc(items[i])}"' if items[i] is not None else '') \
            + (' NeedsToBeDirectParent="true"' if direct else '')
        if i == len(items) - 1:
            return f'{ind}<{tag}{attrs} />'
        return f'{ind}<{tag}{attrs}>{nl}{build(i + 1, ind + unit)}{nl}{ind}</{tag}>'

    edits.append((head.start, head.end, build(0, indent)[len(indent):]))
    return edits


def reference(el):
    """(kind, symbol Choice) of a VL.Qt reference, or None."""
    choices = [c for c in el.kids('Choice') if c.attr('Kind') not in (None, 'NodeFlag')]
    if not choices or choices[-1].attr('Name') is None:
        return None
    symbol = choices[-1]
    return ('type' if symbol.attr('Kind') == 'TypeFlag' else 'node'), symbol


def known_pins(api, cat, name, pins):
    """True if the snapshot knows the node and every pin of the patch is one of its pins (a pin of a group, Input 3,
    counts as its group)."""
    known = api[3].get((cat, name)) or api[0].get(name)
    return bool(known) and all(p in known or re.sub(r' \d+$', '', p) in known for p in pins)


def migrate(raw, steps, api=None):
    """(new bytes, change lines, error lines) of one patch. `api` is load_api of the snapshot (or None): the Kind of a
    reference follows it (a node that is a process node there is ProcessAppFlag, not OperationCallFlag), and it breaks a
    tie between versions by the pins of the node (a setter Frame has an Output pin, the getter Frame that took its name has
    none)."""
    bom = raw.startswith(BOM)
    text = raw[len(BOM):].decode('utf-8') if bom else raw.decode('utf-8')
    if not steps:
        return raw, [], []
    ET.fromstring(text)
    doc = parse(text)
    nl = '\r\n' if '\r\n' in text else '\n'
    edits, changes, errors = [], [], []
    for el in doc.iter():
        if not QT_DEPENDENCY.match(el.attr('LastDependency') or '') or el.attr('LastCategoryFullName') is None:
            continue
        ref = reference(el)
        if ref is None:
            continue
        kind, symbol = ref
        ident = (kind, el.attr('LastCategoryFullName'), symbol.attr('Name'))
        pin_els = el.parent.kids('Pin') if kind == 'node' and el.tag == 'p:NodeReference' and el.parent.tag == 'Node' else []
        pin_els += el.kids('PinReference')
        pins = [p.attr('Name') or '' for p in pin_els]
        label = f'{ident[2]} [{el.ident_id()}]' if kind == 'node' else f'type {ident[2]} [{el.ident_id()}]'
        results = evaluate(steps, ident, pins)
        outcomes = set(results.values())
        if len(outcomes) > 1 and api is not None:
            fitting = {r for r in outcomes if known_pins(api, r[0][1], r[0][2], r[1])}
            # A node that is valid as it stands (a getter Frame) is not migrated again.
            unchanged = {r for r in fitting if r == ((kind, el.attr('LastCategoryFullName'), symbol.attr('Name')), tuple(pins))}
            if unchanged or fitting:
                outcomes = unchanged or fitting
        if not outcomes:
            errors.append(f'{label}: no step of the mapping fits its names ({ident[1]}; pins mix old and new names)')
            continue
        if len(outcomes) > 1:
            versions = ', '.join(steps[v - 1].id if v else 'none' for v in sorted(results))
            errors.append(f'{label}: cannot tell which steps it has seen (fits after: {versions}); '
                          'its names do not tell the versions apart')
            continue
        (new_kind, new_cat, new_name), new_pins = outcomes.pop()
        local = []
        try:
            if new_cat != ident[1]:
                local.append(attr_edit(el, 'LastCategoryFullName', new_cat))
                local += chain_edits(el, ident[1], new_cat, text, nl)
                changes.append(f'{label}: category {ident[1]} -> {new_cat}')
            if new_name != ident[2]:
                local.append(attr_edit(symbol, 'Name', new_name))
                changes.append(f'{label}: node {ident[2]} -> {new_name}')
            if api is not None and kind == 'node' and symbol.attr('Kind') == 'OperationCallFlag'                     and (new_cat, new_name) in api[2]:
                local.append(attr_edit(symbol, 'Kind', 'ProcessAppFlag'))
                changes.append(f'{label}: operation -> process node')
            before = [(p.attr('Kind'), p.attr('Name')) for p in pin_els if p.tag == 'Pin']
            after = [(p.attr('Kind'), new) for p, new in zip(pin_els, new_pins) if p.tag == 'Pin']
            if len(set(after)) < len(set(before)):
                raise MigrateError('two pins get the same name')
            for p, old, new in zip(pin_els, pins, new_pins):
                if new != old:
                    local.append(attr_edit(p, 'Name', new))
                    changes.append(f'{label}: pin {old} -> {new}' + (' (PinReference)' if p.tag == 'PinReference' else ''))
        except MigrateError as e:
            errors.append(f'{label}: {e}')
            continue
        edits += local
    if errors or not edits:
        return raw, changes, errors
    edits.sort()
    for (s1, e1, _), (s2, _, _) in zip(edits, edits[1:]):
        if s2 < e1:
            raise MigrateError(f'overlapping edits at offset {s2}')
    for start, end, value in reversed(edits):
        text = text[:start] + value + text[end:]
    ET.fromstring(text)
    return (BOM if bom else b'') + text.encode('utf-8'), changes, errors


# ---- main ----------------------------------------------------------------------------------------------------------

def rel_or_abs(path, start):
    try:
        return os.path.relpath(os.path.abspath(path), start)
    except ValueError:
        return os.path.abspath(path)


def display(path):
    rel = rel_or_abs(path, ROOT)
    return (path if rel.startswith('..') else rel).replace(os.sep, '/')


def expand(args):
    files = []
    for a in args or [os.path.join(ROOT, p) for p in DEFAULT_PATCHES]:
        matches = sorted(glob.glob(a, recursive=True)) if any(c in a for c in '*?[') else [a]
        for m in matches:
            parts = set(os.path.normpath(rel_or_abs(m, ROOT)).split(os.sep))
            if not args and parts & SKIP_DIRS:
                continue
            if os.path.abspath(m) not in [os.path.abspath(f) for f in files]:
                files.append(m)
    return files


def main(argv):
    opts = {a.split('=', 1)[0]: (a.split('=', 1)[1] if '=' in a else True) for a in argv if a.startswith('--')}
    unknown = set(opts) - {'--check', '--mapping', '--api', '--no-api-check', '--quiet'}
    if unknown:
        print(f'unknown option(s): {", ".join(sorted(unknown))}', file=sys.stderr)
        return 2
    mapping = opts.get('--mapping') if isinstance(opts.get('--mapping'), str) else DEFAULT_MAPPING
    api = opts.get('--api') if isinstance(opts.get('--api'), str) else DEFAULT_API
    if not os.path.isfile(mapping):
        print(f'{display(mapping)}: mapping file not found', file=sys.stderr)
        return 2
    steps, errors = load_mapping(mapping)
    if not errors and steps and not opts.get('--no-api-check'):
        errors = check_api(steps, api, mapping)
    if errors:
        for e in errors:
            print(f'error {e}')
        print(f'{len(errors)} error(s) in the mapping, nothing written')
        return 2

    files = expand([a for a in argv if not a.startswith('--')])
    snapshot = load_api(api) if not opts.get('--no-api-check') and os.path.isfile(api) else None
    pending, problems = {}, 0
    for f in files:
        if not os.path.isfile(f):
            print(f'{display(f)}: error not found')
            problems += 1
            continue
        raw = open(f, 'rb').read()
        try:
            new, changes, errs = migrate(raw, steps, snapshot)
        except (MigrateError, ET.ParseError, UnicodeDecodeError) as e:
            changes, errs, new = [], [f'cannot read: {e}'], raw
        for c in changes:
            print(f'{display(f)}: {c}')
        for e in errs:
            print(f'{display(f)}: error {e}')
        problems += len(errs)
        if new != raw:
            pending[f] = new
    if problems:
        print(f'{len(files)} patch(es): {problems} error(s), nothing written')
        return 2
    if opts.get('--check'):
        if not opts.get('--quiet'):
            print(f'{len(files)} patch(es), {len(steps)} step(s): {len(pending)} would change')
        return 1 if pending else 0
    for f, new in pending.items():
        with open(f, 'wb') as out:
            out.write(new)
    if not opts.get('--quiet'):
        print(f'{len(files)} patch(es), {len(steps)} step(s): {len(pending)} changed')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
