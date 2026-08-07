# Sailwind Co-op - Feature Wishlist & Brainstorm

> **Status:** Ideation / wishlist only. Nothing here is committed to a roadmap.
> **Generated:** 2026-06-25
> **Source:** Divergent brainstorm - 15 lenses generated 199 raw ideas, curated into themes, effort/impact tagged. Grounded against the current mod's host-authoritative one-shared-ship architecture (floating-origin synced; already syncs economy/trade, missions, damage, embark/FOM, fishing/cooking, sleep-quorum, shipyard, rope/mooring/anchor, control).

Effort/impact tags: `[effort·impact]` where effort ∈ S/M/L/XL and impact ∈ low/med/high. Vibe conveyed by grouping (practical → ambitious → ridiculous).

## Two organizing principles that fell out of this

1. **Almost everything hangs off one missing piece of infra: a look-and-point world ping.** Build that first and a dozen other features get cheaper.
2. **Design throughline - "the ship is the commons, the belly is private":** shared hold / purse / provisions / chart / morale, but each player keeps their own hunger / thirst / sickness. Most of the strongest ideas respect this split.

---

## ⭐ The standouts (cherry-pick these first)

1. **Captain's Ping (look-and-point marker)** `[M·high]` - Look at any rope/hazard/point and drop a named, color-coded world marker everyone sees ("Raise this", "ROCKS two points off the bow"). *Single most-converged idea across all 15 lenses.* Solves the core "nobody can see your cursor on a moving deck" problem; reuses already-synced look-rotation. Half the other features hang off it.
2. **Leak Triage / Bilge Brigade** `[L·high]` - A breach opens discrete leak points; one crewmate dives to find-and-patch while others pump and bucket-bail faster than it floods, boat listing as water climbs. The cleanest "two hands aren't enough" moment - pump-aggregate + waterLevel + oakum already synced (Phase 5).
3. **The Ship's Log (auto-written journal)** `[M·high]` - Persistent dated entries auto-populated from events already synced (embark, damage, fishing, trade, sleep): "Day 47, weathered a storm off Al Ankh, Rusty patched the forward leak." Reuses the parchment-scroll UI. Warm backbone of crew identity.
4. **The Ship's Ledger / Action Audit Log** `[S·high]` - A scrolling "who did what, when": *"Rusty sold 12 cotton for 340g", "Anna cut the stern mooring."* Near-free - SyncManagers already see every action with the originating player id. Unlocks accountability, anti-grief recovery, AND the traitor party modes.
5. **All-Hands Storm Bell + reefing checklist** `[S·high]` - When weather crosses the storm threshold, the bell auto-rings and everyone gets an "ALL HANDS" banner with a shared checklist that ticks off as the crew does each task. Highest impact-per-line-of-code - reads entirely off existing weather + sail sync.
6. **Captain's Hat + Permission Tiers** `[M/L·high]` - One stealable wearable = Captain (only one who can sell/scrap the ship or accept region-changing missions); host assigns ranks gating only the *irreversible* actions. Trust foundation that lets a Discord rando crew without one click ruining the voyage. Gate nothing about basic sailing.
7. **Shared Chart: Pins & Annotations** `[M·high]` - Drop labeled pins and pencil lines on the nav table that sync to every chart ("steer for THIS pin", "shoal here"). Lowest-risk way to make the no-GPS navigator a real role - `ChartData.lines` already serializes and syncs.
8. **Per-Player Difficulty Asymmetry** `[M·high]` - Host sets per-crewmate assist sliders (wider rope-tension bands, gentler sextant fixes, slower thirst) so a 100-hour captain and a first-timer both meaningfully crew the *same* realistic-sim boat. The thing that keeps the new friend coming back.

---

## 🟢 Quick wins (cheap, mostly reuse what exists)

