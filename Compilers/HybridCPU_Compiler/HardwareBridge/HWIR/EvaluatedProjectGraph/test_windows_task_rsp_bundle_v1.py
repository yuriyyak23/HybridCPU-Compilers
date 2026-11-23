"""Expose only the Windows RSP bundle tests to top-level Bridge discovery."""
import importlib.util
from pathlib import Path
import sys


_nested_dir = Path(__file__).parent / "TaskTimeObserver"
_nested_file = _nested_dir / "test_windows_task_rsp_bundle_v1.py"
sys.path.insert(0, str(_nested_dir))
try:
    _spec = importlib.util.spec_from_file_location(
        "bridge_tasktime_windows_rsp_bundle_tests", _nested_file)
    if _spec is None or _spec.loader is None:
        raise ImportError("cannot load nested Windows RSP bundle tests")
    _module = importlib.util.module_from_spec(_spec)
    _spec.loader.exec_module(_module)
finally:
    sys.path.remove(str(_nested_dir))


class WindowsTaskRspBundleDiscoveryTests(_module.SyntheticWindowsTaskRspBundleTests):
    """Inherited synthetic cases; other nested observer tests stay undiscovered."""


del _module, _spec, _nested_dir, _nested_file
