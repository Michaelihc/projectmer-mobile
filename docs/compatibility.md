# ProjectMER compatibility

`src/ProjectMER` is ProjectMER 2025.11.2.1 (MapEditorReborn, LabAPI edition) built on
[LabAPI-Mobile](https://github.com/Michaelihc/labapimobile) for the Carl Mod server. ProjectMER maps (`.yml`) and
schematics (`.json`) load unchanged. The stock Carl Mod client has only primitive, light and shooting-target toys and
no toy parenting, so the types below are adapted or absent. Absent types stay in the loaded data: a load logs one
warning per type with a count, and saving a map keeps their entries. Installation, commands and the mobile options are
in the [README](../README.md). LabAPI-Mobile's own differences from official LabAPI are in its
[docs/compatibility.md](https://github.com/Michaelihc/labapimobile/blob/main/docs/compatibility.md).

States used in the tables:

- **supported**: same behaviour as ProjectMER.
- **adapted**: same purpose, with a difference described in the notes.
- **absent**: the client has no such object, or the feature does not exist in Carl Mod.

## Carl Mod server builds

One `ProjectMER.dll` runs on Carl Mod 0.0.5, the official 0.0.4 server distribution and the 0.0.4 build with the
deathmatch module, with the same behaviour on each. It references no `CarlModExtras` member and no game member that
exists on only some of the builds; every type and member it references (and the target of its warhead patch) resolves
in the `Managed` folder of each build, and the network prefabs it spawns have the same names on all three. On 0.0.5 it
needs LabAPI-Mobile 1.1.7-mobile.5 or later, the first release that runs on 0.0.5. The Android client must have the
server's game version; ProjectMER on 0.0.5 is verified server-side only (the 0.0.4 client cannot join it).

## Map object types

| YAML key | State | Notes |
| --- | --- | --- |
| `primitives` | adapted | Static toys. `PrimitiveFlags` are encoded in the sign of the `Scale` SyncVar and the spawn transform, exact for every flag set and primitive type (one-sided Quad and Plane included). `None` is a server-only object that indicators and the tool gun still find; `Collidable` alone spawns with alpha 0 or is skipped (`invisible_collider_mode`). |
| `lights` | adapted | Intensity (times `light_intensity_scale`, 0.025 by default, because ProjectMER content uses official SL's HDRP intensities), range, color and shadows on/off are applied. `LightType`, `Shape`, spot angles and shadow strength are kept but not applied (spot lights become point lights). Shadows stay off unless `allow_light_shadows`; at most `max_lights` lights. |
| `doors` | adapted | LCZ, HCZ and EZ breakable doors. `RequiredPermissions` uses `KeycardPermissions` (same flag names; `All` means every flag). Bulk doors and gates are skipped. Moving a spawned door respawns it. Every door carries a `NetIdWaypoint`, and player, ragdoll and pickup positions are sent relative to waypoint ids that each side numbers by netId. The port keeps the server's numbering equal to the clients' ([`Features/Mobile/MerWaypoints.cs`](../src/ProjectMER/Features/Mobile/MerWaypoints.cs)): a new door's waypoint starts only once the door has a netId, the server renumbers after every door spawn or respawn, and when a door is destroyed while a newer MER door remains, that door is respawned so connected and later-joining clients number alike. A door whose waypoint started without a netId would be numbered ahead of every vanilla door and shift every id, and clients would place teleported players and new pickups at another door. A facility holds at most 223 door waypoints (the game numbers them 32 to 254); further MER doors are skipped with a warning. Doors of a load that still wait to spawn count towards the limit, doors being unloaded do not, and a removed door is destroyed at once, so a reload never numbers old and new doors together. |
| `workstations` | adapted | Position and yaw quantized to 5.625°; pitch and roll are lost. |
| `item_spawnpoints` | adapted | Firearms use the 13.x firearm state. `Lantern` reads as `Flashlight`; other items missing from Carl Mod are skipped. Pickups are separate objects, not children of the spawnpoint. |
| `player_spawnpoints` | supported | Applied through LabAPI `PlayerSpawning`. With zone culling the spawnpoint's zone is sent to the player before the spawn, as for teleports. |
| `shooting_targets` | supported | Sport, D-class and binary targets. As in ProjectMER, the target buttons do nothing on MER targets (one of them destroys the target). |
| `teleports` | supported | Server-side trigger volumes. |
| `lockers` | adapted | 12 of 16 locker types; SCP-1576, Anti-SCP-207 and SCP-1344 pedestals and the experimental weapon locker are skipped. Chamber permissions use `KeycardPermissions`. Position and yaw only. |
| `schematics` | adapted | See below. |
| `triangles` | absent | ProjectMER draws a triangle with quads under sheared parents; a toy without a parent cannot shear. |
| `capybaras`, `texts`, `interactables`, `scp079_cameras`, `waypoints` | absent | The client has no such toys. A locked schematic pickup (`ButtonInteracted`) is the closest replacement for an interactable. |

Maps of the EXILED-based MapEditorReborn for SL 13.2 use another format and do not load.

## Schematic block types

| Block type | State | Notes |
| --- | --- | --- |
| 0 Empty | adapted | A server-side anchor GameObject; nothing is networked (ProjectMER networked an invisible primitive). |
| 1 Primitive | adapted | As map primitives, spawned at their world transform. Schematics exported without `PrimitiveFlags` keep ProjectMER's rule (`Scale.x >= 0` means collidable). Blocks that render nothing (no flags, alpha 0 without collider, zero scale) are anchors only (`optimize_schematics`). With `merge_blocks`, static duplicates are removed and static cubes and quads are merged (below). |
| 2 Light | adapted | As map lights, and at most `max_lights_per_schematic` per schematic (strongest first). |
| 3 Pickup | supported | `Chance` and `Locked` (button, `ButtonInteracted`) work. Item names (MapEditorReborn 13.2) and numbers are accepted; the 14.x keycard names `KeycardMTFPrivate/Operative/Captain` map to the fork's NTF keycards. |
| 4 Workstation | adapted | Position and yaw only. |
| 5 Schematic, 6 Teleport, 7 Locker | absent | ProjectMER does not build them either; the anchor is kept. |
| 8 Text, 9 Interactable, 10 Waypoint, 11 Triangle | absent | No such toys; the anchor is kept for child blocks. |
| `AnimatorName` (animator bundles) | supported | The animated subtree spawns as dynamic toys that follow their anchors, synced every `dynamic_toy_sync_interval`. A bundle built for another Unity version does not load; the schematic then stays static. |
| `<name>-Rigidbodies.json` | supported | The rigidbody goes on the block's server-side anchor. Every collidable primitive of its subtree gets a copy of its collider on its own anchor, so the body collides as the subtree's compound collider, as ProjectMER's parented toys did; the toys follow their anchors with their own server colliders switched off. A body waits (kinematic) until those toys are spawned. Entries of pickups apply to the pickup's own body. |
| `"Static"` property | adapted | Blocks are static toys unless they are in an animated or physics subtree (`static_by_default`). `"Static": false` only counts with `honor_static_property`. |

**Duplicates and merging** (`merge_blocks`, default `Maps`: schematics placed by maps, `mp create` and the tool gun).
Static primitives identical to an earlier block (type, transform, colour, flags) are dropped, except partly transparent
ones (stacked translucent copies blend into a deeper tint). Static cubes that share a whole face, and coplanar quads
facing the same way that share a whole edge, are merged when they have the same rotation (up to the cube's or quad's
symmetry), colour, flags and transparency class. Partly transparent blocks and blocks under sheared or mirrored parents
are left alone. A merged block covers exactly the space of the blocks it replaces; those keep their anchor (name,
transform, `AttachedBlocks` entry) without a toy. `mp optimize <schematic>` reports every step.

## Schematic API and events

| Item | State | Notes |
| --- | --- | --- |
| Schematic root | adapted | A plain server GameObject with `SchematicObject`; blocks are not its children. Each networked block has a `MerBlockLink` with its schematic and block id. |
| `ObjectSpawner.SpawnSchematic`, `SerializableSchematic.SpawnOrUpdateObject` | adapted | Return the root at once. The file is parsed and planned on a worker thread; anchors and toys are built in later frames within `spawn_time_budget_ms` and networked through the spawn queue. A schematic whose block data cannot be used (a pickup `Chance` that is not a number...) is destroyed with an error during the build; ProjectMER threw from the spawn call. |
| `SchematicObject.AttachedBlocks`, `NetworkIdentities`, `AdminToyBases`, `AnimationController` | adapted | Finish the build synchronously first (`EnsureSpawned`). `AttachedBlocks` holds the anchors and the networked objects. |
| `SchematicObject.Position`, `Rotation`, `Scale` | adapted | Static blocks are resent in place, at most once per 0.25 s. |
| `SchematicObject.IsStatic` | adapted | `false` networks merged and duplicate blocks individually again, then makes every block a dynamic toy following its anchor, also when animated or physics parts are dynamic already. |
| `SchematicObject.Data`, `Plan`, `IsSpawned`, `IsBuilt`, `EnsureSpawned`, `NetworkedCount`, `CurrentAdminToyBases`, `CurrentNetworkIdentities`, `SpawnGroup` | adapted | Port additions. `Data` and `Plan` wait for the worker if needed; the `Current*` lists do not finish the build. |
| `Schematic.SchematicSpawning` | adapted | Raised before the build with a copy of the data. If the file was not parsed yet, it is raised when the worker has parsed it, and cancelling it destroys the root returned earlier. |
| `Schematic.SchematicSpawned` | adapted | Raised when every server object of the schematic exists, a few frames after the spawn call (ProjectMER raised it during the call). |
| `Schematic.SchematicBuilt` | adapted | Port addition: raised when every networked block has been spawned for the players. |
| `Schematic.SchematicDestroyed`, `ButtonInteracted` | supported | `SchematicDestroyed` is raised only for schematics that raised `SchematicSpawned`. |
| `MapUtils.GetSchematicDataByName` | adapted | Newtonsoft.Json instead of Utf8Json, same value shapes in `Properties`. Results are cached until the file changes and shared: treat them as read-only. |
| `ObjectSpawner.SpawnTriangle` | absent | Returns `null` with a warning. |
| Tool gun schematic dropdown (server-specific settings) | absent | Carl Mod has no server-specific settings; `mp tg schematic <name or index>` chooses the schematic. |
| `ExperimentalWeaponLockerGlobalLimitsPatch` | absent | Carl Mod has no experimental weapon locker. |
| `AlphaWarheadCanBeDetonatedFix` | adapted | Behind `warhead_spares_outside_rooms`. Carl Mod kills everything below y = 900; the patch spares positions below that height that are outside every room (MER-built areas), except lifts. |
