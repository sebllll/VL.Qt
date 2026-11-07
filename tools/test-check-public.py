"""Test for tools/check-public.py and .githooks/pre-push, using throwaway git repositories.

Usage: python tools/test-check-public.py
Exit code 0 = ok.
"""
import os
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCRIPT = os.path.join(ROOT, "tools", "check-public.py")
HOOK = os.path.join(ROOT, ".githooks", "pre-push")

PRIVATE_PATHS = "\n".join([
    "# test list",
    "plan/",  # check-public: allow
    "CLAUDE.md",  # check-public: allow
    "/root-only.txt",
    "*.secret",
    "!keep.secret",
    "build/",
    "",
])

failures = []


def run(cmd, cwd, stdin=None):
    return subprocess.run(cmd, cwd=cwd, input=stdin, capture_output=True, text=True, encoding="utf-8")


def git(repo, *args):
    result = subprocess.run(["git", "-c", "user.name=t", "-c", "user.email=t@example.com", *args],
                            cwd=repo, capture_output=True, text=True)
    assert result.returncode == 0, result.stderr
    return result.stdout.strip()


def new_repo(files):
    repo = tempfile.mkdtemp(prefix="check-public-")
    git(repo, "init", "-q")
    git(repo, "config", "core.autocrlf", "false")
    write(repo, "tools/private-paths.txt", PRIVATE_PATHS)
    for path, content in files.items():
        write(repo, path, content)
    git(repo, "add", "-A")
    return repo


def write(repo, path, content):
    full = os.path.join(repo, path)
    os.makedirs(os.path.dirname(full), exist_ok=True)
    with open(full, "wb") as f:
        f.write(content if isinstance(content, bytes) else content.encode("utf-8"))


def check(name, condition, detail=""):
    if not condition:
        failures.append(name + (": " + detail if detail else ""))


def checker(repo, *args):
    return run([sys.executable, "-I", SCRIPT, *args], repo)


def expect(name, repo, args, code, contains=(), absent=()):
    r = checker(repo, *args)
    detail = "exit %d, stdout=%r stderr=%r" % (r.returncode, r.stdout, r.stderr)
    check(name + " (exit code)", r.returncode == code, detail)
    for text in contains:
        check(name + " (finds %r)" % text, text in r.stdout, detail)
    for text in absent:
        check(name + " (no %r)" % text, text not in r.stdout, detail)
    return r


def test_clean():
    repo = new_repo({"README.md": "Hello.\n", "src/a.cs": "class A {}\n"})
    expect("clean tree", repo, [], 0)
    shutil.rmtree(repo, ignore_errors=True)


def test_paths():
    repo = new_repo({
        "plan/x.md": "a", "sub/plan/y.md": "b", "docs/plan/z.md": "c",  # check-public: allow
        "CLAUDE.md": "c", "sub/CLAUDE.md": "c",  # check-public: allow
        "root-only.txt": "r", "sub/root-only.txt": "r",
        "a.secret": "s", "keep.secret": "k", "deep/b.secret": "s",
        "build": "a file named like a directory pattern", "src/build/out.txt": "o",
        "src/ok.cs": "class A {}\n",
    })
    r = expect("paths", repo, ["--paths"], 1,
               contains=["plan/x.md:0:", "sub/plan/y.md:0:", "docs/plan/z.md:0:", "CLAUDE.md:0:", "sub/CLAUDE.md:0:",  # check-public: allow
                         "\nroot-only.txt:0:", "a.secret:0:", "deep/b.secret:0:", "src/build/out.txt:0:"],
               absent=["sub/root-only.txt", "keep.secret", "src/ok.cs", "\nbuild:0:"])
    check("paths: findings are separate lines", all(": " in line for line in r.stdout.splitlines()))
    expect("content only ignores private paths", repo, ["--content"], 0)
    expect("default mode includes paths", repo, [], 1, contains=["plan/x.md:0:"])  # check-public: allow
    shutil.rmtree(repo, ignore_errors=True)


