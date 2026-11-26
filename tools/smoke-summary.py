#!/usr/bin/env python3
"""Summary of an NUnit SmokeTest run (tools/test-linux.sh): counters of the .trx, failed tests with their message, failing checks.
Usage: smoke-summary.py <smoke dir>   (reads smoke.trx and checks.tsv there)
Exit code: 0 = tests ran and none failed, 1 = failed tests, 2 = no .trx or no test executed (e.g. a filter that matches nothing)."""
import os
import sys
import xml.etree.ElementTree as ET

d = sys.argv[1]
trx = os.path.join(d, "smoke.trx")
if not os.path.exists(trx):
    print(f"no smoke.trx in {d}")
    sys.exit(2)
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
root = ET.parse(trx).getroot()
c = root.find("t:ResultSummary/t:Counters", ns).attrib
# A test with a warning (a perf limit in warn-only mode) is NotExecuted in the trx, but it ran: list its WARN lines
# and count it as run (like tools/build.ps1).
warned = 0
for r in root.iterfind(".//t:UnitTestResult", ns):
    if r.get("outcome") == "NotExecuted":
        out = r.find("t:Output/t:StdOut", ns)
        lines = [l.strip() for l in (out.text or "").splitlines() if l.lstrip().startswith("WARN ")] if out is not None else []
        if lines:
            warned += 1
        for line in lines:
            print(f"WARN {r.get('testName', '?')}: {line[5:]}")
    if r.get("outcome") == "Failed":
        print("FAILED TEST " + r.get("testName", "?"))
        msg = r.find("t:Output/t:ErrorInfo/t:Message", ns)
        if msg is not None and msg.text:
            for line in msg.text.strip().splitlines()[:8]:
                print("    " + line)
tsv = os.path.join(d, "checks.tsv")
if os.path.exists(tsv):
    with open(tsv, encoding="utf-8", errors="replace") as f:
        for line in f:
            parts = line.rstrip("\n").split("\t")
            if len(parts) > 1 and parts[1] == "FAIL":
                print("FAIL check: " + " | ".join(parts))
print(f"SmokeTest: {c['passed']} passed, {c['failed']} failed, {warned} with warnings, {c['notExecuted']} not executed of {c['total']} tests ({trx})")
if int(c["executed"]) + warned == 0:
    print("no SmokeTest ran (filter?)")
    sys.exit(2)
sys.exit(1 if int(c["failed"]) or int(c.get("error", 0)) or int(c.get("aborted", 0)) else 0)
