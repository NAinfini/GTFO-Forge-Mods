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

工具可以拷出仓库单独用：把 `tools/` 下的 `run-drive-session.py`、`drive_session.py`、`run-experiment.py`、`rec_segments.py`、`installed_pack.py`、`drive_cli.py`、`console_commands.py`、`console_run.py`、`test_script.py`、`package_names.py` 与 `Tools/Game/` 下的 `game_process.py`、`game_profile.py`，以及 `Tools/safe_delete.py` 放进同一个文件夹即可。仓库外运行时必须给 `--lock`（或 `FORGE_GAME_LOCK`），`--work-dir` 可以是任意目录，`--profile` 不限于仓库允许的档；探针从档里装好的 `plugins/NAinfini-ForgeDevelopment/ForgeDevelopment/probes` 读。

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

按名调用的通用能力：`call` 答 `False` 算失败（`call.returned-false`），这类调用本来就是在问是否就绪时加 `--allow-false`；`--until` 等到的答案是空串立即报错，不再等到超时。`call static:ForgeDevelopment.Native.SeededDrop Start <关卡> ["build,hostId,session"] [机器人数]` 以指定三个种子单人开局（先离开大厅或关卡，菜单起来后经 ForgeCore `SoloDrop`；`State` 读进度），两次同种子进关是同一张图；`call static:ForgeDevelopment.Native.LevelScenario Start [stay]` 在关内无人操作地打一场并做一轮关卡互动（刷怪、开火与近战、走跑蹲跳、投掷、聊天、噪声、标记、哨戒炮与地雷、药包、倒地与救起、资源箱、安全门、反应堆、扫描、发电机、拾取、终端、生存波、检查点存读），120 秒后离开关卡（`stay` 为 true 时留在关内），每步在 `LogOutput.log` 写 `scenario: did …` 或 `scenario: FAILED …`，`State` 读进度。`call static:ForgeDevelopment.Native.SignLayers Read` 读回关内每块带底板的字（字牌、世界标签、带底的模型标签）的层序：字的主材质与每个后备字体子网格材质的渲染队列和深度测试、底板的队列，答 `boards=;materials=;subMeshes=;wrong=;unordered=;noDepthProperty=;first=`（`unordered` 是字的队列没有排在底板队列之上的字牌数；`noDepthProperty` 是两个深度测试属性都读不到的字材质数，只判它的队列、只报告不判失败：游戏里回退字体的子网格材质就是这样）。`SignLayers SelfTest` 把故意写坏的层序喂给同一个判定函数，答 `selftest=ok;control=1;caught=3/3` 才算检测本身没坏（一个正确材质判过、三个故意写坏的：队列低一档、深度测试 Always、没有材质，全部判出）（`scripts/sign-layers.test` 先跑它，再断言 `wrong=0`、`unordered=0`）。`call static:ForgeDevelopment.Native.FirstPersonWearProbe Report` 读回穿在本机第一人称身体上的模型（人物自己的手臂）：原版第一人称动画器是否人形、30 个手指骨有几个，原版手臂渲染器几个、被藏几个，每个穿戴模型画没画、阴影设置、三角形数，挂在原版手骨与手指骨上的模型骨与原版关节的最大距离与缩放；同一类型的 `Followed` 读 ForgeCore 每帧跟随的穿戴列表（每件开没开、显没显、原版部件藏没藏、模型与所穿身体是否还在、原版手臂物体开关），`Drawn` 列出第一人称里此刻实际在画的渲染器（路径、层、是否蒙皮）。`run-drive-session.py start --perf` 把 `EnablePerformanceLogging` 租为开，性能日志写在 `BepInEx/PerformanceLogs`（帧时间分位、主线程托管分配），用 `tools/analyze_performance.py` 读。

`--wait 秒`（默认 30）是单次调用等游戏接受命令的上限，由调用方决定；超过即带原因报错，不会像脚本流程那样等满 180 或 300 秒。进关（`drop`）本身要几分钟是游戏加载（`Generating`、`StopElevatorRide`），期间另开一个终端用 `status` 就能看到阶段，不要重复提交。进关耗时由 `DropTiming` 只读观察（不加新钩子，只在已有的每帧回调里读游戏状态名）：两个阶段标记之间，主线程有一帧超过 1 秒写 `drop.stall`（含阶段与帧间隔毫秒），每静默 5 秒写一条 `drop.wait`（含阶段、游戏状态与帧率），用它们定位没有阶段标记的那段时间。

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
| 位置视线 | `teleport <x y z> [dim= face=]`、`teleport room=<房间> <x y z>`（房间局部坐标；`<房间>` 可写实例号、包里的房间名，或 `@<预制件名片段>` 指原版无名房间，按已加载房间的预制件名匹配）、`teleport near=<交互物名> [offset=…]`（走到名字含该文字的交互物旁）、`teleport zone=<区> [area=]`、`teleport exit=<出口> [offset=x,y,z]`（游戏把人放到请求点下方的导航体素节点上，约半米一格，读回与这个采样落点比：水平 5 厘米内、竖直在台阶高度与 0.6 米中较大者内；结果是实际落点）；`look <x y z>`、`aim <目标>`（`--ads` 开镜） |
| 玩家战斗 | `fire [N 或 Ns] [at=名字]`、`melee [charge=秒] [at=]`、`wield <primary\|secondary\|melee\|tool\|consumable\|pack\|武器名>`、`reload`、`throw [charge= at=]`、`jump`、`crouch`、`stand` |
| 玩家移动操作 | `move forward\|back\|left\|right <秒> [run]`、`move to <x y z>`（朝着点按前进，到 30 秒为止）、`interact [x y z] [name=名字] [radius=米] [hold=] [at=]`、`press/hold/release/key <动作>` |
| 状态 | `god on\|off`、`give <物品> [数量]`、`ammo fill`、`down`、`revive` |
| 敌人 | `spawn <敌人> [数量] [at=front:8] [mode=hibernate\|aggressive] [as=名字]`、`enemies [半径]`、`clear enemies\|groups`、`hit`、`limbs`、`follow` |
| 世界 | `door read\|open <门 JSON 或 near:半径>`（`near:` 以玩家所在位置为圆心）、`noise <半径>`、`event <JSON>`、`checkpoint save\|restore\|confirm\|wipe`、`scan read <出口>`、`extract <出口>`、`nav near\|path\|sample\|voxel\|course <JSON>`、`model-shot <JSON>`、`terminal type "<一行>"\|exit\|read [行数]` |
| 装包与内容读回 | `install <zip>…`：把导出包装进本会话的档案（与工坊 `content.install` 同样的文件和记录），一份请求交给运行中的游戏（`BepInEx/forge-reload/request.json`，写与读同 I-RELOAD「工坊与游戏的交接」，实现在 `tools/reload_handoff.py`），等游戏答复；答 `status`、`code`、`generation`、`digest`、`recordMatches`、`rejections`、`rejected`、`rejoinState`，不是已换代、或装进去的字节与游戏读到的记录对不上即失败。先 `leave` 回大厅，从关内发请求游戏会先自己回大厅、换代后再进同一关。`entries [名字片段]`：敌人表与武器目录此刻的样子（经作者读回 `AuthorReads.EntryRows`，要装了 ForgeEngine），答 `generation`、`replicationDigest`、`replicationCount`、`vanillaCount`、`forgeOrder` 与 `ownOrder`（Forge 敌人按复制序号的先后写成 E 内容条目、R Rundown 行、O 其他；`ownOrder` 只算名字含片段的）、`enemyRows`、`enemyRowsIncomplete`（缺预制体、生成表位或复制序号的行数）、`entryEnemies`、`rundownEnemies`、`weaponRows`、`weaponRowsIncomplete`（不在装备目录里的行数）、`weaponEntries`（名字用 `\|` 连）、`replication`（`序号:敌人号`用 `;` 连） |
| 菜单 | `picker hover\|leave <卡序号>`：打开着的 ForgeCore 挑选器当作光标进入／离开列表里第几张卡（调挑选器自己的悬停入口 `Picker.Hover`，不动鼠标），答 `focus`（详情此刻跟着的卡，-1 为该格正在用的）；截悬停前后图用它 |
| 画面对比 | `toon forward\|deferred\|off [对象名]`：把本机手里的物品（或按名字找到的激活物体）上带底色贴图的不透明材质换成 ForgeCore 卡通着色器的前向接法或延迟接法（经 ForgeCore `ToonDress`，与角色皮肤的卡通材质同一条路；延迟接法另在每个渲染器下放一份叠加层），`off` 换回原材质；换武器、枪模型刷新外观或换关后要再敲一次。只在开发模组里 |
| 读取 | `player [路径…]`、`read <目标> <路径…>`、`state ammo\|stamina\|fog\|intel\|hud\|lifecycle\|variables`、`alarms [all]`、`recorder`、`log [tail= grep=]`、`status`、`screenshot`、`call`、`step`、`quit` |
| 脚本控制 | `expect <读取> <比较符> <值>`、`wait <n>s\|<n>f`、`wait until <读取> <比较符> <值> timeout=<n>s`、`stop-on-fail`、行尾 `as=名字` 把这条指令的回答记成 `$名字`，`#` 注释 |