def test_content():
    files = {
        "w.md": "see C:\\Users\\bob\\Documents\\x\n",  # check-public: allow
        "w2.md": "json \"C:\\\\Users\\\\alice\\\\x\" and d:/users/carol/y\n",  # check-public: allow
        "l.md": "at /home/dave/project\n",  # check-public: allow
        "m.md": "at /Users/erin/project\n",  # check-public: allow
        "u.md": "share \\\\fileserver\\projects\\x\n",
        "h.md": "open http://nas.local:8080/ and printer.lan\n",  # check-public: allow
        "ip1.md": "curl http://192.168.1.20/x\n",  # check-public: allow
        "ip2.md": "git clone ssh://git@10.1.2.3/repo\n",  # check-public: allow
        "ip3.md": "https://172.16.0.5:9000 and http://172.31.255.255/\n",  # check-public: allow
        "binary.bin": b"\x00\x01 C:\\Users\\bob\\x\n",  # check-public: allow
        "LICENSES/notes.txt": "C:\\Users\\bob\\x\n",  # check-public: allow
        # none of these is a finding
        "ok.md": "\n".join([
            "C:\\Users\\<name>\\x", "C:\\Users\\%USERNAME%\\x", "C:\\Users\\Public\\x", "$env:USERPROFILE\\x",
            "/home/runner/work", "/home/user/x", "file:///home/x", "https://github.com/home/foo",
            "docs/home/foo", "src/Users/Model.cs", "~/.local/share", "appsettings.local.json", "foo.local.json",
            "\\\\.\\pipe\\name", "..\\\\bin\\\\Debug", "regex \\\\d+\\\\s",
            "http://localhost:5000", "http://127.0.0.1/", "http://192.169.1.1/", "http://172.32.0.1/",
            "https://example.com/", "version 10.1.2.3 of the tool", "172.16.0.5 without a URL",
        ]) + "\n",
    }
    repo = new_repo(files)
    r = expect("content", repo, ["--content"], 1,
               contains=["w.md:1:", "w2.md:1:", "l.md:1:", "m.md:1:", "u.md:1:", "h.md:1:", "ip1.md:1:", "ip2.md:1:", "ip3.md:1:"],
               absent=["binary.bin", "LICENSES/", "ok.md"])
    check("content: one finding per kind and line",
          sum(1 for line in r.stdout.splitlines() if line.startswith("h.md:1:")) == 1, r.stdout)
    check("content: matched text is not echoed", "bob" not in r.stdout and "fileserver" not in r.stdout, r.stdout)
    expect("paths only ignores content", repo, ["--paths"], 0)
    shutil.rmtree(repo, ignore_errors=True)


def test_private_patterns():
    repo = new_repo({
        "a.md": "Maintainer Foo Bar wrote this.\n",
        "CLA.md": "Maintainer Foo Bar signs here.\n",
        "docs/b.md": "foo bar lowercase\n",
        "Foo Bar notes.txt": "nothing in it\n",
        "ok.md": "fine\n",
    })
    patterns = os.path.join(repo, "patterns.txt")
    write(repo, "patterns.txt", "# comment\n\nFoo Bar\tCLA.md\n(?i)lowercase\tdocs/**\n")
    expect("private patterns", repo, ["--content", "--private-patterns", patterns], 1,
           contains=["a.md:1:", "Foo Bar notes.txt:0:"], absent=["CLA.md", "docs/b.md", "ok.md"])
    # Default location, read from the repository root, and skipped silently when missing.
    expect("default pattern file missing", repo, ["--content"], 0)
    write(repo, "docs-internal/public-check-patterns.txt", "Foo Bar\tCLA.md,a.md\n")  # check-public: allow
    expect("default pattern file", repo, ["--content"], 1, contains=["Foo Bar notes.txt:0:"], absent=["a.md", "CLA.md:"])
    # Missing explicit file and bad regex are usage errors.
    r = checker(repo, "--private-patterns", os.path.join(repo, "nope.txt"))
    check("missing explicit pattern file", r.returncode == 2, r.stderr)
    write(repo, "bad.txt", "(unclosed\n")
    r = checker(repo, "--private-patterns", os.path.join(repo, "bad.txt"))
    check("invalid regex", r.returncode == 2 and "bad.txt:1" in r.stderr, r.stderr)
    shutil.rmtree(repo, ignore_errors=True)


