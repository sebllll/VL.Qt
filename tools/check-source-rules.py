#!/usr/bin/env python3
"""Closing check of the refactoring (P14): three source rules for `src`, run by the gate.

Usage: python tools/check-source-rules.py [--root <repo>] [--warn] [--list]   (exit code 1 = findings, 0 = ok)

Rules (names as in docs-internal/plans/refactor/review/00_brief.md):
  K1  no issue or pull request reference (`#455`, `PR #12`) in `src`: comments and messages explain the code, the history is in git.
  K3  no C# file with two top-level types (class, struct, interface, enum, record, delegate), besides the named exceptions below.
  K4  no comment line over 160 characters (the whole physical line counts, so does a trailing comment behind code).

Read-only; it never changes a file. `--warn` prints the findings and exits with 0 (introduction of the check, local use).
`--list` prints the exceptions with their reasons. Regression test: tools/test-source-rules.py.
"""
import argparse
import os
import re
import subprocess
import sys

MAX_COMMENT_LINE = 160

# Text files of `src` that the rules read (binary files and generated data are skipped).
TEXT_EXTENSIONS = {'.cs', '.cpp', '.h', '.qml', '.frag', '.md', '.txt', '.xml', '.csproj', '.targets'}
# Languages with C-style comments, where K3 (C# only) and K4 apply.
C_STYLE = {'.cs', '.cpp', '.h', '.qml', '.frag'}
SKIP_DIRS = {'bin', 'obj', 'build', '.vs', 'node_modules', '.git'}

# K1: `#` and 1 to 5 digits that are not part of a word, an entity (&#39;) or a colour (#123456 has six digits).
ISSUE_REF = re.compile(r'(?<![\w&#])#\d{1,5}\b')

# K1 exceptions: a numbered item in a message or a comment is no issue reference. (pattern on the text before the match, reason)
K1_EXCEPTIONS = [
    (re.compile(r'\b(?:command|uniform|entry|link|property|pump|frame|item|index|step|round|stage|param|argument|arg)\s+$', re.I),
     'numbered item of a message or a comment ("command #1", "uniform #3"), also asserted by tests'),
]

# K3 exceptions: file (relative to the repository root, forward slashes) -> reason.
K3_EXCEPTIONS = {}

# K4 exceptions: file -> reason. A whole file only, never a single line: shorten the line instead.
K4_EXCEPTIONS = {}


class Finding:
    def __init__(self, rule, path, line, text):
        self.rule, self.path, self.line, self.text = rule, path, line, text

    def __str__(self):
        return f'{self.path}:{self.line}: {self.rule} {self.text}'


def source_files(root):
    """Text files below <root>/src: the tracked and untracked-but-not-ignored ones of git, else a folder walk."""
    result = []
    if os.path.isdir(os.path.join(root, '.git')) or os.path.isfile(os.path.join(root, '.git')):
        try:
            out = subprocess.run(['git', 'ls-files', '-co', '--exclude-standard', '-z', '--', 'src'], cwd=root,
                                 capture_output=True, check=True).stdout.decode('utf-8', 'replace')
            result = [p for p in out.split('\0') if p and os.path.isfile(os.path.join(root, p))]
        except (OSError, subprocess.CalledProcessError):
            result = []
    if not result:
        for folder, dirs, files in os.walk(os.path.join(root, 'src')):
            dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
            for f in files:
                result.append(os.path.relpath(os.path.join(folder, f), root).replace(os.sep, '/'))
    return sorted(p for p in result if os.path.splitext(p)[1].lower() in TEXT_EXTENSIONS)


def read_text(path):
    with open(path, 'rb') as f:
        return f.read().decode('utf-8-sig', 'replace')


def scan(text, ext):
    """Walks C-style source once. Returns (comment_lines, code): the 1-based numbers of lines that hold comment text,
    and the text with comments, strings and char literals blanked (newlines kept) for the type scan of K3."""
    n = len(text)
    out = []
    comment_lines = set()
    line = 1
    i = 0
    cs = ext == '.cs'
    cpp = ext in ('.cpp', '.h')
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ''
        if c == '\n':
            line += 1
            out.append('\n')
            i += 1
        elif c == '/' and nxt == '/':
            comment_lines.add(line)
            while i < n and text[i] != '\n':
                out.append(' ')
                i += 1
        elif c == '/' and nxt == '*':
            comment_lines.add(line)
            out.append('  ')
            i += 2
            while i < n and not (text[i] == '*' and i + 1 < n and text[i + 1] == '/'):
                if text[i] == '\n':
                    line += 1
                    out.append('\n')
                else:
                    out.append(' ')
                comment_lines.add(line)
                i += 1
            out.append('  ')
            i += 2
        elif cs and text.startswith('"""', i) or (cs and c in '$@' and re.match(r'[$@]*"""', text[i:i + 8] or '')):
            # C# raw string literal: ends at the same number of quotes
            m = re.match(r'[$@]*(")+', text[i:])
            quotes = m.group(0).count('"')
            i += m.end()
            out.append(' ')
            close = '"' * quotes
            while i < n and not text.startswith(close, i):
                if text[i] == '\n':
                    line += 1
                    out.append('\n')
                i += 1
            i += quotes
        elif cs and (text.startswith('@"', i) or text.startswith('$@"', i) or text.startswith('@$"', i)):
            i = text.index('"', i) + 1
            out.append(' ')
            while i < n:
                if text[i] == '"':
                    if text.startswith('""', i):
                        i += 2
                        continue
                    i += 1
                    break
                if text[i] == '\n':
                    line += 1
                    out.append('\n')
                i += 1
        elif cpp and c == 'R' and nxt == '"' and (i == 0 or not (text[i - 1].isalnum() or text[i - 1] == '_')):
            m = re.match(r'R"([^(\s]{0,16})\(', text[i:])
            if m:
                close = ')' + m.group(1) + '"'
                i += m.end()
                out.append(' ')
                while i < n and not text.startswith(close, i):
                    if text[i] == '\n':
                        line += 1
                        out.append('\n')
                    i += 1
                i += len(close)
            else:
                out.append(c)
                i += 1
        elif c == '"':
            out.append(' ')
            i += 1
            while i < n and text[i] != '"' and text[i] != '\n':
                if text[i] == '\\':
                    i += 1
                i += 1
            i += 1
        elif c == "'":
            m = re.match(r"'(?:\\.[^']{0,8}|[^'\\\n])'", text[i:i + 12])
            if m:
                out.append(' ')
                i += m.end()
            else:
                out.append(c)
                i += 1
        else:
            out.append(c)
            i += 1
    return comment_lines, ''.join(out)


