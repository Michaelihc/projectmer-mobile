# ProjectMER port plan for the Carl Mod server

This plan covers porting ProjectMER (MapEditorReborn, LabAPI edition, version 2025.11.2.1 including the
Triangle and Quad additions) to the Carl Mod fork. It sets out the feature matrix, file-format
compatibility, the mobile performance design, tool-gun editing without server-specific settings, and a
file-level port and verification plan.

Paths without a prefix are relative to this repository (`src/ProjectMER`, `docs/`, `tools/make-mer-fixtures.py`).
`../labapimobile/` is the sibling [LabAPI-Mobile](https://github.com/Michaelihc/labapimobile) checkout, which holds
LabAPI, the test tools (`tools/Start-TestServer.ps1`, `tools/Send-ServerCommand.ps1`, `tools/android/`,
`tools/EventProbe/`) and the extracted server (`.runtime/server-original`).

Path abbreviations used below. Entries marked *local* are decompilations and reference copies in the author's local
workspace; they are not part of either repository.

| Abbreviation | Path |
|---|---|
| `PMER/` | Upstream ProjectMER source, <https://github.com/Michal78900/ProjectMER> (*local* copy: `.refereces/scpsl-metarepo/.references/Reference Plugins/ProjectMER/` in the LabAPI-Mobile workspace) |
| `MER132/` | MapEditorReborn for SL 13.2, <https://github.com/Michal78900/MapEditorReborn> (*local*: Carl Mod analysis workspace, `references/MapEditorReborn-sl13.2/MapEditorReborn/`) |
| `FORK/` | Decompiled Carl Mod server `Assembly-CSharp`, Mono CIL bodies (*local*: Carl Mod analysis workspace, `decompiled/server/`) |
| `CLIENT/` | Carl Mod client IL2CPP metadata view; bodies are placeholders (*local*: Carl Mod analysis workspace, `decompiled/carl-client/`) |
| `OFFICIAL/` | Decompiled official SL 14.2.7 dedicated server `Assembly-CSharp` (*local*: `.refereces/scpsl-metarepo/.references/Decompiled/DedicatedServer/Assembly-CSharp/` in the LabAPI-Mobile workspace) |
| `MIRROR/` | `../labapimobile/.runtime/server-original/Carl Mod_Data/Managed/Mirror.dll`, decompiled with `ilspycmd -p -o <dir> Mirror.dll`; cited as `Mirror/<File>.cs: <member>` |
| `ASSETS` | `../labapimobile/.runtime/server-original/Carl Mod_Data/{resources.assets,sharedassets1.assets,level1}` |

The client is IL2CPP but built from the same game version as the server, and its metadata has the same
fields and members for the toy classes (`CLIENT/AdminToys/*.cs`). Client toy behaviour below is
therefore read from the server's CIL bodies.

---

## 0. Fork facts that drive the design

1. **Only four toy classes exist.** `FORK/AdminToys/` and `CLIENT/AdminToys/` contain `AdminToyBase`,
   `PrimitiveObjectToy`, `LightSourceToy`, `ShootingTarget` and the unused enum `PrimitiveFlags`.
   The fork has no Text, Capybara, InvisibleInteractable, Scp079Camera, Waypoint, Speaker or
   SpawnableCullingParent toys.
2. **There is no `PrimitiveFlags` SyncVar.** `PrimitiveObjectToy` syncs only `PrimitiveType` and
   `MaterialColor`. `PrimitiveFlags` is referenced nowhere else (grep of `FORK/`). Collision is
   decided in `PrimitiveObjectToy.SetPrimitive` (`FORK/AdminToys/PrimitiveObjectToy.cs:81-100`): the
   built-in collider is removed and a `MeshCollider` is added only if `Scale.x > 0 || Scale.y > 0 ||
   Scale.z > 0` (the `Scale` SyncVar, not the transform). The collider is convex except for Plane and
   Quad. `SetPrimitive` runs from `Start` (line 76) and from the `PrimitiveType` hook. Primitives are
   always rendered.
3. **Clients have no toy parenting.** The fork's `AdminToyBase` (`FORK/AdminToys/AdminToyBase.cs`) has
   no parent sync. The client writes **world** position, rotation and `localScale` from SyncVars in
   `UpdatePositionClient` (line 126), unless `IsStatic`. Official 14.x adds
   `_clientParentId`/`RpcChangeParent` (`OFFICIAL/AdminToys/AdminToyBase.cs:27-29,152-160,212-237`),
   and ProjectMER's hierarchy (schematic roots, triangle shear parents) depends on that. Mirror's spawn
   message carries `transform.localPosition/localRotation/localScale`
   (`Mirror/NetworkServer.cs: SendSpawnMessage`). The client applies them as local values on a root
   object (`Mirror/NetworkClient.cs: ApplySpawnPayload`). **Any networked object that has a server-side
   parent at spawn time appears in the wrong place on the client.**
4. **`IsStatic` stops all per-frame transform work on both sides.** The server's `UpdatePositionServer`
   (line 116) and the client's `UpdatePositionClient` both return early. A static toy keeps the transform
   from its spawn message. That transform includes a full-precision `Quaternion` (16 bytes), whereas
   dynamic toys sync rotation as `LowPrecisionQuaternion`, four `sbyte`s truncated from `x*127`
   (`FORK/LowPrecisionQuaternionSerializer.cs`). That truncation gives up to about 1° of error, or
   about 17 cm at 10 m, which shows up as seams between rotated blocks.
5. **Each primitive gets its own materials.** `SetColor` creates `new Material(_regularMatTemplate)` and
   `new Material(_transparentMatTemplate)` per toy instance (`PrimitiveObjectToy.cs:112-113`). Nothing
   destroys them in `OnDestroy`, so every client-side spawn/despawn leaks two materials until the next
   scene unload. Unique materials also rule out dynamic batching and instancing. The fork uses the
   built-in render pipeline: Managed has `Unity.Postprocessing.Runtime.dll` and no
   `Unity.RenderPipelines.*`, so there is no SRP batcher. **Every visible primitive costs at least one
   draw call, plus one per extra pixel light and one per shadow-casting light.**
6. **Toys are outside the fork's client room culling.** Pickups, lockers and doors carry
   `CustomCulling.DynamicCullableBase`/`DoorLinkingRooms` (asset scan, §1.3;
   `FORK/CustomCulling/DynamicCullableBase.cs`). The `PrimitiveObjectToy` and `LightSourceToy` prefabs
   carry only their toy component. Toys behind walls in culled rooms still render whenever they are in
   the frustum. The only way to remove them from a phone is not to spawn them for that player.
7. **No interest management is installed.** `CustomInterestManagement` exists
   (`FORK/CustomInterestManagement.cs`: every ready connection observes everything). It is not
   referenced anywhere in `FORK/` and is not attached in `level1`, which holds `NetworkManager` with
   `CustomNetworkManager` and `CustomLiteNetLib4MirrorTransport` (UnityPy scan of `ASSETS`). So
   `NetworkServer.aoi` is null. Per-connection visibility is still available through
   `NetworkIdentity.visibility` and the observer internals (§3.8).
8. **There are no server-specific settings.** `FORK/` and `CLIENT/` have no `UserSettings.ServerSpecific`
   namespace. The mobile HUD has attack, aim, reload, throw-away (drop), inspect, interact, **admin
   (Remote Admin)** and **console** buttons (`CLIENT/MobileTouchController.cs:58-247`), plus a light
   toggle (`CLIENT/MobileActionArea.cs:136`).
9. **Utf8Json is not shipped.** The fork's Managed folder has `Newtonsoft.Json.dll`,
   `System.Text.Json.dll` and `YamlDotNet.dll`. `Serialization.CommentsObjectGraphVisitor` and
   `CommentGatheringTypeInspector`, which `YamlParser` uses, exist in `FORK/Serialization/`. MEC
   `Timing` lives in `DigitalDust.dll`.

---

## 1. Feature matrix

Legend: **as-is** = port with namespace/API changes only; **adapted** = works with a different
implementation; **unsupported** = the stock client lacks the feature, so the entry is skipped with one
warning per type (§2.3) and kept in the data model so saving a map does not drop it.

### 1.1 Map object types (`PMER/Features/Serializable/MapSchematic.cs` dictionaries)

| YAML key | Type | Status | How / why |
|---|---|---|---|
| `primitives` | `SerializablePrimitive` | adapted | Flags are encoded in the scale sign and static spawn (§3.3). `Visible\|Collidable` and `Visible` are exact. `Collidable`-only becomes an alpha-0 transparent primitive (config). `None` is not networked. |
| `triangles` | `SerializableTriangle` | unsupported | ProjectMER builds each triangle from three parallelograms. Each one is a quad under a non-uniformly scaled, rotated parent, i.e. a shear (`PMER/Features/TrianglePrimitiveBuilder.cs:63-90`). Without client parenting (fact 3), a toy is only rotation × axis scale, so it can never shear. |
| `lights` | `SerializableLight` | adapted | The fork syncs `LightIntensity`, `LightRange`, `LightColor` and `bool LightShadows` (`FORK/AdminToys/LightSourceToy.cs`). `LightType`, `Shape`, `SpotAngle`, `InnerSpotAngle` and `Strength` are dropped with a warning; spot lights become point lights. Shadows are forced off by default, and a light cap applies (§3.6). |
| `doors` | `SerializableDoor` | adapted | Lcz, Hcz and Ez prefabs exist; Bulkdoor and Gate do not (§1.3) and are skipped. `RequiredPermissions` keeps its name but uses the fork's `KeycardPermissions`. `DoorPermissionsPolicy` becomes `DoorPermissions { RequiredPermissions, RequireAll }` (`FORK/Interactables.Interobjects.DoorUtils/DoorPermissions.cs`). `DoorRandomInitialStateExtension` does not exist, so that call is removed. Overriding a vanilla door by name (`MapSchematic.Reload`) works through LabAPI `Door.Get(name)`. Moving a door still needs UnSpawn+Spawn, because doors have no transform sync. |
| `workstations` | `SerializableWorkstation` | as-is | Prefab is `Spawnable Work Station Structure`. `StructurePositionSync` gives the client position plus yaw quantized to 5.625°; pitch and roll are lost (`FORK/MapGeneration.Distributors/StructurePositionSync.cs`, `Update`). |
| `item_spawnpoints` | `SerializableItemSpawnpoint` | adapted | Firearm setup uses 13.x `FirearmStatus(ammo, flags, attachments)` instead of 14.x `MagazineModule`. The default `ItemType.Lantern` is missing from the fork's enum, so it becomes `Flashlight`. Pickups are not parented to the spawnpoint GameObject (fact 3). |
| `player_spawnpoints` | `SerializablePlayerSpawnpoint` | as-is (depends on LabAPI) | Needs LabAPI `PlayerSpawning` with `UseSpawnPoint`/`SpawnLocation`. Indicator rewritten (§1.5). |
| `capybaras` | `SerializableCapybara` | unsupported | No `CapybaraToy` in the client. |
| `texts` | `SerializableText` | unsupported | No `TextToy`. |
| `interactables` | `SerializableInteractable` | unsupported | No `InvisibleInteractableToy`. The closest existing alternative is the schematic "locked pickup" button (`ButtonInteracted`). |
| `scp079_cameras` | `SerializableScp079Camera` | unsupported | No `Scp079CameraToy`. 13.x cameras are scene objects with `sceneId`, not spawnable prefabs. |
| `shooting_targets` | `SerializableShootingTarget` | as-is | Prefabs `sportTargetPrefab`, `dboyTargetPrefab`, `binaryTargetPrefab` exist. `ShootingTarget.ServerInteract` button 5 destroys the target, so the `PlayerInteractingShootingTarget` cancel in `GenericEventHandlers` is required. |
| `schematics` | `SerializableSchematic` | adapted | The root becomes a plain server GameObject instead of an invisible networked primitive. A `None`-flag primitive would render in the fork. Blocks are flattened and spawned static and batched (§3). |
| `teleports` | `SerializableTeleport` | as-is | Server-only trigger `BoxCollider`. Players have a server `CharacterController` (`FORK/PlayerRoles.FirstPersonControl/FirstPersonMovementModule.cs:255`). Indicator rewritten. |
| `lockers` | `SerializableLocker` | adapted | 13 of 16 `LockerType`s exist (§1.3). `PedestalScpScp1576`, `PedestalAntiScp207`, `PedestalScp1344` and `ExperimentalWeapon` are skipped. Chamber permissions use `KeycardPermissions`. Yaw-only placement, as for workstations. |
| `waypoints` | `SerializableWaypoint` | unsupported | No `WaypointToy`. Players standing on moving toys are not carried. |

### 1.2 Schematic block types (`PMER/Features/Enums/BlockType.cs`, `SchematicBlockData.Create`)

| BlockType | Status | Port behaviour |
|---|---|---|
| 0 Empty | adapted | A server-only GameObject (anchor) instead of a networked `PrimitiveObjectToy` with `PrimitiveFlags.None`, which renders in the fork. Anchors are skipped entirely when nothing beneath them needs them (§3.9). |
| 1 Primitive | adapted | Flag encoding as in §3.3. Old schematics without `PrimitiveFlags` keep ProjectMER's rule (`Scale.x >= 0` means collidable). |
| 2 Light | adapted | As for map lights. Animators on lights are skipped, as in ProjectMER. |
| 3 Pickup | as-is | `Chance` and `Locked` (button → `ButtonInteracted`) are kept. `ItemType` accepts both names and numbers; MER 13.2 wrote names (`MER132/API/Features/Objects/SchematicObject.cs`, `Enum.Parse`). Flattened to world space. |
| 4 Workstation | as-is | See 1.1. |
| 5 Schematic, 6 Teleport, 7 Locker | unchanged | ProjectMER already falls back to an empty object for these (`SchematicBlockData.Create`, `_ => CreateEmpty(true)`). The port keeps the anchor and the one-time warning. Locker blocks could be added later from MER 13.2 (`MER132/.../SchematicObject.cs` `case BlockType.Locker`). |
| 8 Text, 9 Interactable, 10 Waypoint | unsupported | Skipped with one warning per type; the anchor is kept if it has children. |
| 11 Triangle | unsupported | See 1.1. |

### 1.3 Network prefabs in the fork

Below are the root GameObjects with `Mirror.NetworkIdentity` found by a UnityPy scan of `ASSETS`. These
are the prefabs Mirror can spawn. The runtime source of truth is `NetworkClient.prefabs` (assetId →
prefab), which the server populates; vanilla `SpawnToyCommand` and MER 13.2 both read it
(`FORK/CommandSystem.Commands.RemoteAdmin/SpawnToyCommand.cs`,
`MER132/Events/Handlers/Internal/EventHandler.cs:58-83`).

| ProjectMER key | Fork prefab name (`gameObject.name`) | Identifying component | Present |
|---|---|---|---|
| `PrimitiveObject` | `PrimitiveObjectToy` | `AdminToys.PrimitiveObjectToy` | yes |
| `LightSource` | `LightSourceToy` | `AdminToys.LightSourceToy` | yes |
| `DoorLcz` | `LCZ BreakableDoor` | `Interactables.Interobjects.BreakableDoor` (+`DoorLinkingRooms`, `NetIdWaypoint`) | yes |
| `DoorHcz` | `HCZ BreakableDoor` | `BreakableDoor` | yes |
| `DoorEz` | `EZ BreakableDoor` | `BreakableDoor` | yes |
| `DoorHeavyBulk` | `HCZ BulkDoor` | — | **no** (added in 14.x) |
| `DoorGate` | `Spawnable Unsecured Pryable GateDoor` | — | **no**. Gates exist only as `level2` scene objects (`Unsecured Pryable GateDoor`, `Keycard Pryable GateDoor`). They have a `sceneId` and no assetId, so they cannot be cloned for clients. |
| `Workstation` | `Spawnable Work Station Structure` | `WorkstationController`, `SpawnableStructure`, `StructurePositionSync` | yes |
| `ShootingTargetSport/DBoy/Binary` | `sportTargetPrefab`, `dboyTargetPrefab`, `binaryTargetPrefab` | `AdminToys.ShootingTarget` | yes |
| `LockerLargeGun` | `LargeGunLockerStructure` | `Locker`, `StructurePositionSync` | yes |
| `LockerRifleRack` | `RifleRackStructure` | `Locker` | yes |
| `LockerMisc` | `MiscLocker` | `Locker` | yes |
| `LockerRegularMedkit` | `RegularMedkitStructure` | `Locker` | yes |
| `LockerAdrenalineMedkit` | `AdrenalineMedkitStructure` | `Locker` | yes |
| `PedestalScp500/018/207/244/268/1853/2176` | `Scp500PedestalStructure Variant`, `Scp018…`, `Scp207…`, `Scp244…`, `Scp268…`, `Scp1853…`, `Scp2176PedestalStructure Variant` | `PedestalScpLocker` | yes |
| `PedestalScp1576`, `PedestalAntiScp207`, `PedestalScp1344`, `LockerExperimentalWeapon` | — | — | **no** |
| `Capybara`, `Text`, `Interactable`, `Camera*`, `Waypoint`, `CullingParent` | — | — | **no** |
| (not in ProjectMER) | `GeneratorStructure` | `Scp079Generator` | yes (possible future object type) |

How the port finds them:

- `PrefabManager.RegisterPrefabs` keeps ProjectMER's approach of iterating `NetworkClient.prefabs.Values`
  and matching on component plus name. It uses the fork names above, for example
  `TryGetComponent(out WorkstationController)` for the workstation, whose name differs from 14.x. It
  logs once, at `WaitingForPlayers`, every expected prefab that was not found.
- Other registries in the fork: `MapGeneration.DoorSpawnpoint.TargetPrefab` for door prefabs (MER 13.2
  used `FindObjectsOfType<DoorSpawnpoint>()`), and
  `SpawnablesDistributorSettings.SpawnableStructures`, whose `SpawnableStructure.StructureType` covers
  `Workstation`, `LargeGunLocker`, `ScpPedestal`, `Scp079Generator` and others
  (`FORK/MapGeneration.Distributors/`).
- A debug command, `mp prefabs`, dumps `name`, `assetId` and the component list of every registered
  prefab. In-game `spawntoy list` lists the toy prefabs.

### 1.4 Commands (`PMER/Commands/`; parent `mapeditor`, aliases `mer`/`mp`; permissions `mpr.<command>`)

| Command | Status | Notes |
|---|---|---|
| `save`, `load`, `unload`, `list`, `merge` | as-is | Map loading goes through the spawn queue (§3.5). |
| `toolgun` (`tg`) | adapted | No SSS dropdown. New subcommands `mp tg schematic <name\|index>`, `mp tg type <type>` and `mp tg mode <create\|delete>`, per player and usable without holding the tool gun (§4). |
| `indicators` | adapted | Per player: `mp indicators` toggles them for the sender, `on`/`off` set them, and `off all` (also from the server console) turns them off for everyone. Other players never receive them (§3.8). The networked parts are built when the first player turns them on and destroyed when the last one turns them off or leaves. No per-frame `Update`. |
| `position set/add/bring/grab`, `rotation set/add`, `scale set/add` | adapted | Edits to static toys resend the spawn message in place, with no destroy (§3.2). `grab` switches toys and schematics of up to 50 blocks to dynamic while grabbed; larger schematics, doors and server-only objects move a proxy box and move once on release (§4). |
| `modify` | as-is | Reflection over `Serializable*`. Enum parsing uses the fork's enums. |
| `create`, `delete`, `select` | adapted | `create` lists only supported types. Selection maps a hit collider back to its `MapEditorObject` through a link component, because toys are no longer children of their schematic root (§3.2). |
| new: `mp stats` | — | Networked and static counts, light count, queue length, observed objects per player, estimated spawn bytes. |
| new: `mp optimize <schematic> [map\|api]` (`opt`) | — | Shows the cached plan of the simplifier (§3.9): networked objects by kind and estimated spawn bytes after each step, and the reason counts. `api` shows the plan of a plugin-spawned schematic (`merge_blocks: Maps` does not merge those). |
| new: `mp prefabs` | — | Prefab dump (§1.3). |

### 1.5 Tool-gun features (`PMER/Features/ToolGun/*`, `Events/Handlers/Internal/ToolGunEventsHandler.cs`)

| Feature | Status | Notes |
|---|---|---|
| COM-18 item with 0 ammo; dry-fire triggers the action | adapted | An FSP-9: the fork's client never dry-fires an empty semi-automatic weapon such as the COM-18. The fork's `FirearmBasicMessagesHandler` handles `Dryfire`, `Reload`, `AdsIn/Out`, `ToggleFlashlight` and `Inspect` (`FORK/InventorySystem.Items.Firearms.BasicMessages/FirearmBasicMessagesHandler.cs:99-142`). Status: 0 rounds, `Cocked \| Chambered \| MagazineInserted` (the FSP-9 has a bolt lock). The attachment code is computed from the firearm (first attachment per slot, the flashlight in its slot). The acquisition refill is disabled and a tool gun leaving the inventory any other way than `mp tg` has its pickup destroyed (§4). |
| Create/Delete mode by flashlight, Select by ADS | adapted | Aim + attack selects in either mode (mobile aim is a toggle). Inspect and the light toggle switch Create/Delete; the mode is per player and the flashlight shows it (on: Create). The fork only toggles the light with a flashlight attachment, which the tool gun has (§4). |
| Drop = next type, Reload = previous type | adapted | Mobile throw-away and reload buttons. The cycle is every schematic, then the supported types; ProjectMER's enum arithmetic breaks once types are removed. |
| Schematic choice via SSS dropdown | replaced | `mp tg schematic`, or reload/drop through the schematics; the name is shown in the HUD (§4). |
| HUD hint every 0.1 s (`ToolGunUI.GetHintHUD`) | adapted | A compact HUD sent at once when its contents change, within the client's limit of 10 hints per 5 s (at most 8 per 5 s), with a keep-alive every 2 s. It drops the 36 padding lines and the `LiberationSans SDF` font tag (§4). |
| Indicators for invisible objects | adapted | Per-player visible, non-colliding (§3.3) and server-trigger selectable, as in ProjectMER (`IndicatorObject.TrySpawnOrUpdateIndicator` adds a trigger `BoxCollider`, here on the Ignore Raycast layer like the editing triggers, §4). Indicator roots with `PrimitiveFlags.None` become server-only anchors. Networked parts exist only while someone views indicators; roots and their triggers are also created for objects made or edited while nobody does, as in ProjectMER, so they stay selectable. |
| AutoSelect after create | as-is | |

### 1.6 Events

| Event / handler | Status | Notes |
|---|---|---|
| `Schematic.SchematicSpawning` (cancellable, `Data` replaceable), `SchematicSpawned`, `SchematicDestroyed`, `ButtonInteracted` | adapted | `SchematicSpawning` is raised before the build with a copy of the data: during the spawn call when the file is cached, otherwise once the worker has parsed it (cancelling then destroys the root returned earlier). `SchematicSpawned` (with the new `SchematicObject.IsSpawned`) is raised when every server object exists, a few frames after the spawn call (§3.5). A new `SchematicBuilt` event and `SchematicObject.IsBuilt` signal that the batched network spawn has finished. `SchematicDestroyed` is raised only for schematics that raised `SchematicSpawned`. |
| `GenericEventsHandler` (prefab registration, player spawnpoints, shooting-target guard) | as-is | Depends on LabAPI `ServerWaitingForPlayers`, `PlayerSpawning` and `PlayerInteractingShootingTarget`. |
| `PickupEventsHandler` (button pickups, `NumberOfUses`) | adapted | Firearm refill uses `FirearmStatus`. Depends on `PlayerSearchingPickup`, `PlayerPickingUpItem` and `PlayerPickingUpAmmo`. |
| `ToolGunEventsHandler` | adapted | Uses `PlayerDryFiringWeapon`, `PlayerReloadingWeapon`, `PlayerUnloadingWeapon`, `PlayerDroppingItem`, `PlayerInspectingItem` and `PlayerTogglingWeaponFlashlight` (raised by the port from `FirearmBasicMessagesHandler.ServerRequestReceived` and `Inventory.CmdDropItem`), plus `PlayerChangedItem`, `PlayerLeft`, `ServerRoundRestarted` and `Scp914ProcessingInventoryItem` (SCP-914 leaves a tool gun unchanged). Aim is read from `AdsModule.ServerAds` (`AimingWeapon` does not exist; `AimedWeapon` is not subscribed). Tool guns leaving an inventory are caught by the fork's `InventoryExtensions.OnItemRemoved`; their pickup is locked (so escaping does not hand the same serial back as a plain FSP-9) and destroyed. |
| `ActionOnEventHandlers` | as-is | Waiting-for-players, round started, LCZ decontamination started, and warhead started/stopped/detonated. The unload path calls `HandleMapLoading` for split arguments; the port calls `HandleMapUnloading`. |

### 1.7 Config options

| Option | Status |
|---|---|
| `EnableFileSystemWatcher` | adapted. `FileSystemWatcher.Changed` runs on a thread-pool thread, but ProjectMER calls `Timing.CallDelayed` from it (`PMER/ProjectMER.cs: OnMapFileChanged`). The port enqueues the map name to a main-thread queue that an MEC coroutine drains. A map whose file is unchanged since the server last read it is not reloaded again (`mp save` writes the file and loads the map itself). |
| `AutoSelect` | as-is |
| `OnWaitingForPlayers`, `OnRoundStarted`, `OnLczDecontaminationStarted`, `OnWarheadStarted/Stopped/Detonated` | as-is |
| New mobile options | `static_by_default: true`, `honor_static_property: false` (§3.2), `spawn_max_per_frame: 10`, `spawn_time_budget_ms: 3`, `dynamic_toy_sync_interval: 0.1`, `allow_light_shadows: false`, `light_intensity_scale: 0.025` (§3.6), `max_lights: 16`, `primitive_warn_per_schematic: 300`, `networked_warn_total: 1500`, `networked_hard_cap: 4000`, `invisible_collider_mode: Transparent` (`Transparent`/`Skip`), `optimize_schematics: true`, `merge_blocks: Maps` (`None`/`Maps`/`All`, §3.9), `max_lights_per_schematic: 4` (`-1` = no per-schematic cap), `managed_visibility: true`, `zone_culling: SurfaceFacility` (`None`/`SurfaceFacility`/`PerZone`; read at each round reset), `zone_culling_min_objects: 150`, `hud_interval: 0.5`, `warhead_spares_outside_rooms: true` (§1.8), `log_spawn_stats: true` (one throughput line per spawn-queue drain). `spawn_max_per_frame` and `light_intensity_scale` were set from the Android measurements in §5.3; the other values were confirmed there (a real-device pass is still open). |

### 1.8 Other API and patches

| Item | Status | Notes |
|---|---|---|
| `ObjectSpawner.*` | as-is | |
| `SchematicObject` (`Position/Rotation/Scale` setters, `AttachedBlocks`, `NetworkIdentities`, `AdminToyBases`, `AnimationController`, `Destroy`) | adapted | Blocks are kept in internal lists because flattened toys are not children. `AttachedBlocks`, `NetworkIdentities`, `AdminToyBases`, `AnimationController` and the `IsStatic` setter first finish the build synchronously (new `EnsureSpawned()`, §3.5); the new `CurrentAdminToyBases`/`CurrentNetworkIdentities` return what exists without waiting, for periodic scans. On a static schematic, `Position/Rotation/Scale` setters resend spawn payloads in place, coalesced to at most one resend per 0.25 s. New `IsStatic` property; setting it to false networks merged and duplicate blocks individually again (§3.9), then switches to synced dynamic toys. The root no longer has a `PrimitiveObjectToy`. |
| `AnimationController`, animator AssetBundles, `<name>-Rigidbodies.json` | as-is, costly | Animated or physics schematics run as dynamic toys with a per-schematic sync component (§3.2). Risk: bundles built for other Unity versions may not load in Unity 6000.3. |
| `CullingExtensions` (`SendSpawnMessage` by reflection, raw `ObjectDestroyMessage`) | adapted | Reimplemented over the observer API (§3.8), because raw messages desync Mirror's `observers`/`observing` bookkeeping. `NetworkServer.SendSpawnMessage(NetworkIdentity, NetworkConnectionToClient)` exists as an internal method in the fork's Mirror and is accessible through the publicized reference. A show or hide is a per-player decision that zone culling keeps until the new `ResetSchematicVisibility`/`ResetNetworkIdentityVisibility`; whole schematics are shown and hidden at the paced stream rates. |
| `Patches/AlphaWarheadCanBeDetonatedFix` | adapted, optional | The fork's `AlphaWarheadController.CanBeDetonated(Vector3, bool)` uses `pos.y < 900f` (`FORK/AlphaWarheadController.cs:412`); 14.x uses `GetZone() != Surface`. The port keeps ProjectMER's intent: positions outside any room and below 900 survive. It is behind a config flag. The method has a loop, so Mono will not inline it. |
| `Patches/ExperimentalWeaponLockerGlobalLimitsPatch` | dropped | No `ExperimentalWeaponLocker` in `FORK/MapGeneration.Distributors/`. |
| Double `Harmony.PatchAll()` in `Enable` | fixed in port | ProjectMER calls `PatchAll` twice on two instances (`PMER/ProjectMER.cs`), so every patch is applied twice. |

---

## 2. Schematic and map format compatibility

### 2.1 Schematic JSON (`Schematics/<name>/<name>.json`, `SchematicObjectDataList`)

- **Format.** `RootObjectId`, then `Blocks[]` of `{Name, ObjectId, ParentId, AnimatorName, Position,
  Rotation, Scale, BlockType, Properties}`, unchanged. Block type numbers 0-7 are the same in MER 13.2
  and ProjectMER (`MER132/API/Enums/BlockType.cs`), so both generations of exported schematics load.
- **Parser.** Utf8Json becomes Newtonsoft.Json. `Properties` is `Dictionary<string, object>`, which
  Newtonsoft fills with `JObject`, `JArray`, `long` and `double`. ProjectMER tests for
  `IDictionary<string, object>` (`SchematicBlockData.GetVectorProperty`, `StructExtensions.ToVector2`)
  and uses `Convert.ToXxx`. A small converter therefore turns `JObject` into
  `Dictionary<string, object>` and `JArray` into `List<object>`. Vector3 is read from `{x,y,z}`.
- **Cache.** Parsed data is cached by path, file length and modified time, with the build plans derived
  from it (§3.9). ProjectMER re-reads and re-parses the JSON on every spawn
  (`MapUtils.GetSchematicDataByName`). Schematic loads parse on a worker thread (`SchematicLoader`,
  §3.5); `MapUtils.GetSchematicDataByName` stays synchronous for plugins.
- **What loads unchanged:**
  - Primitive (with or without `PrimitiveFlags`; the negative-scale convention is kept)
  - Light (old `Shadows` bool and new `ShadowType` forms; spot data is ignored)
  - Pickup (numeric or named `ItemType`)
  - Workstation
  - Empty
  - `-Rigidbodies.json`
  - animator bundles named in `AnimatorName`
- **Skipped with one warning per type:** Text, Interactable, Waypoint, Triangle, plus the
  Schematic/Teleport/Locker block types that ProjectMER already does not build. MER 13.2's
  `-Teleports.json` is ignored, as in ProjectMER.
- **Item names.** Unknown items are skipped with a warning. Missing in the fork: `Lantern`,
  `AntiSCP207`, `SCP1344`, `GunA7`, `GunFRMG0`, `GunSCP127`, `Coal`, `SpecialCoal`, `Snowball`,
  `SCP1509`, `MarshmallowItem`, `Scp021J`, the custom keycards, `SurfaceAccessPass`, `SCP1507Tape` and
  `DebugRagdollMover`. Renamed: `KeycardMTFPrivate/Operative/Captain` are
  `KeycardNTFOfficer/Lieutenant/Commander` in the fork. That is a name-only difference; numeric values
  are equal (comparison of `FORK/ItemType.cs` and `OFFICIAL/ItemType.cs`). An alias table maps the 14.x
  names.

### 2.2 Map YAML (`Maps/<name>.yml`, `MapSchematic`)

- **ProjectMER maps load.** Same `YamlParser` settings (underscored names, `IgnoreUnmatchedProperties`,
  `VectorConverter`). Every dictionary stays in `MapSchematic`, including unsupported ones, so
  `load` → edit → `save` keeps their entries byte-for-byte in content.
- **Room ids.** Ids are `"{FacilityZone}_{RoomShape}_{RoomName}"`. `FacilityZone` and `RoomShape` are
  identical in the fork. `RoomName` lacks `Hcz127`, `HczAcroamaticAbatement`, `HczRampTunnel` and
  `HczWaysideIncinerator` (comparison of `FORK/MapGeneration/RoomName.cs` and the official file).
  `RoomExtensions.GetRooms` uses `Enum.Parse` and would throw for the whole map, so the port uses
  `TryParse`: an unknown room skips that object with a warning. Objects in `Outside` use world
  coordinates. Surface geometry may differ between 14.x and the fork, so surface maps need a visual
  check.
- **Enum values.** `DoorPermissionFlags` names and values match `KeycardPermissions` except `All`,
  which maps to the union of all flags. `LightShadows` becomes a bool (`!= None`). `LightType` and
  `LightShape` are kept in the model but ignored when spawning.
- **Maps from the EXILED-based MER 13.2 are not loadable.** Their format uses lists, EXILED `RoomType`
  and a different schema (`MER132/API/Features/Serializable/MapSchematic.cs`). They fail with a YAML
  error, as they already do in ProjectMER. A converter is an optional later step.

### 2.3 Handling unsupported content

- `SpawnOrUpdateObject` returns `null` for unsupported types instead of throwing.
  `MapSchematic.SpawnObject` already skips `null` results.
- One `Logger.Warn` per (map or schematic, type) per load, with a count. For example:
  `Schematic "Bunker": skipped 12 Text blocks (TextToy is not available in the Carl Mod client)`.
- `SchematicSpawning` handlers still receive the full, unfiltered `Data`.
- `mp create <unsupported type>` replies with the reason instead of spawning.

---

## 3. Mobile performance design

### 3.1 Cost model (fork code and fork Mirror)

SyncVars per toy:

- `AdminToyBase`: 5 (`Position`, `Rotation`, `Scale`, `MovementSmoothing`, `IsStatic`)
- `PrimitiveObjectToy`: 5 + 2 (`PrimitiveType`, `MaterialColor`)
- `LightSourceToy`: 5 + 4
- `ShootingTarget`: 5 + 1

Messages, from `Mirror/GeneratedNetworkCode.cs: _Write_Mirror.SpawnMessage`,
`Mirror/NetworkBehaviour.cs: Serialize/SerializeObjectsDelta`, `Mirror/Compression.cs: CompressVarUInt`,
`Batcher.MessageHeaderSize` and the toy `SerializeSyncVars` methods:

| Message | Bytes on the wire (approx.) | Breakdown |
|---|---|---|
| Spawn, primitive | ~103 | Batch size varint 1 + message id 2 + netId varuint 2-3 + flags 1 + sceneId 1 + assetId 4-5 + pos/rot/scale 40 + payload length 1 + payload 49. The payload is component mask 1 + safety byte 1 + `AdminToyBase` 30 (12+4+12+1+1) + type varint 1 + `Color` 16. |
| Spawn, light | ~111 | Payload 57: 30 + 4 + 4 + 16 + 1, plus mask and safety bytes. |
| Hide or destroy | ~6 | `ObjectHideMessage`/`ObjectDestroyMessage` with the netId only. |
| Delta, moving primitive (position + rotation) | ~35 | Includes a fixed 8-byte `syncObjectDirtyBits` `WriteULong` that this Mirror writes for every dirty component, and two dirty-mask varints. |

Bandwidth and server CPU:

- Mirror's default `NetworkServer.tickRate` is 60. `NetworkManager.sendRate` overrides it from the
  scene (`Mirror/NetworkManager.cs:221`); log the actual value at runtime. NetworkBehaviour
  `syncInterval` defaults to 0, so a dynamic toy that changes every frame costs about 35 B × 60 ≈
  2.1 KB/s per observing player. 50 animated blocks cost about 100 KB/s per player.
- On every broadcast tick, `NetworkServer.BroadcastToConnection` walks every observed identity of every
  connection, even ones that are not dirty. That is O(objects × players) per tick: 3000 toys × 30
  players means 90k iterations per tick, an estimated 2-4 ms at 60 Hz. Fewer networked objects and
  smaller observer sets also save server CPU.
- A new player receives every visible spawned object in one go
  (`Mirror/NetworkServer.cs: SpawnObserversForConnection`). 2000 primitives come to about 206 KB.

Client cost per primitive:

- On spawn: instantiate the prefab, then `CreatePrimitive` twice for non-sphere types (once from the
  initial-state hook, once from `Start`). Then a convex `MeshCollider` cook when collidable, and two new
  `Material`s.
- Per frame while dynamic: a `LateUpdate` lerp that writes the transform. That also moves a static
  collider, which PhysX handles expensively.
- Rendering: one draw call per pass (fact 5).
- These client costs must be measured on the Android target (§5.3).

### 3.2 Flattened hierarchy and static toys

Rule: **no networked object has a server-side parent at spawn time.** This covers toys, pickups, doors
and structures.

- `SchematicObject.Init` builds the hierarchy from server-only anchors: plain GameObjects carrying the
  block's local transform, created only where needed (blocks with children, blocks that produce
  nothing on the client, and every block of an animated or physics subtree). It takes each networked
  block's world transform from Unity: `position`, `rotation` and `lossyScale` of the block's anchor, or
  of one reusable scratch transform placed under the parent anchor for leaves without an anchor. The
  plain products (`parent.rotation * localRotation` and the diagonal of `inverse(R) * M`) are not what
  Unity reports once a parent has negative scale components: a server test with 200 random hierarchies
  found up to 180° and 73 units of difference. It then instantiates the toy at root level with that world
  transform and sets `NetworkPosition`, `NetworkRotation` and `NetworkScale` to the encoded values
  (§3.3) before `NetworkServer.Spawn`.
