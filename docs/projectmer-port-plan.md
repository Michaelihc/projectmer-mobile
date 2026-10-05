# ProjectMER port plan for the Carl Mod server

This plan covers porting ProjectMER (MapEditorReborn, LabAPI edition, version 2025.11.2.1 including the
Triangle and Quad additions) to the Carl Mod fork. It sets out the feature matrix, file-format
compatibility, the mobile performance design, tool-gun editing without server-specific settings, and a
file-level port and verification plan.

Path abbreviations used below:

| Abbreviation | Path |
|---|---|
| `PMER/` | `.refereces/scpsl-metarepo/.references/Reference Plugins/ProjectMER/` |
| `MER132/` | `../scpsl-mobile-analysis-20261005/references/MapEditorReborn-sl13.2/MapEditorReborn/` |
| `FORK/` | `../scpsl-mobile-analysis-20261005/decompiled/server/` (fork Assembly-CSharp, Mono CIL bodies) |
| `CLIENT/` | `../scpsl-mobile-analysis-20261005/decompiled/carl-client/` (IL2CPP metadata view; bodies are placeholders) |
| `OFFICIAL/` | `.refereces/scpsl-metarepo/.references/Decompiled/DedicatedServer/Assembly-CSharp/` (SL 14.2.7) |
| `MIRROR/` | `.runtime/server-original/Carl Mod_Data/Managed/Mirror.dll`, decompiled with `ilspycmd -p -o <dir> Mirror.dll`; cited as `Mirror/<File>.cs: <member>` |
| `ASSETS` | `.runtime/server-original/Carl Mod_Data/{resources.assets,sharedassets1.assets,level1}` |

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
| `toolgun` (`tg`) | adapted | No SSS dropdown. New subcommands: `mp tg schematic <name\|index>` and `mp tg type <type>` (§4). |
| `indicators` | adapted | Indicators are visible only to players who enabled them (§3.8). No per-frame `Update`. |
| `position set/add/bring/grab`, `rotation set/add`, `scale set/add` | adapted | Edits to static toys resend the spawn message in place, with no destroy (§3.2). `grab` on large schematics moves a proxy box and applies the move on release. |
| `modify` | as-is | Reflection over `Serializable*`. Enum parsing uses the fork's enums. |
| `create`, `delete`, `select` | adapted | `create` lists only supported types. Selection maps a hit collider back to its `MapEditorObject` through a link component, because toys are no longer children of their schematic root (§3.2). |
| new: `mp stats` | — | Networked and static counts, light count, queue length, observed objects per player, estimated spawn bytes. |
| new: `mp optimize <schematic>` | — | Runs the simplifier (§3.9) and reports the before/after block counts. |
| new: `mp prefabs` | — | Prefab dump (§1.3). |

### 1.5 Tool-gun features (`PMER/Features/ToolGun/*`, `Events/Handlers/Internal/ToolGunEventsHandler.cs`)

| Feature | Status | Notes |
|---|---|---|
| COM-18 item with 0 ammo; dry-fire triggers the action | adapted | The fork's `FirearmBasicMessagesHandler` handles `Dryfire`, `Reload`, `AdsIn/Out`, `ToggleFlashlight` and `Inspect` (`FORK/InventorySystem.Items.Firearms.BasicMessages/FirearmBasicMessagesHandler.cs:99-142`). Ammo and cocked state come from `FirearmStatus`/`FirearmStatusFlags.Cocked` instead of `MagazineModule`/`AutomaticActionModule`. Attachment code `454` is 14.x-specific; the port uses an attachment code computed for the fork's COM-18. |
| Create/Delete mode by flashlight, Select by ADS | adapted | ADS is kept (mobile aim button). The flashlight needs a flashlight attachment, and the fork only toggles it when `HasAdvantageFlag(Flashlight)`. Inspect is added as a mode toggle that does not need an attachment (§4). |
| Drop = next type, Reload = previous type | as-is | Mobile throw-away and reload buttons. Cycling uses a list of supported types; ProjectMER's enum arithmetic breaks once types are removed. |
| Schematic choice via SSS dropdown | replaced | `mp tg schematic`, plus the selected name shown in the HUD (§4). |
| HUD hint every 0.1 s (`ToolGunUI.GetHintHUD`) | adapted | A compact HUD sent only when its contents change (keep-alive every 1 s). It drops the 36 padding lines and the `LiberationSans SDF` font tag (§4). |
| Indicators for invisible objects | adapted | Per-player visible, non-colliding (§3.3) and server-trigger selectable, as in ProjectMER (`IndicatorObject.TrySpawnOrUpdateIndicator` adds a trigger `BoxCollider`). Indicator roots with `PrimitiveFlags.None` become server-only anchors. |
| AutoSelect after create | as-is | |

