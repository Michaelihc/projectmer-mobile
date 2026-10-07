[English](README.md) | 简体中文

# ProjectMER-Mobile

[ProjectMER](https://github.com/Michal78900/ProjectMER) 2025.11.2.1（MapEditorReborn 的 LabAPI 版）在 Carl Mod 服务端上的移植。
Carl Mod 是 SCP: Secret Laboratory 的手机版分支，本插件运行在 [LabAPI-Mobile](https://github.com/Michaelihc/labapimobile)
上。它可以加载 ProjectMER 地图（`.yml`）和蓝图（schematic，`.json`，由 ProjectMER 和 MapEditorReborn 13.2 的导出工具生成），
并提供 ProjectMER 的指令和工具枪。

插件只运行在服务端：玩家使用原版 Carl Mod 安卓客户端，客户端不做任何修改。这个客户端认识的物体类型比 SCP:SL 14 少，
而且手机要渲染每一个联网物体，所以部分内容会被适配或跳过（见[支持的内容](#支持的内容)）。

## 环境要求

- 已安装 [LabAPI-Mobile](https://github.com/Michaelihc/labapimobile) 的 Carl Mod 专用服务器（安装方法见其 README）。
  LabAPI-Mobile 提供 `LabApi.dll` 和 Harmony，ProjectMER 不需要其他文件。
- 支持的服务端版本：Carl Mod 0.0.5、官方发布的 0.0.4 服务端，以及带死斗（deathmatch）模块的 0.0.4 版本。同一个
  `ProjectMER.dll` 可在这三个版本上运行，三个版本均已配合 LabAPI-Mobile 1.1.7-mobile.5 测试。
- LabAPI-Mobile 版本：Carl Mod 0.0.5 需要 LabAPI-Mobile **1.1.7-mobile.5 或更高版本**；更早的 LabAPI-Mobile 版本完全无法在
  0.0.5 上运行。在 0.0.4 上同样请使用 1.1.7-mobile.5 或更高版本（两个 0.0.4 版本上测试的就是这个版本）。
- 玩家需要使用与服务端游戏版本相同的安卓客户端：0.0.4 客户端无法加入 0.0.5 服务端。客户端上的表现（手机渲染的内容、
  工具枪按键、下文的帧时间）是在 0.0.4 上用 0.0.4 客户端验证的。在 0.0.5 上，ProjectMER 只做了服务端验证（使用服务端
  假人）：地图和蓝图、全部指令、生成队列、卸载和回合重启，以及工具枪在服务端处理的请求。

## 安装

1. 安装 LabAPI-Mobile 并启动一次服务器，生成数据目录 `<AppData>/SCP Secret Laboratory/LabAPI-Mobile/`。`<AppData>` 是运行
   服务器的用户的 `%APPDATA%`；如果服务器目录下的 `hoster_policy.txt` 包含 `gamedir_for_configs: true`，则是服务器目录里的
   `AppData` 文件夹。
2. 把发布包 `ProjectMER-Mobile-<version>.zip`（或你自己编译的结果）中的 `plugins/ProjectMER.dll` 复制到
   `LabAPI-Mobile/plugins/global/`（只给某个端口用则放到 `plugins/<port>/`）。
3. 启动服务器。日志中会出现 `ProjectMER ... enabled`，插件会创建：
   - `LabAPI-Mobile/configs/ProjectMER/Maps/`：存放地图（`<name>.yml`）；
   - `LabAPI-Mobile/configs/ProjectMER/Schematics/`：存放蓝图，每个蓝图一个文件夹：`Schematics/<name>/<name>.json`，
     `<name>-Rigidbodies.json` 和动画 bundle 放在同一文件夹。直接放在 `Schematics/<name>.json` 的蓝图会在第一次使用时
     被移进自己的文件夹；
   - `LabAPI-Mobile/configs/<port>/ProjectMER/config.yml`：配置文件。
4. 在 `LabAPI-Mobile/configs/permissions.yml` 中授予权限：每条指令一个 `mpr.<command>`（例如 `mpr.load`），或者直接
   `mpr.*`。

## 指令

远程管理（RA）指令 `mapeditor`（别名 `mer`、`mp`）。单独输入 `mp` 可查看列表。在服务器控制台中输入时要加前导斜杠
（`/mp list`）。

| 指令 | 别名 | 用途 |
| --- | --- | --- |
| `load <map>` | `l` | 加载地图。对已加载的地图再次执行会重新加载。 |
| `unload [map]` | `unl` | 卸载一张地图，或卸载全部地图。 |
| `save <map>` | `s` | 保存已加载的地图，包括之后新建的物体（`Untitled` 地图）。如果地图文件存在但无法读取，文件保持不变，保存失败。 |
| `list` | `li`、`ls` | 列出地图和蓝图（带编号，供 `mp tg schematic` 使用）。 |
| `merge <new> <map> <map>...` | | 把多张地图合并成一张新地图。 |
| `create <type\|schematic> [x y z]` | `cr`、`spawn` | 在准星位置或指定坐标创建物体。 |
| `delete [id]`、`select [id]` | `del`；`sel` | 删除或选中准星指向的物体，或按 id 操作。 |
| `position`、`rotation`、`scale` `set\|add ...` | `pos`、`rot`、`scl` | 移动、旋转或缩放选中的物体。`mp pos bring` 把物体移到你身边，`mp pos grab` 让物体跟随你移动。 |
| `modify <property> <value>` | `mod` | 修改选中物体的属性。 |
| `toolgun [schematic <name\|index>\|type <type>\|mode <create\|delete>]` | `tg` | 给予或收回工具枪；子指令用来选择要创建的内容。 |
| `indicators [on\|off\|off all]` | `i` | 向你显示不可见的物体（出生点、传送点、不可见的图元）。 |
| `stats` | `st` | 联网物体数量、预算、生成队列，以及每个玩家能看到的物体。 |
| `optimize <schematic> [map\|api]` | `opt` | 显示蓝图优化器对某个蓝图的处理结果：每一步之后的联网物体数和生成字节数。 |
| `prefabs` | `pf` | 列出网络预制体。 |

`stats`、`optimize` 和 `prefabs` 也会把报告写入服务器日志。地图名和蓝图名必须是 `Maps`、`Schematics` 文件夹中的普通文件名：
带路径（`/`、`\`、`..`）、盘符或其他文件名非法字符的名称会被拒绝。

**在手机上使用远程管理。** 客户端的控制台（游戏内的“控制台”按钮）会把以 `/` 开头的输入作为远程管理请求发送，例如 `/mp tg` 或
`/mp pos add 0 0.5 0`。玩家需要通过 `config_remoteadmin.txt` 获得远程管理权限。回复不会显示在控制台里（请在服务器日志中
查看），但每条编辑指令的效果都会直接体现在场景和工具枪 HUD 上。

## 手机上的工具枪

工具枪是一把空弹匣的 FSP-9（Carl Mod 客户端对空弹匣的半自动枪，例如 ProjectMER 使用的 COM-18，从不发送空枪击发）。
触屏按钮对应如下：

| 按钮 | 功能 |
| --- | --- |
| 攻击，点按 | 按当前模式在准星处创建或删除。按住也只执行一次。 |
| 瞄准（鼠标图标），然后攻击 | 选中准星处的物体；没有物体时取消选择。无碰撞的物体也能选中。 |
| 手电开关（((•)) 图标） | 切换创建/删除模式；创建模式下手电亮起。 |
| 检视 | 同样切换模式，但客户端还会播放约 3 秒的检视动画。 |
| R（换弹）和丢弃箭头 | 在循环列表中切换上一项和下一项：先是所有蓝图，然后是物体类型。 |

准星下方的 HUD 显示当前模式和类型（或准星下的物体）、选中的物体、抓取状态和所在房间。Carl Mod 没有服务器专属设置
（server-specific settings），所以要创建的蓝图通过 `mp tg schematic <name|index>` 或循环切换来选择。工具枪不会变成普通
武器：如果它不是通过 `mp tg` 离开物品栏（死亡、被铐、逃离），就会被销毁；SCP-914 也不会改变它。

## 配置

ProjectMER 原有的选项保持不变：`enable_file_system_watcher`、`auto_select`，以及地图动作 `on_waiting_for_players`、
`on_round_started`、`on_lcz_decontamination_started`、`on_warhead_started`、`on_warhead_stopped`、
`on_warhead_detonated`（`load:<map>`、`unload:<map>`、`console:<command>`；见 `config.yml` 中的注释）。开启文件监视后，
已加载地图的文件在磁盘上变化时会自动重新加载；`mp save` 自己会重新加载地图，不会再触发第二次加载。

移动端选项：

| 选项 | 默认值 | 含义 |
| --- | --- | --- |
| `static_by_default` | `true` | 蓝图方块默认是静态 toy（不逐帧同步），属于动画或物理部分的方块除外。`false`：使用 ProjectMER 的规则，只有导出时带 `"Static": true` 的方块是静态的。 |
| `honor_static_property` | `false` | 在 `static_by_default` 下，导出时带 `"Static": false` 的方块变为动态。导出工具会给从不移动的方块也写 `false`，建议保持关闭。 |
| `spawn_max_per_frame` | `10` | 内容流式加载时，每个服务器帧最多联网生成的物体数；对加入和区域流式同步则是每个玩家的上限（约每秒 600 个物体）。 |
| `spawn_time_budget_ms` | `3` | 每帧用于联网生成排队物体和构建蓝图的服务器时间。 |
| `dynamic_toy_sync_interval` | `0.1` | 动画或物理方块两次同步位置之间的秒数。 |
| `allow_light_shadows` | `false` | MER 灯光是否可以投射阴影。 |
| `light_intensity_scale` | `0.025` | MER 灯光强度的倍率。ProjectMER 内容使用官方 SL 的 HDRP 强度（导出的灯强度为 60 很常见）；Carl Mod 使用 Unity 内置渲染管线，1 到 2 就是正常亮度，60 会让所有受光表面变成白色。为 Carl Mod 制作的内容请设为 `1`。 |
| `max_lights` | `16` | 同时加载的 MER 灯光上限，按强度 × 范围从强到弱保留。 |
| `max_lights_per_schematic` | `4` | 每个蓝图保留的灯光上限，从强到弱；`-1` 表示不限制。 |
| `primitive_warn_per_schematic` | `300` | 单个蓝图联网的方块超过此数量时发出警告。 |
| `networked_warn_total` | `1500` | 所有 MER 内容联网的物体总数超过此数量时发出警告。 |
| `networked_hard_cap` | `4000` | 超过此数量后拒绝生成更多 MER 物体。 |
| `invisible_collider_mode` | `Transparent` | 只有碰撞的图元：`Transparent`（alpha 为 0，仍会绘制）或 `Skip`（不生成，没有碰撞）。 |
| `optimize_schematics` | `true` | 不联网生成在客户端上不产生任何效果的蓝图方块（空组、不可见图元、缩放为零）。 |
| `merge_blocks` | `Maps` | 在蓝图的静态部分去除重复方块，并合并立方体和四边形：`None`、`Maps`（地图、`mp create` 和工具枪放置的蓝图）或 `All`（也包括插件生成的蓝图）。 |
| `managed_visibility` | `true` | MER 物体按玩家分别控制可见性（加入时分批同步、仅管理员可见的指示器）。 |
| `zone_culling` | `SurfaceFacility` | 对处于设施其他部分的玩家隐藏大型地图和蓝图：`None`、`SurfaceFacility` 或 `PerZone`。 |
| `zone_culling_min_objects` | `150` | 只有联网物体不少于此数量的组才会按区域剔除。 |
| `hud_interval` | `0.5` | 检查工具枪 HUD 输入的间隔秒数。HUD 有变化时立即发送，没有变化时每 `max(2, 4 × hud_interval)` 秒发送一次（客户端每 5 秒最多接受 10 条提示）。 |
| `warhead_spares_outside_rooms` | `true` | 位于地表以下且不在任何房间内的位置不会被核弹波及，与 ProjectMER 相同。 |
| `log_spawn_stats` | `true` | 每次生成队列清空时记录一行吞吐量日志。 |

## 移动端限制与性能默认值

以下数据是在安卓模拟器中用 Carl Mod 0.0.4 客户端测得的（x86_64 AVD，通过二进制翻译运行 ARM 客户端），不是在真机上
测得的。它们用于比较不同配置，真机结果会有差异。完整测量结果见
[docs/projectmer-port-plan.md §5.3](docs/projectmer-port-plan.md#53-verification-plan)。

- **每个联网物体都会给每个玩家增加一次绘制调用。** 任一位置可见的 MER 图元最好控制在约 150 个以内。每个可见立方体每帧
  大约消耗 5-6 µs：150 个立方体让客户端从 50 FPS 降到 49 FPS，500 个降到 44 FPS，2000 个降到 31 FPS。`mp stats` 显示
  总数；上面的警告选项会提示过大的内容。透明图元（alpha 小于 1、不可见碰撞体）还会额外消耗填充率。
- **默认静态。** 静态 toy 保持生成消息中的变换，每帧没有开销。编辑静态物体（`mp pos`、`mp rot`、`mp scale`、
  `mp modify`、移动蓝图）时会原地重发：手机更新的是同一个物体，不会出现重复，也不会泄漏材质。只有蓝图的动画和物理部分
  是动态的，每隔 `dynamic_toy_sync_interval` 同步一次；即使不动，500 个动态立方体的帧时间也比 500 个静态立方体多约 7%。
- **客户端没有父子层级。** 蓝图方块按世界变换生成，方块层级只存在于服务端。需要错切父节点的内容（三角形）无法显示。
- **灯光。** 默认没有阴影，总数受 `max_lights` 限制，每个蓝图受 `max_lights_per_schematic` 限制。聚光灯会变成点光源，
  强度乘以 `light_intensity_scale`。无阴影灯光开销很小（8 或 16 盏灯都在多次测量的波动范围内）；8 盏带阴影的灯光会让
  帧率下降约 20%。
- **加载。** 蓝图文件在工作线程上解析和规划，方块在之后的若干帧内按 `spawn_time_budget_ms` 构建并联网生成，默认设置下
  约每秒 600 个物体。一个 2500 块的蓝图大约 4 秒后对玩家完整可见。手机会在一帧内实例化该帧收到的所有物体：加载 2000 个
  立方体时，`spawn_max_per_frame` 为 10 时客户端最差帧约 110 ms，20 时约 170 ms，50 时 300-500 ms，因此默认值为 10。
- **蓝图优化器。** 在客户端上不产生效果的方块只作为服务端锚点保留。开启 `merge_blocks` 时，会去除完全重复的方块（部分
  透明的除外，因为叠加后颜色会变深），并合并共享完整面的不透明立方体，以及共面、朝向相同且共享完整边的四边形；合并后的
  方块所占空间与被替换的方块完全一致。常见的活动蓝图联网物体比 ProjectMER 少 10-16%（Skeld 2923 → 2447，Shipment
  1130 → 970），其中合并最多贡献 5 个百分点。任意蓝图都可以用 `mp optimize <schematic>` 查看数据。
- **在手机上销毁物体代价很高。** 客户端从不释放被销毁图元的材质：每个被销毁的图元大约残留 6 KB（一个 500 物体的区域
  隐藏后再显示：约 3 MB）。避免在一局中反复卸载和加载大型内容；区域剔除也因此只区分地表和设施。
- **门。** MER 门带有路径点（waypoint），玩家、布娃娃和掉落物的位置都相对于这些路径点发送。本移植会让服务端的路径点
  编号与客户端保持一致；地图可以添加门，直到设施中共有 223 扇门（在生成的设施之外约 125 扇 MER 门）。正在等待生成的门
  计入上限，正在卸载的门不计入。

## 支持的内容

[docs/compatibility.md](docs/compatibility.md)（英文）列出了每种地图物体类型、蓝图方块类型和 API 差异。概要如下：

| 内容 | 状态 |
| --- | --- |
| 图元、灯光、工作台、物品和玩家出生点、射击靶、传送点、蓝图 | 支持，或已适配客户端（见上面的限制）。 |
| 门 | LCZ、HCZ 和 EZ 门；跳过 Bulk 门和大门（gate）。 |
| 储物柜 | 支持 16 种中的 12 种；跳过 SCP-1576、Anti-SCP-207、SCP-1344 展台和实验武器柜。 |
| 蓝图方块 | 空物体、图元、灯光、掉落物和工作台方块，动画 bundle 和 `-Rigidbodies.json`。 |
| 三角形、水豚、文本、交互物、SCP-079 摄像头、路径点；蓝图中的文本、交互物、路径点和三角形方块 | 不支持：客户端没有这些物体。它们仍保留在加载的数据中，每次加载对每种类型记录一条警告，保存地图时也会保留。 |
| 基于 EXILED 的 SL 13.2 版 MapEditorReborn 的地图 | 无法加载（格式不同）。它的蓝图可以加载。 |

### 插件开发者须知

像引用其他 LabAPI-Mobile 插件一样引用 `ProjectMER.dll`。API 与 ProjectMER 相同，区别如下：

- `ObjectSpawner.SpawnSchematic` 会立即返回，蓝图在之后的若干帧内构建。`SchematicObject.IsSpawned` 和
  `Schematic.SchematicSpawned` 表示所有服务端物体都已存在；`SchematicObject.IsBuilt` 和 `Schematic.SchematicBuilt`
  表示所有方块都已到达玩家。访问 `AttachedBlocks`、`NetworkIdentities`、`AdminToyBases`、`AnimationController` 或设置
  `IsStatic` 时，会先同步完成构建（`SchematicObject.EnsureSpawned()`），所以为 ProjectMER 编写的代码看到的是完整蓝图。
- `Schematic.SchematicSpawning` 在构建前触发。如果文件尚未解析，则在工作线程解析完成后触发；取消该事件会销毁之前返回的
  蓝图对象。
- 方块不是蓝图的子物体：`AttachedBlocks` 包含服务端锚点和联网物体；每个联网物体都有一个 `MerBlockLink`，指回所属蓝图和
  方块 id。
- 被合并和重复的方块（见 `merge_blocks`）只保留锚点（名称、变换），没有自己的 toy。设置 `IsStatic = false` 会先把它们
  重新单独联网，再把方块变为动态。
- 方块数据无法使用的蓝图（例如掉落物的 `Chance` 不是数字）会在构建时报错并被销毁；此时 `ObjectSpawner.SpawnSchematic`
  已经返回了它。
- ProjectMER 在回合重启时重置回合状态（已加载的地图、生成队列、可见性），而不是在 `WaitingForPlayers` 时，所以任何插件
  在 `WaitingForPlayers` 处理器中生成的 MER 内容都会保留。

## 从源码构建

要求：.NET SDK 8 或更高版本，以及用于编译的 Carl Mod 服务端文件。把两个仓库克隆到同一目录下：

```powershell
git clone https://github.com/Michaelihc/labapimobile
git clone https://github.com/Michaelihc/projectmer-mobile
cd projectmer-mobile
dotnet build ProjectMER-Mobile.sln -c Release
.\tools\Package.ps1          # 发布包：dist\ProjectMER-Mobile-<version>.zip
```

构建会编译 `../labapimobile/src/LabApi`，并引用 `../labapimobile/.runtime/server-original/Carl Mod_Data/Managed` 中的游戏
程序集，该目录由 LabAPI-Mobile 的 `tools/extract-server.py` 生成（见其 README）。两个位置都可以覆盖：
`-p:LabApiMobileRoot=<checkout>` 和 `-p:CarlManaged=<server>\Carl Mod_Data\Managed`（`Package.ps1` 对应参数为
`-LabApiMobileRoot` 和 `-CarlManaged`）。`CarlManaged` 必须是 0.0.4 服务端（任一 0.0.4 版本）的 `Managed` 文件夹：
LabAPI-Mobile 的源码使用 0.0.5 已改名的 0.0.4 成员名。编译结果可在全部三个服务端版本上运行。如果 `ProjectMER.dll` 引用了
游戏、LabAPI-Mobile 和 Harmony 都不提供的程序集，`Package.ps1` 会报错。

[docs/testing.md](docs/testing.md)（英文）介绍了测试服务器、测试数据（`tools/make-mer-fixtures.py`）和安卓客户端上的检查，
使用的是 LabAPI-Mobile 仓库中的测试工具。

## 致谢

- [ProjectMER](https://github.com/Michal78900/ProjectMER)（MapEditorReborn 的 LabAPI 版），作者
  [Michal78900](https://github.com/Michal78900)；最初的插件构想和代码重构来自 [Killers0992](https://github.com/Killers0992)，
  另一次代码重构和文档来自 [Nao](https://github.com/NaoUnderscore)，测试由 Cegła、The Jukers 服务器管理团队及其他人完成，
  以上均按上游的致谢列出。
- [MapEditorReborn](https://github.com/Michal78900/MapEditorReborn)，ProjectMER 的前身。
- [LabAPI-Mobile](https://github.com/Michaelihc/labapimobile)，本插件运行所依赖的 LabAPI 移植。

SCP: Secret Laboratory 是 Northwood Studios 的游戏；Carl Mod 是它的第三方手机版本。本仓库不包含任何游戏文件。

## 许可

本仓库中的修改和新增内容（Carl Mod 移植部分）采用 Creative Commons 署名-相同方式共享 3.0 未本地化版本（CC BY-SA 3.0 Unported，见 [LICENSE](LICENSE)）许可，与 MapEditorReborn 源文件声明的许可证一致。来自上游 ProjectMER 的代码仍归其作者所有；上游仓库没有发布许可证文件，因此本仓库不能就这部分代码授予其作者授予范围之外的任何权利。致谢见 [NOTICE.md](NOTICE.md)。
