# ForgeDevelopment

> 实施顺序与原版内容完整覆盖见[唯一开发计划](../../Docs/forge-contract/FORGE-FRAMEWORK.md) §4，分步验收见 §7；本文件仅说明实现与用法。

可选的作者诊断包：观察生成、空间、性能、异常与运行证据，关联到作者对象并输出报告。新增原版读数的入口也在本包，见下文“新增原版读数”。

本包的记录器（Native/Recorder）只为作品验证比对记证据：方法追踪、日志与状态，写入作者会话的记录目录；玩家回放与回放查看归 GTFOReplay。

**普通玩家默认不依赖这个包。** 它不调度正常玩法，也不把"没有报错"解释为地图正确。需要玩家自己启用诊断回传才能用起来的能力不算完成——诊断是作者型用户的第二层入口，不是玩家必经路径。

交付要求、作者工具范围与验收见唯一开发计划 §6 U-DEV-MOD 和 §7（链接见[仓库 README](../README.md)），最近一次验证记录见 [VALIDATION.md](VALIDATION.md)，发行说明见 [CHANGELOG.md](CHANGELOG.md)。性能日志的采集器在本包内，并以源码链接的方式被 ForgePerformance 编入；一般游玩用不到；InfiniTweaks 只放 Quality of Life 功能。

## 工程与启动门槛

只有一个工程：

| 工程 | 内容 |
| --- | --- |
| `Native/ForgeDevelopment.Native.csproj` | 真实 BepInEx 插件 `ForgeDevelopment.Native.dll`，包含诊断 Hook、快照边界和全量记录器 |

本包没有托管 SDK 程序集；包版本只取 `Native/Host/Plugin.cs` 的 `PluginVersion`。

插件身份是 `[BepInPlugin("NAinfini.ForgeDevelopment", "Forge Development", "1.0.0")]`，硬依赖 `NAinfini.ForgeCore` 与 GTFO-API，不依赖 ForgeEngine：原版、MTFO 与 Forge 关卡都能用。原生工程只引用 `ForgeCore.csproj`；构建只需要 `GTFOBepInExPath`。Forge 地图专属的作者读数（计划重读、运行时变量、生成进度、房间解析、模型射击、机器人位）在 ForgeEngine 的 `ForgeEngine.Authoring.AuthorReads`，本插件运行时按类型名找它（`Native/Host/EngineLookup.cs`），没装 ForgeEngine 时答 `forge-engine-not-installed`，ForgeEngine 不在 Authoring 模式时答 `forge-engine-not-authoring`。

**启动门槛：** 本插件自己的 `[General] Enabled`（默认 `true`）决定是否启动；`false` 时记录一行 inactive 日志后返回，不绑定其他配置、不打 Hook、不加组件。会话里多于一名真人玩家时同样不启动记录器与原生追踪（已在运行则停止），只写一行 info；单人带机器人不算多人。启动顺序是绑定配置 → 解析与预检固定目标 → `RuntimeDiagnostics.Initialize` → `RecRuntime.Start` → 按目标安装本程序集的观察补丁 → 作者、采集、实验与可选性能组件。原生安全拒绝只停该目标，记录 `development.hook-refused`（目标、原因与实际补丁 owner）和 `session/trace_refused profile=fixed-hooks`，其余采集继续；`development.hooks-ready` 给出安装与拒绝数，部分覆盖不能当作完整采集。相同目标的多个补丁合并安装，安装前再次执行同一原生门禁；没有原始来源记录的已改写入口仍拒绝，不用 owner 名称、磁盘原字节或 trampoline 猜测准入。目标声明错误、实际补丁安装异常或组件启动失败仍按已获取阶段逆序清理；原始异常和清理失败保留，同一实例不重试。记录器内部各阶段自己降级：会话开不出来才整体跳过，日志/跟踪/进程清单任一段失败只记 `stage_failed` 并继续其余通道。完整组件启动后查看 `Forge Development 1.0.0 loaded`，并同时核对钩子覆盖汇总。