开火、近战、重装、投掷、跳蹲站默认模拟玩家按键（`GameInputDrive` 把键喂进游戏自己的输入，租约随这条指令结束而释放）；加 `--direct` 改为直接调原生方法（`throw --direct` 松手时调手里投掷物自己的 `Throw(m_throwStrength)`，即原版松手那一步）。`give` 给的消耗品带满该物品的次数（`ConsumableAmmoMax`，与关卡里的消耗品一样），手里拿的不是枪时 `state ammo` 照样读：消耗品的剩余次数在背包的 `consumable` 一栏（扔一次减一），`clip` 只对枪有意义。`key` 收的是键名，`press/hold/release` 收的是游戏动作名（如 `press Flashlight`）。`expect` 读取：`player Position.y`、`state ammo clip`、`enemy <id 或 $名字.id> health`、`alarms active`、`read <目标> <路径>`、`$名字.字段`、`log`（看上一条指令开始以来写进的日志行，包括它返回之后才写的，`--all` 看本次脚本运行的整段日志，不含运行之前的旧日志；已经失败的指令自己写下的 `experiment: failed` 行不重复算；`expect log … ignore=<正则>` 排除已知无关的行，失败时给出命中的那一行与它在日志里的位置）；`fire` 要求读回里真有子弹减少，`fire 0`、没出子弹、读不到回读都失败，`hit` 等战斗读回含 `damage`；比较符 `== != < <= > >= contains !contains`，`~0.05` 为容差。

几条指令的结果与失败码：

| 指令 | 结果字段 | 失败 |
| --- | --- | --- |
| `wield <槽位>` | `taken=true`、`requested`、`wieldedSlot`、`wielded`（游戏现在手里的槽位与物品） | `wield.not-taken: 要求 <槽位>，手里仍是 <槽位> <物品>`：游戏没接受换手，例如战甲驾驶中手里固定是战甲的枪 |
| `interact`（不带目标） | `selected`、`interacted=true` | 先读游戏自己的选中（`at=` 时先瞄准），连续两次读到同一个交互物才按 Use。`interact.nothing-selected`：3 秒内游戏没有稳定选中任何交互物，不按 |
| `interact x y z` | 同下行（找到原版交互物时），否则 `pressed=true` | 取该点 2 米内最近的启用原版交互物，按下行的站位走过去、看向它、等游戏选中它再按 Use，失败码同下行；点附近没有原版交互物（如 Ordnance 的重武器领取提示）时退回旧走法：走到眼睛离该点 1 米内再看向它按 Use，这类提示不在游戏的选中里，只报 `pressed=true`，是否拿到用随后的 `read` 断言 |
| `interact name=<名字> [x y z] [radius=30]` | `name`（`类型/物体/父物体名@所属关卡部件类型`，所属部件是往上至多 4 层里最近的 `LevelGeneration` 组件，终端系统的登记组件 `LG_GenericTerminalItem`（门、闸门控制器、物品凡有终端名的都带）不算：安全门交互物 `Interact_Timed/Interaction_Open_Or_Activate/crossing` 归 `LG_SecurityDoor` 一类，弱门按钮 `Interact_SimpleButton/LG_DoorButton` 归 `LG_DoorButton`／`LG_WeakDoor`，终端归 `LG_ComputerTerminal`；名字是物体名，按钮文字是它的提示，`name=` 两者都认，中文提示可用）、`point`、`distance`、`hold`、`blocked`、`front`（终端屏幕朝向的水平单位向量，别的交互物为 `none`）、`height`（交互点离它下方地面的高度）、`posture`（`stand`／`crouch`）、`eye`（该姿势的眼高）、`stand`（站位脚下点）、`standClear`（站位能直接看见它）、`selected`、`aimAngle`（最终视线与指向交互点连线的夹角，度，转身和俯仰合计）、`reason`、`walked=false`（原地就按了时）、`interacted=true` | 选法：`name=文字` 时所属部件类型含该文字的交互物优先，其次才是自身类型、物体或父物体名含该文字的，同一档里取最近（`name=Terminal` 先取终端本身，不会被名字带 terminal 的道具抢走）；`name=文字@部件类型` 只要所属部件类型含 `部件类型`、且（`文字` 非空时）自身名字含 `文字` 的（如 `name=@LG_WeakDoor`、`name=Button@LG_DoorButton`）；都不区分大小写。站位按原版选择（`PlayerInteraction.FixedUpdate` 与 `UpdateWorldInteractions`）：游戏在镜头处取半径为玩家的 `m_searchRadius`（1.2 米）加 `Min(|视线方向 y|, 0.5)` 的球，球碰到的交互物里挑原点在屏幕上离中心最近的；带 `RequireCollisionCheck` 的（终端等）还要从镜头到它原点、按 `MASK_PLAYER_INTERACT_BLOCKERS` 的连线什么都没碰到或先碰到它自己。先定姿势：眼高取玩家自己 `PlayerDataBlock` 的 `camPosDefault.y`（站）与 `camPosCrouch.y`（蹲），读不到时两者都用此刻眼睛离脚底的高度；终端（有屏幕朝向的）一律站着；别的只有「站立眼高减交互点离地高度」大于 `sqrt((m_searchRadius + 0.5)^2 - 0.5^2)`（站在最近一圈 0.5 米处、低头时选择球最大也够不到）才蹲，否则站；蹲着用完站起，站姿目标遇到玩家蹲着会先站起。那两个眼高的原版数值仓库里没有记录，以游戏读回的 `eye` 为准。站在原地看向它、游戏已经稳定选中它时直接按 Use，不走（`walked=false`）；否则在交互物周围 0.5／0.8／1.1 米三圈、每圈 12 个方向找导航网格上的落脚点，要求按所选姿势的眼高算出的眼睛看向它碰撞体中心时，碰撞体在这次俯仰的选择球内，且上面的连线判定通过；终端只取屏幕正前方左右各 60 度内的落脚点（按吸附到导航网格后的点算），屏幕朝向取自终端自己的 `LG_ComputerTerminal.m_CameraAlign`：用终端时游戏把镜头放在那里，所以它相对屏幕中心的水平方向就是正面（只用它的位置，不用它的轴），侧面和背面的点不算，取最近一圈里离玩家最近的点，走到 0.35 米内。没有能看见它的点时 `standClear=false`，走到最近的可落脚点，再以游戏自己的选中为准（终端等自带多个碰撞体的，连线判定偏严，`standClear=false` 时游戏照样可能选中它）。看向它后每几帧读一次游戏的选中，连续两次都是它才按 Use，最多等 3 秒。计时交互不写 `hold=` 时按满它的时长。`interact` 只按直线走，30 秒为限，跨房间先 `teleport`。`interact.not-found`：半径内没有；`interact.out-of-reach`：30 秒内走不到；`interact.not-selected:<reason>`：等了 3 秒游戏选中的仍不是它，不按；`reason` 按原版选择从此刻镜头判：`too-low`／`too-high`（碰撞体在选择球外，交互点比眼睛低／高 0.3 米以上）、`too-far`（球外、与眼睛齐平）、`blocked`（连线判定不过）、`not-aimed`（视线偏离超过 10 度）、`none`（这些都满足，游戏仍选了别的） |
| `terminal type "<一行>"` | `typed`、`terminal`（序号）、`state`，再加 1.5 秒后读屏的 `using`、`lines`、`tail`（最后 8 行，`\|` 分隔，去掉富文本标签） | 对玩家正在用的终端（先 `interact name=Terminal` 进去）写入这一行，再调交互状态自己的回车入口 `LG_TERM_PlayerInteracting.OnReturn`，命令的执行、同步和输出与手敲一致。`terminal.not-in-use`：玩家没在用终端；`terminal.not-taking-input`：终端当前状态不收输入；`terminal.line-length`：空行或超过 200 字 |
| `terminal exit` | `exited`、`using=false` | 调终端自己的 `LG_ComputerTerminal.ExitFPSView`（原版退出键走的同一入口），0.8 秒后读回玩家已不在用 |
| `terminal read [行数]` | `terminal`、`using`、`state`、`lines`（屏幕缓冲总行数）、`tail`（最后若干行，默认 8，最多 40） | 读 ForgeCore 的终端屏幕缓冲；没在用终端时读玩家 3 米内最近的一台，没有则 `terminal.none` |
| `state hud` | `objectiveHeader`、`objective`、`progression[i]`（标题 : 正文）、`intel`、`timerVisible`、`timerTitle`、`timer`、`promptVisible`、`prompt`、`messageVisible`、`message`、`scans`、`scan[i]`（可见的扫描环世界文字，离玩家最近的在前） | 只读原版 HUD 部件的文字（`PUI_GameObjectives`、`PUI_WardenIntel`、`PUI_ObjectiveTimer`、`InteractionGuiLayer`、`CP_Bioscan_Hud`），去掉富文本、换行成空格、每段截到 160 字、每类最多 6 条。读不到的：字幕与语音台词、指南针与雷达、导航标记的屏幕文字、状态条（数值走 `state ammo` 与 `player`）；HUD 还没建好时答 `unavailable:no-hud` |
| `state variables <前缀>` | `result`（驱动回执）、`matched`、`shown`，以及每个变量 `<身份>=<值>`（每人一份的变量是 `<身份>@<玩家>`；前 16 个，值截到 64 字；完整的在记录里） | 关卡变量的身份是 `var.g` 加 `[包名, 关卡, 变量行 id]` 的 JSON 的 sha256（`runtimeLevelVariableIdentity`），脚本里用 `expect $名字.var.g… == "值"` 判断 |
| `teleport …` | `Position`、`DimensionIndex`、`Alive`，以及 `requestedPosition`、`sampledLanding`（游戏的导航体素采样落点）、`landed`（读回确认时的实际位置），都是 `x,y,z` | 驱动回执的 `summary` 带这三项；传送没确认时只有前两项 |
| `nav sample\|path\|voxel\|near\|course <JSON>` | 原样的 JSON 读回 | 字段：`sample` 收 `from`（必填）、`to`、`step`、`maxDistance`；`path` 收 `from`、`to`、`snap`；`voxel` 收 `points`、`heightSens`；`near` 收 `center`、`radius`、`limit`；`course` 收 `points`、`player`（`true` 时把本机玩家位置加为最后一个点）、`sample`（默认 2 米），每点答原生查到的 course node、区、area、是否占有节点簇、该节点每个 portal 的对面节点、闸门、`zonePortal`、`progressionLocked`、`traversable`，再对每对点答全图是否连通（`graphReachable`）与敌人可走的路线（`enemyRoute`，不走 `IsProgressionLocked` 的 portal，与 `EnemyCourseNavigation.TryFindNodeTowardGoalNode` 相同；不通为 `null`）。写了别的字段答 `drive.nav.unknown-field:<字段> (accepted: …)`，缺必填点答 `drive.nav.missing-field:<字段>`，点不是三个数答 `drive.nav.point-required:<字段>` |
| `alarms [all]` | `active`（正在跑的扫描与警报数）、`alarms`（其中会拉警报的）、`scans`、`waves`（Mastermind 持有的生存波）、`list`（`puzzle:<uid>:<active\|idle>[,alarm][,solved]:<数据块>` 与 `wave:<事件号>:<状态>`，`\|` 分隔；`all` 连空闲与已完成的扫描一起列） | — |
| `recorder` | `session`、`dropped`、`queueRefusals`、`budgetReached`、`lastDrop`（最近一次丢记录的原因）、`processDropped`、`processQueueRefusals` | — |
| `checkpoint save` | `native-checkpoint-save-issued` | `drive.checkpoint-save-same-position`：玩家站在已存档的位置上。原版只在检查点门的扫描完成时存档，每扇门一次、位置各不相同，存档事件（`EventAPI.OnCheckpointReached`，Forge 在它上面拍快照）就是「门锁位置变了」；同一位置再存一次只会重新抓游戏的缓冲、不触发这个事件，所以发令前拒绝，换个位置再存 |
| `checkpoint restore` | `native-checkpoint-reload-issued` | 只在团灭后的失败界面可用。`drive.checkpoint-restore-needs-wipe`：还在关内（原版没有不团灭就读档的入口；`checkpoint wipe` 后等失败界面出来，约 6 秒）；`drive.checkpoint-save-required`：本关没有存档（复制状态里没有存档，或采集里没有 Checkpoint 缓冲）；`drive.checkpoint-session-refused`：失败界面上会话仍拒绝读档指令 |
| `checkpoint confirm` | `native-checkpoint-confirmed; save\|restore;events;reloadCount=前->后;checkpointUsage;checkpointRecalls;isReloadingCheckpoint;recalling;lastInteraction;alive;position;world` | 最多等 60 秒，游戏还没结算就失败为 `drive.checkpoint-readback-missing:` 加同一串原生读回，写明卡在哪；存档或读档后世界号变了（离关，或主机异常卡住后自己推进）失败为 `drive.checkpoint-world-changed:前->后`。恢复的确认要求恢复事件、`reloadCount`、`checkpointUsage` 和 `checkpointRecalls` 都递增 |