- **Ship's Bell Strike Codes** `[S·high]` - bell becomes interactable; pattern codes (1=all hands, 2=man overboard, 3=hazard). One-byte packet on the notification path.
- **Bosun's Whistle Pipe-Calls** `[S·high]` - radial of traditional trills (Hoist/Avast/Haul/Belay) + caption banner. A command voice for non-captains not on Discord.
- **Race Buoy & Stopwatch** `[S·high]` - drop numbered buoys, host-timed course + leaderboard. Seeds every other race idea.
- **Fishing Contest Scoreboard** `[S·med]` - call a timed derby; fishing's already synced, so it's a host tally + roster panel. Turns becalmed downtime into a fish-off.
- **Summon-to-Ship for Stragglers** `[S·high]` - roster button to haul aboard the friend left on the dock / overboard. `TeleportPlayer` already exists.
- **Lobby Muster / Readiness Check** `[S·med]` - "Ready?" ping; roster fills in ticks so you know nobody's mid-trade before weighing anchor.
- **Colorblind-Safe Role Colors + Badge** `[S·med]` - one persistent per-player color + shape used across pings, rope highlights, nametags, roster. Do this *early* - force-multiplies half the other ideas.
- **Crew Stations HUD ("Who's at the Helm?")** `[M·high]` - glanceable strip of who's at HELM / a named sail / BILGE / ANCHOR, inferred from input streams ControlSync already tracks.
- **Returning-Crew Cold Open** `[S·med]` - templated recap on reload: "Day 47. The *Sea Wretch* lies at anchor off Al Ankh, hold half-full of spice, a slow leak forward." Pure polish once the log exists.
- **Latency/Desync "Rigging Tell"** `[S·med]` - green/amber/red dot per roster name from the watchdog's existing `LastRemotePacketTime`; know if "they're clear of the boom" is real or 300ms stale.

---

## 🧭 Full set, by theme

### Command, Authority & Crew Politics
- **Captain's Hat (single authority token)** `[M·high]` - wearable = Captain; only the wearer can sell/scrap or accept region-changing missions. Hand it over, or vote it off an AFK captain. Backed by a single host `captainPeerId`.
- **Mutiny & Vote-to-Depose** `[L·med]` - majority vote transfers the hat off a reckless/AFK captain (reuse sleep-quorum machinery; transfer the *role*, not the network host, for v1).
- **Captain's Bell / Emergency All-Stop** `[S·med]` - timed host-side veto of everyone's helm input for the reef-approaching control fight (never disable charController - R4.15 lesson).
- **Station Permission Tiers (Captain/Officer/Deckhand/Guest)** `[L·high]` - host assigns ranks; ranks unlock action categories; gated packets host-rejected with a notification. Default everyone to Officer to stay frictionless.
- **Crew Rank Ladder** `[M·med]` - earned Deckhand→Captain progression granting real mod permissions + quorum vote weight; host keeps an override.

### Stations, Roles & Division of Labor
- **Crew Stations HUD** `[M·high]` - who's at which station, from input streams ControlSync already knows + a "need help here" flag.
- **Station Claim & Soft-Lock** `[M·high]` - claim a station; grabbing a claimed control triggers a "take over from \<name\>?" confirm instead of a silent fight. Owner-id per control, auto-releases on disconnect.
- **Role Cards / Watch Bill** `[M·high]` - narrow roles (Helmsman, Bosun, Navigator, Cook, Lookout, Bilge Rat) with *tiny* perks (cook's meals buff slightly, navigator's sextant error tighter). Keep perks small - it's a sim.
- **Watch Rotation & the Ship's Bell** `[M·med]` - shared watch schedule tied to the ship clock; off-watch sleep via existing quorum.
- **Helm/Heading Auto-Trim Assist** `[M·med]` - helmsman locks a heading; gentle rudder nudges hold it so a 3-person crew can sail an 8-person ship. Assistance, not god-mode (no wind/current compensation).
- **Watch Fatigue & Relieve-the-Watch** `[M·med]` - long stints cause gentle heading drift + a "relieve the watch" prompt. Keep mild and optional.

### Signalling & Communication on a Shared Deck
- **Captain's Order Wheel** `[M·high]` - radial of standing orders ("Stand by to come about") posting a *persistent* HUD banner until acknowledged. Intent, not automation.
- **Crew Emote / Callout Wheel** `[M·high]` - sailing callouts + arm poses ("Ready to come about!", "Taking on water!") for the mic-shy.
- **Pinned Crew Notes / Standing-Orders Slate** `[M·med]` - chalk slate by the helm, persists in save, new joiners read it to catch up.
- **Signal Flag Hoist & Lantern Codes** `[L·med]` - diegetic no-voice channel; real magic is fleet play (another crew reads your hoist via ghost-presence).
- **Proximity & Speaking-Tube Spatial Voice** `[XL·high]` *(ambitious)* - positional VOIP muffled through the hull; brass speaking-tubes route voice deck↔hold. Occlusion is the magic Discord can't have. Ship preset-phrase speaking-tubes first as the cheap MVP.