capture、experiments 与记录器在同一个程序集里，全部是编译期直接引用：`RecRuntime` 在层级边界与关卡清理时直接调 `CaptureRegistry.SnapshotAll`，热键从 `RecHotkeys` 直接调 `CaptureRegistry.SnapshotAll("hotkey")`（F7）与 `ExperimentPanel.Toggle()`（F5），`Plugin.Load` 直接调 `CaptureRegistry.EnsureStarted`、`ExperimentPanel.Load()`，没有按签名反射查找的第二条路径。Forge 内核自己的诊断由 ForgeEngine 写进 `BepInEx/forge-logs/*.jsonl`，记录器不再另开 `forge` 通道。

本插件不向 Forge 内核登记模块，也不提交玩法请求。报告 metadata 的 `forgeVersion` 是已装 ForgeEngine 的版本（没装写 `absent`），另写 `developmentVersion`；问题来源名为 `ForgeDevelopment`。

## 安装与配置

开发包 `NAinfini-ForgeDevelopment`（发行集里受众为作者）只放 `ForgeDevelopment.Native.dll` 与它的 probes，装在 ForgeCore 之上；普通玩家包不包含它。

### 完整包自检与配置更新

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

已有同名配置不同于本版时，普通 `--apply` 在写入前停止。检查预览后，明确要采用发行默认值才使用 `--apply --replace-existing`；原字节先保存在输出所列的 `backupDirectory`，无关自定义文件保持不变。安装收据识别出本版已移除的旧 JSON 时，必须另加 `--prune-retired` 才会把**仍与旧收据摘要一致**的文件移入备份；已经被用户修改的旧文件始终要求人工处理。首次升级没有旧安装收据时，工具无法判定额外 JSON 是旧发行资源还是用户文件，按 `unmanagedFiles` 明确列出并保留，不猜测删除。

安装或更新后重新启动游戏。普通 trace 配置仅在插件启动时载入；F5 的实验列表可用 `reload files` 重读命令，但不能借此重装 trace hooks。

### V1 检查点入口与证据边界


发布自检证明资源完整、构建时记录入口检查通过，不证明检查点在 GTFO 中执行成功。玩法前提未实现的条目在门禁收据中保留 `gameplayPrerequisiteBlocks`。游戏内提交记录请求、截图文件和真正落盘的原始 JSONL 也要区分：核对会话、点身份、前后阶段、`index.json` 的丢弃数及预算状态，不能把按钮反馈或无异常当作游戏验收。

### 指令台与测试脚本

`tools/drive_cli.py` 是指令台：对 `run-drive-session.py start` 起好的常驻会话逐条下指令，或用 `run <脚本>.test` 整份执行测试脚本。每次执行的指令记录、日志和结果写进 `<work-dir>/console/<开始时间>-<脚本名>/`，其中 `run.json`（`forge.console-run` 1.0.0）可导入网站的结果页。指令表、脚本语法、会话记录与重放见 [DEVELOPERS.md](DEVELOPERS.md)，示例脚本在 `scripts/`。游戏内没有测试面板，也没有 `[PackTest]` 配置；断言只检查脚本里写出的读数，配置回读不算行为证据。


### 诊断配置

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

## E1 原生人口点数记录

退出游戏后把 `[Recorder] PopulationBudgetEnabled = true`，保留记录器与状态采集开启，再重新启动并进入关卡。此项默认关闭，必须在区域生成前启用；它安装单独的精确签名观察器，不把带 ref/out 的方法交给通用 trace。F6 只加书签；F7 请求全量状态与人口预算快照，F8 留画面。

记录写入同一会话 JSONL 的 `population_budget` 通道，schema 为 `forge.population-budget.v1`。先查 `hook` / `installation` 的实际安装结果，再按会话、worldEpoch、observationEpoch、callId、parentCallId 与 groupLife 对齐调用；缺原生准入、前后半段、原调用被跳过、异常、身份不明、截断或丢记录都不能算通过。已有正常 trace 档不能替代这些显式记录。

