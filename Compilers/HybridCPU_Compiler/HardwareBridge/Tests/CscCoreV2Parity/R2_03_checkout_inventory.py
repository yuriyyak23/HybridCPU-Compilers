"""R2-03 full checkout SHA-256 inventory; never follows symlinks.

Stores a before manifest in runner TEMP and checks HEAD, all tracked bytes and
Git porcelain after execution. No source contents are copied or uploaded.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import sys


def git(root, *argv):
    p = subprocess.run(["git", "-C", str(root), *argv],
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                       timeout=120, check=False)
    if p.returncode:
        raise RuntimeError("R203_GIT_EXIT_%s" % p.returncode)
    return p.stdout


def snapshot(root):
    root = root.resolve(strict=True)
    head = git(root, "rev-parse", "HEAD").decode("ascii").strip()
    if len(head) != 40 or any(x not in '0123456789abcdef' for x in head):
        raise RuntimeError("R203_INVALID_HEAD")
    if git(root, "status", "--porcelain=v1", "-z", "--untracked-files=all"):
        raise RuntimeError("R203_DIRTY_CHECKOUT")
    entries = []
    seen = set()
    listing = git(root, "ls-files", "--stage", "-z")
    for entry in listing.split(b"\0"):
        if not entry:
            continue
        try:
            header, encoded = entry.split(b"\t", 1)
            mode, _, stage = header.decode("ascii").split(" ")
            relative = encoded.decode("utf-8", errors="strict")
        except (ValueError, UnicodeError) as ex:
            raise RuntimeError("R203_INVALID_GIT_INDEX") from ex
        if stage != "0" or mode not in ("100644", "100755", "120000"):
            raise RuntimeError("R203_UNMERGED_OR_UNKNOWN_MODE")
        parts = relative.split("/")
        if (not relative or relative.startswith("/") or "\\" in relative or
                any(p in ("", ".", "..") for p in parts)):
            raise RuntimeError("R203_UNSAFE_GIT_PATH")
        folded = relative.casefold()
        if folded in seen:
            raise RuntimeError("R203_CASE_COLLISION")
        seen.add(folded)
        path = root.joinpath(*parts)
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode):
            raw = os.fsencode(os.readlink(path))
            kind = "symlink"
        elif stat.S_ISREG(info.st_mode):
            with path.open("rb") as stream:
                if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
                    raise RuntimeError("R203_NONREGULAR_TRACKED_FILE")
                raw = stream.read()
            after = path.lstat()
            if (info.st_size, info.st_mtime_ns, info.st_ino) != (
                    after.st_size, after.st_mtime_ns, after.st_ino):
                raise RuntimeError("R203_CONCURRENT_SOURCE_WRITE")
            kind = "file"
        else:
            raise RuntimeError("R203_NONREGULAR_TRACKED_FILE")
        entries.append((relative, mode, kind, len(raw), hashlib.sha256(raw).hexdigest()))
    manifest = json.dumps(entries, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    return {"head": head, "count": len(entries),
            "inventory_sha256": hashlib.sha256(manifest).hexdigest()}


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--root", type=Path, required=True)
    choice = p.add_mutually_exclusive_group(required=True)
    choice.add_argument("--record", type=Path)
    choice.add_argument("--verify", type=Path)
    args = p.parse_args()
    current = snapshot(args.root)
    if args.record:
        output = args.record.resolve(strict=False)
        if output.exists():
            raise RuntimeError("R203_INVENTORY_EXISTS")
        if output.is_relative_to(args.root.resolve(strict=True)):
            raise RuntimeError("R203_INVENTORY_INSIDE_CHECKOUT")
        output.write_bytes((json.dumps(current, sort_keys=True) + "\n").encode("utf-8"))
        print("R203_INVENTORY_BEFORE count=%d sha256=%s HEAD=%s" %
              (current["count"], current["inventory_sha256"], current["head"]))
    else:
        expected = json.loads(args.verify.read_bytes())
        if expected != current:
            raise RuntimeError("R203_CHECKOUT_CHANGED: expected %s, actual %s" %
                               (expected["inventory_sha256"], current["inventory_sha256"]))
        print("R203_INVENTORY_AFTER count=%d sha256=%s HEAD=%s" %
              (current["count"], current["inventory_sha256"], current["head"]))


if __name__ == "__main__":
    try:
        main()
    except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as ex:
        print("R203_INVENTORY_FAIL " + str(ex), file=sys.stderr)
        sys.exit(1)
