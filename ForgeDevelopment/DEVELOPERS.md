# ForgeDevelopment 开发者说明

## 量握持手型的手指姿势

`tools/capture_finger_poses.py --session <session.forge> --output <文件>` 在已落地的会话上（手里要有一把枪）把游戏每种握持动画下左右手 15 个指关节的姿势量出来写成 JSON，经 `Native/Experiments/FingerPoseCapture.cs`（`Names`、`Joints`、`Play`、`Read`）：一次换一种动画，用游戏自己的做法换（改手里枪的部件持有者上的动画名，再让背包重播持握动画），之后的调用里读，连续两次读数相同才记；自己会动的动画列在 `moving` 下。ForgeSkin 后端的 `hand-shapes.json` 就是它量的。

## 实时接管运行中的游戏

`tools/drive_cli.py` 让 AI 或人在一个已落地的常驻会话上逐条下指令。每个子命令立即返回一行 JSON；同一套指令也可写成 `*.test` 测试脚本整份执行。它与 `run-drive-session.py` 共用 `drive_session.Client`（status、call、command）和 `MenuDrive` 读回，不另有传输。

```
python Mods/ForgeDevelopment/tools/run-drive-session.py start --package <包> --work-dir artifacts/work/drivesession/<名>
python Mods/ForgeDevelopment/tools/run-drive-session.py start --rundown <大关 id> --level A1 --work-dir artifacts/work/drivesession/<名>
python Mods/ForgeDevelopment/tools/drive_cli.py --session <session.forge> <子命令>
```

`--package` 进 Forge 包里的关；`--rundown <id> --level <层+序号>` 不要包，直接进已装的大关（原版或 MTFO 都行），两者必给其一。`--profile` 是 r2modman 档名（默认 Forge-Multiplayer），`--game` 是游戏目录（不给就按 Steam 注册表与 `libraryfolders.vdf` 找 GTFO），`--lock` 是游戏锁文件（也可用环境变量 `FORGE_GAME_LOCK`）。档里装了 ForgeEngine 时才把它的 `[Runtime] Mode` 租成 Authoring，没装就只用 ForgeCore 与本包，Forge 地图专属的读数（`call static:ForgeEngine.Authoring.AuthorReads …`）答 `forge-engine-not-installed`。

工具可以拷出仓库单独用：把 `tools/` 下的 `run-drive-session.py`、`drive_session.py`、`run-experiment.py`、`rec_segments.py`、`installed_pack.py`、`drive_cli.py`、`console_commands.py`、`console_run.py`、`test_script.py` 与 `Tools/Game/` 下的 `game_process.py`、`game_profile.py`、`safe_delete.py` 放进同一个文件夹即可。仓库外运行时必须给 `--lock`（或 `FORGE_GAME_LOCK`），`--work-dir` 可以是任意目录，`--profile` 不限于仓库允许的档；探针从档里装好的 `plugins/NAinfini-ForgeDevelopment/ForgeDevelopment/probes` 读。

| 子命令 | 作用 |
| --- | --- |
| `status` | 游戏状态、心跳年龄、正在执行的命令、世界、大厅读回；关内再读玩家（存活、位置、维度）。心跳过期或有命令在跑时只报告并说明原因，不提交任何请求 |
| `lobby` | 菜单、大厅与槽位的原生读回 |
| `drop <rundown> <tier> <ordinal> [--bots JSON] [--timeout 秒]` | 从菜单或大厅进到指定关卡并落地；每个阶段与每 15 秒一次的等待状态实时打到 stderr |
| `call <static:类型> <方法> [JSON 参数…] [--until <方法> <前缀> --timeout 秒]` | 调用静态方法（运行时按名找类型，任何已加载模组的都行，本包不引用它们）；`--until` 再轮询同一类型的一个无参方法，直到回答以前缀开头 |
| `read <目标> <路径…>` | 原生字段读回 |
| `step '<JSON>'` / `step @文件` | 执行 drive 步骤；数组用 `--target`，或写成 `{"target":…,"steps":[…]}` |
| `log [--tail N]` | `BepInEx/LogOutput.log` 与最新 `ForgeReports` 记录的末尾 |
| `screenshot` | 让游戏自己截图（ForgeDevelopment.Native.Screenshot，ScreenCapture），文件落盘后返回路径；窗口在后台、被遮挡都可以 |
| `quit` | 请求常驻会话关闭游戏 |