### 1.6 Events

| Event / handler | Status | Notes |
|---|---|---|
| `Schematic.SchematicSpawning` (cancellable, `Data` replaceable), `SchematicSpawned`, `SchematicDestroyed`, `ButtonInteracted` | as-is | `SchematicSpawned` still fires synchronously once the server-side objects exist. A new `SchematicBuilt` event and `SchematicObject.IsBuilt` signal that the batched network spawn has finished. |
| `GenericEventsHandler` (prefab registration, player spawnpoints, shooting-target guard) | as-is | Depends on LabAPI `ServerWaitingForPlayers`, `PlayerSpawning` and `PlayerInteractingShootingTarget`. |
| `PickupEventsHandler` (button pickups, `NumberOfUses`) | adapted | Firearm refill uses `FirearmStatus`. Depends on `PlayerSearchingPickup`, `PlayerPickingUpItem` and `PlayerPickingUpAmmo`. |
| `ToolGunEventsHandler` | adapted | Uses `PlayerDryFiringWeapon`, `PlayerReloadingWeapon`, `PlayerDroppingItem` and `PlayerAimingWeapon`, plus `PlayerInspectingItem`/`PlayerTogglingWeaponFlashlight` for the mode toggle. All map to `FirearmBasicMessagesHandler.ServerRequestReceived` in the fork; the LabAPI port must raise them there. |
| `ActionOnEventHandlers` | as-is | Waiting-for-players, round started, LCZ decontamination started, and warhead started/stopped/detonated. The unload path calls `HandleMapLoading` for split arguments; the port calls `HandleMapUnloading`. |

### 1.7 Config options

| Option | Status |
|---|---|
| `EnableFileSystemWatcher` | adapted. `FileSystemWatcher.Changed` runs on a thread-pool thread, but ProjectMER calls `Timing.CallDelayed` from it (`PMER/ProjectMER.cs: OnMapFileChanged`). The port enqueues the map name to a main-thread queue that an MEC coroutine drains. |
| `AutoSelect` | as-is |
| `OnWaitingForPlayers`, `OnRoundStarted`, `OnLczDecontaminationStarted`, `OnWarheadStarted/Stopped/Detonated` | as-is |
| New mobile options | `static_by_default: true`, `honor_static_property: false` (§3.2), `spawn_max_per_frame: 20`, `spawn_time_budget_ms: 3`, `dynamic_toy_sync_interval: 0.1`, `allow_light_shadows: false`, `max_lights: 16`, `primitive_warn_per_schematic: 300`, `networked_warn_total: 1500`, `networked_hard_cap: 4000`, `invisible_collider_mode: Transparent` (`Transparent`/`Skip`), `optimize_schematics: true`, `managed_visibility: true`, `zone_culling: SurfaceFacility` (`None`/`SurfaceFacility`/`PerZone`), `zone_culling_min_objects: 150`, `hud_interval: 0.5`, `warhead_spares_outside_rooms: true` (§1.8), `log_spawn_stats: true` (one throughput line per spawn-queue drain). These are starting values to tune with §5.3 measurements. |

### 1.8 Other API and patches