- `ObjectFromId` keeps pointing at the anchors, so the animator, rigidbody and `AnimationController`
  APIs see the same structure as in ProjectMER.
- Each toy gets a small `MerBlockLink` component pointing to its `SchematicObject`/`MapEditorObject`.
  Tool-gun raycasts and `TryGetComponentInParent` resolve through it.
- **Static (default).** Blocks are `IsStatic = true` unless one of these holds:
  - the block is in the subtree of a block with an animator or a rigidbody entry (only those
    subtrees move; the rest of an animated schematic stays static)
  - the API switched the schematic to dynamic (`SchematicObject.IsStatic = false`)
  - `honor_static_property` is on and the block has an explicit `"Static": false`

  ProjectMER treats a missing `Static` property as dynamic (`SchematicBlockData.Create`). The port
  inverts that through `static_by_default`, because exported schematics rarely move. Exporters also
  write `"Static": false` for blocks that never move: across the 57 AutoEvent ProjectMER schematics,
  9054 primitive and light blocks say `false`, 4513 `true` and 12936 have no property. Honoring
  `false` would make all 2558 networked blocks of Skeld dynamic, while only 286 of them sit under its
  33 animated roots. So the property only counts with `honor_static_property`. Standalone map
  objects (`primitives`, `lights`, `shooting_targets`) are static as well. ProjectMER never makes them
  static.
