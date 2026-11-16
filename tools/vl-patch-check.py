"""Read-only checker for the VL.Qt nodes in vvvv patches (.vl).

Usage:
  python tools/vl-patch-check.py [<patch.vl|glob> ...]    check (default: help/Qt/*.vl, help/*.vl,
                                                          VL.Qt.Stride/help/*.vl and VL.Qt.Audio/help/*.vl)
  python tools/vl-patch-check.py --fix <patch.vl> ...      also make hidden pins with a value visible
  python tools/vl-patch-check.py --accept [--reason=TEXT] <patch.vl> ...   put the current findings into the baseline
Options: --baseline=<file> (default help/.padlint.json), --quiet (no summary line).

It never moves or deletes anything. The pins of the nodes come from the C# sources (tools/vl_pin_catalog.py).

Errors (exit code 1):
  hidden-non-default  a pin is hidden (IsHidden="true") but holds a value that differs from its default (a DefaultValue
                      on the pin or a link). --fix makes such a pin visible; that is the only change --fix makes.
                      Not for pins the node writes itself (Bounds of Renderer, Save Bounds).
  forbidden-iobox     an IOBox is linked to a pin that must not have one (Interaction: the element becomes opaque to input).
  missing-pin         a link points to a pin the node no longer has (renamed or removed in C#).
  stale-baseline      a baseline entry whose element no longer exists or that no longer matches a finding.
Hint (exit code stays 0):
  default-iobox       an input IOBox holds the pin's default value (sometimes intended in help patches: accept it).

Considered: only nodes found in the catalog; only real IOBoxes (isIOBox="true") linked to exactly one pin; never slot
pads (SlotId) or other pads. Pins missing from the catalog are reported only when they carry a link.

Baseline (help/.padlint.json): intended exceptions, one entry per finding: patch (relative to the baseline's folder),
element Id, rule, optional reason. Findings in the baseline are not reported. vvvv keeps element Ids when it saves, so
entries survive manual editing. --accept replaces the entries of the given patches with their current findings and
keeps the reasons of entries that are still found.
"""
import glob, json, os, re, sys
import xml.etree.ElementTree as ET

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import vl_pin_catalog as pc  # noqa: E402

P = '{property}'
DEFAULT_PATCHES = ('help/Qt/*.vl', 'help/*.vl', 'VL.Qt.Stride/help/*.vl', 'VL.Qt.Audio/help/*.vl')
DEFAULT_BASELINE = os.path.join(ROOT, 'help', '.padlint.json')
ERRORS = ('hidden-non-default', 'forbidden-iobox', 'missing-pin', 'stale-baseline')
KNOWN_EXTRA_PINS = {'Node Context'}  # written by vvvv for every process node, hidden, not part of the catalog


class Finding:
    def __init__(self, rule, node, pin, element_id, reason):
        self.rule, self.node, self.pin, self.id, self.reason = rule, node, pin, element_id, reason

    @property
    def level(self):
        return 'error' if self.rule in ERRORS else 'hint'


# ---- XML reading ---------------------------------------------------------------------------------------------------

def node_name(node, catalog):
    """Catalog name of a vvvv node, or None if it is not a VL.Qt process node."""
    ref = node.find(P + 'NodeReference')
    if ref is None or not (ref.get('LastCategoryFullName') or '').startswith('Qt'):
        return None
    names = [c.get('Name') for c in ref.findall('Choice') if c.get('Kind') == 'ProcessAppFlag']
    return names[-1] if names and names[-1] in catalog else None


