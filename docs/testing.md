# Testing ProjectMER

ProjectMER is tested on a local Carl Mod server with LabAPI-Mobile installed and, for client behaviour and frame times,
with the stock Carl Mod Android client in an Android emulator. The test tools belong to LabAPI-Mobile and live in the
sibling `labapimobile` checkout:

| Tool (in `../labapimobile`) | Use |
| --- | --- |
| `tools/Start-TestServer.ps1`, `Stop-TestServer.ps1`, `Send-ServerCommand.ps1` | Local test servers and the file console. |
| `tools/android/*.ps1` | Android SDK setup, emulator, client install and connection, captures, frame-time measurement. |
| `tools/EventProbe/` | Developer plugin: mirrors the console into the server log and has commands that drive tests. |
| `src/Installer/` | Installs `LabApi.dll` and `0Harmony.dll` into a server copy. |

LabAPI-Mobile's [docs/testing.md](https://github.com/Michaelihc/labapimobile/blob/main/docs/testing.md) explains these
tools (emulator setup, ports, the file console, UI coordinates, frame-time method). The scripts keep their state in the
`.runtime/` folder of the `labapimobile` checkout (servers, logs, PID files, captures). Run them with absolute paths;
the examples below set `$lam` to the checkout and `$mer` to this repository.

```powershell
$mer = (Resolve-Path .).Path                    # this repository
$lam = (Resolve-Path ..\labapimobile).Path      # the LabAPI-Mobile checkout
```

## Build and install on a test server

```powershell
$server = "$lam\.runtime\server-emu"
if (-not (Test-Path $server)) {                 # a server copy; never install into server-original
    robocopy "$lam\.runtime\server-original" $server /E /NFL /NDL /NJH /NJS /NP | Out-Null
    Set-Content "$server\hoster_policy.txt" 'gamedir_for_configs: true' -Encoding ASCII
}
dotnet build "$mer\ProjectMER-Mobile.sln" -c Release --artifacts-path C:\tmp\pmer-test
dotnet build "$lam\tools\EventProbe\EventProbe.csproj" -c Release --artifacts-path C:\tmp\pmer-test
dotnet run --project "$lam\src\Installer" -c Release -- $server C:\tmp\pmer-test\bin\LabApi\release
$plugins = "$server\AppData\SCP Secret Laboratory\LabAPI-Mobile\plugins\global"
New-Item -ItemType Directory -Force $plugins | Out-Null
Copy-Item C:\tmp\pmer-test\bin\ProjectMER\release\ProjectMER.dll, C:\tmp\pmer-test\bin\EventProbe\release\EventProbe.dll $plugins
& "$lam\tools\Start-TestServer.ps1" -ServerDir $server -Port 7791 -CommandSession emu -LogPath "$lam\.runtime\logs\server-emu-7791.log"
```

`hoster_policy.txt` keeps the server's configs in its own `AppData` folder instead of your real `%APPDATA%`. The log shows
`[LabApi] [PATCHES] Applied ... (0 failed)` and `ProjectMER ... enabled`. Send commands with
`& "$lam\tools\Send-ServerCommand.ps1" -ServerDir $server -Command "mp list"` and read the replies in
the server log (EventProbe mirrors the console there). Stop the server with
`& "$lam\tools\Stop-TestServer.ps1" -Port 7791`.

## Fixtures

```powershell
python tools/make-mer-fixtures.py [--real <folder of real ProjectMER schematics>]
```

writes synthetic schematics and maps to `.runtime/mer-fixtures/{Schematics,Maps}` in this repository. `--real` (or the
`MER_REAL_SCHEMATICS` environment variable) copies the real schematics named in the script (Battle, Jail, Shipment,
Skeld, 35Hp, DeathParty) from a folder with one subfolder per schematic; without it they are skipped. Copy the output to
`LabAPI-Mobile\configs\ProjectMER\{Maps,Schematics}` of the test server. Fixtures are never committed.