读档按原版的顺序：`checkpoint save`、`checkpoint confirm`、`checkpoint wipe`（全队倒地）、等失败界面、`checkpoint restore`、`checkpoint confirm`。`restore` 走失败界面「从检查点重来」按钮的同一条路：`SNet.Sync.SessionCommand(ReloadCheckpoint)`、`CheckpointManager.ReloadCheckpoint()`、`StartRecallCheckpointWithAllSyncedPlayers()`。原版没有不团灭就读档的入口：关内会话拒绝这条指令，只发检查点门的同步交互 `AttemptInteract(ReloadCheckpoint)` 也只会把复制状态置为正在读档并让 `reloadCount` 加一，不启动召回，会一直卡在 `isReloadingCheckpoint=true`；所以关内 `restore` 直接答 `drive.checkpoint-restore-needs-wipe`，不发任何指令。读档不换世界：内核世界号不变，确认行里的 `world` 前后相同；世界号变了确认即失败为 `drive.checkpoint-world-changed`，说明离了关或引擎在恢复中挂起并自己步进，要去日志里找 `runtime.suspended`。记录器丢记录时，BepInEx 日志每分钟最多一条 `Forge recorder dropped N records (latest: <原因>; R queue refusals)`；单条记录超过 64 KiB 记录预算时，原记录换成同频道同种类的一条 `$error` 记录（写明 `RecValueTooLargeException`），`recorder` 随时读回累计与最近原因。

每次 `run` 在 `<会话目录>/console/<时间>-<脚本名>/`（在 artifacts 下，不进 Git）写：`commands.jsonl`（逐行原样，可被 `replay` 重放）、`NNNN-<指令>/result.json` 与 `log.txt`（指令前后的日志）、`run.json`（格式 `forge.console-run`，版本 `1.0.0`）和同内容的 `run.md`。示例脚本在 `scripts/`；脚本里不写房间编号：房间按名字（`teleport room=机关样例区`，名字从已装 V1 包的工程索引查，同名多个实例时取已加载的那个），无名原版房间用 `room=@<预制件片段>`，按钮用 `teleport near=` 加提示文字；`spawn at=front:8` 这类相对写法才能在任何关卡上跑。

隔离：每次 `run` 在第一行之前和最后一行之后各做一次复位（离开终端、关无敌、清掉刷出的敌人与按住的键），一个脚本不会把玩家留在终端里影响下一个；单条敲的指令不复位。每次运行结束重写 `session.forge`，状态页（选择、世界号、录制会话、玩家）跟着 `level`、`leave` 与脚本走，不会停在旧值。失败时 `run.md` 单列失败节，写期望值、实际值、哪条指令（展开后的原文与行号）、该指令的结果文件夹和日志位置，同样的内容打在标准错误上。

## 热加载 D 档脚本

`scripts/reload-d-entries.test` 把三版测试包依次热加载进开着的游戏，每步读回敌人行、复制序号与武器条目。测试包由 `tests/ReloadPacks/build-reload-packs.ts` 用网站自己的导出器从 `reload-packs.json`（固定输入）生成，产物在 `artifacts/work/reload-packs/v1|v2|v3`（不进 Git），生成时每份都过一遍离线包准入：`pnpm ts Mods/ForgeDevelopment/tests/ReloadPacks/build-reload-packs.ts`。三个包是 `ReloadD-Rundown`（一关的 Rundown，敌人行即 Map Rundown 的敌人行）、`ReloadD-Enemies`（敌人内容条目）、`ReloadD-Weapons`（武器内容条目），Rundown 占主菜单第 8 格。v1：Rundown 三行 Keep、Swap、Drop；v2：新增敌人条目 A 与武器条目；v3：撤下 Drop 行与敌人条目 A（新增条目 B），Swap 行与武器条目原地替换。脚本的断言：每步 `status == committed`、无拒收、记录摘要对得上；各步的名字列表含该有的、不含该撤的；`ownOrder` 依次为 `RRR`、`ERRR`、`ERR`（内容条目先于 Rundown 行，原有行不动）；`vanillaCount` 始终不变；`enemyRowsIncomplete` 与 `weaponRowsIncomplete` 为 0。换代只在 Authoring 模式、大厅干净时发生，所以脚本先 `leave`。三个包装完后留在档里（交接没有撤包的办法），跑完删档案里 `ReloadD-*` 文件夹。

