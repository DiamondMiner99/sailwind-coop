# Sailwind Co-op

Steam co-op for Sailwind. The host opens a lobby from the pause menu, friends join from a Steam invite, and the
crew sails the host's boat together. Crews of 2 to 8 players are supported.

This is alpha software and can crash or desync. Back up your saves before playing.

## Features

- One shared boat. The host runs the boat and checks every crewmate's actions, and guests connect to the host.
- Every crewmate can steer, trim and push the sails, haul ropes, use the anchor, trade, fish, cook, navigate,
  sleep and run missions.
- Hosting, inviting and the crew list are in the pause menu. Its Friends screen shows the invites you have
  received.
- Join from the title screen or from your own game. You arrive on the host's deck.
- Crewmates are shown as characters from the Sailwind Player Model mod, with their names above them.
- Food, water and sleep are kept for each player. Money and the economy are shared and held by the host.
- The crew can sleep in the boat's bunks or in one tavern room, and wakes once everyone is rested.
- A guest's progress is written to a separate co-op save. A guest's own save slots are not written.
- Kept in step across the crew: items and crates, missions, trading and market prices, fishing, cooking, hull
  damage and repair, weather, waves, time of day, NPC boats and shipyard changes.

## Install

With a mod manager (r2modman, Thunderstore Mod Manager or Gale), install Sailwind Co-op. BepInExPack and Sailwind
Player Model are installed with it. Start the game from the mod manager. Steam has to be running, since invites
and the connection go through Steam.

Every player in the crew installs the mod.

For a manual install, download `SailwindCoop-vX.Y.Z.zip` from the
[GitHub releases](https://github.com/DiamondMiner99/sailwind-coop/releases) and extract the whole zip into the
Sailwind folder, the one with `Sailwind.exe`. That zip includes BepInEx and Sailwind Player Model.

On Linux and Steam Deck, set Sailwind's launch options to:

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

### pander33's SailwindCoop

pander33's SailwindCoop is a different co-op mod with the same name. The two cannot be used together. With both
installed, BepInEx loads pander33's and skips this one, and the only sign is a line in `LogOutput.log`. In a manual
install both go in `BepInEx/plugins/SailwindCoop/`, so installing one overwrites the other.

## Playing

1. Everyone starts Sailwind. The host loads their world and boards their boat.
2. The host presses Esc, then Host Co-op, then Invite Friend, and picks a friend from the list.
3. The friend accepts the Steam invite, from the title screen or from their own game.

To end a session, the host closes the lobby from the pause menu or quits. A guest leaves by quitting the game.
When the host ends a session, each guest's game shows a message and closes, so a guest is never left playing in
the host's world.

The crew limit is `MaxPlayers` in `BepInEx/config/com.sailwindcoop.mod.cfg`, 8 by default.

## Other mods

These have to be installed at the same version on every machine, or on none. A crew that differs is refused at
join, with a message naming the mod:

- Shipyard Expansion (custom rigs are shared with the crew, including live edits at a shipyard)
- HMS Leopard (the cutter, rowing, the bell and the gunports)
- Sail Collision Fix (its three options have to match too)
- NAND Tweaks (only its six options that change the simulation have to match)
- Deep Ports
- Towable Boats (its "Small boats can tow" option has to match too)
- Postal Expansion

These work in co-op and are not checked at join:

- Sail Damage: only the host's sails tear, and the whole crew sees the same damage and patches.
- MusicMod and Viking Horn: crewmates hear each other play and blow the horn.
- HooksHangMore: items and anchors on its hooks stay hung for the whole crew.
- FurstMate: the host's dog, cat and booby are the crew's pets, and any crewmate can pet, feed and command them.

## Bug reports

Logs help a lot with a report. Copy them before you start the game again, because they are replaced on the next
launch:

- `BepInEx/LogOutput.log`
- `BepInEx/SailwindCoop-verbose-*.log`, written only while verbose logging is on. F8 turns it on.
- `%USERPROFILE%\AppData\LocalLow\Raw Lion Workshop\Sailwind\Player-prev.log`

With a mod manager, the BepInEx folder is inside the manager's profile folder, not the Sailwind folder. In r2modman
it is under Settings, Browse profile folder.

Report issues on [GitHub](https://github.com/DiamondMiner99/sailwind-coop/issues). Logs from the host and from the
crewmate who had the problem are the most useful.

## Credits

Built on Pillumz's [sailwind-coop](https://github.com/Pillumz/sailwind-coop) (MIT), which made the Steam
connection and the state sync this mod is built on. Sailwind is made by Raw Lion Workshop. This mod is not
affiliated with them.

MIT license.
