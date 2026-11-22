"""Build Jellyfin's install archive and catalog from the compiled plugin."""

import argparse
import hashlib
import json
import re
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo

ROOT = Path(__file__).resolve().parents[1]
DLL_NAME = "Jellyfin.Plugin.StrmDownload.dll"
PROJECT = ROOT / "src/Jellyfin.Plugin.StrmDownload/Jellyfin.Plugin.StrmDownload.csproj"


def package(repository, dll, output, tag=None):
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Repository must be a GitHub owner/repository pair.")
    project = ET.parse(PROJECT).getroot()
    version = project.findtext("./PropertyGroup/Version")
    assembly_version = project.findtext("./PropertyGroup/AssemblyVersion")
    if not version or not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("Project Version must be a stable three-part version.")
    if assembly_version != version + ".0":
        raise ValueError("AssemblyVersion must match Version with a trailing .0.")
    if tag is not None and tag != "v" + version:
        raise ValueError(f"Release tag must be v{version}.")
    target_abi = project.find(".//PackageReference[@Include='Jellyfin.Controller']").get("Version")
    target_abi = ".".join((target_abi.split(".") + ["0"] * 4)[:4])

    dll_bytes = dll.read_bytes()
    output.mkdir(parents=True, exist_ok=True)
    (output / DLL_NAME).write_bytes(dll_bytes)
    archive = output / f"strm-download_{assembly_version}.zip"
    # Stable metadata keeps the ZIP checksum identical for identical input files.
    with ZipFile(archive, "w", compression=ZIP_DEFLATED) as bundle:
        for name, data in [(DLL_NAME, dll_bytes), ("LICENSE", (ROOT / "LICENSE").read_bytes())]:
            entry = ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            entry.compress_type = ZIP_DEFLATED
            entry.create_system = 3
            entry.external_attr = 0o100644 << 16
            bundle.writestr(entry, data, compresslevel=9)

    catalog = json.loads((ROOT / "manifest.json").read_text())
    catalog[0]["owner"] = repository.split("/")[0]
    catalog[0]["versions"] = [{
        "version": assembly_version,
        "targetAbi": target_abi,
        "sourceUrl": f"https://github.com/{repository}/releases/download/v{version}/{archive.name}",
        # Jellyfin requires MD5 for its catalog's archive checksum.
        "checksum": hashlib.md5(archive.read_bytes(), usedforsecurity=False).hexdigest(),
        "timestamp": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "changelog": "See the GitHub release notes.",
    }]
    manifest = output / "manifest.json"
    manifest.write_text(json.dumps(catalog, indent=2) + "\n")
    hashes = {DLL_NAME: dll_bytes, archive.name: archive.read_bytes(), manifest.name: manifest.read_bytes()}
    (output / "SHA256SUMS").write_text("".join(
        f"{hashlib.sha256(data).hexdigest()}  {name}\n" for name, data in hashes.items()
    ))
    return archive, manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--dll", type=Path, default=ROOT / "artifacts" / DLL_NAME)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts")
    parser.add_argument("--tag", help="Validate a release tag against the project version.")
    args = parser.parse_args()
    try:
        for path in package(args.repository, args.dll, args.output, args.tag):
            print(path)
    except (ValueError, OSError) as error:
        parser.error(str(error))


if __name__ == "__main__":
    main()
