# Packaging Sailwind Co-op for Thunderstore

This folder holds what a Thunderstore package needs and nothing else. The GitHub release zip stays
what it has always been: a game-root overlay you extract over your Sailwind folder. The two layouts
are genuinely different and one cannot be renamed into the other.

## Why a hand-repacked GitHub zip does not work

The GitHub zip puts four files at the game root, and one of them is load-bearing:

```
steam_api64.dll        <- next to Sailwind.exe
winhttp.dll            <- BepInEx's loader
doorstop_config.ini
INSTALL.txt
BepInEx/core/*         <- BepInEx itself
BepInEx/plugins/SailwindCoop/SailwindCoop.dll
BepInEx/plugins/SailwindCoop/Facepunch.Steamworks.Win64.dll
```

A mod manager flattens a package into `<profile>/BepInEx/plugins/<Author>-<Package>/` and strips the
package's own `BepInEx/plugins/` prefix. Only the BepInEx pack is allowed to write to the profile
root. **So no Thunderstore package can put a file next to `Sailwind.exe`.**

That mattered because `Facepunch.Steamworks.Win64.dll` is a *managed* assembly, which BepInEx
resolves from any subfolder of the plugin path, while `steam_api64.dll` is a *native* library that
Windows resolves for itself, from the executable's folder, System32, the working directory and PATH.
The plugins folder is on none of those lists. Every Steam call then failed with a
`DllNotFoundException`, which the mod reported as a generic Steam failure, which is why the symptom
players described was "co-op only works if I launch through Steam" - they were really falling back to
a separate game-root install that had the file.

As of v0.3.0 the mod loads `steam_api64.dll` by absolute path from beside its own assembly, so it
works from a plugins folder. A copy next to `Sailwind.exe` still wins if one is there, so ordinary
installs are unchanged.

## Package layout

`manifest.json`, `icon.png` and `README.md` must sit at the **zip root**, not inside a folder.

```
manifest.json
icon.png                        <- exactly 256x256, present in this folder
README.md
BepInEx/plugins/SailwindCoop/SailwindCoop.dll
BepInEx/plugins/SailwindCoop/Facepunch.Steamworks.Win64.dll
BepInEx/plugins/SailwindCoop/steam_api64.dll
```

Do **not** ship `BepInEx/core/*`, `winhttp.dll` or `doorstop_config.ini`. Those belong to the
BepInExPack dependency and the manager owns them; shipping our own copies means two BepInEx installs
fighting over the same process.

`manifest.json` rules, all of which Thunderstore enforces on upload:

- `name` must match `[A-Za-z0-9_]+`, so `SailwindCoop` and never `Sailwind Co-op`.
- `version_number` must be strict `major.minor.patch`. Keep it in step with `Plugin.PluginVersion`.
- `dependencies` are `Namespace-Name-Version` strings.

## Building the zip

Pack with `tar.exe`, never `Compress-Archive` - the latter writes backslash path separators, which
Linux and Proton unzip into files literally named `BepInEx\plugins\...`.

## Known rough edge

Under a mod manager the mod's logs go to the **profile's** BepInEx folder, not `<Sailwind>/BepInEx`.
Anyone collecting logs for a bug report needs to be pointed at the profile folder instead.