按名调用的通用能力：`call static:ForgeDevelopment.Native.SeededDrop Start <关卡> ["build,hostId,session"] [机器人数]` 以指定三个种子单人开局（先离开大厅或关卡，菜单起来后经 ForgeCore `SoloDrop`；`State` 读进度），两次同种子进关是同一张图；`call static:ForgeDevelopment.Native.LevelScenario Start [stay]` 在关内无人操作地打一场并做一轮关卡互动（刷怪、开火与近战、走跑蹲跳、投掷、聊天、噪声、标记、哨戒炮与地雷、药包、倒地与救起、资源箱、安全门、反应堆、扫描、发电机、拾取、终端、生存波、检查点存读），120 秒后离开关卡（`stay` 为 true 时留在关内），每步在 `LogOutput.log` 写 `scenario: did …` 或 `scenario: FAILED …`，`State` 读进度。`call static:ForgeDevelopment.Native.SignLayers Read` 读回关内每块带底板的字（字牌、世界标签、带底的模型标签）的层序：字的主材质与每个后备字体子网格材质的渲染队列和深度测试、底板的队列，答 `boards=;materials=;subMeshes=;wrong=;noDepthProperty=;first=`（`noDepthProperty` 是着色器两个深度测试属性都没声明、读不到深度测试的材质数）（`scripts/sign-layers.test` 用它断言 `wrong=0`）。`call static:ForgeDevelopment.Native.FirstPersonWearProbe Report` 读回穿在本机第一人称身体上的模型（人物自己的手臂）：原版第一人称动画器是否人形、30 个手指骨有几个，原版手臂渲染器几个、被藏几个，每个穿戴模型画没画、阴影设置、三角形数，挂在原版手骨与手指骨上的模型骨与原版关节的最大距离与缩放；同一类型的 `Followed` 读 ForgeCore 每帧跟随的穿戴列表（每件开没开、显没显、原版部件藏没藏、模型与所穿身体是否还在、原版手臂物体开关），`Drawn` 列出第一人称里此刻实际在画的渲染器（路径、层、是否蒙皮）。`run-drive-session.py start --perf` 把 `EnablePerformanceLogging` 租为开，性能日志写在 `BepInEx/PerformanceLogs`（帧时间分位、主线程托管分配），用 `tools/analyze_performance.py` 读。

`--wait 秒`（默认 30）是单次调用等游戏接受命令的上限，由调用方决定；超过即带原因报错，不会像脚本流程那样等满 180 或 300 秒。进关（`drop`）本身要几分钟是游戏加载（`Generating`、`StopElevatorRide`），期间另开一个终端用 `status` 就能看到阶段，不要重复提交。

每次调用的 `driver-timing.jsonl` 写在会话目录的 `cli/<时间>/` 下。

`call`、`step` 里任何一步答回一个 `GameObject`，这个物体都会被当成实验造出来的东西，在这条命令结束时销毁（`ExperimentRuntime.Track`，为清理 `Instantiate` 出来的副本而设）。所以不要拿 `GameObject.Find` 去取场景里已有的物体再往下调：命令一结束那个物体就没了（用它取挑选器的卡做悬停，会把卡销毁）。要调某个组件，从一个答回组件或托管对象的入口出发（静态属性、`get_Current` 之类），或用对应的指令（如 `picker hover`）。

## 指令台与测试脚本

同一个 `drive_cli.py` 也是指令台：下面每条指令既能单条敲，也能一行一条写进 `.test` 脚本，由 `run` 跑。进关由 `run-drive-session.py start` 做，指令台只接管已落地的会话。指令集在 `tools/test_script.py` 的 `VERBS`（语法与比较都在这里，不需要游戏），执行在 `tools/console_commands.py`，记录在 `tools/console_run.py`。