主机换代后另一台机器加入的复制序号核对：每台机器各跑一次 `scripts/reload-d-replication-table.test`（读出自己的序号表），再 `python -B Mods/ForgeDevelopment/tools/compare_replication.py <主机的 console 运行目录> <后加入机器的运行目录>`，不一致时逐位指出（退出码 1）；真联机的步骤见合同 §4.5「热加载」。

## 网络模拟

`NetSim`（`Native/Network/NetSim.cs`）把本机发出的每个包按给定延迟加抖动扣住再发，游戏自己的和所有模组的都算（都经 `SNet_Core_STEAM.SendBytes` 的三个重载）；不可靠包按比例丢，可靠包不丢、只多等一次，发给同一玩家的可靠包保持顺序（群发、列表发和单发算在一起，不分通道）；群发和列表发的接收者在游戏发出的那一刻定下，包在等的时候进组的玩家收不到、出组的照样收到；三个参数都给 0 等于关闭。只动发送侧：两台机器各开一次才是双向。钩子在第一次 `Set` 时安装，关闭时全部直通。联机测试用设置文件开：`NAinfini.ForgeDevelopment.cfg` 的 `[Network Simulation]` 填 `DelayMs`、`JitterMs`、`LossPercent`（任一非 0 即从第一帧起生效，改后重启），因为指令台和采集一样，只要会话里有别的玩家就关掉，下面的指令只在单人或别人进来之前能跑。开、关、被拒和每次 `Report` 都在 `LogOutput.log` 写一行 `net-sim …`。指令文件 `probes/commands/net-sim.forge`：`net-sim-250`（250 毫秒）、`net-sim-250-jitter-loss`（250 毫秒、抖动 30 毫秒、不可靠包丢 2%）、`net-sim-report`（当前参数与 held / released / dropped / waiting 计数）、`net-sim-off`。结果以 `refused` 开头表示钩子没装上、模拟不生效。

## 探针成员离线检查

`tests/ProbeMembers` 在 `python Mods/verify.py ForgeDevelopment` 里随其他测试项目一起跑（需要 BepInEx 档案，读其中的 `core/Mono.Cecil.dll`）：先构建 `ForgeDevelopment.Native`，再用 Cecil 只读打开 `artifacts/dotnet/bin` 下的 `ForgeDevelopment.Native`、`ForgeEngine`、`ForgeEngine.Framework`、`ForgeCore`，按运行时的解析规则逐条走 `probes/commands/*.forge` 的静态目标、`call`、`read`、`set`、`wait.path`（静态目标第一段是静态成员，之后是实例成员，属性、字段、无参方法依次找，含基类；`call` 的返回类型就是下一步 `result` 的类型），以及 `tools/*.py` 里 `call(类, "成员")` 点名的本包类型成员。走到游戏的 interop 类型、`object`、接口、泛型参数或 `drive.*` 调用后停下不判；在 Forge 或 BCL 类型上找不到的名字、`Forge` 开头却不存在的静态类型都失败。自带一条读已删除的 `RuntimeKernel.schedules` 的坏探针，查不出它这项检查本身失败。结果行：`<通过>/<总数> passed; failed=<n>; commands=<条数>; toolCalls=<条数>; assemblies=<目录>`，每条失败一行 `FAIL: <文件> <命令 id>: '<路径>': no member <名字> on <类型>`。

## 诊断包说明（原包页 README）

> 实施顺序与原版内容完整覆盖见[唯一开发计划](../../Docs/forge-contract/FORGE-FRAMEWORK.md) §4，分步验收见 §7；本文件仅说明实现与用法。

可选的作者诊断包：观察生成、空间、性能、异常与运行证据，关联到作者对象并输出报告。新增原版读数的入口也在本包，见下文“新增原版读数”。

本包的记录器（Native/Recorder）只为作品验证比对记证据：方法追踪、日志与状态，写入作者会话的记录目录；玩家回放与回放查看归 GTFOReplay。

**普通玩家默认不依赖这个包。** 它不调度正常玩法，也不把"没有报错"解释为地图正确。需要玩家自己启用诊断回传才能用起来的能力不算完成——诊断是作者型用户的第二层入口，不是玩家必经路径。

交付要求、作者工具范围与验收见唯一开发计划 §6 U-DEV-MOD 和 §7（链接见[仓库 README](../README.md)），发行说明见 [CHANGELOG.md](CHANGELOG.md)。性能日志的采集器在本包内，并以源码链接的方式被 ForgePerformance 编入；一般游玩用不到；InfiniTweaks 只放 Quality of Life 功能。

### 工程与启动门槛

只有一个工程：

| 工程 | 内容 |
| --- | --- |
| `Native/ForgeDevelopment.Native.csproj` | 真实 BepInEx 插件 `ForgeDevelopment.Native.dll`，包含诊断 Hook、快照边界和全量记录器 |

本包没有托管 SDK 程序集；包版本只取 `Native/Host/Plugin.cs` 的 `PluginVersion`。

插件身份是 `[BepInPlugin("NAinfini.ForgeDevelopment", "Forge Development", "1.0.0")]`，硬依赖 `NAinfini.ForgeCore` 与 GTFO-API，不依赖 ForgeEngine：原版、MTFO 与 Forge 关卡都能用。原生工程只引用 `ForgeCore.csproj`；构建只需要 `GTFOBepInExPath`。Forge 地图专属的作者读数（计划重读、运行时变量、生成进度、房间解析、模型射击、机器人位）在 ForgeEngine 的 `ForgeEngine.Authoring.AuthorReads`，本插件运行时按类型名找它（`Native/Host/EngineLookup.cs`），没装 ForgeEngine 时答 `forge-engine-not-installed`，ForgeEngine 不在 Authoring 模式时答 `forge-engine-not-authoring`。

**启动门槛：** 本插件自己的 `[General] Enabled`（默认 `true`）决定是否启动；`false` 时记录一行 inactive 日志后返回，不绑定其他配置、不打 Hook、不加组件。会话里多于一名真人玩家时同样不启动记录器与原生追踪（已在运行则停止），只写一行 info；单人带机器人不算多人。启动顺序是绑定配置 → 解析与预检固定目标 → `RuntimeDiagnostics.Initialize` → `RecRuntime.Start` → 按目标安装本程序集的观察补丁 → 作者、采集、实验与可选性能组件。原生安全拒绝只停该目标，记录 `development.hook-refused`（目标、原因与实际补丁 owner）和 `session/trace_refused profile=fixed-hooks`，其余采集继续；`development.hooks-ready` 给出安装与拒绝数，部分覆盖不能当作完整采集。相同目标的多个补丁合并安装，安装前再次执行同一原生门禁；没有原始来源记录的已改写入口仍拒绝，不用 owner 名称、磁盘原字节或 trampoline 猜测准入。目标声明错误、实际补丁安装异常或组件启动失败仍按已获取阶段逆序清理；原始异常和清理失败保留，同一实例不重试。记录器内部各阶段自己降级：会话开不出来才整体跳过，日志/跟踪/进程清单任一段失败只记 `stage_failed` 并继续其余通道。完整组件启动后查看 `Forge Development 1.0.0 loaded`，并同时核对钩子覆盖汇总。

capture、experiments 与记录器在同一个程序集里，全部是编译期直接引用：`RecRuntime` 在层级边界与关卡清理时直接调 `CaptureRegistry.SnapshotAll`，热键从 `RecHotkeys` 直接调 `CaptureRegistry.SnapshotAll("hotkey")`（F7）与 `ExperimentPanel.Toggle()`（F5），`Plugin.Load` 直接调 `CaptureRegistry.EnsureStarted`、`ExperimentPanel.Load()`，没有按签名反射查找的第二条路径。Forge 内核自己的诊断由 ForgeEngine 写进 `BepInEx/forge-logs/*.jsonl`，记录器不再另开 `forge` 通道。

本插件不向 Forge 内核登记模块，也不提交玩法请求。报告 metadata 的 `forgeVersion` 是已装 ForgeEngine 的版本（没装写 `absent`），另写 `developmentVersion`；问题来源名为 `ForgeDevelopment`。

