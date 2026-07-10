# ⛵ Sailwind Co-op

**Crew a single ship with your friends in [Sailwind](https://store.steampowered.com/app/1764530/Sailwind/).**

One hull, many hands: somebody on the helm, somebody trimming the sails, somebody hauling
ropes and dropping anchor, somebody below deck cooking or fishing while the navigator plots
the next leg. The host opens a lobby, friends join from a Steam invite, and everyone shares
the same boat, the same world, and the same voyage.

[![version](https://img.shields.io/badge/version-v0.2.26-blue)](../../releases)
[![game](https://img.shields.io/badge/Sailwind-v0.38-1f6feb)](https://store.steampowered.com/app/1764530/Sailwind/)
[![status](https://img.shields.io/badge/status-alpha-orange)](KNOWN-ISSUES.md)
[![license](https://img.shields.io/badge/license-MIT-green)](LICENSE)

> ### ⚠️ This is an early alpha. Back up your saves.
> The mod is under active development and has been played in real 2-5 player sessions, but
> it can still crash, freeze, or desync. Always copy your save folder before a session.
> See **[KNOWN-ISSUES.md](KNOWN-ISSUES.md)** for the honest current state.
>
> **Every crew member must run the exact same mod version.** The network format changes
> between releases, so mismatched builds will fail or desync.

---

## What this fork adds

This is a **fork** of [`Pillumz/sailwind-coop`](https://github.com/Pillumz/sailwind-coop) by **Pillumz**,
the MIT-licensed mod that built the Steam-P2P co-op networking and the full state-sync
foundation that makes a shared boat work. On top of that foundation, this fork adds:

- **A full crew, not just two.** Host plus guests share one boat: crews of **2-8** players
  are supported. Star topology: guests connect to the host, who validates and relays so
  everyone sees everyone.
- **Per-crewmate everything.** What was single-guest state is now per-player: multiple bodies,
  two people pulling different ropes at once, per-carrier held items, one-controller helm
  arbitration, and a targeted resync on each join.
- **A menu-driven UI:** Host / Invite / Join / Close Lobby in the pause menu, plus a live
  crew roster. No hotkeys to memorize.
- **Visible humanoid bodies:** crewmates render as in-game people with name tags (not capsules),
  and you see your own body in third person.
- **Independent survival:** food / water / sleep are **per-player**, with crew sleep (shared
  bunk or one tavern room) that auto-wakes the crew once everyone is rested.
- **Join from the title screen** plus **guest save-safety** (a guest never writes the host's world
  into their own slot) and warn-and-quit so a guest is never stranded.
- **Broad state sync:** items, crates, missions, trading, the economy, fishing, cooking, hull
  damage & repair, weather, ocean waves, time of day, sleep, NPC boats, and shipyard
  customization all stay in step across the crew.

See **[CHANGELOG.md](CHANGELOG.md)** for the full version history.

## Features

- **Crew co-op** over Steam friend invites: no dedicated servers, no port forwarding.
- **One shared boat:** everyone sails the same vessel; the host runs the physics and validates actions (host-authoritative, star topology).
- **Everyone actually crews:** guests can steer, push the boat & sails, haul ropes, drop anchor, trade, fish, cook, navigate, sleep, and run missions. Not spectators.
- **Menu-driven:** Host / Invite / Join / Close Lobby live right in the pause menu, with a live crew roster.
- **Join from the title screen or mid-game:** accept an invite and you're teleported straight onto the host's deck.
- **See your crew:** each player is an in-game humanoid with a name tag (and you see your own body in third person).
- **Independent survival, shared wallet:** food / water / sleep are per-player; money and the world economy are shared and **host-authoritative** (purchases are validated against the host's wallet).
- **Crew sleep:** bunk down together or rent one shared tavern room; the crew auto-wakes once *everyone* is rested.
- **Live menus:** when any crewmate trades or takes a mission, everyone's open trade/mission screen refreshes instead of going stale.
- **Save-safe for guests:** co-op progress is written to a separate co-op session save; a guest's own solo saves are never touched.

## Install

Every player - host and guests - installs the mod, and **everyone needs the same version**.
Steam must be running (the mod uses Steam friend invites and Steam P2P networking).

### Quick install (recommended)

1. Download the latest **`SailwindCoop-vX.Y.Z.zip`** from the [Releases](../../releases) page.
2. Find your Sailwind folder: in Steam, right-click **Sailwind → Manage → Browse local files**.
3. **Extract the zip straight into that folder** (the one with `Sailwind.exe`), choosing **overwrite** if asked.
   You should now see `winhttp.dll` and a `BepInEx` folder sitting next to `Sailwind.exe`.
4. Launch the game. That's it. The bundle includes the BepInEx mod loader, so there's nothing else to install.

**Linux / Steam Deck:** set Sailwind's launch options (right-click Sailwind → Properties → Launch Options) to:
```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

### Manual install (if you already run other BepInEx mods)

Don't extract the bundle's `BepInEx` folder over your existing one. Just take the plugin:

1. Make sure you have [BepInEx 5.4.x (x64)](https://github.com/BepInEx/BepInEx/releases) installed and run once.
2. From the zip, copy the **`BepInEx/plugins/SailwindCoop/`** folder (it holds `SailwindCoop.dll` and
   `Facepunch.Steamworks.Win64.dll`) into your own `Sailwind/BepInEx/plugins/`.

### Updating

**Re-extract and overwrite the old files, on every machine.** The network format changes
between releases, so a stale build on one machine will break the session for everyone.

## How to play

### Start a session

1. Everyone launches Sailwind. The **host** loads their world and boards their boat.
2. **Host:** press `Esc` → **Host Co-op** to open a lobby, then **Invite Friend** and pick someone.
   Keep inviting to fill the crew (up to the configured cap).
3. **Crew:** accept the Steam invite. You can be at the title screen (your latest save auto-loads)
   or already in your own game; either way you're teleported onto the host's deck.

### End a session

Order matters a little, but the mod is built so nobody gets stranded:

- **Host:** `Esc` → **Close Lobby** (or just quit). The host's game keeps running and saves normally.
- **Crew:** `Esc` → **Leave Lobby** or **Quit Game**. A guest is returned to a warning and the game
  closes, by design, so a guest never lingers on the host's save. **Your own solo save is left untouched.**

### Controls

It's all in the pause menu; there are no co-op hotkeys to remember.

| Key | What it does |
|-----|--------------|
| `Esc` | Open the co-op pause menu (Host / Invite / Join / Close or Leave Lobby / Recover Boat / Settings) |
| `F8` | Toggle the mod's verbose logging + a small corner indicator (see [Troubleshooting](#troubleshooting)) |
| `Shift`+`F8` | Toggle the full on-screen debug overlay (while logging is on) |

### Crew size

Crews of **2 to 8 players** (host + up to 7 guests) are supported; 8 is the bundled default.
Tune it with the `MaxPlayers` setting in `BepInEx/config/com.sailwindcoop.mod.cfg`
after running the game once.

```ini
[Coop]
## Maximum crew on one shared boat, including the host. 8 (host + 7) is the recommended default.
MaxPlayers = 8
```

## Troubleshooting & bug reports

**Capturing logs for a bug report (please do this):**
- The standard BepInEx log is `BepInEx/LogOutput.log` (always written).
- The mod's rich per-subsystem log is `BepInEx/SailwindCoop-verbose-<timestamp>.log` (one timestamped
  file per session, most recent ~10 kept), but it's **only written while debug mode is on**. Press
  **`F8`** at the start of a session on **every** machine to enable it.
  When reporting an issue, grab both files from **both** the host and the affected crewmate.

**A crewmate can't see the host / connection issues:**
- Make sure everyone is on the **exact same mod build** (re-extract the zip, overwriting).
- Check Steam shows you online (not invisible/away). Have the host re-open the lobby.

**Game crashes on startup:**
- Confirm the loader installed: `winhttp.dll` should be next to `Sailwind.exe`.
- Check `BepInEx/LogOutput.log` for the error.

**Something desynced / looks wrong mid-voyage:**
- See **[KNOWN-ISSUES.md](KNOWN-ISSUES.md)**. It lists the current limitations, so you can tell a
  known rough edge from a fresh bug. For structured testing, see
  **[PLAYTEST-GUIDE.md](PLAYTEST-GUIDE.md)**.

## Known limitations

- **One shared boat.** The crew sails the host's boat together; multiple crewed boats in one
  session are not supported yet.
- **Shared, host-authoritative wallet.** Money is one crew pool owned by the host; purchases
  by guests are validated against it.
- Everyone must run the **same mod version and the same Sailwind version** (targets v0.38).
- Full list: **[KNOWN-ISSUES.md](KNOWN-ISSUES.md)**.

## Building from source

<details>
<summary>For developers</summary>

### Requirements

- .NET SDK (for `dotnet build`)
- [BepInEx 5.4](https://github.com/BepInEx/BepInEx/releases) installed in your Sailwind folder
- [Facepunch.Steamworks](https://github.com/Facepunch/Facepunch.Steamworks) extracted to `src/SailwindCoop/libs/`

### Build

```bash
dotnet build src/SailwindCoop/SailwindCoop.csproj -c Release \
  -p:GameDir="C:/Program Files (x86)/Steam/steamapps/common/Sailwind"
```

Point `GameDir` at your Sailwind install. Output: `src/SailwindCoop/bin/Release/net472/SailwindCoop.dll`.

On Linux, `src/build.fish` wraps the same build (set `SAILWIND_GAME_DIR` to your install);
copy `src/deploy.fish.example` to `src/deploy.fish` for a local deploy helper.

### Project structure

```
src/SailwindCoop/
  Plugin.cs        # entry point + lifecycle
  Networking/      # Steam P2P, packet definitions/serialization, lobby/crew cap
  Sync/            # state-sync managers (boat, sleep, survival, trading, push, ...)
  Patches/         # Harmony patches for game hooks
  Player/          # remote + local player bodies
  UI/              # co-op menus + debug overlay
  Debug/           # verbose logging, profiling, debug mode (F8)
```

</details>

## Credits & license

Standing on MIT-licensed shoulders:

- **[Pillumz](https://github.com/Pillumz)** wrote the original
  [`Pillumz/sailwind-coop`](https://github.com/Pillumz/sailwind-coop) (MIT): the Steam-P2P co-op
  networking and the state-sync foundation a shared boat is built on.
- **This fork** ([DiamondMiner99](https://github.com/DiamondMiner99/sailwind-coop)) rebuilds the
  co-op UX on that foundation (menu-driven lobby, humanoid bodies, independent survival, crew
  sleep, join-from-title), scales it to a full crew (N-player), and runs the ongoing
  stability/hardening campaign on top.
- **[Raw Lion Workshop](https://store.steampowered.com/app/1764530/Sailwind/)** made Sailwind
  itself. Buy the game; it's wonderful.
- Thanks to our playtesters for braving the alpha seas.

All mod code MIT licensed; see [LICENSE](LICENSE). Not affiliated with or endorsed by the developers of Sailwind.
