"""Guard for the public repository: keeps internal files and internal content out.

Usage:
  python tools/check-public.py [--rev <commit>] [--paths] [--content] [--private-patterns <file>]

  --rev       check the tree of this commit (read through git, nothing is checked out);
              default: the files tracked in the index (staged content)
  --paths     fail on files matching a pattern of tools/private-paths.txt (gitignore-style)
  --content   fail on internal content in text files: generic patterns built into this script
              (local user paths, machine names, private hosts) plus the optional private pattern file
  --private-patterns  regex per line, optional TAB + comma-separated globs of paths where the pattern is
              allowed ('#' starts a comment); default: DEFAULT_PATTERNS_FILE below, skipped
              silently if it is missing. The regex is applied to every line of every text file and to the path.
  No mode flag = --paths and --content.
  Pragma: a line containing the text "check-public: allow" is skipped by all content patterns (generic and private).

Files that match tools/private-paths.txt are not scanned for content (they never go to the public repo
and are reported by --paths anyway). Binary files and LICENSES/ are skipped.
Exit code: 0 = clean, 1 = findings (one line each: path:line: reason), 2 = usage error.
Python 3 standard library only; runs on Windows, Linux and in GitHub Actions.
"""
import argparse
import re
import subprocess
import sys
import threading

PRIVATE_PATHS_FILE = "tools/private-paths.txt"
DEFAULT_PATTERNS_FILE = "docs-internal/public-check-patterns.txt"  # check-public: allow
# Text files above this size are not scanned (reported on stderr).
MAX_SCAN_BYTES = 5 * 1024 * 1024
SKIP_CONTENT_PREFIXES = ("LICENSES/",)
# A line containing this text is skipped by all content patterns (for fixtures and unavoidable mentions).
PRAGMA_ALLOW = "check-public: allow"


class UsageError(Exception):
    pass


# ---------------------------------------------------------------- git access

def git(root, *args, input_bytes=None):
    result = subprocess.run(["git", *args], cwd=root, input=input_bytes, capture_output=True)
    if result.returncode != 0:
        raise UsageError("git %s failed: %s" % (" ".join(args), result.stderr.decode("utf-8", "replace").strip()))
    return result.stdout


def repo_root():
    try:
        out = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True)
    except OSError:
        raise UsageError("git not found")
    if out.returncode != 0:
        raise UsageError("not inside a git repository")
    return out.stdout.decode("utf-8", "replace").strip()


def list_entries(root, rev):
    """Returns (path, blob sha) of all regular files in the commit tree or in the index."""
    entries = []
    if rev is None:
        raw = git(root, "ls-files", "-s", "-z")
        for rec in raw.split(b"\0"):
            if not rec:
                continue
            meta, _, path = rec.partition(b"\t")
            mode, sha, _stage = meta.decode().split(" ")
            if mode in ("100644", "100755"):
                entries.append((path.decode("utf-8", "surrogateescape"), sha))
    else:
        git(root, "rev-parse", "--verify", "--quiet", rev + "^{tree}")
        raw = git(root, "ls-tree", "-r", "-z", rev)
        for rec in raw.split(b"\0"):
            if not rec:
                continue
            meta, _, path = rec.partition(b"\t")
            mode, kind, sha = meta.decode().split(" ")
            if kind == "blob" and mode in ("100644", "100755"):
                entries.append((path.decode("utf-8", "surrogateescape"), sha))
    return entries


def read_blobs(root, shas):
    """Yields (sha, bytes or None for blobs above the size limit) for the given blob shas, streamed."""
    shas = sorted(set(shas))
    if not shas:
        return
    sizes = {}
    check = git(root, "cat-file", "--batch-check", input_bytes=("\n".join(shas) + "\n").encode())
    for line in check.decode().splitlines():
        sha, _kind, size = line.split(" ")
        sizes[sha] = int(size)
    wanted = [s for s in shas if sizes[s] <= MAX_SCAN_BYTES]
    for sha in shas:
        if sizes[sha] > MAX_SCAN_BYTES:
            yield sha, None
    if not wanted:
        return
    proc = subprocess.Popen(["git", "cat-file", "--batch"], cwd=root, stdin=subprocess.PIPE, stdout=subprocess.PIPE)

    def feed():
        try:
            proc.stdin.write(("\n".join(wanted) + "\n").encode())
        finally:
            proc.stdin.close()

    writer = threading.Thread(target=feed, daemon=True)
    writer.start()
    try:
        for sha in wanted:
            header = proc.stdout.readline().decode().split(" ")
            data = proc.stdout.read(int(header[2]))
            proc.stdout.read(1)  # trailing LF
            yield header[0], data
    finally:
        writer.join()
        proc.stdout.close()
        proc.wait()