### 安装与配置

开发包 `NAinfini-ForgeDevelopment`（发行集里受众为作者）只放 `ForgeDevelopment.Native.dll` 与它的 probes，装在 ForgeCore 之上；普通玩家包不包含它。

#### 完整包自检与配置更新

保持发行 ZIP 的目录结构：`ForgeDevelopment.Native.dll`、`probes/`、`tools/` 是同级路径。原生命令的初始文件从 DLL 旁的 `probes/commands/` 读取，只复制尚不存在的 JSON 到 `BepInEx/ForgeDevelopment/commands/`。追踪档从 `probes/trace/` 同步到 `BepInEx/ForgeDevelopment/trace/`：包内同名档内容变化时采用新包，旧字节保存在 `.previous-<SHA256>` 副本并写出更新日志；该副本不参与加载，独立命名的作者档保持不变。`BepInEx/config/` 下本插件只保留 `NAinfini.ForgeDevelopment.cfg`。

追踪回调只用包装器指针标识原生对象，不调用 `GetInstanceID` 或原生名字 getter；未经验证的原生字段、集合与装箱值写明未读取，不算完整观察。全档崩溃回归将 `[Recorder] TraceEnabled=true`、`TraceAllProfiles=true`，包含默认关闭的 `everything`。自动单人进关的 `[LevelGuideCapture] MinimumInLevelSeconds=180` 要求连续在关内停留三分钟再导出、返回菜单；普通采集默认仍为两秒。无人值守回归可设置 `QuitWhenComplete=true`，在队列完成且关卡和大厅清理完毕后正常退出游戏；默认不自动退出。巡检结束后将该节 `Enabled=false`。

从可信发行来源取得 ZIP，并核对其发布的 SHA-256。将完整 ZIP 解压，在插件目录（ZIP 里的 `plugins/ForgeDevelopment`；模组管理器装好后是 `BepInEx/plugins/NAinfini-ForgeDevelopment/ForgeDevelopment`）使用 Python 3.10+ 运行：

```powershell
# 无需仓库、.NET SDK 或游戏程序集：核对完整资源集合、SHA-256 与构建时门禁收据
python -B tools/verify-probe-package.py

# 先关闭游戏；预览差异，不写 profile
python -B tools/install-probes.py --bepinex-root "$env:GTFO_BEPINEX_PATH"

# 没有同名冲突时安装，并记下本版文件的安装收据
python -B tools/install-probes.py --bepinex-root "$env:GTFO_BEPINEX_PATH" --apply

# 安装后核对随包 JSON 在实际读取目录中的字节
python -B tools/install-probes.py --bepinex-root "$env:GTFO_BEPINEX_PATH" --check
```

`GTFO_BEPINEX_PATH` 必须指向要使用的 profile 中已经存在的 `BepInEx` 目录。自检读取包内 probes 目录里的 `package-manifest.forge`，缺文件、额外文件或摘要变化会失败；它不是数字签名，也不替代发行来源与 ZIP 摘要核对。不要把生成的日志、证据或配置保存在完整包目录内，避免改变被核验的文件集合。

已有同名配置不同于本版时，普通 `--apply` 在写入前停止。检查预览后，明确要采用发行默认值才使用 `--apply --replace-existing`；不保存副本，被替换文件的路径和原 sha256 列在输出的 `files` 里（要回到旧内容就重装旧包），无关自定义文件保持不变。安装收据识别出本版已移除的旧 JSON 时，必须另加 `--prune-retired` 才会删除**仍与旧收据摘要一致**的文件（同样列在输出的 `retired` 里）；已经被用户修改的旧文件始终要求人工处理。首次升级没有旧安装收据时，工具无法判定额外 JSON 是旧发行资源还是用户文件，按 `unmanagedFiles` 明确列出并保留，不猜测删除。

安装或更新后重新启动游戏。普通 trace 配置仅在插件启动时载入；F5 的实验列表可用 `reload files` 重读命令，但不能借此重装 trace hooks。

#### V1 检查点入口与证据边界


发布自检证明资源完整、构建时记录入口检查通过，不证明检查点在 GTFO 中执行成功。玩法前提未实现的条目在门禁收据中保留 `gameplayPrerequisiteBlocks`。游戏内提交记录请求、截图文件和真正落盘的原始 JSONL 也要区分：核对会话、点身份、前后阶段、`index.forge` 的丢弃数及预算状态，不能把按钮反馈或无异常当作游戏验收。

#### 指令台与测试脚本

`tools/drive_cli.py` 是指令台：对 `run-drive-session.py start` 起好的常驻会话逐条下指令，或用 `run <脚本>.test` 整份执行测试脚本。每次执行的指令记录、日志和结果写进 `<work-dir>/console/<开始时间>-<脚本名>/`，其中 `run.json`（`forge.console-run` 1.0.0）可导入网站的结果页。指令表、脚本语法、会话记录与重放见本文件后面的“指令台与测试脚本”一节，示例脚本在 `scripts/`。游戏内没有测试面板，也没有 `[PackTest]` 配置；断言只检查脚本里写出的读数，配置回读不算行为证据。


#### 诊断配置

配置文件是独立的 `BepInEx/config/NAinfini.ForgeDevelopment.cfg`（节选；`[Performance Diagnostics]` 另有摘要间隔和卡顿阈值）：

```ini
[Authoring]
ExportReportKey = F10
ExportEnemyAbilityKey = F3
InspectionBudgetMilliseconds = 2
ProjectManifest =

[Recorder]
Enabled = true
BudgetGiB = 2
SegmentMiB = 8
TraceEnabled = false
PopulationBudgetEnabled = false

[Performance Diagnostics]
EnablePerformanceLogging = true
ManualSnapshotKey = F9
```

F9 写性能检查点并刷新日志，F10 导出当前生成报告，F11 开关战斗采样。报告写入 `BepInEx/ForgeReports`，性能日志写入 `BepInEx/PerformanceLogs/ForgeDevelopment_*.log`，只保留最新 10 份，单份上限 64 MiB（超出后写一行截断记录并停止写入，仅保留结束记录）。关卡结束和进程退出也请求导出。写入失败会报告错误；看 JSON 里的 `dropped` 和检查状态，不能只看 `outcome`。只关闭 `EnablePerformanceLogging` 只关闭性能采集。

关卡生成进度（`Diagnostics/FactoryProgressWatch.cs`）：从 `LG_Factory.Setup` 到 `FactoryDone` 或离开关卡之间，每 0.5 秒读一次原生工厂（当前批次 `m_currentBatchName`／`m_batchStep`、当前任务指针、静态的任务实例计数 `m_jobInstanceCounter`），每帧只做一次时间比较，不分配内存。BepInEx 日志（因此也在录制器的 log 通道里）每 10 秒一行 `factory.progress batch=… job=<类名>:<GetName> jobsCreated=… jobInstanceCounter=… sinceProgress=…s inBatch=…s elapsed=…s`；批次、当前任务指针或实例计数连续 5 秒不变则写一行 Warning `factory.no-progress …`，之后的重复间隔 10、20、40 秒翻倍到 60 秒封顶，所以持续卡死约每分钟一行。同样内容写进当次报告的 `factory` 事件，卡住时另记 `factory_no_progress` 问题（按批次聚合计数）；每次批次切换在报告里记一条 `factory/batch`。`jobsCreated` 是本关从第一次采样起新建的任务数；`jobInstanceCounter` 是原生静态计数的原值，整个游戏会话只增不减，不是本关的数量。判定在 `FactoryProgressTracker.cs`，不依赖游戏类型。`m_jobInstanceCounter` 是任务实例创建计数而不是已完成任务数，只用于判断是否有变化；它不能说明剩余任务量，所以"很慢"与"卡死"要结合 `jobInstances`、`job` 与 `inBatch` 判断，而不是看单行。该监视只观察，不改变工厂行为。

性能日志与 ForgePerformance 共用 `forgeperformance-2` 格式，插件版本保持 1.0.0。默认每秒汇总帧时间与各分项的可合并直方图、总耗时／最大值、CPU／呈现等待、精确卡顿计数、房间／雾／敌人数的最小值／最大值／末值、标记和内存；完整帧只保留卡顿、标记及标记前后各 3 秒。前置缓冲最多 2048 帧，丢失历史与后台队列丢帧会计数，不阻塞游戏。字段不可用时保留原因。波次停止（`SurvivalWave.StopEvent`）、刷怪组登记（`RegisterGroup`）已被 ForgeEngine 的 Enemy 领域补丁，警报状态变化（`ChainedPuzzleInstance.OnStateChange`）已被 ForgeEngine 的 Map 领域补丁；共享原生后端拒绝已被他包补丁的入口、也不叠加原始 detour，所以 ForgeDevelopment 不声明这三个标记，只保留波次开始。日志的 `marker_unavailable` 和 `wave_marker_coverage` 逐项写明缺口；独立 ForgePerformance 通过 Harmony 链仍提供它们。Python 分析器流式读 `forgeperformance-1` 与 `forgeperformance-2`，没有格式标记的更早日志不读、直接报错，整局分位数由直方图合并，跨上下文窗口不用于房间／雾对照。游戏运行时 Windows 资源管理器可能显示日志为 0 字节，即使文件正在写入；F9 仍写检查点。

