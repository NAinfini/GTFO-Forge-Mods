# probes/trace — 原生方法跟踪配置

这些文件是 `RecTracer` 的配置，运行时从 `BepInEx/ForgeDevelopment/trace/*.json` 读取。加一个点
= 加一行配置，不需要改代码、不需要重新发版。字段含义见仓库 `Native/Recorder/RecTracer.cs` 的解析器。

## 约定

- **类型名以本机 interop 程序集为准**。全局命名空间里的类型（`GameStateManager`、`LG_PowerGeneratorCluster`、
  `ItemEquippable`、`Interact_*`、`GuiManager`、`NavMarker*`、`Dam_*`、`SurvivalWave`、`Mastermind`、
  `CellSound`、`M_*`）直接写短名，不要凭直觉加 `LevelGeneration.` / `Gear.` 前缀；带命名空间的类型写全名
  （`LevelGeneration.LG_SecurityDoor`、`Enemies.EnemyAgent`、`SNetwork.SNet_Packet`、`ChainedPuzzles.CP_*`）。
  写错的类型名与写错的方法名都会被 `tests/Capture` 的 Cecil 检查列出来。
- **泛型类型定义不写进 profile**。运行期 `RecTracerRuntime.FindTypes` 会跳过泛型类型定义与接口，所以
  `SNetwork.SNet_Packet\`1`、`SNetwork.SNet_StateReplicator\`1` 这类名字永远装不上；非泛型的基类
  （`SNetwork.SNet_Packet`、`SNetwork.SNet_ReplicationManager`）才是能装的目标。
- **默认开启的 profile 要能在正常一局里跑得动**。`Update` / `LateUpdate` / `FixedUpdate` / `OnGUI` 被跟踪器
  内置拒绝表挡住，不要写进方法列表；高频方法用 `"record": "count"` 加 `maxPerSecond`。
- **泛型方法与带指针/泛型参数的方法、普通 by-ref 参数方法会被拒绝**，拒绝结果写进 `session` 通道的 skip 列表，不是静默丢弃。
  用通配符匹配到这类重载是正常的；把**具体某个这样的方法名**写进 `methods` 才是配置错误。
- `record` 只能是 `count` / `args` / `args+result` / `changes`。
- `instance` 是 `RecReflect` 读的字段路径，可以穿私有字段（例如 `m_sync.m_stateReplicator.State`）。
  路径读不到就写 null，不会抛异常。
- `maxPerSecond` 超出后按 `onOverflow`（`count`）处理，并每 5 秒写一条 `tracer/overflow` 记录。

## profile 一览

