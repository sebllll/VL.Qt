"""Enforce the repo encoding rule: UTF-8 BOM for .cs/.vl, no BOM elsewhere.

Line endings are handled by .gitattributes; this only fixes the BOM.

Usage:
  python tools/fix-encoding.py <file>...   fix the given files
  python tools/fix-encoding.py --all       fix all tracked text files
  python tools/fix-encoding.py --staged    fix the files staged for commit and stage the fixes (git pre-commit hook)
  python tools/fix-encoding.py --check ... only report (exit 1 if a file is wrong), combine with files, --all or --staged
  python tools/fix-encoding.py --hook      Claude Code PostToolUse hook (JSON on stdin)
"""
import json
import pathlib
import subprocess
import sys

BOM = b"\xef\xbb\xbf"
BOM_EXTS = {".cs", ".vl"}
# .props are left alone: vvvv writes help/*.props (export settings) with BOM, MSBuild ones have none.
NO_BOM_EXTS = {".md", ".ps1", ".py", ".cpp", ".h", ".qml", ".csproj",
               ".targets", ".json", ".yml", ".yaml", ".txt", ".sh", ".map", ".xml"}


def needs_fix(path: pathlib.Path) -> bool:
    ext = path.suffix.lower()
    if ext not in BOM_EXTS and ext not in NO_BOM_EXTS or not path.is_file():
        return False
    has_bom = path.read_bytes()[:3] == BOM
    return (ext in BOM_EXTS) != has_bom


def fix(path: pathlib.Path) -> bool:
    if not needs_fix(path):
        return False
    data = path.read_bytes()
    path.write_bytes(data[len(BOM):] if data.startswith(BOM) else BOM + data)
    return True


def git_paths(*args: str) -> list[str]:
    out = subprocess.run(["git", *args, "-z"], capture_output=True, check=True).stdout
    return [p for p in out.decode("utf-8").split("\0") if p]


def main() -> int:
    args = sys.argv[1:]
    check = "--check" in args
    args = [a for a in args if a != "--check"]
    staged = args == ["--staged"]
    if args == ["--hook"]:
        try:
            payload = json.load(sys.stdin)
        except ValueError:
            return 0
        file_path = (payload.get("tool_input") or {}).get("file_path")
        paths = [file_path] if file_path else []
    elif args == ["--all"]:
        paths = git_paths("ls-files")
    elif staged:
        paths = git_paths("diff", "--cached", "--name-only", "--diff-filter=ACMR")
    else:
        paths = args

    if check:
        wrong = [p for p in paths if needs_fix(pathlib.Path(p))]
        for p in wrong:
            print(f"wrong BOM: {p}")
        return 1 if wrong else 0

    # Before fixing: a file with unstaged edits must not be staged as a whole afterwards.
    unstaged = set(git_paths("diff", "--name-only")) if staged else set()
    changed = [p for p in paths if fix(pathlib.Path(p))]
    if args == ["--hook"]:
        return 0
    for p in changed:
        print(f"fixed BOM: {p}")
    if staged:
        # Re-stage the fixes. A file that also has unstaged edits is left for you to stage, so no edit is committed unseen.
        partial = [p for p in changed if p in unstaged]
        for p in changed:
            if p not in partial:
                subprocess.run(["git", "add", "--", p], check=True)
        if partial:
            print("The BOM of these files was fixed, but they have unstaged changes. Stage them again and commit:")
            for p in partial:
                print(f"  git add {p}")
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