- **Editing a static toy** (modify, position, rotation, scale, or a `SchematicObject` setter). Update
  the transform and SyncVars, then call `NetworkServer.SendSpawnMessage(identity, conn)` for each
  observer. The client's `FindOrSpawnObject` finds the existing netId and `ApplySpawnPayload`
  overwrites the transform and state at full precision. That is one message of about 103 B per toy,
  with no destroy, no re-instantiation and no material leak. A change to collidability needs a real
  respawn (`UnSpawn` + `Spawn`), because the collider is decided only in `SetPrimitive`.
- **Dynamic subtrees** (animated or physics). The toy components are disabled on the server (§3.4).
  One `SchematicSync` component per schematic copies anchor world transforms into the toys' SyncVars
  in its `LateUpdate`, only for anchors whose `transform.hasChanged` is set. It uses `lossyScale`,
  which avoids MER 13.2's root-scale approximation (`MER132/Patches/UpdatePositionServerPatch.cs`) and
  any Harmony patch on `AdminToyBase`. Set `syncInterval = dynamic_toy_sync_interval` on those toys.
  Add a kinematic `Rigidbody` server-side to moving collidable toys of animated subtrees so PhysX does
  not rebuild static colliders. A rigidbody entry goes on the block's anchor, and every collidable
  primitive of its subtree gets a copy of its collider (the same convex mesh; a thin box for planes and
  quads) on its own anchor: the anchors below the body make its compound collider, as the parented toys
  did in ProjectMER. The toys of the subtree follow their anchors with their server colliders switched off
  (`SchematicSync` checks every frame, because the server builds a toy's primitive in `Start` and again
  when the spawn reaches its host client), and the body stays kinematic until those toys are spawned, so it
  never overlaps colliders of its own toys. Rigidbody entries of pickups go on the pickup's own body.

### 3.3 Collider and visibility encoding

| ProjectMER `PrimitiveFlags` | Static toy | Dynamic toy |
|---|---|---|
| `Visible\|Collidable` (3) | transform scale `\|s\|`, rotation `R·Q`; `NetworkScale = \|s\|` | same |
| `Visible` (2) | transform scale `\|s\|`, rotation `R·Q`; `NetworkScale = −\|s\|` | transform and `NetworkScale = −\|s\|`, rotation `R·Q′` |
| `Collidable` (1) | `invisible_collider_mode: Transparent`: as (3) with `MaterialColor.a = 0`. `Skip`: not spawned | same |
| `None` (0) | not networked (server anchor only) | same |

