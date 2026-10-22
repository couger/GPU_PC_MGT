"""Scan reachable Git history and the source allowlist; print locations, never secrets."""
import argparse
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PATTERNS = {
    "private-key": r"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----",
    "github-token": r"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})",
    "api-key": r"\b(?:AKIA[A-Z0-9]{16}|sk-(?:proj-)?[A-Za-z0-9_-]{24,})",
    "dpapi-data": r"AQAAANCMnd8BFdER[A-Za-z0-9+/=]{30,}",
    "private-network": r"\b(?:192\.168\.\d+\.\d+|10\.\d+\.\d+\.\d+|172\.(?:1[6-9]|2\d|3[01])\.\d+\.\d+)\b",
    "personal-path": r"[A-Za-z]:[\\/]+Users[\\/]+[^\\/\s\"']+",
    "credential-url": r"\b(?:https?|ssh|ftp)://[^\s/:]+:[^\s/@]+@",
}
BLOCKED = re.compile(r"(^|/)(ssh|data|\.tmp)/|(^|/)(config|widget|probe-config|probe-tray|probe-ui)\.json$|\.(exe|dll|pdb|log|pem|key|pfx|p12|bak|zip)$|^web/data\.json$")

def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--history", action="store_true")
    parser.add_argument("--working-tree", action="store_true")
    options = parser.parse_args()
    failed = []
    objects = {}
    if options.history:
        for commit in git("rev-list", "--all").decode().splitlines():
            for item in git("ls-tree", "-rz", commit).split(b"\0"):
                if not item:
                    continue
                head, path = item.split(b"\t", 1)
                _, kind, oid = head.split()
                if kind == b"blob":
                    objects.setdefault(oid.decode(), set()).add(path.decode())
    def inspect(path, content):
        if BLOCKED.search(path):
            failed.append((path, 0, "blocked-file"))
            return
        if path.endswith((".png", ".ico")):
            return
        text = content.decode("utf-8-sig", errors="replace")
        for label, pattern in PATTERNS.items():
            for match in re.finditer(pattern, text):
                failed.append((path, text[:match.start()].count("\n") + 1, label))
    for oid, names in objects.items():
        content = git("cat-file", "blob", oid)
        for path in names:
            inspect(path, content)
    if options.working_tree:
        paths = git("ls-files", "--cached", "--others", "--exclude-standard", "-z").split(b"\0")
        for name in paths:
            if name:
                path = name.decode()
                source = ROOT / path
                if source.is_file():
                    inspect(path, source.read_bytes())
    elif not options.history:
        parser.error("select --history and/or --working-tree")
    for path, line, label in sorted(set(failed)):
        print(f"REVIEW {path}:{line} [{label}]")
    if failed:
        return 1
    print(f"PASS public-content scan; {len(objects)} historical blobs; working tree={options.working_tree}")
    return 0

if __name__ == "__main__":
    sys.exit(main())
