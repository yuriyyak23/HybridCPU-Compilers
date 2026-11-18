#!/usr/bin/env python3
"""Materialize exact Git blob bytes; avoids archive/checkout EOL filters."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import subprocess
import sys


def io_path(path: pathlib.Path) -> str:
    value = os.path.abspath(os.fspath(path))
    if os.name == "nt" and len(value) >= 248 and not value.startswith("\\\\?\\"):
        return "\\\\?\\" + value
    return value


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--git-dir", required=True, type=pathlib.Path)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--output-root", required=True, type=pathlib.Path)
    parser.add_argument("--manifest", required=True, type=pathlib.Path)
    parser.add_argument("--paths", required=True, nargs="+")
    args = parser.parse_args()
    if args.output_root.exists() or args.manifest.exists():
        raise SystemExit("refusing existing Golden materialization output")

    git_prefix = ["git", f"--git-dir={args.git_dir}"]
    listing = subprocess.check_output(
        git_prefix + ["ls-tree", "-r", "-z", "--full-tree", args.commit, "--", *args.paths]
    )
    entries = []
    for record in listing.split(b"\0"):
        if not record:
            continue
        header, raw_path = record.split(b"\t", 1)
        mode, kind, oid = header.decode("ascii").split(" ")
        if kind != "blob":
            raise SystemExit(f"unsupported tree entry: {raw_path!r} ({kind})")
        relative = raw_path.decode("utf-8", errors="surrogateescape")
        pure = pathlib.PurePosixPath(relative)
        if pure.is_absolute() or ".." in pure.parts:
            raise SystemExit(f"unsafe tree path: {relative}")
        entries.append({"path": relative, "mode": mode, "blob": oid})
    if not entries:
        raise SystemExit("pinned commit/path selection returned no blobs")

    args.output_root.mkdir(parents=True)
    process = subprocess.Popen(
        git_prefix + ["cat-file", "--batch"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    assert process.stdin is not None and process.stdout is not None
    manifest_files = []
    for entry in entries:
        # Send and consume one object at a time. Writing every OID before reading
        # stdout can deadlock when cat-file's output pipe fills on large trees.
        process.stdin.write(entry["blob"].encode("ascii") + b"\n")
        process.stdin.flush()
        header = process.stdout.readline().decode("ascii", errors="strict").strip().split()
        if len(header) != 3 or header[0] != entry["blob"] or header[1] != "blob":
            raise SystemExit(f"unexpected cat-file response for {entry['path']}: {header}")
        remaining = int(header[2])
        relative = pathlib.PurePosixPath(entry["path"])
        destination = args.output_root.joinpath(*relative.parts)
        os.makedirs(io_path(destination.parent), exist_ok=True)
        digest = hashlib.sha256()
        size = 0
        with open(io_path(destination), "wb") as output:
            while remaining:
                block = process.stdout.read(min(1024 * 1024, remaining))
                if not block:
                    raise SystemExit(f"truncated blob: {entry['path']}")
                output.write(block)
                digest.update(block)
                size += len(block)
                remaining -= len(block)
        if process.stdout.read(1) != b"\n":
            raise SystemExit(f"missing cat-file record delimiter: {entry['path']}")
        manifest_files.append({**entry, "bytes": size, "sha256": digest.hexdigest()})

    process.stdin.close()

    stderr = process.stderr.read() if process.stderr else b""
    code = process.wait()
    if code:
        raise subprocess.CalledProcessError(code, process.args, stderr=stderr)
    manifest = {
        "schema": "circuitweave.pinned-git-raw-blob-materialization/v1",
        "commit": args.commit,
        "materialization": "git cat-file --batch raw blob bytes; no archive or checkout filters",
        "fileCount": len(manifest_files),
        "files": manifest_files,
    }
    args.manifest.parent.mkdir(parents=True, exist_ok=True)
    args.manifest.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": "PASS_EXACT_RAW_GIT_BLOBS", "files": len(manifest_files), "manifest": str(args.manifest)}))


if __name__ == "__main__":
    main()
