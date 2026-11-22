import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from zipfile import ZipFile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("release_package", ROOT / "scripts/package.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class PackageTests(unittest.TestCase):
    def test_archive_catalog_and_checksums_match(self):
        with tempfile.TemporaryDirectory() as directory:
            directory = Path(directory)
            dll = directory / "input.dll"
            dll.write_bytes(b"test assembly bytes")
            output = directory / "artifacts"
            archive, manifest = release.package("example/plugin", dll, output)
            archive_bytes = archive.read_bytes()
            with ZipFile(archive) as bundle:
                self.assertEqual([release.DLL_NAME, "LICENSE"], bundle.namelist())
                self.assertEqual(dll.read_bytes(), bundle.read(release.DLL_NAME))
                self.assertIsNone(bundle.testzip())
            catalog = json.loads(manifest.read_text())
            version = catalog[0]["versions"][0]
            self.assertEqual("example", catalog[0]["owner"])
            self.assertEqual("12.0.0.0", version["targetAbi"])
            self.assertTrue(version["sourceUrl"].startswith("https://github.com/example/plugin/releases/download/v"))
            self.assertTrue(version["sourceUrl"].endswith("/" + archive.name))
            self.assertEqual(hashlib.md5(archive_bytes, usedforsecurity=False).hexdigest(), version["checksum"])
            for line in (output / "SHA256SUMS").read_text().splitlines():
                checksum, name = line.split("  ")
                self.assertEqual(hashlib.sha256((output / name).read_bytes()).hexdigest(), checksum)
            release.package("example/plugin", dll, output)
            self.assertEqual(archive_bytes, archive.read_bytes())

    def test_rejects_wrong_tag_before_writing_outputs(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "artifacts"
            with self.assertRaisesRegex(ValueError, "Release tag"):
                release.package("example/plugin", Path("missing.dll"), output, "v999.0.0")
            self.assertFalse(output.exists())

    def test_rejects_invalid_repository(self):
        for repository in ["example", "https://github.com/example/plugin", "example/plugin/extra", "example/plugin;command"]:
            with self.subTest(repository=repository), self.assertRaises(ValueError):
                release.package(repository, Path("missing.dll"), Path("unused"))


if __name__ == "__main__":
    unittest.main()
