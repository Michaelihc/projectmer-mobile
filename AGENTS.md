# ProjectMER-Mobile

Follow the parent workspace instructions. Reply in English unless requested otherwise.

ProjectMER 2025.11.2.1 (MapEditorReborn, LabAPI edition) ported to the Carl Mod server, a mobile fork of SCP:SL based
on roughly SL 13.1-13.2 game code, on top of LabAPI-Mobile. Server-side only: the Android client is IL2CPP and is never
patched. ProjectMER may use only content the stock Carl Mod client already understands.

## Layout

- `src/ProjectMER/` — the port. Namespaces, type names and public API follow upstream ProjectMER; mobile-specific code
  is mainly in `Features/Mobile/` and `Features/Serialization/`; `docs/projectmer-port-plan.md` §5.2 maps every file to
  its upstream original.
- `docs/compatibility.md` — supported, adapted and absent map objects, schematic blocks and API differences.
- `docs/testing.md` — test servers, fixtures and Android client checks.
- `docs/projectmer-port-plan.md` — design, file mapping and measurements; its *local* paths are reference material
  outside the repositories.
- `tools/make-mer-fixtures.py` — test fixtures into `.runtime/mer-fixtures` (`--real` or `MER_REAL_SCHEMATICS` for
  copies of real schematics).
- `tools/Package.ps1` — release archive `dist/ProjectMER-Mobile-<version>.zip`; `tools/package/INSTALL.txt` is its
  template.
- `README.md` and `README.zh-CN.md` — user documentation in English and Simplified Chinese. Keep both in sync: same
  content and structure, commands, file names, identifiers and config keys in English.
- `NOTICE.md` — credits and licence status. Do not add or choose a licence.
- `.runtime/` (ignored) — fixtures and other local output. `dist/` (ignored) — package output.

## Sibling LabAPI-Mobile checkout

The build needs LabAPI-Mobile (<https://github.com/Michaelihc/labapimobile>) checked out next to this repository as
`../labapimobile`. `Directory.Build.props` defines `LabApiMobileRoot` (default `../labapimobile`) and `CarlManaged`
(default `$(LabApiMobileRoot)/.runtime/server-original/Carl Mod_Data/Managed`); both can be overridden with `-p:`.
`ProjectMER.csproj` references `$(LabApiMobileRoot)/src/LabApi/LabApi.csproj` and compiles against the publicized game
assemblies in `CarlManaged`.

Read `../labapimobile/AGENTS.md` before working here. Its rules apply to this repository too: reference inputs and
where they live, compatibility checks against the fork's assemblies (similar names in SL 14 sources prove nothing),
porting rules, and how test servers are built and run. That checkout is a separate repository: do not edit it from a
task in this one. Changes ProjectMER needs from LabAPI (events, wrappers) are made and committed there.

## References

- Upstream ProjectMER: <https://github.com/Michal78900/ProjectMER>; MapEditorReborn for SL 13.2 (the same toy set as
  Carl Mod): <https://github.com/Michal78900/MapEditorReborn>. Local copies and decompilations are listed in
  `../labapimobile/AGENTS.md` and in the path table of `docs/projectmer-port-plan.md`.
- The Carl Mod server assemblies (`CarlManaged`) are the authority for game signatures. Client C# exports are IL2CPP
  metadata views with placeholder bodies.

## Porting rules

- Keep ProjectMER's public API, map and schematic formats, so plugins and content made for ProjectMER work unchanged.
- Adapt or skip what the client lacks; keep unsupported data loadable and savable, with one warning per type and load.
  Record every adaptation and absence in `docs/compatibility.md`, and user-visible behaviour in both READMEs.

## Performance rules

Mobile clients are the bottleneck: low-end phones render every networked object and receive every SyncVar.

- Keep networked object counts low: static toys by default, no networked objects for blocks that render nothing, spawn
  large sets across frames through the spawn queue within its budgets.
- Never dirty SyncVars with unchanged values. Edits resend static toys in place; avoid despawn/respawn churn (the client
  leaks the materials of destroyed primitives).
- No LINQ, closures, boxing or string formatting in spawn-queue, sync, culling, tool-gun loop or other per-frame paths.
- No `Update` loops where an event or a timed MEC coroutine with a sensible interval suffices.
- Game state only on the Unity main thread; file parsing and planning may run on a worker (`SchematicLoader`), and the
  file watcher goes through `MainThreadQueue`.
- Measure changes that affect the client on the Android emulator (`docs/testing.md`) and say that numbers are emulator
  numbers.

## Build and test

- `dotnet build ProjectMER-Mobile.sln -c Release`. Parallel agents add `--artifacts-path C:\tmp\pmer-<name>` so their
  obj/bin folders do not collide.
- `tools/Package.ps1` builds and packages; it fails if `ProjectMER.dll` references an assembly that neither the game,
  LabApi nor Harmony provides.
- Test servers, the installer, EventProbe and the Android tools are LabAPI-Mobile's (`../labapimobile/tools`,
  `../labapimobile/src/Installer`); `docs/testing.md` shows how to run them from here. Install into a server copy, never
  `server-original`; the server folder needs `hoster_policy.txt` with `gamedir_for_configs: true`; stop servers by PID.