# ---------------------------------------------------------------- globs and path rules

def glob_to_regex(glob):
    """Glob to regex: '*' and '?' stay inside a path segment, '**' crosses '/', '[...]' is a class."""
    out = []
    i = 0
    while i < len(glob):
        c = glob[i]
        if c == "*":
            if glob.startswith("**/", i):
                out.append("(?:.*/)?")
                i += 3
            elif glob.startswith("**", i):
                out.append(".*")
                i += 2
            else:
                out.append("[^/]*")
                i += 1
        elif c == "?":
            out.append("[^/]")
            i += 1
        elif c == "[" and glob.find("]", i + 2) != -1:
            j = glob.find("]", i + 2)
            body = glob[i + 1:j].replace("\\", "\\\\")
            if body.startswith("!"):
                body = "^" + body[1:]
            out.append("[" + body + "]")
            i = j + 1
        elif c == "\\" and i + 1 < len(glob):
            out.append(re.escape(glob[i + 1]))
            i += 2
        else:
            out.append(re.escape(c))
            i += 1
    return "".join(out)


def compile_glob(glob):
    """Glob matched against the whole repo-relative path (no gitignore semantics)."""
    return re.compile("^" + glob_to_regex(glob.strip().lstrip("/")) + "$")


class PathRule:
    """One gitignore-style line: no slash = any level, slash inside or in front = anchored at the root,
    trailing slash = directories only, leading '!' = re-include."""

    def __init__(self, line):
        self.text = line
        self.negated = line.startswith("!")
        pattern = line[1:] if self.negated else line
        self.dir_only = pattern.endswith("/")
        pattern = pattern.rstrip("/")
        self.anchored = "/" in pattern
        self.regex = re.compile("^" + glob_to_regex(pattern.lstrip("/")) + "$")

    def matches(self, prefix, is_dir):
        if self.dir_only and not is_dir:
            return False
        target = prefix if self.anchored else prefix.rsplit("/", 1)[-1]
        return self.regex.match(target) is not None


class PathRules:
    def __init__(self, lines):
        self.rules = []
        for raw in lines:
            line = raw.strip()
            if line and not line.startswith("#"):
                self.rules.append(PathRule(line))

    def match(self, path):
        """Returns the matching rule text if the file or one of its parent directories is listed, else None."""
        parts = path.split("/")
        for k in range(1, len(parts) + 1):
            prefix = "/".join(parts[:k])
            is_dir = k < len(parts)
            hit = None
            for rule in self.rules:
                if rule.matches(prefix, is_dir):
                    hit = None if rule.negated else rule.text
            if hit is not None:
                return hit
        return None


def load_private_paths(root):
    try:
        with open(root + "/" + PRIVATE_PATHS_FILE, encoding="utf-8-sig") as f:
            return PathRules(f.read().splitlines())
    except OSError:
        raise UsageError("cannot read " + PRIVATE_PATHS_FILE + " (needed for --paths and to skip private files)")


# ---------------------------------------------------------------- content patterns

_PLACEHOLDER_USERS = {
    "public", "default", "all", "shared", "user", "username", "name", "you", "me", "example", "runner",
    "yourname", "your-name", "your_name", "yourusername", "your-username", "myuser", "user1", "someone",
    "foo", "bar", "xxx", "x",
}


def _real_user(m):
    name = m.group("n").lower()
    return name not in _PLACEHOLDER_USERS and any(ch.isalnum() for ch in name)


_USER_CHARS = r"[^\\/\s\"'<>|*?%$\{\}:`()\[\]]+"
_PRIVATE_IP = (r"(?:10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}"
               r"|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}|169\.254\.\d{1,3}\.\d{1,3})")

# (reason, regex, optional filter on the match). Reasons do not repeat the matched text: CI logs are public.
GENERIC_PATTERNS = [
    ("absolute local user path (drive:\\Users\\<name>)",
     re.compile(r"(?i)(?<![\w])[A-Z]:[\\/]+Users[\\/]+(?P<n>" + _USER_CHARS + ")"), _real_user),
    ("absolute local user path (/home/<name>)",
     re.compile(r"(?<![\w.\\/-])/home/(?P<n>[A-Za-z0-9._-]+)"), _real_user),
    ("absolute local user path (/Users/<name>)",
     re.compile(r"(?<![\w:.\\/-])/Users/(?P<n>[A-Za-z0-9._-]+)"), _real_user),
    ("Windows machine/UNC path (\\\\host\\share)",
     re.compile(r"(?<![\\\w.:])\\\\(?P<h>[A-Za-z0-9][\w.-]*)\\(?P<s>[\w$.-]+)"), None),
    (".local/.lan host name",
     re.compile(r"(?i)(?<![\w.-])[a-z0-9][\w-]*(?:\.[\w-]+)*\.(?:local|lan)(?![\w-]|\.\w)"), None),
    ("private IP address in a URL",
     re.compile(r"(?i)\b[a-z][a-z0-9+.-]*://(?:[^/\s@]*@)?" + _PRIVATE_IP + r"(?!\d)"), None),
]