`Q` turns the sign pattern of `s` into a rotation: take `D = diag(sign(s))`, flip the x sign if an
odd number of components is negative, and the result is the identity or a 180° turn about X, Y or Z.
`Q′` does the same for `−sign(s)`. For a positive `s`, `Q` is the identity and `Q′ = Rx(180°)`. The
LabAPI `PrimitiveObjectToy` wrapper implements this (`Flags`, `Scale`, `Rotation` and `IsStatic` all
re-encode), and `ToyFactory` builds on it. A server self-test checked 864 combinations (3 flag sets ×
static/dynamic × 6 primitive types × 8 sign patterns, plus flag/static toggles and setter changes): the
rendered matrix always equals the requested one up to a local X mirror, and the `Scale` SyncVar always
gives the intended collider.

Why this works:

- **Mixed signs are exact too.** Plain `|s|` would drop a mirror on Y or Z, which flips which side
  of a one-sided Plane or Quad is visible. The rotation `Q` keeps it.
- **The client gets no collider when every `Scale` component is ≤ 0.** For a static toy, the client
  never applies the `Scale` SyncVar to the transform; only the spawn message's `localScale` is used. So
  the visual scale (positive) and the collision decision (negative SyncVar) are independent. That makes
  the encoding exact, including one-sided Quad and Plane.
- **Dynamic toys must render with the negative scale.** `R·Rx(180°)·(−S) = R·diag(−sx, sy, sz)`, which
  is the original mirrored on local X. All six Unity primitives are symmetric under x → −x, and the X
  mirror keeps the Quad normal (−Z) and the Plane normal (+Y). Unity flips the winding for a negative
  determinant, so the result looks identical.
- **The server makes the same collider decision.** `Start` → `SetPrimitive` reads the same `Scale`
  SyncVar, so server physics (bullets, pickups, tool-gun raycasts) behaves the same as the client.
- **Non-collidable objects still need to be selectable** by the tool gun. While a tool gun exists (or
  another owner, such as the indicator toggle, holds `EditingColliders`), the editor adds a server-only
  trigger `BoxCollider` to the server-side primitive of every non-collidable MER primitive, on the Ignore
  Raycast layer, which the game's own queries leave out (§4). It is removed when editing stops.
- **Invisible colliders are not cheap.** A transparent primitive still renders, with blending and
  overdraw that matter on tile GPUs. The port warns when a load has more than 50 transparent
  primitives.

### 3.4 Server-side `LateUpdate`/`Update`

- **Static toys.** After the toy's server `Start` has created its primitive and collider, set
  `enabled = false`. Check that the publicized `_spawnedPrimitve` is not null, one frame after spawn, in
  the spawn-queue coroutine. That removes the `LateUpdate` call from Unity's per-behaviour loop. Mirror
  serialization does not check `enabled`: `Mirror/NetworkIdentity.cs` and `NetworkBehaviour.cs` have no
  `enabled`/`isActiveAndEnabled` checks.
- **`LightSourceToy.Update`** copies four SyncVars into the server's `Light` every frame. It does no
  rendering on a `-nographics` server, so disable it for all MER lights. When a light is edited, the
  SyncVars change and the client's own `Update` applies them.
- **Do not disable** `ShootingTarget`, doors, lockers, workstations, or toys that a plugin made dynamic
  through the API. `SchematicSync` drives dynamic schematic toys instead.
- **The saving is per-component call overhead only.** `UpdatePositionServer` already returns early for
  static toys. The estimate is roughly 0.05-0.1 µs per behaviour per frame, about 0.5 ms per frame at
  5000 toys.

### 3.5 Loading and spawn batching

A schematic load never builds the whole schematic in one frame:

- **Parse and plan off the main thread.** `SchematicLoader` (`Features/Serialization/`) reads
  `<name>.json` (streamed into Newtonsoft) and `<name>-Rigidbodies.json` on a thread-pool thread and
  runs `SchematicOptimizer.Build` (§3.9) there. Both are pure data: no Unity calls (colours are parsed
  without `ColorUtility`; named colours are resolved on the main thread when the block is built) and no
  plugin state. The main thread polls the job each frame and publishes the results into the
  `SchematicJson` cache. Concurrent loads of one file share a job; a cached file and plan complete in
  the same frame.
- **Build across frames.** `SpawnQueue` also drives `SchematicObject` builders. After the frame's
  spawns, the rest of `spawn_time_budget_ms` goes to the builders, oldest first, each making at least
  one step: prepare (unsupported-type warnings, light admission, rigidbody entries), animator bundles
  (one block per step), blocks (anchor and/or toy, one per step), merged blocks, then the dynamic setup
  and animators. About 100 blocks fit into 3 ms.
- **API consistency.** `SchematicSpawning` is raised before the build: during the spawn call when the
  file is cached, otherwise once the worker has parsed it, and cancelling it then destroys the root
  returned earlier (and removes it from its map). `SchematicSpawned`/`IsSpawned` mark that every server
  object exists, `SchematicBuilt`/`IsBuilt` that the spawn group drained. Block accessors (§1.8) call
  `EnsureSpawned`, which waits for the worker and finishes the build synchronously, so ProjectMER-style
  plugin code (`ObjectSpawner.SpawnSchematic(...).AttachedBlocks`) still sees a complete schematic and
  pays the old hitch only when it asks for blocks at once.
- **One warning per type per load.** A build's `UnsupportedContent` scope is suspended between frames
  and resumed for each step (`UnsupportedContent.Suspend`/`Resume`).
- **Light reservations.** Lights admitted under `max_lights` count against it until they are created
  (`Budget.ReservedLights`), so builds that overlap in time do not over-admit.

Networking:

- **One global `SpawnQueue`** holds prepared (instantiated, positioned, SyncVars set) objects waiting
  for `NetworkServer.Spawn`. An MEC coroutine on `Segment.Update` drains it each frame, up to
  `spawn_max_per_frame` (10) or until `spawn_time_budget_ms` (3 ms, measured with `Stopwatch`).
- **Priority:**
  1. collidable blocks, nearest to any player first, so floors appear before decoration
  2. visible blocks
  3. lights
  4. pickups
- **Each spawn costs one payload serialization per ready connection**, inside `RebuildObservers` and
  `SendSpawnMessage`. The budget therefore scales with player count; the time cap handles that.
- **Unload** cancels any pending entries of that map or schematic before destroying spawned ones.
  Destroys are cheap at about 6 B each, and are batched with the same coroutine at 4× the spawn limit.
- **Throughput.** At 10 per frame and 60 Hz, a 1000-block schematic streams in about 1.7 s, about
  600 objects/s or 62 KB/s per player; the client instantiates 10-20 primitives per frame instead of 1000 at once.
  §5.3 compares 10, 20 and 50 per frame on the client: at 20 the worst client frames during a 2000-cube load are about
  50% longer than at 10, at 50 three to four times longer. The server-side numbers below were taken at 20 per frame.
- **Measured on the server** (no client connected; 60 Hz server; JIT warmed by one earlier load).
  "Worst frame" is the longest server frame interval (Stopwatch between frames) from the load command
  until the queue drained; "synchronous" is the phase-A build, which parsed and built inside the load
  command:

  | Map | Synchronous build, cold / warm cache | Off-thread parse, incremental build, cold / warm |
  |---|---|---|
  | Skeld (2923 ProjectMER objects) | 245-274 ms / 206-300 ms | 42-50 ms / 42-47 ms (one GC each) |
  | Grid2000Static (2000 cubes) | 209 ms / 183 ms | 46-52 ms (GC) / 18.5-50 ms |
  | Shipment (1130) | 88 ms / 99 ms | 19-20 ms / 45-46 ms (GC) |
  | Jail (315) | 48 ms / 36 ms | 19 ms / 20 ms |
  | Battle, 35Hp, DeathParty | 20-24 ms / 20-23 ms | 18-19 ms / 18 ms |

  Every remaining frame above 20 ms coincides with a garbage collection. Carl Mod's Mono uses the
  non-incremental Boehm collector (`GarbageCollector.isIncremental` is false); a full collection takes
  25 ms at an 85 MB live heap, and a Skeld load allocates about 36 MB of managed memory (about 15 KB
  per toy, mostly in toy creation), so a 2500-block load triggers about one collection. The synchronous build had the same collections inside
  its load frame. Loads without a collection stay below 20 ms (Skeld: slowest frame 16.9 ms).
  For Skeld the worker parses in 49-52 ms and plans in 15-45 ms; the main thread builds for 60-105 ms
  spread over 21-35 frames (slices of 3-5 ms), all server objects exist 0.36-0.54 s after the load, and
  the last block reaches the players after 2.1-2.4 s (1100-1150 objects/s, 20 per frame). The 2000-cube
  grid streams in 1.7 s. Unloading during the parse leaves nothing behind (0 networked, 0 reserved
  lights); unloading during the build drops the queued rest (1016 of 2447 after 0.15 s); a round
  restart during the build logs no errors.
- **ProjectMER's synchronous behaviour is replaced.** It spawns every block immediately inside
  `CreateObject` (`PMER/Features/Objects/SchematicObject.cs`). Its recursion is also O(n²):
  `blocks.Find`, `FindAll` and a LINQ `parentSchematics` array per block. The port's plan holds the
  reachable children of each object id (built once, O(n)).

### 3.6 Lights

- **Shadows off.** `allow_light_shadows: false` forces `LightShadows = false` on every MER light,
  including old schematics with `"Shadows": true` and maps with ProjectMER's default `Shadows: Hard`.
  A shadowed point light renders a 6-face shadow cubemap each frame and adds a shadow-caster pass for
  every primitive in range. `CreatePrimitive` renderers cast and receive shadows by default.
- **`max_lights: 16`** across all loaded maps and schematics. Beyond the cap, the lowest
  `intensity × range` lights are skipped with one warning. The port also warns once when more than 8
  are loaded. In the built-in forward path, every pixel light that touches a primitive adds a draw
  call. Toy lights are not linked to `CullableRoom`s, so the fork's `AutoToggleLight` room culling
  never turns them off.
- **`max_lights_per_schematic: 4`.** Each schematic's plan keeps only its strongest lights (§3.9), so one
  light-heavy schematic (Skeld has 135) cannot take the whole `max_lights` budget.
- **`Range` is not clamped**, but a range above 30 m triggers a warning: the light touches many
  objects.
- **Intensity scale.** ProjectMER content is authored for official SL, whose lights are HDRP lights with physical
  intensities (`HDAdditionalLightData` in `OFFICIAL/RoomLight.cs`); exported lamps commonly have intensity 20-100 and
  the same range. Carl Mod renders with the built-in pipeline, where 1-2 is a normal light. On the Android client,
  Shipment's five intensity-60 lights turned every surface white; intensities 1-2 gave the intended colours, 4 already
  washed out. `light_intensity_scale` (0.025) multiplies every MER light's intensity, so 60 becomes 1.5.

### 3.7 Budgets and warnings

- Per schematic: warn above `primitive_warn_per_schematic` (300) networked blocks after simplification.
  The warning shows the count of transparent and dynamic blocks.
- Global: warn when total networked MER objects exceed `networked_warn_total` (1500). Refuse further
  spawns above `networked_hard_cap` (4000), with an error and a `mp stats` hint.
- Rationale: every visible primitive is at least one draw call, and the game's own scene already uses
  much of a low-end phone's draw-call budget. The working target is **≤ 150 MER primitives visible
  from any one spot**. The defaults are starting points for §5.3, not measured limits.

### 3.8 Per-player and per-zone culling with Mirror observers

Implemented in `Features/Mobile/MerVisibility.cs` with `VisibilityEntry`, `VisibilityViewer`, `VisibilityRoot` and
`VisibilityAudience`, without Harmony and without replacing `NetworkServer.aoi`.

**Mirror mechanics** (fork Mirror):

- Set `identity.visibility = Visibility.ForceHidden` before `NetworkServer.Spawn`. With
  `aoi == null`, `RebuildObserversDefault` then adds no observers, and `SpawnObserversForConnection`
  skips the object for joining players (`Mirror/NetworkServer.cs`).
- Show for one connection: `identity.AddObserver(conn)`. It adds to `identity.observers` and calls
  `conn.AddToObserving`, which calls `ShowForConnection` → `SendSpawnMessage`. `ShowForConnection` sends nothing to a
  connection that is not ready, so not-ready connections are never made observers.
- Hide for one connection: `identity.RemoveObserver(conn)` plus `conn.RemoveFromObserving(identity,
  false)`, which sends `ObjectHideMessage`. The client handles it in `NetworkClient.OnObjectHide` and
  destroys the object.
- All of these are `internal` in the fork's Mirror and compile through the publicized reference.
  Unspawn and destroy still reach exactly the current observers, and `SetClientNotReady` and a disconnect clear them
  (`RemoveFromObservingsObservers`).
- The server runs as host: `NetworkServer.localConnection` (id 0) is ready and in `connections`. It is made an observer
  of every MER object, as Mirror's default would, so host-side SyncVar hooks (`NetworkClient.spawned`) keep running.
  Only remote connections are managed.

**Objects** (`managed_visibility: true`). Every MER object is spawned `ForceHidden` and gets a `VisibilityEntry` (group,
zone, audience, explicit per-player decisions); Mirror's `identity.observers` stays the record of who receives it.

- Spawns from the spawn queue are shown at once to every ready player who should see them (the queue paces them).
- In-place resends go to the current observers except the server's host connection. A static object moved into another
  zone is hidden from the players of the old zone instead of being resent to them. The host is skipped because the fork's
  Mirror deserializes a spawn payload into the server's own object when the host client handles it
  (`Mirror/NetworkClient.cs: OnHostClientSpawn`), one frame after it was sent: a resend followed by SyncVar writes in the
  same frame would be reverted on the server (for example a selection box made dynamic for a grab right after its
  resend). LabAPI also re-serializes the host's spawn payload at handling time (`HostSpawnPayloadPatch`,
  docs/compatibility.md).
