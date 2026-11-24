"""Build the Thunderstore package from a published GitHub release zip.

Usage: python -I packaging/pack_thunderstore.py <SailwindCoop-vX.Y.Z.zip> <out.zip>

Takes the plugin files out of the release zip and puts them at the package root next to the manifest, icon and
store README from packaging/thunderstore/. See packaging/PACKAGING.md.
"""
import json
import pathlib
import re
import sys
import zipfile

HERE = pathlib.Path(__file__).resolve().parent / "thunderstore"

# Release zip path -> package path. Player Model is a separate package, so its DLL is not copied.
PLUGIN_FILES = {
    "BepInEx/plugins/SailwindCoop/SailwindCoop.dll": "SailwindCoop.dll",
    "BepInEx/plugins/SailwindCoop/Facepunch.Steamworks.Win64.dll": "Facepunch.Steamworks.Win64.dll",
    "steam_api64.dll": "steam_api64.dll",
}


def main(release_zip: str, out_zip: str) -> None:
    manifest = json.loads((HERE / "manifest.json").read_text(encoding="utf-8"))
    match = re.search(r"v(\d+\.\d+\.\d+)\.zip$", release_zip)
    if not match or match.group(1) != manifest["version_number"]:
        sys.exit(f"manifest.json is {manifest['version_number']} but the release zip is {release_zip}")
    if len(manifest["description"]) > 250:
        sys.exit("manifest description is over 250 characters")

    with zipfile.ZipFile(release_zip) as src, zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED) as dst:
        for name in ("manifest.json", "icon.png", "README.md"):
            dst.writestr(name, (HERE / name).read_bytes())
        for src_name, dst_name in PLUGIN_FILES.items():
            dst.writestr(dst_name, src.read(src_name))

    with zipfile.ZipFile(out_zip) as check:
        for info in check.infolist():
            if "\\" in info.filename or info.flag_bits & 0x08:
                sys.exit(f"bad entry {info.filename!r}")
            print(f"{info.filename}  {info.file_size}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2])