区域的 ref population 前后值、单组选择的 out cost、实际生成列表与全局各维度占用分别记录；配置预算、实际消耗和敌人数不混算。F7 的 `allowedTotalCost` 与 `dimensionEnemyCostMap[].cost` 用于实际占用及全灭后的归零核对，缺字段不补零。直接／强制生成 API 未提供选择成本，原生 placement 引用的跨回调生命周期尚未证明时保留未证；不得由同一指针、数量或时间邻近推算。其他补丁可能改变观察点数值，需一并保留已加载程序集与补丁清单。

该记录器的离线编译和回归仅证明可记录的字段与拒绝边界，不证明这次游戏已完整采到三本账。覆盖检查中的明确记录缺口会使 recording readiness 失败，生成结果可重现不等于记录就绪。

## 原版敌人能力参考清点

等游戏 DataBlock 已加载后按 F3（可改 `[Authoring] ExportEnemyAbilityKey`），每次将独立原件写至 `BepInEx/ForgeReports/enemy-abilities/<gameRevision>/<sessionId>-<captureSequence:D6>.forge`，不覆盖上次观察，日志给出路径与条数。`forge-enemy-ability-reference` / `schemaVersion=1` 的原有 `enemies` 结构不变：已加载块可能被其他模组改变，先核对游戏修订号与模组组合，再按 `enemyId`、`index` 对比 `value`；现场 `game-observed` 不等于原版出处。定义读取失败时不输出伪造空清单。

追加的 `prefabObservations[]` 按 `abilityPrefab` 关联定义，仅查询已经加载的资源，不加载、不实例化。`components[]` 记录真实 `EnemyAbility` 派生组件的 `instanceId`、`nativeType`、`abilityType`、`abilityIndex`，`fields[]` 按原生字段元数据读取生成访问器，保留 `field`、`valueType`、`status`、`value` 与读取错误。只采集支持的数字、布尔、枚举、向量和有界浮点数组，不遍历任意对象图；`observed` 只说明该支持字段成功读取，不表示整个预制件内部全部覆盖。资源未加载、字段缺失、非有限数、类型不支持与预算截断均保留明确状态。

`liveCooldownObservations[]` 读取活敌人的 `m_cooldowns` / `m_lastTriggerTimes`，用 `AbilityComps[abilityType].Comps[abilityIndex]` 给出实际组件，并附 `componentObservation` 的实例字段。此处 `abilityIndex` 是原生分类表的槽位，不能直接当作 `enemies[].abilities[].index`。`sample` 保留 `unityTime`、`realtimeSinceStartup`、`simulationTick`、`lastTriggerTimeRaw` 和 Runtime 实际解析的 `entityLife`（`Id@WorldEpoch.LifeEpoch`）；同时保留 `enemy`、敌人块 `enemyId`、`enemyInstanceId`、组件身份以及 `source.generationId/worldEpoch/captureId`。

在同一已解析生命期、同一能力槽与组件上重复 F3，`interval` 给出前一个已成功写盘的 `previousCaptureId`、两种时钟的经过秒数、模拟 tick 差与原生触发计时差。未解析生命期、生命变化、时钟倒退或缺上次样本不会补 0 或推断间隔。采样差只证明两次读数之间经过的时间，不能证明冷却何时结束或单位是什么；仍须结合真实 `UseAbility` / `IsAbilityReady` 调用 trace 和实际出手效果判断，F3 自己不调用这些玩法方法。`fieldUnits.Cooldown`、`pending` 和 `interval.unitStatus` 保持待游戏核验，不自动修改离线参考表。

两段补充采集均有数量与 100 ms 协作预算；`observationCoverage.prefabs/liveCooldowns` 记录发现量、已检查量、实际耗时、失败原因和省略错误数，`limits` 给出上限。单个原生调用无法抢占，超时停止后续读取；`partial`、`not-loaded`、`unsupported-type`、缺实例或缺字段都不算该项通过。