class PrivatePattern:
    def __init__(self, source, line_no, regex, allowed):
        try:
            self.regex = re.compile(regex)
        except re.error as e:
            raise UsageError("%s:%d: invalid regex: %s" % (source, line_no, e))
        self.text = regex
        self.allowed = [compile_glob(g) for g in allowed]

    def allows(self, path):
        return any(g.match(path) for g in self.allowed)


def load_private_patterns(path, explicit):
    try:
        with open(path, encoding="utf-8-sig") as f:
            lines = f.read().splitlines()
    except OSError:
        if explicit:
            raise UsageError("cannot read " + path)
        return []
    patterns = []
    for n, raw in enumerate(lines, 1):
        if not raw.strip() or raw.lstrip().startswith("#"):
            continue
        regex, _, globs = raw.partition("\t")
        allowed = [g.strip() for g in globs.split(",") if g.strip()]
        patterns.append(PrivatePattern(path, n, regex.strip(), allowed))
    return patterns


# ---------------------------------------------------------------- checks

def check_paths(entries, private):
    findings = []
    for path, _sha in entries:
        hit = private.match(path)
        if hit is not None:
            findings.append((path, 0, "private path (matches '%s' in %s)" % (hit, PRIVATE_PATHS_FILE)))
    return findings


def decode(data):
    if data.startswith(b"\xef\xbb\xbf"):
        data = data[3:]
    return data.decode("utf-8", "replace")


def scan_text(path, text, patterns):
    findings = []
    active = [p for p in patterns if not p.allows(path)]
    for n, line in enumerate(text.splitlines(), 1):
        if PRAGMA_ALLOW in line:
            continue
        for reason, regex, accept in GENERIC_PATTERNS:
            for m in regex.finditer(line):
                if accept is None or accept(m):
                    findings.append((path, n, reason))
                    break
        for p in active:
            if p.regex.search(line):
                findings.append((path, n, "private pattern /%s/" % p.text))
    return findings


def check_content(root, entries, private, patterns):
    findings = []
    by_sha = {}
    for path, sha in entries:
        if private.match(path) is not None:
            continue
        # Path names are checked with the private patterns as well.
        for p in patterns:
            if not p.allows(path) and p.regex.search(path):
                findings.append((path, 0, "private pattern /%s/ in the file name" % p.text))
        if not path.startswith(SKIP_CONTENT_PREFIXES):
            by_sha.setdefault(sha, []).append(path)
    skipped = 0
    for sha, data in read_blobs(root, by_sha.keys()):
        if data is None:
            skipped += len(by_sha[sha])
            continue
        if b"\0" in data[:8000]:
            continue
        text = decode(data)
        for path in by_sha[sha]:
            findings.extend(scan_text(path, text, patterns))
    if skipped:
        print("check-public: skipped %d file(s) above %d MB" % (skipped, MAX_SCAN_BYTES // (1024 * 1024)), file=sys.stderr)
    return findings


def main(argv):
    parser = argparse.ArgumentParser(prog="check-public.py", description="Keeps internal files and content out of the public repository.")
    parser.add_argument("--rev", help="check the tree of this commit (default: the index)")
    parser.add_argument("--paths", action="store_true", help="fail on files listed in " + PRIVATE_PATHS_FILE)
    parser.add_argument("--content", action="store_true", help="fail on internal content (generic and private patterns)")
    parser.add_argument("--private-patterns", metavar="FILE", help="private pattern file (default: " + DEFAULT_PATTERNS_FILE + ")")
    args = parser.parse_args(argv)
    do_paths = args.paths or not args.content
    do_content = args.content or not args.paths

    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except AttributeError:
            pass

    root = repo_root()
    entries = list_entries(root, args.rev)
    private = load_private_paths(root)
    findings = []
    if do_paths:
        findings += check_paths(entries, private)
    if do_content:
        explicit = args.private_patterns is not None
        patterns = load_private_patterns(args.private_patterns or root + "/" + DEFAULT_PATTERNS_FILE, explicit)
        findings += check_content(root, entries, private, patterns)
    findings = sorted(set(findings), key=lambda f: (f[0], f[1], f[2]))
    for path, line, reason in findings:
        print("%s:%d: %s" % (path, line, reason))
    if findings:
        print("check-public: %d finding(s)" % len(findings), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except UsageError as e:
        print("check-public: " + str(e), file=sys.stderr)
        sys.exit(2)