```
python Mods/ForgeDevelopment/tools/drive_cli.py --session <session.forge> fire 6 at=s1      # 单条（先 spawn 出 s1 再打）
python Mods/ForgeDevelopment/tools/drive_cli.py --session <session.forge> run Mods/ForgeDevelopment/scripts/combat-basics.test
python Mods/ForgeDevelopment/tools/drive_cli.py --session <session.forge> replay <会话目录>      # 把记录的指令在活游戏上重放
python Mods/ForgeDevelopment/tools/drive_cli.py replay <会话目录> --recorded [--script 新脚本]    # 不开游戏，按记录的读数重新判断每条 expect
```

退出码：0 全过，1 有断言或指令失败，2 没有运行（脚本语法错、会话不在）。语法错在动游戏之前整份拒绝并指出行号。

| 类别 | 指令 |
| --- | --- |
| 关卡 | `level [大关 层 序] [seed= timeout= bots=]`、`leave`、`reenter` |
| 位置视线 | `teleport <x y z> [dim= face=]`、`teleport room=<房间> <x y z>`（房间局部坐标）、`teleport zone=<区> [area=]`、`teleport exit=<出口> [offset=x,y,z]`（游戏把人放到请求点下方的导航体素节点上，约半米一格，读回与这个采样落点比：水平 5 厘米内、竖直在台阶高度与 0.6 米中较大者内；结果是实际落点）；`look <x y z>`、`aim <目标>`（`--ads` 开镜） |
| 玩家战斗 | `fire [N 或 Ns] [at=名字]`、`melee [charge=秒] [at=]`、`wield <primary\|secondary\|melee\|tool\|consumable\|pack\|武器名>`、`reload`、`throw [charge= at=]`、`jump`、`crouch`、`stand` |
| 玩家移动操作 | `move forward\|back\|left\|right <秒> [run]`、`move to <x y z>`（朝着点按前进，到 30 秒为止）、`interact [x y z] [name=名字] [radius=米] [hold=] [at=]`、`press/hold/release/key <动作>` |
| 状态 | `god on\|off`、`give <物品> [数量]`、`ammo fill`、`down`、`revive` |
| 敌人 | `spawn <敌人> [数量] [at=front:8] [mode=hibernate\|aggressive] [as=名字]`、`enemies [半径]`、`clear enemies\|groups`、`hit`、`limbs`、`follow` |
| 世界 | `door read\|open <门 JSON 或 near:半径>`（`near:` 以玩家所在位置为圆心）、`noise <半径>`、`event <JSON>`、`checkpoint save\|restore\|confirm\|wipe`、`scan read <出口>`、`extract <出口>`、`nav near\|path\|sample\|voxel <JSON>`、`model-shot <JSON>`、`terminal type "<一行>"\|exit\|read [行数]` |
| 菜单 | `picker hover\|leave <卡序号>`：打开着的 ForgeCore 挑选器当作光标进入／离开列表里第几张卡（调挑选器自己的悬停入口 `Picker.Hover`，不动鼠标），答 `focus`（详情此刻跟着的卡，-1 为该格正在用的）；截悬停前后图用它 |
| 画面对比 | `toon forward\|deferred\|off [对象名]`：把本机手里的物品（或按名字找到的激活物体）上带底色贴图的不透明材质换成 ForgeCore 卡通着色器的前向接法或延迟接法（经 ForgeCore `ToonDress`，与角色皮肤的卡通材质同一条路；延迟接法另在每个渲染器下放一份叠加层），`off` 换回原材质；换武器、枪模型刷新外观或换关后要再敲一次。只在开发模组里 |
| 读取 | `player [路径…]`、`read <目标> <路径…>`、`state ammo\|stamina\|fog\|intel\|hud\|lifecycle\|variables`、`alarms [all]`、`recorder`、`log [tail= grep=]`、`status`、`screenshot`、`call`、`step`、`quit` |
| 脚本控制 | `expect <读取> <比较符> <值>`、`wait <n>s\|<n>f`、`wait until <读取> <比较符> <值> timeout=<n>s`、`stop-on-fail`、行尾 `as=名字` 把这条指令的回答记成 `$名字`，`#` 注释 |