| Item | Status | Notes |
|---|---|---|
| `ObjectSpawner.*` | as-is | |
| `SchematicObject` (`Position/Rotation/Scale` setters, `AttachedBlocks`, `NetworkIdentities`, `AdminToyBases`, `AnimationController`, `Destroy`) | adapted | Blocks are kept in internal lists because flattened toys are not children. On a static schematic, `Position/Rotation/Scale` setters resend spawn payloads in place, coalesced to at most one resend per 0.25 s. New `IsStatic` property; setting it to false switches to synced dynamic toys. The root no longer has a `PrimitiveObjectToy`. |
| `AnimationController`, animator AssetBundles, `<name>-Rigidbodies.json` | as-is, costly | Animated or physics schematics run as dynamic toys with a per-schematic sync component (§3.2). Risk: bundles built for other Unity versions may not load in Unity 6000.3. |
| `CullingExtensions` (`SendSpawnMessage` by reflection, raw `ObjectDestroyMessage`) | adapted | Reimplemented over the observer API (§3.8), because raw messages desync Mirror's `observers`/`observing` bookkeeping. `NetworkServer.SendSpawnMessage(NetworkIdentity, NetworkConnectionToClient)` exists as an internal method in the fork's Mirror and is accessible through the publicized reference. |
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
- **Cache.** Parsed data is cached by name, file length and modified time. ProjectMER re-reads and
  re-parses the JSON on every spawn (`MapUtils.GetSchematicDataByName`).
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
  Add a kinematic `Rigidbody` server-side to moving collidable toys so PhysX does not rebuild static
  colliders. A rigidbody entry goes on the toy itself (it has the collider), whose own `LateUpdate`
  syncs it; `SchematicSync` then moves the block's anchor from the toy so the children follow. Rigidbody
  entries of pickups go on the pickup's own body, and entries of empties on their anchor.

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
- **Non-collidable objects still need to be selectable** by the tool gun. While someone holds a tool
  gun or has indicators on, the editor adds a server-only trigger `BoxCollider` to non-collidable MER
  toys. It is removed when editing stops, so it never affects hit registration during play.
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

### 3.5 Spawn batching

- **One global `SpawnQueue`** holds prepared (instantiated, positioned, SyncVars set) objects waiting
  for `NetworkServer.Spawn`. An MEC coroutine on `Segment.Update` drains it each frame, up to
  `spawn_max_per_frame` (20) or until `spawn_time_budget_ms` (3 ms, measured with `Stopwatch`).
- **Priority:**
  1. collidable blocks, nearest to any player first, so floors appear before decoration
  2. visible blocks
  3. lights
  4. pickups
- **Each spawn costs one payload serialization per ready connection**, inside `RebuildObservers` and
  `SendSpawnMessage`. The budget therefore scales with player count; the time cap handles that.
- **Unload** cancels any pending entries of that map or schematic before destroying spawned ones.
  Destroys are cheap at about 6 B each, and are batched with the same coroutine at 4× the spawn limit.
- **Throughput.** At 20 per frame and 60 Hz, a 1000-block schematic streams in under 1 s, about
  1200 objects/s or 124 KB/s per player. The client then instantiates about 40 primitives per frame at
  30 fps instead of 1000 at once.
- **Measured on the server** (phase A, no client connected): the 2000-cube grid streams in 1.65 s over
  100 frames (1215 objects/s, 20 per frame), with an average queue slice of 0.11 ms and the slowest
  at 0.24 ms; Skeld's 2558 blocks take 2.11 s. Spawning is not the server cost: the synchronous build
  (instantiating, positioning and encoding every block) makes the load frame itself take 170-270 ms
  for 2000-2600 blocks. Unloading during streaming drops the rest of the group (1560-1580 of 2000 dropped
  after 0.5 s in the test).
- **ProjectMER's synchronous behaviour is replaced.** It spawns every block immediately inside
  `CreateObject` (`PMER/Features/Objects/SchematicObject.cs`). Its recursion is also O(n²):
  `blocks.Find`, `FindAll` and a LINQ `parentSchematics` array per block. The port builds a
  `Dictionary<int, List<SchematicBlockData>>` of children once, which makes it O(n).

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
- **`Range` is not clamped**, but a range above 30 m triggers a warning: the light touches many
  objects.

### 3.7 Budgets and warnings

- Per schematic: warn above `primitive_warn_per_schematic` (300) networked blocks after simplification.
  The warning shows the count of transparent and dynamic blocks.
