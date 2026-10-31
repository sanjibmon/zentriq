#!/usr/bin/env python3
"""Checks an inventory document written by `zentriq-agent --collect` against the expected distribution.

Usage: check-inventory.py <inventory.json> <distribution> <family> <server|desktop>
Prints one line describing what was detected; exits 1 when something expected is missing or wrong.
"""
import json
import sys

path, dist, fam, role = sys.argv[1:5]
try:
    with open(path, encoding="utf-8") as f:
        doc = json.load(f)
except (OSError, ValueError) as e:
    print(f"no inventory ({e})")
    sys.exit(1)

os_ = doc.get("os", {})
problems = []
def expect(cond, text):
    if not cond:
        problems.append(text)

expect(os_.get("family") == "linux", f"family {os_.get('family')}")
expect(os_.get("distribution") == dist, f"distribution {os_.get('distribution')} (expected {dist})")
expect(os_.get("distributionFamily") == fam, f"distribution family {os_.get('distributionFamily')} (expected {fam})")
expect(os_.get("isServer") == (role == "server"), f"isServer {os_.get('isServer')} (expected {role})")
expect(bool(os_.get("name")) and bool(os_.get("version")), "name / version missing")
expect(bool(os_.get("editionId")), "edition missing")
expect(bool(os_.get("kernel")), "kernel missing")
expect(os_.get("packageManager") in ("dpkg", "rpm"), f"package manager {os_.get('packageManager')}")
expect(len(doc.get("software", [])) >= 20, f"only {len(doc.get('software', []))} packages")
expect(any(s.get("name") == "zentriq-agent" for s in doc.get("software", [])) or "tar" in path, "zentriq-agent package not in the software list")
expect(bool(doc.get("device", {}).get("hostname")), "hostname missing")
expect((doc.get("hardware", {}).get("memoryBytes") or 0) > 0, "memory missing")
expect(doc.get("hardware", {}).get("cpuName"), "processor missing")
expect(doc.get("source") == "agent", f"source {doc.get('source')}")
expect(isinstance(doc.get("security", {}).get("linux"), dict), "Linux security settings missing")

summary = (f"{os_.get('name')} {os_.get('version')} · {os_.get('distribution')} ({os_.get('distributionFamily')}) · "
           f"{os_.get('editionId')} · {os_.get('installationType')} · desktop {os_.get('desktopEnvironment') or 'none'} · "
           f"{len(doc.get('software', []))} packages ({os_.get('packageManager')}) · kernel {os_.get('kernel')}")
print(summary + (" · PROBLEMS: " + "; ".join(problems) if problems else ""))
sys.exit(1 if problems else 0)