开火、近战、重装、投掷、跳蹲站默认模拟玩家按键（`GameInputDrive` 把键喂进游戏自己的输入，租约随这条指令结束而释放）；加 `--direct` 改为直接调原生方法（`throw --direct` 松手时调手里投掷物自己的 `Throw(m_throwStrength)`，即原版松手那一步）。`give` 给的消耗品带满该物品的次数（`ConsumableAmmoMax`，与关卡里的消耗品一样），手里拿的不是枪时 `state ammo` 照样读：消耗品的剩余次数在背包的 `consumable` 一栏（扔一次减一），`clip` 只对枪有意义。`key` 收的是键名，`press/hold/release` 收的是游戏动作名（如 `press Flashlight`）。`expect` 读取：`player Position.y`、`state ammo clip`、`enemy <id 或 $名字.id> health`、`alarms active`、`read <目标> <路径>`、`$名字.字段`、`log`（看上一条指令开始以来写进的日志行，包括它返回之后才写的，`--all` 看整个会话）；比较符 `== != < <= > >= contains !contains`，`~0.05` 为容差。

几条指令的结果与失败码：

| 指令 | 结果字段 | 失败 |
| --- | --- | --- |
| `wield <槽位>` | `taken=true`、`requested`、`wieldedSlot`、`wielded`（游戏现在手里的槽位与物品） | `wield.not-taken: 要求 <槽位>，手里仍是 <槽位> <物品>`：游戏没接受换手，例如战甲驾驶中手里固定是战甲的枪 |
| `interact`（不带目标） | `selected`、`interacted=true` | 先读游戏自己的选中（`at=` 时先瞄准），连续两次读到同一个交互物才按 Use。`interact.nothing-selected`：3 秒内游戏没有稳定选中任何交互物，不按 |
| `interact x y z` | 同下行（找到原版交互物时），否则 `pressed=true` | 取该点 2 米内最近的启用原版交互物，按下行的站位走过去、看向它、等游戏选中它再按 Use，失败码同下行；点附近没有原版交互物（如 Ordnance 的重武器领取提示）时退回旧走法：走到眼睛离该点 1 米内再看向它按 Use，这类提示不在游戏的选中里，只报 `pressed=true`，是否拿到用随后的 `read` 断言 |
| `interact name=<名字> [x y z] [radius=30]` | `name`（`类型/物体/父物体名@所属关卡部件类型`，所属部件是往上至多 4 层里最近的 `LevelGeneration` 组件，终端系统的登记组件 `LG_GenericTerminalItem`（门、闸门控制器、物品凡有终端名的都带）不算：安全门交互物 `Interact_Timed/Interaction_Open_Or_Activate/crossing` 归 `LG_SecurityDoor` 一类，弱门按钮 `Interact_SimpleButton/LG_DoorButton` 归 `LG_DoorButton`／`LG_WeakDoor`，终端归 `LG_ComputerTerminal`；名字不认中文）、`point`、`distance`、`hold`、`blocked`、`front`（终端屏幕朝向的水平单位向量，别的交互物为 `none`）、`height`（交互点离它下方地面的高度）、`posture`（`stand`／`crouch`）、`eye`（该姿势的眼高）、`stand`（站位脚下点）、`standClear`（站位能直接看见它）、`selected`、`aimAngle`（最终视线与指向交互点连线的夹角，度，转身和俯仰合计）、`reason`、`walked=false`（原地就按了时）、`interacted=true` | 选法：`name=文字` 时所属部件类型含该文字的交互物优先，其次才是自身类型、物体或父物体名含该文字的，同一档里取最近（`name=Terminal` 先取终端本身，不会被名字带 terminal 的道具抢走）；`name=文字@部件类型` 只要所属部件类型含 `部件类型`、且（`文字` 非空时）自身名字含 `文字` 的（如 `name=@LG_WeakDoor`、`name=Button@LG_DoorButton`）；都不区分大小写。站位按原版选择（`PlayerInteraction.FixedUpdate` 与 `UpdateWorldInteractions`）：游戏在镜头处取半径为玩家的 `m_searchRadius`（1.2 米）加 `Min(|视线方向 y|, 0.5)` 的球，球碰到的交互物里挑原点在屏幕上离中心最近的；带 `RequireCollisionCheck` 的（终端等）还要从镜头到它原点、按 `MASK_PLAYER_INTERACT_BLOCKERS` 的连线什么都没碰到或先碰到它自己。先定姿势：眼高取玩家自己 `PlayerDataBlock` 的 `camPosDefault.y`（站）与 `camPosCrouch.y`（蹲），读不到时两者都用此刻眼睛离脚底的高度；终端（有屏幕朝向的）一律站着；别的只有「站立眼高减交互点离地高度」大于 `sqrt((m_searchRadius + 0.5)^2 - 0.5^2)`（站在最近一圈 0.5 米处、低头时选择球最大也够不到）才蹲，否则站；蹲着用完站起，站姿目标遇到玩家蹲着会先站起。那两个眼高的原版数值仓库里没有记录，以游戏读回的 `eye` 为准。站在原地看向它、游戏已经稳定选中它时直接按 Use，不走（`walked=false`）；否则在交互物周围 0.5／0.8／1.1 米三圈、每圈 12 个方向找导航网格上的落脚点，要求按所选姿势的眼高算出的眼睛看向它碰撞体中心时，碰撞体在这次俯仰的选择球内，且上面的连线判定通过；终端只取屏幕正前方左右各 60 度内的落脚点（按吸附到导航网格后的点算），屏幕朝向取自终端自己的 `LG_ComputerTerminal.m_CameraAlign`：用终端时游戏把镜头放在那里，所以它相对屏幕中心的水平方向就是正面（只用它的位置，不用它的轴），侧面和背面的点不算，取最近一圈里离玩家最近的点，走到 0.35 米内。没有能看见它的点时 `standClear=false`，走到最近的可落脚点，再以游戏自己的选中为准（终端等自带多个碰撞体的，连线判定偏严，`standClear=false` 时游戏照样可能选中它）。看向它后每几帧读一次游戏的选中，连续两次都是它才按 Use，最多等 3 秒。计时交互不写 `hold=` 时按满它的时长。`interact` 只按直线走，30 秒为限，跨房间先 `teleport`。`interact.not-found`：半径内没有；`interact.out-of-reach`：30 秒内走不到；`interact.not-selected:<reason>`：等了 3 秒游戏选中的仍不是它，不按；`reason` 按原版选择从此刻镜头判：`too-low`／`too-high`（碰撞体在选择球外，交互点比眼睛低／高 0.3 米以上）、`too-far`（球外、与眼睛齐平）、`blocked`（连线判定不过）、`not-aimed`（视线偏离超过 10 度）、`none`（这些都满足，游戏仍选了别的） |
| `terminal type "<一行>"` | `typed`、`terminal`（序号）、`state`，再加 1.5 秒后读屏的 `using`、`lines`、`tail`（最后 8 行，`\|` 分隔，去掉富文本标签） | 对玩家正在用的终端（先 `interact name=Terminal` 进去）写入这一行，再调交互状态自己的回车入口 `LG_TERM_PlayerInteracting.OnReturn`，命令的执行、同步和输出与手敲一致。`terminal.not-in-use`：玩家没在用终端；`terminal.not-taking-input`：终端当前状态不收输入；`terminal.line-length`：空行或超过 200 字 |
| `terminal exit` | `exited`、`using=false` | 调终端自己的 `LG_ComputerTerminal.ExitFPSView`（原版退出键走的同一入口），0.8 秒后读回玩家已不在用 |
| `terminal read [行数]` | `terminal`、`using`、`state`、`lines`（屏幕缓冲总行数）、`tail`（最后若干行，默认 8，最多 40） | 读 ForgeCore 的终端屏幕缓冲；没在用终端时读玩家 3 米内最近的一台，没有则 `terminal.none` |
| `state hud` | `objectiveHeader`、`objective`、`progression[i]`（标题 : 正文）、`intel`、`timerVisible`、`timerTitle`、`timer`、`promptVisible`、`prompt`、`messageVisible`、`message`、`scans`、`scan[i]`（可见的扫描环世界文字，离玩家最近的在前） | 只读原版 HUD 部件的文字（`PUI_GameObjectives`、`PUI_WardenIntel`、`PUI_ObjectiveTimer`、`InteractionGuiLayer`、`CP_Bioscan_Hud`），去掉富文本、换行成空格、每段截到 160 字、每类最多 6 条。读不到的：字幕与语音台词、指南针与雷达、导航标记的屏幕文字、状态条（数值走 `state ammo` 与 `player`）；HUD 还没建好时答 `unavailable:no-hud` |
| `state variables <前缀>` | `result`（驱动回执）、`matched`、`shown`，以及每个变量 `<身份>=<值>`（每人一份的变量是 `<身份>@<玩家>`；前 16 个，值截到 64 字；完整的在记录里） | 关卡变量的身份是 `var.g` 加 `[包名, 关卡, 变量行 id]` 的 JSON 的 sha256（`runtimeLevelVariableIdentity`），脚本里用 `expect $名字.var.g… == "值"` 判断 |
| `teleport …` | `Position`、`DimensionIndex`、`Alive`，以及 `requestedPosition`、`sampledLanding`（游戏的导航体素采样落点）、`landed`（读回确认时的实际位置），都是 `x,y,z` | 驱动回执的 `summary` 带这三项；传送没确认时只有前两项 |
| `nav sample\|path\|voxel\|near <JSON>` | 原样的 JSON 读回 | 字段：`sample` 收 `from`（必填）、`to`、`step`、`maxDistance`；`path` 收 `from`、`to`、`snap`；`voxel` 收 `points`、`heightSens`；`near` 收 `center`、`radius`、`limit`。写了别的字段答 `drive.nav.unknown-field:<字段> (accepted: …)`，缺必填点答 `drive.nav.missing-field:<字段>`，点不是三个数答 `drive.nav.point-required:<字段>` |
| `alarms [all]` | `active`（正在跑的扫描与警报数）、`alarms`（其中会拉警报的）、`scans`、`waves`（Mastermind 持有的生存波）、`list`（`puzzle:<uid>:<active\|idle>[,alarm][,solved]:<数据块>` 与 `wave:<事件号>:<状态>`，`\|` 分隔；`all` 连空闲与已完成的扫描一起列） | — |
| `recorder` | `session`、`dropped`、`queueRefusals`、`budgetReached`、`lastDrop`（最近一次丢记录的原因）、`processDropped`、`processQueueRefusals` | — |
| `checkpoint save` | `native-checkpoint-save-issued` | `drive.checkpoint-save-same-position`：玩家站在已存档的位置上。原版只在检查点门的扫描完成时存档，每扇门一次、位置各不相同，存档事件（`EventAPI.OnCheckpointReached`，Forge 在它上面拍快照）就是「门锁位置变了」；同一位置再存一次只会重新抓游戏的缓冲、不触发这个事件，所以发令前拒绝，换个位置再存 |
| `checkpoint restore` | `native-checkpoint-reload-issued` | 只在团灭后的失败界面可用。`drive.checkpoint-restore-needs-wipe`：还在关内（原版没有不团灭就读档的入口；`checkpoint wipe` 后等失败界面出来，约 6 秒）；`drive.checkpoint-save-required`：本关没有存档（复制状态里没有存档，或采集里没有 Checkpoint 缓冲）；`drive.checkpoint-session-refused`：失败界面上会话仍拒绝读档指令 |
| `checkpoint confirm` | `native-checkpoint-confirmed; save\|restore;events;reloadCount=前->后;checkpointUsage;checkpointRecalls;isReloadingCheckpoint;recalling;lastInteraction;alive;position;world` | 最多等 60 秒，游戏还没结算就失败为 `drive.checkpoint-readback-missing:` 加同一串原生读回，写明卡在哪；存档或读档后世界号变了（离关，或主机异常卡住后自己推进）失败为 `drive.checkpoint-world-changed:前->后`。恢复的确认要求恢复事件、`reloadCount`、`checkpointUsage` 和 `checkpointRecalls` 都递增 |