- Global: warn when total networked MER objects exceed `networked_warn_total` (1500). Refuse further
  spawns above `networked_hard_cap` (4000), with an error and a `mp stats` hint.
- Rationale: every visible primitive is at least one draw call, and the game's own scene already uses
  much of a low-end phone's draw-call budget. The working target is **≤ 150 MER primitives visible
  from any one spot**. The defaults are starting points for §5.3, not measured limits.

### 3.8 Per-player and per-zone culling with Mirror observers

**Feasibility: yes, without Harmony and without replacing `NetworkServer.aoi`.**

- Set `identity.visibility = Visibility.ForceHidden` before `NetworkServer.Spawn`. With
  `aoi == null`, `RebuildObserversDefault` then adds no observers, and `SpawnObserversForConnection`
  skips the object for joining players (`Mirror/NetworkServer.cs`).
- Show for one connection: `identity.AddObserver(conn)`. It adds to `identity.observers` and calls
  `conn.AddToObserving`, which calls `ShowForConnection` → `SendSpawnMessage`.
- Hide for one connection: `identity.RemoveObserver(conn)` plus `conn.RemoveFromObserving(identity,
  false)`, which sends `ObjectHideMessage`. The client handles it in `NetworkClient.OnObjectHide` and
  destroys the object.
- All of these are `internal` in the fork's Mirror and compile through the publicized reference.
  Unspawn and destroy still reach exactly the current observers, and `SetClientNotReady` clears them
  (`RemoveFromObservingsObservers`).

**Cost of a transition** (one player changes zone, group of N primitives):

- about 103 B × N to that player; N = 1000 is about 103 KB, about 0.85 s at the spawn budget
- about 6 B × N of hide messages
- N client instantiations
- **2 × N leaked `Material`s** on the client (fact 5) until the next scene load

**Benefit while the player stays in the other zone:**

- N fewer draw-call candidates and N fewer transforms, renderers and colliders on the phone
- N fewer entries in that connection's per-tick broadcast walk on the server

**Recommendation:**

1. **Always use managed visibility for MER objects** (`managed_visibility: true`), even without zone
   culling. It provides:
   - paced join streaming: a joining player gets MER objects through the spawn queue, nearest and
     collidable first, instead of one huge burst
   - admin-only indicators, selection highlight and grab proxy, all with zero churn
2. **Zone culling only at Surface vs Facility granularity** (`zone_culling: SurfaceFacility`):
   - Only for groups (a map or a top-level schematic) with at least `zone_culling_min_objects` (150)
     networked objects, assigned by their room's zone or by `y ≥ 900`. 900 is the game's own threshold
     (`FORK/AlphaWarheadController.cs:412`).
   - Transitions happen only through the Gate A/B elevators, a few times per player per round. That
     bounds both the material leak and the bandwidth spikes.
   - Prefetch the target group when a player enters an elevator chamber. Keep the old group for 5 s
     after leaving it (hysteresis).
   - A 1 s MEC poll of player positions detects zone changes; there is no per-frame work.
3. **`PerZone` (LCZ/HCZ/EZ) is opt-in.** The HCZ↔EZ checkpoint is walk-through, so streaming would
   visibly pop in. **Distance and room culling are not recommended**: room changes every few seconds
   would leak materials and use bandwidth continuously.
4. **Spectators and Overwatch follow the zone of their spectated player, add-only.** Groups are never
   hidden until the player respawns, which bounds the churn caused by switching spectate targets.
5. **MER teleports to another zone** call the culling system to show the target group before moving
   the player.

### 3.9 Schematic simplification (`optimize_schematics`, cached per schematic file)

1. **Do not network blocks that produce nothing on the client**:
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

   Measured on the server (phase A, `optimize_schematics: true`, ProjectMER count = root + one toy per
   block + six per triangle): 35Hp 55 → 46, Battle 72 → 69, DeathParty 21 → 19, Jail 315 → 294,
   Shipment 1130 → 1020, Skeld 2923 → 2558 (plus 119 of its 135 lights dropped by `max_lights`).