- **Server-side fixtures** (`Grid*`, `FlagMatrix`, `NestedEmpties`, `Lights*`, `Legacy13`, `UnsupportedBlocks`,
  `Physics`, `EveryType` and their `Map*` maps) sit at x 20, z -60 above the surface, in the void outside Carl Mod's
  closed surface hall. They are for server checks: load reports, warnings, `mp stats`, `mp optimize`, save round trips.
- **Android client fixtures** (`A*`) are built around the surface NTF spawn (below).

## ProjectMER on the Android client

Install `ProjectMER.dll` next to `EventProbe.dll` as above, and copy the fixtures to
`LabAPI-Mobile\configs\ProjectMER\{Maps,Schematics}`. EventProbe mirrors the console into the server log (ProjectMER's
command replies and load reports are read there) and its commands drive the tests: `probe tp`/`probe yaw` give
repeatable views, `probe as <id> mp ...` runs player-bound ProjectMER commands (`select`, `pos`, `mod`...) as that
player, `probe elevator`, `probe netobj` and `probe waypoints` check rides, object state and door waypoints
([EventProbe README](https://github.com/Michaelihc/labapimobile/blob/main/tools/EventProbe/README.md)).

- **The stage.** Carl Mod's surface is a closed hall, and the server-side fixtures at x 20, z -60 float in the void. The
  `A*` fixtures are built for a player at the NTF spawn: `/forceclass <id> 13`, `probe tp <id> 132.76 995.4 -38.76`,
  `probe yaw <id> 180` looks down the hall (floor at y 994.5). `AWall150/500/2000` (and `AWall500Dyn`) are walls of
  0.2 m cubes 14 m ahead, `AFlagWall` every flag and sign pattern, `AWalkLanes` walk-through lanes, `ASeams` rotated
  rows, `AEveryType` doors, workstation, lockers, pickups, a target and teleports, `ALights8/16/8Shadows` lights over the
  wall, and `AVoid<real schematic>` places a copied real schematic at y 1100 above the hall (teleport into it, and back
  to the stage before unloading it, or the player falls into the void).
- **Pitch.** The server can set only the yaw. Swiping the view changes the client's pitch until the next respawn, so
  respawn (`forceclass`) before frame-time runs and do not touch the screen during them. The role intro text stays on
  screen for about 6 s after a respawn.
- **Remote Admin.** Add the AVD's device id to `Members` in `config_remoteadmin.txt` and grant `.*` to that group in
  `LabAPI-Mobile\configs\permissions.yml`; the client log then shows "Your remote admin access has been granted" when
  it connects. The 管理 (RA panel) button did not open the panel on the emulator, but the client console (控制台, tap the
  input at 1070, 918, type with `adb shell input text`, PROCEED at 1862, 918) sends any `/command` as a Remote Admin
  request. RA replies are not printed in the console; read them in the server log.
- **Tool gun buttons** (with the FSP-9 equipped from the top item bar, third slot at 1200, 50 for the NTF loadout):
  attack 1728, 786 (or the left one at 72, 344), aim (mouse icon) 1946, 548, light toggle 1946, 270, inspect 254, 432,
  reload (R) 1896, 984, throw away 72, 832.
- **Elevators.** Every round generates the facility again, so look up the chambers with
  `probe goto <id> ElevatorChamber <n> 0` (y about 995 is a gate at the top, y about -1000 at the bottom) and use
  `probe elevator <id>` to ride. A player inside a gate chamber sees both zones; zone culling hides the other zone only
  after the player has left the chamber.
- **Frame time.** `& "$lam\tools\android\Measure-FrameTime.ps1" -Name <label> -DurationSec 30` at the stage view; take
  at least three runs per configuration and interleave the configurations. The emulator runs the ARM client through
  binary translation, so the numbers compare configurations and are not phone numbers.
- **Client memory.** `adb -s emulator-5554 shell dumpsys meminfo com.carlmod.game` (TOTAL PSS) shows the material leak of
  destroyed primitives ([projectmer-port-plan.md §5.3](projectmer-port-plan.md#53-verification-plan)). Restart the
  client between long measurement series.