读档按原版的顺序：`checkpoint save`、`checkpoint confirm`、`checkpoint wipe`（全队倒地）、等失败界面、`checkpoint restore`、`checkpoint confirm`。`restore` 走失败界面「从检查点重来」按钮的同一条路：`SNet.Sync.SessionCommand(ReloadCheckpoint)`、`CheckpointManager.ReloadCheckpoint()`、`StartRecallCheckpointWithAllSyncedPlayers()`。原版没有不团灭就读档的入口：关内会话拒绝这条指令，只发检查点门的同步交互 `AttemptInteract(ReloadCheckpoint)` 也只会把复制状态置为正在读档并让 `reloadCount` 加一，不启动召回，会一直卡在 `isReloadingCheckpoint=true`；所以关内 `restore` 直接答 `drive.checkpoint-restore-needs-wipe`，不发任何指令。读档不换世界：内核世界号不变，确认行里的 `world` 前后相同；世界号变了确认即失败为 `drive.checkpoint-world-changed`，说明离了关或引擎在恢复中挂起并自己步进，要去日志里找 `runtime.suspended`。记录器丢记录时，BepInEx 日志每分钟最多一条 `Forge recorder dropped N records (latest: <原因>; R queue refusals)`；单条记录超过 64 KiB 记录预算时，原记录换成同频道同种类的一条 `$error` 记录（写明 `RecValueTooLargeException`），`recorder` 随时读回累计与最近原因。