### Many Hands: Multi-Person Tasks & Emergencies *(heart of one-ship co-op)*
- **Two-Hand Capstan & Tail-and-Belay** `[M·high]` - load scales with wind, so it's solo in calm, crew-only in a gale. The one mechanic purely better with many hands.
- **All-Hands Storm Bell + reefing checklist** `[S·high]` - auto-rings at storm threshold; shared checklist ticks off as crew act.
- **All Hands, Tack the Ship (count-in maneuver)** `[L·high]` - helm calls a 3-2-1; crew get sequenced prompts at their sheets/braces; botch the timing and you're caught in irons.
- **Broken Rigging Repair Aloft** `[M·med]` - someone climbs out exposed while the deck crew keeps the motion gentle so they aren't flung off. Trust exercise.
- **Run Aground & Kedge Off** `[L·med]` - row the spare anchor out by tender, all hands grind the capstan to haul her free on a rising swell.
- **Fother the Sail (emergency hull plug)** `[L·med]` *(ambitious)* - two crew haul a spare sail over the bow until water pressure sucks it over the hole, dropping that leak's flow to buy the bilge crew time.
- **Galley Fire & the Bucket Line** `[L·med]` *(ridiculous)* - the stove can start a fire that spreads to rigging; crew fight it with a bucket-line. Gate behind config, keep rare and recoverable.