def test_pragma():
    # Line 1 and 3 carry the pragma, line 2 and 4 do not (generic and private pattern).
    lines = [
        "see C:/Users/bob/x  <!-- check-public: allow -->",
        "see C:/Users/bob/y",  # check-public: allow
        "Foo Bar  # check-public: allow",
        "Foo Bar",
    ]
    repo = new_repo({"a.md": "\n".join(lines) + "\n", "patterns.txt": "Foo Bar\n"})
    patterns = os.path.join(repo, "patterns.txt")
    expect("pragma", repo, ["--content", "--private-patterns", patterns], 1, contains=["a.md:2:", "a.md:4:"],
           absent=["a.md:1:", "a.md:3:"])
    shutil.rmtree(repo, ignore_errors=True)


def test_rev_and_index():
    repo = new_repo({"a.md": "C:\\Users\\bob\\x\n", "b.md": "fine\n"})  # check-public: allow
    git(repo, "commit", "-q", "-m", "one")
    first = git(repo, "rev-parse", "HEAD")
    write(repo, "a.md", "fixed\n")
    write(repo, "plan/p.md", "private\n")  # check-public: allow
    git(repo, "add", "-A")
    # The index is clean of content findings but has a private path; the old commit has the content finding only.
    expect("index", repo, [], 1, contains=["plan/p.md:0:"], absent=["a.md"])  # check-public: allow
    expect("--rev old commit", repo, ["--rev", first], 1, contains=["a.md:1:"], absent=["plan/"])  # check-public: allow
    git(repo, "commit", "-q", "-m", "two")
    # Worktree edits are not seen: only the commit counts.
    write(repo, "b.md", "C:\\Users\\bob\\y\n")  # check-public: allow
    expect("--rev ignores the worktree", repo, ["--rev", "HEAD", "--content"], 0)
    git(repo, "add", "b.md")
    expect("index sees staged content", repo, ["--content"], 1, contains=["b.md:1:"])
    check("bad rev", checker(repo, "--rev", "no-such-rev").returncode == 2)
    check("bad option", checker(repo, "--nope").returncode == 2)
    outside = tempfile.mkdtemp(prefix="check-public-nogit-")
    check("outside a repository", checker(outside).returncode == 2)
    shutil.rmtree(outside, ignore_errors=True)
    shutil.rmtree(repo, ignore_errors=True)


def test_hook():
    sh = shutil.which("sh")
    if sh is None:
        print("skip: no sh found, pre-push hook not tested")
        return
    repo = new_repo({"a.md": "C:\\Users\\bob\\x\n"})  # check-public: allow
    write(repo, "tools/check-public.py", open(SCRIPT, "rb").read())
    git(repo, "add", "-A")
    git(repo, "commit", "-q", "-m", "bad")
    bad = git(repo, "rev-parse", "HEAD")
    write(repo, "a.md", "fine\n")
    git(repo, "commit", "-q", "-am", "good")
    good = git(repo, "rev-parse", "HEAD")
    zero = "0" * 40

    def push(remote, *shas):
        lines = "".join("refs/heads/main %s refs/heads/main %s\n" % (sha, zero) for sha in shas)
        return run([sh, HOOK, remote, "https://example.com/repo.git"], repo, stdin=lines)

    check("hook: other remote is not checked", push("origin", bad).returncode == 0)
    check("hook: clean ref to upstream passes", push("upstream", good).returncode == 0)
    r = push("upstream", bad)
    check("hook: finding blocks upstream", r.returncode != 0 and "a.md:1:" in r.stdout, r.stdout + r.stderr)
    check("hook: one bad ref blocks all", push("upstream", good, bad).returncode != 0)
    check("hook: deleting a ref passes", push("upstream", zero).returncode == 0)
    shutil.rmtree(repo, ignore_errors=True)


def main():
    for test in (test_clean, test_paths, test_content, test_private_patterns, test_pragma, test_rev_and_index, test_hook):
        test()
    if failures:
        print("FAILED (%d):" % len(failures))
        for f in failures:
            print("  - " + f)
        return 1
    print("ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