每次 `run` 在 `<会话目录>/console/<时间>-<脚本名>/`（在 artifacts 下，不进 Git）写：`commands.jsonl`（逐行原样，可被 `replay` 重放）、`NNNN-<指令>/result.json` 与 `log.txt`（指令前后的日志）、`run.json`（格式 `forge.console-run`，版本 `1.0.0`）和同内容的 `run.md`。示例脚本在 `scripts/`；脚本里不写房间编号，用 `spawn at=front:8` 这类相对写法，才能在任何关卡上跑。

## 网络模拟

`NetSim`（`Native/Network/NetSim.cs`）把本机发出的每个包按给定延迟加抖动扣住再发，游戏自己的和所有模组的都算（都经 `SNet_Core_STEAM.SendBytes` 的三个重载）；不可靠包按比例丢，可靠包不丢、只多等一次，发给同一玩家的可靠包保持顺序（群发、列表发和单发算在一起，不分通道）；群发和列表发的接收者在游戏发出的那一刻定下，包在等的时候进组的玩家收不到、出组的照样收到；三个参数都给 0 等于关闭。只动发送侧：两台机器各开一次才是双向。钩子在第一次 `Set` 时安装，关闭时全部直通。联机测试用设置文件开：`NAinfini.ForgeDevelopment.cfg` 的 `[Network Simulation]` 填 `DelayMs`、`JitterMs`、`LossPercent`（任一非 0 即从第一帧起生效，改后重启），因为指令台和采集一样，只要会话里有别的玩家就关掉，下面的指令只在单人或别人进来之前能跑。开、关、被拒和每次 `Report` 都在 `LogOutput.log` 写一行 `net-sim …`。指令文件 `probes/commands/net-sim.forge`：`net-sim-250`（250 毫秒）、`net-sim-250-jitter-loss`（250 毫秒、抖动 30 毫秒、不可靠包丢 2%）、`net-sim-report`（当前参数与 held / released / dropped / waiting 计数）、`net-sim-off`。结果以 `refused` 开头表示钩子没装上、模拟不生效。