- Respawns keep the visibility: `MerVisibility.Respawn` (doors, collider changes) and plugin respawns through the LabAPI
  wrapper (`PrimitiveObjectToy.Flags`), because `AdminToy.Respawn` adds the previous observers of a `ForceHidden` toy
  back.
- `managed_visibility: false` spawns with Mirror's default (everyone, joiners in one burst); admin-only objects stay
  admin-only.

**Players.** Remote connections are tracked through `ReferenceHub.OnPlayerAdded`/`OnPlayerRemoved` and
`PlayerRoleManager.OnRoleChanged`, with the 1 s poll as a safety net.

- **Join streaming.** When a player's connection is ready, it receives the existing objects as a paced stream:
  collidable, then visible, lights and pickups, each nearest first (the spawn queue's order). Doors come first, all at
  once (see the waypoint note below).
- **Pacing.** Each player gets at most `spawn_max_per_frame` spawns per server frame; all streams together stay within
  `spawn_time_budget_ms`, round-robin across players. While the spawn queue has pending spawns, streams use a quarter
  of both. Hides are paced at four times the limit.
- A player whose connection stops being ready is dropped from all streams and streamed again from scratch once ready.
  A disconnect drops its pending work, its audience memberships and its explicit decisions.
- Round restart clears all state. Waiting for players does not: plugins handling that event before ProjectMER may
  already have spawned MER content for the new round.

**Admin-only objects** (indicators, selection box, grab proxy) are spawned with `adminOnly: true`. A
`VisibilityAudience` (a set of players) shows its objects to its members only; `MerVisibility.ShowTo(identity,
player)` shows one object to one player. Nobody else receives a spawn, hide, destroy or state message for them.
Indicators use one audience: the players who turned them on (§1.4).

**Explicit decisions.** `MerVisibility.ShowTo`/`HideFrom` and the `CullingExtensions` (`SpawnSchematic`,
`DestroySchematic`...) record a per-player decision that zone culling does not override until `ClearOverride`
(`ResetSchematicVisibility`). Whole schematics are shown and hidden at the paced rates.

**Zone culling** (`zone_culling`, read once per round):

- The unit is a top-level spawn group: a map load, or a schematic spawned outside a map. It is culled when it has at
  least `zone_culling_min_objects` (150) networked objects. A unit that is already that large when its first object
  spawns is culled from the start. Any other unit is culled provisionally while it loads (schematics may build over
  several frames): its objects reach only players in their own zone until nothing of it is queued, its schematics report
  `IsBuilt` and no object was added for a poll interval. A unit still below the threshold is then shown in every zone,
  which only adds objects for the other zones' players, so nobody receives objects that are hidden again. A settled
  unit stays culled until it is empty; a small settled unit that grows past the threshold (tool gun) becomes culled,
  the only case that hides objects players already have.
- An object's zone is that of its schematic's root (a schematic is never split), otherwise its own position.
  `SurfaceFacility` uses Surface for `y ≥ 900` (the game's own threshold, `FORK/AlphaWarheadController.cs:412`) and
  Facility otherwise. `PerZone` uses the zone of `RoomIdUtils.RoomAtPosition`, Surface for `y ≥ 900` outside rooms,
  and never culls objects outside rooms below that.
- **Doors are never culled.** Door prefabs carry a `NetIdWaypoint`. Each side numbers its net waypoints by sorting the
  ones it has by netId (`RelativePositioning/NetIdWaypoint.cs: Update`), and player and pickup positions travel
  relative to a waypoint number. A client missing a MER door would number the later MER doors differently and place
  objects near them wrongly. Vanilla doors have lower netIds, so only MER doors are affected. The numbering only
  happens in the frame after a waypoint starts, so the server must start a MER door's waypoint after the door has a
  netId and renumber after respawns and destroys (`Features/Mobile/MerWaypoints.cs`, docs/compatibility.md `doors`).
- A player's zone comes from its body. Spectators and Overwatch use the spectated player (SCP-079's camera when they
  spectate SCP-079), SCP-079 its camera. Players without a body only gain zones until they spawn again.
- A 1 s MEC poll reads positions; there is no per-frame work. A player inside an elevator chamber (bounds plus 1 m)
  whose floors are in different zones sees every zone it serves (Gate A/B; with `PerZone` also the LCZ checkpoint
  elevators). With `PerZone`, the `HczCheckpointToEntranceZone` room shows HCZ and EZ. The stream of a newly visible
  zone is ordered around the player, the elevator floor or the teleport destination in that zone.
- A zone stays visible for 5 s after the first poll that no longer needs it, so 5 to 6 s after the player left it.
- MER teleports and MER player spawnpoints call `MerVisibility.Prefetch` before moving the player: the destination
  zone is added and its nearest objects (one `spawn_max_per_frame` batch) are sent at once; the rest is streamed.

**Cost of a transition** (one player changes zone, group of N primitives):

- about 103 B × N to that player; N = 1000 is about 103 KB, about 0.85 s at the spawn budget
- about 6 B × N of hide messages
- N client instantiations
- **2 × N leaked `Material`s** on the client (fact 5) until the next scene load

**Benefit while the player stays in the other zone:**

- N fewer draw-call candidates and N fewer transforms, renderers and colliders on the phone
- N fewer entries in that connection's per-tick broadcast walk on the server

**Measured on the server** (phase B, port 7794, a server copy under `../labapimobile/.runtime/`). Test players are server dummies; a Harmony
prefix on `ServerDummyConnection.SendToTransport` parses the batches the server sends each of them. Map: a 500-cube
grid on the Surface, a 500-cube grid and 200 primitives in LCZ, and 3 doors; default limits (20 per frame, 3 ms).

- Load with one player per zone: 503 objects to the Surface player, 703 to the Facility player, 1203 to the host, no
  hides. Every player has all doors.
- Join: a player joining in the Facility gets the 33 unculled objects (doors at once) in 2 frames, then its zone (700)
  in 0.57 s over 35 frames at 20 per frame, collidable first and nearest first. Without managed visibility the same
  player gets 1768 objects in one frame.
- Moving a player to the Surface: the new zone starts streaming at the next poll (0.23-0.56 s measured), 500 objects
  in 0.42 s; the 700 Facility objects are hidden 5.2-5.3 s after the move, 80 per frame. Other players receive
  nothing.
- Gate A chamber: both zones while inside; the other zone is hidden 5.1 s after leaving. Teleport: 20 objects within
  3.6 m of the destination are sent in the trigger frame, all 500 within 0.39 s.
- Spectators: following a Surface player adds the Surface (500 shows); switching to a Facility target and waiting
  6.5 s hides nothing; respawning in the Facility hides the Surface 5 s later.
- Indicators: turning them on sends 16 parts to that player in one frame and 0 messages to the 3 others; a second
  admin gets the same 16; turning them off hides them; when the last viewer leaves they are destroyed.
- A wrapper respawn (`Flags`) of a culled toy: its one observer gets a destroy and a spawn, the others nothing.
- Not ready: observers cleared, objects spawned meanwhile not sent, 831 objects streamed in 0.70 s once ready again.
  A disconnect 0.05 s into a join stream leaves 0 stale observers. A round restart leaves 0 managed objects.
- `PerZone`: players in LCZ, HCZ, EZ and on the Surface each get only their zone's culled objects; the Gate A
  chamber shows EZ and Surface, an LCZ checkpoint elevator LCZ and HCZ, the `HczCheckpointToEntranceZone` room HCZ
  and EZ. Objects outside every room below the surface are not culled.
- A round reset with players present: the next load reaches them directly as it spawns.
- Loads below the threshold (35 and 100 objects) reach the other zone's players 1-2 s after they settle. A map of 10
  primitives plus a 500-block schematic that builds asynchronously is culled with no hides.
- Server cost: slowest stream frame 1.8-1.9 ms (budget 3 ms); slowest poll 0.9 ms with `SurfaceFacility` and 1.2 ms
  with `PerZone`, both in polls that queued a 500-700-object zone.

**Recommendation:**

1. **Always use managed visibility for MER objects** (`managed_visibility: true`), even without zone
   culling, for paced join streaming and admin-only objects.
2. **Zone culling at Surface vs Facility granularity** (`zone_culling: SurfaceFacility`). Transitions happen only
   through the Gate A/B elevators (prefetched) and teleports, a few times per player per round. That bounds both the
   material leak and the bandwidth spikes.
3. **`PerZone` (LCZ/HCZ/EZ) is opt-in.** The HCZ↔EZ checkpoint is walk-through; its room prefetches both zones, but a
   player who crosses it quickly can still see pop-in. **Distance and room culling are not recommended**: room changes
   every few seconds would leak materials and use bandwidth continuously.
4. **Spectators and Overwatch follow the zone of their spectated player, add-only.** Groups are never
   hidden until the player respawns, which bounds the churn caused by switching spectate targets.

### 3.9 Schematic simplification (cached per schematic file and settings)

`SchematicOptimizer.Build` runs on the loader's worker thread (§3.5) and produces the
`SchematicBuildPlan` that `SchematicObject` builds from: an output per block (`None` = server-side
anchor) with the reason it is not networked, the networked flags, the primitive type and colour, the
reachable children, the reachable rigidbody entries, the merged blocks with their source indices, and the
networked counts after each step. Plans are cached with the parse result per `OptimizerSettings`
(`optimize_schematics`, `invisible_collider_mode`, `max_lights_per_schematic`, merging,
`static_by_default`, `honor_static_property`) and the rigidbody file's stamp, so they are rebuilt when a
file or the configuration changes. Source files are never rewritten.

1. **Do not network blocks that produce nothing on the client** (`optimize_schematics`):
   - `PrimitiveFlags.None` primitives
   - primitives with `MaterialColor.a == 0` that are not collidable
   - zero-scale blocks
   - unsupported block types

   They stay as server-only anchor GameObjects. Anchors cost nothing on the client, and plugins
   find marker blocks (often empties or invisible primitives named `Spawnpoint` and the like) through
   `AttachedBlocks`/`ObjectFromId`, so removing them would break those plugins without saving a
   networked object.
2. **Never network empty groups.** Empty blocks are server-only anchors (ProjectMER spawned each one
   as a networked `None`-flag toy).

3. **Anchors only where needed.** Static leaf blocks get no anchor; their world transform comes from a
   scratch transform under the parent anchor (§3.2). Blocks with children, blocks without networked
   output and animated or physics subtrees keep anchors. Discarding the remaining group anchors would
   save only server memory and is not planned.
4. **Light cap.** At most `max_lights_per_schematic` (4; `-1` = no cap) lights per schematic, strongest
   `intensity × range` first, file order on ties. `max_lights` still applies to everything loaded.
5. **Drop exact duplicates** (with merging, below): static primitives whose type, root-space matrix and
   position (quantized to 1e-4), colour and networked flags equal an earlier block's. Partly transparent
   duplicates stay: stacked translucent copies blend into a deeper tint.
6. **Merge cubes and quads** (`merge_blocks`: `Maps` by default, i.e. schematics whose spawn group has a
   map, as for map files, `mp create` and the tool gun; `All` adds schematics spawned by plugins through
   the API; `None`). Plugins that address single blocks of their own schematics keep every block under
   `Maps`.
   - Candidates are static `Cube` and `Quad` blocks (not in an animated or physics subtree, static per
     §3.2) that are opaque (`Visible` and alpha 1) or fully invisible (alpha 0 or no `Visible`).
     Partly transparent blocks are left alone: their internal faces show.
   - The plan computes each block's root-space matrix `M` in double precision from the local TRS chain
     (Unity's `Quaternion.Euler` order, Z then X then Y). A block is a box only if `Rᵀ·M` is diagonal,
     where `R` is Unity's world rotation (the product of the local rotations); sheared blocks are left
     alone. Under a negatively scaled ancestor Unity's world rotation is not that product, so those
     blocks are left alone too.
   - Group key: primitive type, networked flags, transparency class, colour, and a canonical frame:
     the `R·P` with the smallest quantized (1e-5) matrix over the 24 rotations of the cube (cubes), or
     over the 4 rotations about the normal after turning the frame so the visible side faces local −Z
     (quads; the visible normal is `M⁻ᵀ·(0,0,−1)`). A cube turned by 90° therefore merges with its
     neighbours, and quads facing opposite ways never merge.
   - In the group frame each block is an axis-aligned box (a quad is a rectangle at a plane
     coordinate); extents come from whole rows of `Fᵀ·M`, so permuted frames give the right box.
     Greedy sweeps per axis bucket the boxes by their extents on the other two axes (1 mm), sort them
     and merge runs that touch or overlap (within 1 mm; overlaps are unions because only opaque or
     invisible blocks merge). Sweeps repeat until nothing changes; three cyclic axis orders for cubes and
     two for quads are tried and the result with the fewest boxes is kept.
   - A merged block is a static leaf under the root with an exact quaternion rotation and positive
     scale; it covers exactly the space of its sources, which keep their anchors (name, transform,
     `AttachedBlocks`) without a toy. Merging is skipped for a root with non-uniform scale (rotated
     merged boxes would shear differently). `SchematicObject.IsStatic = false` destroys the merged
     blocks and networks the sources and removed duplicates individually before everything becomes
     dynamic; switching back keeps them individual.
7. **Report.** `mp optimize <name> [map|api]` prints the networked count by kind and the estimated spawn
   bytes (`Budget` per-kind sizes, §3.1) after each step, with the reason counts. It answers from the
   cached plan until the file changes.

Measured on the server (map placement, default configuration; ProjectMER count = root + one toy per
block + six per triangle; spawn data per player in KB):

| Schematic | ProjectMER | Not networked | Light cap | Duplicates | Merged | Spawn data |
|---|---|---|---|---|---|---|
| Skeld | 2923 | 2677 | 2546 | 2542 | 2447 (182 blocks into 87) | 295.1 → 246.2 |
| Shipment | 1130 | 1020 | 1019 | 1019 | 970 (98 into 49) | 113.7 → 97.6 |
| Jail | 315 | 294 | 285 | 285 | 284 (2 into 1) | 32.0 → 28.8 |
| Battle | 72 | 69 | 69 | 69 | 65 (8 into 4) | 7.3 → 6.6 |
| 35Hp | 55 | 46 | 46 | 46 | 46 | 5.6 → 4.7 |
| DeathParty | 21 | 19 | 19 | 19 | 19 | 2.1 → 1.9 |
| MergeCases (synthetic fixture) | 71 | 65 | 63 | 62 | 26 (45 into 9) | 7.2 → 2.6 |

Real exports have few equal blocks that share whole faces: Skeld's merges are 180 quads and 2 cubes, and
91 of its 1908 candidates are translucent; Shipment's are 98 cubes. Merging alone saves 3.7% on Skeld and 4.8% on
Shipment; the empty groups and the light cap save more.

Visual equivalence was checked on the server against a reference hierarchy built from the file (what
the port renders for each unmerged source): for every merged toy, every source corner lies inside it
(worst 0.024 mm), the sources cover it completely (cell test on the grid of source boundaries: no
uncovered cell), quads face the same way, and type, colour and flags match. All 87 (Skeld), 49
(Shipment), 1 (Jail), 4 (Battle) and 9 (MergeCases) merged blocks pass, so no surface is lost. The
unmerged static blocks match the reference exactly (Skeld: 2070 static toys, error 0). Save and reload
leave the schematic files byte-identical and give the same counts; animated (Skeld: 33 animators, 286
dynamic toys) and physics schematics (a falling box and its child fall 2.65 m together) behave as before.

### 3.10 Smaller CPU fixes carried in the port

- `IndicatorObject.Update` sets the transform every frame for every indicator. Replace it with updates
  from `UpdateObjectAndCopies`.
- `ToolGunEventsHandler.ToolGunGUI` rebuilds the HUD by reflection every 0.1 s per holder. The port
  compares the HUD inputs every `hud_interval`, rebuilds the text only when one changed and sends it only
  on a change or a keep-alive (§4).
- `IndicatorObject.TryGetIndicator` does a linear `Dictionary.First` search. Add a reverse dictionary.
- No LINQ, closures or string formatting in the spawn-queue, sync or culling paths ([AGENTS.md](../AGENTS.md)
  performance rules).

---

## 4. Tool gun and editing without server-specific settings

**The tool gun item** (`Features/ToolGun/ToolGunItem.cs`).

- It is an **FSP-9**, not ProjectMER's COM-18. The fork's client never dry-fires a semi-automatic weapon
  with an empty magazine: `AutomaticAction.DoClientsideAction` only clears `_hammerReady` and queues
  nothing, so no `Dryfire` request is sent. A server probe of the fork's firearm prefabs found the COM-15
  and COM-18 semi-automatic (`AutomaticFirearm._semiAutomatic`) and the FSP-9, Crossvec, E-11, AK and
  Logicer automatic. The FSP-9 also has a flashlight attachment (side rail), so the mobile light-toggle
  button is shown for it. Its weight does not matter: `WeightToMovementSpeed` gives 0.998 at 2-3 kg.
- Status: 0 rounds with `Cocked | Chambered | MagazineInserted`, plus `FlashlightEnabled` in Create mode.
  `Chambered` is required because the FSP-9 has a bolt lock: an unchambered bolt-lock gun only plays the
  trigger click and sends nothing. The LabAPI dry-fire event needs `Cocked` and 0 rounds
  (`FirearmRequestPatch.WillAuthorizeDryFire`). The event is cancelled, so `Cocked` stays and every tap
  dry-fires again; the hammer resets after each dry fire, so holding the button does not repeat.
- The attachment code is computed once from the firearm: the first attachment of every slot, except the
  flashlight in its slot (5641 for the fork's FSP-9).
- It cannot shoot, reload or be dropped as a real weapon:
  - `Firearm._refillAmmo` is cleared and `AcquisitionAlreadyReceived` is set. A firearm added without a
    pickup is refilled to a full magazine when the client sends `CmdConfirmAcquisition`
    (`Firearm.ServerConfirmAcqusition`). Measured: a plain FSP-9 goes from 0 to 30 rounds, the tool gun
    stays at 0.
  - The fork refuses shots without ammo (`AutomaticAction.ServerAuthorizeShot`). Reload and unload
    requests are cancelled. No ammo is given: the automatic ammo manager's `ClientCanReload` is always
    true, so the reload button works without it.
  - Drop (`CmdDropItem`) is cancelled. Every other way out of the inventory (death, cuffing and plugins
    use `ServerDropEverything`/`ServerDropItem`) passes the fork's static
    `InventoryExtensions.OnItemRemoved`: the tool gun is forgotten and its pickup is destroyed one frame
    later (callers of `ServerDropItem` still use the pickup in that frame).

**Input mapping** (`Events/Handlers/Internal/ToolGunEventsHandler.cs`). Only requests the stock firearm
handler already processes are used (`FirearmBasicMessagesHandler.ServerRequestReceived`, plus
`Inventory.CmdDropItem`). The button behaviour comes from the fork's `MobileTouchController` and
`MobileActionArea`, which the server assembly shares with the client.

| Input (mobile button) | Request / LabAPI event | Action |
|---|---|---|
| Attack (tap) | `Dryfire` / `PlayerDryFiringWeapon` (cancelled) | Create or Delete at the crosshair, Select while aiming. |
| Aim (a toggle on mobile) | `AdsIn`/`AdsOut`, read from `AdsModule.ServerAds` | While aiming, attack selects; aiming at nothing deselects, as in ProjectMER. |
| Inspect | `Inspect` / `PlayerInspectingItem` | Toggles Create ↔ Delete, and the flashlight with it (on: Create). The client inspects only when not aiming. |
| Light toggle | `ToggleFlashlight` / `PlayerTogglingWeaponFlashlight` (`NewState` set to the new mode) | The same toggle. |
| Reload (tap) | `Reload` / `PlayerReloadingWeapon` (cancelled) | Previous entry of the cycle. |
| Throw away | `CmdDropItem` / `PlayerDroppingItem` (cancelled) | Next entry of the cycle. |

- The cycle is every schematic (ProjectMER's type 0) followed by the supported object types in enum
  order: Primitive, Light, Door, Workstation, ItemSpawnpoint, PlayerSpawnpoint, ShootingTarget, Locker,
  Teleport, Quad. Inputs re-read the schematic folder at most every 5 s.
- `AimingWeapon` does not exist in LabAPI. `AimedWeapon` is not subscribed, because a subscriber makes the
  port allocate event arguments for every aim request of every player; the HUD shows the aim state at its
  next check instead.
- Unload (a long reload press on PC) is cancelled and does nothing. Mobile reload is a click
  (`MobileActionArea.UsesClick`), so phones never send it.

**Replacing the SSS dropdown.**

- `mp tg schematic <name|index>` (indices from `mp list`), `mp tg type <type>` and
  `mp tg mode <create|delete>` work without holding the tool gun. Mode, type and schematic are per player
  (`ToolGunState`, a `Dictionary<Player, ToolGunState>`) instead of an SSS setting.
- `mp create <schematic>` is unchanged.

**HUD** (`Features/ToolGun/ToolGunHud.cs`, `ToolGunUI.cs`).

- Up to five short lines in a `TextHint`:
  - the mode with the object type or schematic and its position in the cycle, or, in Delete and Select,
    the object under the crosshair
  - the selected object's id, type and map (`*` when the map has unsaved changes)
  - the grab state
  - the room id, with the index among rooms of that id
  - a reminder of the buttons
- Sizes 80/70/58% (at 50% a line is about 1 mm tall on a phone), pushed below the crosshair with empty lines into the
  free strip between the joystick and the attack button, so the HUD does not cover the object being edited. A `<mark>`
  backdrop does not render in Carl Mod hints.
- Uses `<size>`/`<color>` only and ASCII text. Names are escaped: braces are doubled (`TextHint` formats
  its text) and angle brackets replaced.
- The client drops hints beyond 10 per 5 s (`HintDisplay._showRateLimit = new RateLimit(10, 5f)`), and
  hints stack: a newer hint covers older ones until it expires. So:
  - every `hud_interval` (0.5 s) the inputs are compared (mode, aim, type, schematic, selection, object
    under the crosshair, room, grab); the text is rebuilt only when one of them changed, and at once
    after an input
  - a player gets at most 8 HUD hints per sliding 5 s, at least 0.25 s apart; a change that does not fit
    is sent as soon as it does
  - unchanged text is re-sent as a keep-alive every `max(2 s, 4 × hud_interval)`, with a duration of
    keep-alive + 1 s; `hud_interval` is the check interval, not the keep-alive
  - hiding the HUD sends a blank hint that lasts until the last HUD hint would have expired
- Measured with a dummy player (`Mirror.NetworkDiagnostics`): one hint is about 370 B. Holding the tool
  gun idle sends one every 2.0 s (about 185 B/s). Twelve type changes 0.1 s apart gave at most 7 hints in
  any 5 s window, at least 0.30 s apart. The tick that updates the HUD and boxes of two editing players
  takes 0.01-0.06 ms.

**Selection box** (`SelectionBox.cs`, `ObjectBounds.cs`).

- A translucent cube (α 0.25; cyan, yellow while grabbing) around the selected object, one per selecting
  player. It is a non-collidable static toy spawned with `MerVisibility.Spawn(..., adminOnly: true)` and
  shown to its player with `MerVisibility.ShowTo`. Any other observer except the server's host connection
  is removed with `MerVisibility.HideFrom` at every check, so the box stays private whatever the policy
  for admin-only objects is. Measured with managed visibility: the observers are the host and the owner,
  and no message reaches the other player.
- Bounds are aligned with the object's root. Schematics use their blocks (primitive mesh sizes through
  the encoded toy transforms, lights as points, pickups and structures by their colliders), other objects
  their colliders, and 0.5 m otherwise; 2 % + 6 cm of padding. They are computed when the selection or
  the object's scale changes and kept relative to the root, so a moved or rotated object only moves the
  box: one in-place resend of about 103 B to the owner.

**Selecting non-collidable objects** (`EditingColliders.cs`, §3.3).

- While any tool gun exists, every non-collidable primitive of a loaded map or schematic gets a
  server-only trigger `BoxCollider` on a child of its server-side primitive object, sized to the mesh
  (planes and quads get 5 cm of thickness). They are removed with the last tool gun.
  `EditingColliders.Acquire(owner)` and `Release(owner)` are public, so the indicator toggle can keep them
  as well.
- The triggers (and the indicator roots' triggers) are on Unity's Ignore Raycast layer. Raycasts without a
  mask leave it out, and of the game's layer masks only the tesla gate's player overlap includes it. On the
  Default layer, with `Physics.queriesHitTriggers = true`, they would stop bullets
  (`StandardHitregBase.HitregMask`), line-of-sight and explosion linecasts and SCP-049's corpse ray at
  see-through decor while anyone edits. Unnamed layers are no alternative: raycasts without a mask hit
  them, and pooled game objects use some of them.
- New objects are picked up by a rescan every 2 s and right after a tool-gun create. Handled primitives are
  skipped by instance id, and built schematics whose primitives all existed at an earlier scan are skipped
  whole. Measured with Skeld loaded (2542 networked blocks): the first scan took 2.05 ms over 2543 toys and
  added 197 triggers; later scans took 0.014 ms.
- The tool gun ray takes the nearest solid hit (`QueryTriggerInteraction.Ignore`), or a nearer trigger
  that belongs to MER (editing triggers, indicator roots, teleports; the trigger pass adds the Ignore
  Raycast layer). Other game triggers on the Default/Door/CCTV layers are ignored. This server has
  `Physics.queriesHitTriggers = true`, so ProjectMER's plain raycast stopped at any trigger.

**Precise edits** use Remote Admin and console commands, both available on mobile:

- `mp pos add 0 0.1 0`, `mp rot set …`, `mp mod color #ff0000` and so on are unchanged.
- Add short aliases where missing, for example `mp p a`, `mp r a`. `mp pos grab` has the alias `mp pos g`.
- `select <id>`/`delete <id>` work by id when aiming is fiddly on a touchscreen.

**Grab** (`mp position grab`, `Features/ToolGun/GrabSession.cs`).

- Every 0.1 s the object's origin is placed on the player's view ray at the distance it had when the grab
  began, as in ProjectMER.
- **Toys** (primitives, lights, shooting targets): a static toy switches to dynamic for the grab
  (movement smoothing 60), so each step is a small SyncVar delta that clients interpolate. On release it
  switches back to static and the commit resends it in place once, at full precision.
- **Schematics with at most 50 networked blocks**: `SchematicObject.IsStatic = false` for the grab; the
  blocks follow the root through `SchematicSync`. On release the commit runs first (blocks that follow
  the root schedule no resync), then the schematic switches back to static, which resends every block
  once. Measured with 35Hp (46 blocks): about 29 B per block and step while moving, so about 13 KB/s per
  observer at 10 steps per second, and one spawn message per block and observer on release.
- **Structures** (workstations, lockers) are moved and resent in place every step.
- **Proxy**: larger schematics, doors and server-only objects (teleports, spawnpoints) do not move until
  release; only the player's selection box moves, as a dynamic toy that one player observes (about 35 B
  per step). The object then moves once. Measured: Jail (294 networked blocks, 280 toys) is resent
  exactly once per block and observer (about 28 KB per observer); a door is respawned once. Doors are
  proxied because clients place a door only when it spawns: moving one every 0.1 s would destroy and
  re-instantiate it on every phone at every step.
- The grab ends when the command runs again, the selection changes, the object is deleted, or the player
  dies or leaves. The position is stored relative to the object's room, as in ProjectMER, and every copy
  is updated.

---

## 5. Port plan

### 5.1 Steps

1. **Project skeleton.**
   - `src/ProjectMER/ProjectMER.csproj` targets net48 with LangVersion 12, matching LabAPI-Mobile's `LabApi.csproj`.
   - It references the fork's Managed folder (`CarlManaged` in `Directory.Build.props`): publicized
     `Assembly-CSharp`, `Assembly-CSharp-firstpass` and `Mirror`, plus `DigitalDust` (MEC),
     `CommandSystem.Core`, `NorthwoodLib`, `YamlDotNet`, `Newtonsoft.Json`, and the Unity modules
     (Core, Physics, Animation, AssetBundle).
   - It has a `ProjectReference` to `../labapimobile/src/LabApi` (`LabApiMobileRoot`), plus `Lib.Harmony` 2.3.6.
   - Data goes to `LabAPI-Mobile/configs/ProjectMER/{Maps,Schematics}` through `PathManager.Configs`.
2. **Near-verbatim files** (table 5.2). Fix compile errors against the fork: `KeycardPermissions`,
   `FirearmStatus`, `RoleTypeId`/`ItemType` names, and `CachedLayerMask` in `ToolGunHandler`.
3. **`PrefabManager`** with the fork names and a missing-prefab log. Add `mp prefabs`.
4. **Toy factory and encoding** (new `Features/Mobile/ToyFactory.cs`):
   - flag/scale/rotation encoding
   - static and disable handling
   - world-transform spawn
   - `MerBlockLink`
   - resend-in-place helper
5. **Spawn queue** (new `Features/Mobile/SpawnQueue.cs`) with priority, budgets and cancellation.
   Route `MapSchematic.Reload`, `SerializableSchematic` and standalone objects through it.
6. **SchematicObject and SchematicBlockData rewrite**:
   - anchors
   - O(n) build
   - flattening
   - optimizer hook
   - `SchematicSync` for dynamic schematics
   - `IsBuilt`/`SchematicBuilt`
7. **Data-only unsupported types** and the warning aggregator (new `Features/UnsupportedContent.cs`).
8. **JSON layer** (new `Features/Serialization/SchematicJson.cs`): Newtonsoft settings, the
   `Properties` normalizer, and the parse cache. `SchematicLoader` parses and plans on a worker thread;
   `SchematicColor` parses colours without Unity calls.
9. **Managed visibility** (new `Features/Mobile/MerVisibility.cs`):
   - ForceHidden spawning
   - per-connection show/hide
   - join streaming
   - admin-only objects
   - zone culling with prefetch and hysteresis
   - spectator handling

   Reimplement `CullingExtensions` on top of it.
10. **Tool gun**: `ToolGunItem` (fork firearm state, input mapping), `ToolGunUI`/`ToolGunHud` (mobile HUD),
    `ToolGunEventsHandler` (events), `ToolGunState` (per player), `ToolGunLoop` (one timed coroutine),
    `mp tg schematic`/`type`/`mode`.
11. **Indicators, grab proxy and selection box** through managed visibility.
12. **Budgets, light caps, and `mp stats`/`mp optimize`.** The optimizer (`Features/Mobile/SchematicOptimizer.cs`)
    adds the per-schematic light cap, duplicate removal and cube/quad merging (§3.9); `SchematicObject`
    builds across frames (§3.5).
13. **Patches**: the optional warhead patch with a one-line citation of
    `OFFICIAL/AlphaWarheadController.cs: CanBeDetonated`; drop the experimental-locker patch; a single
    `PatchAll`.
14. **Main-thread queue** for the `FileSystemWatcher`.
15. **Docs**:
    - the ProjectMER section of `docs/compatibility.md`: supported, adapted and absent map object types,
      schematic block types, and schematic API and event differences
    - the user README [`README.md`](../README.md): installation, commands, the configuration reference
      with the mobile options, mobile limits, and notes for plugin developers

### 5.2 File mapping (`PMER/` → `src/ProjectMER/`)

| ProjectMER file | Port |
|---|---|
| `ProjectMER.cs` | Light edits: one `PatchAll`, watcher → main-thread queue, `Version`/`RequiredApiVersion` for the port. |
| `Configs/Config.cs` | Kept, plus the mobile options from §1.7. |
| `Commands/MapEditorParentCommand.cs`, `Map/Load.cs`, `Map/Save.cs`, `Map/Unload.cs`, `Utility/List.cs`, `Utility/Merge.cs` | Verbatim. |
| `Commands/Modifying/Modify.cs`, `Position/Position.cs`, `Position/SubCommands/{Add,Set,Bring}.cs`, `Rotation/{Rotation,SubCommands/Add,SubCommands/Set}.cs`, `Scale/{Scale,SubCommands/Add,SubCommands/Set}.cs` | Verbatim; edits flow through `UpdateObjectAndCopies`, which now resends in place. |
| `Commands/Modifying/Position/SubCommands/Grab.cs` | Rewrite: delegates to `GrabSession` (proxy for large schematics, doors and server-only objects; static↔dynamic switching). |
| `Commands/ToolGunLike/Create.cs` | Small edit: list and accept only supported types; reply with a reason for unsupported ones. |
| `Commands/ToolGunLike/Delete.cs`, `Select.cs` | Verbatim (raycast resolution changes inside `ToolGunHandler`); deselecting reports success. |
| `Commands/Utility/Indicators.cs` | Small edit: per-player toggle. |
| `Commands/Utility/ToggleToolGun.cs` | Small edit: new `schematic`/`type`/`mode` subcommands. |
| `Events/Arguments/*.cs`, `Events/Arguments/Interfaces/ISchematicEvent.cs`, `Events/Handlers/Schematic.cs` | Verbatim, plus the `SchematicBuilt` event. |
| `Events/Handlers/Internal/ActionOnEventHandlers.cs` | Verbatim, plus the unload-path fix. |
| `Events/Handlers/Internal/GenericEventHandlers.cs` | Verbatim (also resets the visibility and queue state). |
| `Events/Handlers/Internal/PickupEventsHandler.cs` | Adapt the firearm refill to `FirearmStatus`. |
| `Events/Handlers/Internal/ToolGunEventsHandler.cs` | Rewrite (§4). |
| `Features/AnimationController.cs`, `ObjectSpawner.cs`, `VectorConverter.cs`, `YamlParser.cs`, `Interfaces/IIndicatorDefinition.cs` | Verbatim. |
| `Features/Enums/*.cs` | Verbatim; values kept for API and YAML compatibility. Unsupported members are documented, not removed. |
| `Features/Extensions/DictionaryExtensions.cs`, `StructExtensions.cs`, `ReflectionExtensions.cs`, `ToolGunExtensions.cs` | Verbatim. |
| `Features/Extensions/RoomExtensions.cs` | `TryParse` for room ids. |
| `Features/Extensions/CullingExtensions.cs` | Rewrite on `MerVisibility`. |
| `Features/Extensions/SSDropdownSettingExtensions.cs` | Removed (no SSS). |
| `Features/MapUtils.cs` | Newtonsoft and the schematic cache; otherwise verbatim. |
| `Features/PrefabManager.cs` | Rewrite (§1.3). |
| `Features/TrianglePrimitiveBuilder.cs` | Removed (unsupported). |
| `Features/Objects/MapEditorObject.cs` | Small edit: resend-in-place path, `MerBlockLink`. |
| `Features/Objects/SchematicObject.cs` | Rewrite (§3.2, §3.5, §3.9). |
| `Features/Objects/IndicatorObject.cs` | Rewrite: no `Update`, per-player visibility, reverse lookup. |
| `Features/Objects/TeleportObject.cs` | Verbatim, plus the zone prefetch call. |
| `Features/Serializable/MapSchematic.cs` | Small edit: keep all dictionaries, spawn through the queue, aggregate warnings. |
| `Features/Serializable/SerializableObject.cs`, `SerializableWorkstation.cs`, `SerializableShootingTarget.cs`, `SerializableTeleport.cs` | Verbatim spawn logic. Indicators (Teleport) use the new indicator factory. |
| `Features/Serializable/SerializablePrimitive.cs` | Rewrite of the spawn body (§3.3); properties unchanged. |
| `Features/Serializable/SerializableLight.cs` | Adapt to the four synced light properties, shadow policy and cap. |
| `Features/Serializable/SerializableDoor.cs` | Adapt the permissions type and `DoorPermissions`; drop the random-state extension; Bulk and Gate → null + warning. |
| `Features/Serializable/SerializableItemSpawnpoint.cs` | Adapt firearm init and the default item; no pickup parenting. |
| `Features/Serializable/SerializablePlayerSpawnpoint.cs` | Indicator rewrite only. |
| `Features/Serializable/Lockers/SerializableLocker.cs`, `SerializableLockerChamber.cs`, `SerializableLockerLoot.cs` | Adapt the permissions type; missing locker types → null + warning. |
| `Features/Serializable/Schematics/SerializableSchematic.cs` | Rewrite: plain root, cached data, queue. |
| `Features/Serializable/Schematics/SchematicBlockData.cs` | Rewrite: block factory per §1.2/§3.3. |
| `Features/Serializable/Schematics/SchematicObjectDataList.cs`, `SerializableRigidbody.cs` | Verbatim. |
| `Features/Serializable/SerializableCapybara.cs`, `SerializableText.cs`, `SerializableInteractable.cs`, `SerializableScp079Camera.cs`, `SerializableWaypoint.cs`, `SerializableTriangle.cs` | Data-only: properties kept, `SpawnOrUpdateObject` returns null + warning, indicator removed. |
| `Features/ToolGun/ToolGunHandler.cs` | Edit: link-based hit resolution, MER-aware trigger raycast, supported-type list; selecting and deleting update boxes and grabs. |
| `Features/ToolGun/ToolGunItem.cs`, `ToolGunUI.cs` | Rewrite (§4). New next to them: `ToolGunState`, `ToolGunHud`, `ToolGunLoop`, `SelectionBox`, `ObjectBounds`, `EditingColliders`, `GrabSession`. |
| `Patches/AlphaWarheadCanBeDetonatedFix.cs` | Adapt to the fork's semantics, behind a config flag. |
| `Patches/ExperimentalWeaponLockerGlobalLimitsPatch.cs` | Removed. |
| new | `Features/Mobile/{ToyFactory,SpawnQueue,MerVisibility,VisibilityEntry,VisibilityViewer,VisibilityRoot,VisibilityAudience,MerWaypoints,SchematicSync,SchematicOptimizer,Budget,MainThreadQueue}.cs`, `Features/Objects/MerBlockLink.cs`, `Features/Serialization/{SchematicJson,SchematicLoader,SchematicColor}.cs`, `Features/Enums/BlockMergeMode.cs`, `Features/UnsupportedContent.cs`, `Commands/Utility/{Stats,Optimize,Prefabs}.cs` |

### 5.3 Verification plan

**Build and install.**

1. Build with `dotnet build ProjectMER-Mobile.sln -c Release --artifacts-path <dir>`.
2. Run LabAPI-Mobile's installer (`../labapimobile/src/Installer`) on a server copy (never `server-original`).
3. Put `ProjectMER.dll` in `LabAPI-Mobile` plugins.
4. Start the server with `../labapimobile/tools/Start-TestServer.ps1 -CommandSession mer` and send commands with
   `../labapimobile/tools/Send-ServerCommand.ps1`. Results are checked in the server log, because the file console
   does not return output.

**Fixtures.** Generated by a script into `.runtime/` and never committed:

- Grids of 100, 500 and 2000 cubes, in static and dynamic variants.
- Every flag combination and every primitive type, including Quad and Plane, with positive, mixed and
  all-negative scales.
- Nested empties 5 deep with non-uniformly scaled, rotated parents, to check the `lossyScale` flatten.
- 4 and 16 lights with and without shadows, plus a spot light (expect a downgrade warning).
- A MER 13.2-era schematic with no `PrimitiveFlags` and named `ItemType`s.
- An animated schematic (bundle) and a rigidbody schematic.
- A ProjectMER map containing every object type, including all unsupported ones.

**Server test setup.** Notes from running the phase A checks:

- With `-key<session>` the file console drops every `ServerConsole` line (LabAPI and plugin logs and
  command responses), so they are not in the Unity log either. A test-only plugin that adds an
  `IOutput` to `ServerConsole.ConsoleOutputs` (it is recreated on round restart) and logs
  `ServerEvents.CommandExecuted` responses makes them visible.
- Without players the server enters idle mode after `idle_mode_time` (5 s): `targetFrameRate = 1` and
  `timeScale = 0.01`, which stretches every spawn-queue and MEC timing. Set `idle_mode_enabled: false`
  in `config_gameplay.txt` for measurements.
- `../labapimobile/tools/Send-ServerCommand.ps1` deletes command files that the server has not picked up yet when
  `-WaitSec` is short (below about 1 s), so the command is lost.
- Player-bound commands (`select`, `delete`, `modify`, `position`...) can run as the host hub with
  `CommandProcessor.ProcessQuery(query, new PlayerCommandSender(ReferenceHub.HostHub))`.
  `ServerDummy.Spawn` threw a `NullReferenceException` outside a running round.

**Server checks.**

- The prefab dump matches §1.3.
- Exactly one warning per unsupported type per load.
- `load` → `save` round-trip: the YAML is semantically unchanged, unsupported entries included.
- The spawn queue drains within its budget: `mp stats` during load, plus frame time in the log.
- `mp stats`: static count = networked count for static fixtures; MER toy behaviours are disabled.
- Byte estimates are confirmed by hooking `Mirror.NetworkDiagnostics.OutMessageEvent` in a debug build
  (per-message-type bytes for SpawnMessage, ObjectHideMessage and EntityStateMessage).
- Round restart clears all objects, queues and observers. A join mid-round streams nearest-first. A
  disconnect during streaming causes no exceptions. A `FileSystemWatcher` reload runs on the main
  thread.

**Android client.**

1. `../labapimobile/tools/android/Start-Emulator.ps1`, `Install-Client.ps1`, `Start-Client.ps1`, then
   `Connect-Client.ps1 -Address 10.0.2.2:<port>`.
2. **Visual correctness** (`Capture.ps1` at fixed RA teleport spots):
   - all flag encodings, including Quad/Plane facing after the all-negative and `Visible`-only
     encodings
   - walking through `Visible` blocks; blocked by `Collidable` blocks and alpha-0 colliders
   - seams on rotated static vs dynamic blocks
   - doors, lockers and workstations: opening, yaw placement
   - pickups at the right place with no fly-in from the origin
3. **Performance.** Use `Measure-FrameTime.ps1`: same AVD, same spot, player still.
   - frame time with 0/150/500/2000 visible cubes
   - 0/8/16 lights, shadows off and on
   - static vs dynamic 500-cube grid
   - P95/P99 during a 2000-block load with `spawn_max_per_frame` 10/20/50 (spawn hitch)
4. **Culling.** Ride the Gate A elevator 10 times. Measure:
   - streaming time
   - frame spikes
   - `adb shell dumpsys meminfo com.carlmod.game` before and after, to quantify the material leak
5. **Tool gun.** Run `mp tg` from the mobile RA panel, then check on the phone:
   - the FSP-9 shows the attack, aim, reload, throw-away and inspect buttons and the light toggle
   - attack (tap) plays the dry-fire click and creates the current type at the crosshair; holding it
     creates once; no muzzle flash, hit marker or ammo change
   - aim (toggle), wait for the aim animation, then attack: selects the object at the crosshair; at the
     sky it deselects. The HUD switches to SELECT within 0.5 s of aiming
   - inspect (not aiming) and the light toggle switch Create/Delete; the light follows the mode
   - reload (tap) and throw away step backwards and forwards through schematics and types: no reload
     animation, the item is not thrown and stays in the hotbar
   - the HUD: legible at 2400x1080, updates on every input, disappears within about 3 s after
     switching to another item, no flicker from stacked hints
   - the selection box around the selected object (cyan, α 0.25), a second client does not see it; a
     non-collidable (Visible-only) primitive can be selected by aiming at it
   - `mp pos grab` from the RA panel: a primitive and a small schematic move smoothly, the box of a
     large schematic or a door moves (yellow) and the object follows on release, without seams
   - dying with the tool gun drops no FSP-9
   Then run the full edit loop from the mobile RA panel: create, select, mod, pos, save, load.
6. **Real device.** One pass on a low-end ARM64 phone. The emulator runs ARM through binary
   translation (`Measure-FrameTime.ps1` notes), so its absolute numbers are not phone numbers. Final
   values for the §1.7 budgets come from this pass.

**Measured on the Android emulator** (Carl Mod 0.0.4 client, AVD `carlmod_api36`, 2400x1080, server `../labapimobile/.runtime/server-emu`
on the same host; fixtures from `tools/make-mer-fixtures.py`, the `A*` set on a stage in front of the surface NTF spawn;
[docs/testing.md](testing.md)). Frame times are `Measure-FrameTime.ps1` runs of 30 s with the player still; they compare
configurations, not phones.

- **Resend in place** (the riskiest assumption, §3.2): one static cube moved with `mp pos add`, turned with
  `mp rot add`, rescaled and recoloured. The client updated the same object every time: no duplicate, no missing
  object, no client log entry. The documented hide + show fallback is not needed.
- **Visual correctness:**

  | Check | Result |
  | --- | --- |
  | Sphere, capsule, cylinder, cube with positive and all-negative scale; flags `Visible \| Collidable` and `Visible`; static and dynamic | All render with the same shape and colour in all four rows. |
  | Quad facing (`(1,1,1)`, `(1,1,-1)`, `(1,-1,1)`, `(-1,-1,-1)`), static and dynamic | Visible exactly when the mirrored normal faces the camera (the ones with negative `z` are back-facing), in every row. |
  | Plane facing, same patterns, below and above eye level | Up-facing planes (`y` positive) visible from above, down-facing ones from below, in every row. |
  | Walking into walls | `Visible` only: walked through. `Visible \| Collidable`: blocked. `Collidable` only (alpha 0): invisible and blocked. |
  | Seams, 0.6 m cubes and 0.8 m tiles under a rotated parent, static vs dynamic | No visible difference at 3 m (the low-precision rotation of dynamic toys stays below a pixel at this size). |
  | Doors (LCZ, HCZ at 30°, EZ at 90°) | Placed at their yaw; open with E. |
  | Workstation at 45°, lockers | The screen faces the 45° direction; it activates ("ready"). The medkit locker opens with its medkit inside. |
  | Pickups (gravity off and on) | At their positions, no fly-in; picked up with E. |
  | Teleports | Walking into one moves the player to its target. |
  | Lights | Light the hall and the blocks; shadows only with `allow_light_shadows`. Real schematics' HDRP intensities (60-100) need `light_intensity_scale` (§3.6): at scale 1 every lit surface turns white. |
  | Real schematics (Shipment, Battle, 35Hp) | Render with their colours after the intensity scale; Shipment's merged blocks show no seams or holes. |

- **Client checks that need port code:** door waypoint numbering (§1.1 `doors`, docs/compatibility.md), host spawn
  payloads (§3.8), light intensities (§3.6), and the tool gun HUD size and position (§4).
- **Zone culling** (SurfaceFacility, a 500-cube wall on the Surface, Gate B elevator; the player left the chamber at the
  bottom for 8 s on every round trip; 10 rides): every round trip hid the 500 objects 5-6 s after the player left the
  chamber (0.12 s at 80 per frame) and streamed them again when the player re-entered it (0.42 s at 20 per frame), so the
  content was there on arrival at the top. Client frames during such a stream: worst 50-200 ms against 36 ms without one.
  Client memory grew 15.6 MB over the five hide/show cycles, about 6 KB per destroyed primitive (2000-cube unloads:
  about 10 KB each); the leak is real but bounded by how often players change zone.
- **Join** with a 2000-cube Surface map and three MER doors: the joining spectator received the 8 unculled objects in
  one frame, and the Surface objects as a paced stream when it spawned there (2000 in 1.66 s over 100 frames at 20 per
  frame). Positions near the MER doors were right after the join.
- **Indicators** with two clients: the admin who turned them on received 7 parts; the other client received none
  (`mp stats`: 2018 against 2011 observed MER objects). The selection box was observed by the host and its owner only.
- **Tool gun** (FSP-9): attack creates once per tap (holding creates once) with the dry-fire click and no ammo change;
  aim then attack selects, at nothing deselects, and selects a `Visible`-only cube through its editing trigger; the
  light toggle and inspect switch modes (inspect also plays a 3 s animation); reload and throw-away step through the
  cycle without animation or dropping; the HUD follows every input within 0.5 s and hides when another item is held;
  dying drops no FSP-9. `mp pos grab`: a primitive and a 46-block schematic move with the view, a door moves as a yellow
  proxy box and follows on release, with its selection box (also for `Visible`-only objects). The RA panel (管理) did
  not open on the emulator; the full edit loop (`create`, `mod color`,
  `pos add`, `rot set`, `save`, `unload`, `load`) ran from the client console with `/mp ...` commands.
- **Frame time** (mean of three 30 s runs at the stage view; FPS / mean frame ms / P95 / P99 ms):

  | Configuration | FPS | ms | P95 | P99 |
  | --- | --- | --- | --- | --- |
  | 0 MER objects | 50.5 | 19.8 | 34 | 36 |
  | 150 static cubes in view | 48.9 | 20.5 | 34 | 34-36 |
  | 500 static cubes | 44.3 (6 runs) | 22.6 | 34-36 | 36-46 |
  | 2000 static cubes | 30.8 | 32.5 | 38-50 | 50-54 |
  | 500 dynamic cubes (not moving) | 41.2 | 24.3 | 34-36 | 36-46 |
  | 150 cubes + 0 lights | 47.0 | 21.3 | 34-36 | 36-38 |
  | 150 cubes + 8 lights, no shadows | 43.2 (5 runs, 39.5-49.0) | 23.1 | 36 | 36-48 |
  | 150 cubes + 16 lights, no shadows | 47.2 (5 runs) | 21.2 | 34 | 36-38 |
  | 150 cubes + 8 lights with shadows | 37.2 | 26.9 | 34-36 | 36-46 |
  | Shipment, optimizer on (970 networked) | 52.6 | 19.0 | 33-34 | 34-36 |
  | Shipment, optimizer off (1020 networked) | 50.6 | 19.8 | 34 | 36 |

  A visible cube costs about 5-6 µs of client frame time. Shadowless lights stay within the run-to-run spread (the
  per-object pixel light limit caps their passes); shadowed ones cost about 20%. Shipment's optimizer saving (50
  objects and one light) is about 4%.
- **Load hitch** (a 2000-cube map loaded behind the camera, so only instantiation costs; 12 s windows):

  | `spawn_max_per_frame` | Stream | P95 ms | P99 ms | Worst frame ms |
  | --- | --- | --- | --- | --- |
  | no load | - | 34 | 36 | 36-38 |
  | 10 (5 runs) | 3.4 s | 34-54 | 50-86 (mean 62) | 66-150 (mean 112) |
  | 20 (5 runs) | 1.7 s | 48-50 | 66-86 (mean 72) | 118-250 (mean 167) |
  | 50 (3 runs) | 0.7 s | 36-38 | 114-122 | 300-500 |

- **Chosen defaults.** `spawn_max_per_frame` 10 rather than 20: worst frames about a third shorter for twice the stream time
  (about 600 objects/s, 3-4 s for a 2000-2500-block map, mostly during the lobby). `light_intensity_scale` 0.025 (new).
  The others stand: `zone_culling_min_objects` 150 and the 150-visible target (150 cubes cost 3%),
  `networked_warn_total` 1500 (1500 visible would cost about 25-30%), `networked_hard_cap` 4000, `allow_light_shadows`
  false, `max_lights` 16, `max_lights_per_schematic` 4, `static_by_default` true.
- **Measurement note.** Windows power throttling of a background emulator window cut the client from about 51 to 39 FPS
  in the same scene; `Start-Emulator.ps1` and `Measure-FrameTime.ps1` opt the emulator out (LabAPI-Mobile's docs/testing.md).

---

## 6. Risks and open questions

| Risk | Mitigation |
|---|---|
| The LabAPI port does not raise the events ProjectMER relies on, or raises them with different semantics: `PlayerSpawning` (`SpawnLocation`), searching and picking up pickups, the firearm requests (dry-fire, reload, ADS, inspect, flashlight), the shooting-target interaction, and LCZ decontamination started. | Track these as explicit dependencies of the LabAPI port. The tool gun degrades to RA-only commands if an input event is missing. |
| The mobile client might not send `Dryfire`, `Inspect` or `ToggleFlashlight` from its touch UI the same way the PC client does. | The shared `MobileTouchController`/`MobileActionArea`/`Firearm.UpdateKeys` code sends them (attack is held while touched, aim is a toggle, inspect, reload, throw-away and the light toggle are clicks; the light toggle needs a flashlight attachment), and an empty semi-automatic gun never dry-fires, hence the FSP-9 (§4). Verify on the emulator in step 5 of §5.3. RA commands stay a complete fallback. |
| Re-sending `SpawnMessage` to an existing netId relies on the fork's `NetworkClient.FindOrSpawnObject` → `ApplySpawnPayload` reuse path. That path is IL2CPP on the client, so behaviour is inferred from the shared C#. | Verified on the Android client (§5.3): move, turn, scale and recolour update the same object. Resends skip the server's host connection, whose spawn handling would revert later SyncVar writes. |
| Material leak on client despawn (fact 5) makes any churn-heavy feature degrade phones over a round. | Measured at 6-10 KB per destroyed primitive (§5.3). Culling stays Surface/Facility, spectators are add-only, and editing uses resend-in-place. |
| The `Scale`-sign encoding depends on `SetPrimitive` reading the `Scale` SyncVar before `Start`. Initial deserialization sets `Scale` in `AdminToyBase.DeserializeSyncVars` before the derived `PrimitiveType` hook runs. | Covered by the flag fixtures on Android. |
| Animator AssetBundles built for another Unity version may fail to load on Unity 6000.3 servers. | Log and fall back to a static schematic. |
| Draw-call and spawn budgets are estimates until measured. The emulator is not representative. | Set from the emulator measurements (§5.3); a real-device pass is still open. |
| MER doors change the door waypoint numbering that player and pickup positions rely on. | `MerWaypoints` keeps the server's numbering equal to the clients' (§5.3, docs/compatibility.md); at most 223 door waypoints (ids 32 to 254). |
| ProjectMER light intensities are HDRP values; the fork renders with the built-in pipeline. | `light_intensity_scale` (§3.6). |
| Removing Triangle/Quad-built content (ProjectMER's triangle exporter) leaves holes in schematics that use it. | Warning with counts. Quads (`ToolGunObjectType.Quad` = primitive Quad) remain supported. |
| Third-party plugins that read `SchematicObject.GetComponent<PrimitiveObjectToy>()` on the root, or expect blocks to be children of the root, break. | Documented in `docs/compatibility.md`. `AttachedBlocks`/`AdminToyBases` still return the blocks. |
| Door ids wrap at 255 (`DoorVariant.Start` `_serverDoorIdClock`, `FORK/Interactables.Interobjects.DoorUtils/DoorVariant.cs:213-219`). | Warn when MER doors push the total past 255. |
| Merged and duplicate blocks have no toy of their own, so plugin code that recolours, moves or destroys single cubes of a schematic sees no effect. | `merge_blocks: Maps` (default) leaves plugin-spawned schematics unmerged; `IsStatic = false` networks the blocks individually again. Documented in the README and `docs/compatibility.md`. |
| Plugin code that reads `AttachedBlocks` and the other block lists right after spawning a schematic, or a periodic scan over every schematic, forces a synchronous build and brings back the load hitch. | The `Current*` lists and `IsSpawned` let periodic code skip unfinished schematics; `SchematicSpawned` tells plugins when the blocks exist. |
| Carl Mod's Mono garbage collector is not incremental; a full collection stops the server for 25 ms at an 85 MB live heap and grows with the heap. A large load triggers about one. | Keep allocation per built block low; keep the live heap small (LabAPI must drop wrappers of destroyed toys). |