### The No-GPS Navigator as a Real Crew Role
- **Shared Chart: Pins, Annotations & Map Pings** `[M·high]` - `ChartData.lines` already syncs; points/labels are the add. Mirror the existing MapLinePacket path.
- **Two-Hands-on-the-Sextant Fix** `[L·high]` *(ambitious)* - one holds the instrument steady, one calls "MARK" at the chronometer; fix only valid if MARK lands while steady. QuadrantInspect + ChronometerTime + CompassLatitude already sync individually.
- **Hazard Soundings & the Lead Line** `[M·high]` - leadsman at the bow calls depth ("by the mark, three!") while the helm threads the channel. Depth = seabed raycast under the bow.
- **Heave the Log (two-person speed)** `[M·med]` - one throws the chip-log + holds the reel, one flips the sandglass and calls "turn... STOP". Navigator uses the result for DR.
- **Dead-Reckoning Pencil Track & Drift Reveal** `[L·high]` *(ambitious)* - auto-extends the DR line; when the next celestial fix disagrees, draws thought-vs-true so you SEE leeway/current. Must intentionally accumulate drift so it's never GPS.
- **Blind Helm / Conn-by-Voice (storm & fog mode)** `[L·med]` *(ambitious)* - masks the helmsman's compass/horizon; they steer purely on the navigator's relayed orders. Literally unplayable solo.
- **Shared Fog-of-Sight Chart Reveal & Voyage Trace** `[L·med]` *(ambitious)* - coastlines ink in only as crew actually sight them; past tracks overlay color-coded by trip.
- **Compass Conspiracy (saboteur's bent needle)** `[M·low]` *(ridiculous)* - opt-in saboteur subtly bends the compass; navigator must catch it via a celestial cross-check. Cheap on existing nav sync.

### Shared Economy, Ownership & the Ship's Ledger
- **The Ship's Ledger / Action Audit Log** `[S·high]` - capped ring buffer + R4.16 scroll UI clone; managers already see every action with the actor id.
- **Personal Purses + Shared Coffer & Auto Profit-Split** `[L·high]` - split policy dices each sale into crew purses (navigator 1.5×, deckhand 1×) with gold-rain on a successful run. Opt-in.
- **Economy Guardrails - Spend/Sell Approval** `[M·med]` - over-threshold or flagged-cargo sales queue a one-line captain approval. Reuses the R4.18/R4.19 trade-intercept path.
- **Crew Wages, Shares & Voyage Dividends** `[L·med]` *(ambitious)* - % stakes in the hull; wages auto-debit per in-game day; voyage-end dividends. Needs a stable per-player key (SteamId).
- **Trading Company Identity, Flag & Insurance** `[S·med]` - name the company, fly a pennant, insure the hold. Pairs with the heraldry designer.
- **Most-Profitable-Voyage Contest & Route Records** `[M·med]` - host tracks net coin delta + per-port log; ranked board + persistent route records rival crews can steal.

### Crew Identity, Persistence & the Logbook
- **The Ship's Log (auto-written voyage journal)** `[M·high]` - populated from already-synced events; reuse the parchment scroll. Length-cap entries (mind oversize-transport, R4.11).
- **Christen & Legendary Ship Status** `[M·high]` - vote a name painted on the transom; over voyages she accrues reputation and harbormasters greet her by name. Reuses sleep-quorum vote + shipyard relay.
- **Crew Reputation with Port Factions** `[L·high]` - pooled crew actions raise/lower standing (cheaper docks, exclusive missions). Gate behind a per-port reputation scalar; don't fork vanilla price tables.
- **Home Port the Crew Builds Up** `[XL·high]` *(ambitious)* - fund incremental upgrades (reserved dock, stocked larder, faster repair berth) from a shared chest over sessions. Stage it; watch save size.
- **Crew Achievements & Milestone Banners** `[M·high]` - shared save-persistent milestones fire a notification + log entry for everyone. Distance needs a position-delta accumulator.
- **Crew Flag & Heraldry Designer** `[L·med]` - simple layered emblem editor; flies on the ensign, optionally prints on the sail. Also how ghost-presence boats recognize each other. Define the format cleanly now for cross-mod payoff.
- **Crew Titles, Charter & Trust Persistence** `[M·med]` - sign the articles on join; persistent per-SteamId record (voyages served, titles, rep, notes) so regulars auto-restore and a banned griefer can't return.
- **Returning-Crew Cold Open (session recap)** `[S·med]` - templated recap on reload once the log + named-ship + persisted state exist.

### Shared Danger & Deep Shipkeeping *(ship is common, belly is private)*
- **Man Overboard alert & rescue** `[M·high]` - crewmate goes off the deck; everyone gets a bearing/distance; crew must come about and recover them on a survival clock. Detection = in-water + off-boat-collider (host already has via FOM/embark). Debounce deliberate swimming.
- **The Ship's Stores: Rationing on a Long Haul** `[L·high]` - shared provisions locker + water cask with a visible level; 6 mouths drain it 6× faster than a solo sailor planned. The purest expression of the design throughline.
- **Shared Mess: Cook Once, Feed the Crew** `[M·med]` - a pot yields multiple bowls; each crewmate eats THEIR bowl; a good recipe stamps a short crew-wide "well-fed" flag. Respects independent needs.
- **Rigging Wear & the Parted Line** `[L·med]` - lines accrue wear; ignored, they PART under load mid-storm. Adds a per-line wear float + part event + re-reeve.
- **Anchor Watch in a Blow (dragging anchor)** `[M·med]` - anchor can DRAG, creeping the ship toward rocks while the crew sleeps. Weaponizes the quorum-sleep system. *Prototype against v0.38's resistance-based anchoring first.*
- **Crew Morale & the Shanty** `[M·med]` - one host morale value rising with shared meals/port arrivals/storm-survival; subtle crew-wide edge; start a shanty at the capstan to nudge it. Keep the effect SUBTLE.
- **Whale & Sea-Monster Encounters** `[L·med]` *(ambitious)* - host-spawned whale fouls the rudder / scrapes the hull; scale up to a tentacle for the ridiculous end. Everyone's on the same deck when she lurches. Gate frequency.
- **Sick Bay: Scurvy, Seasickness & Shared Care** `[M·med]` *(ambitious)* - salt rations sap per-player vitamins into mild debuffs; cure is care (citrus/ginger remedy, or rest while others cover). Trigger is free - vitamins already per-player.
- **Careen & Caulk: Beach Maintenance Day** `[XL·med]` *(ambitious)* - beach the ship at low tide, heel her, whole crew works the exposed hull. Prototype as a dockside "haul out" first.
- **Crew Casualty & Field Care** `[M·med]` *(ambitious)* - a fall or a boom to the head dazes/KOs a crewmate; drag them to a bunk and tend them. Never permadeath.

### Fleets, Towing, Rescue & Ghost Presence *(beyond one ship)*
- **Tow Line & Rescue** `[L·high]` - a mooring SpringJoint whose `connectedBody` is the *other* boat's rigidbody. You can't tow yourself. Clamp break force so a bad tow parts the line.
- **Raft-Up & Boarding Plank** `[L·high]` - gunwale-to-gunwale spring lines + a walkable plank collider bridging two FOM-shifted decks. Lock to low relative speed.
- **Rendezvous & Convoy Station-Keeping** `[M·high]` *(ambitious)* - two crews find each other on open water by celestial nav, no map dot (reuse ghost mod's horizon proxy + DebugOverlay WorldToScreenPoint). Keep diegetic - no radar.
- **Second Hull, Split Crew** `[XL·high]` *(ambitious)* - the keystone: two fully-interactive host-authoritative boats in one session. Rendering solved by NPC boat sync; hard part is a full interactive deck + two helm leases + host CPU of two physics hulls.
- **Ship-to-Ship Cargo Transfer** `[M·med]` - hand-carry crates across the plank/under tow; reuses item + held-item relay. Gate on raft-up.
- **Ghost Wreck & Salvage** `[M·med]` *(ambitious)* - a sunk/offline player leaves a pinned half-submerged wreck other crews can salvage. Kinematic wreck = low physics risk.
- **Interactive Ghost Boarding** `[XL·high]` *(ambitious)* - two separate singleplayer saves raft up; one player's ghost promotes to a real synced deck for the duration, then they part ways. The bridge that turns presence into participation. The two mods share ~60-70% of infra.
- **Lead-and-Pilot Through a Strait** `[M·med]` - pilot boat drops ephemeral marker buoys at safe-water points; the follower steers buoy-to-buoy. Local knowledge becomes a transferable physical thing.

### Onboarding the New Friend (teaching & accessibility)
- **Highlight-a-Rope (line tracing)** `[L·high]` - the whole run of a line lights up end-to-end (sail → blocks → cleat) in the newbie's role color. Captain can highlight different lines for different people = live division of labor. Needs a static per-ship rope→sail→cleat map.
- **Per-Player Difficulty Asymmetry** `[M·high]` - host-set assist sliders per crewmate; host already validates rope-settle/drain/fixes, so it's a multiplier table keyed by player. Opt-in and visible.
- **Veteran Hand-Off Autopilot ("I've got the helm")** `[L·high]` - captain hands a station to a host-side AI assist so a newbie learns one thing at a time. Make it *slightly worse* than a human so it isn't abused; stepping stone toward host migration.
- **Roles-as-Training-Wheels station presets** `[M·high]` - scope a beginner to a survivable slice (Bowman / Bilge Officer / Lookout); other stations dimmed; captain unlocks more as confidence grows.
- **Colorblind-Safe Role Colors + Badge** `[S·med]` - persistent per-player color + redundant shape across pings, ropes, nametags, roster. Do early.
- **Contextual Just-In-Time Coaching Cards** `[M·med]` - short cards pop only on first touch of a system; route the newbie to "ask your captain to ping the bilge pump", not a wiki. Veteran sees none of it.
- **Do-What-The-Captain-Pings Drill Mode** `[M·high]` - captain queues a SEQUENCE of pings; newbie works the checklist one step at a time, each ticking green. Builds on Captain's Ping.
- **The Press-Ganged Parrot Mentor** `[M·low]` *(ridiculous)* - shoulder parrot squawks contextual advice in pirate-speak, flaps toward whatever the captain pings, heckles over-trims. Cosmetic mouthpiece for the ping + coaching triggers. Muteable per player.

### Competition, Games & Downtime
- **Race Buoy & Stopwatch (instant regatta)** `[S·high]` - drop numbered buoys; host-timed course + splits + leaderboard. Seeds every other race idea.
- **Crew Duel: Race the Rigging (split-watch drill)** `[M·high]` - split into port/starboard watches; host issues a rapid sail-handling drill and times each watch. Highest impact-per-effort competitive idea that ships *without* the ghost mod.
- **Fishing Contest Scoreboard** `[S·med]` - timed derby; host tally + roster panel over already-synced fishing.
- **Two-Ship Regatta (ghost-mod handshake)** `[XL·high]` *(ambitious)* - pair two co-op sessions on the same buoy course with a live gap indicator. Needs an IDENTICAL wind seed (wind is the core mechanic, so determinism is the hard part).
- **Handicap Race Rating (fair unequal fights)** `[S·med]` - PHRF-style so a sloop can race a brig fairly. Mostly config + math on the stopwatch.
- **Synchronized Diving Minigame** `[M·low]` *(ridiculous)* - crew dives on a shared countdown for a splash score; underwater formation fishes out sunken cargo or scrubs the hull for a real speed bonus.
- **Ship-as-Instrument Band Mode** `[L·med]` *(ridiculous)* - every object becomes an instrument (halyard-tension twangs by tension!); host mixes inputs into one shanty; a tight synced rhythm earns a trim/haul buff.

### Join Flow, Sessions & Server Ops
- **Summon-to-Ship for Stragglers** `[S·high]` - roster "Summon to deck" reusing `TeleportPlayer`; rescues the friend left on the dock / overboard. Pairs with Man Overboard.
- **Reconnect-Where-You-Left-Off** `[L·high]` - host keeps a dropped guest's seat warm for a grace window; on rejoin via the same SteamId, skip the cold-join churn. Careful with OnDisconnected cleanup (R4.6) and HasStreamed gates so a returning peer doesn't wedge sleep quorum.
- **Friend Invite & Join-In-Progress Polish** `[M·high]` - list online friends hosting a lobby with crew count + region; diegetic swim-up-from-the-wake entrance for mid-voyage joiners. Wake-spawn must account for FOM offset + heading.
- **Host Migration (pass the captaincy)** `[XL·high]` *(ambitious)* - promote a designated guest to host, seeding authority from their last-applied BoatWorldState. The hardest item: authority transfer, save-slot ownership, Steam timing. Start graceful-quit-only. (Converges with the deferred N1 sim-handoff.)
- **Vote-to-Kick, Probation & Undo-the-Sabotage** `[M·high]` - majority kick vote (host instakick); new joiners get a probation window with anchor/mooring/economy gated; captain gets one-click "Counter" on a destructive audit entry that auto-demotes the culprit. Define the "destructive" set conservatively.
- **Lobby Muster / Readiness Check** `[S·med]` - "Ready?" ping; roster fills in ticks. Advisory, no hard gate.
- **Spectator "Crow's Nest" for Loading Joiners** `[M·med]` *(ambitious)* - free-look spectator camera orbiting the host ship while BoatWorldState streams in. Camera-only, safe re: authority. The oversize-transport gap (R4.11) is the real underlying risk.
- **Latency & Desync "Rigging Tell"** `[S·med]` - green/amber/red dot per roster name from the watchdog's `LastRemotePacketTime`; subtle shimmer on a laggy avatar.

---

## 🚀 Moonshots & party modes (deliberately ridiculous bucket)

- **Stowaway Hide-and-Seek / Saboteur's Privateer** `[L·high]` - social deduction; a hidden traitor sabotages with *real* mechanics (loosen a sheet, crack a leak, nudge the compass) and the crew uses the audit log + mutiny + locks to catch them. Can't exist with fewer than four people. Hardest part is per-player fog-of-war on who-did-what.
- **Kraken Boss Raid (All Hands)** `[XL·high]` - tentacle count scales to crew size, leaks out-flow a single pump; the one fight that justifies all 8 hands. Net-new monster AI (vanilla has no combat); reuse the leak/bilge/damage loop. Rare, toggleable.
- **Grog & Drunk-Physics Mode** `[M·med]` - drinking applies sway-cam + sloppy rope-grab to YOUR inputs; only survivable if someone stays sober. No control-theft without consent. Toggle.
- **Message in a Bottle (cross-server)** `[L·med]` - sealed note thrown overboard surfaces days later near a stranger's ship, fished out via existing fishing code. Needs a tiny shared relay; moderation is the risk (cap length, block links).
- **Cross-Ship Radio (Shanty Frequencies)** `[XL·high]` *(ambitious)* - crank-powered wireless; crews on other sessions sharing your frequency hear your shanties/Morse, ghost-rendered as fog-shapes. Ship a Morse/text MVP first.
- **Trebuchet the Cargo (no-dock delivery)** `[L·med]` - three-hands-in-sequence siege engine launches crates onto a distant quay on a wind-corrected arc. Edge case: the crate lands on another player. *That is a feature.*
- **The Captain's Signal Cannon** `[L·med]` - blanks-only ceremonial/distress cannon; misload cracks a deck plank (feeds the leak system) and deafens nearby crew. Cross-ship "heard you" gates on ghost-presence.
- **Pray Against the Weather** `[M·med]` *(ambitious)* - bow shrine; odds scale with how many crew kneel together. Keep capricious so prayer never beats seamanship. Weather/wind already host-authoritative.
- **The Figurehead Awakens** `[M·low]` - grumpy carved spirit roasts the crew by name and brings up their worst wrecks at the worst times. A barker system over already-synced events.
- **Ghost Crew of the Drowned** `[L·med]` *(ambitious)* - record a voyage's station occupancy + helm inputs; replay former crewmates as translucent ghosts working the same ship. Cosmetic-only avoids desync.

---

## 🕳️ Gaps - idea-spaces *no lens covered* (worth a future pass)

1. **Cargo as a physical/spatial puzzle** - where you stow heavy cargo affects ballast/heel/handling; lashing it so it doesn't shift in a storm; a crew loading-plan at the dock. *(Strongest of the gaps - ballast is real Sailwind physics and nobody pitched it.)*
2. **Learning the *world*, not just the ship** - a shared "gazetteer" the veteran annotates for the rookie (trade map, regional prices, hazards).
3. **Authored long-form content** - co-op campaign/questlines, multi-session story missions designed *for* a crew, a procedural "crisis voyage" generator.
4. **Pets & livestock** - chickens/goats for eggs/milk on long hauls, a ship's cat for morale/vermin; a living-cargo loop the crew tends.
5. **Passengers & NPC social texture** - ferrying paying passengers with personalities/demands; harbor characters who react to your *named* crew over time.
6. **Spectacle & shareability** - clip/replay of the storm you survived, a shared postcard/screenshot pipeline, an end-of-voyage highlight reel.
7. **Config UX & version-skew safety** - in-game settings panel for the dozens of toggles, "chill cruise / hardcore / party" profiles, graceful feature-flag negotiation when crew run mismatched mod versions (relevant given the "both ends need R4.9+" wire-change history).
8. **Shore-leave & calendar** - port taverns/gambling/nightlife as a shared destination, festivals, seasonal weather arcs.
9. **Griefing at scale** - vote systems assume good faith; nothing handles vote-brigading, AFK quorum-stalling, or shared property/debt/rep when a crew acrimoniously splits.
10. **Audio-as-a-system, deeper accessibility** - positional ambient soundscape, captions for bell/whistle/cannon cues, motion-sickness comfort on a pitching deck, UI scale for roster/log, a modder API for custom synced stations.

---

## Two closing flags (from looking at the whole pile)

- The **Ledger + Captain's Hat + Permission Tiers** cluster is the highest-leverage *next* investment: it's the prerequisite for safely opening lobbies to non-friends, *and* it doubles as the clue-feed for the traitor party modes.
- The **cargo-stowage/ballast gap (#1)** is the one missing idea that best fits Sailwind's actual physics identity - nobody pitched it, but it's very on-brand.

---

## Added 2026-08-06

### Custom paintings with procedural frames, synced

Player-supplied artwork hung aboard: drop PNGs in a folder, the game picks from them, and each gets a
procedurally built frame sized to the image rather than one fixed frame prefab. Could live in this mod
or ship standalone; the co-op half is the only part that needs this project.

Cheaper than it sounds on the loading side. `UnityEngine.ImageConversionModule` is ALREADY referenced
by this csproj, so `texture.LoadImage(File.ReadAllBytes(path))` needs no new dependency. A procedural
frame is a scaled/mitred border mesh driven by the image's aspect ratio, so one prefab covers every
shape.

**THE CO-OP TRAP, and the reason to design it before writing any of it: never send the image.** A PNG
is orders of magnitude past what belongs on this wire, and the join snapshot is already flagged as at
risk on very item-heavy worlds. So peers exchange a CHOICE, not a picture, and that means every peer
needs the file already. Three options, in ascending honesty:

1. **Sync the filename.** Tiny packet. Breaks silently the moment a crewmate lacks that file, and
   "silently" is the problem - they see a different painting and nobody finds out.
2. **Sync filename plus a content hash.** Same cost, but a peer can now DETECT a mismatch and show a
   clearly-marked placeholder instead of quietly hanging a different picture. Strongly preferred.
3. **Treat the folder as a gated mod-parity item**, like Deep Ports' bundle fingerprint: hash the
   whole set and refuse or warn on mismatch. Heaviest, and probably wrong for something cosmetic.

Option 2 is the right default. Cosmetic divergence should be visible, not silent, and it must never
block a join.

Note "at random from a folder" needs a SEED, not `Random`. Each machine picking independently
guarantees divergence. The host should choose and broadcast the choice, or every peer derives it from
a shared seed plus a stable slot id, so the same wall gets the same picture without a packet per frame.

Worth checking whether NAND Tweaks or another mod already hangs custom art, since that would make this
a compat question rather than a new system.