## 探针成员离线检查

`tests/ProbeMembers` 在 `python Mods/verify.py ForgeDevelopment` 里随其他测试项目一起跑（需要 BepInEx 档案，读其中的 `core/Mono.Cecil.dll`）：先构建 `ForgeDevelopment.Native`，再用 Cecil 只读打开 `artifacts/dotnet/bin` 下的 `ForgeDevelopment.Native`、`ForgeEngine`、`ForgeEngine.Framework`、`ForgeCore`，按运行时的解析规则逐条走 `probes/commands/*.forge` 的静态目标、`call`、`read`、`set`、`wait.path`（静态目标第一段是静态成员，之后是实例成员，属性、字段、无参方法依次找，含基类；`call` 的返回类型就是下一步 `result` 的类型），以及 `tools/*.py` 里 `call(类, "成员")` 点名的本包类型成员。走到游戏的 interop 类型、`object`、接口、泛型参数或 `drive.*` 调用后停下不判；在 Forge 或 BCL 类型上找不到的名字、`Forge` 开头却不存在的静态类型都失败。自带一条读已删除的 `RuntimeKernel.schedules` 的坏探针，查不出它这项检查本身失败。结果行：`<通过>/<总数> passed; failed=<n>; commands=<条数>; toolCalls=<条数>; assemblies=<目录>`，每条失败一行 `FAIL: <文件> <命令 id>: '<路径>': no member <名字> on <类型>`。
