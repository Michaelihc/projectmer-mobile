English | [简体中文](README.zh-CN.md)

# ProjectMER-Mobile

[ProjectMER](https://github.com/Michal78900/ProjectMER) 2025.11.2.1 (MapEditorReborn, LabAPI edition) for the Carl Mod
server, a mobile fork of SCP: Secret Laboratory, running on
[LabAPI-Mobile](https://github.com/Michaelihc/labapimobile). It loads ProjectMER maps (`.yml`) and schematics (`.json`,
from the ProjectMER and MapEditorReborn 13.2 exporters) and has ProjectMER's commands and tool gun.

It is server-side only: players use the stock Carl Mod Android client, which is never modified. That client knows
fewer object types than SCP:SL 14 and a phone renders every networked object, so some content is adapted or skipped
([Supported content](#supported-content)).

## Requirements

- A Carl Mod dedicated server with [LabAPI-Mobile](https://github.com/Michaelihc/labapimobile) installed (see its
  README). LabAPI-Mobile provides `LabApi.dll` and Harmony; ProjectMER needs no other files.
- Supported server builds: Carl Mod 0.0.5, the official 0.0.4 server distribution and the 0.0.4 build with the
  deathmatch module. The same `ProjectMER.dll` runs on all three, and all three are tested with LabAPI-Mobile
  1.1.7-mobile.5.
- LabAPI-Mobile version: Carl Mod 0.0.5 needs LabAPI-Mobile **1.1.7-mobile.5 or later**; earlier LabAPI-Mobile releases
  do not run on 0.0.5 at all. On 0.0.4, use 1.1.7-mobile.5 or later as well (the version tested on both 0.0.4 builds).
- Players need the Android client of the server's game version: the 0.0.4 client cannot join a 0.0.5 server. Client-side
  behaviour (what phones render, the tool gun buttons, the frame times below) was verified with the 0.0.4 client on
  0.0.4. On 0.0.5, ProjectMER was verified server-side only, with dummy players: maps and schematics, every command,
  the spawn queue, unloading and round restarts, and the tool gun's server-side requests.

## Install

1. Install LabAPI-Mobile and start the server once, so its data folder
   `<AppData>/SCP Secret Laboratory/LabAPI-Mobile/` exists. `<AppData>` is `%APPDATA%` of the user running the server,
   or the server's `AppData` folder when its `hoster_policy.txt` contains `gamedir_for_configs: true`.
2. Copy `plugins/ProjectMER.dll` from the release archive `ProjectMER-Mobile-<version>.zip` (or your own build) into
   `LabAPI-Mobile/plugins/global/` (or `plugins/<port>/` for one port).
3. Start the server. The log shows `ProjectMER ... enabled`, and the plugin creates:
   - `LabAPI-Mobile/configs/ProjectMER/Maps/` for maps (`<name>.yml`);
   - `LabAPI-Mobile/configs/ProjectMER/Schematics/` for schematics, one folder each: `Schematics/<name>/<name>.json`,
     plus `<name>-Rigidbodies.json` and animator bundles next to it. A bare `Schematics/<name>.json` is moved into its
     folder the first time it is used;
   - `LabAPI-Mobile/configs/<port>/ProjectMER/config.yml`, the configuration.
4. Grant permissions in `LabAPI-Mobile/configs/permissions.yml`: `mpr.<command>` per command (for example `mpr.load`),
   or `mpr.*`.

## Commands

Remote Admin command `mapeditor` (aliases `mer`, `mp`). Run `mp` alone for the list. In the server console, type it
with a leading slash (`/mp list`).

| Command | Aliases | Use |
| --- | --- | --- |
| `load <map>` | `l` | Loads a map. Loading a loaded map reloads it. |
| `unload [map]` | `unl` | Unloads a map, or every map. |
| `save <map>` | `s` | Saves the loaded map, including objects created since (the `Untitled` map). A map file that exists but cannot be read is left untouched and the save fails. |
| `list` | `li`, `ls` | Lists maps and schematics (numbered, for `mp tg schematic`). |
| `merge <new> <map> <map>...` | | Merges maps into a new one. |
| `create <type\|schematic> [x y z]` | `cr`, `spawn` | Creates an object where you look, or at a position. |
| `delete [id]`, `select [id]` | `del`; `sel` | Deletes or selects the object you look at, or by id. |
| `position`, `rotation`, `scale` `set\|add ...` | `pos`, `rot`, `scl` | Moves, turns or scales the selected object. `mp pos bring` and `mp pos grab` move it to or with you. |
| `modify <property> <value>` | `mod` | Changes a property of the selected object. |
| `toolgun [schematic <name\|index>\|type <type>\|mode <create\|delete>]` | `tg` | Gives or removes the tool gun; the subcommands choose what it creates. |
| `indicators [on\|off\|off all]` | `i` | Shows invisible objects (spawnpoints, teleports, invisible primitives) to you. |
| `stats` | `st` | Networked object counts, budgets, the spawn queue and what each player observes. |
| `optimize <schematic> [map\|api]` | `opt` | What the schematic optimizer does to a schematic: networked objects and spawn bytes after each step. |
| `prefabs` | `pf` | Lists the network prefabs. |

`stats`, `optimize` and `prefabs` also write their report to the server log. Map and schematic names are plain file
names in the `Maps` and `Schematics` folders: names with folders (`/`, `\`, `..`), a drive or other characters that are
invalid in file names are refused.

**Remote Admin on the phone.** The client console (控制台) sends commands typed with a leading `/` as Remote Admin
requests, for example `/mp tg` or `/mp pos add 0 0.5 0`. The player needs Remote Admin access through
`config_remoteadmin.txt`. Replies are not printed in the console (read them in the server log), but every editing
command shows its effect in the world and in the tool gun HUD.

## Tool gun on mobile

The tool gun is an FSP-9 with an empty magazine (the Carl Mod client never dry-fires an empty semi-automatic gun such as
ProjectMER's COM-18). The touch buttons map as follows:

| Button | Action |
| --- | --- |
| 攻击 (attack), tap | Create or delete at the crosshair, in the current mode. Holding it acts once. |
| Aim (mouse icon), then attack | Select the object at the crosshair; at nothing, deselect. Non-collidable objects are selectable too. |
| Light toggle (the ((•)) icon) | Switch create/delete; the flashlight is on in create mode. |
| 检视 (inspect) | The same switch, but the client also plays the inspect animation for about 3 s. |
| R (reload) and the throw-away arrow | Previous and next entry of the cycle: every schematic, then the object types. |

The HUD below the crosshair shows the mode and type (or the object under the crosshair), the selection, a grab and the
room. Carl Mod has no server-specific settings, so the schematic to create is chosen with `mp tg schematic <name|index>`
or by cycling. The tool gun never becomes an ordinary gun: when it leaves the inventory other than through `mp tg`
(death, cuffing, escaping) it is destroyed, and SCP-914 leaves it unchanged.

## Configuration

ProjectMER's own options are unchanged: `enable_file_system_watcher`, `auto_select`, and the map actions
`on_waiting_for_players`, `on_round_started`, `on_lcz_decontamination_started`, `on_warhead_started`,
`on_warhead_stopped`, `on_warhead_detonated` (`load:<map>`, `unload:<map>`, `console:<command>`; see the comments in
`config.yml`). With the watcher, a loaded map is reloaded when its file changes on disk; `mp save`, which loads the map
again itself, does not cause a second reload.

Mobile options:

| Option | Default | Meaning |
| --- | --- | --- |
| `static_by_default` | `true` | Schematic blocks are static toys (no per-frame sync) unless they belong to an animated or physics part. `false`: ProjectMER's rule, only blocks exported with `"Static": true` are static. |
| `honor_static_property` | `false` | With `static_by_default`, blocks exported with `"Static": false` become dynamic. Exporters write `false` for blocks that never move, so leave it off. |
| `spawn_max_per_frame` | `10` | Most objects networked per server frame while content streams in, and per player for join and zone streams (about 600 objects per second). |
| `spawn_time_budget_ms` | `3` | Server time per frame for networking queued objects and building schematics. |
| `dynamic_toy_sync_interval` | `0.1` | Seconds between transform updates of animated or physics blocks. |
| `allow_light_shadows` | `false` | Whether MER lights may cast shadows. |
| `light_intensity_scale` | `0.025` | Multiplier for MER light intensities. ProjectMER content uses official SL's HDRP intensities (an exported lamp of intensity 60 is ordinary); Carl Mod renders with Unity's built-in pipeline, where 1 to 2 is a normal light and 60 turns every lit surface white. Use `1` for content made for Carl Mod. |
| `max_lights` | `16` | Most MER lights loaded at once, strongest (intensity × range) first. |
| `max_lights_per_schematic` | `4` | Most lights kept per schematic, strongest first; `-1` for no per-schematic cap. |
| `primitive_warn_per_schematic` | `300` | Warn when one schematic networks more blocks than this. |
| `networked_warn_total` | `1500` | Warn when all MER content networks more objects than this. |
| `networked_hard_cap` | `4000` | Refuse further MER objects above this count. |
| `invisible_collider_mode` | `Transparent` | Collidable-only primitives: `Transparent` (alpha 0; still drawn) or `Skip` (not spawned, no collision). |
| `optimize_schematics` | `true` | Do not network schematic blocks that produce nothing on the client (empty groups, invisible primitives, zero scale). |
| `merge_blocks` | `Maps` | Remove duplicate blocks and merge cubes and quads in static parts of schematics: `None`, `Maps` (schematics placed by maps, `mp create` and the tool gun) or `All` (also schematics spawned by plugins). |
| `managed_visibility` | `true` | MER objects reach players through per-player visibility (paced join streaming, admin-only indicators). |
| `zone_culling` | `SurfaceFacility` | Hide large maps and schematics from players in another part of the facility: `None`, `SurfaceFacility` or `PerZone`. |
| `zone_culling_min_objects` | `150` | Only groups with at least this many networked objects are zone culled. |
| `hud_interval` | `0.5` | Seconds between checks of the tool gun HUD inputs. A changed HUD is sent at once, an unchanged one every `max(2, 4 × hud_interval)` seconds (the client accepts at most 10 hints per 5 s). |
| `warhead_spares_outside_rooms` | `true` | Positions below the surface and outside every room survive the warhead, as in ProjectMER. |
| `log_spawn_stats` | `true` | Log a throughput line each time the spawn queue empties. |

## Mobile limits and performance defaults

The numbers below were measured with the Carl Mod 0.0.4 client in an Android emulator (an x86_64 AVD that runs the ARM
client through binary translation), not on a phone. They compare configurations; phones will differ.
[docs/projectmer-port-plan.md §5.3](docs/projectmer-port-plan.md#53-verification-plan) has the full measurements.

- **Every networked object costs every player a draw call.** Aim for at most about 150 MER primitives visible from any
  one spot. Each visible cube cost about 5-6 µs per frame: 150 cubes took the client from 50 to 49 FPS, 500 to 44 FPS
  and 2000 to 31 FPS. `mp stats` shows the totals; the warnings above flag large content. Transparent primitives (alpha
  below 1, invisible colliders) also cost fill rate.
- **Static by default.** Static toys keep the transform of their spawn message and cost nothing per frame. Editing one
  (`mp pos`, `mp rot`, `mp scale`, `mp modify`, moving a schematic) resends it in place: the phone updates the same
  object, with no duplicate and no leaked materials. Only animated and physics parts of schematics are dynamic, synced
  every `dynamic_toy_sync_interval`; 500 dynamic cubes cost about 7% more frame time than 500 static ones even when
  nothing moves.
- **No parenting on the client.** Schematic blocks are spawned at their world transform; the block hierarchy exists
  only on the server. Content that needs sheared parents (triangles) cannot be shown.
- **Lights.** No shadows by default, `max_lights` in total and `max_lights_per_schematic` per schematic. Spot lights
  become point lights, and intensities are scaled by `light_intensity_scale`. Shadowless lights cost little (8 or 16
  lights stayed within the run-to-run spread); 8 shadowed lights cost about 20% of the frame rate.
- **Loading.** Schematic files are parsed and planned on a worker thread, and blocks are built and networked over the
  following frames within `spawn_time_budget_ms`, about 600 objects per second at the defaults. A 2500-block schematic
  is complete for players after about 4 s. Phones instantiate what arrives in one frame: with a 2000-cube load the
  worst client frames were around 110 ms at `spawn_max_per_frame` 10, 170 ms at 20 and 300-500 ms at 50, hence the
  default of 10.
- **Schematic optimizer.** Blocks that produce nothing on the client stay server-side anchors. With `merge_blocks`,
  exact duplicates are removed (not partly transparent ones, which blend into a deeper tint), and opaque cubes that
  share a whole face and coplanar quads facing the same way that share a whole edge are merged; merged blocks cover
  exactly the space of the blocks they replace. Typical event schematics network 10-16% fewer objects than in
  ProjectMER (Skeld 2923 → 2447, Shipment 1130 → 970); merging contributes up to 5 points of that.
  `mp optimize <schematic>` shows the numbers for any schematic.
- **Despawns are expensive on phones.** The client never frees the materials of a destroyed primitive: each destroyed
  primitive kept about 6 KB (a 500-object zone hidden and shown again: about 3 MB). Avoid repeatedly unloading and
  loading large content during a round; zone culling stays at Surface/Facility granularity for this reason.
- **Doors.** MER doors carry the waypoints that player, ragdoll and pickup positions are sent relative to. The port
  keeps their numbering in step with the clients; a map can add doors until the facility has 223 (about 125 MER doors
  on top of a generated facility). Doors of a load that still wait to spawn count towards that; doors being unloaded do
  not.

## Supported content

[docs/compatibility.md](docs/compatibility.md) lists every map object type, schematic block type and API difference.
In short:

| Content | State |
| --- | --- |
| Primitives, lights, workstations, item and player spawnpoints, shooting targets, teleports, schematics | Supported or adapted to the client (see the limits above). |
| Doors | LCZ, HCZ and EZ doors; bulk doors and gates are skipped. |
| Lockers | 12 of 16 types; the SCP-1576, Anti-SCP-207 and SCP-1344 pedestals and the experimental weapon locker are skipped. |
| Schematic blocks | Empty, primitive, light, pickup and workstation blocks, animator bundles and `-Rigidbodies.json`. |
| Triangles, capybaras, texts, interactables, SCP-079 cameras, waypoints; schematic text, interactable, waypoint and triangle blocks | Absent: the client has no such objects. They stay in the loaded data, a load logs one warning per type, and saving a map keeps them. |
| Maps of the EXILED-based MapEditorReborn for SL 13.2 | Not loaded (another format). Its schematics load. |

### For plugin developers

Reference `ProjectMER.dll` like any LabAPI-Mobile plugin. The API is ProjectMER's, with these differences:

- `ObjectSpawner.SpawnSchematic` returns at once and the schematic builds over the next frames.
  `SchematicObject.IsSpawned` and `Schematic.SchematicSpawned` mark that every server object exists;
  `SchematicObject.IsBuilt` and `Schematic.SchematicBuilt` that every block reached the players.
  `AttachedBlocks`, `NetworkIdentities`, `AdminToyBases`, `AnimationController` and setting `IsStatic` finish the build
  synchronously first (`SchematicObject.EnsureSpawned()`), so code written for ProjectMER sees a complete schematic.
- `Schematic.SchematicSpawning` is raised before the build. If the file was not parsed yet, it is raised once the
  worker has parsed it, and cancelling it destroys the schematic object returned earlier.
- Blocks are not children of the schematic: `AttachedBlocks` holds the server-side anchors and the networked objects;
  each networked object has a `MerBlockLink` pointing back to its schematic and block id.
- Merged and duplicate blocks (see `merge_blocks`) keep only their anchor (name, transform), without a toy of their
  own. Setting `IsStatic = false` networks them individually again before the blocks become dynamic.
- A schematic whose block data cannot be used (for example a pickup `Chance` that is not a number) is destroyed with an
  error while it builds; `ObjectSpawner.SpawnSchematic` has already returned it.
- ProjectMER resets its round state (loaded maps, the spawn queue, visibility) when the round restarts, not at
  `WaitingForPlayers`, so MER content spawned from any plugin's `WaitingForPlayers` handler is kept.

## Building from source

Requirements: the .NET SDK 8 or later, and the Carl Mod server files to compile against. Clone both repositories side
by side:

```powershell
git clone https://github.com/Michaelihc/labapimobile
git clone https://github.com/Michaelihc/projectmer-mobile
cd projectmer-mobile
dotnet build ProjectMER-Mobile.sln -c Release
.\tools\Package.ps1          # release archive: dist\ProjectMER-Mobile-<version>.zip
```

The build compiles `../labapimobile/src/LabApi` and references the game assemblies in
`../labapimobile/.runtime/server-original/Carl Mod_Data/Managed`, which LabAPI-Mobile's `tools/extract-server.py`
creates (see its README). Both locations can be overridden: `-p:LabApiMobileRoot=<checkout>` and
`-p:CarlManaged=<server>\Carl Mod_Data\Managed` (`Package.ps1` takes `-LabApiMobileRoot` and `-CarlManaged`).
`CarlManaged` must be the `Managed` folder of a 0.0.4 server (either 0.0.4 build): LabAPI-Mobile's source uses 0.0.4
member names that 0.0.5 renamed. The result runs on all three server builds.
`Package.ps1` fails if `ProjectMER.dll` references an assembly that neither the game, LabAPI-Mobile nor Harmony
provides.

[docs/testing.md](docs/testing.md) covers test servers, fixtures (`tools/make-mer-fixtures.py`) and checks on the
Android client, using the test tools of the LabAPI-Mobile checkout.

## Credits

- [ProjectMER](https://github.com/Michal78900/ProjectMER) (MapEditorReborn, LabAPI edition) by
  [Michal78900](https://github.com/Michal78900), with the original idea and a code overhaul by
  [Killers0992](https://github.com/Killers0992), another overhaul and documentation by
  [Nao](https://github.com/NaoUnderscore), and testing by Cegła, The Jukers server staff and others, as credited
  upstream.
- [MapEditorReborn](https://github.com/Michal78900/MapEditorReborn), ProjectMER's predecessor.
- [LabAPI-Mobile](https://github.com/Michaelihc/labapimobile), the LabAPI port this plugin runs on.

SCP: Secret Laboratory is a game by Northwood Studios; Carl Mod is a third-party mobile version of it. This repository
contains no game files.

## Licence

The changes and additions in this repository (the Carl Mod port) are licensed under Creative Commons
Attribution-ShareAlike 3.0 Unported ([LICENSE](LICENSE)), the licence MapEditorReborn's source files declare. Code that
comes from upstream ProjectMER remains the work of its authors; the upstream repository publishes no licence file, so
this repository grants no rights to that code beyond what its authors grant. [NOTICE.md](NOTICE.md) has the credits.