| profile | 默认 | 覆盖 |
| --- | --- | --- |
| `level` | 开 | GameStateManager / RundownManager / WardenObjectiveManager / WorldEventManager / CheckpointManager / Dimension / ElevatorRide / LG_Factory / 链式谜题（含扫描路线、半径、来源区域）/ 关卡交互管理器 / 区域扩展与闸口生成 job / 反应堆 / 电梯井警报 / Bulkhead 钥匙与控制器登记 |
| `door-terminal` | 开 | 安全门、安全门锁、弱门与弱门破坏、弱锁、门刀剔除器、门按钮、终端与终端管理器、命令解释器状态、HSU、隔断门控制器、资源容器、交互组件 |
| `objects` | 开 | 发电机与发电机集群、拾取物与关卡物品、资源容器、`Interact_*` 全族、玩家交互、部署标记 `PlayerBackpack`、刷怪卵荚音效 |
| `player` | 开 | PlayerAgent、伤害基类与部位、背包 / 弹药 / 物品栏、locomotion 状态切换、PlayerSync、聊天与语音、复活、AgentModifierManager、`Alive` setter、游戏事件 `GameEventManager.PostEvent`（哪台机器发出） |
| `weapon` | 开 | BulletWeapon / Shotgun / Weapon / ItemEquippable / 近战 / 哨戒炮 / 绊雷 / 胶枪 / 生物追踪器 / 投掷物 / ArchetypeDataBlock / 标记同步 ToolSyncManager / 弹体与胶体 ProjectileManager / 装备生成 ItemReplicationManager / 连发与蓄力原型 `Gear.BWA_*`、`BulletWeaponArchetype`、`MWS_ChargeUp` / 哨戒炮开火 `SentryGunInstance_Firing_Bullets` / 装备池 `GearManager` |
| `enemy` | 开 | EnemyAgent / EnemyAI / EnemyBehaviour / ES_* 状态进出与攻防窗口（按短名匹配，覆盖 `Enemies`、全局与 `ES_Legacy` 三处命名空间的状态类，记录 `m_enemyAgent`；含唤醒 `DoWakeup`） / 侦察检测进入与伸触须动画、侦察尖叫状态 / 攻击起手包 / 受击反应 / 移动状态机 / 能力调用 / 飞行空中图 / SquidBoss 阶段 / 敌人 prefab 构建 / 部位伤害与胶量 / 分配器 / 敌群 / 检测 / EnemySync |
| `damage` | 开 | `Dam_EnemyDamageBase` 与 `Dam_PlayerDamageBase` 的伤害入口（含触手/陷阱/窒息/坠落等 Receive*）、血量写入 SendSetHealth/ReceiveSetHealth、`Dam_EnemyDamageLimb`、`SurvivalWave`、`Mastermind`、静态敌人伤害 |
| `gui` | 开 | GuiManager / NavMarkerLayer / NavMarker / 标记闪烁 `CoroutineManager.BlinkIn` / PlaceNavMarkerOnGO / PlayerGuiLayer / PUI_* / CM_* / WardenIntel |
| `environment` | 开 | EnvironmentStateManager / LG_Light 全族（含类别与颜色）/ 雾与雾球及其分配 / 效果体积登记 / 除污 / CellSound（默认只计数）/ 动画序列器 |
| `net` | 开 | 包与包缓冲收发、状态复制器、复制管理器、SNet_SyncManager、SNet_Capture、SNet_Player、`M_*` 序列化 |
| `everything` | **关** | 按命名空间通配（不含 `*` 兜底条目），只在需要地毯式确认时用 `SetProfileEnabled("everything", true)` 打开 |

## 加一个新点

```json
{ "type": "LevelGeneration.LG_SomeType", "methods": ["SomeMethod"], "record": "args+result",
  "instance": ["m_field"], "maxPerSecond": 20, "onOverflow": "count" }
```

放进对应玩法域的 profile 即可。若类型/方法名写错，`tests/Capture` 会报出具体是哪条。

## V1 精确取证补充

- `door-terminal` 显式记录终端解释器的 EvaluateInput / ReceiveCommand / AddOutput / ReadLog，用于把输入、输出和实际读日志与同一终端对应。
- `level` 显式记录 WorldEventTrigger_Sync 的 SetupWorldEventTrigger / OnInternalStateChange，配合 WorldEventManager 的真实派发，按每个触发器和各端记录核对碰撞/注视/交互/扫描结果。
- `damage` 增加 SurvivalWave.SpawnGroup；`weapon` 增加 ItemEquippable.PickUp；`player` 增加 PlayerLocomotion.AddExternalPushForce。它们只观察已有原生调用。
- `enemy` 的 EnemyPopulationManager 是 opt-in entry `phases: "before+after"`，其他 entry 保持默认返回时采样。五个原生成员表/值与真实候选、实际返回在同一 callId 两侧保存；不能把 postfix 值当作入口值。
- 配对观察要求同 session / patch / callId / signature，before=entered、after=returned，双侧 observationComplete 为真，且 observationScope 存在、一致并对应本次安装；同 callId 另有 exception/threw 也否决（别的 postfix 可能随后抛错）；缺阶段、opaque、预算丢弃、异常、skip、溢出与截断均未证。完整只限定记录标出的观察范围，不把包装层前后提升为原生前后。GetWeightedRandomType 候选的 m_weight 记录原值，不自动断言已归一化概率；m_currentBaseWeights 不因名字推定为最终抽样权重。
- EnemyCostManager 只记录实际 AddCost / RemoveCost / GetEnemyCost / GetTotalCost / IsAllowed 调用；不同维度、类型与数值重载分开，不主动调用选择或修改函数。
- 记录阶段、方法存在和字段非空都不是玩法通过。

