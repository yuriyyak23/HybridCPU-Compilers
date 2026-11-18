"""Offline contract checks; never invokes a publisher or EDA tool."""
import ast
import hashlib
import importlib.util
import json
import pathlib
import subprocess
import tempfile
import unittest

HERE = pathlib.Path(__file__).resolve().parent
VERIFIER_SPEC = importlib.util.spec_from_file_location(
    "verify_stage_aware_fault_winner_rtl", HERE / "Verify-StageAwareFaultWinnerRtl.py")
VERIFIER = importlib.util.module_from_spec(VERIFIER_SPEC)
VERIFIER_SPEC.loader.exec_module(VERIFIER)


class ComposedReceiptContract(unittest.TestCase):
    def test_runtime_trace_reader_accepts_published_json_array(self):
        rows = [{"scenario": "a", "value": 1}, {"scenario": "b", "value": 2}]
        with tempfile.TemporaryDirectory() as tmp:
            path = pathlib.Path(tmp) / "trace.jsonl"
            path.write_text(json.dumps(rows, indent=2), encoding="utf-8")
            self.assertEqual(VERIFIER.read_runtime_trace_rows(str(path)), rows)

    def test_runtime_trace_reader_accepts_ndjson(self):
        rows = [{"scenario": "a", "value": 1}, {"scenario": "b", "value": 2}]
        with tempfile.TemporaryDirectory() as tmp:
            path = pathlib.Path(tmp) / "trace.jsonl"
            path.write_text("\n".join(json.dumps(row) for row in rows) + "\n", encoding="utf-8")
            self.assertEqual(VERIFIER.read_runtime_trace_rows(str(path)), rows)

    def test_runtime_trace_reader_rejects_non_object_rows(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = pathlib.Path(tmp) / "trace.jsonl"
            path.write_text('[{"scenario":"a"}, 7]', encoding="utf-8")
            with self.assertRaises(ValueError):
                VERIFIER.read_runtime_trace_rows(str(path))

    def test_verifier_writes_original_bytes_and_claims_them(self):
        tree = ast.parse((HERE / "Verify-StageAwareFaultWinnerRtl.py").read_text(encoding="utf-8"))
        source = ast.unparse(tree)
        self.assertIn("composed_bytes = composed_rtl.read_bytes()", source)
        self.assertIn("composed_text = composed_bytes.decode('utf-8-sig')", source)
        self.assertIn("staged_composed_rtl.write_bytes(composed_bytes)", source)
        self.assertIn("run(args.iverilog, args.vvp, staged_composed_rtl,", source)
        self.assertIn("staged_composed_rtl.read_bytes() != composed_bytes", source)
        self.assertIn("'candidateRtlSha256': sha(composed_bytes)", source)
        self.assertIn("'vectorsSha256': sha(vector_bytes)", source)
        self.assertIn("'schema': 'circuitweave.processor-composed-pagefault-delivery-rtl-check/v2'", source)

    def test_publisher_binding_validator(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            rtl = b"module x;\nendmodule\n"
            vectors = b'{"vector":1}\n'
            (root / "composed-systemverilog").write_bytes(rtl)
            (root / "vectors.jsonl").write_bytes(vectors)
            receipt = {"schema": "circuitweave.processor-composed-pagefault-delivery-rtl-check/v2",
                       "candidateRtlSha256": hashlib.sha256(rtl).hexdigest(),
                       "vectorsSha256": hashlib.sha256(vectors).hexdigest()}
            receipt_path = root / "composed-rtl-receipt.json"
            def write():
                receipt_path.write_text(json.dumps(receipt), encoding="utf-8")
            write()
            # Load only the function AST, never execute the publisher script.
            command = r"""param($publisher,$snapshot)
$tokens=$null; $errors=$null
$ast=[System.Management.Automation.Language.Parser]::ParseFile($publisher,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw 'Publisher parse error' }
$f=$ast.FindAll({param($x) $x -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $x.Name -ceq 'Assert-ComposedReceiptBindings'},$true)
if (@($f).Count -ne 1) { throw 'Expected one validator' }
. ([scriptblock]::Create($f[0].Extent.Text))
Assert-ComposedReceiptBindings $snapshot
"""
            script = root / "check.ps1"
            script.write_text(command, encoding="utf-8")
            def check():
                return subprocess.run(["pwsh", "-NoProfile", "-NonInteractive", "-File", str(script),
                                       str(HERE / "Publish-ComposedPageFaultDeliveryStudio.ps1"), str(root)],
                                      capture_output=True, text=True).returncode
            self.assertEqual(check(), 0)
            receipt["schema"] = "old"; write(); self.assertNotEqual(check(), 0)
            receipt["schema"] = "circuitweave.processor-composed-pagefault-delivery-rtl-check/v2"
            receipt["candidateRtlSha256"] = "0" * 64; write(); self.assertNotEqual(check(), 0)
            receipt["candidateRtlSha256"] = hashlib.sha256(rtl).hexdigest()
            receipt["vectorsSha256"] = "0" * 64; write(); self.assertNotEqual(check(), 0)
            receipt["vectorsSha256"] = hashlib.sha256(vectors).hexdigest(); write()
            (root / "composed-systemverilog").write_bytes(rtl.replace(b"\n", b"\r\n"))
            self.assertNotEqual(check(), 0)
            (root / "composed-systemverilog").write_bytes(rtl)
            (root / "vectors.jsonl").unlink(); self.assertNotEqual(check(), 0)


if __name__ == "__main__":
    unittest.main()