### E1 原生人口点数记录

退出游戏后把 `[Recorder] PopulationBudgetEnabled = true`，保留记录器与状态采集开启，再重新启动并进入关卡。此项默认关闭，必须在区域生成前启用；它安装单独的精确签名观察器，不把带 ref/out 的方法交给通用 trace。F6 只加书签；F7 请求全量状态与人口预算快照，F8 留画面。

记录写入同一会话 JSONL 的 `population_budget` 通道，schema 为 `forge.population-budget.v1`。先查 `hook` / `installation` 的实际安装结果，再按会话、worldEpoch、observationEpoch、callId、parentCallId 与 groupLife 对齐调用；缺原生准入、前后半段、原调用被跳过、异常、身份不明、截断或丢记录都不能算通过。已有正常 trace 档不能替代这些显式记录。

区域的 ref population 前后值、单组选择的 out cost、实际生成列表与全局各维度占用分别记录；配置预算、实际消耗和敌人数不混算。F7 的 `allowedTotalCost` 与 `dimensionEnemyCostMap[].cost` 用于实际占用及全灭后的归零核对，缺字段不补零。直接／强制生成 API 未提供选择成本，原生 placement 引用的跨回调生命周期尚未证明时保留未证；不得由同一指针、数量或时间邻近推算。其他补丁可能改变观察点数值，需一并保留已加载程序集与补丁清单。

该记录器的离线编译和回归仅证明可记录的字段与拒绝边界，不证明这次游戏已完整采到三本账。覆盖检查中的明确记录缺口会使 recording readiness 失败，生成结果可重现不等于记录就绪。

### 原版敌人能力参考清点

等游戏 DataBlock 已加载后按 F3（可改 `[Authoring] ExportEnemyAbilityKey`），每次将独立原件写至 `BepInEx/ForgeReports/enemy-abilities/<gameRevision>/<sessionId>-<captureSequence:D6>.forge`，不覆盖上次观察，日志给出路径与条数。`forge-enemy-ability-reference` / `schemaVersion=1` 的原有 `enemies` 结构不变：已加载块可能被其他模组改变，先核对游戏修订号与模组组合，再按 `enemyId`、`index` 对比 `value`；现场 `game-observed` 不等于原版出处。定义读取失败时不输出伪造空清单。

追加的 `prefabObservations[]` 按 `abilityPrefab` 关联定义，仅查询已经加载的资源，不加载、不实例化。`components[]` 记录真实 `EnemyAbility` 派生组件的 `instanceId`、`nativeType`、`abilityType`、`abilityIndex`，`fields[]` 按原生字段元数据读取生成访问器，保留 `field`、`valueType`、`status`、`value` 与读取错误。只采集支持的数字、布尔、枚举、向量和有界浮点数组，不遍历任意对象图；`observed` 只说明该支持字段成功读取，不表示整个预制件内部全部覆盖。资源未加载、字段缺失、非有限数、类型不支持与预算截断均保留明确状态。

`liveCooldownObservations[]` 读取活敌人的 `m_cooldowns` / `m_lastTriggerTimes`，用 `AbilityComps[abilityType].Comps[abilityIndex]` 给出实际组件，并附 `componentObservation` 的实例字段。此处 `abilityIndex` 是原生分类表的槽位，不能直接当作 `enemies[].abilities[].index`。`sample` 保留 `unityTime`、`realtimeSinceStartup`、`simulationTick`、`lastTriggerTimeRaw` 和 Runtime 实际解析的 `entityLife`（`Id@WorldEpoch.LifeEpoch`）；同时保留 `enemy`、敌人块 `enemyId`、`enemyInstanceId`、组件身份以及 `source.generationId/worldEpoch/captureId`。

在同一已解析生命期、同一能力槽与组件上重复 F3，`interval` 给出前一个已成功写盘的 `previousCaptureId`、两种时钟的经过秒数、模拟 tick 差与原生触发计时差。未解析生命期、生命变化、时钟倒退或缺上次样本不会补 0 或推断间隔。采样差只证明两次读数之间经过的时间，不能证明冷却何时结束或单位是什么；仍须结合真实 `UseAbility` / `IsAbilityReady` 调用 trace 和实际出手效果判断，F3 自己不调用这些玩法方法。`fieldUnits.Cooldown`、`pending` 和 `interval.unitStatus` 保持待游戏核验，不自动修改离线参考表。

两段补充采集均有数量与 100 ms 协作预算；`observationCoverage.prefabs/liveCooldowns` 记录发现量、已检查量、实际耗时、失败原因和省略错误数，`limits` 给出上限。单个原生调用无法抢占，超时停止后续读取；`partial`、`not-loaded`、`unsupported-type`、缺实例或缺字段都不算该项通过。

### 新增原版读数

`catalog/authoring/vanilla-values.json` 里状态为 `needs-ingame` 的值靠一次手动游戏会话补读，两步都在终端里运行，需要主检出里的本机输入（目录、`Source/`）：

```powershell
# 1. 游戏关闭时写计划和启动时间窗；--additional 另读能力预制件与种群键
python -B Mods/ForgeDevelopment/tools/capture_materials.py lease --bepinex-root <profile>\BepInEx --work-dir artifacts/work/vanilla-readings/<名字>
# 2. 自己启动游戏、单人进关、退出游戏后，按进过的关卡归档读数并严格回填，同时恢复租约
python -B Mods/ForgeDevelopment/tools/capture_materials.py collect --bepinex-root <profile>\BepInEx --work-dir artifacts/work/vanilla-readings/<名字> --level R1A1
```

前提：开发包已安装，`[Recorder] RecordKiB = 1024`（命令不改游戏配置）；计划存在时记录器随插件启动。读数写在 `materials` 通道，`collect` 只收这次租约（`captureRunId`）且落在启动时间窗内的记录，存到 `Source/game-exports/levels/<gameRevision>/<关卡>/vanilla-readings.<sha>.jsonl`，再按 `capture_vanilla_values.py` 的严格规则回填；回填结果在 `<work-dir>/vanilla-backfill.json`。

原版读数在真实初始化点记录。此构建把部位初始化内联到 `Dam_EnemyDamageBase.Setup`；采集在它返回后读取 `DamageLimbs`，并验证已初始化的接收器、所属敌人及数组中的部位身份。模板的零值不回填。每条读数携带补丁清单、启动及读取时不存在 Forge 内容的证明、活动计划数、实验执行数与原版数据覆写检查；任何活动计划、配置内容、实验或未知补丁都使读数拒绝。人口选择的两个 `out` 参数只在原生调用完成后读取，定点修复当前 IL2CPP 桥提前读取输出地址的问题，原生返回和写回保留。游戏文件指纹与目录使用同一 `GameAssembly:sha256:` 格式。

### 全量记录器

另有一个逐条记录整个会话的记录器，写入 `BepInEx/ForgeReports/rec-<时间>-<role>-<slot>/`。它默认关闭（`Enabled = false`），开启后按通道落盘、不依赖任何一份具体的验证点清单；写入线程每次清空队列都把已写的行交给系统，外部读者（驱动、`rec-query.py`）随即读得到，不必等后续记录填满缓冲。会话里出现第二名玩家时记录器与原生追踪都不启动；已经在录的会在下一次检查时停止，只写一行 info 说明。跟踪默认也关闭（`TraceEnabled = false`），需要时打开。记录器不抄 ForgeEngine 内核的逐节点行为追踪；Forge 关卡的触发、入口与步骤代码看 ForgeEngine 自己的 forge-logs。`probes/trace/net.forge` 默认不启用，网络追踪要手动开启。打开的追踪在游戏数据就绪后每帧装一个钩子（每个十几到几十毫秒，全套两千多个要两分钟以上），关卡生成（游戏状态 Generating）期间暂停、之后接着装，`development.trace-ready` 的 `generationHeldFrames` 是暂停的帧数；要追踪生成过程，等这一行出现后再进关。手测只开记录器、不开 `TraceEnabled`。

配置键在 `[Recorder]` 节：

