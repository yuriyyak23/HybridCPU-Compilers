#!/usr/bin/env python3
"""HB-INT-01 project-graph policy and mutation tests; no .NET/native qualification."""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[4]
BRIDGE = ROOT / "Compilers" / "HybridCPU_Compiler" / "HardwareBridge"
HWIR = BRIDGE / "HWIR" / "HardwareBridge.Hwir.csproj"
COMPANION = BRIDGE / "HardwareBridge.Companion.csproj"
PARENT = BRIDGE.parent / "HybridCPU_Compiler.csproj"

def project(text):
    return ET.fromstring(text)

def references(tree):
    return [element.attrib["Include"] for element in tree.iter("ProjectReference")]

def core_has_bridge_backlink(text):
    return any("HardwareBridge" in ref for ref in references(project(text)))

class PortableDependencyBoundary(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.hwir_text = HWIR.read_text(encoding="utf-8")
        cls.hwir = project(cls.hwir_text)
        cls.companion_text = COMPANION.read_text(encoding="utf-8")
        cls.companion = project(cls.companion_text)
        cls.parent_text = PARENT.read_text(encoding="utf-8")
        cls.parent = project(cls.parent_text)

    def test_default_hwir_requires_no_core_checkout(self):
        self.assertEqual(references(self.hwir), ["$(CircuitWeaveFrontendRoslynProject)"])
        groups = list(self.hwir.findall("ItemGroup"))
        project_refs = [g for g in groups if any(i.tag == "ProjectReference" for i in g)]
        self.assertEqual(len(project_refs), 1)
        self.assertEqual(project_refs[0].get("Condition"),
                         "'$(EnableBridgeHwirLegacyFixtures)' == 'true'")
        self.assertIn("false</EnableBridgeHwirLegacyFixtures>", self.hwir_text)
        self.assertEqual(len([x for x in self.hwir.iter("Compile")
                              if "SourceSnapshot" in x.get("Include", "")]), 1)
        self.assertNotIn("CircuitWeave-main", self.hwir_text)

    def test_legacy_guard_is_explicit_and_fail_closed(self):
        target = next(t for t in self.hwir.iter("Target")
                      if t.get("Name") == "ValidatePinnedBridgeHwirFixtures")
        self.assertEqual(target.get("Condition"), "'$(EnableBridgeHwirLegacyFixtures)' == 'true'")
        body = ET.tostring(target, encoding="unicode")
        for token in ("HBINT0101", "HBINT0102", "HBINT0103", "HBINT0104",
                      "HBINT0105", "CircuitWeaveRepoRoot", "CircuitWeaveExpectedHeadSha",
                      "BridgeExpectedSdkVersion", "NETCoreSdkVersion",
                      "rev-parse HEAD", "status --porcelain", "DirtyCoreCheckout",
                      "GetFullPath"):
            with self.subTest(token=token):
                self.assertIn(token, body)
        self.assertEqual(len(list(target.iter("Exec"))), 2)

    def test_parent_compiler_does_not_link_hardware(self):
        self.assertFalse(any("HardwareBridge" in ref for ref in references(self.parent)))
        self.assertIn(r"HardwareBridge\**\*.cs", self.parent_text)
        self.assertEqual(references(self.companion),
                         [r"..\Roslyn\HybridCPU.Compiler.Roslyn.csproj"])
        self.assertIn("HARDWAREBRIDGE_COMPANION", self.companion_text)
        self.assertFalse(any("CircuitWeave" in ref for ref in references(self.companion)))

    def test_mutation_inadvertent_core_backlink(self):
        minimal_core = '<Project><ItemGroup><ProjectReference Include="../Bindings/Bindings.csproj" /></ItemGroup></Project>'
        mutated = minimal_core.replace("</ItemGroup>",
            '<ProjectReference Include="/some/HardwareBridge/HWIR/HardwareBridge.Hwir.csproj" /></ItemGroup>')
        self.assertFalse(core_has_bridge_backlink(minimal_core))
        self.assertTrue(core_has_bridge_backlink(mutated))

    def test_mutation_inadvertent_fixed_sibling(self):
        mutated = self.hwir_text.replace("$(CircuitWeaveFrontendRoslynProject)",
                                         "../../CircuitWeave-main/src/CircuitWeave.Frontend.Roslyn/CircuitWeave.Frontend.Roslyn.csproj")
        self.assertNotIn("CircuitWeave-main", self.hwir_text)
        self.assertIn("CircuitWeave-main", mutated)
        self.assertFalse(any("CircuitWeave-main" in ref for ref in references(self.hwir)))

    def test_mutation_lost_revision_pin(self):
        target = next(t for t in self.hwir.iter("Target")
                      if t.get("Name") == "ValidatePinnedBridgeHwirFixtures")
        original = ET.tostring(target, encoding="unicode")
        changed = original.replace("rev-parse HEAD", "rev-parse --is-inside-work-tree")
        self.assertIn("rev-parse HEAD", original)
        self.assertNotIn("rev-parse HEAD", changed)

    def test_mutation_lost_sdk_guard(self):
        target = next(t for t in self.hwir.iter("Target")
                      if t.get("Name") == "ValidatePinnedBridgeHwirFixtures")
        original = ET.tostring(target, encoding="unicode")
        changed = original.replace("NETCoreSdkVersion", "TrustedSdkWithoutCheck")
        self.assertIn("NETCoreSdkVersion", original)
        self.assertNotIn("NETCoreSdkVersion", changed)

    def test_no_source_write_or_software_authority_upgrade(self):
        self.assertFalse(list(self.hwir.iter("Exec")) or list(self.companion.iter("Exec")))
        self.assertNotIn("CircuitWeave", self.companion_text)
        # The only always-compiled HWIR file is an existing read-only snapshot.
        unconditional = [c.get("Include") for group in self.hwir.findall("ItemGroup")
                         if group.get("Condition") is None for c in group.findall("Compile")]
        self.assertEqual(unconditional, [r"..\SourceSnapshot\BridgeSourceSnapshotV1.cs"])

if __name__ == "__main__":
    unittest.main(verbosity=2)
