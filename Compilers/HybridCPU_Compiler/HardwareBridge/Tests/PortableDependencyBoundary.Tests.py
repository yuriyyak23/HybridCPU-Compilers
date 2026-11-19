#!/usr/bin/env python3
"""HB-INT-01 static boundary regression; not a .NET/Golden/native qualification."""
from pathlib import Path
import re
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[3]
bridge = root / "Compilers" / "HybridCPU_Compiler" / "HardwareBridge"
hwir = bridge / "HWIR" / "HardwareBridge.Hwir.csproj"
companion = bridge / "HardwareBridge.Companion.csproj"

hwir_text = hwir.read_text(encoding="utf-8")
hwir_xml = ET.parse(hwir).getroot()
ref = [x.attrib["Include"] for x in hwir_xml.iter("ProjectReference")]
assert ref == ["$(CircuitWeaveFrontendRoslynProject)"], ref
assert "CircuitWeave-main" not in hwir_text
assert "HBINT0101" in hwir_text and "HBINT0102" in hwir_text
assert "Condition" in hwir_text
assert "HardwareBridge.Hwir.csproj" not in companion.read_text(encoding="utf-8")
companion_refs = [x.attrib["Include"] for x in ET.parse(companion).getroot().iter("ProjectReference")]
assert companion_refs == [r"..\Roslyn\HybridCPU.Compiler.Roslyn.csproj"], companion_refs
assert "CircuitWeave" not in companion.read_text(encoding="utf-8")
print("PASS HWIR-only opt-in explicit ProjectReference; no fixed sibling Core path")
print("PASS Companion software compiler reference independent of HWIR/Core")
print("NotRun: requires .NET11 and a separately pinned Core for regression vectors")