def check_patch(raw, catalog):
    """Findings of one patch (bytes of the .vl file) -> (list of Finding, set of element Ids in the patch)."""
    root = ET.fromstring(raw.decode('utf-8-sig'))
    ids = {e.get('Id') for e in root.iter() if e.get('Id')}
    pins = {}  # pin Id -> (node name, pin element)
    for n in root.iter('Node'):
        name = node_name(n, catalog)
        if name:
            for pin in n.findall('Pin'):
                pins[pin.get('Id')] = (name, pin)
    iopads = {p.get('Id'): p for p in root.iter('Pad') if p.get('isIOBox') == 'true' and p.get('SlotId') is None}
    peers, sources = {}, {}  # Id -> linked Ids; pin Id -> Ids linked into it
    for link in root.iter('Link'):
        lids = link.get('Ids', '').split(',')
        if len(lids) < 2:
            continue
        a, b = lids[0], lids[-1]  # source, sink; the ones in between are control points of the link
        peers.setdefault(a, []).append(b)
        peers.setdefault(b, []).append(a)
        sources.setdefault(b, []).append(a)

    findings = []
    for pid, (name, pin) in pins.items():
        pin_name = pin.get('Name')
        spec = pc.find_pin(catalog[name], pin_name)
        linked = peers.get(pid, [])
        if spec is None:
            if linked and pin_name not in KNOWN_EXTRA_PINS:
                findings.append(Finding('missing-pin', name, pin_name, pid,
                                        f'linked, but {name} has no pin "{pin_name}" (renamed or removed in C#)'))
            continue
        if spec['direction'] != 'in' or pin.get('IsHidden') != 'true':
            continue
        if sources.get(pid):
            findings.append(Finding('hidden-non-default', name, pin_name, pid, 'hidden, but linked'))
        elif (pin.get('DefaultValue') is not None and not spec['written_by_node']
              and pc.is_default(pin.get('DefaultValue'), spec) is False):
            findings.append(Finding('hidden-non-default', name, pin_name, pid,
                                    f'hidden, but holds "{pin.get("DefaultValue")}" (default "{spec["default"]}")'))

    for pad_id, pad in iopads.items():
        linked = peers.get(pad_id, [])
        if len(linked) != 1 or linked[0] not in pins:
            continue
        pid = linked[0]
        if pad_id not in sources.get(pid, []):
            continue  # an output IOBox: the pin feeds the pad
        name, pin = pins[pid]
        spec = pc.find_pin(catalog[name], pin.get('Name'))
        if spec is None or spec['direction'] != 'in':
            continue
        if spec['forbid_iobox']:
            findings.append(Finding('forbidden-iobox', name, spec['name'], pad_id,
                                    'an IOBox here makes the element opaque to input; link an interaction node or nothing'))
        elif pc.is_default(pad.get('Value'), spec):
            value = pad.get('Value', spec['default'])
            findings.append(Finding('default-iobox', name, pin.get('Name'), pad_id,
                                    f'IOBox holds the default "{value}"'))
    return findings, ids


def fix_hidden(raw, pin_ids):
    """Makes the given pins visible: removes their IsHidden="true", nothing else."""
    for pid in pin_ids:
        raw, n = re.subn(rb'(<Pin\s[^>]*?Id="' + re.escape(pid.encode()) + rb'"[^>]*?)\s+IsHidden="true"', rb'\1', raw)
        if n != 1:
            raise RuntimeError(f'pin {pid}: IsHidden not found exactly once')
    return raw


# ---- baseline ------------------------------------------------------------------------------------------------------

def load_baseline(path):
    if not os.path.exists(path):
        return []
    with open(path, encoding='utf-8') as f:
        return json.load(f).get('entries', [])


def save_baseline(path, entries):
    entries = sorted(entries, key=lambda e: (e['patch'], e['rule'], e['id']))
    data = {
        'about': 'Intended exceptions for tools/vl-patch-check.py: patch (relative to this folder), element Id, rule, '
                 'reason. Update with: python tools/vl-patch-check.py --accept <patch.vl>',
        'entries': [{k: e[k] for k in ('patch', 'id', 'rule', 'reason') if k in e} for e in entries],
    }
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(data, f, indent=2, ensure_ascii=False)
        f.write('\n')


def rel_or_abs(path, start):
    """Path relative to start; the absolute path if there is none (other drive on Windows)."""
    try:
        return os.path.relpath(os.path.abspath(path), start)
    except ValueError:
        return os.path.abspath(path)