## 新增原版读数

`catalog/authoring/vanilla-values.json` 里状态为 `needs-ingame` 的值靠一次手动游戏会话补读，两步都在终端里运行，需要主检出里的本机输入（目录、`Source/`）：

```powershell
# 1. 游戏关闭时写计划和启动时间窗；--additional 另读能力预制件与种群键
python -B Mods/ForgeDevelopment/tools/capture_materials.py lease --bepinex-root <profile>\BepInEx --work-dir artifacts/work/vanilla-readings/<名字>
# 2. 自己启动游戏、单人进关、退出游戏后，按进过的关卡归档读数并严格回填，同时恢复租约
python -B Mods/ForgeDevelopment/tools/capture_materials.py collect --bepinex-root <profile>\BepInEx --work-dir artifacts/work/vanilla-readings/<名字> --level R1A1
```

前提：开发包已安装，`[Recorder] RecordKiB = 1024`（命令不改游戏配置）；计划存在时记录器随插件启动。读数写在 `materials` 通道，`collect` 只收这次租约（`captureRunId`）且落在启动时间窗内的记录，存到 `Source/game-exports/levels/<gameRevision>/<关卡>/vanilla-readings.<sha>.jsonl`，再按 `capture_vanilla_values.py` 的严格规则回填；回填结果在 `<work-dir>/vanilla-backfill.json`。

原版读数在真实初始化点记录。此构建把部位初始化内联到 `Dam_EnemyDamageBase.Setup`；采集在它返回后读取 `DamageLimbs`，并验证已初始化的接收器、所属敌人及数组中的部位身份。模板的零值不回填。每条读数携带补丁清单、启动及读取时不存在 Forge 内容的证明、活动计划数、实验执行数与原版数据覆写检查；任何活动计划、配置内容、实验或未知补丁都使读数拒绝。人口选择的两个 `out` 参数只在原生调用完成后读取，定点修复当前 IL2CPP 桥提前读取输出地址的问题，原生返回和写回保留。游戏文件指纹与目录使用同一 `GameAssembly:sha256:` 格式。

## 全量记录器

另有一个逐条记录整个会话的记录器，写入 `BepInEx/ForgeReports/rec-<时间>-<role>-<slot>/`。它默认关闭（`Enabled = false`），开启后按通道落盘、不依赖任何一份具体的验证点清单；写入线程每次清空队列都把已写的行交给系统，外部读者（驱动、`rec-query.py`）随即读得到，不必等后续记录填满缓冲。会话里出现第二名玩家时记录器与原生追踪都不启动；已经在录的会在下一次检查时停止，只写一行 info 说明。跟踪默认也关闭（`TraceEnabled = false`），需要时打开。记录器不抄 ForgeEngine 内核的逐节点行为追踪；Forge 关卡的触发、入口与步骤代码看 ForgeEngine 自己的 forge-logs。`probes/trace/net.forge` 默认不启用，网络追踪要手动开启。打开的追踪在游戏数据就绪后每帧装一个钩子（每个十几到几十毫秒，全套两千多个要两分钟以上），关卡生成（游戏状态 Generating）期间暂停、之后接着装，`development.trace-ready` 的 `generationHeldFrames` 是暂停的帧数；要追踪生成过程，等这一行出现后再进关。手测只开记录器、不开 `TraceEnabled`。

配置键在 `[Recorder]` 节：