```ini
[Recorder]
Enabled = false
WorldCaptureEnabled = true
BudgetGiB = 2
SegmentMiB = 8
RecordKiB = 64
QueueRecords = 16384
TraceEnabled = false
BookmarkKey = F6
BookmarkLabel =
SnapshotKey = F7
ScreenshotKey = F8
ExperimentPanelKey = F5
```

每个会话目录里是若干 `rec-<session>-<序号>.jsonl` 分段、截图目录 `shots/` 和一份 `index.forge`（关会话时写；正在录的会话还没有它）。正在写的分段是明文，可按字节偏移实时读，`rec-query.py` 读正在录的会话时跳过末尾还没写完的半行，分段头还没落盘的分段列在提示里，没有任何已关分段时给出明确说明，不报错；分段一关闭就压成 `.jsonl.gz` 并删掉明文（读取端正占用时稍后再删）。每个分段第一行是分段头 `{"v":"forge.rec","segment":{...}}`，放这一段记录共有的上下文字段（session、role、slot、level、generationSessionId、generationId、worldEpoch）；每条记录只写与分段头不同的字段，缺的写进 `$absent`。关卡或世界变了就开新分段。完整记录 = 分段头字段（去掉 `$absent`）叠上本行字段，`tools/rec_segments.py` 是唯一读法，所有读取工具都用它。`BudgetGiB` 按未压缩字节计。`index.forge` 记录通道计数、分段列表、丢弃数、是否触到总量上限以及装了哪些补丁；`patches` 字段就是跟踪器的启动报告，所以一个会话能自己说明它当时在跟踪什么。同一份启动报告在 trace 配置加载时就作为文档 `trace-patches.forge` 写入会话目录（数千条补丁与拒绝远超单条记录上限，不进 JSONL），会话通道里的 `patches` 记录只指向该文档。

会话目录里另有一份 `enemy-appearance.json`，在关卡生成完成（`level-generated`）时清点一次：逐条记录每个 `EnemyDataBlock` 的 `ModelDatas[].ModelFile` 与 `ModelCustomization`、每个已加载模型资源的蒙皮渲染器（对象、`rootBone`、按序骨骼名、材质与 shader）、每个出现过的 shader 的属性表，以及每个外观定制资源里的 `CB_CustomPrefab` 与 `CB_CustomMaterials` 条目；结果与上一次完全相同就不重写。它只写原始事实，给网站以后导入附加物清单和可调色的着色器属性用。

会话目录里还有一份 `enemy-limbs.json`，也在关卡生成完成（`level-generated`）时清点一次，用的是游戏实际生成的敌人预制体：逐条记录每个已加载 `EnemyDataBlock`（`enemyType` 是它的 persistentID）经 `EnemyPrefabManager.GetEnemyPrefab` 取到的预制体（该类型当时是否已建也记下）与它 balancing 块的 `AllowDamgeBonusFromBehind`；模型根是预制体上 `EnemyAgent.MainModelTransform`，记下它下面每个蒙皮渲染器的名字、相对模型根的 4×4 矩阵、`rootBone` 名与相对位置、骨骼名列表与 bindposes 数，以及 `Dam_EnemyDamageBase.DamageLimbs` 每项的下标、`m_limbID`、`DebugName`、是否 `Dam_EnemyDamageLimb_Custom`、对象名与到模型根的路径、最近的蒙皮骨骼祖先、相对模型根位置、`m_type`、`m_health`/`m_healthMax`、弱点与护甲倍率、`HasDestruction()`、`DestructionType`、Custom 的 `m_clampDamageToLimbMaxHealth`、碰撞体类型与 bounds 中心（相对模型根）；预制体上还没被 setup 写入的生命写 `null` 并注明原因。末尾按「Custom 肢体位置 ↔ EggSack 渲染器 rootBone 位置」的最近距离给出 `tumorPairing` 一对一配对表与每对距离，供部位编号核对。结果与上一次完全相同就不重写。

每条记录是一行 JSON，固定字段是 `v`（`forge.rec`）、`seq`、`session`、`channel`、`kind`、`t`（进程毫秒）、`frame`、`snetTime`（玩家同步时间，取到才有）、`role`、`slot`、`level`（远征 key）、`worldEpoch`、`tick`，业务内容在 `body` 里。通道含义见下表。

| 通道 | 内容 |
| --- | --- |
| `session` | 开始、启动事实、跟踪配置解析结果、补丁装载与卸载、实验命令装载、关卡边界、收尾阶段与摘要 |
| `tracer` | 原生方法或 CLR 包装层调用、`changes` 变化、速率溢出计数、首次命中调用栈、补丁自身错误 |
| `net` | SNet 包与状态复制（capture 批） |
| `state` | 快照（capture 批；关卡对象按 `LG_Floor` 层级遍历，被禁用的门/终端/物件也在里面，每个对象行带 `active`） |
| `log` | Unity 线程日志、BepInEx 监听、进程未处理异常 |
| `mark` | 书签 |
| `shot` | 截图路径 |
| `exp` | 实验（exp 批） |
| `probe` | 验证点命中（capture 批） |

限制与失败都是显式的：单条记录超过 `RecordKiB` 时那条被丢弃并计数，总量到 `BudgetGiB` 后停写并把每个后续记录计入 `dropped`，屏幕左下角和 `session` 通道会说明发生了什么。写入线程只拿到已经序列化好的字节，游戏线程只做入队；记录器自己不会读 IL2CPP 对象。

### 怎么抓一个新的点

一个还没验证过的行为点，从"我在游戏里想知道这里发生了什么"到"我手里有一条记录"，是三步，不需要改代码、不需要重新发版：

**1. 加一份跟踪配置。** 配置放在 `BepInEx/ForgeDevelopment/trace/`，第一次运行时自动从包的 `probes/trace/` 复制过去；之后只读这个作者数据目录，你的改动不会被插件更新覆盖。

```json
{ "profile": "my-door-check", "enabledByDefault": true, "entries": [
  { "type": "LevelGeneration.LG_SecurityDoor", "methods": ["OnOpen", "OnClose"],
    "record": "args+result", "instance": ["m_sync.m_stateReplicator.State", "Gate.m_linksTo"],
    "firstStack": true, "maxPerSecond": 50, "onOverflow": "count" }
]}
```

- `type` 支持通配符，写 `*LG_SecurityDoor*` 也行；`methods` 与 `exclude` 同样支持 `*`。
- `record` 是 `count`、`args`、`args+result` 或 `changes`。`changes` 只在参数或 `instance` 字段与上次不同时记一条，适合每帧都会跑的方法。
- `instance` 是在实例上读的字段路径，由 `RecReflect` 读，私有字段也能读。
- `firstStack` 在第一次命中时记一次托管调用栈。
- `maxPerSecond` 之外按 `onOverflow` 处理，并每 5 秒写一条溢出计数；`Update`、`LateUpdate`、`FixedUpdate`、`OnGUI` 和属性 getter 默认只计数或被跳过，想覆盖就把它写进 `methods`。

启动时每个条目的解析结果都会写进 `session` 通道：匹配到几个方法、跳过几个、原因是什么。方法签名是泛型、带指针或带 `ref` 参数时跳过，原因是 `generic` 或 `unsupported-parameter`。

原生入口安全检查不会因配置而放宽。共享方法体、短方法、条件分支或不受当前原生解码器支持的序言，在有可验证 CLR 包装层时按包装层记录；调用和配对记录的 `observationScope=managed-wrapper` 明确表示只覆盖经该包装层的调用，不覆盖游戏原生代码直接调用。`native-method` 表示原生入口观察，`managed-method` 表示普通托管方法。固定生命周期与性能观察器仍要求原生入口，不以包装层记录替代。

预期的原生覆盖缺口每批只输出一次 info 汇总，按原因计数；共用函数体索引由 ForgeCore 建（`core-native-body-index`，开发者日志）。会话目录的 `trace-coverage.json` 保留每个 profile、类型、方法、完整签名、精确原因、观察范围、已有补丁 owner 和尝试次数；相同清单也在 trace 报告的 `nativeCoverage` 中。`unavailable` 表示没有安装该观察点。包装层安装失败仍输出 error，并保留失败记录；不重试、不记作成功。记录器不可用时汇总明确标出 `reportQueued=False`，可通过 trace 报告查询内存清单。

**2. 进游戏，装上这个包，触发那个行为。** 需要的话按 F6 打书签（带上你当时看的方向）、F7 要一份全量快照、F8 截一张图。整个过程不需要重启插件，改完配置重启游戏即可。

**3. 离线查。** 会话目录就是输入：

