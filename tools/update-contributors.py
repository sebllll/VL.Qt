#!/usr/bin/env python3
"""Writes CONTRIBUTORS.md from the CLA signatures and the merged pull requests.

The file lives on the branch cla-signatures next to the signatures: the workflow contributors.yml writes it
there, because develop and main only take pull requests.

A login is listed if it signed the CLA (signatures/cla.json of the contributor-assistant action),
has at least one merged pull request and is not on the opt-out list. Logins passed with
--include are always listed (the maintainer, who never signs).

Usage:
  python tools/update-contributors.py --signatures cla.json --merged merged.txt --out CONTRIBUTORS.md
                                      [--optout optout.txt] [--include <login> ...]

  --signatures  JSON file of the action: {"signedContributors": [{"name": "<login>", ...}, ...]}
  --merged      text file with one GitHub login per line (authors of merged pull requests)
  --optout      optional text file with one login per line (empty lines and lines starting with # ignored);
                kept in signatures/optout.txt on the branch cla-signatures
  --include     login that is listed regardless of signature and merged pull requests (repeatable)
  --out         file to write (LF, no BOM); the exit code is 0 whether or not it changed

Logins are compared case-insensitively; the list is sorted case-insensitively.
"""

import argparse
import json
import sys
from pathlib import Path

# The list lives on the branch cla-signatures, so links into the project are absolute.
DOCS = "https://github.com/sebllll/VL.Qt/blob/develop/"
HEADER = (
    "# Contributors\n"
    "\n"
    f"People who contributed code to VL.Qt. The list is generated automatically from the signed [CLA]({DOCS}CLA.md) "
    f"and the merged pull requests of this repository (see [CONTRIBUTING.md]({DOCS}CONTRIBUTING.md)); "
    "please do not edit it by hand.\n"
    "\n"
)


def read_logins(path):
    logins = []
    for line in Path(path).read_text(encoding="utf-8-sig").splitlines():
        line = line.strip()
        if line and not line.startswith("#"):
            logins.append(line.lstrip("@"))
    return logins


def read_signers(path):
    data = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    return [entry["name"] for entry in data.get("signedContributors", []) if entry.get("name")]


def build(signers, merged, optout, include):
    merged_keys = {login.lower() for login in merged}
    optout_keys = {login.lower() for login in optout}
    chosen = {}
    for login in signers:
        if login.lower() in merged_keys:
            chosen.setdefault(login.lower(), login)
    for login in include:
        chosen.setdefault(login.lower(), login)
    names = [chosen[key] for key in sorted(chosen) if key not in optout_keys]
    return HEADER + "".join(f"- [@{name}](https://github.com/{name})\n" for name in names)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--signatures", required=True)
    parser.add_argument("--merged", required=True)
    parser.add_argument("--optout")
    parser.add_argument("--include", action="append", default=[])
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    optout = read_logins(args.optout) if args.optout and Path(args.optout).exists() else []
    text = build(read_signers(args.signatures), read_logins(args.merged), optout, args.include)

    out = Path(args.out)
    if out.exists() and out.read_bytes() == text.encode("utf-8"):
        print(f"{out}: unchanged")
        return 0
    out.write_bytes(text.encode("utf-8"))
    print(f"{out}: written")
    return 0


if __name__ == "__main__":
    sys.exit(main())