## 默认启用配置的唯一归属

默认配置的原生目标是 `catalog/native/native-runtime.json` 里原本被默认配置匹配的 3109 个已声明方法名加 9 个 V1 确切原生成员，减去移出 `enemy` 的 `EnemyAppearance.InterpolateGlow`，合计 3117 个；默认配置没有重复归属。新增的 managed noise callback 单独列为转交观察，不算原生方法。

- `EnemyAppearance.InterpolateGlow` 不进任何默认配置：它的原生方法体会自己抛 NullReferenceException，原版里这个异常沿原生调用栈向上传；挂上观察补丁后它在 Il2CppInterop 跳板被接住、记成 `During invoking native->managed trampoline` 错误并让调用方继续执行，观察补丁因此改了游戏行为（一局几十条）。观察点不得改变被观察方法的异常与返回语义，这类方法只能不挂。特定方法从旧通配项拆成成对记录时排除旧匹配，限频不放宽。元数据名字存在不代表 IL2CPP 签名安全或挂钩成功。

- 门扫描保留其专门 CP 类型 entry，通配重复项不再抢走带成员字段的记录。
- 伤害/波次重复方法由 `damage` 的相应条目保留必要字段，`enemy` 只保留其他不相交方法。
- 维度自然路径的特定 Dimension、WorldEvent 执行、Player warp、FactoryDone、PlayerSync 和 GameState 调用归 `dimension-landings`，它保留原只读字段并合并原其他配置的必要字段；其余方法仍在原 profile。
- 默认配置应配套安装；单独开一个领域 profile 不表示已经覆盖该类型的所有方法。
- 必须成对取值的人口抽选行使用 `trace-pair:` 强引用，误删 `phases` 或降为 after-only 会使门禁失败；普通 `trace:` 不承诺入口快照。

再次更新原生目录或类型通配时必须复核重复归属与方法/字段覆盖。

## 实际安装范围与拒绝证据

- 核 `patches[]` 的确切签名、`installed`、`removed`、`failures`、`observationScope` 与 `nativeReason`，并与实际 call 记录对应。`methods[]` 只表示匹配；`installed=true` 不单独证明原生入口已挂钩。`native-method` 是原生方法观察，`managed-method` 是托管方法观察，`managed-wrapper` 是生成的 CLR 包装层观察；模块 Noise 仍受下述专用白名单限制。缺失、`uninstalled` 或 `unavailable` 不算可用范围。
- 某些共享、短/分支、SIMD 或已改写的原生入口仍被原生安全门拒绝，但可验证的 CLR 包装层可独立安装观察。`managed-wrapper` 不改写被拒的原生入口；原生直接调用可绕过包装层。其 `entered` / `returned` 和 `observationComplete=true` 只限定包装层入口/返回，不能证明全部原生调用、原生 original 执行、原生方法前后边界或原生补丁顺序；其他原生补丁仍可能调整参数、跳过原生 original 或改变结果。只有包装层而判据要求原生边界时保留未证，不把缺日志当零次。
- 原生拒绝与包装层降级按批记 `development.trace-coverage` 汇总；完整条目在实际保存的 `trace-coverage.json` 或 trace 报告的 `nativeCoverage`，保留 profile/type/method/signature/reason/observationScope/owners/attempts。`reportQueued=true` 只说明已入写队列，必须核实际文件或完整报告；没有文件不代表没有拒绝。不再要求旧的逐方法 `development.trace-refused` / `development.hook-refused` warning。
- 固定生命周期/性能 hook 仍只接受原生准入，不能用包装层替代。核 `development.hooks-ready installed=... refused=... coverage=...`、`nativeCoverage` 中 `profile=fixed-hooks` 的拒绝项及记录器开启时的 `session/trace_refused`；`coverage=partial` 不表示被拒点已有证据。真实安装与卸载错误仍保留为错误，源代码/配置门禁和覆盖汇总都不是游戏通过。

## V1 必验观察边界

