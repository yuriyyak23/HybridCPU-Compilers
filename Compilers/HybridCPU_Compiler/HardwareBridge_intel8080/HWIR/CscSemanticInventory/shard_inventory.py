"""Split a bounded semantic inventory into digest-addressed JSON artifacts.

Each source type is present once. The index retains project-level metadata and
binds every shard to the original report identity; no qualification is implied.
"""
import hashlib
import json
import os
import sys
import tempfile
from pathlib import Path

source, target = map(Path, sys.argv[1:])
if target.resolve().parent != Path(tempfile.gettempdir()).resolve() or target.exists():
    raise ValueError("output directory must be a new direct child of TEMP")
data = source.read_bytes()
report = json.loads(data)
if report["schema"] != "hardwarebridge.csc-semantic-inventory/v1" or report["disposition"] != "isolated-unqualified":
    raise ValueError("unsupported semantic report")
target.mkdir()
def encode(obj):
    return json.dumps(obj, ensure_ascii=True, separators=(",", ":")).encode()

parts = []
project_metadata = []
limit = 18 * 1024 * 1024
for project in report["projects"]:
    project_metadata.append({k: v for k, v in project.items() if k != "types"})
    group, size = [], 0
    def flush():
        global group, size
        if not group:
            return
        name = f"semantic-types-{len(parts):03d}.json"
        content = b'{"schema":"hardwarebridge.csc-semantic-types/v1","project":' + encode(project["project"]) + b',"types":[' + b",".join(group) + b"]}"
        if len(content) > 24 * 1024 * 1024:
            raise ValueError("semantic shard exceeds limit")
        (target / name).write_bytes(content)
        parts.append({"file": name, "project": project["project"], "typeCount": len(group), "length": len(content), "sha256": hashlib.sha256(content).hexdigest()})
        group, size = [], 0
    for item in project["types"]:
        raw = encode(item)
        if group and size + len(raw) > limit:
            flush()
        group.append(raw); size += len(raw) + 1
    flush()
if len(parts) > 64:
    raise ValueError("too many semantic shards")
index = {"schema": "hardwarebridge.csc-semantic-shards/v1", "disposition": "isolated-unqualified",
         "reportSha256": hashlib.sha256(data).hexdigest(), "graphSha256": report["graphSha256"], "cscAuditSha256": report["cscAuditSha256"],
         "scope": report["scope"], "projects": project_metadata, "shards": parts}
(target / "semantic-index.json").write_bytes(encode(index))
print("Sharded", sum(p["typeCount"] for p in parts), "types into", len(parts), "artifacts")