MODIFIERS = {'public', 'internal', 'private', 'protected', 'static', 'sealed', 'abstract', 'partial', 'unsafe', 'readonly', 'ref',
             'file', 'new', 'extern', 'record'}
TYPE_DECL = re.compile(r'\b(class|struct|interface|enum|record\s+class|record\s+struct|record|delegate)\b')


def top_level_types(code):
    """Names of the C# types declared outside any type: directly in the file or in (file-scoped or block) namespaces."""
    types = []
    stack = []  # one entry per open brace: True for a namespace block
    start = 0  # start of the current statement (after ; { } or ])
    i = 0
    n = len(code)
    while i < n:
        c = code[i]
        if c in ';}]':
            if c == '}' and stack:
                stack.pop()
            start = i + 1
            i += 1
        elif c == '{':
            head = code[start:i]
            stack.append(bool(re.search(r'\bnamespace\b', head)))
            start = i + 1
            i += 1
        elif c == '[':
            # attribute or indexer/array: skip to the matching bracket, the statement start moves behind it
            attr_start = i
            depth = 1
            i += 1
            while i < n and depth:
                if code[i] == '[':
                    depth += 1
                elif code[i] == ']':
                    depth -= 1
                i += 1
            if not code[start:attr_start].strip():
                start = i
        else:
            m = TYPE_DECL.match(code, i) if c.isalpha() and (i == 0 or not (code[i - 1].isalnum() or code[i - 1] == '_')) else None
            if m:
                prefix = code[start:i].split()
                kind = re.sub(r'\s+', ' ', m.group(1))
                if all(p in MODIFIERS for p in prefix) and all(stack):
                    rest = code[m.end():]
                    if kind == 'delegate':
                        d = re.match(r'\s+[^;(]*?\b(\w+)\s*(?:<[^>(]*>)?\s*\(', rest)
                        name = d.group(1) if d else None
                    else:
                        d = re.match(r'\s+(\w+)(\s*<[^>{(]*>)?', rest)
                        name = (d.group(1) + (re.sub(r'\s', '', d.group(2)) if d.group(2) else '')) if d else None
                        if kind == 'record' and name in ('class', 'struct'):
                            name = None
                    if name and name not in types:
                        types.append(name)
                i = m.end()
            else:
                i += 1
    return types


def check_file(root, rel):
    findings = []
    ext = os.path.splitext(rel)[1].lower()
    try:
        text = read_text(os.path.join(root, rel))
    except OSError:
        return findings
    lines = text.split('\n')

    # K1: everywhere in the text, comments and messages alike.
    for no, raw in enumerate(lines, 1):
        for m in ISSUE_REF.finditer(raw):
            if any(p.search(raw[:m.start()]) for p, _ in K1_EXCEPTIONS):
                continue
            findings.append(Finding('K1', rel, no, f'issue reference "{m.group(0)}": say in words what the comment explains'))
            break

    if ext in C_STYLE:
        comment_lines, code = scan(text, ext)
        # K4
        if rel not in K4_EXCEPTIONS:
            for no in sorted(comment_lines):
                length = len(lines[no - 1].rstrip('\r'))
                if length > MAX_COMMENT_LINE:
                    findings.append(Finding('K4', rel, no, f'comment line of {length} characters (max {MAX_COMMENT_LINE})'))
        # K3
        if ext == '.cs' and rel not in K3_EXCEPTIONS:
            types = top_level_types(code)
            if len(types) > 1:
                findings.append(Finding('K3', rel, 1, f'{len(types)} top-level types ({", ".join(types)}): one file per type'))
    return findings


def check(root):
    findings = []
    for rel in source_files(root):
        findings.extend(check_file(root, rel))
    return findings


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--root', default=os.path.dirname(os.path.dirname(os.path.abspath(__file__))), help='repository root')
    ap.add_argument('--warn', action='store_true', help='print the findings, exit code 0')
    ap.add_argument('--list', action='store_true', help='print the exceptions with their reasons and exit')
    args = ap.parse_args(argv)

    if args.list:
        for _, reason in K1_EXCEPTIONS:
            print(f'K1 pattern: {reason}')
        for path, reason in sorted(K3_EXCEPTIONS.items()):
            print(f'K3 {path}: {reason}')
        for path, reason in sorted(K4_EXCEPTIONS.items()):
            print(f'K4 {path}: {reason}')
        return 0

    findings = check(args.root)
    for f in findings:
        print(f)
    counts = {r: sum(1 for f in findings if f.rule == r) for r in ('K1', 'K3', 'K4')}
    print(f'check-source-rules: K1 {counts["K1"]}, K3 {counts["K3"]}, K4 {counts["K4"]}'
          + (' (warn only)' if args.warn and findings else ''))
    return 0 if args.warn or not findings else 1


if __name__ == '__main__':
    sys.exit(main())