- `enemy` 的 `EB_Hibernating` / `EB_Patrolling.OnNoiseDetected(NM_NoiseData&, float)` 只由专用 typed ref 适配观察，其他 by-ref 不开放。核 `patches[]` 的完整签名、`installed=true`、`removed=null`、错误状态与 `observationScope/nativeReason`，并对照 `nativeCoverage`；`methods[]` 仅是匹配清单。`args[0].reader=typed-ref-NM_NoiseData` 保留真实噪声参数及是否可收窄为玩家，但 `managed-wrapper` 的完整 typed ref 配对仍不证明原生直接调用或原生前后边界。
- 若原生方法已被 ForgeEngine 的 Enemy 领域 detour，安全门仍可拒绝它。唯一允许的模块观察是 `ForgeEnemy.Native.EnemyActionHooks.Noise`，仅在两条确切原生 postfix 都实际注册时接入；`module-trace-pair:` 的 `route=module-postfix`、`pairScope=managed-helper-entry-return`、`nativeOriginalExecuted=unknown` 必须保留。模块前后不等于原生前后；它不独立证明原生方法执行或敌人实际醒来。
- `SetDetectedAgent` / `SetTarget` 和唤醒方法记录有界的敌人 ID、目标 ID、检测类型。初次无目标时的 `null-before-leaf` 保持未证；完整的 returned AFTER 只能证明返回时字段已有该值，不反推 BEFORE 或 prefix 时刻。不要为得到完整配对把缺目标改成默认值。
- 手电真正读数在 `state/snapshot.body.sections.environment.items` 的 `kind=playerFlashlight`，通过 `wielded` / `partPointer` / `settingsId` 对应当前装备，再核 intensity/range/spotAngle/color。维度门在 `sections.objectives.items` 的 `kind=dimensionPortal`，保留 serial/pointer/instanceId/entity；原生指针只在同机同生命比较。
- F3 和 `v1-observe.forge` 同路导出已加载能力 prefab 的有限数值字段及活实例原生 cooldown/lastTriggerTime，文件按 captureId 唯一保存；未加载、字段未支持、截断、生命身份不明、单位 pending-game 均不得升级。IsAbilityReady 只观察游戏自然调用，不由导出器主动调用。
- 所有日志、命令完成和源代码检查只定位材料。实际伤害、出手、图像/声音、对象数量和生命周期要独立验证；没有进行游戏运行就没有游戏通过结论。

## 区内门与隔音对照（只读，尚有原生证据缺口）

本批把空间拓扑和三层声音观察分开保存，不根据“未出现记录”推导隔音，不主动调用噪声、醒来、开门或导航函数。