def patch_key(path, baseline_path):
    return rel_or_abs(path, os.path.dirname(os.path.abspath(baseline_path))).replace(os.sep, '/')


def display(path):
    rel = rel_or_abs(path, ROOT)
    return (path if rel.startswith('..') else rel).replace(os.sep, '/')


# ---- main ----------------------------------------------------------------------------------------------------------

def expand(args):
    files = []
    for a in args or [os.path.join(ROOT, p) for p in DEFAULT_PATCHES]:
        matches = sorted(glob.glob(a)) if any(c in a for c in '*?[') else [a]
        files += [m for m in matches if m not in files]
    return files


def main(argv):
    opts = {a.split('=', 1)[0]: (a.split('=', 1)[1] if '=' in a else True) for a in argv if a.startswith('--')}
    unknown = set(opts) - {'--fix', '--accept', '--reason', '--baseline', '--quiet'}
    if unknown:
        print(f'unknown option(s): {", ".join(sorted(unknown))}', file=sys.stderr)
        return 2
    files = expand([a for a in argv if not a.startswith('--')])
    if opts.get('--accept') and not [a for a in argv if not a.startswith('--')]:
        print('--accept needs the patch files to accept', file=sys.stderr)
        return 2
    baseline_path = opts.get('--baseline', DEFAULT_BASELINE)
    baseline = load_baseline(baseline_path)
    catalog = pc.load_catalog()

    errors = hints = fixed = 0
    keys = set()
    for f in files:
        if not os.path.isfile(f):
            print(f'{display(f)}: not found', file=sys.stderr)
            errors += 1
            continue
        raw = open(f, 'rb').read()
        findings, ids = check_patch(raw, catalog)
        key = patch_key(f, baseline_path)
        keys.add(key)
        if opts.get('--accept'):
            old = {(e['id'], e['rule']): e for e in baseline if e['patch'] == key}
            baseline = [e for e in baseline if e['patch'] != key]
            for x in findings:
                reason = old.get((x.id, x.rule), {}).get('reason') or (opts['--reason'] if isinstance(opts.get('--reason'), str) else '')
                baseline.append(dict(patch=key, id=x.id, rule=x.rule, reason=reason))
            print(f'{display(f)}: {len(findings)} finding(s) in the baseline')
            continue
        accepted = {(e['id'], e['rule']) for e in baseline if e['patch'] == key}
        open_findings = [x for x in findings if (x.id, x.rule) not in accepted]
        found = {(x.id, x.rule) for x in findings}
        for e in baseline:
            if e['patch'] != key or (e['id'], e['rule']) in found:
                continue
            why = 'element no longer exists' if e['id'] not in ids else 'no longer found'
            open_findings.append(Finding('stale-baseline', '-', '-', e['id'],
                                         f'baseline entry for {e["rule"]}: {why}; run --accept on this patch'))
        to_fix = [x for x in open_findings if x.rule == 'hidden-non-default'] if opts.get('--fix') else []
        if to_fix:
            open(f, 'wb').write(fix_hidden(raw, sorted({x.id for x in to_fix})))
        for x in open_findings:
            tag = 'fixed' if x in to_fix else x.level
            print(f'{display(f)}: {tag} {x.rule} {x.node}.{x.pin} [{x.id}]: {x.reason}')
            if x in to_fix:
                fixed += 1
            elif x.level == 'error':
                errors += 1
            else:
                hints += 1
    if opts.get('--accept'):
        save_baseline(baseline_path, baseline)
        return 0
    # entries of patches that no longer exist
    for e in baseline:
        if e['patch'] not in keys and not os.path.exists(os.path.join(os.path.dirname(os.path.abspath(baseline_path)), e['patch'])):
            print(f'{e["patch"]}: error stale-baseline -.- [{e["id"]}]: baseline entry for {e["rule"]}: patch no longer exists')
            errors += 1
    if not opts.get('--quiet'):
        print(f'{len(files)} patch(es): {errors} error(s), {hints} hint(s)' + (f', {fixed} fixed' if fixed else ''))
    return 1 if errors else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
