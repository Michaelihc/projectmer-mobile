# ProjectMER for Carl Mod

MapEditorReborn (ProjectMER 2025.11.2.1, LabAPI edition) for the Carl Mod server, running on LabAPIMobile. It is
server-side only: players use the stock Carl Mod Android client. It loads ProjectMER maps (`.yml`) and schematics
(`.json`, from the ProjectMER and MapEditorReborn 13.2 exporters).

The Carl Mod client knows fewer object types than SCP:SL 14 and renders every networked object on a phone, so some
content is adapted or skipped. [docs/compatibility.md](../../docs/compatibility.md#projectmer) lists every type.

## Install

1. Install LabAPIMobile on the server copy:
   `dotnet run --project src/Installer -- <server-dir> <dir-with-LabApi.dll-and-0Harmony.dll>`.
2. Build the plugin: `dotnet build src/ProjectMER/ProjectMER.csproj -c Release`.
3. Copy `ProjectMER.dll` into `<appdata>/SCP Secret Laboratory/LabAPI-Mobile/plugins/global/` (or `plugins/<port>/`).
   With `gamedir_for_configs: true` in `hoster_policy.txt`, `<appdata>` is the server's `AppData` folder.
4. Start the server once. It creates:
   - `LabAPI-Mobile/configs/ProjectMER/Maps/` for maps (`<name>.yml`);
   - `LabAPI-Mobile/configs/ProjectMER/Schematics/` for schematics, one folder each:
     `Schematics/<name>/<name>.json`, plus `<name>-Rigidbodies.json` and animator bundles next to it. A bare
     `Schematics/<name>.json` is moved into its folder the first time it is used;
   - `LabAPI-Mobile/configs/<port>/ProjectMER/config.yml`, the configuration.
5. Grant permissions in `LabAPI-Mobile/configs/permissions.yml`: `mpr.<command>` per command (for example
   `mpr.load`), or `mpr.*`.

## Commands

Remote Admin command `mapeditor` (aliases `mer`, `mp`). Every command also works from the Remote Admin panel of the
mobile client. Run `mp` alone for the list.

| Command | Aliases | Use |
| --- | --- | --- |
| `load <map>` | `l` | Loads a map. Loading a loaded map reloads it. |
| `unload [map]` | `unl` | Unloads a map, or every map. |
| `save <map>` | `s` | Saves the loaded map, including objects created since (the `Untitled` map). |
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

`stats`, `optimize` and `prefabs` also write their report to the server log.

**Tool gun on mobile.** The tool gun is an FSP-9 with an empty magazine (the Carl Mod client never dry-fires an empty
semi-automatic gun such as ProjectMER's COM-18). The touch buttons map as follows:

| Button | Action |
| --- | --- |
| 攻击 (attack), tap | Create or delete at the crosshair, in the current mode. Holding it acts once. |
| Aim (mouse icon), then attack | Select the object at the crosshair; at nothing, deselect. Non-collidable objects are selectable too. |
| Light toggle (the ((•)) icon) | Switch create/delete; the flashlight is on in create mode. |
| 检视 (inspect) | The same switch, but the client also plays the inspect animation for about 3 s. |
| R (reload) and the throw-away arrow | Previous and next entry of the cycle: every schematic, then the object types. |

The HUD below the crosshair shows the mode and type (or the object under the crosshair), the selection, a grab and the
room. Carl Mod has no server-specific settings, so the schematic to create is chosen with
`mp tg schematic <name|index>` or by cycling.

**Remote Admin on the phone.** The commands also run from the client console (控制台): type them with a leading `/`, for
example `/mp tg` or `/mp pos add 0 0.5 0`; the console shows "Sending remote admin request" and the server runs it as
an RA command (the player needs RA access through `config_remoteadmin.txt`). On the emulator the 管理 (RA panel)
button did not open the panel, so the console is the tested route; RA replies are not printed in the console, but every
editing command shows its effect in the world and in the tool gun HUD.

## Configuration

ProjectMER's own options are unchanged: `enable_file_system_watcher`, `auto_select`, and the map actions
`on_waiting_for_players`, `on_round_started`, `on_lcz_decontamination_started`, `on_warhead_started`,
`on_warhead_stopped`, `on_warhead_detonated` (`load:<map>`, `unload:<map>`, `console:<command>`; see the comments in
`config.yml`).

Mobile options:

| Option | Default | Meaning |
| --- | --- | --- |
| `static_by_default` | `true` | Schematic blocks are static toys (no per-frame sync) unless they belong to an animated or physics part. `false`: ProjectMER's rule, only blocks exported with `"Static": true` are static. |
| `honor_static_property` | `false` | With `static_by_default`, blocks exported with `"Static": false` become dynamic. Exporters write `false` for blocks that never move, so leave it off. |
| `spawn_max_per_frame` | `10` | Most objects networked per server frame while content streams in, and per player for join and zone streams (about 600 objects per second). Phones instantiate what arrives in one frame: on the reference emulator a 2000-cube load had worst frames around 110 ms at 10, 170 ms at 20 and 300-500 ms at 50. |
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

## Mobile limits

- **Every networked object costs every player a draw call.** Aim for at most about 150 MER primitives visible from
  any one spot. On the reference emulator each visible cube cost about 5-6 µs per frame: 150 cubes took the client
  from 50 to 49 FPS, 500 to 44 FPS and 2000 to 31 FPS (docs/projectmer-port-plan.md §5.3). `mp stats` shows the
  totals; the warnings above flag large content. Transparent primitives (alpha below 1, invisible colliders) also cost
  fill rate.
- **Static by default.** Static toys keep the transform of their spawn message and cost nothing per frame. Editing
  one (`mp pos`, `mp rot`, `mp scale`, `mp modify`, moving a schematic) resends it in place: the phone updates the same
  object, with no duplicate and no leaked materials. Only animated and physics parts of schematics are dynamic, synced
  every `dynamic_toy_sync_interval`; 500 dynamic cubes cost about 7% more frame time than 500 static ones even when
  nothing moves.
- **No parenting on the client.** Schematic blocks are spawned at their world transform; the block hierarchy exists
  only on the server. Content that needs sheared parents (triangles) cannot be shown.
- **Lights.** No shadows by default, `max_lights` in total and `max_lights_per_schematic` per schematic. Spot lights
  become point lights, and intensities are scaled by `light_intensity_scale`. Shadowless lights cost little (8 or 16
  lights stayed within the run-to-run spread); 8 shadowed lights cost about 20% of the frame rate.
- **Schematic optimizer.** Blocks that produce nothing on the client stay server-side anchors. With `merge_blocks`,
  exact duplicates are removed, opaque cubes that share a whole face and coplanar quads facing the same way that
  share a whole edge are merged; merged blocks cover exactly the space of the blocks they replace. Run
  `mp optimize <schematic>` for the numbers. Typical event schematics network 10-16% fewer objects than in ProjectMER
  (Skeld 2923 → 2447, Shipment 1130 → 970); merging contributes up to 5 points of that.
- **Loading.** Schematic files are parsed and planned on a worker thread, and blocks are built and networked over the
  following frames within `spawn_time_budget_ms`, about 600 objects per second at the defaults (`spawn_max_per_frame`
  10). A 2500-block schematic is complete for players after about 4 s.
- **Despawns are expensive on phones.** The client never frees the materials of a destroyed primitive: each destroyed
  primitive kept about 6 KB on the emulator (a 500-object zone hidden and shown again: about 3 MB). Avoid repeatedly
  unloading and loading large content during a round; zone culling stays at Surface/Facility granularity for this
  reason.
- **Doors.** MER doors carry the waypoints that player, ragdoll and pickup positions are sent relative to. The port
  keeps their numbering in step with the clients (docs/compatibility.md); a map can add doors until the facility has
  224 (about 128 MER doors on top of a generated facility).

### For plugin developers

The API is ProjectMER's, with these differences:

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