```ini
[Recorder]
Enabled = false
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

每个会话目录里是若干 `rec-<session>-<序号>.jsonl` 分段、截图目录 `shots/` 和一份 `index.json`。正在写的分段是明文，可按字节偏移实时读；分段一关闭就压成 `.jsonl.gz` 并删掉明文（读取端正占用时稍后再删）。每个分段第一行是分段头 `{"v":"forge.rec","segment":{...}}`，放这一段记录共有的上下文字段（session、role、slot、level、generationSessionId、generationId、worldEpoch）；每条记录只写与分段头不同的字段，缺的写进 `$absent`。关卡或世界变了就开新分段。完整记录 = 分段头字段（去掉 `$absent`）叠上本行字段，`tools/rec_segments.py` 是唯一读法，所有读取工具都用它。`BudgetGiB` 按未压缩字节计。`index.json` 记录通道计数、分段列表、丢弃数、是否触到总量上限以及装了哪些补丁；`patches` 字段就是跟踪器的启动报告，所以一个会话能自己说明它当时在跟踪什么。同一份启动报告在 trace 配置加载时就作为文档 `trace-patches.forge` 写入会话目录（数千条补丁与拒绝远超单条记录上限，不进 JSONL），会话通道里的 `patches` 记录只指向该文档。

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

## 怎么抓一个新的点

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

## 现行输入与报告合同

项目清单的根对象必须包含 `format`、`projectId`、`experiment`、`requiredPlugins`、`sources`、`objectReferences`，不接受额外或重复字段。`format` 固定为 `gtfo-forge-project`；**不接受 `schemaVersion` 或 `expectedObjects`，不做旧格式 fallback**。

`experiment` 必须包含 `packageVersion`、`authoringSha256`、`dependencies`，dependencies 是字符串可以为空。`requiredPlugins` 条目是 guid 与 version（精确版本，按相等判定，I-RELEASE）；`sources` 是 path、sha256、kind，kind 只接受 DataBlock / ModConfig；hash 是小写 SHA-256。

对象声明只支持 zone 与 room。Zone locator 明确 layoutId、dimension、layer、localIndex；room locator 是 unique-geomorph-in-zone，明确 zoneAuthorId、该区域的原生 dimension / layer / localIndex（必须与那条 zone 引用逐值相等）、room 的 id 与 revision 和完整的 `Assets/` 来源身份。房间解析只在那一个区域内进行：区域没有就是 `zone_unresolved`，区域内没有这个房间就是 `no_candidate`，区域内有两个同源房间就是 `multiple_candidates`（候选照常列出），解析器没装或 prefab 取不回来仍是 `creation_context_unverified`。**显示名称、Hierarchy 路径、预览模型都不代替创建证据。**

`ProjectManifest` 配置是本地文件选择器，相对路径以 BepInEx 为基准；它与清单内 `sources` 的安全规则不同——sources 必须是 BepInEx 内的规范相对路径，用正斜杠，禁止绝对路径、盘符、空段、点段、上级目录和重解析点逃逸。

清单上限 4 MiB，严格 UTF-8 与 JSON、深度 32。来源扫描上限：256 目录、4096 条目、256 文件、单文件 8 MiB、总计 64 MiB；报告上限 16 MiB。`sourceCoverageStatus=complete` **只表示采样覆盖结束**，不表示 `sourceVerification=matched`，更不证明内存或游戏行为一致。

报告的 format 是 `gtfo-forge-diagnostics-report`，`objectReferences` 独立记录 worldEpoch、simulationTick、scanStatus、sourceVerification、groups 与 overflow。遍历是分帧观察，**不是原子世界快照**；完整的局部遍历也不证明目标、导航或远征可以完成。对象存在不等于任务脚本正确，`FactoryDone` 只表示观察到结构生成完成；现有诊断没有宣称修复 R7D2 随机异常或连续切关的 CullingCluster 异常。

## 检查会话与报告快照

`ProjectChecks.Load` 返回该报告实际附着的 scan，严格清单校验和来源采样保留。`ProjectInspectionSession` 绑定固定的 report、scan 与 worldEpoch，分别处理重复开始、扫描前取消、旧世界的迟到完成、无世界上下文和部分扫描。worldEpoch 是本包自己的计数（`RecTime`，GTFO-API 的 `LevelAPI.OnBuildStart` 与 `OnLevelCleanup` 各推进一次），与 ForgeEngine 的世界计数无关；报告的 simulationTick 不再有来源，写 `null`。`RuntimeDiagnostics` 在生成、切关、退出时清理扫描与来源请求；跨报告或跨世界的旧 JobTrace 不写入新报告。会话不在 `LG_Factory.Setup` 前缀里建：前缀里读到的可能还是上一个世界的 epoch，据此建的会话对不上本次生成的任务与检查步骤。`GenerationWorldBinding` 因此在生成开始后的第一次观察（第一个任务或第一帧）按当时的 epoch 建会话并加载 scan，任务追踪与生成后检查都与这个 epoch 比较；epoch 之后再变则检查以 `world_epoch_changed` 结束，任务不再写入。客户端若在第一次观察之后才收到主机 epoch，则会话绑在旧 epoch 并同样结束，这一情形没有重新绑定。每次 `Build()` 调用的追踪记录在 JSON 报告里为 `generation_job` 事件（`jobType` 字段是类名；只在返回完成或该任务首次调用时写），簿记耗时累计在 `generationTraceOverheadMs`。`WorldInspection` 的原生实例 ID 与显示路径分开；活动布局只取实际主维度的已生成层。

**Geomorph 的 prefab 名称不是作者来源的证明。** 缺少创建上下文时保留 `creation_context_unverified`，不伪造房间匹配。

报告在 Enqueue 时就捕获不可变的托管证据；之后的新世界、扫描完成、来源验证或取消都不能改写已经排队的旧快照。序列化、字节预算裁剪和原子写盘在 writer 线程，后台不读取 Unity 或 IL2CPP 对象。队列保留 8 个 pending 路径上限。`ShutdownSequence` 继续执行所有清理阶段并返回只读、有序的失败收据。

## 制作工具

使用 Python 3.10+ 标准库，不下载依赖也不执行外部代码：

```powershell
python Mods/ForgeDevelopment/tools/analyze_performance.py session.log --output performance.md
python Mods/ForgeDevelopment/tools/rec-query.py "$env:GTFO_BEPINEX_PATH/ForgeReports/rec-<会话>" --list-channels
python Mods/ForgeDevelopment/tools/rec-query.py <会话> --channel tracer --method OnOpen --json
python Mods/ForgeDevelopment/tools/rec-merge.py host=<会话> client=<会话> --same-run
```

离线性能分析按远征分段、计算帧加权 FPS、排出慢分钟窗口与长帧，报告扫描开销、被检测的模组路径、内存趋势和缺失的通道。它不会把窗口分位数平均成整场分位数、不会仅凭增长断定内存泄漏、不会仅凭一个异常归咎某个模组。

## 复跑

```powershell
python Mods/ForgeDevelopment/tools/verify-diagnostics.py --bepinex "$env:GTFO_BEPINEX_PATH"
```

脚本依次构建宿主、原生插件与各测试工程，运行插件启动、报告、项目规则、检查、快照、关机、场景清单、遥测、采样、原生布局与 Python 套件。默认新建系统临时目录，也可以指定一个尚不存在的 `--output`；源码变化或任何失败都返回非零。完整的套件清单与结果见 [VALIDATION.md](VALIDATION.md)。

最后是负例门槛：把包复制到证据目录，分别注入五个已知破坏（快照改为导出时捕获、旧 session 接受新世界的迟到观察、去掉错误条数上限、放开 trace 聚合上限、首个清理失败后停止后续清理），每个都必须编译通过并由对应套件判失败；锚点缺失或多于一处、变异后仍全绿都算验证失败。工作树与游戏 profile 不被修改。

## 边界

当前 DLL 不应作为玩家发行物。没有 GTFO 加载、多人、原生 Hook 安全或采集开销的验收；"不安装 / 安装但未启用 / 启用"三种实际加载模式也未在游戏里核验。InfiniTweaks 的 QoL 行为不属于本模块。禁止无限重试、重置指定 prefab 种子、静默替换房间、删除任务对象、任意挂接 CourseNode、吞掉异常或调低游戏日志级别来伪装成功。