3. **Anchors only where needed.** Static leaf blocks get no anchor; their world transform comes from a
   scratch transform under the parent anchor (§3.2). Blocks with children, blocks without networked
   output and animated or physics subtrees keep anchors. Discarding the remaining group anchors would
   save only server memory and is not planned.
4. **Merge cubes.**
   - Group static `Cube` blocks by:
     - quantized world rotation
     - colour
     - encoded flags
     - transparency
   - In the group's rotation frame each cube is an AABB. Greedily merge pairs that share a full face
     (two extents equal within 1 mm and touching along the third axis), sweeping each axis until no
     merge is possible.
   - The merged cube covers exactly the same volume and loses only internal faces, so it is
     pixel-identical apart from lighting seams.
   - Applies to static blocks only. Not applied across differing parents of dynamic schematics.
5. **Drop exact duplicates** (same type, transform, colour and flags).
6. **Lights.** Keep at most 4 per schematic (configurable) by `intensity × range`.
7. **Report.** `mp optimize <name>` prints counts before and after each step and the estimated spawn
   bytes. The optimized block list is cached in memory, keyed by file time; source files are never
   rewritten.

### 3.10 Smaller CPU fixes carried in the port

- `IndicatorObject.Update` sets the transform every frame for every indicator. Replace it with updates
  from `UpdateObjectAndCopies`.
- `ToolGunEventsHandler.ToolGunGUI` rebuilds the HUD by reflection every 0.1 s per holder. Change it to
  `hud_interval` and send only on change.
- `IndicatorObject.TryGetIndicator` does a linear `Dictionary.First` search. Add a reverse dictionary.
- No LINQ, closures or string formatting in the spawn-queue, sync or culling paths (AGENTS.md
  performance rules).

---

## 4. Tool gun and editing without server-specific settings

**Input mapping.** Only requests the stock firearm handler already processes are used
(`FirearmBasicMessagesHandler.ServerRequestReceived`). All of them have mobile buttons.

| Input (mobile button) | Request / LabAPI event | Action |
|---|---|---|
| Attack (empty magazine → dry-fire) | `Dryfire` / `PlayerDryFiringWeapon` (cancelled) | Run the current mode at the crosshair: Create, Delete or Select. |
| Aim held | `AdsIn/AdsOut` / `PlayerAimingWeapon` | While aiming, attack selects. This is ProjectMER's `SelectMode`. |
| Inspect | `Inspect` / `PlayerInspectingItem` | Toggle Create ↔ Delete. Needs no attachment. |
| Light toggle | `ToggleFlashlight` / `PlayerTogglingWeaponFlashlight` | Same toggle as Inspect, as in ProjectMER, when the attachment code includes a flashlight. |
| Reload | `Reload` / `PlayerReloadingWeapon` (cancelled) | Previous object type. When the type is Schematic, previous schematic name. |
| Throw away (drop) | `PlayerDroppingItem` (cancelled) | Next object type, or next schematic name. |

**Replacing the SSS dropdown.**

- `mp tg schematic <name|index>` sets the player's schematic. `mp list` numbers the schematics.
- `mp create <schematic>` is unchanged.
- The selected schematic is per player (a `Dictionary<Player, ToolGunState>`) instead of an SSS
  setting.

**HUD.**

- At most 8 short lines in a `TextHint`: mode, object type or schematic, selected object id and map,
  room id, and a reminder of the button mapping.
- Uses `<size>`/`<color>` only and avoids the `LiberationSans SDF` font tag.
- Sent when the content changes, with a keep-alive every `hud_interval` and a hint duration of
  `hud_interval + 0.3 s`.
- Selection feedback is a translucent box (α 0.25) around the selected object, visible only to that
  admin through managed visibility (§3.8).

**Precise edits** use Remote Admin and console commands, both available on mobile:

- `mp pos add 0 0.1 0`, `mp rot set …`, `mp mod color #ff0000` and so on are unchanged.
- Add short aliases where missing, for example `mp p a`, `mp r a`.
- `select <id>`/`delete <id>` work by id when aiming is fiddly on a touchscreen.

**Grab.**

