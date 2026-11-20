# Changelog

## 1.0.2

- 敌人成群时的定时检查（每 0.25 秒一次）更省：玩家是否存在每次只查一次，不再每个敌人查一遍；敌人有没有动画组件在登记时记下。各开关的作用和默认值不变。

敌人成群时的开关，各自独立；括号里是默认值和作用在哪台机器：

- `SetupRendererLookup`（开，每台机器）：不影响观感和玩法。敌人 `Setup` 时本模组改查自己的尸体记录，不再把渲染器遍历两遍。游戏内实测本模组自己的 `Setup` 前缀每次从约 0.038 ms 降到约 0.001 ms。
- `SpawnFrameDefer`（开，每台机器）：不影响观感和玩法。刚刷出敌人的那一帧，本模组自己的定时工作推到下一帧，不会连推两帧。
- `FarSkinQuality` / `FarSkinDistance`（开，20 米，每台机器）：影响观感。远处敌人的网格只用两根骨骼混合，不写运动矢量；远处关节变粗，开 TAA 时可能轻微拖影。
- `FarAnimatorThrottle` / `FarAnimatorDistance` / `FarAnimatorInterval`（开，25 米，3 帧，每台机器）：影响玩法。远处正在走路的敌人每几帧才算一次动画，动作发顿，跟着骨骼的受击盒最多滞后两帧；走路以外的状态和开了根运动的不降。
- 修复 `FarAnimatorThrottle`：被降频的敌人关掉动画组件时不再丢掉动画状态（之前会滑着走、没有跑动和攻击动作），恢复时还原组件原来的设置。
- `ClientHiddenSyncThrottle` / `HiddenSyncDistance` / `HiddenSyncInterval`（关，30 米，4 帧，只客户端）：影响玩法。看不见且较远的敌人每几帧才从主机同步一次位置，碰撞体随之滞后，隔墙射击或爆炸的判定可能不同；主机不受影响。
- `UpdateBudgets` 和六个 `Budget…` 数值（关，只主机）：数值默认就是原版（游戏内读回 `5,2,1 / 10,4,1 / 12,3,1 / 20,10,5 / 5,2,1 / 1`），不改数值打开也不变；改了会影响玩法：敌人思考、侦测、移动的节奏变了。
- `AvoidanceQuality` / `AvoidanceType`（关，1，只主机）：原版就是低质量避让（游戏内读回 1），只有 0（不避让）更省；改成 0 会影响玩法：敌人互相穿插、堵门方式变了。打开或改值时，已经在走的敌人当场换成新值，不再等它下次开导航。
- `PathfindingIterations` / `PathfindingIterationsPerFrame`（关，100，只主机）：100 是原版值；调低会影响玩法：寻路摊到更多帧，敌人起步可能变慢。
- `HitReactKeepsAgent`（关，只主机）：影响玩法。敌人受击硬直时只停导航、不关再开，硬直结束的位置修正与原版不同，落点可能略有偏差。
- 开关关着时用原版的值和代码，不碰游戏。
- 新增开发用探针 `Probe` / `ProbeSeconds`（关，1 秒）：每个窗口写一行帧时间、主线程与渲染线程 CPU、敌人刷出、唤醒、导航开关、阴影重绘、逐敌人更新与射手弹体的次数和耗时，写到 `BepInEx/PerformanceLogs/ForgePerformance_*.log`；同时装上全部开关的钩子，可在一局里来回切换对比。探针还给游戏状态更新、敌人更新管理器各组的逐敌人更新、敌人移动、触手、FinalIK、玩家与敌人碰撞、相机、阴影图集、帧率限制器、音频等脚本更新计时，把主线程的脚本阶段拆开；每行带 `utc`，可与 ForgeDevelopment 的每秒行对齐。控制台可用 `Value` 改避让类型和寻路迭代数。每行带 `foreground_frames`（游戏窗口有焦点的帧数），每个标记带 `runInBackground`、垂直同步、目标帧率与当前焦点；控制台 `Limits` 读这些值，`Foreground` / `Background` 请求切换焦点（Windows 可能拒绝，结果写在返回里）。用 `tools/analyze_probe.py` 汇总（标签写成 `<项>-A-<n>` / `<项>-B-<n>` 时按段中位数做 A/B 对比，可并入 ForgeDevelopment 的阶段耗时），`tools/crowd-probe.test`、`tools/crowd-ab.test` 与前后台对照 `tools/crowd-focus.test` 是驱动脚本。
- 每个补丁写入前都先核对目标函数开头的字节（`Crowd/EntryAdmission.cs`），不只是探针和敌群补丁：减负补丁、风补丁同样先查。规则按游戏文件里的原始字节判断，所以入口已被 Harmony 改过（例如 ForgeCore 先装了 `EnemyAgent.Setup`）也能判；规则补上了转发桩的尾跳转。被拒的目标记一行 `type=perf_native_hook`，带诊断码 `native-hook-prologue-refused`。减负补丁和风补丁各自整组生效：有一个入口被拒，这一组全部不装并报错。
- `TentacleLimitErrorFold`：每关第一条触手上限报错当场写一行（`state=first`），关卡结束再写总数（`state=level-total`）。进程被杀或崩溃时也留有记录。
- 探针不再给 `EnemyAllocator.SpawnEnemy` 计时（那是 ForgeEngine 的方法）；刷怪看 `EnemySpawned` 和 `OwnSetupPrefix`。
- 敌人 `Setup` 时不再无条件查 `EnemyTransformSync` 组件，只在 `ClientHiddenSyncThrottle` 或探针开着时查。不影响观感和玩法。
- 探针清理旧日志失败时记一行（`perf.probe-log-prune-failed`），不再静默跳过。
- `SummaryLog` 打开时，每次游戏状态变化写一行 `type=performance_state`（关卡、UTC 和性能计数器时间），帧采集工具录下的数据可以按关卡切开；`tools/analyze_play.py` 读它和 PresentMon 的表，按关卡给出平均帧率、1% low 和帧时间分位。
- 探针加原生部分（`tools/NativeProbe`，放在插件旁才加载，`Probe` 关着时不碰）：主线程与渲染线程的栈采样、按相机阶段切分的 GPU 时间线、着色器创建计时、33 ms 以上的帧逐条记录；`tools/analyze_samples.py` 把采样归到函数和各个模组，`tools/analyze_gpu_timeline.py` 汇总 GPU 分段。
- 探针读回 `Lag` 多给三组数：手动推进动画的次数、动画时间前进的次数、骨骼姿势变化的次数（以及只算画面内敌人的同样三项）；新增 `Animators`：存活数、被降频数、最近的被降频敌人距离、动画被关而不在降频名单里的数量。
- 远处动画降频改挂 `ES_PathMove.Exit`，不再和 ForgeEngine 同挂 `EnemyLocomotion.ChangeState`。
- 装饰阴影一次要移除的项超过 4096 个时，多出的留在原处并记一行 `perf.decoration-removal-limit`，不再抛异常。