- `layout` 的 `gateTopology` 与 area 行，以及实时安全门/弱门行，保存同一 `topologySessionId` / `topologyGenerationId` 下的原生引用桥。gate 双侧 Area、course node、cluster 与 portal 双侧分别保存，不假定两套 A/B 顺序相同。关/开/破坏状态、gate 缓存通行字段和 portal 开启缓存各自记录；没有方法体纯度依据的计算 getter 保持 unknown。未成功执行原版 Build 的返回值不能证明建门成功。
- trace 上下文的 `generationSessionId` / `generationId` 供同机同代连接拓扑；指针只在该生命范围比较，不是跨机、跨关或持久资源 ID。
- `acoustic.layer` 配置只接受 `player-detection`、`noise-manager`、`wakeup-propagation`。每条输出 `acoustics.source` / `receiver`、各自 position / Area / course node / cluster、实际三维距离和明确的 unknown 原因。不同机制不能合并为一次噪声。
- 端点和字段来自配置中的有界 `instance` / `argN` 路径；`parent.source` 仅继承同线程、同次安装、仍有效的嵌套调用，不把时间接近或坐标最近当因果。跨线程、代切换、停录重开、异常与卸载均不得沿用旧父源。端点、距离或配置字段缺失会使 `observationComplete=false`。
- 已声明的采样点为玩家物品状态发送/接收及 `EnemyAI.SetDetectedAgent`（玩家标志/检测）、精确静态 `NoiseManager.MakeNoise(NM_NoiseData)` 和两条 `OnNoiseDetected(ref NM_NoiseData, float)`（噪声数据/接收）、`SetTarget` / `ChangeState` / `ES_HibernateWakeUp` 的 Enter、DoWakeup、CommonExit（目标与状态）。静态 MakeNoise 没有实例接收者，只有该精确专用适配允许 paired `instance: []`；不放开普通 by-ref 方法。
- `signatureOnly: true` 只枚举此 entry 的真实声明并写 `native-signature-review-required` 收据，绝不安装此 entry 或伪造 call。`sound-isolation-signatures` 用它对照 ProcessNoise、ProcessNode、DoHibernateWakeupPropagation、PropagateTargetFull/Limited 的运行期声明。用户已从 build 20403457 的 dump.cs 核实参数、RVA 和 NM_NoiseData 字段偏移，记录于 `evidence/noise-signatures-20403457.json`；该文件是人工核对来源，不是方法体提取结果。它不是全局封锁名单，其他已有 profile 的通配匹配仍走原有安全准入；不能由该配置推断某方法全局未挂钩。
- `sound-isolation-signatures` 默认不启用（`enabledByDefault: false`），需要对照签名时手动开启，否则每次启动都会写一条 `native-signature-review-required`。
- `level` 里的 `RundownManager.MainLayerCompleted` 在 exclude 中：build 20403457 的 RVA 0x13e0c60 只有 16 字节（`48 85 d2 75 03 32 c0 c3 83 7a 18 00 0f 9f c0 c3`），入口第 3 字节起就是条件跳转，按 `NativeHookPrologue` 的规则不能安全 detour；32 字节入口窗口还会越过函数尾读到相邻函数，那里已被别的 hook 改写，所以报 `native-hook-original-unverified`。
- 现有 native 对象读取保护完整保留：hook 中未经 lifetime/bridge 证据的 IL2CPP 对象仍返回 `unverified-trace-object`。离线 CLR doubles 验证的是序列化与安全边界，不证明游戏里 NM_NoiseData、玩家/敌人引用的实值已读到。本批只登记上述补充声明，尚未给五个处理/传播方法新增数据采集适配；方法体和桥接生命周期证据未齐前，不能宣称三层链齐全。接收回调、目标改变、DoWakeup 返回均不等于接收者真正醒来，应核实际状态转移并保留未知。

### 手动采集顺序

1. 用本机真实 Native 产物构建并安装 ForgeDevelopment 与当前 trace 配置，进入以下任一实际包含的房间：`geo_64x64_tech_data_center_HA_01`、`geo_64x64_mining_refinery_HA_01`、`geo_64x64_mining_storage_HA_06b`。候选名只是筛查标签，不生成房间、敌人或门。
2. F7 取初始全量状态；保留同次生成的报告和 `rec-*` 目录。确认快照中的双侧原生引用，并截图标明本次人和敌人实际站位。
3. 每个门先保持原版关闭状态，在一侧手动走动、近战、开枪，分别试验，每次前后默认 F6 加书签、F7 取快照、F8 截图。开门后、弱门被手动砸开后分别重复；不能恢复的敌人惊醒/门破坏使用新一次生成并分开比较，不能当同代前后。
4. 交换两侧重复，另外手动触发原版尖叫。记下看见哪些敌人真正醒来；不要为“没醒”补成功回执。每轮默认 F10 导出报告；如改过热键，以本机配置为准。F5 仍是原覆盖表的 BEFORE/AFTER，不是自定义标签入口。
5. 先核 `trace-coverage.json` 的实际签名、拒绝原因、observationScope、overflow/截断与配对完整性，再查三层差异。`managed-wrapper` 不能证明原生内部调用，`module-postfix` 不能证明原版 original 执行；unknown、缺阶段、丢弃和未安装均归缺证，不能解释为隔音。

本机仍需补：`evidence/h10-noise-bodies-20403457.json` 中的处理/传播方法体与 NM_NoiseData/actor 在 hook 中的安全读取、生命周期证据，以及 Native 构建、三候选房间的真实采集和原版敌人状态判读。声明未提供的返回类型/static 修饰保持未核实，不用参数和 RVA 反推。当前材料不能提前判定门是否隔绝某层传播。