```powershell
# 这个会话有哪些通道
python Mods/ForgeDevelopment/tools/rec-query.py "$env:GTFO_BEPINEX_PATH/ForgeReports/rec-20260916T101500Z-host-0" --list-channels

# 只看门的方法调用
python Mods/ForgeDevelopment/tools/rec-query.py <session> --channel tracer --type LG_SecurityDoor --limit 20

# 只看 snetTime 10 到 20 秒之间发生了什么
python Mods/ForgeDevelopment/tools/rec-query.py <session> --since 10 --until 20

# 书签前后 5 秒
python Mods/ForgeDevelopment/tools/rec-query.py <session> --around 14.2 --window 5

# 给脚本用的 JSON
python Mods/ForgeDevelopment/tools/rec-query.py <session> --channel log --json
```

两台机器都开这个包时，`rec-merge.py` 合并两份会话，按 `snetTime` 对齐，并指出"客户端发生了、主机没收到"的事实。配对规则是可选的表格文件（`pointId`、`mergeRule` 两列，制表符分隔）：

```powershell
python Mods/ForgeDevelopment/tools/rec-merge.py host=<主机会话目录> client=<客户端会话目录> --same-run --rules <规则表>
```

规则写法是 `trace:<通道>/<kind>[/<类型>.<方法>]#<名字>=<body 内路径>[,<名字>=<路径>][;window=<秒>]`，例如 `trace:net/replicator#m_state=fields.m_state`。不给 `--rules` 时按通道/kind/类型/方法配对；给了但文件缺失或规则写错会明确报出来。

网站离线包导出（`Web/src/features/map/map-package.ts`）只给 `NAinfini.ForgeEngine.cfg` 写 `[Runtime] Mode`，不写 `[Authoring] ProjectManifest`。玩家包不包含本插件（开发层不进玩家包）。

### 现行输入与报告合同

项目清单的根对象必须包含 `format`、`projectId`、`experiment`、`requiredPlugins`、`sources`、`objectReferences`，不接受额外或重复字段。`format` 固定为 `gtfo-forge-project`；**不接受 `schemaVersion` 或 `expectedObjects`，不做旧格式 fallback**。

`experiment` 必须包含 `packageVersion`、`authoringSha256`、`dependencies`，dependencies 是字符串可以为空。`requiredPlugins` 条目是 guid 与 version（精确版本，按相等判定，I-RELEASE）；`sources` 是 path、sha256、kind，kind 只接受 DataBlock / ModConfig；hash 是小写 SHA-256。

对象声明只支持 zone 与 room。Zone locator 明确 layoutId、dimension、layer、localIndex；room locator 是 unique-geomorph-in-zone，明确 zoneAuthorId、该区域的原生 dimension / layer / localIndex（必须与那条 zone 引用逐值相等）、room 的 id 与 revision 和完整的 `Assets/` 来源身份。房间解析只在那一个区域内进行：区域没有就是 `zone_unresolved`，区域内没有这个房间就是 `no_candidate`，区域内有两个同源房间就是 `multiple_candidates`（候选照常列出），解析器没装或 prefab 取不回来仍是 `creation_context_unverified`。**显示名称、Hierarchy 路径、预览模型都不代替创建证据。**

`ProjectManifest` 配置是本地文件选择器，相对路径以 BepInEx 为基准；它与清单内 `sources` 的安全规则不同——sources 必须是 BepInEx 内的规范相对路径，用正斜杠，禁止绝对路径、盘符、空段、点段、上级目录和重解析点逃逸。

清单上限 4 MiB，严格 UTF-8 与 JSON、深度 32。来源扫描上限：256 目录、4096 条目、256 文件、单文件 8 MiB、总计 64 MiB；报告上限 16 MiB。`sourceCoverageStatus=complete` **只表示采样覆盖结束**，不表示 `sourceVerification=matched`，更不证明内存或游戏行为一致。

报告的 format 是 `gtfo-forge-diagnostics-report`，`objectReferences` 独立记录 worldEpoch、simulationTick、scanStatus、sourceVerification、groups 与 overflow。遍历是分帧观察，**不是原子世界快照**；完整的局部遍历也不证明目标、导航或远征可以完成。对象存在不等于任务脚本正确，`FactoryDone` 只表示观察到结构生成完成；现有诊断没有宣称修复 R7D2 随机异常或连续切关的 CullingCluster 异常。

### 检查会话与报告快照

`ProjectChecks.Load` 返回该报告实际附着的 scan，严格清单校验和来源采样保留。`ProjectInspectionSession` 绑定固定的 report、scan 与 worldEpoch，分别处理重复开始、扫描前取消、旧世界的迟到完成、无世界上下文和部分扫描。worldEpoch 是本包自己的计数（`RecTime`，GTFO-API 的 `LevelAPI.OnBuildStart` 与 `OnLevelCleanup` 各推进一次），与 ForgeEngine 的世界计数无关；报告的 simulationTick 不再有来源，写 `null`。`RuntimeDiagnostics` 在生成、切关、退出时清理扫描与来源请求；跨报告或跨世界的旧 JobTrace 不写入新报告。会话不在 `LG_Factory.Setup` 前缀里建：前缀里读到的可能还是上一个世界的 epoch，据此建的会话对不上本次生成的任务与检查步骤。`GenerationWorldBinding` 因此在生成开始后的第一次观察（第一个任务或第一帧）按当时的 epoch 建会话并加载 scan，任务追踪与生成后检查都与这个 epoch 比较；epoch 之后再变则检查以 `world_epoch_changed` 结束，任务不再写入。客户端若在第一次观察之后才收到主机 epoch，则会话绑在旧 epoch 并同样结束，这一情形没有重新绑定。每次 `Build()` 调用的追踪记录在 JSON 报告里为 `generation_job` 事件（`jobType` 字段是类名；只在返回完成或该任务首次调用时写），簿记耗时累计在 `generationTraceOverheadMs`。`WorldInspection` 的原生实例 ID 与显示路径分开；活动布局只取实际主维度的已生成层。

**Geomorph 的 prefab 名称不是作者来源的证明。** 缺少创建上下文时保留 `creation_context_unverified`，不伪造房间匹配。

报告在 Enqueue 时就捕获不可变的托管证据；之后的新世界、扫描完成、来源验证或取消都不能改写已经排队的旧快照。序列化、字节预算裁剪和原子写盘在 writer 线程，后台不读取 Unity 或 IL2CPP 对象。队列保留 8 个 pending 路径上限。`ShutdownSequence` 继续执行所有清理阶段并返回只读、有序的失败收据。

### 制作工具

使用 Python 3.10+ 标准库，不下载依赖也不执行外部代码：

```powershell
python Mods/ForgeDevelopment/tools/analyze_performance.py session.log --output performance.md
python Mods/ForgeDevelopment/tools/rec-query.py "$env:GTFO_BEPINEX_PATH/ForgeReports/rec-<会话>" --list-channels
python Mods/ForgeDevelopment/tools/rec-query.py <会话> --channel tracer --method OnOpen --json
python Mods/ForgeDevelopment/tools/rec-merge.py host=<会话> client=<会话> --same-run
```

离线性能分析按远征分段、计算帧加权 FPS、排出慢分钟窗口与长帧，报告扫描开销、被检测的模组路径、内存趋势和缺失的通道。它不会把窗口分位数平均成整场分位数、不会仅凭增长断定内存泄漏、不会仅凭一个异常归咎某个模组。

### 复跑

```powershell
python Mods/ForgeDevelopment/tools/verify-diagnostics.py --bepinex "$env:GTFO_BEPINEX_PATH"
```

脚本依次构建宿主、原生插件与各测试工程，运行插件启动、报告、项目规则、检查、快照、关机、场景清单、遥测、采样、原生布局与 Python 套件。默认新建系统临时目录，也可以指定一个尚不存在的 `--output`；源码变化或任何失败都返回非零。

最后是负例门槛：把包复制到证据目录，分别注入五个已知破坏（快照改为导出时捕获、旧 session 接受新世界的迟到观察、去掉错误条数上限、放开 trace 聚合上限、首个清理失败后停止后续清理），每个都必须编译通过并由对应套件判失败；锚点缺失或多于一处、变异后仍全绿都算验证失败。工作树与游戏 profile 不被修改。

### 边界

当前 DLL 不应作为玩家发行物。没有 GTFO 加载、多人、原生 Hook 安全或采集开销的验收；"不安装 / 安装但未启用 / 启用"三种实际加载模式也未在游戏里核验。InfiniTweaks 的 QoL 行为不属于本模块。禁止无限重试、重置指定 prefab 种子、静默替换房间、删除任务对象、任意挂接 CourseNode、吞掉异常或调低游戏日志级别来伪装成功。