## 1.0.1

- 启动时游戏文件的哈希跨启动缓存在 `BepInEx/cache/ForgePerformance/file-sha256.txt`，文件变了才重算。
- 装饰剔除不再每帧逐个查找。

## 1.0.0

- `FastCorpseHide` 隐藏尸体后同时关掉尸体碰撞体，复用或退图时恢复。

- 新增视觉与清理开关：阴影距离、贴图等级、软粒子、关卡泄漏清理、尸体溶解粒子清理、碎屑静止、泡沫球音源回收、日志堆栈裁剪、界面镜像、低画质喷吐体、尸体提前回收；默认全开，各自可关。
- `LeakCleanup` 扩充：终端销毁时取消建关完成回调、关卡清理时清空任务提示队列、菜单滚动窗口重置表头时删除重复表头项。
- 新增 `MeleePacketReuse`（默认关）、`IrfWorldCameraOnly` 与 `IrfOffscreenSkip`（默认开）。
- 新增 `ClosedDoorDepthLimit`（默认开，有玩法代价：门缝看不到远处敌人）与 `ClosedDoorDepth`（默认 2）：任何关着的门（弱门、安全门、Apex 门、闸门）后面只画最先几个房间，紧挨门的房间照常画，别的路线看得到的房间与受损或摧毁的门照原版。
- 新增 `RagdollNodeRefresh`（默认开）：布娃娃停下时让游戏重新查尸体所在的剔除房间，修尸体被打飞穿过房间边界后消失。
- 新增 `TextureStreamingOff`（默认开，关内没有流送贴图时才关流送管理器）与六个渲染开关：接触阴影、屏幕空间反射、次表面散射、环境光遮蔽、TAA 换 FXAA、每帧阴影更新数（默认 2）。
- 修复 `VeryLowFog`：设置值改走 `CS_Value.Value`，雾画质确实降到 VeryLow。修复 `VisorLiquidOff`：按原生绑定名替换两张液体纹理，不再每次进关拒绝。
- 默认开关以帧率最高为准：所有不改玩法的削减默认开，含 `IrfWorldCameraOnly`、`IrfOffscreenSkip`。`CorpseRecycle`（尸体更早不挡子弹）和 `ClosedDoorDepthLimit`（门缝看不到远处敌人）有玩法代价，由所有者决定默认开，各自可关。`MeleePacketReuse` 默认关。
- 数值默认：所有者 2026-11-09 指定 `CorpseLifetime=0.01` 秒、`CorpseRecycleSeconds=1` 秒、`ParticleFactor=0.01`、`ParticleHz=1`、`WindLimit=1`、`DecalLimit=1`、`DecalLifetime=0.01` 秒、`CullDistance=10` 米；`RagdollLimit=4`、`ShellLimit=6`、`LightLimit=12`、`ClosedDoorDepth=2` 是挑选值；阴影两项不变。全部未在游戏里实测。
- 修复 `MeleePacketReuse` 混装报错：不再从网络复制器里删包位，改为把新包的位置指向共用包，网络包编号与原版一致，与未装本项、未装本模组或装了别的版本的玩家同局不再错位。
- 修复 `GlueSoundRecycle`：防重复回收只在同一帧内生效，后来的泡沫球不会因为对象地址被复用而漏掉音源回收。
- 新增 `DroppedMagazineCleanup`（默认开）与 `DroppedMagazineSeconds`（默认 3 秒）：换弹掉到地上的弹夹落地后到时间就还回枪自己的对象池，从地上消失；别的玩家掉的弹夹在本机同样处理。换弹、弹药、声音不变，不发任何联机数据。
- 新增 `SpentFogRepellerCleanup`（默认开）：扔出的驱雾器在本机这份驱雾结束（原版状态变为停用）后，本机不再画它的模型；物品、碰撞、联机状态都保持原版，不发任何数据，各台机器按自己的设置。还在驱雾的不动，搬运的大型驱雾器不动。用完的发光棒原版已在熄灭后移除，不另设开关。
- `CombatDecals` 去掉 `Decal.Shown` 钩子：游戏只在回池时隐藏贴花，回池入口已处理，这个钩子每次回池都白扫一遍全部登记贴花。

- 修复 `LowQualitySpitter`：建关时已生成的喷吐体在进关后补扫处理，原来只处理进关后才出现的。
- 修复 `CorpseShadows`：复用的敌人重新出生时恢复被关掉的投影，不再等到退图。
- `LeakCleanup` 的实例化渲染缓冲只在退图拆除时销毁，关内销毁的特征（含敌人身上的）不碰共享缓冲。
- `CombatDecals` 只对确认的战斗贴花检查层级，结果按特效对象缓存到退图；`DebrisFreeze` 的随机延迟改用本地随机数，不推进 Unity 全局随机序列。
- 退图不再把被 `RagdollCap` 停掉的布娃娃重新启用（离开关卡本就销毁它们，一次启用大量布娃娃会有物理尖峰）；退图异常只记日志，不再从状态事件里抛出，不打断其他订阅者。
