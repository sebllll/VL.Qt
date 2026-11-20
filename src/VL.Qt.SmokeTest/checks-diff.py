# Temporary (W1-01): compares the check lines of a SmokeTest run with the baseline, see CONVERTING.md.
import re, collections, sys

def norm(m):
    m = m.strip().replace("\\", "/")
    m = re.sub(r"[A-Za-z]?:?/[^ ()]*?smoke/", "<smoke>/", m)
    m = re.sub(r"^(\(?\d+[a-z]?\)?|[A-Z]\d[a-z]?):\s*", "", m)
    m = re.sub(r"[^\x20-\x7e]", "?", m)
    m = re.sub(r"\d+(\.\d+)?", "N", m)
    return m.strip()

base = collections.Counter(); mine = collections.Counter()
for l in open(sys.argv[1], encoding="utf-8", errors="replace"):
    p = l.rstrip("\n").split("\t")
    if len(p) >= 4:
        base[(p[2], norm(p[3]))] += 1
for l in open(sys.argv[2], encoding="utf-8", errors="replace"):
    p = l.rstrip("\n").split("\t")
    if len(p) >= 3 and p[0] not in sys.argv[3:]:
        mine[(p[1], norm(p[2]))] += 1
print("base", sum(base.values()), "mine", sum(mine.values()))
print("only base:")
for k, v in (base - mine).items(): print(" ", k, v)
print("only mine:")
for k, v in (mine - base).items(): print(" ", k, v)
