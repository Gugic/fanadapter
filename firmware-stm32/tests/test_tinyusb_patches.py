"""Offline patch verification and compiled DWC2 receive regression.

Run after PlatformIO installs TinyUSB:
    python3 -m unittest discover -s firmware-stm32/tests -v
Optional: TINYUSB_DIR selects another installed checkout; HOST_CC/HOST_CFLAGS select the host compiler.
The original dependency tree is read only; patching and compilation use temporary directories.
"""
import importlib.util
import os
from pathlib import Path
import shlex
import subprocess
import tempfile
import unittest

PROJECT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("tinyusb_patches", PROJECT / "apply_tinyusb_patches.py")
patches = importlib.util.module_from_spec(spec)
spec.loader.exec_module(patches)
DRIVERS = Path("src/portable/synopsys/dwc2")
REPLACEMENTS = {"hcd_dwc2.c": patches.REPLACEMENTS, "dcd_dwc2.c": patches.DCD_REPLACEMENTS}


def pristine(text, filename):
    text = text.replace("\r\n", "\n")
    replacements = REPLACEMENTS[filename]
    if patches.MARKER in text:
        patches.patched_source(text, replacements, filename)  # Reject a damaged starting checkout.
        for old, new, _ in reversed(replacements):
            text = text.replace(new, old)
    patches.patched_source(text, replacements, filename)  # Verify the resulting unpatched fixture.
    return text


class TinyUsbPatchTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        dependency = Path(os.environ.get("TINYUSB_DIR", PROJECT / ".pio/libdeps/weact_h743/TinyUSB"))
        cls.original = {}
        for filename in REPLACEMENTS:
            path = dependency / DRIVERS / filename
            if not path.is_file():
                raise RuntimeError("Install TinyUSB with the firmware PlatformIO build first: " + str(path))
            cls.original[filename] = pristine(path.read_text(encoding="utf-8"), filename)

    def fixture(self, root, sources=None):
        driver_dir = root / DRIVERS
        driver_dir.mkdir(parents=True)
        for filename, text in (sources or self.original).items():
            (driver_dir / filename).write_bytes(text.encode("utf-8"))

    def test_fresh_and_idempotent_for_both_drivers(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.fixture(root)
            patches.apply_patches(root)
            first = {name: (root / DRIVERS / name).read_bytes() for name in REPLACEMENTS}
            patches.apply_patches(root)
            for name, content in first.items():
                self.assertEqual(content, (root / DRIVERS / name).read_bytes())
                self.assertEqual(content.decode(), patches.patched_source(self.original[name], REPLACEMENTS[name], name))

    def test_existing_host_patches_are_preserved(self):
        sources = dict(self.original)
        sources["hcd_dwc2.c"] = patches.patched_source(sources["hcd_dwc2.c"], patches.REPLACEMENTS, "hcd_dwc2.c")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.fixture(root, sources)
            patches.apply_patches(root)
            self.assertEqual(sources["hcd_dwc2.c"], (root / DRIVERS / "hcd_dwc2.c").read_text())

    def test_crlf_fresh_and_already_patched(self):
        for filename, original in self.original.items():
            replacements = REPLACEMENTS[filename]
            expected = patches.patched_source(original, replacements, filename)
            for source in (original, expected):
                with self.subTest(filename=filename, already_patched=source == expected):
                    self.assertEqual(expected, patches.patched_source(source.replace("\n", "\r\n"), replacements, filename))

    def test_drift_partial_and_marker_only_states_fail(self):
        for filename, original in self.original.items():
            replacements = REPLACEMENTS[filename]
            patched = patches.patched_source(original, replacements, filename)
            old, new, _ = replacements[0]
            damaged = {
                "upstream_drift": original.replace(old, old[:len(old) // 2] + "drift" + old[len(old) // 2:], 1),
                "partial_patch": patched.replace(new, "// " + patches.MARKER + "\n", 1),
                "code_changed_with_markers_intact": patched.replace(new, new.replace("\n", " \n", 1), 1),
                "extra_marker": patched + "// " + patches.MARKER + "\n",
            }
            for state, source in damaged.items():
                with self.subTest(filename=filename, state=state), self.assertRaises(patches.PatchError):
                    patches.patched_source(source, replacements, filename)

    def test_invalid_device_driver_leaves_host_driver_unmodified(self):
        sources = dict(self.original)
        sources["dcd_dwc2.c"] = "unexpected upstream contents\n"
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.fixture(root, sources)
            with self.assertRaises(patches.PatchError):
                patches.apply_patches(root)
            for filename, text in sources.items():
                self.assertEqual(text, (root / DRIVERS / filename).read_text())

    def test_actual_receive_handlers_reject_regression(self):
        original = self.original["dcd_dwc2.c"]
        patched = patches.patched_source(original, patches.DCD_REPLACEMENTS, "dcd_dwc2.c")
        compiler = shlex.split(os.environ.get("HOST_CC", "cc"))
        flags = shlex.split(os.environ.get("HOST_CFLAGS", ""))
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name, source in (("unpatched", original), ("patched", patched)):
                # Compile the actual fetched functions, not a separately maintained copy.
                start = source.index("static void handle_rxflvl_irq(")
                end = source.index("\nstatic void handle_epin_slave(", start)
                (root / "dwc2_receive_handlers.inc").write_text(source[start:end], encoding="utf-8")
                executable = root / name
                subprocess.run(compiler + ["-std=c11", "-Wall", "-Wextra", "-Werror"] + flags +
                               ["-I", str(root), "-I", str(PROJECT / "src"),
                                str(PROJECT / "tests/dwc2_receive_test.c"), str(PROJECT / "src/json_min.c"),
                                "-o", str(executable)],
                               check=True, capture_output=True, text=True)
                result = subprocess.run([str(executable)], capture_output=True, text=True, check=False)
                if name == "unpatched":
                    self.assertEqual(result.returncode, 1, result.stderr)
                    self.assertIn("OUT packet of 0 bytes incorrectly reported 64 bytes", result.stderr)
                else:
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertIn("fragmented and EP0 packets passed", result.stdout)


if __name__ == "__main__":
    unittest.main()
