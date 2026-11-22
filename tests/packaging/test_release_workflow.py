import os
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


class ReleaseWorkflowTests(unittest.TestCase):
    def run_publish_step(self, exists):
        workflow = (ROOT / ".github/workflows/release.yml").read_text()
        # Exercise the actual workflow shell block with a fake gh; no GitHub calls.
        step = workflow.split("      - name: Publish release assets\n", 1)[1]
        script = step.split("        run: |\n", 1)[1]
        script = "\n".join(line[10:] for line in script.splitlines())
        with tempfile.TemporaryDirectory() as directory:
            directory = Path(directory)
            (directory / "artifacts").mkdir()
            for name in ["plugin.zip", "manifest.json", "NOTICE"]:
                (directory / "artifacts" / name).write_text("test artifact")
            fake_gh = directory / "gh"
            fake_gh.write_text(
                "#!/bin/sh\n"
                'printf "%s\\n" "$*" >> "$GH_CALL_LOG"\n'
                'if [ "$2" = "view" ]; then exit "$RELEASE_VIEW_STATUS"; fi\n'
            )
            fake_gh.chmod(0o755)
            log = directory / "calls"
            environment = dict(os.environ, PATH=str(directory) + os.pathsep + os.environ["PATH"],
                               RELEASE_TAG="v1.0.0", RELEASE_VIEW_STATUS="0" if exists else "1",
                               GH_CALL_LOG=str(log))
            subprocess.run(["bash", "-e", "-o", "pipefail", "-c", script],
                           cwd=directory, env=environment, check=True)
            return log.read_text().splitlines()

    def test_uploads_to_existing_release_without_changing_notes(self):
        calls = self.run_publish_step(exists=True)
        self.assertEqual(2, len(calls))
        self.assertEqual("release view v1.0.0 --json tagName", calls[0])
        self.assertTrue(calls[1].startswith("release upload v1.0.0 "))
        self.assertIn("artifacts/manifest.json", calls[1])
        self.assertIn("--clobber", calls[1])

    def test_creates_missing_release_with_license_notices(self):
        calls = self.run_publish_step(exists=False)
        self.assertEqual(2, len(calls))
        self.assertTrue(calls[1].startswith("release create v1.0.0 "))
        self.assertIn("--verify-tag", calls[1])
        self.assertIn("--notes-file artifacts/NOTICE", calls[1])
        self.assertIn("--generate-notes", calls[1])

if __name__ == "__main__":
    unittest.main()
