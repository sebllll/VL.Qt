"""Regression test for tools/check-source-rules.py (closing check K1, K3, K4).

Usage: python tools/test-source-rules.py   (exit code 0 = ok)

Writes small fixture trees (src/...) to a temporary folder and runs the checker on them:
  - K1: `#455`, `PR #12` in a comment and in a string are findings; a numbered item ("command #1"), a six-digit colour,
    an HTML entity and a markdown heading are not;
  - K4: a comment line over 160 characters (// and /// and inside /* */ and trailing behind code) is a finding, a long code
    line and a long string with `//` inside are not, 160 characters exactly is allowed;
  - K3: two top-level types in one C# file are a finding (class, struct, enum, interface, record, delegate, generic variant,
    in a block or file-scoped namespace); nested types, a partial type twice, `where T : class` and types named in
    comments and strings are not; the exceptions list is honoured;
  - the exit code: 1 with findings, 0 without, 0 with --warn; the real repository passes (when the checked-in tree is clean).
"""
import importlib.util
import os
import subprocess
import sys
import tempfile

TOOLS = os.path.dirname(os.path.abspath(__file__))
CHECK = os.path.join(TOOLS, 'check-source-rules.py')
spec = importlib.util.spec_from_file_location('check_source_rules', CHECK)
csr = importlib.util.module_from_spec(spec)
spec.loader.exec_module(csr)

FAILED = []


def expect(cond, msg):
    if not cond:
        FAILED.append(msg)
    print(('ok   ' if cond else 'FAIL ') + msg)


def write(root, rel, text):
    path = os.path.join(root, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(text)


def findings(root, rule=None):
    return [f for f in csr.check(root) if rule is None or f.rule == rule]


def run(root, *args):
    return subprocess.run([sys.executable, '-I', CHECK, '--root', root, *args], capture_output=True, text=True).returncode


with tempfile.TemporaryDirectory() as tmp:
    # ---- K1
    write(tmp, 'src/A/Bad.cs', '''namespace X
{
    internal sealed class Bad
    {
        // fixed in #455
        // see PR #12 for details
        const string s = "reported in #216";
    }
}
''')
    write(tmp, 'src/A/Good.cs', '''namespace X
{
    internal sealed class Good
    {
        // apply stops at command #1 and "uniform #3: bad"
        const string c = "#123456"; // colour
        const string e = "&#39;";
    }
}
''')
    write(tmp, 'src/A/Notes.md', '# Heading\n\n## Section\n')
    k1 = findings(tmp, 'K1')
    expect(sorted((f.path, f.line) for f in k1) == [('src/A/Bad.cs', 5), ('src/A/Bad.cs', 6), ('src/A/Bad.cs', 7)],
           f'K1 finds the three references in Bad.cs and nothing else ({[(f.path, f.line) for f in k1]})')

    # ---- K4
    long170 = 'x' * 154
    write(tmp, 'src/B/Long.cs', f'''namespace X
{{
    internal sealed class Long
    {{
        // {long170}
        /// <summary>{long170}</summary>
        /* {long170}
           {long170} */
        int a = 1; // {long170}
        const string url = "http://{'y' * 170}";
        int bbbb = {'1 + ' * 60}1;
        // {'z' * 140}
    }}
}}
''')
    k4 = findings(tmp, 'K4')
    expect(sorted(f.line for f in k4 if f.path == 'src/B/Long.cs') == [5, 6, 7, 8, 9],
           f'K4 finds // /// /* */ and the trailing comment only ({sorted(f.line for f in k4)})')
    exact = '// ' + 'e' * 157
    assert len(exact) == 160
    write(tmp, 'src/B/Exact.cs', f'internal sealed class Exact\n{{\n{exact}\n}}\n')
    expect(not [f for f in findings(tmp, 'K4') if f.path == 'src/B/Exact.cs'], 'K4 allows exactly 160 characters')
    write(tmp, 'src/B/Cpp.cpp', f'int a; // {long170}\nconst char* s = R"x(\n// {long170}\n)x";\n')
    expect(sorted(f.line for f in findings(tmp, 'K4') if f.path == 'src/B/Cpp.cpp') == [1],
           'K4 in C++: trailing comment found, raw string content is no comment')

    # ---- K3
    write(tmp, 'src/C/Two.cs', 'namespace X\n{\n    public sealed class A { }\n\n    public enum B { One }\n}\n')
    write(tmp, 'src/C/FileScoped.cs', 'namespace X;\n\npublic struct A { }\ninternal interface IB { }\n')
    write(tmp, 'src/C/Mixed.cs', '''using System;
namespace X
{
    [Obsolete("x")]
    public sealed record R(int V);
    public delegate void D(int x);
    public sealed class G<T> where T : class { }
    public sealed class G { }
}
''')
    write(tmp, 'src/C/One.cs', '''namespace X
{
    // class Fake { } and struct Other
    public sealed partial class One
    {
        const string s = "class NotAType { }";
        public enum Nested { A }
        public sealed class Inner { }
        public void M<T>() where T : class, new() { }
        public delegate void Callback();
    }

    public sealed partial class One
    {
    }
}
''')
    write(tmp, 'src/C/Generic.cs', 'namespace X\n{\n    public sealed class Box<T> where T : struct\n    {\n        struct S { }\n    }\n}\n')
    k3 = sorted(f.path for f in findings(tmp, 'K3'))
    expect(k3 == ['src/C/FileScoped.cs', 'src/C/Mixed.cs', 'src/C/Two.cs'],
           f'K3 finds Two, FileScoped and Mixed (4 types), not One or Generic ({k3})')
    mixed = [f for f in findings(tmp, 'K3') if f.path == 'src/C/Mixed.cs'][0].text
    expect(mixed.startswith('4 top-level types (R, D, G<T>, G)'), f'K3 names record, delegate and the generic variant ({mixed})')
    saved = dict(csr.K3_EXCEPTIONS)
    csr.K3_EXCEPTIONS['src/C/Two.cs'] = 'test'
    expect('src/C/Two.cs' not in [f.path for f in findings(tmp, 'K3')], 'K3 exception list is honoured')
    csr.K3_EXCEPTIONS.clear()
    csr.K3_EXCEPTIONS.update(saved)

    # ---- exit codes
    expect(run(tmp) == 1, 'exit code 1 with findings')
    expect(run(tmp, '--warn') == 0, 'exit code 0 with --warn')
    with tempfile.TemporaryDirectory() as clean:
        write(clean, 'src/D/Fine.cs', 'namespace X\n{\n    internal sealed class Fine\n    {\n        // short\n    }\n}\n')
        expect(run(clean) == 0, 'exit code 0 without findings')

# the repository itself passes: the check is part of the gate
repo = os.path.dirname(TOOLS)
expect(run(repo) == 0, 'the repository passes the source rules')

print('FAILED: ' + ', '.join(FAILED) if FAILED else 'all ok')
sys.exit(1 if FAILED else 0)
