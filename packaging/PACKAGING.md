# Packaging for Thunderstore and Nexus

`thunderstore/` holds the package sources: `manifest.json`, `icon.png` (256x256) and `README.md`, which is the
listing page players see. These notes are for us and are not shipped.

## Thunderstore

Community: https://thunderstore.io/c/sailwind/ . Team and namespace: `DiamondMiner99`.

Build the package from the PUBLISHED GitHub release zip, so the store gets the same DLLs as GitHub:

```
gh release download vX.Y.Z -R DiamondMiner99/sailwind-coop -p "SailwindCoop-vX.Y.Z.zip" -D <scratch>
python -I packaging/pack_thunderstore.py <scratch>/SailwindCoop-vX.Y.Z.zip <scratch>/DiamondMiner99-SailwindCoop-X.Y.Z.zip
```

The script checks that `manifest.json`'s version matches the zip name, then writes the package with Python's
`zipfile`: forward slashes, deflate, no data descriptors. Never pack with `Compress-Archive` (backslash paths) or
`tar` (GNU tar in Git Bash writes a tar, bsdtar sets data descriptors).

Package layout, everything at the zip root:

```
manifest.json
icon.png
README.md
SailwindCoop.dll
Facepunch.Steamworks.Win64.dll
steam_api64.dll
```

A mod manager puts loose root files in `BepInEx/plugins/DiamondMiner99-SailwindCoop/`. Only the BepInExPack may
write to the game root, so no package can put `steam_api64.dll` next to `Sailwind.exe`. Since v0.3.0 the mod loads
`steam_api64.dll` by absolute path from beside its own assembly, which is why the package carries a copy. A copy
next to `Sailwind.exe` still wins when there is one.

Never ship `BepInEx/core/*`, `winhttp.dll` or `doorstop_config.ini`. Those belong to the BepInExPack dependency.

Player Model is NOT in the co-op package. It is its own package, `DiamondMiner99-SailwindPlayerModel`, listed in
`dependencies`, and it has to be uploaded first or the co-op upload is rejected for a missing dependency. Its
package sources are in the sailwind-playermodel repo under `packaging/thunderstore/`.

Manifest rules Thunderstore enforces: `name` is `[A-Za-z0-9_]+`, `version_number` is `major.minor.patch` and
matches `Plugin.PluginVersion`, `description` is at most 250 characters, `dependencies` are
`Namespace-Name-Version`. The Sailwind community's BepInExPack is `BepInEx-BepInExPack-5.4.2305` (2026-11-15).

Under a mod manager, logs go to the profile's BepInEx folder, not `<Sailwind>/BepInEx`.

## Nexus

The Nexus file is the GitHub release zip unchanged. It is a game-root overlay with BepInEx and Player Model in it,
which is what a manual install needs. Whether Sailwind has a Nexus game page was not confirmed on 2026-11-15
(Nexus refused the automated fetch).