- Single toys, doors and structures are moved by the 0.1 s coroutine. A static toy is switched to
  dynamic while grabbed, then resent at full precision on release.
- Schematics above 50 networked blocks move one admin-only proxy box instead, and are resent in place
  on release (§3.2).

---

## 5. Port plan

### 5.1 Steps

1. **Project skeleton.**
   - `src/ProjectMER/ProjectMER.csproj` targets net48 with LangVersion 12, matching `LabApi.csproj`.
   - It references the fork's Managed folder from `Directory.Build.props`: publicized
     `Assembly-CSharp`, `Assembly-CSharp-firstpass` and `Mirror`, plus `DigitalDust` (MEC),
     `CommandSystem.Core`, `NorthwoodLib`, `YamlDotNet`, `Newtonsoft.Json`, and the Unity modules
     (Core, Physics, Animation, AssetBundle).
   - It has a `ProjectReference` to `src/LabApi`, plus `Lib.Harmony` 2.3.6.
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
   `Properties` normalizer, and the parse cache.
9. **Managed visibility** (new `Features/Mobile/MerVisibility.cs`):
   - ForceHidden spawning
   - per-connection show/hide
   - join streaming
   - admin-only objects
   - zone culling with prefetch and hysteresis
   - spectator handling

   Reimplement `CullingExtensions` on top of it.
10. **Tool gun**: `ToolGunItem` (fork firearm state, input mapping), `ToolGunUI` (mobile HUD),
    `ToolGunEventsHandler` (events, HUD diffing), `mp tg schematic`/`type`.
11. **Indicators, grab proxy and selection box** through managed visibility.
12. **Budgets, light caps, and `mp stats`/`mp optimize`.**
13. **Patches**: the optional warhead patch with a one-line citation of
    `OFFICIAL/AlphaWarheadController.cs: CanBeDetonated`; drop the experimental-locker patch; a single
    `PatchAll`.
14. **Main-thread queue** for the `FileSystemWatcher`.
15. **Docs**:
    - a ProjectMER section in `docs/compatibility.md`: supported, adapted and absent types, and the
      config reference
    - a short user README in `src/ProjectMER/`

### 5.2 File mapping (`PMER/` → `src/ProjectMER/`)

| ProjectMER file | Port |
|---|---|
| `ProjectMER.cs` | Light edits: one `PatchAll`, watcher → main-thread queue, `Version`/`RequiredApiVersion` for the port. |
| `Configs/Config.cs` | Kept, plus the mobile options from §1.7. |
| `Commands/MapEditorParentCommand.cs`, `Map/Load.cs`, `Map/Save.cs`, `Map/Unload.cs`, `Utility/List.cs`, `Utility/Merge.cs` | Verbatim. |
| `Commands/Modifying/Modify.cs`, `Position/Position.cs`, `Position/SubCommands/{Add,Set,Bring}.cs`, `Rotation/{Rotation,SubCommands/Add,SubCommands/Set}.cs`, `Scale/{Scale,SubCommands/Add,SubCommands/Set}.cs` | Verbatim; edits flow through `UpdateObjectAndCopies`, which now resends in place. |
| `Commands/Modifying/Position/SubCommands/Grab.cs` | Rewrite: proxy for large schematics, static↔dynamic switching. |
| `Commands/ToolGunLike/Create.cs` | Small edit: list and accept only supported types; reply with a reason for unsupported ones. |
| `Commands/ToolGunLike/Delete.cs`, `Select.cs` | Verbatim (raycast resolution changes inside `ToolGunHandler`). |
| `Commands/Utility/Indicators.cs` | Small edit: per-player toggle. |
| `Commands/Utility/ToggleToolGun.cs` | Small edit: new `schematic`/`type` subcommands. |
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
| `Features/ToolGun/ToolGunHandler.cs` | Small edit: link-based hit resolution, supported-type list. |
| `Features/ToolGun/ToolGunItem.cs`, `ToolGunUI.cs` | Rewrite (§4). |
| `Patches/AlphaWarheadCanBeDetonatedFix.cs` | Adapt to the fork's semantics, behind a config flag. |
| `Patches/ExperimentalWeaponLockerGlobalLimitsPatch.cs` | Removed. |
| new | `Features/Mobile/{ToyFactory,SpawnQueue,MerVisibility,SchematicSync,SchematicOptimizer,Budget,MainThreadQueue}.cs`, `Features/Objects/MerBlockLink.cs`, `Features/Serialization/SchematicJson.cs`, `Features/UnsupportedContent.cs`, `Commands/Utility/{Stats,Optimize,Prefabs}.cs` |

### 5.3 Verification plan

**Build and install.**

1. Build with `dotnet build src/ProjectMER/ProjectMER.csproj -c Release --artifacts-path
   C:\tmp\labapi-mer`.
2. Run the LabAPI installer on a server copy (never `server-original`).
3. Put `ProjectMER.dll` in `LabAPI-Mobile` plugins.
4. Start the server with `tools/Start-TestServer.ps1 -CommandSession mer` and send commands with
   `tools/Send-ServerCommand.ps1`. Results are checked in the server log, because the file console
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
- `tools/Send-ServerCommand.ps1` deletes command files that the server has not picked up yet when
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

1. `tools/android/Start-Emulator.ps1`, `Install-Client.ps1`, `Start-Client.ps1`, then
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
5. **Tool gun.** Exercise every mobile button from §4 and confirm the server-log events. Run the full
   edit loop from the mobile RA panel: create, select, mod, pos, save, load.
6. **Real device.** One pass on a low-end ARM64 phone. The emulator runs ARM through binary
   translation (`Measure-FrameTime.ps1` notes), so its absolute numbers are not phone numbers. Final
   values for the §1.7 budgets come from this pass.

---

## 6. Risks and open questions

| Risk | Mitigation |
|---|---|
| The LabAPI port does not raise the events ProjectMER relies on, or raises them with different semantics: `PlayerSpawning` (`SpawnLocation`), searching and picking up pickups, the firearm requests (dry-fire, reload, ADS, inspect, flashlight), the shooting-target interaction, and LCZ decontamination started. | Track these as explicit dependencies of the LabAPI port. The tool gun degrades to RA-only commands if an input event is missing. |
| The mobile client might not send `Dryfire`, `Inspect` or `ToggleFlashlight` from its touch UI the same way the PC client does. | Verify on the emulator in step 5 of §5.3. RA commands stay a complete fallback. |
| Re-sending `SpawnMessage` to an existing netId relies on the fork's `NetworkClient.FindOrSpawnObject` → `ApplySpawnPayload` reuse path. That path is IL2CPP on the client, so behaviour is inferred from the shared C#. | Verify early with one static cube moved by `mp pos add`. Fallback: hide + show (costs a material leak per edit, editing only). |
| Material leak on client despawn (fact 5) makes any churn-heavy feature degrade phones over a round. | Culling stays Surface/Facility, spectators are add-only, and editing uses resend-in-place. |
| The `Scale`-sign encoding depends on `SetPrimitive` reading the `Scale` SyncVar before `Start`. Initial deserialization sets `Scale` in `AdminToyBase.DeserializeSyncVars` before the derived `PrimitiveType` hook runs. | Covered by the flag fixtures on Android. |
| Animator AssetBundles built for another Unity version may fail to load on Unity 6000.3 servers. | Log and fall back to a static schematic. |
| Draw-call and spawn budgets are estimates until measured. The emulator is not representative. | Real-device pass before setting the defaults. |
| Removing Triangle/Quad-built content (ProjectMER's triangle exporter) leaves holes in schematics that use it. | Warning with counts. Quads (`ToolGunObjectType.Quad` = primitive Quad) remain supported. |
| Third-party plugins that read `SchematicObject.GetComponent<PrimitiveObjectToy>()` on the root, or expect blocks to be children of the root, break. | Documented in `docs/compatibility.md`. `AttachedBlocks`/`AdminToyBases` still return the blocks. |
| Door ids wrap at 255 (`DoorVariant.Start` `_serverDoorIdClock`, `FORK/Interactables.Interobjects.DoorUtils/DoorVariant.cs:213-219`). | Warn when MER doors push the total past 255. |
