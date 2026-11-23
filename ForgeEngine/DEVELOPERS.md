# Forge Engine 1.0.0

> 实施顺序与原版内容完整覆盖见[唯一开发计划](../../Docs/forge-contract/FORGE-FRAMEWORK.md) §4，分步验收见 §7；本文件仅说明实现与用法。

Runtime 是唯一的公共服务与 GTFO 宿主：类型、注册、权限、生命周期、模拟调度、状态、事务结果，**以及多人环境下的网络可靠性与社区修复**。Player、Map、Enemy、Weapon 四个领域也在同一个 `ForgeEngine.dll` 里（各自的文件夹 `Player/`、`Map/`、`Enemy/`、`Weapon/`，说明见下文同名四节），由插件启动时按固定顺序启动；玩家只装这一个 DLL。Development 是另装的作者插件，宿主不依赖它。

交付要求见唯一开发计划 §6 U-RUNTIME、U-NET、U-PLAYER-MOD、U-MAP-MOD、U-ENEMY、U-WEAPON-MOD（链接见[仓库 README](../README.md)）。跨包依赖规则、provider 身份与所有权见下文“跨包结构与所有权”。公共 SDK 的 API 细节见 [Framework/README.md](Framework/README.md) 与 [Framework/HOST-LIFECYCLE.md](Framework/HOST-LIFECYCLE.md)。跨域触发原子（provider `forge.module.trigger`：纯计算、目标筛选、区域场）并入本包，随宿主启动内置注册，说明见 [Framework/Trigger/README.md](Framework/Trigger/README.md)。

## 当前能力

### 公共 SDK

`Plugin.Runtime` 返回 `IForgeEngine?`，模块通过它注册、查询和读取生命周期。启动、世界切换、计划装卸、推进、迁移、检查点、复制状态和远端命令执行由宿主的 internal 方法控制。离线测试使用 `ForgeEngine.Testing.TestRuntime` 的扩展方法调用这些真实入口，不另建执行路径。

**宿主迁移的交接接口（引擎侧）。** 迁移提交时发出的 `WorldChanged` 带 `RuntimeLifecycleEvent.IsMigration = true`，`PreviousWorldEpoch` 仍是旧世界期、`Current.WorldEpoch` 是新世界期：同一关卡的同一批对象继续存在。持有原生对象的领域在 `IsMigration` 时只跟随世界期、保留自己的表与回执，不当作新世界清空；原生句柄跨主机的采用走 `RuntimeModuleHandle.RegisterProviderHandover`（Map 与 Enemy 的用法见各自一节的「主机迁移」）。

`Mods/Testing/ForgeEngine.Testing.csproj` 是离线测试支持程序集，不随玩家包发行。`TestContexts.Create` 从模块的 handler 声明和类型化输入构造正式命令帧；绑定外部 canonical 能力时，显式传入 `declarations`（`RuntimeKernel.ExportManifest()` 的 `registry` 属性，或合同模块的 `RegistryJson` 解析值）。`Query` / `Evaluate` / `Effect` 构造相应上下文，`SetEffect` 设置命令的持续效果上下文。`ResultAssert.Status` / `Rows` / `Row` 检查状态与有序结果行，`TryValidate` 复用生产结果规则，`RenderRows` / `RowRefusal` 通过真实结果行 ABI 检查身份、字段、配对及容量。`TestRuntime.ReadPlan` / `Load` 只读取 TS 编译器生成、由索引 SHA-256 校验的计划；`VariantKey` 用测试参数定位已有变体，不编译或修改计划。

发行源码声明含 Test 的类型由 `Tools/Forge/validate-layering.ts` 拒绝，例外表只减不增。

计划图夹具与编译选项在 `Tests/Forge/fixtures/compiled-plans/`。修改图或合同端口后运行 `pnpm generate:test-plans` 重新生成；生成器先按 Primitive 投影刷新清单夹具里与合同提供者同 id、同 owner 的行的 graph（其它 owner 声明的测试行不动），再调用正式编译器，再由计划读取器校验语义往返一致。`pnpm check:test-plans` 逐字节检查生成结果，已接入 `pnpm test:forge`；不要直接修改生成的 `plans/` 或 `index.json`。清单夹具只存行池与配方（`manifest-pool.json`、`manifest-recipes.json`），TypeScript 经 `manifest-pool.ts` 的 `loadManifest` 读取；生成器写出的 `manifests/` 下的 JSON 是生成物，改清单改行池与配方，新清单放进 `manifests/` 后运行 `pnpm generate:test-plans` 入池。加载器的拒绝测试可对编译产物作明确的非法字段突变，不能另写正向计划布局。Framework 内部白盒用例可链接测试读取器源码，领域 handler 测试引用独立的 Testing 程序集。

`Framework/` 是不依赖 Unity 或 GTFO 类型的注册、严格计划验证、世界与实体生命周期、单一有界队列和执行结果合同，与宿主和四个领域一起编进 `ForgeEngine.dll`。`Framework/ForgeEngine.Framework.csproj` 把同一批不碰游戏的源码（`Framework/**` 加四个领域里不在 `Native/` 下的源码，清单在 `Framework/Domains.props`）另编成 `ForgeEngine.Framework.dll`，只给服务器准入程序和测试用，不进玩家安装包。四个领域与第三方扩展都走同一个 `RegisterModule(module, level)`：领域的级别取引擎 cfg 的 `Logging.Level`，第三方经 `Plugin.Runtime` 注册并自己给级别。只允许模块用自己的返回句柄发布或取消——这是受信模块之间的所有权约束，不是任意第三方 DLL 的安全沙箱。

`Framework/Policy/CombatContracts.cs` 注册 5 个通用 combat canonical 定义（承伤事实、治疗动作、生命变化事实、死亡流程、肢体破坏），0 个 binding。Enemy 模块只注册自己的实际 binding 和 `gtfo.enemy:<GlobalID>` 接收器；来源、目标与阵营独立。

计划执行按显式后继走 `action` / `control` / `query` / `pure` 四类步骤：`query` 与 `pure` 按需在同一激活内求值一次，`action` 走注册 handler 或领域提交路径，`control` 由内核自己在同一条走线里分派（`branch`、`sequence`、`delay`、`ramp`、`repeat`（按次数，或带间隔成为定时重复）、`for_each`（遍历对象或位置列表）、`enum_switch`、`cancel`、`restart`、`present`，以及变量读写、关卡具名对象读写、`once`、`emit-message`、`wait-event`）。计划本身仍是扁平、预解析的步骤数组：没有 region、没有回跳、没有运行期按名字查找。

控制走线的挂起与恢复是这一版收口的部分：`delay` 与 `g-wait` 暂停时把它们所在的控制区域（sequence 的下一个出口、循环的下一轮）交给调度器的续延，恢复后先走完被中断的那一段再继续，循环的 `index` / `item` 随区域一起恢复；带间隔的 `repeat` 的 `next` 立即进入、它的 `body` 每跳与 `ramp` 的每跳是各自的激活，因此不带外围区域。一次控制步骤的每一次进入是一个独立续延（自己的句柄、自己的脉冲序列、自己的账本身份），因此同一 `delay` 在脉冲或循环里可以同时存在多份。`g-wait` 离开任一出口（超时、事件唤醒、计划卸载，以及 provider/句柄失效后再也无法恢复的丢弃）时退役：从等待表里出表、归还句柄槽位、关掉它占着的订阅门，所以完成过的等待不再占等待名额、也不再让那条绑定报告有订阅者；超时从等待真正开始的那一 tick 起算。退役发生在**结局被受理的那一刻**，因此受理之后任何提前退出（作用域取消、计划卸载、provider 消失、预算拒绝）都不会把槽位留下；等待还带来源作用域身份，`CancelScope` 与模块注销会把该来源挂起的等待一并退役，所以没有截止时间的等待也不会活过它的来源。归还槽位时只释放**仍属于这个等待的那一个句柄**（同索引、同 provider、同世界、同 generation 才算），因此 provider 注销腾出的索引被另一个 provider 重新铸造后，陈旧结局不会把别人的句柄清掉。

`stop-entrypoint` 是整个走线的规则，不是某一层的：动作被拒、失败、取消或过期时入口立即停止，外围的 sequence 不会走下一个出口、循环不会进下一轮；只有「部分提交 + 已确认提交」继续。续延/等待的恢复是已经受理过的同一次激活，因此不再被触发器门二次询问。

两张跨计划的身份表各自用**结构化的键**，不再靠字符串切分：门行按 `(planId, nodeId)` 元组建键与查找，`g-once` 锁存按 `(planId, stepNode)` 组合（同一张卡不论从哪张触发卡走到，整关只放行一次），模块变量的挂载键按 `(planId, entryNode)` 组合，三者唯一的字符串拼写（`plan.node` / `plan:step` / `plan/entry`）在保存、恢复与卸载边界上从**右**拆分——条目节点 id 的文法 `[A-Za-z][A-Za-z0-9_-]*` 不允许分隔符，所以最后一个分隔符必然是那一个，计划 id 里的点、冒号和斜杠都能原样读回；卸载计划时按元组/计划半边精确比较，不再用前缀匹配，id 互为前缀的两个计划不会互相删行、也不会互相清值。

变量表执行它声明的地址上限 `RuntimeVariableContract.MaximumEntries`（4096）：新地址超出即按 `variable-budget` 拒绝，已存在地址的更新不受影响；整份快照与增量在读取任何一行之前先按同一预算整份拒绝，不会先分配再失败。一份增量是**原子**的：移除键先暂存、行先解析、容量按增量留下的那张表来问（移除算作它自己替换品可用的空间），全部通过后才一起应用移除与写入；畸形行或超限都整份拒绝，不移除也不写入。

变量的值跨计划卸载保留（值属于世界，检查点恢复依赖这一点），但**只在同类型同作用域下**：一个名字被重新声明为另一种类型或作用域时，旧载荷在声明登记的那一刻被清除（经 touch 集复制给客户端），读取方拿到的是新声明自己的初值，而不是上一份契约的字节。

计划离开只有一条路径：显式 `UnloadPlan` 与模块注销带走依赖计划时都走同一个内部移除——先在 provider 仍能还原原生状态时结束该计划的效果，再退役它持有的等待、遗忘它的变量/门/锁存/模块值，最后移除计划本身；生命周期与订阅重建是调用方的批边界（一次卸载一次，整批注销一次）。provider 自己的清理（句柄、效果、以及它作为**来源**的等待）与计划自己的清理互补，不互相替代。

等齐、受控并发、统一取消、重试与超时由一组动作（`group`，`Framework/Execution/RuntimeKernel.Groups.cs`，状态在 `KernelFlow.cs`）承担，语义见合同 §3.2 I-PLAN，游戏内未取证。未完成的部分照实记：一个 `ramp` 嵌在别的控制区域里时，它的每跳区域不被外围区域等待。计划加载的完整校验、成本预留与结算、查询预算、触发器门、变量与检查点、网络恢复分别见下文各节。

**挂载匹配器不再有内核自有的种类。** `attachments[]` 的每个种类都由注册它的 provider 匹配：`RuntimeModule.AttachmentMatchers` 的值是 `AttachmentMatcherRegistration`，由 `BySubject`（针对事件主体匹配）或 `ByScope`（只看挂载目标，不看主体）二者之一构造，注册期就确定了这个种类要不要事件主体。派发时，无主体的注册项直接从挂载目标判定，因此世界、计时与脉冲这些不带主体的事件同样会被判到；有主体的注册项照旧在事件的 source 与触发载荷的实体端口上问。没有任何 provider 注册的 kind 在装载期按 `attachment-kind` 拒收（`level` 也不例外），所以计划不会出现"被接受但永不派发"的状态。provider 注销时它自己的 kind 一并移除，同一 kind 可以被下一个 provider 重新认领。

**四个领域共用的宿主与合同小件。** Framework（无 Unity 类型）：`HookFeature`（领域的钩子功能表一行：名字与它站在哪些回调类上；`Withdrawn` 取被拒类拖下的功能，`Refusals` 写被拒理由；各领域派生自己的行、保留自己的表与记录文字）、`DomainSession`（`CheckThread` 线程断言、`Cleanup` 收集清理失败、`PreserveCleanupFailure` 保留首个失败并限长）、`ResultAggregation`（多行结果归并为一行，全产品只有这一份。`Conclude(行, 取提交态, 取码, 输出, 取是否做成)` 用于主机与持有者层：没有行是 `no-targets`；全部做成是成功；有行已提交是 Partial，其中有行未知则提交态为未知；没有行提交时，有未知行是失败/未知并取第一条未知行的码，否则是拒绝并取第一行的码。`Presented(行, 取状态, 取码, 输出[, 取提交态])` 用于展示层，与 `RowWriter.ConcludePresented` 共用同一个 `PresentedTally`：用 `RowWriter.EndElsewhere` 写的行（别的机器负责的接收者，如别人机器上的玩家）照常写出但不参与本机结论，全部行都是这种时取第一条的回答；其余行里，没有行是 `no-targets`；有行提交未知（只有房主汇总各机器回答时会有）是失败/未知并取该行的码；每行都成功是成功；有行已发出是 Partial，另有行被拒或失败时取第一条被拒行的码（真实拒绝不被已发出的行盖住），否则取第一条发出行的码；都没发出且状态相同取该状态与第一行的码；其余是失败并取第一条非拒绝行的码。房主汇总一步表现的各机器回答也走 `Presented`，每台机器一行。空接收者集合由内核在调用处理函数之前统一回答 `no-targets`，处理函数不再自己判空，收到的接收者至少一个，并且每个接收者写一行）、`ContainedFailure`（被包住的异常写一条错误记录，次数闸门由各包自持 `ForgeCore.OnceReporter`）、`RuntimeJson.Payload/Canonical/TryInteger/TryDecimal`、`EvaluationContext.RequiredInput`、`RuntimeModule.Without`（按绑定或能力 id 撤回）。宿主（`GameBindings`）：`DomainWorkPhases`（帧入口：Player、Map、Enemy 与表现层共用一个 Runtime 工作租约 `forge.domain.frame`，每帧按 Player、Map、Enemy、表现层的固定顺序跑；某一项的帧抛异常时按它自己的名字（`forge.player.frame`、`forge.map.frame`、`forge.map.tick`、`forge.enemy.frame`、`forge.presentation.frame`、`forge.presentation.hud`）上报，并在本世界内不再跑它，其余照跑；`perTick: true` 的一项只在 Runtime 就绪且内核 tick 前进后跑一次，暂停或迁移中不推进，「每个内核 tick 只跑一次」的门只有这一份）、`WorldFault`（领域会话的单关故障：`Mark(世界编号)` 后本世界内 `Holds` 为真，进下一个世界自动清除，原生回调一次失败不再让整个领域停到重启；Map（含门与终端、终端日志、扫描覆盖、世界事件各半边）、Player、Enemy、Weapon 共用，只有启动失败与关闭仍由各会话自己的永久标志管）、`DomainWorldObservation`（按世界重订阅并可回滚的钩子租约）、`GameVectors`（Unity 向量读取）、楼层组件枚举 ForgeCore `GameFloor.Components`、`NativeMemory`（可读内存查询，补丁安全与钩子平台共用）、`RecipientLedger`（按接收者记账的重放基类，HUD 文字与地图接收者展示共用）；游戏程序集哈希只有 `GameRuntimeBridge.GameAssemblySha256` 一份。导入模型的包内文件（地图摆放、房间、武器挂件与敌人挂件共用一份）：Framework 的 `PublishedResources.TryReadHashed`（按「SHA-256 文件名 + 扩展名」读并核对字节）、`PublishedResources.PackageRoot`（文档所在的插件目录）与 `ImportedMeshResource`（`.fmesh`）；GameBindings 的 `ContentPackages.FindResource(安装根, 包, 哈希, 扩展名, 谁要, 拒绝)`（I-RESOLVE 唯一找文件入口：先本包 `forge/resources/<哈希><扩展名>`，再按 `manifest.json` 依赖声明顺序看依赖包，按文件名判断存在；都没有写 Error `resource-missing`，点名包、哈希与谁要；各包 `manifest.json` 依赖顺序每个安装根只读一次，不扫描建索引、不缓存文件）；同一个 `ContentPackages` 是读内容文档的唯一入口：ForgeEngine 加载时（`GameRuntimeBridge.Initialize`）对 `plugins/*` 发现一次包（日志 `content.discovered`），各领域在自己的启动或会话里用 `Read(安装根, 读取者, forge 子目录, 文件名模式, 码, 拒绝)`、`ReadResources(安装根, 读取者, 资源种类, 码, 拒绝)`（`forge/resources/*.resource.forge` 每个安装根只读一次并按信封种类分发，同 id 冲突全部拒绝）与 `ReadRundowns(安装根, 读取者, 报告)` 领到自己那类文档（每次一条 `content.read`，记读取者、类别、文档数与包数），不再自己列 `plugins/`；ForgeEngine 自己的计划（`PlanDiscovery`，`forge/plans`）与内容条目（`ContentPackDiscovery`，`forge/content`，包目录取自这次发现、仍按自己的别名与上限规则判）、Map 的包准入（`MapPackageIntake.Run`，游戏与离线准入运行器同一段代码）和 ForgeDevelopment 的包测试读取都走这次发现；作者重载（`ReloadAuthoringPlans`）重新发现一次，Play 不重扫；`PublishedResources.Discover(安装根, 包列表, …)`、`PackageDirectories`、`SortResources` 是它在 SDK 里的底层。作者声音就是包里的 `forge/resources/<revision>.ogg` 文件本身（revision 是 Ogg 字节的 SHA-256，没有描述文档、不放 base64），按 revision 找：`CustomSoundTable.Find(安装根, 包, id, revision, 谁要, 拒绝)`（先 `FindResource` 找文件，再用 `PublishedResources` 的有界读取器核对字节哈希与 Ogg Vorbis 头，只留路径与声道数，字节读完即丢）；`CustomSoundLibrary` 在关卡计划采纳时找计划音频字面量用到的声音、领域用 `Require(安装根, 持有者, 包, revision, 谁要, 拒绝)` 登记自己要的声音（敌人换声，id 就是 revision），合成 `Current`，宿主把 cue 文本与各文件路径经 `AudioKit.Load(身份, cues, files)` 交给 ForgeAudio 直接从包内文件播放，不解码、不拷贝、不写 `BepInEx/cache`；握手把 `.ogg` 与 `.png`、`.fmesh` 一样计入资源指纹；GameBindings 的 `ImportedModelFiles.ForEntry`（一条内容条目用到的导入模型，武器与敌人内容注册都从这里取，交给 `NativeModelRuntime.Prepare`）与 `ImportedModelFiles.ForReferences`（一组模型引用各读一次，地图摆放用），模型文档、网格与 PNG 都经 `FindResource` 找到再核对，坏的逐个报出、其余照常装。`NativeModelRuntime.CopyPrefix` 是摆放出来的对象名前缀，宿主据此区分自己的渲染器与挂上去的模型。`NativeModelRuntime.Loading(assembly)` 答模型要的分片还在异步加载、或（带导入模型的组装）本关预载的导入模型贴图还没解完（按整关答：`BeginLevel` 建关时把本关选中内容的导入模型贴图排进 ForgeVisual 的解码队列，查询只再问还没解完的包、不分配，全部解完后只看计数；第一次摆放只建网格与材质；还在解码时 `Place` 答 `model-resource-loading:imported-textures-decoding:<id>`），调用方等它，不必每帧试摆并读拒绝码。`NativeModelRuntime.Place` 的 prepare 回调拿到未激活的摆放根和它导入模型的批量绘制（导入模型没有渲染器挂在根下）；`drawLayer` 让导入模型画在宿主身体的层上（敌人骨骼的层是命中层），不给时用宿主自己的层；包里缺的导入模型每关每个资源记一条 Error（`model-imported-missing`），该零件不画、其余照常。`Place(..., collision: true)`（地图摆放与房间陈设的 `collision: "native"`）给带碰撞形状的导入模型零件在摆放根下按零件位姿挂 MeshCollider，网格是 ForgeVisual `ImportedModel.CollisionMeshes` 同关共用的那份，层是 `LayerManager.LAYER_DEFAULT`（与原生碰撞体一样在着色层设完后恢复），报告 `model-collision` 带 `importedColliders`；没有碰撞形状的模型每关每个资源记一次 `model-collision-imported-skipped`（reason=no-collision-shapes），只做装饰。导入模型的朝镜头子件（`ImportedModel.FacingParts`）每个在摆放根下按零件位姿建一个轴心节点，挂自己的网格渲染器（不进批量绘制，层与主体的批量相同），节点交 ForgeVisual `CameraFacing.Follow(节点, Facing.YawToCamera)` 在相机剔除前转向本机镜头，不挂碰撞；外观只隐藏它部分槽位时每关每个模型记一次 `model-facing-partly-hidden`，那些槽位照画。
**只读订阅触发器与服务。** `RuntimeModuleHandle.ObserveTrigger(bindingId, Action<RuntimeEventView>)` 让任何已注册模块只读观察任一 provider 的触发绑定（返回 `RuntimeTriggerSubscription`，可 `Dispose`，模块注销时自动结束）：观察者计入 `RuntimeSubscriptionGate`，看见 provider 用 `Publish` 发布并被接纳的事件（负载有效、非重复），不论有没有计划消费；它不入队、不改派发结果（无人消费时发布方仍得到 `no-consumer`），不是第二个调度器；回调里不能发布或改注册（`trigger-observer-mutation`），回调异常即移除该观察者并写 `observer-failed` 日志；容量 `MaximumTriggerObservers`=64，超出按 `trigger-observer-budget` 拒绝，未知或非触发绑定按 `not-trigger` 拒绝；定时脉冲产生的事件不在范围内，迁移缓冲期内到达的事件不通知。没有订阅者时发布路径只多一次计数判断，不分配。最短第三方示例（provider、一个动作、一份手写计划、一个观察者）在 [Framework/README.md](Framework/README.md) 并随 Framework 测试实跑。四个领域在同一个程序集里，彼此直接调用对方的会话（`PlayerHost.Directory` / `Players` / `Session`、`MapHost.Session`、`EnemyHost.Session`、`WeaponNativeSession.Current`），不经服务表。服务表（`PublishService<T>` / `TryService<T>`）只放给别的插件用的入口：给第三方的 `IPlayerDirectory`（Player 发布）与 `EnemyAgentResolver`（Enemy 发布），给 ForgeDevelopment 的 `PlayerBotSlotBegin`、`IncomingDamageReceiver`（Player）、`EnemyNoiseTraceResolver`（Enemy）、`MapGenerationReadback`、`IMapCaptureService`、`IMapInspectionService`（Map）。服务按精确类型发布，所以函数服务是具名委托（`Framework/Execution/CrossPackageServices.cs` 里的 `EnemyAgentResolver`、`MapGenerationReadback`，`GameBindings` 里的 `PlayerBotSlotBegin`、`EnemyNoiseTraceResolver`），不用裸 `Func`：第三方发布同签名的 `Func` 不会撞 `service-conflict`。`EnemyAgentResolver` 返回 `object?`（不碰游戏的那份程序集不引用游戏类型），调用方转成 `Enemies.EnemyAgent`。

### 宿主启动与配置

宿主配置由 `RuntimeSettings.cs` 拥有：`Runtime.Mode`（默认 `Play`；Rundown 包不带基础包 cfg）、`Logging.Level`（默认 `error`）。这是整个包唯一的日志级别：Runtime 自己的 provider、内置的 Trigger 与 Presentation、四个领域的 provider 都取它，领域没有自己的 cfg。原键名与命名、数字模式值都保留。模式按原始文本显式解析——真实的 `ConfigFile` 枚举绑定器曾被观察到对非法文本静默选中 `Authoring` 或组合枚举值，因此非法或空模式现在在原生初始化之前就失败，不会拿到一个"启用"的默认值。

`Off` 只绑定宿主键，不启动任何组件或 Hook，也不扫描计划。`Play` 与 `Authoring` 启动同一个宿主：1 个 GameDataAPI 数据就绪订阅、5 个 LevelAPI 关卡生命周期订阅、2 个 EventAPI 检查点事件订阅（`EventAPI.OnCheckpointReached` 存档、`EventAPI.OnCheckpointReloaded` 重载）、5 个只覆盖原版主机迁移边界的 Harmony 补丁与迁移完成的 ForgeCore 订阅（`GameHooks.MasterMigrationFound`）、网络绑定（SNet 成员事件订阅；引擎的 ForgeCore 通道在第一次启动时开一次，见下）与 ForgeCore `Frame` 上的帧回调（`HostFrame`）。注册窗口打开时，`Plugin.Load` 先内置注册 Trigger 与 Presentation，再按 Player、Map、Enemy、Weapon 的固定顺序调 `PlayerHost.Start`、`MapHost.Start`、`EnemyHost.Start`、`WeaponHost.Start`；某个领域启动失败只回滚它自己，写一条 BepInEx 错误 `Forge <domain> domain failed to start`，其余领域照常启动。宿主挂起时一个领域都不启动。**宿主不含任何诊断、报告或性能采集**；两种模式唯一的区别是可选的 [ForgeDevelopment](../ForgeDevelopment/README.md) 插件只在 `Authoring` 下启动。模式变更需要重启进程，不支持热卸载。

关卡生命周期走 GTFO-API（`BepInDependency("dev.gtfomodding.gtfo-api", ">=0.6.1")`，硬依赖写最低版本），宿主不再自己补 `GameStateManager`：`LevelAPI.OnBuildStart` 对应原来的 `Generating`（丢旧世界、释放等待新远征的挂起）、`LevelAPI.OnEnterLevel` 对应 `InLevel`（玩家能移动时取主机基线并打开玩法阶段门槛）、`LevelAPI.OnLevelCleanup` 对应原来的 `OnLevelCleanup`/`OnResetSession`（关卡消失）。检查点存档与重载同样是订阅，不是 detour。GTFO-API 没有主机迁移生命周期，因此宿主只在原版 `SNet_MasterManager.ForceMigration` / `SearchForMigrationMaster` 开窗，并在 `SNet_Capture.CaptureGameState(Migration_A/B)`、`RecallGameState`、`SNet_SyncManager.OnFoundNewMasterDuringMigration` 与 `SNet_MasterManager.ForceExitToLobby` 跟随原生边界：不参与选举、不调用 checkpoint recall，也不替代 SNet 世界同步；签名与 RVA 见 [evidence/host-migration.json](evidence/host-migration.json)。

`GameAssembly` 哈希不符不再抛异常：宿主把 kernel 停在 `Registering`、置 `Plugin.IsSuspended`/`Plugin.SuspensionCode` 为 `startup-failed`，写一条 `runtime.suspended`（message 含期望与实际哈希），既不启动领域也不加载计划。依赖本包的第三方插件在自己的 Load 里查 `Plugin.IsSuspended`，停止注册并各自写一条 BepInEx error。

`Plugin.ConfiguredMode` 是冻结的启动选择，公开在宿主程序集里，**不是权限、不是就绪状态、不是主机权威**。`Plugin.Runtime` 只在 Load 成功后提供；失败或 Off 状态下为 null，同一实例不重试 Load。依赖 Runtime 的插件在自己的 Load 中、Runtime 首个 FixedUpdate 之前注册。

启动失败时先关闭玩法入口再清理：宿主停止 → unpatch → 关闭帧回调。每个已获取的阶段都会被尝试清理，即使其中一步或错误上报失败也继续执行后面的步骤，最后重新抛出原始启动异常。清理与上报的失败保留在 `Data["ForgeEngine.StartupCleanupFailures"]` 的 AggregateException 里（字典不可写时不能替换原始异常）。这是对已获取阶段的尽力清理，不是任意原生副作用的回滚保证。

**计划按关载入。** `Play`/`Authoring` 下，首个固定更新在其他插件注册完成后，按包目录发现离线计划：只看 `BepInEx/plugins` 的一级子目录下的 `forge/plans`（不存在则静默跳过），取直接子文件中按 ordinal 精确匹配 `.plan.forge` 后缀的文件，按 `/` 分隔的相对路径 ordinal 排序。启动只建计划索引（`GameBindings/PlanDiscovery.cs` 的 `Index`，`Framework/Content/PlanIndexEntry.cs`）：每个文件读一次字节、算 SHA-256，只读文件头里的 `planId`、`resource` 与 `attachments`，不解码、不解析、不进内核；`level`、`player` 挂载的 reference 与 `map-object` 挂载的 `level` 字段是这份计划能运行的关卡，任何别的挂载（或没有挂载）表示每关都载入，文件头读不出来的文件也按每关载入，由内核解析时给出原本的拒绝码。读不了的文件（`plan-path`、`json-size`、`invalid-json`）在启动时各写一条 `plan.rejected`。选好关卡时（ForgeCore `GameEvents.ExpeditionSelected`，主机与客机都有）在线程池里读这一关的文件（本关计划、无关卡限制的计划，以及与它们同 planId 的其他副本），纯托管字节；游戏线程在关卡之间读完即一次性采纳，最迟在 `LevelAPI.OnBuildStart` 等待读完后采纳，所以生成开始前计划集已是本关的。采纳先过 Rundown 拒绝与内容包前置（目录完整性按索引里的全部文件判断），再由 `RuntimeKernel.AdoptPlanSet` 换集：字节相同的已载计划原样保留（变量、闸门不动），不在本关的计划走唯一的移除路径，其余才解析；订阅表随之只挂本关计划。文件字节与启动索引不同的按 `content-invalid` 拒绝（改文件要重启或作者重载）。每关采纳写一条 `plan.adopted`（`level`、`files`、`plans`、`waitedMs`、`elapsedMs`）；启动写一条 `level=index` 的 `plan.adopted` 与 `stage=index` 的 `plan.discovery`。单文件超过 4 MiB 按 `json-size` 拒绝，不设文件数与合计字节上限。作者重载（`ReloadAuthoringPlans`）重建索引并重读当前关卡。宿主不把注册清单写到磁盘：ForgeDevelopment 录制时经 `kernel.ExportManifest()` 取，发行用的清单由 `Mods/Release/export-runtime-manifest` 生成。

**内容代次（契约 I-RELOAD）。** 启动与热加载走同一条路：`GameRuntimeBridge.Generations`（`GameBindings/ContentGenerations.cs`，不含游戏类型）把空代次（0）提交成第一代，作者换代把第 N 代提交成第 N+1 代，都经 `ContentGenerations.Commit`。一代分四段：发现（`Discover`：各参与方 `Read` 把读到的东西放进 `ContentDiskSnapshot`，一代只读一次盘）→ 准入（`Admit`：纯函数，得到带摘要的 `ContentPart`）→ 备料（`Prepare`：建好原生对象不发布，返回 `PreparedContent`；任一参与方失败或拒绝，已备好的全部 `Discard`，当前代原样）→ 提交（`PreparedContent.Commit`：短写入加读回，并为放进游戏的每样东西记一个 `ContentLease`；某个参与方提交失败，已提交的按逆序 `Revert`，当前代原样，抛 `ContentGenerationRefusal`）。全部提交后新代生效，各参与方 `Release(上一代的租约)`（默认撤销被替换的租约；同槽位换的参与方覆盖成只放手旧对象），再发「内容已换」事件 `ContentGenerations.Changed`（`ContentChanged`：从几代到几代、摘要、内容变了的参与方）。输入摘要没变的参与方返回 `PreparedContent.Carry(当前租约)`，租约原样带进新代。每一代写一条 `content.generation-committed`（`outcome` 为 startup / reload / redo，detail 含租约数、摘要、换代前后钩子数）；没提交的写 `content.generation-refused`，带拒绝码与参与方。

参与方按注册顺序提交（领域在自己的 `Start` 里经 `GameRuntimeBridge.RegisterContentParticipant` 注册，必须在第一代之前）：

| 参与方                  | 档位                       | 现状（本包这一版）                                                                                                                                                                                                                                                                       | 该档车道要补的                                                                                                                                                                                                                           |
| ----------------------- | -------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `entries`（宿主）       | D1 敌人、D2 武器、行为条目 | 每一代（含启动和重做）扫描、身份预检、向领域消费者派发（`ContentEntryGeneration`，见表下）；表行同槽位换、删，整代一份 `TableRows` 租约；摘要不变就带过去 | 身份预检仍只在启动跑；换装备后的图标不重画 |
| `map`（Map）            | B 侧车、C 原生块与菜单     | 启动与换代是同一个提交 `MapPluginSession.CommitContent`（启动经 Map 登记的宿主回调，换代由参与方直接调）：按入口快照校验包，备好模型登记与生成方案；每个发布的 Rundown 一笔表事务（所有者 `NAinfini.ForgeEngine.Map`，`MapRundownRows`：表里仍是本所有者同内容的行不动，内容变了的同槽位 `Replace`，新的或被游戏丢掉的 `Add`；同一个包上一代发布、这一代不再发布的行在这个包的同一笔事务里 `Remove`，所以行号换了而名字没换的行不算撞名；其余上一代有、这一代没有包发布的行最后统一 `Remove`）；装上新的 `RundownMenu` 实例（上一份菜单先把 `ContentLease.Overwrite` 存的基线写回）；按代次建的侧车半边（扫描覆盖、撤离点、门锁、目标轮次、全局光、固定放置、放置模型）销毁后按新行重建，常驻半边原地换行，房间模板与拆件（Unity 对象）先销毁。站稳后 `Release` 放手被换下的旧行对象、释放上一代的模型登记并重摆 Rundown 页（`RundownMenu.PlaceAgain`）；别的参与方提交失败时 `Revert` 把半边、菜单、模型登记与表行换回上一代。租约：表行、模型登记、Unity 对象、菜单、侧车行集合，只在本代仍是当前代时撤销。重做用同一份输入重写表行与菜单，侧车半边与模型登记不动。摘要不含 `map-generation.forge`；Rundown 按已接受的条目校验，所以文件没变而 `entries` 摘要变了也重新提交。换代里被拒的包（读取时的拒收与 Rundown 校验，经 `RundownPlanAdmission.BeginGeneration` / `EndGeneration`）照启动的路写地图日志与结果文件（提交里的 Rundown 拒绝：校验、菜单位置、原生发布，记在这次发现的 `MapPackageAdmission.RundownRefusals`；发布没成的包另记 `rundown-publication-rejected`；两者都进结果文件该包的拒收行），这一代站稳后换掉上一代的拒绝集，包里的计划随包拒收；不暂停 Forge 内容 | — |
| `map-generation`（Map） | A 生成方案                 | 每一代从自己那次读取编出 `GenerationProgram`。启动用 Map 提交时备好的方案；换代时 Map 也重新提交了，就用它备好的方案（经 `CompiledMapGenerator.RequireInstalled`：请求受支持且生成钩子已装上），Map 沿用了（只有方案变了，含包新增或去掉方案），就取本次读取里已发布 Rundown 的方案，提交时先逐份 `RequireInstalled`（不发布的包不算），不过就整代拒绝；提交时 `CompiledMapGenerator.Activate(plans, 代次号)` 一步换掉，回滚换回上一代的集合，重做沿用。生成钩子在 `Authoring` 第一代后随可选钩组 `map-generation`（`CompiledMapGenerator.EnsureHooks`）装上，所以换代能给原本没有方案的包加方案；建图的 run 读回里记 `content-generation`（代次号与 `programHash`） | — |
| `plans`（宿主）         | 计划索引                   | 摘要不变就带过去；变了换 `_planIndex`、重新 `NetworkBinding.AdoptPlans` 并重读已选关卡的计划，回滚换回旧索引                                                                                                                                                                             | N：换代后联机内容摘要的重新通告与作者入口                                                                                                                                                                                                |

**条目消费者的代次（`GameBindings/GameRuntimeBridge.Entries.cs`）。** `entries` 每一代新建一个 `ContentEntryGeneration`，`Deliver` 先调各领域的 `IContentEntryDomain.BeginEntries`（领域在 `Start` 经 `ContentEntryGeneration.RegisterDomain` 登记），再派发条目，再把上一代发布、这一代没再发布的行一次删掉，最后调 `EndEntries`。领域的条目在派发里照旧安装，表行一律走 `ContentEntryGeneration.Publish(defs, strict)`：派发之外等于 `NativeDataBlockTables.Register`；派发之内上一代发布过的行同槽位 `Replace`，新行 `Add`（重做时表里已丢的行重新 `Add`）。领域把这一代的撤销与放手登记在 `ContentEntryGeneration.Current` 上（`OnUndo` 提交失败时逆序执行，`OnRelease` 新代站稳后执行），`Withdrawn` 与 `Previously` 给出这一代删掉和上一代有过的行。敌人（`EnemyContentRegistration`）：`BeginEntries` 把档案与模型表换回启动基线；`EndEntries` 在预制体已建好时（大厅里换代）对这一代的每个 Enemy 行调 `EnemyPrefabManager.GenerateEnemy` 重新生成并换进 `m_enemyPrefabs`，旧预制体放手时销毁，删掉的行同时拿掉预制体；首次启动时预制体由游戏自己那一轮生成。敌人分配器的生成表 `EnemyAllocator.m_enemyTypeLookup` 与复制列表（`m_enemyReplicationManager`，联机按序号复制）只由 `EnemyAllocator.GetEnemyPrefabs` 在开局时按表序填一次（不能再跑：复制列表会拒收已有的预制体），之后加入的玩家按自己开局时的内容填。所以每一代站稳后（`Generations.Changed`，`EnemyContentRegistration.ContentStood`）按冷启动的规则重排：Map 的 Rundown 敌人行缺预制体的补生成、行已不在的拿掉并销毁；复制列表与生成表整份重建，规则同 `GetEnemyPrefabs`（每项不带同步版本）：先按表序放非 Forge 行当前的预制体，其后按冷启动的追加顺序放 Forge 的行（条目按这一代的发布顺序 `ContentEntryGeneration.Published`，再按 Map 发布的包与块顺序，再按表序），不留指向已删预制体的项。序号只由行和表序决定，不看行现在是哪个预制体对象：原版行的预制体被重建换了对象（数据表重新初始化后别的代码重建全部预制体）照样按原序号重排，表一致的两端得到同一份列表。Map 的 Rundown 敌人行原地换了内容时不重新生成预制体。武器（`WeaponContentGeneration`）：`BeginEntries` 把字段、部件、模型表、手电与噪声换回启动基线；装备目录（`WeaponGearCatalogue`）对上一代有过的 PlayerOfflineGear 行同位换掉目录项，删掉的行从目录与槽位池里拿掉；随后本机和主机的机器人按槽位重装：换掉的装备装新的（`weapon.loadout-gear-reequipped`），被删的装备退到槽位池里第一件非 Forge 装备，本机的上次装备记录一并改写，并记一次 `weapon.loadout-gear-withdrawn`（槽位、被删项、退回项、玩家、装备结果）。

**条目读回（`Authoring/AuthorReads.Content.cs` 的 `EntryRows`）。** 开发控制台 `entries` 指令按名调用的只读读回：Forge 拥有的敌人行（来源是内容条目还是 Rundown 行、有无预制体、生成表位、复制序号）、敌人复制列表按序号的全貌与它的 SHA-256 摘要、Forge 的 PlayerOfflineGear 行（是否在装备目录、所在槽位池）。实现在 `Enemy/Native/Content/EnemyContentReadback.cs` 与 `Weapon/Native/Content/WeaponContentReadback.cs`，不改任何状态。

**生成器从哪里取数据。** Forge 编译生成器（`CompiledMapGenerator`）建图时：房间、区、连接、种子、覆盖率、资源与链接全部来自启动（现在是当代）编好的 `GenerationProgram`（`Programs`，已挂到代次上）；`LevelLayout`、`Dimension`、`ComplexResourceSet` 行在建图时经 `NativeDataBlockTables.RequireBlock` 按 id 现取表里的当前对象；区的种子与覆盖率写进现取 LevelLayout 行里那个 `ExpeditionZoneData` 对象（`run.RestoreInputs` 在 run 清掉时写回）；原版 `LG_ZoneSettings` 直接保存传入的 `ExpeditionZoneData` 引用，`LayerBuildData` 也直接保存 `m_zoneBuildDatas` 列表引用。所以同内容换代时 A 档换方案就够让建图跟新代（游戏里换代后再建图，生成器读回的代次号是新代、房间数齐）。原版建图拿到的就是表里当前那个行对象：在 V1 A1 落地后逐区比对，每个已建区的 `LG_ZoneSettings.m_zoneData` 与当前 LevelLayout 行 `Zones` 里同 `LocalIndex` 元素是同一个原生指针（作者读回 `AuthorReads.ZoneDataSource`）。所以不按代次克隆：改动表行内容的换代由 C 用表事务同槽位换上新行对象，下一次建图读到的就是新对象。

**换代时机与钩子。** 只有作者换代（`Plugin.ReloadContent()`）会提交新代：只在 `Authoring`，且只在干净处（本机是主机、在大厅、`Builder.CurrentFloor` 为空、大厅里没有别的真人，或停在主菜单没有会话；宿主不在派发也不在播回放，`GameRuntimeBridge.CleanLobbyRefusal`），否则以 `reload-lobby-not-clean` 拒绝；`Play` 永不换代（`reload-authoring-required`）。`Authoring` 下第一代提交后把各领域登记的可选钩组全量装上（`GameRuntimeBridge.RegisterOptionalHooks`：Map 的 `MapLazyHooks.EnsureAll` 与生成钩子 `CompiledMapGenerator.EnsureHooks`、Weapon 的 `WeaponLazyHooks.EnsureAll`），换代时不新装钩子；换代的提交记录写换代前后的已打补丁方法数。第一代之后再收到 GTFO-API 的数据表初始化事件时，宿主用 `NativeDataBlockTables.AuditOwnership()` 核对账本（`content.tables-reinitialized`）：行都在就什么都不做；行不在了就用同一份输入重做提交（`redo`），做不到就停 Forge 内容到重启。

**工坊交接、作者指令与一键重进。** `ContentReloadHandoff`（`GameBindings/ContentReloadHandoff.cs`）在宿主 Update 里每秒查一次 `BepInEx/forge-reload/request.json`，格式与结果文件 `result.json` 见契约 I-RELOAD「工坊与游戏的交接」：先核对请求里每个包的 `.forge-installed.json` 摘要（`reload-package-mismatch`），再走 `Plugin.ReloadContent()`，从 `GameRuntimeBridge.LastReloadSnapshot`（这次换代的发现快照）取每个包的拒收行（`entries` 的 `ContentCatalog`、`plans` 的 `PlanIndex.Refused`、`map` 的 `MapPackageAdmission`），答完删请求、整体改名写结果，记 `content.reload-handoff`。内核启动后它以不共享方式打开并持有 `BepInEx/forge-reload/game.lock` 到进程结束，工坊靠打不开这个文件判断游戏开着（打不开时记一次 `running-mark-unavailable`）。作者指令 `AuthorReads.ReloadContent()` 走同一个 `ContentReloadHandoff.Reload`，返回同一份结果文档。请求在关卡里到达时在换代前定下要进的关卡（刚退出的那关），换代成功后交给 ForgeCore `SoloDrop.Rejoin`，下落结束时结果文件再写一次；请求在大厅或主菜单（NoLobby，Rundown 选择界面）到达时只换代，`rejoin.state` 为 `skipped-not-in-level`，不进关。请求里没有 rejoin 字段。请求到达时本机独自主持一局且在关卡里（`Authoring`、拒绝原因只差不在大厅）：先写 `status: leaving`，经 `SoloDrop.Leave` 回大厅，到达楼层为空的大厅或主菜单后 10 秒内变干净就换代并重进刚才那关；回不去（`SoloDrop.LeaveLimit` 60 秒）或回去后仍不干净给 `reload-leave-failed`，不重试，记一行。联机重新通告：`NetworkBinding` 订阅本内核的 `Generations.Changed`，换代后清掉缓存的 Rundown 包、重读当前关卡的内容快照，`PumpContent` 落地时设 ForgeCore 通道的 `Content`，通道据此重新问各玩家。

**只停 Forge 内容到重启。** 表回滚后的所有权审计不过、换代回滚本身失败、或游戏丢掉的内容放不回去时，`NativeContentRefusal.Stop` 记下原因；下一个固定更新宿主挂起并停内核，游戏照常，并在游戏里发一行聊天提示（`suspended.content-stopped`）。

### 执行日志

**内核记录点已接入。** 按 `forge.log` 事件码表，Runtime 归属与内核可见的记录点全部在内核里写出：`registration.rejected`、`binding.registered`、`plan.loaded`、`plan.rejected`、`world.began`、`trigger.fired`、`event.rejected`、`budget.exceeded`、`event.deferred`、`event.cancelled`、`dispatch.started`、`dispatch.finished`、`entry.started`、`entry.finished`、`gate.refused`（这五个只在 trace 写出）、`step.started`、`step.finished`、`entry.stopped`、`observer.failed`、`runtime.suspended`、`runtime.tick_rate`，加上 writer 自己的 `log.level`/`log.dropped`。`adapter.*` 等适配器事件码定稿后再做，四个领域自己的原生诊断码见各自一节。SDK 合同见 [Framework README](Framework/README.md#执行日志-sink-与级别)。

`runtime.tick_rate` 是宿主读游戏再交给内核写的专用记录：宿主绑定在世界纪元建立时读 `Time.fixedDeltaTime` 并算出 tick 率（合同 §3.2「时间单位」：一个 tick 是主机的一次 `FixedUpdate`），框架层只带 `fixedDeltaTime`/`ticksPerSecond` 两个数、不引用 Unity。它是 info 级，默认 `Logging.Level = error` 下不写；游戏内核对它只需进一关并把 `NAinfini.ForgeEngine.cfg` 的 `[Logging] Level` 设为 `info`。

**宿主自己的结构化记录点**（码在码表 `catalog/authoring/diagnostic-codes.json`，`RuntimeHostLogCodes` 的常量在构建前由它生成，网络的几条由 `Network/NetworkLogPoints.cs` 写，经内核的宿主专用入口 `LogRuntime` 以 Runtime provider 写出，先问级别再构造，关闭时不分配；只按角色称呼对端，不写 SNet 会话号）：`runtime.gate`（trace，`CanExecute` 各分量与游戏状态名有变化时一条）、`runtime.checkpoint`（info，检查点捕获、跳过与读档恢复，带文档字节数与摘要、读档后回读是否与保存一致、重载的计划数）、`net.session`（info，挂接与脱离会话：`phase`、`role`）、`net.handshake`（info，每个对端的握手判定；`content-audio-*` 另逐个列出本机资源文件）、`net.world-epoch`（info，内核每次进入一个世界纪元：`phase`、`epoch`、`reason`）、`net.request`（trace，回答或汇总的每个命令请求：`role`、`tier`、`status`、`code`）、`net.variables`（trace，每份发出或应用的状态体一条：`phase`、`body`、`epoch`、`bytes`）。ForgeDevelopment 的记录器按 `trace:forge/<码>` 采集它们。

归属按合同的归属规则：`step.started`/`step.finished` 与 `trigger.fired` 归该 binding 所属的 provider，`binding.registered` 归被注册 binding 的 provider，`observer.failed` 归该观察者的 provider，`event.cancelled` 与其余 `event.*`、`plan.*`、`entry.stopped`、`budget.exceeded`、`world.began`、`runtime.suspended`、`registration.*` 归 Runtime。级别由码本身决定，只有 `step.finished` 按结果分级：`failed` 或 commit 为 `unknown` 记 error，其余记 info。`registration.rejected` 的 `subjectProvider` 是被拒绝的 provider；成功的主机迁移不写挂起，只有原版 `ForceExitToLobby` 兜底写一条 `runtime.suspended/host-migration-failed`。

**内核是唯一来源。** 事件与步骤结果直接交给 sink，宿主不订阅 `TickResult` 把命令/事件回执转成 BepInEx 警告，也不逐 tick 镜像观察者故障。error/info 不带 `inputs`，调用点不拼接字符串、记录是只读结构按 `in` 传递；内核侧只保留 trace 级记录点；逐节点行为追踪经 `RuntimeKernel.ObserveBehaviorTrace` 给观察者，当前没有包订阅（ForgeDevelopment 的记录器不再抄它）。

`RegisterModule(RuntimeModule, RuntimeLogLevel)` 的级别是必填参数，不放进 `RuntimeModule`：四个领域启动时把引擎 cfg 的级别交给内核，第三方插件给自己的级别。级别表因此覆盖 Runtime 自己（`forge.runtime`）以及每个已注册的 provider；注销即移除；注册时先发布新表、再写该 provider 的 `binding.registered` 记录，所以每条记录携带的表都已经列出它自己的 provider。未注册的 provider 仍以 `log-provider-unregistered` 拒绝，不给默认值。Runtime 宿主注册的内置 provider 是 CombatContracts、ControlContracts、VariableContracts、TriggerContracts、StatusContracts、ObservationContracts 六个，加上跨域原子的 Trigger 提供者 `forge.module.trigger`（`Native/Trigger/TriggerHost.cs`，代码在 `Framework/Trigger/`）与跨域共享表现提供者 `forge.module.presentation`（`Native/Presentation/PresentationHost.cs`，声明在 `GameBindings/Presentation/PresentationContract.cs`）；它们走只对宿主程序集可见的内部路径取 Runtime 的级别。SessionContract 另在宿主自己的 provider ID 下注册会话 binding。

`[Logging] Level` 接受 `off`、`error`、`info`（大小写与首尾空白不敏感），默认 `error`，按原始文本解析，其他值（包括 `trace`）在注册之前失败。改动需要重启。`Runtime.Mode = Off` 时不启动任何领域；依赖 Runtime 的第三方插件在自己的 Load 里直接返回。

宿主的 `Logging/RuntimeLogWriter.cs` 实现 sink，写 `forge.log` JSONL：

- 惰性启动：第一条被接受的记录才创建后台线程、目录和文件。没有记录就没有线程、目录、文件和控制台输出。
- 文件是 `BepInEx/forge-logs/<yyyyMMddTHHmmssZ>-<4 位十六进制随机>.jsonl`，UTF-8 无 BOM，`\n` 换行，`CreateNew` 不覆盖。新文件建好后按总字节清理：新文件按单文件上限（256 MiB）计，旧文件从新到旧依次保留到总量 1 GiB 为止，第一个放不下的及更旧的删除，只删该目录顶层的 `*.jsonl`。
- 第一行是 `log.level`（level 为 info，带 `levels[{provider, level}]` 与 `elevated`），与第一条真实记录一起写出。级别表每变一次都会发布新快照（后面的记录因此带上最新的 provider 列表），但文件只在 tier 变化时补一行 `log.level`：首行之外最多再有一行提级行，与合同「一个文件最多两行」一致。
- 限流：普通档每 tick 256 行、队列 8192；提级档每 tick 4096 行、队列 65536。超限的记录丢弃并计数，下一条被接受的记录前或停止时写 `log.dropped`（带 `count`，level 为 error）。`log.level` 与 `log.dropped` 不受限流，因此队列最多可超出上限 2 项。
- 单文件 256 MiB（预留一行给最后的 `log.dropped`）。到达上限后停止写文件，下一条被内核线程接受的记录前报一次 `log.dropped`，停止时在文件末尾写带未写条数的 `log.dropped`。
- error 与 info 通过 `ManualLogSource` 镜像到控制台，trace 不镜像。镜像发生在调用 `Write()` 的那个内核线程上（BepInEx 会把消息同步分发给所有 `ILogListener`，监听器可能在未注册线程上碰 IL2CPP 对象）；后台线程只做序列化、写文件和保留清理，一次 BepInEx 调用都没有。文件建不出来、写失败或保留清理失败时，后台线程只记计数与原因，由下一次内核线程上的 `Write()`（或 `Dispose()`）按合同写成 `log.dropped`，message 里带具体原因，不再另造通道。
- `GameRuntimeBridge.Stop` 在 `StopRuntime` 之后结束写入，最多等 5 秒；超时时报告未写出的条数。进程被强杀时，队列中未写出的记录和文件上限的最后一行 `log.dropped` 会丢失。
- 限值只是内部构造参数，供测试注入，不对玩家开放配置。

共享原生入口计数 `native-hook.shared-counters`（Info，每个入口/阶段/订阅者一行）只在生命周期边界写：`subscribed`、`level-entered`、`world-ending`、`lease-ended`、`runtime-stopping`；关内没有周期行，也没有每帧轮询。H12 取证要关内读数时，用 ForgeDevelopment 命令 `runtime-shared-counters-now`（调用 `NativeHookRegistration.LogSharedCountersNow`）在检查前后各写一次 `boundary=on-demand` 快照。字段：`installations` 是该入口的物理安装数（应为 1）；`callbacks` 是这一行 `consumer` 的订阅租约在当前租期收到的次数；`providerCallbacks` 是该入口进程累计被调用的次数（含无订阅者时），只比较同一租期内两次快照的差；`phase` 是订阅所在阶段，`consumer=none`、`status=no-subscribers` 只表示该阶段当下无订阅者，不能当作整个入口的订阅者数。



### 公开库

ForgeEngine 依赖 ForgeCore（含界面、声音与画面部分，合同 §1.4），它随基础包发行集一起发行（`Mods/Release/release.json` 的 `libraries`）。首个固定帧的发行核对（`release-mismatch`）对基础包要求已装版本与内嵌 `release.json` 精确一致，对 ForgeCore 只要求不低于其中写的最低版本（库独立发行，装了更新版不挂起）。四个领域的用法：配置一律经 `ForgeCore.Settings`，数据块事务用 `ForgeCore.NativeDataBlockTables`，装枪资源用 `ForgeCore.Gear.Prepare`，游戏文本语言的修订号用 `ForgeCore.GameEvents.LanguageRevision`。

### 诊断不在宿主内

生成、空间、性能、异常聚合与报告都在 [ForgeDevelopment](../ForgeDevelopment/README.md)，宿主程序集不引用 Development。作者工具交付与验收见唯一开发计划 §6 U-DEV-MOD 和 §7。

### 网络层

传输、成员表、握手、世界编号、恰好一次的请求、主机表和每帧发送预算都是 ForgeCore `Net` 的（[ForgeCore/DEVELOPERS.md](../ForgeCore/DEVELOPERS.md) 的 Net 一节）。引擎没有自己的传输、成员状态机、纪元同步、去重账本、分片、协议版本号或流量计量；流量读 `ForgeCore.Net.Traffic`。引擎在通道 `NAinfini.Forge` 上说什么写在 `Network/EngineNet.cs`（纯逻辑，不读游戏类型，离线测试直接跑它），游戏一侧的事实（本机玩家与房主是谁、玩家槽位地址、内容快照、收到的东西从哪个安全点进内核）由 `GameBindings/NetworkBinding.cs` 提供。计划执行只有主机权威，内容不一致只挂起客机。整个包只有这一条通道：四个领域要各端一致的东西也在它上面，消息名带领域前缀（见下表之后）。

`GameRuntimeBridge.Initialize` 在哈希校验通过并 `BeginWorld` 之后调用 `NetworkBinding.Start()`，`GameRuntimeBridge.Stop` 与 `Plugin.Load` 的回滚链都会 `Stop()`（幂等）。Off 模式与启动被挂起（`startup-failed`）时不会 `Start`，通道也就不开。**通道一个进程只开一次**：第一次 `Start` 时 `Net.Open` 并建 `EngineNet`，六条路由此时全部登记；`Stop` 只把内核从通道上摘下并让通道 `HoldContent`，再次 `Start` 不重复登记。领域经 `NetworkBinding.Channel` 拿到同一条通道，在自己启动时登记自己的消息；网络绑定在任何领域启动之前就已开通道。会话读数（本机玩家、是否房主、房主是谁）只在 `RefreshSession` 一处读：SNet 的 `OnPlayerJoin` / `OnPlayerLeave` / `OnPlayerEvent`、关卡进入和 ForgeCore 宣告的每个世界都会触发它。

| 路由 | ForgeCore 工具 | 走什么 |
| --- | --- | --- |
| `state` | 可靠有序 `Bytes`，版本 3 | 房主的变量状态流：二进制增量给所有人，整份 JSON 文档只给欠它的玩家；客机的索取 |
| `present` | `Request`，版本 5，64 KiB | 表现步骤，房主对每个收件人各问一次；指令末尾带收件人自己那次预测的令牌（没有为 0） |
| `command` | `Request`，版本 6 | 领域命令，客机问房主 |
| `owner` | 可靠 `Bytes`，版本 4，64 KiB | 所有者指令：房主的 apply 带整份请求，持有者的 result / ended 只带指令编号、世界编号和结果 |
| `step` | 可靠 `Event<WorldStep>` | 房主在同一个世界编号里的步进 |
| `migration` | 字节行表 `Table<int>`，版本 3 | 迁移镜像，每段一行 |

消息布局由 ForgeCore 的格式指纹比较（路由名、送达方式、版本号）。改了某条路由的体，就把那条路由登记处的版本号加一；领域命令的载荷由各端点自己定，改了任一端点的载荷也加 `command` 的版本号。版本不同的两台机器握手即被拒（`protocol-mismatch`，指出是哪条消息），不会进关后才读错。

**客机本地预测。** 客机上报自己玩家的事实（交互点的开始和完成，以及第一个命中是地图物件的一枪）时，同一刻把这件事在本机会播给自己的表现步骤先播一遍，不等房主来回。房主照旧是唯一的执行方：它准入事实、走计划、给每个收件人发指令；发给上报者的指令带回上报时的令牌，客机按令牌、计划、步骤对上账就用当时的结果回答，不再播第二遍。

- 能预测的步骤在客机第一次上报该入口的事件时判定一次：入口开头连续的表现步骤，步骤不带时长、花费、声音或播放跟踪、句柄、提升参数和结果判定；输入只能是计划常量、事件端口，或者接收者端口直接接「全部玩家」选择器（`forge.selector.target.players`，中间隔任何筛选都不算）；入口不带触发选项、消息名，挂载只有不问主体的范围类。入口分叉时只沿每一步的第一条后继走，所以只有第一条分支开头那一串会预测。其余一律等房主的指令。
- 发布方两半各一个调用，都在 `EventWriter` 上：房主发布时用带上报玩家和令牌的 `Raise`，客机上报后用同一张表写同样的值再调 `Predict`。令牌由双方都已有的量算出，不为 0：交互点用请求序号和阶段，线上不另带；一枪用武器上报里那一枪的编号加一，射击记录总带这个编号。客机写的值必须和房主发布的一样，房主自己算的值（如取消时的进度）不预测。
- 只有房主说得出的值，客机不写：读到没写的事件端口的步骤不预测，其余步骤照常。一枪的装备就是这样（客机说不出房主给那把枪的引用）：射击事件在客机上不带装备、范围留空，所以挂在关卡或地图物件上、靠目标端口认领的入口能预测，挂在装备上或读装备端口的入口等房主。第一个命中是敌人或玩家的一枪不预测；房主重判目标后拒掉的一枪留下一条 `prediction-unconfirmed`，已播的不撤回。
- 预测是可有可无的一步：发布方把写表和 `Predict` 包在自己的 try 里，抛出的任何异常交给 `RuntimeModuleHandle.PredictionFailed`，按触发器每关记一次 `prediction-failed`（带原因）；已发出的上报和游戏自己的开火、交互不受影响，这件事等房主指令照常播。
- 本机玩家不在接收者里的步骤不预测。3 秒没等到房主指令的预测结案并记一次 `prediction-unconfirmed`（房主拒了这次上报，或没有入口认领）；同时未结的预测最多 64 条，满了不预测并记 `prediction-capacity`；换关清空。换房主时在途事件上的令牌不随迁移镜像走：那一拍触发的步骤会在上报者机器上多播一次并留下一条 `prediction-unconfirmed`。

**内容身份。** 两边是否相同由通道握手判定；`Network/NetworkHandshake.cs` 只回答“本机的内容是什么、和对方差在哪”。计划集取启动的计划索引（不是内核已载的计划，内核只载当前一关）：每个有 `planId` 的文件一行（`planId`、`resourceId`、`resourceRevision` 与文件字节的 SHA-256；同一 planId 的副本字节不同时整组不进握手，由本机准入拒绝），按当前 Rundown 用到的包筛选。内容快照在工作线程里读完后，`NetworkBinding.PumpContent` 把 `PeerIdentity`（运行时 ID、运行时与 API 大版本、游戏构建、计划条数、32 字节计划集摘要、内容快照、包清单）写进通道的 `ContentDetail`，把 `NetworkHandshake.ContentText()` 写进 `Content`，通道随即对每名玩家重新判定；采纳计划集之前和停止之后通道 `HoldContent`，不判也不被判。通道拒绝一名玩家时（`Refused`），`NetworkHandshake.Refuse` 拿对方的 `ContentDetail` 按固定次序说出第一处真实差异（运行时、缺哪个包或哪类文件不同、计划集），只有消息布局不同时报 `protocol-mismatch`，写一条 `net.handshake`；`content-audio-*` 另把本机资源文件逐个列出。被自己房主拒绝的客机以该拒绝码挂起（`GameRuntimeBridge.Suspend`，另写一条 `network-suspended-by-host`），原版游戏照常；房主只是不再向那名玩家发任何东西。

**世界纪元。** 内核纪元 = ForgeCore 世界编号 × 256（`NetworkProtocol.WorldSpan`）+ 本机在该编号里的步数。`Net.WorldChanged` 在安全点进 `GameRuntimeBridge.NetworkWorldChanged`：检查点代不动内核的世界；关内换房主是迁移（见下）；其余每个新编号都是内核进入的下一个世界（建关为新世代，其他为关卡清理）。房主自己挂起没有新编号，内核步进一格，房主用 `step` 告诉客机，之后才握手成功的玩家在 `PeerSynced` 时补发。会话之内内核纪元只升不降。客机进入新会话后收到的第一个世界如果低于本进程内核所在的纪元（之前在别的会话里玩过，或在会话外自己步进过），内核从房主的编号重新计数（`RuntimeKernel.RestartWorldCount` 只放行紧接着的那一次 `BeginWorld` 或 `FollowWorld`），同时丢掉之前收到的状态体并向房主重新索取整份文档，写一条 `net.world-epoch`（phase `restarted`，reason `session-restart`）；换房主期间不重新计数。同一会话里再收到更低的编号（客机抢先到了一个房主没有发过的编号），关内仍以 `stale-world` 挂起到下一关。客机先建关、后听到房主编号（会话在建关之后才接上，或房主的编号不是客机抢先到的那个；房主最后一次变动是建关或检查点重载）时，关卡和关里摆好的东西都留着，内核不开新世界：`RuntimeKernel.FollowWorld` 把自己的镜像在房主的纪元下恢复，各域按主机迁移的方式跟随（生命周期 `WorldChanged` 的 `IsMigration` 为真），写一条 `net.world-epoch`（phase `followed`）；内核持有无法成像的状态时记 `world-follow-refused <码>`，退回开新世界（`net.world-epoch` phase `moved`、reason `level-kept`）：这次 `WorldChanged` 不是迁移，各域照新世界清掉自己的状态，但关卡留着，`GameRuntimeBridge.KeepsBuiltLevel` 在这一次通知里为真，地图生成器据此把正在进行的生成按同一份计划换到新编号继续摆完（`CompiledMapGenerator.WorldBegan`）；生成记录对不上被保留的世界或关卡时记 `map.generation-world-unfollowed`（error），未建完就以 `gen-stale-generation` 拒绝生成、退出本关，不留半截的图。各域的缓存只认「是不是内核当前的世界」，不按大小比较世界纪元。每次移动写一条 `net.world-epoch`。

**读档。** 读档不换世界：内核纪元不动（ForgeCore 的检查点代加一），同一场远征继续；读档后纪元上升一格只会是恢复失败、房主挂起自己步进的那一格。存档时机就是原版的存档时机：原版只在检查点门的扫描完成时存（`LG_SecurityDoor.LG_CheckpointScannerJob` 的完成回调先 `CheckpointManager.StoreCheckpoint` 再 `SNet_Capture.CaptureGameState(Checkpoint)`，每扇门一次、位置各不相同），GTFO-API 的 `OnCheckpointReached`（门锁位置变了）与它一一对应，房主在这一刻拍内核快照。行为卡「立即保存一个检查点」（`SessionActions.CheckpointSave`，只在房主执行；原版的 `AttemptInteract` 在客机上只报错、不转发）做的就是那个回调里房主的三步，次序相同：`CheckpointManager.AttemptInteract(StoreCheckpoint, anchor)`（`StoreCheckpoint` 静态入口的函数体就是这一句；`OnCheckpointReached` 在这一句里触发，内核快照随之拍下）、`SNet.Capture.CaptureGameState(Checkpoint)`、`SendBuffer(Checkpoint)`（原版自己把缓冲发给其他玩家），所以同一次存档里游戏缓冲和内核快照都拍；内核快照是这张卡执行到那一刻的状态，同一条流程里排在它后面的步骤写下的值不在存档里。只发第一句不会留下可读的缓冲。存成的判据是复制状态读回「最后一次交互是存档、位置是 anchor」并且 `GotBuffer(Checkpoint)` 为真，报 `checkpoint-store-issued`；读不回报 `checkpoint-store-not-observed`，原版调用抛异常报 `native-commit-exception`（两者都是提交未知）。anchor 与游戏当前持有的门锁位置相同（原版的 Vector3 相等）时，发令前以 `checkpoint-unavailable` 拒绝、什么都不写（与「没有检查点管理器或抓取器」同一个码）：这种存法只会重抓游戏缓冲而不触发存档事件，内核和各包的快照会落在游戏缓冲后面；想再存一次就换个位置（例如接玩家当前位置）。读档会把复制状态放回存档时的位置，所以读档后在原位置再执行这张卡同样被拒绝，此时那一份存档本来就还在。恢复（`RuntimeKernel.RestoreCheckpoint`）放回变量、触发门计数和各状态源，并遵守两条规则，状态源各自不再另作处理：

- 事件身份在「同一世界、两次读档之间」唯一。恢复把这个世界此前接纳过的事件身份全部忘掉（`EventQueue.ForgetEvents`，租约与调度的身份不动），并把重放下限提到当前 tick，读档前那条时间线上的事件再发来按 `event-stale` 拒绝。所以事件来源的序号无论随存档放回、随重建归零还是接着数，读档后发出的下一件事都是新事件，不会撞上读档前的同名事件被 `event-id-conflict` 拒绝。
- 状态源放回一份状态时，其中一行点名的对象已经不在、或者已经换了一条命（读档会重建全部敌人；放置模型存档后可能被重新摆过），这一行按名丢弃并记一条 info，其余行照常放回；只有格式不对、世界不符才整份拒绝（拒绝会回滚已放回的状态源并挂起运行时）。敌人动作状态丢弃的行记 `enemy.action-life-retired`，放置模型位移丢弃的行记 `map.placed-model-state-row-refused`（`reason=life-ended`）。这与内核丢弃敌人作用域变量和按实例计数的触发门是同一个语义：读档后的敌人是新的一条命，要挂在它身上的东西由生成时的触发重新加上。

**状态流。** 房主每次推进后：有变量变化就把增量广播给所有人；对每个欠整份文档的玩家单发整份（导出一次，多人共用）。欠账只在三处产生：玩家握手成功（迟加入、重连）、ForgeCore 的世界变了（新编号、检查点代、换房主，此时所有人都欠）、客机索取。没有任何握手成功的玩家时什么都不导出。客机在拿到整份文档之前不收增量；等了 100 个 tick 还没来，或者整份文档应用失败，就向房主发一个字节的索取。整份文档是内核的 JSON 文本（和检查点同一份）；增量是二进制（`RuntimeVariableStore.WriteDelta`，原生状态每个来源或整份或 `StatePatch` 补丁，补丁带基准修订，对不上按 `native-state-base` 拒收并重新索取），截断或格式不对按 `format-invalid` 整条拒收。每个体带导出时的内核纪元：早于本机世界的丢弃并报 `network-state-refused`，晚于的留在队列里等世界到。应用在安全点、在一切派发之外，失败原子回滚。

**表现步骤。** `EngineNet.Present` 对每个收件人一行：槽位没有玩家（`presentation-recipient-missing`）、两个收件人指向同一名玩家（`presentation-recipient-ambiguous`）、对方握手未成（`handshake-unmatched` / `hello-unsent`）、输入超过 64 KiB（`payload-too-large`）当场拒绝；本机玩家直接执行；其余每人一个 ForgeCore 请求，12 秒内没有回答记 `presentation-result-timeout`，对方离开记 `presentation-recipient-left`，世界变了记 `stale-world`，对方已为前一任房主执行过记 `migration-commit-unknown`（提交未知）。全部到齐后，每台机器的回答当作一行，按 `ResultAggregation.Presented` 汇总成一个 `CommandResult` 交回内核（只进行为跟踪，内核当场给出的仍是派发回执）：一台机器写上、另一台真实拒绝时是 partial 并带那台机器的拒绝码；`outputs.rows` 每个收件人一行，`presentationRecipientsComplete` 说明是否每人都真的回答了。表现不改世界状态，所以声称提交、带事实或不是合法结果的回答按 `invalid-presentation-result` 拒绝。在途的表现步骤不进迁移镜像。

**领域命令。** `DomainCommands.Register(endpoint, 执行器)` / `RegisterResults(endpoint, 观察者)` / `Send(endpoint, resourceId, lifeEpoch, payload)` 是领域包的入口（载荷上限 512 字节，资源 id 上限 128 字）。房主自己提交时当场交给执行器；客机提交时问房主（没有握手成功的房主时 `Send` 返回 false），房主在 `DomainCommandRouting.Execute` 里执行一次并回答，没有执行器的端点答 `no-consumer`，执行器抛异常答 `domain-handler-exception`（提交未知）。回答只在客机仍处于提交时的那个世界时交给观察者；没有等到回答时观察者得到 failed / unknown：世界变了 `stale-world`，超时或房主离开 `command-unanswered`，新房主的账本里已有这次请求 `migration-commit-unknown`。

**所有者指令。** `OwnerCommandChannel` 把房主的所有者步骤发给持有对象的那一台机器，把它的结果和结束通知带回房主；执行与生命周期仍由内核的 `OwnerCommandLedger` 决定，通道只记住“哪条请求发给了谁”，所以没被发过这一步的玩家答不了它。超过 64 KiB 的 apply 拒绝（`payload-too-large`），超过的结果改报 failed / unknown `owner-end-budget`。内核与通道两侧的所有者账本只保留已完全结清的请求中最近 1024 项（终态、取消已确认、没有 handle: 清理槽、没有未知结局），按整 tick 淘汰；淘汰线以下的重放以 `owner-command-stale` 拒绝，未结清的项永不淘汰；世界结束时账本整体清空。

**主机迁移。** 原版每次 `CaptureGameState(Migration_A/B)` 时房主把内核镜像写进 `migration` 表：行键 = `buffer × 4096 + 行号`，0 是头（格式版本与各段名称），1 是检查点，2 起每行是内核镜像的一个顶层段；段的字节没变就不重发。另有一行（键 −1）是房主已执行领域命令的账本，随每次捕获更新，每个客机收到就装进自己的请求账本，所以被提升的玩家从收到的第一条重问起就能回答“已执行过”。一个 buffer 的镜像超过 4 MiB 或 4094 段时这次捕获被放弃（`host-migration-capture-refused`，表里该 buffer 的行删掉），不挂起；只有真的迁移召回这个 buffer 时才以 `host-migration-state-missing` 拒绝。换房主时每台机器暂停内核，等原版召回 buffer、新房主的世界编号到达后，从自己持有的表里重组同一份镜像并在新纪元下恢复（等不到则约 30 秒后按缺状态拒绝）；恢复后新房主接管镜像里未结清的所有者请求，并给所有人重发整份状态文档。原版 `ForceExitToLobby` 兜底写 `host-migration-failed`。

迟加入的整份文档含变量、`g-once`、gate 镜像与各领域登记的原生状态源（`DomainStateChannel.RegisterNativeState` / `RegisterRecipientState`）；迁移镜像另含 gate 的 debounce、效果剩余 tick、句柄元数据、排队首跳与内核去重账本。领域自己要各端一致、又不属于这份文档的状态，在同一条引擎通道上用带领域前缀的消息：`map/trigger-zones`（字节行表）与 `map/timed-round-time`（主机表），`player/text-language`（每名玩家自己的一行大厅行），`weapon/projectiles`（不可靠有序消息）；细节见各领域一节。握手只判这一条通道，被通道拒绝的玩家这几条也收不到。生命周期观察者累计故障 `RuntimeKernel.MaximumLifecycleObserverFaults`（8）次才被移除，单次异常只记日志。

钩子证据表（各领域的 `<领域>/evidence/*-hooks.json`）只有一个重新生成工具：`tools/RegenerateHookEvidence`，第一个参数选领域（`map`、`player`、`enemy`、`enemy-behavior`、`weapon`），其余为 `<BepInEx> <artifacts> <hooks.json> <dump.cs>`；它读已构建的 `ForgeEngine.dll`（按命名空间取该领域的类型）与游戏 interop 程序集，只改该领域自己的表。

`enemy-behavior` 只重写 `Enemy/evidence/enemy-behavior-hooks.json` 里随 Forge 代码移动的源码位置，从已构建的 `ForgeEngine.dll` 与它的 pdb 读调用点（只算 Enemy 领域与引擎 `GameBindings` 的调用）：`hookMembers` 里写成调用点格式（`文件.cs:行 方法; …`）的 `usedBy` 与对应的 `used by …` 观察、这些行的 `declared by … at dump.cs N` 与 `body at 0x…` 观察（取该行自己的 `dumpLine`、`nativeRva`），以及 `readFields` 备注里 `X reads this field` 之后的调用点。表里其余列（id、role、声明、RVA、dump 行号、偏移、观察和写成文字说明的 `usedBy`）是审核过的手写输入，工具只读不改，缺了就报错；它不读 dump.cs，最后一个参数可传空串。改过 Enemy 领域或 `GameBindings` 里调用游戏成员的代码后：

```powershell
dotnet <artifacts>/tools/ForgeEngine/bin/RegenerateHookEvidence/release/RegenerateHookEvidence.dll enemy-behavior <BepInEx> <artifacts> Mods/ForgeEngine/Enemy/evidence/enemy-behavior-hooks.json ""
dotnet <artifacts>/bin/EnemyNativeEvidence/release/ForgeEnemy.NativeEvidence.Tests.dll NativeEvidence --generate <BepInEx> <artifacts>/bin/ForgeEngine/release/ForgeEngine.dll Mods/ForgeEngine/Enemy/evidence/native-api-20403457.json <输出.json>
```

第二条重锁 `native-api-20403457.json` 里这张表的哈希，把输出覆盖回原文件。`verify.py` 的 `hook declaration inputs` 一步每次都在本次运行的副本上重出这张表，与库内的不一致就失败。

原生证据的身份只有一处：`Mods/Testing/SourceTools/NativeIdentity.cs`。游戏本体锁 Steam build、`GameAssembly.dll`、`global-metadata.dat`、dump.cs 的整文件哈希；interop 程序集每次生成的 MVID、PE 时间戳和校验和都会变，所以锁 `InteropSignature`（全部类型、字段、方法、参数、属性、事件及其特性、布局、常量和 IL 的哈希，证据字段 `signatureSha256`）。每次检查先跑 `interop.detection`：只换 MVID 指纹不变，改方法名、参数类型、删字段、改方法体必须变。PowerShell 工具通过 `Mods/Testing/SourceTools/InteropSignature.ps1` 取同一份实现。

### 已知边界

断线重连按原版 drop-in，不走迁移恢复。numeric lease 只是 SDK 内部的重算贡献记录，属性回写由各领域完成。`BeginWorld` 仍不是检查点或网络恢复；迁移由 `RestoreHostMigration` 在原版 recall 完成后单独接管。没有 Control/Selector 嵌套图的完整 VM。

## 安装与配置

需要 BepInExPack GTFO 3.2.4。安装产物只有一个 `ForgeEngine.dll`（内核、宿主与四个领域；发行集 `../Release/release.json` 的 `NAinfini-ForgeEngine` 1.0.0 按此列出），`ForgeEngine.Framework.dll` 不进安装包。

配置文件是 `BepInEx/config/NAinfini.ForgeEngine.cfg`，整个包只有这一份，四个领域没有自己的 cfg：

```ini
[Runtime]
Mode = Play

[Logging]
Level = error

[Rundowns]
UnlockAllLevels = false
```

`[Rundowns] UnlockAllLevels`（默认关）给做图、测图的人用：打开后选关页上所有 Rundown 的所有关卡都可进。它只置原版自己的全开开关 `Global.AllowFullRundown`（原版 `CM_PageRundown_New.UpdateTierIconsWithProgression` 把它并进每关与每层标记的解锁判定），不伪造通关、不写 ForgeCore 的进度文件。写入时机是 Map 已挂的 `CM_PageRundown_New.OnRundownProgressionFileUpdated` 前缀的第一步（`RundownMenu.ApplyUnlockAll`），即选关页读这个开关之前：原版启动时 `GlobalSetup.Setup` 会把开关写回 false，所以不在加载时写。关掉后只在本包写过 true 时写回 false，下一次选关页刷新进度时生效。`Runtime.Mode = Off` 时不装钩子，开关无效。

诊断配置（`[Authoring]`、`[Performance Diagnostics]`）在 `NAinfini.ForgeDevelopment.cfg`，见 Development 的说明；本文件不读这些键。

## 跨包结构与所有权

公共 SDK 与宿主归本包，所以 `Mods/` 下全部模组的跨包结构写在这里。

**依赖方向。** 第三方插件单向引用 `ForgeEngine.dll`，经 `Plugin.Runtime` 取得内核，之后只用公开类型（见「公开类型」）；`Framework/` 不引用宿主或领域，也不含 Unity、BepInEx 依赖。四个领域在同一个程序集里，彼此直接调用对方的会话：Player 的目录 `PlayerHost.Directory` 是全包认玩家的唯一入口，Map、Enemy、Weapon 与 Presentation 都读它；Player 会话没在跑时目录对每次读取以 `player-directory-missing` 拒绝。同一个目录以 `IPlayerDirectory` 发布给别的插件。会话号只有一种拼写：宿主 `GameBindings/PlayerAddress` 给出的玩家槽位，目录的 `SessionOf` 与网络命令路由的本机地址都取它。Weapon 不认门与终端的原生类型，只经 `ResolveEntityInstance("gtfo.map_object", …)` 按种类查询地图对象。源码依赖、运行时能力闭包和发布包依赖是三件不同的事。

**工程。** `ForgeEngine.csproj` 编出唯一的 `ForgeEngine.dll`：GTFO 宿主（模拟时钟、世界与会话桥、启动配置、计划发现 `GameBindings/PlanDiscovery.cs`、日志 writer `Logging/RuntimeLogWriter.cs`，不含诊断）、`Framework/**` 与四个领域的全部源码（任何 `tests/`、`tools/` 目录除外）。`Framework/ForgeEngine.Framework.csproj` 编同一批源码里不碰游戏的部分（`Framework/**` 加四个领域不在 `Native/` 下的源码，清单在 `Framework/Domains.props`），只给服务器准入程序与离线测试用，不进玩家安装包；所以领域里不在 `Native/` 下的源码不能引用 Unity、BepInEx、Harmony 或游戏类型。ForgeDevelopment 没有托管工程。根 `Forge.Architecture.sln` 覆盖 `ForgeEngine.Framework`、`ForgeEngine`、`tests/Contracts`、`Mods/Testing` 与其余 Forge 程序集，宿主的 `Native` 测试工程另行构建。架构验收检查（`tests/Architecture`）：provider 可共存、重复注册以 `provider-conflict` 原子拒绝、注册不启动工作、注销幂等。玩家 SDK 与模组工程目标为 `net6.0`；接口指纹构建工具和检查需要 .NET SDK 8 或更新版本（工具自身 net8.0）。没有 DI 容器、第二套事件总线或状态机框架。

**注册身份。** 只有调用方显式 `RegisterModule(module, level)` 才登记 provider，级别同样是必填；加载程序集不启动任何工作。

| 模块 | provider ID | 版本 | 注册内容 |
| --- | --- | --- | --- |
| Development | — | — | 不登记 provider、不引用本程序集；原生插件 `NAinfini.ForgeDevelopment` 只依赖 ForgeCore，Forge 地图读数经 `Authoring/AuthorReads` 按类型名在运行时找到 |
| Trigger | `forge.module.trigger` | `1.0.0` | 1 条 `evaluate` 绑定（`forge.condition.predicate.compare`）；由 ForgeEngine 插件在启动时内置注册（`Native/Trigger/TriggerHost.cs`），没有独立的包、插件 GUID 或 cfg |
| Presentation | `forge.module.presentation` | `1.0.0` | 4 条 `execute` 绑定（`forge.action.presentation.effect_play`、`forge.action.presentation.point_light`、`forge.action.presentation.audio_play`、`forge.action.presentation.audio_stop`），`presentation` 执行层，观众经 Player 的目录换成会话；特效与临时灯光由 ForgeVisual 画（特效预制体在 ForgeVisual 内嵌的 `forge-vfx.bundle` 里）；逐帧推进是共用帧入口 `forge.domain.frame` 里排在三个领域之后的两项（`DomainWorkPhases`：声音同步每帧 `forge.presentation.frame`，HUD 每个内核 tick 一次 `forge.presentation.hud`）；按播放保留副本的处理器（目前只有特效行，由形状声明 `HandlerShape.KeysByPlay()` 标记）的每条命令带主机给出的播放身份 `$play`（触发作用域、所在间隔 repeat/ramp 序列的实例，不在循环里时再加触发主体的 id 与 life epoch（与顺序无关），触发器没有声明主体时改加事件 id，在同步循环里时加每层循环的当前目标——候选实体的 id 与 life epoch——或轮次，以及该步骤实际执行的 tick），其余表现行不带，接收端按同一标记要求有无；持续特效在接收端按它在每步定长表（最多 512 个播放、1024 槽）里取副本，目标离开列表不会让其余目标换副本，表满时每 tick 至多整理一次闲置行，仍无空位报 `effect-held-budget`；换世界（主机迁移）时副本表保留播放与键、按新世界重新计时；声音行是全包唯一的播放入口：游戏事件走关卡事件入口、点发声器与 Wwise 跟随，自定义声音（`audio` 资源，Ogg Vorbis）经 ForgeAudio 播放，字幕渲染、跟随对象与门警报停止由 Map 的 `MapSoundPlaces` 回答；由 ForgeEngine 插件在启动时内置注册（`Native/Presentation/PresentationHost.cs`），没有独立的包、插件 GUID 或 cfg |
| Player | `forge.module.gtfo.player` | `1.0.0` | 玩家与机器人的 binding 以及 `gtfo.player` 的 resolver、实例解析器与候选来源，由 `PlayerHost.Start` 注册 |
| Map | `forge.module.gtfo.map` | `1.0.0` | 地图对象、关卡对象、关卡流程的 binding 与 `gtfo.map_object` resolver，由 `MapHost.Start` 注册；玩家引用取自 Player 的目录 |
| Enemy | `forge.module.gtfo.enemy` | `1.0.0` | 敌人的 binding、`gtfo.enemy` resolver 与 `enemy-type` 挂载，由 `EnemyHost.Start` 注册 |
| Weapon | `forge.module.gtfo.weapon` | `1.0.0` | 装备的 binding、`gtfo.equipment` resolver 与 `gear-block` 挂载，另登记持有者 provider `forge.module.gtfo.weapon.holder`，由 `WeaponHost.Start` 注册 |
| 共享 combat 合同 | `forge.contract.combat` | — | 5 个 canonical 定义，逐字采用网站目录行，0 个 binding |
| 共享 status 合同 | `forge.contract.status` | — | 1 个 canonical 定义（`forge.trigger.status.ended`）逐字采用网站目录行，加 1 个 binding（结束事实的观察入口）；由内核发布，没有 provider handler |

宿主插件 `NAinfini.ForgeEngine` 1.0.0 只带 5 个主机迁移 Harmony 补丁；其余生命周期由 1 个 GameDataAPI 数据就绪订阅、5 个 LevelAPI 关卡订阅与 2 个 EventAPI 检查点事件订阅承担。

**跨领域所有权。**

| 边界 | 唯一所有者 | 协作者如何使用 |
| --- | --- | --- |
| canonical 语义、端口与修订 | 网站的作者能力目录（`Tools/Forge/build-capability-catalog.ts` 的 `capabilityCatalog()`）；C# 共享合同逐字采用目录行 | 领域只提供自己的 binding，不复制或覆盖 canonical ID |
| 调度、因果、预算、通用状态和结果 | Runtime | 领域提交公开请求，不复制队列或时钟 |
| 执行日志 writer | Runtime | 各包只调用记录接口；Development 只做提级与 trace |
| 传输、握手、世界编号、请求去重、主机表、发送预算 | ForgeCore `Net` | 引擎只开一条通道 `NAinfini.Forge`，四个领域在上面用带领域前缀的消息；谁都不自己实现重传、去重或补发 |
| 主机权威判定、变量状态流、迁移镜像 | Runtime（U-NET） | 领域经 `DomainCommands` 提交、经 `DomainStateChannel` 登记状态源 |
| 合法出生空间与拓扑 | Map | Enemy 提交尺寸、移动、碰撞要求，Map 组合遭遇数量、时机、分布 |
| 地图生成的底层逻辑 | Map | 近期只用原版 geomorph |
| 实体身份与 receiver | 实际拥有该游戏实体的领域模块 | 公共引用含 world/life epoch，不拿原生指针作内容 ID；其他领域经 SDK 实例解析取引用 |
| 装备槽、库存和部署实例 | Weapon | Map 管世界落点与任务物品关联；同一笔成本不能两方各扣一次 |
| 玩家倒地、复活、重生、传送、检查点 | Map 与 Runtime 生命周期 | Heal 不隐式复活，Teleport 不新建 life |
| 诊断与报告 | Development | 普通玩家无采集也能执行玩法 |

source、owner、instigator、recipient 四者不互换。实体种类不决定敌我关系，伤害或治疗极性不决定目标；未知关系、缺失 actor、过期 world/life epoch、不支持的 receiver 都必须给出明确结果。all-target 查询预算不足不得静默截断。定时效果分解为事实 → 选择/条件 → 公共 Pulse/Interval → 普通 Action → 结果，不为每种用途写一个计时器。

**触发卡输出声明角色。** 角色表（`Framework/Entities/RuntimeActorRoles.cs`）仍是事件角色的唯一来源：`source`、`instigator`、`owner` 读同名输出，`event-target` 读 `target`。触发卡另一个名字的单个实体输出可以用 `actorRole`（只收 `instigator`、`owner`）声明自己就是该角色，角色表在登记时把该角色指到这个输出的格子，不复制值、不改事件内容；同一角色有两个输出，或 `actorRole` 写在非单个实体的端口上，登记时以 `context-role-port` 拒绝。现有声明：`forge.trigger.interaction.door_state.actor`、`forge.trigger.interaction.hold_completed.player`、`forge.trigger.combat.damage_applied.source` 是引发者，`forge.trigger.equipment.deploy_completed.actor` 是所属者。按玩家记账的触发门先问引发者（是玩家才算），再问挂载接受的那一个实体。

**选择器的候选。** 对候选集合做选择的行（范围、最近／最远、连锁、排序、关系筛选、二分、区域成员、命名范围）经 `ObservedSpaceNodes.ObserveCurrent` 读候选：内核能证明已经不在的候选（`stale-world`、`stale-entity`）不再属于候选集合，与「存在」判断读成缺失一致；其余读不到的情况（预算用完、观察者拒绝或失败、候选暂时读不出）照旧整步拒绝。锚点、连锁起点不是候选，过期时以它自己的码拒绝。单个实体的读取仍用严格的 `Observe`。

**随机。** `SeededStream.OfEvent` 给没写种子的步骤一个种子：节点、世界和当前触发事件的身份。`forge.modifier.value.random_range` 的 `seed` 可不接，不接时用它；`forge.selector.target.random` 的 `seed_mode: session` 也用它。每次触发各抽一次，同一次触发里重复求值（多个下游、等待之后恢复）得到同一个种子；接了或写了种子时只用那个种子。

**敌人的两个生命内设置各有读卡。** `forge.query.enemy.stationary` 读「设置敌人静止」，`forge.query.enemy.wake_locked` 读「设置敌人能否醒来」挂的锁（敌人此刻睡着或醒着都照实读）；两者都只读敌人生命条目上的标记，已死、已移除或旧世界的敌人以 `stale-entity` 拒绝。

**「全部敌人」可以先缩小再计数。** `forge.selector.target.enemies` 有三个可选输入：`zone`（区域资源）、`center` 与 `radius`（必须一起接）。都不接是全关敌人；接了就由内核 `EnumerateEntityCandidates(kind, scope)` 先按敌人所在区域和到中心的距离缩小，再拿缩小后的个数对 256 的上限。全关敌人数不再是上限的口径；缩小后仍超过 256 个，或都不接而全关超过 256 个，都是 `entity-query-budget` 整体拒绝，不截断。只接了中心或只接了半径是 `missing-field`。网站编译器扁平化时，这张卡的结果只被一串「区域内的候选对象」或球形、锥形「范围内的目标」读取、且对应输入没接时，把那些筛选的区域、中心与半径同时接到这张卡上（`Web/src/forge/candidate-scope-lowering.ts`），筛选卡原样保留；球的保留判断与「范围内的目标」同用 `PointMeasure`，所以不超限时结果不变。

## 公开类型

规则：第三方真的会用到才公开。判据是三条：开发者控制台按名调用的作者读数（`Authoring/AuthorReads`、`BotSlotRequest`）、网站开发者文档里提供方示例（`Web/src/features/docs/developer-docs-example.cs`）用到的类型、以及这些类型公开签名里出现的类型；其余全部 `internal`。Player、Map、Enemy、Weapon 四个领域的类型都是内部的。第三方经 `Plugin.Runtime`（`IForgeEngine`）登记提供方，经 `TryService` 取 `IPlayerDirectory`（`PlayerDirectoryService.Require` 是同一件事的简写，取不到报 `player-directory-missing`）和 `EnemyAgentResolver`，把计划里的玩家与敌人引用换成游戏对象。离线测试经 `ForgeEngine.Framework` 对测试程序集开放的内部访问读其余类型。顶层公开类型共 217 个，嵌套类型跟随外层：

- 根：`Plugin`、`RuntimeMode` —— 宿主入口：`Plugin.Runtime` 取内核，读启动模式、挂起状态与可执行门。
- `Framework/Authoring`：`BlockRow`、`Blocks`、`EquipmentAttachmentPorts`、`ProviderBinding`、`ProviderDeclaration`、`RuntimeKernel`、`RuntimeModuleHandle` —— 声明提供方与绑定、积木表、注册后拿到的句柄。
- `Framework/Content`：`AuthoringRoomHit`、`AuthoringRoomIdentity`、`AuthoringRoomResolution`、`AuthoringRoomResolver`、`AuthoringRoomScope`、`ContentRegistrationResult`、`ObjectEntityResolver`、`PlanCandidate`、`PlanLoadOutcome`、`PublishedBytesCodes`、`PublishedDiscoveryCodes`、`PublishedResource`、`PublishedResourceFailure`、`PublishedResources`、`PublishedResourceSetCodes`、`PublishedTextCodes`、`ResourceRef`、`ContentDataBlockIds`、`ContentIssuedBlock`、`ContentVanillaId`、`ContentIdConflict`、`RundownPacks`、`RundownBlock`、`RundownDiagnostic`、`RundownPack`、`RuntimeContentEntry`、`RuntimeResourceCost`、`RuntimeResourceProvider`、`RuntimeResources` —— 包内资源的发现与读取、资源引用与资源提供方、作者房间解析、计划候选与装载结果、内容包记录（ForgeDevelopment 的测试工程链接 `ContentPackages` 源码用到）。
- `Framework/Diagnostics`：`DiagnosticBudgetSnapshot`、`DiagnosticReservation`、`HookSample`、`HookScope`、`HookSection`、`IRuntimeLogSink`、`RuntimeAbiCodes`、`RuntimeBehaviorTraceRecord`、`RuntimeBehaviorTraceSubscription`、`RuntimeLogCodes`、`RuntimeLogFacts`、`RuntimeLogLayout`、`RuntimeLogLayoutConnection`、`RuntimeLogLayoutDirections`、`RuntimeLogLayoutLimits`、`RuntimeLogLayoutZone`、`RuntimeLogLevel`、`RuntimeLogLevels`、`RuntimeLogPlan`、`RuntimeLogProviderLevel`、`RuntimeLogRecord`、`RuntimeLogResult`、`RuntimeLogTier`、`RuntimeLogValue`、`RuntimeLogValueKind` —— 执行日志的级别、记录与写入接口，行为轨迹订阅；诊断 CPU 预算 `DiagnosticCpuBudget` 与钩子计数 `HookCounters` 已移到 ForgeCore（`Once/`）。
- `Framework/Entities`：`EntityKinds`、`RuntimeActorContext`、`RuntimeFactionRelation`、`RuntimeFactionRelations` —— 实体种类常量、角色上下文与阵营关系。
- `Framework/Execution`：`DeferredCommandToken`、`EffectRestoreHandler`、`EnemyAgentResolver`、`EquipmentHolderMatch`、`EquipmentHolderMatcher`、`EquipmentHolderSubject`、`IForgeEngine`、`MapGenerationReadback`、`ProviderHandleAdoption`、`ProviderHandoverAdoption`、`ResultRows`、`RuntimeCostFault`、`RuntimeEffectContext`、`RuntimeEventView`、`RuntimeServiceBinding`、`RuntimeSubscriptionGate`、`RuntimeTriggerSubscription` —— 内核接口、服务与订阅、延迟结果与效果恢复、迁移交接、敌人解析与生成读回两个委托。
- `Framework/Frame`：`ValueKind` —— 执行帧里值的种类。
- `Framework/Observation`：`EntityAreaResolution`、`EntityPose`、`EntityPoseObserver`、`EntityZoneResolution`、`RuntimeEntityInspection`、`RuntimeEntityQueryResult`、`RuntimeEntitySnapshot`、`RuntimeEventRow`、`RuntimeEventRows` —— 实体观察、位姿与所在区域、事件行。
- `Framework/Policy`：`FirstPulse`、`IncomingDamagePolicy`、`IncomingDamageReceipt`、`IncomingDamageReceiver`、`IRuntimeModuleContinuation`、`MissedPulsePolicy`、`NumericLeaseRequest`、`NumericLeaseResult`、`NumericStateResult`、`PulseSchedule`、`ReducedIncomingDamage`、`RuntimeLifecycleEvent`、`RuntimeLifecycleFault`、`RuntimeLifecycleKind`、`RuntimeLifecycleSnapshot`、`RuntimeLifecycleSubscription`、`RuntimeScheduleHandle`、`RuntimeStartupState`、`RuntimeStateLeaseHandle`、`ScheduleReceipt`、`ScheduleResult`、`StateLeaseReceipt`、`TriggerGateReceipt` —— 生命周期观察、定时脉冲与原生续延、数值租约、来伤规则。
- `Framework/Serialization`：`AttachmentMatcher`、`AttachmentMatcherRegistration`、`AttachmentScopeMatcher`、`BindingSupport`、`CommandContext`、`CommandHandler`、`CommandReceipt`、`CommandResult`、`CommandStatuses`、`CommitStates`、`DispatchResult`、`EntityPresence`、`EntityReference`、`EvaluationContext`、`EvaluationOutputs`、`EvaluatorHandler`、`EventReceipt`、`HandlerShape`、`HandlerSlot`、`OwnerOutput`、`PresentationOutput`、`RuntimeApi`、`RuntimeContractException`、`RuntimeEvent`、`RuntimeFact`、`RuntimeIdentity`、`RuntimeJson`、`RuntimeLimits`、`RuntimeModule`、`RuntimeQuerySession`、`RuntimeVariableEntry`、`TickResult` —— 模块、处理方法与求值器的上下文和结果、事件、实体引用、挂载匹配、接口指纹与推进结果。
- `GameBindings`（玩家）：`IPlayerDirectory`、`PlayerDirectoryService`、`RuntimePlayerLife`、`PlayerBotSlotBegin`、`IRuntimeBotSlotRequest`、`PlayerBotSlotsService`、`RuntimeBotGear` —— 玩家目录（给第三方）与机器人位请求（`Authoring/BotSlotRequest` 用）。
- `GameBindings`（地图与敌人检查）：`IMapCaptureService`、`MapCaptureService`、`IMapInspectionService`、`MapInspectionService`、`MapDoorInspection`、`MapGeneratedConnectionInspection`、`MapGenerationRoomIdentity`、`MapModelInspection`、`MapRoomInspection`、`MapTriggerZoneInspection`、`RuntimeMapRoomLocator`、`RuntimeMapRoomReceipt`、`RuntimeMapTaskMove`、`RuntimeMapTaskMoveSnapshot`、`EnemyNoiseTraceResolver`、`EnemyNoiseHookSource`、`EnemyNoiseInspectionContracts`、`EnemyNoiseInspectionResult`、`EnemyNoiseTraceSource` —— 给 ForgeDevelopment 的地图采集、地图检查与敌人噪声检查。
- `GameBindings`（原生入口与钩子）：`NativeHookDefinition`、`NativeHookDiagnostic`、`NativeHookLease`、`NativeHookOrder`、`NativeHookRefusal`、`NativeHookRegistration`、`NativeObservationDefinition`、`NativeObservationWrite`、`NativeWorkFrame`、`RuntimeWorkLease`、`RuntimeWorkPhases`、`GameEntryPhase`、`GameEnemyDamage`、`GameEnemyDamageCall`、`GameEnemyDamageEntry`、`GamePlayerMembership`、`GamePlayerMembershipCall`、`GamePlayerMembershipEntry`、`GamePostEvent`、`GamePostEventPhase`、`GamePostEvents`、`RuntimeUpstreamEvent`、`RuntimeUpstreamEvents`、`RuntimeUpstreamKind`、`DomainWorldObservation` —— 钩子声明与唯一安装者、帧工作租约、共享原生入口的只读订阅。共享入口的订阅者只有一份登记（每个入口一张按优先级、同级按订阅先后排的世界租约表）：`GamePostEvents`、`GamePlayerMembership`、`GameEnemyDamage` 都在各自唯一一次 ForgeCore Bracket 订阅之上用它；租约在世界换代后第一次被问到时自行结束，回调抛异常的订阅者结束并只报一次。一个包要每个世界重新取一组租约时用 `DomainWorldObservation`（全取或全不取），不自写续租表。
- `GameBindings`（生成与诊断）：`FactoryJobs`、`FactoryJobExecutionObserver`、`FactoryJobExecutionToken`、`FactoryJobGenerator`、`DiagnosticWorkBudget`、`PluginLogging` —— 关卡生成任务的观察、诊断预算、插件绑自己的日志级别；落地计时 `DropTimingMetrics`、生成尝试时钟 `GenerationAttempt`/`GenerationAttemptTiming` 与楼层组件枚举 `GameFloor` 已移到 ForgeCore。
- `Authoring`：`AuthorReads`、`BotSlotRequest` —— Forge 地图的作者读数，开发者控制台按名调用（`call static:ForgeEngine.Authoring.AuthorReads <方法>`）：`Mode`、`WorldEpoch`、`LoadedPlans`、`ReloadPlans`、`GenerationProgress`、`RoomInstanceId`、`ResolveRoom`、`Variables`、`ModelShotCollider`、`BotSlot`（返回 `BotSlotRequest`，轮询 `Ready`、调 `Apply`、再轮询 `Applied`）。参数与结果只用文本、数字与游戏类型，调用方不需要本程序集；不在 Authoring 模式或内核没在跑时抛 `forge-engine-not-authoring`。生成后的布局记录 `GeneratedLayoutRecord`（内部）也在这里。
- `Logging`：`DynamicDiagnosticCodes` —— 运行时拼出的诊断码登记。

- 动作输出接口（`Framework/Execution`、`Framework/Serialization`）：`Status`、`Commit`、`Outcome`、`RowWriter`、`TargetStep`、`EventWriter` —— 处理方法用 `CommandContext.Rows` 逐行写结果、用 `CommandContext.EachTarget` 走多目标骨架、用 `CommandContext.Outputs` 按位置写其余输出并返回 `Outcome`；触发器用 `RuntimeModuleHandle.BeginEvent` 按位置写事件。列和输出位置用生成的 `Blocks.<积木>.Row…` / `Out…` 常量。另有：`context.BeginFact(gate)`（`FactWriter`）按触发器输出位置写命令确认的事实；`Outcome.WithDetail` 带诊断说明；`EachTargetPresented` 是表现类多目标骨架；`EachTarget` 的 `countColumn` 由骨架写接收者总数列；`handle.CompleteDeferredResult(token, ref state, row)` 按类型化的行完成延迟结果（另有四固定列重载）。写法见 `Framework/README.md` 的「动作输出接口」。

新增公开类型要有上面三条判据之一；接口指纹（`RuntimeApi.Fingerprint`）随公开面变化重出。

## Player

玩家与机器人：`gtfo.player` 实体身份、玩家选择器，以及主语是玩家的全部行——生命值与接收、生命期（倒地、救起、死亡、重生、传送）、体力、移动状态、动作锁定、指令、输入与按键、事件、机器人指令、属性修改、瞄准与携带物读取、队友头顶信息，以及按玩家呈现的镜头震动与屏幕液体。源码在 `Player/`，命名空间 `ForgePlayer`、`ForgePlayer.Native`。关卡世界与流程在下文 Map 一节，装备与背包在 Weapon 一节；其余领域都读这里登记的玩家身份，不另存玩家表。

### 运行清单

`ModuleDefinition.Describe()` 公开完整的 Player provider（`forge.module.gtfo.player`）元数据，以及必须由游戏绑定提供的 handler / evaluator 名称；`ModuleDefinition.Create(handlers, evaluators)` 只在调用方提供这些真实实现后构造可注册模块。能力行（端口、参数、结果行和结果码清单）来自积木表：`ModuleRegistration` 用 `ProviderDeclaration.DeclareBlocks()` 声明本 provider 的全部积木，代码里的能力 ID 写 `Blocks.<名>.Id`、结果码写 `ResultCode.<名>`，包里不再手写能力行或结果码清单。每行的 binding 与 handler 名只在各自的 `*Contract.cs` 里写一次（`PlayerSelectorContract`、`PlayerHealthContract`、`PlayerLifeContract`、`PlayerEventContract`、`PlayerStateContract`、`PlayerStaminaContract`、`PlayerMovementStateContract`、`PlayerActionContract`、`PlayerActionLockContract`、`PlayerCommandContract`、`PlayerAimContract`、`BotOrderContract`、`AgentModifierContract`、`CombatImpulseContract`、`PresentationActionContract`），`ModuleRegistration` 把它们组合成一次注册。binding 名为 `forge.module.gtfo.player.binding.<名字>`，handler 名以 `gtfo.player.` / `gtfo.bot.` / `gtfo.combat.` / `gtfo.presentation.` 开头。

当前清单（`catalog/forge-release/runtime-manifest.json`，由 `Mods/Release/export-runtime-manifest` 导出）里本 provider 的 binding 以该清单为准，不在这里写数量：动作类（`execute`）有：（机器人指令、属性修改（一次可写多项，移动档就是移速加加速度两项）、伤害、治疗、救起、冲量、效果取消、动作锁定、倒地、感染、体力、传送、镜头震动、屏幕液体）；观察类（`observe`）有：玩家读取、玩家选择器、玩家与输入触发。

### 原生半边（implementation-only）

`Player/Native/` 引用真实 interop，只编进 `ForgeEngine.dll`，不进不碰游戏的那份程序集：
- `PlayerHost`（`Player/Native/Host/PlayerHost.cs`）：引擎 `Plugin.Load` 在 Player、Map、Enemy、Weapon 的固定顺序里第一个调它的 `Start`；宿主 Off 或挂起时不调。日志级别是引擎 cfg 的 `Logging.Level`，随注册交给内核成为 `forge.module.gtfo.player` 的级别。启动失败时回滚本领域、写 `player.native-refused`，由引擎记一条错误后继续启动其余领域。`PlayerHost.Directory` 是全包唯一的玩家目录（`Players` 是它在会话运行时的同一个对象，`Session` 是正在跑的会话）。
- `PlayerPluginSession`：注册窗口内先注册模块（重复 provider 或 `gtfo.player` 命名空间冲突在声明 Hook 前原子失败），先向 Runtime 的共享成员入口订阅玩家身份读回（`PlayerMembershipReadback`），再把 `PlayerNativeHooks` 声明给 Runtime；Runtime 的 `NativeHookRegistration` 是唯一的物理安装者，每个目标都会声明，一次运行写全所有被拒目标。身份订阅拿不到时（每个世界的两个租约要么全拿到，要么一个不拿），原因写成一条 `player.native-refused`，随后注销并保留原始异常；声明的目标被拒时只停掉它们支撑的功能（`PlayerNativeHooks.Features`：倒地、复活、复活交互、死亡、传送、受伤、生命、感染、背包、标记、移动状态、长按、队友头顶信息、手电接管、地图接管），每个停掉的功能写一条 error 级 `player.capability-unavailable`（功能名、事实绑定、服务、被拒目标及原因），注册换成不含这些事实绑定的同一定义，需要它们的方案以 `binding-unavailable` 被拒，对应设备写入方不挂上，其余功能照常运行；物理安装随进程存在，会话释放只撤回本领域状态。每帧工作是三个领域共用帧入口 `forge.domain.frame` 里的第一个（`DomainWorkPhases`，失败时按 `forge.player.frame` 上报），没有自己的帧组件。文字语言是引擎通道 `NAinfini.Forge` 上每名玩家自己的一行大厅行 `player/text-language`（由 `PlayerTextLanguageSession` 登记）：一个字节，值是该玩家生效语言的序号，在本机算出生效语言的同一处写入；这一行随会话保留，迟加入的玩家和换房主都由 ForgeCore 送达，本领域不重发。任何机器（房主和客机一样）用 `forge.query.player.text_language` 读任何玩家的行；机器人没有行，读回 `text-language-unreported`。回调异常锁存故障并清表；所属线程检查。一个注册承载全部玩家面：运行时对一个身份命名空间只接受一个 provider。同一会话在注册后把身份半边挂到 `PlayerHost.Directory`，并以 `PublishService<IPlayerDirectory>` 把这个目录发布给别的插件（`ReferenceOf(SNet_Player)` / `ReferenceOf(PlayerAgent)` / `AgentOf` / `PlayerOf` / `LifeOf` / `SessionOf` / `SessionsOf` / `EverySession` / `IsLocal` / `LocalAgent`），另以 `PublishService<PlayerBotSlotBegin>`（会话释放或故障后以 `player-bot-slots-session-unavailable` 拒绝）与 `IncomingDamageReceiver` 给 ForgeDevelopment；Map、Enemy、Weapon 直接读 `PlayerHost.Directory`；会话号的唯一拼写是宿主 `PlayerAddress` 给出的玩家槽位。会话释放时目录摘掉身份半边，此后每次读取以 `player-directory-missing` 拒绝。本领域的动作与读值都经会话自己的身份半边执行，没有进程级的“当前身份”静态入口。
- `PlayerIdentityModule`：会话把 Player 的真实 handler / evaluator 交给 `ModuleDefinition.Create(...)`，再在同一模块上附加 `gtfo.player` 的 `EntityResolvers`、`EntityInstanceResolvers`、`EntityObservers` 与 `EntityCandidates` 等运行期服务，不另造 provider。身份表以状态源 `forge.player.identity` 随会话文档、检查点和迁移镜像走：客机整表替换；房主是生命的真源，读档时保留自己记录的生命，只收编本世界里自己还没编过号的玩家（刚接任的新房主世界变更后表是空的，所以整表收编），并把自己的编号与生命计数推到表里出现过的最大值之后。
- `PlayerMembershipReadback`：玩家身份的两个读回。`PlayerReplicationManager.OnSpawn` / `OnDeSpawn` 是 共享成员入口（ForgeCore 只挂一次，Runtime 的 `GamePlayerMembership` 绑到世界，Player 与 Development 都是只读订阅者，谁先加载、装没装别的包都不影响），Player 按世界订阅这两个入口的 Returned 阶段，在原生体返回后读回；`OnSpawn` 同时发重生事实，先对齐身份再发。`PlayerManager.OnPlayerSpawned` / `OnPlayerDespawned` 由 Map 声明；静态调用图里这两个方法各只有一个直接调用者，正是 `OnSpawn` / `OnDeSpawn` 返回前的最后一次调用，所以本地、远端与 bot 玩家都在同一时刻读到同一张表。
- `PlayerNativeHooks`：本领域向 Runtime 声明的唯一 Hook 列表（回调类清单见 `PlayerNativeHooks.cs`）：生命期、玩家状态、玩家事件与队友头顶信息的读回。
- `PlayerInputReport`：客户端经 `forge.player.input` 向主机上报本机按键、本机玩家完成的传送（资源 `teleported`，前后两个位置共 24 字节，主机据此为发送者发布「玩家传送完成」），以及本机玩家的体力；主机只缓存最近一帧，体力读回对非本机玩家取该缓存，缓存缺失或过期时读回为未知而不是 0。

证据文件 `Player/evidence/player-hooks.json` 是本模块的 Hook 规格，`Player/tests/PlayerNativeLayout` 逐条按它检查补丁目标签名、postfix 形参表、会话守卫、dump RVA、读回与写入成员签名和直接调用边；其余证据（生命、治疗、状态、事件、动作、机器人指令、属性修改）在同目录的 `Player/evidence/*.json`。

### 玩家实体身份（implementation-only）

这一层给 Map、Weapon 等领域提供经核验的玩家引用。身份规则：
- 每次读回遍历 `PlayerManager.PlayerAgentsInLevel`；只登记 `agent.Owner` 非空且 `owner.PlayerAgent` 回指同一 agent 的条目。内部字典键是 `SNet_Player.Lookup`。
- **隐私硬规则**：真实玩家的 `Lookup` 是 Steam64 账号 ID，只作 Player 私有字典键，不格式化、不序列化，不进入实体 ID、日志、错误、observer 或测试证据。`PlayerNativeAdapter` 断言全部日志、清单与报告不含任何 fixture Lookup，`PlayerNativeLayout` 静态检查原生程序集从不格式化或装箱 `UInt64`。
- 引用 `gtfo.player:<n>`：`n` 是本世界内按首次登记顺序递增的编号，与 Enemy / Weapon 一样由接线计数，不来自槽位；同一玩家在本世界内保持同一编号，WorldEpoch 为内核当前世界，换世界后重新编号。agent 指针变化或 SNet_Player 对象变化即分配新的单调 lifeEpoch，并记 `player.life-started id=gtfo.player:<n> world=… life=… bot=true|false`；消失、替换或同键冲突记 `player.life-ended … reason=despawned|replaced|key-conflict`。
- 倒地、救起、Heal、传送不替换 agent 就不改 life。检查点重载不换世界（见「读档」），模块只在 WorldChanged / Failed / Stopped 清表。
- 只在 Runtime Ready、会话未故障且 `SNet.IsMaster` 时分配。**不用 InLevel 玩法门**：电梯阶段的生成与进关同属一个世界。
- owner 解析不到或互链不成立时不登记，并对每个 agent 警告一次 `player.owner-unresolved`；同一玩家键出现两个 agent 时两者都不登记（`player.key-conflict`，警告不带键）。不按名字、槽位顺序或指针推断身份。
- `IsCurrent` 每次重读：world、前缀、无符号十进制编号、精确引用、agent 未销毁且指针相同、SNet_Player 指针与内部键不变、owner 与互链仍成立。
- 原生实例解析（内核的 `ResolveEntityInstance("gtfo.player", player)` 与目录的 `ReferenceOf` 读同一张表）只接受 `SNet_Player`，门槛与读回相同（已注册、Runtime Ready、会话未故障、`SNet.IsMaster`）。它按 SNet_Player 指针在已登记表里查找，再经上面的 `IsCurrent` 复核后返回该条目的引用；**只查不写**：不遍历 `PlayerAgentsInLevel`、不读 `Lookup`、不分配编号或 life。未登记、互链失效或门槛关闭都返回 null，由调用方决定不记录。
- 玩家候选来源（`EntityCandidates["gtfo.player"]`）是 `gtfo.player` 唯一的列举路径：`CurrentPlayers()` 里仍然成立的引用按 `id` 序数（`StringComparer.Ordinal`）排序交出，与模块内部字典的枚举顺序无关。模块没挂上或已经释放时按名抛 `player-module-unavailable`，内核据此报 `entity-candidates-failed`，**不返回空表**；真的没有玩家时才是空集。单次枚举受内核的候选预算约束，超限 `entity-query-budget` 且不截断。
- `forge.selector.target.players` 只走这一条列举路径：求值器用该步骤的 `RuntimeQuerySession.TryCandidates("gtfo.player")` 取集合，这次枚举与返回的引用计入同一份 query 预算（每 tick 64 次查询 / 1024 个引用），失败码原样交给步骤；没有第二个列举入口。`relation` 仍只回答 `ally`（`self`/`hostile`/`neutral`/`unknown` 以 `relation-unsupported` 拒绝），`empty` 仍只读取、由消费步骤施加策略。

已知未核验：
- bot 的 `Lookup` 是否跨重生稳定。
- postfix 时刻 `PlayerAgentsInLevel` 是否已包含新 agent、是否已移除旧 agent（`OnDeSpawn` 先调用 `UnregisterPlayerAgent` 再调用 `OnPlayerDespawned` 只是静态调用边，运行时顺序未证）。
- 倒地与救起确实不重建 PlayerAgent。
- `Object.Destroy` 延迟销毁期间旧 agent 的可见状态。
- 迟加入玩家与主机迁移路径。

### 机器人位（implementation-only）

已安装 Rundown 包的 `plugins/<包>/forge/bot-slots.forge` 名册在大厅里主机选关后写入对应机器人位的权限、角色与配装，`Player/Native/Lobby` 下的 `BotRosterContent` 读取并准入：名册只能写本包 Rundown 的作者关卡（`<Rundown 块号>:<A-E>:<序号>`），读不了、格式不符、关卡不属于本包或两份名册争同一关的名册单独拒绝并记 `player.bot-roster-refused`，不影响包的其余内容。选关事件与当前远征来自 ForgeCore（`GameEvents.ExpeditionSelected`、`Level.TryActiveExpedition`）；原生请求 `NativeBotSlotRequest` 经 ForgeEngine 的 `PlayerBotSlotBegin` 对 ForgeDevelopment 公开。运行日志码 `player.bot-slot-requested`、`player.bot-slot-applied`、`player.bot-slot-refused`。

### 大件背在背上（implementation-only）

已安装 Rundown 包的 `plugins/<包>/forge/carry-on-back.forge` 按关列出能背的大件（`itemId`、`key` 用 `key_code` 成员、`moveSpeed` 0 到 1、可选 `holdSeconds` 0 到 60）。`Player/Native/Lobby/BotRosterContent.cs` 里的 `LevelDocuments` 是两份按关文件共用的读取和准入，规则与机器人位相同，拒绝记 `player.carry-on-back-refused`。`Player/Native/Carry/CarryOnBack.cs` 只决定哪件、哪个键、多快，换手和落地都是 ForgeCore `CarrySlot`。

- 钩子：包里有规则才订阅 `GameHooks.WieldedSlotBlocked`、`LocalItemHeld`、`PlayerStats.Walk`、`ItemSpawned`。ForgeCore 的补丁解析器按 `HookPrologue` 检查起始字节；被拒或方法缺失时记 `player.carry-on-back-unavailable`，整个能力关闭，大件保持原版。
- 状态：每帧从原版同步的携带槽和手上槽位读出，规则文件各机相同，所以主机、客机、中途加入、读档都不存自己的状态，也不发网络消息。
- 落地：倒地和被扑倒由本机 `CarrySlot.DropIfFallen` 发原版掉落请求；掉线走原版 `CaptureLeavingPlayerSessionStatus`（按背包列举，与手上无关）。
- 动作锁（`StandStill`）和失去焦点时不响应换手键；背着时 `CarryItemRestrictions` 的「拿着不能按东西」不生效（它只在手上时生效），拿回手上恢复。Arsenal 重武器走特殊武器槽，彼此独立；战甲 `CarryHeavy` 用同一份 `CarrySlot`，半秒内重复请求只算一次，速度相乘。

### 玩家生命治疗（implementation-only）

`forge.action.combat.heal` 是 canonical 能力，由 `forge.contract.combat` 声明；Player 把自己的 binding 挂上去，和 Enemy 把同一能力挂到敌人接收器是同一件事，两边都不另造能力：

- 声明在 `PlayerHealthContract`：binding `forge.module.gtfo.player.binding.heal`、handler `gtfo.player.heal`、role `execute`、端口 shape `targets/source/amount/cap` + 结构参数 `overheal_policy`。公开的 `Describe()` 给出这一完整声明与所需 body，原生半边（`PlayerHealthAction`）把真实 handler 交给 `Create(handlers, evaluators)`；元数据工具只读取 `Describe()`，不会得到一个看似可执行、实际缺少 body 的模块。
- 原生写入点只有一个：`Dam_PlayerDamageBase` 自己没有写健康的成员，绝对血量经基类 `Dam_SyncedDamageBase.SendSetHealth(System.Single)`（build 20403457，RVA 0x161F790）提交；游戏自己的治疗 `ReceiveAddHealth` 也是算完增量、clamp 到 `HealthMax` 后调同一个入口。该入口只认主机（非主机分支只上报不落地），并且玩家覆写的 `ReceiveSetHealth` 在 `Owner` 为空或 `Owner.Alive == false` 时**不写入**。证据、字段偏移与未核验项在 `Player/evidence/player-health.json`，写行登记在 `Player/evidence/player-hooks.json` 的 `writes`。
- `PlayerHealthReceiver` 按 `PlayerIdentityModule` 当前 life 解析实例（绝不由 Enemy 解析玩家），双读比对 owner/setup/alive/health/max 后给出一行结论：目标不是玩家 → `unsupported-recipient`（由治疗动作先判）；无当前 life → `stale-entity`；无接收器或未 setup → `missing-health-receiver`；owner 不符 → `health-receiver-owner-mismatch`；已死 → `not-alive`；血量非法 → `invalid-health-state`。提交前再次复核：实例指针换了拒绝为 `receiver-changed-during-commit`，同一实例的状态变了拒绝为 `state-changed-before-commit`。读回只接受"同一实例、上限不变、血量只升不超上限"，否则该行是 `unknown`（`receiver-changed-during-commit` / `unexpected-health-readback`）；其后的目标照常执行，整条命令的结论由 `ResultAggregation.Conclude` 按各行给出。
- 策略与 canonical 一致：`overheal` 整条拒绝（接收器的血量按 `HealthMax` 量化，没有超过上限的可表示值）；`discard` 逐目标跳过会溢出的目标（`would-overheal`）；`clamp` 收到 `min(cap, HealthMax)`；满血目标是**已提交的零变化**，不发虚假变化事实。结果行按 canonical 六列写：`target`、`status`、`committed`、`code`、`amount`、`target_count`，其中 `amount` 是读回的实际变化量，不是请求量。
- 快照的 `receives` 只在接收器可读且 agent 存活时发 `health.heal`：死玩家、无接收器的玩家不发，倒地玩家仍发（原生模型里倒地仍是 alive），但本实现只写血，不调用 `OnRevive`，因此不声称救起。玩家生命变化的触发（`forge.trigger.combat.health_changed`）Player**未**声明：它在敌人侧同时由原生伤害观察发布，只由 Player 治疗发布会把这个 canonical 触发说成只报自己造成的治疗。
- 未核验：以上全部来自元数据、静态调用边与方法体解码，没有启动游戏；主客机实际落地、迟加入重放、倒地状态机是否对血量写入有反应，以及哪些原生治疗工具走 `ReceiveAddHealth` 都未验证（见 `Player/evidence/player-health.json` 的 `unverified` 与 `gameVerificationPlan`）。

### 玩家受伤的引发者

`forge.action.combat.damage` 的 `instigator` 可不接，表示这次伤害没有引发者。玩家接收者只在 `explosion`、`projectile`、`fall`（原生入口没有攻击者参数）和 `dot`（`FireDamage` 传空，离线读过路径安全）放行；`direct`、`melee`、`tentacle` 未核实，以 `instigator-missing` 拒绝。接了但取不到的引发者对所有种类都拒绝，不降级成无引发者，也不用 `source`、`owner` 或接收者顶替；计划加载与网站编译都拒绝把事件可能缺席的可选端口直接接到 `instigator`（`optional-event-port`），要先过「存在」判断。没有引发者的爆炸以接收者位置为爆炸点。证据与游戏内待验证项在 `Player/evidence/damage-kind-presentation.json` 的 `noInstigator`。

### 复跑

两个原生套件在测试工程 `Player/tests/PlayerNativeEvidence` 里，从仓库根经 [`Mods/verify.py`](../verify.py) 运行，它负责构建参数、环境变量和输出目录（`artifacts/mods/ForgeEngine/<运行名>/`）：

```powershell
python Mods/verify.py --game E:/SteamLibrary/steamapps/common/GTFO --dump '<同 build 的 dump.cs>' ForgeEngine:PlayerNativeLayout ForgeEngine:PlayerNativeEvidence
```

runner 给原生测试设的环境变量：

| 测试 | 变量 |
| --- | --- |
| `PlayerNativeLayout` | `GTFO_BEPINEX_PATH`、`FORGE_ARTIFACTS`（= `artifacts/dotnet`）、`FORGE_GTFO_GAME`、`FORGE_GTFO_DUMP`、`FORGE_PLAYER_HOOK_SPEC`（= `Mods/ForgeEngine/Player/evidence/player-hooks.json`） |
| `PlayerNativeEvidence` | `FORGE_EQ_PLAYER_NATIVE`（= 构建出的 `ForgeEngine.dll`） |

游戏 build、dump 或 interop 变化后，`Player/evidence/action-lock-options.json` 里的源文件与目录哈希用 `pwsh Mods/ForgeEngine/Player/tools/regenerate-action-lock-evidence.ps1 -GameRoot <GTFO 目录> -BepInEx <QA profile 的 BepInEx 目录> -Dump <dump.cs>` 重写。

主客机实际落地、迟加入与主机迁移都待游戏内验证。

## Map

Map 与 Room 的空间、设备、任务、遭遇、关卡流程与进程、世界表现，**以及地图生成的底层逻辑**。源码在 `Map/`，命名空间 `ForgeMap`、`ForgeMap.Native`。玩家与机器人在上文 Player 一节。

### 原生地图与资源入口

官方已有房间由 Map 根据包内的 Forge 生成计划选择、排序并加载所需环境资源。
Map 负责地图对象、设备、任务、遭遇及与统一 Trigger 的连接。产品顺序与 MAP-BUILD
要求见上方计划 §4 / §6；第三方资源方案见 §8，不以资源提取或新房间原型阻塞原版关卡。

[Map/GENERATION-SPEC.md](Map/GENERATION-SPEC.md) 只记录原生生成入口、已有描述符夹具及核验方法；
资源描述要求见唯一计划 §3.2；第三方接入选型见 §8。登记资源、预览可见与实机可玩分别取证。

Rundown 包不带任何 MTFO 数据表：作者的 Text 行是 `forge/rundown.forge` 里的普通块，经 ForgeCore `NativeDataBlockTables.Register` 并进原版表（撞名撞号拒绝该包）。通关进度由 ForgeCore `Progression` 按 Rundown 编号记在本机，解锁规则就是包里原版 Rundown 与关卡块的字段，引擎不另加文字行。

一层关卡太多时的选关页排布在 `Map/Native/Lifecycle/RundownTierLayout.cs`，由 `RundownPagePlaced` 后置补丁在原生 `PlaceTier` 之后调用（只在有 Forge Rundown 包且该 Rundown 显示关卡树时）。原生公式（build 20403457，`PlaceTier` RVA 0x1265B10）：层内第 i 个图标在层局部 ((t − ½)·1500·s, −2·min(t, 1 − t)·450·s·ScaleYModifier)，t = i/(n − 1)，层变换缩放 s、图标缩放 1/s，`PutIconAboveTier` 的图标另放；层局部 y 是椭圆的纵深（朝屏幕里），不是屏幕上下，所以分出的行沿页面的 y 轴换算进层局部坐标往下排；层间距 `m_tierSpacing`（游戏内读回 230）。页面间距 1500·s²/(n − 1) 不低于 211.25（原版 R7 A 层 4 关、Scale 0.65）的层一律不动；更窄的层按 281.25 间距、843.75 行宽分行，行距 110（图标连名字和两行状态约 87 高，游戏内读回加间隙），可用高度到下一个有关卡的层，放不下以 0.05 为步缩小到 0.4。开发模组 `RundownIcons.Read` 读回每个图标的屏幕矩形、相交对数和偏离原生公式的个数。

关卡名和 Rundown 名可以分语言：作者给了多种语言时，包里关卡的 `Descriptive.PublicName` 仍是主语言明文（与只有一种语言时字节相同），`rundown.forge` 的 `levelNames` 记「`<层级字母>:<层内序号>` → 包内 Text 块编号」（`RundownPacks.Parse` 校验键形与编号属于本包的 Text 块），`StorytellingData.Title` 是 Text 行编号。`RundownMenu` 每次装上（启动与每次换代）按这张表记下每关的主语言明文和 Text 编号，按当前游戏语言写出文字（该语言一列为空时保留主语言明文），`GameEvents.LanguageChanged` 时重写；主菜单按钮标题读 `StorytellingData` 的标题，读不到用包标题。联机关卡校验 `ExpeditionInTierData.GetChecksum`（RVA 0x1980940，入口 `mov [rsp+8],rbx; push rdi` 共 6 字节）由 `RundownMenuHooks.LevelNameChecksum` 前后各挂一次：哈希前把 PublicName 换回包里的主语言明文，哈希后换回本机语言文字，别的不动。参与校验的永远是包里那段明文，钩子装没装上、装没装这版引擎，各端校验值都一样。钩子被拒（功能 `level-names-per-language` 撤回，记 `map.capability-unavailable`）时不改写 PublicName，所有人看到主语言明文；表里的关卡找不到或 Text 块缺失时同样不改写，记一次 `rundown.level-name-unresolved`。限制：关卡进行中切换游戏语言，数据里的名字随即改写，但关卡内已经显示出来的名字不刷新，要等那处界面下次重画；这一点不打补丁。

### 运行清单与内部身份层

`ModuleDefinition.Describe()` 公开完整的 Map provider 元数据以及必须由游戏绑定提供的 handler / evaluator 名称；`ModuleDefinition.Create(handlers, evaluators)` 只在调用方提供这些真实实现后构造可注册模块。游戏内的唯一 Map provider 由 `MapHost.Start` 注册；第三方插件在自己的 BepInEx `Load` 中经 `Plugin.Runtime` 取得同一个内核、在注册窗口内注册自己的 provider，不重复注册 Map。

生产程序集内的内部身份层类型：`MapIdentityContracts.cs` 定义域内地址、精确来源锁、内部 native key、不含指针的快照，以及按创建尝试颁发的票据。

地址逐项区分 layout 与 revision、dimension、layer、local zone、placement、对象 ID 和对象类别。同一 geomorph 的多个 area 使用各自明确的对象 ID；同资源的两个 placement 不合并。**地址不能通过名字、位置、遍历顺序或预览 GLB 猜测**；提供地址的原生适配器仍待核验。来源锁保留 resource ID 与 revision、来源证据 SHA-256，以及已有 `SourceObjectIdentity` 的 file 与 pathId。

原生 API 与字节证据检查工具可重复运行：本机 build 的 53 个类型、129 个方法的元数据锁，以及 11 个原生区域字节复核；生成相关另锁定 27 个类型、59 个方法（`Map/tools/generation-api-targets.json`）。**这些是 metadata-only 证据，不证明原生调用语义、合法阶段或复制完成。**

### 原生半边（implementation-only）

`Map/Native/` 引用真实 interop，只编进 `ForgeEngine.dll`，不进不碰游戏的那份程序集：
- `MapHost`（`Map/Native/Host/MapHost.cs`）：引擎 `Plugin.Load` 在 Player 之后调它的 `Start`；宿主 Off 或挂起时不调。日志级别是引擎 cfg 的 `Logging.Level`，随注册交给内核成为 `forge.module.gtfo.map` 的级别。启动先捕获已装包的地图内容，再登记引擎通道上的两张主机表（`MapNet.Open`），再起会话；失败时回滚本领域，由引擎记一条错误后继续启动其余领域。`MapHost.Session` 是正在跑的会话，其他领域直接读它（区域落点、维度与范围判定、声音落点等）。
- 世界标签、放置模型的文字、告示牌与图片板：画法归 ForgeVisual（`WorldText`、`WorldBackdrop`、`SignBoard`、`ImageBoard`、`ImageTextures`），Map 只决定何时显示、对谁显示、文字内容与语言，并读回显示结果；`hud_marker` 的原版标记显隐语义与 ForgeUI 的 `AddNavMarker` 不同，留在 Map（`MapLabels`）。
- `MapPluginSession`：注册窗口内先注册模块（重复 provider 或 `gtfo.map_object` 等命名空间冲突在装 Hook 前原子失败），再把本领域的 Hook 类整体声明给 Runtime（`NativeHookRegistration.Declare`，安装与卸载都由 Runtime 做）；某个目标被 Runtime 拒绝时只撤回依赖它的那一组功能（每组一条 `map.capability-unavailable` 错误记录，值为功能名），其余照常启动；失败回滚先释放本领域的 Runtime 租约与帧阶段再注销并保留原始异常；回调异常锁存故障并清表；所属线程检查。帧工作是共用帧入口 `forge.domain.frame` 里 Player 之后、Enemy 之前的两项（`DomainWorkPhases`）：每帧一项只做房间拒绝后的退出（`Frame`，失败时按 `forge.map.frame` 上报），逐 tick 一项跑关内工作（`Tick` → `InLevelFrame`，`perTick: true`，失败时按 `forge.map.tick` 上报），会话自己不再有 tick 门；玩家进入关卡经 Runtime 的玩家成员入口订阅（`GamePlayerMembership`），按世界续租。
- `MapNativeHooks`：交给 Runtime 声明的 Hook 类清单（自有部分加世界对象各族的 `ObjectNativeHooks.Types`），按类名列出地图对象、关卡对象、远征、关卡事件、扫描覆盖、定点放置与灯光的读回与写入点；一个类只对应一个原生目标。房间配方、门构建、区域房间与房间变换的 Hook 仍由 `Map/Native/Content`、`Map/Native/Generation` 与对应安装器自己的 Harmony 实例持有，等它们迁移后再并入这份清单。`MapHookFeatures` 是“目标被拒绝时撤回哪一组功能”的唯一功能表。

玩家与机器人（`gtfo.player` 身份、玩家选择器、生命值、生命期、体力、移动、输入、指令、机器人指令与属性修改）不在 Map：它们由 Player 注册。Map 需要玩家时只经 Player 的目录 `PlayerHost.Directory`（Player 会话没在跑时以 `player-directory-missing` 拒绝）取玩家引用（`ReferenceOf` / `PlayerOf` / `AgentOf`）、会话号（`SessionOf` / `SessionsOf` / `EverySession`）与本机判定（`IsLocal`），不按字符串向内核查 `gtfo.player`，也不另存玩家表；玩家进入关卡经 Runtime 的玩家成员入口订阅（`GamePlayerMembership`，原生函数返回之后），`OnPlayerDespawned` 上的关卡侧 postfix 由 Map 声明；两者都只决定关卡半边何时响应，玩家身份本身由 Player 登记。

证据文件 `Map/evidence/map-hooks.json` 是本模块的 Hook 规格，布局审计逐条按它检查：每个 Hook 的补丁目标签名与 virtual/static、它自己声明的 postfix 形参表、读哪些形参、经哪个会话守卫发布、dump RVA（唯一且不共享、位于可执行段），读回与写入成员的签名，以及直接调用边。门与终端自己的成员登记、地址规则与游戏内确认清单在 `Map/evidence/door-terminal-hooks.json`。

### 门与终端的 map-object 地址（implementation-only）

门与终端在同一个 `gtfo.map_object` 实体命名空间里，`category` 是地址的一段，不是第二个命名空间。地址格式：

```
<category>/<dimension>/<layer>/<zone>/<key>
```

`category` 是 `MapObjectKind` 的小写名（`door`、`terminal`）。三个坐标是**对象所属区域**的原生值：`dimension` 取 `LG_Zone.m_dimensionIndex`，`layer` 取 `LG_Zone.m_layer.m_type`（主层/附加层/超载层 = 0/1/2），`zone` 取 `LG_Zone.LocalIndex`。每段都是十进制、无前导零；任何一段读不到就不产生地址，**不用 0 或 `?` 冒充**。

`key` 在区域内部命名对象。门的 key 是闭集词元 `security`：通往该区域的入口安全门——坐标因此是**被守卫区域**，一个区域至多一扇入口闸，所以不需要编号。终端的 key 是它在**所在区域** `TerminalsSpawnedInZone` 中的 0 起下标，单终端区域恒为 `0`。

实例身份是 `gtfo.map_object:<地址>`，由地址直接推导、可无表反解，换世界由内核 world epoch 整体失效。地址与解析都只走 `Map/Native/Triggers/ZoneIndex.cs` 这一张区域查找表：它按 world epoch 从 `LG_LevelBuilder.Current.m_currentFloor.allZones` 重建 `SpawnedDoor → LG_Zone` 与 `LG_Zone → 终端列表`，门和终端两个读取器都向它要坐标，不各走一套原生图。

不可寻址的对象每类只报一次原因并**不发布任何事实**：不是任何区域入口闸的门（弱门、节点门、装饰门）、bulkhead 层转换门、没有入口闸的出生区域、坐标读不到的区域里的门与终端、区域数据块声明了 `SpecificTerminalSpawnDatas` 的终端（该区域的列表不是它的全部放置顺序）、所属区域列表里没有它的终端（反应堆与任务终端）。地址不接受 `MapperDataID` / `SyncID` 写法，不做兼容解析；`SyncID` 只保留为观测事实，用于把原生回调带的 id 换回终端实例。

**命中对象的解析**：子弹打到的碰撞体不是地图对象本身，而是门的门刀、门框、按钮或终端屏幕。`gtfo.map_object` 的实例解析器因此接受**命中路径唯一能观察到的对象**：`Map/Native/Objects/MapObjectHit.cs` 用 `UnityEngine.Component.GetComponentInParent<LG_SecurityDoor>()` / `<LG_ComputerTerminal>()` 从它爬到该 kind 自己的原生实例，再由 `MapObjectModule.ResolveInstance` 走与直接交实例时**完全相同**的分类、寻址与拒绝路径——地址语法、`MapObjectRefusal` 的逐类原因、观测器与 `map-object` 挂载匹配全部不变。爬不到地图对象（世界几何、部署物、敌人/玩家伤害肢、没有门祖先的碰撞体）就返回 null，绝不合成地址；门在但不可寻址时，报的仍是门自己的那条拒绝原因。爬父级这一步登记在 `Map/tests/MapNativeLayout` 的 `readSpellings`（按"名字不以 `get_` 开头的纯查询"），且只在 `Map/Native/` 里发生：不碰游戏的那一半不引用 Unity，只多了一个可选的对象解包委托。Weapon 侧因此只按 kind 名问一次，不认识门或终端的类型。

**未核验**：`zone.m_sourceGate.SpawnedDoor` 是否就是玩家进入该区域的入口闸、每个非出生区域是否恰好一扇；`TerminalsSpawnedInZone` 的顺序是否等于 `TerminalPlacements` 的顺序，以及反应堆/任务终端如何插入；`LG_LevelBuilder.Current.m_currentFloor.allZones` 在首次回调时是否已填满；以及子弹碰撞体是否真的挂在门/终端之下（门刀换父、终端屏幕碰撞体的层级）。四项都只有静态 interop 证据或根本没有运行期证据，确认清单在 `Map/evidence/door-terminal-hooks.json` 的 `inGameVerificationPlan`。

### level 挂载与关卡身份（implementation-only）

`level` 挂载的 reference 是一个关卡的自身身份：

```
<rundown 块 persistentID>:<tier A-E>:<tier 内 0 基下标>
```

例 `31:A:0`。三段都是十进制、无前导零，tier 是一个大写字母；这是网站导出原版关卡 preset 已经在用的同一套拼写。带 rundown 块 id 是必须的：包里除被替换的那块之外保留整张原版表，只有 `A:0` 会在别的 rundown 的 A1 上误触发。

游戏侧的两个量都在 `Map/Native/Lifecycle/LevelIdentity.cs` 里读一次：`Globals.Global.RundownIdToLoad` 是本进程加载的 rundown 块，`RundownManager.GetActiveExpeditionData()` 的 `tier`（`eRundownTier`，A=1…E=5）与 `expeditionIndex`（0 基）是关卡在该块里的位置。游戏自己拼的 `ActiveExpeditionUniqueKey`（`Local_31_TierA_0`）只写进那一行日志，从不参与比较：它的 `Local_` 前缀是加载方式的细节，创作侧没有理由去预测它。tier 超出 A–E、下标为负、或读不到 expedition（未进关、成员读不出来）都答 `null`，匹配器按"这个世界的关卡身份读不到"处理。

比较规则：

- 解析在 `MapLevelReference.cs`（纯托管，无游戏引用）：三段严格解析，拼不出这个形状的 reference 不匹配任何关卡，并写一条有界诊断；**不会**退化成"匹配所有关卡"。
- `MapObjectModule` 注册 `level` 匹配器时用的是无主体（scope）形式：它只拿挂载目标与当前世界的关卡身份比对，因此在世界、计时、脉冲这些不带主体的事件上同样会被判到——这类事件正是整关触发的常见来源。
- 当前世界的身份在每个 world 里只读一次并缓存，下一次 `BeginWorld` 丢掉：一关只读一次原生成员，且一关的身份不会跨世界复用。
- 读不到身份与非法 reference 各自只报一次，都答 false：一关里没触发过的行为，可以从日志里区分是"没匹配上"还是"根本没比较"。

发行身份上，`Mods/Release/release.json` 给 ForgeEngine 声明的 `attachmentKinds` 含 `level` 与 `map-object`（另有 `enemy-type`、`gear-block`、`player`），网站据此把带这些挂载的计划解析到 ForgeEngine 并写进依赖闭包。静态原生证据（成员签名、`ActiveExpeditionUniqueKey` 取值形态与既有游戏日志旁证）在 `Map/evidence/level-identity.json`，读回签名登记在 `Map/evidence/map-hooks.json` 并由布局审计按名核对。

**未核验**：本任务没有启动游戏。两关包里 A1 的行为是否真的不在 A2 派发、`Global.RundownIdToLoad` 在整场远征里是否恒定、以及同一 tier 内存在被禁用关卡时 `expeditionIndex` 的语义，都只有静态证据与既有日志旁证，确认清单在 `Map/evidence/level-identity.json` 的 `unproven`。

### player 挂载：本关全部玩家（implementation-only）

`player` 挂载的 reference 与 `level` 同一拼写（`<rundown 块 persistentID>:<tier A-E>:<tier 内 0 基下标>`），含义是「这一关的全部玩家」，并且必须带 `port`：宿主是触发卡的哪个输出口。内核只拿这个输出口上的实体来问匹配器（救起事件挂在 `player` 上是被救的人，挂在 `rescuer` 上是救人的人），所以同一事件里的两名玩家不会让 `self` 变得模糊。

匹配器 `MatchesPlayer` 注册在 `MapObjectModule` 上，与 `level` 同条件：没有关卡读取器时两者都不声明，带这种挂载的计划在加载时以 `attachment-kind` 被拒。命中要求三件事同时成立：实体属于 Player 域的 `gtfo.player` 命名空间、内核 `IsEntityCurrent` 认为它在本世界仍有效、当前关卡等于 reference（复用 `MatchesLevel` 的严格解析与一次性诊断）。归属放在 Map 而不是 Player，是因为关卡身份在 Map；计划对整份 Rundown 全局加载，不带关卡的玩家挂载会在每一关都触发。

**未核验**：按输出口选 `self` 的内核规则当前没有测试；没有启动游戏，主客机下玩家事件的发布与这条挂载的实际派发都没有游戏证据。

### 远征结束触发（implementation-only）

`forge.action.map.expedition_end` 的 `fail` 走原版会话命令 `SNet.Sync.SessionCommand(TryEndPlaying, 1)`（`GS_InLevel.FAIL_COMMAND_CODE`），不写 Forge 状态，结束后由原版 `OnExpeditionEnded` 报告。

撤离宿主（`Map/Native/Objectives/ExtractionPointHost.cs`，读 `forge/extraction-point.forge`）：一关一行。撤离点行在作者点上建（或接管该 Area 已有的）唯一 `LG_LevelExitGeo` 并改写它的扫描对位；`mode: "room"` 行不写坐标，在该房间生成的 geomorph 里选它自带的唯一 `LG_LevelExitGeo`，扫描对位与 `m_exitArea` 原样保留（二者是预制体序列化字段，`Start` 与 `OnBuildDone` 都不写）。两种行都只让选中的宿主 `Start`，其他撤离宿主与电梯不登记；没有、多于一个或字段失效以 `room-exit-missing` / `room-exit-ambiguous` / `room-exit-fields` 拒绝（`map.extraction-point-refused`），并回退关卡自带撤离。

`forge.action.map.extraction_move`（`Map/Native/Objectives/ExtractionMoveLedger.cs`）：游戏没有搬动已建撤离扫描的入口，这里是最小适配，只动扫描核心位置、课程节点登记、导航标记目标和目标文字里的撤离区。位置记录走 `DomainStateChannel.RegisterNativeState`（客机、晚加入、读档、换主机都靠它），没有卡被用过时记录为空，不改任何原版行为。扫描已开始（`Scanning`、`TimedOut`）或已结束时显式拒绝；撤离点身份仍是建造时的区，只有文字跟新位置。撤离波次的起点也跟新位置：原版扫描进度回调把端点建成区的节点（`SpawnNode`）交给 `WardenObjectiveManager.OnExitScanProgressUpdate`，`ExtractionWaveNode`（Map 自己的前缀钩，只有 Map 用这个方法）把它换成扫描新节点再由原版开波；每台机器都换，波次由主机的 Mastermind 生成。

`forge.trigger.session.expedition_ended` 是本 provider 唯一的会话级触发行，形状与网站授权目录该行逐字相同：`execution host`、domains `map/session/logic`、无输入、无参数，输出 `next`（execution）与 `outcome`（enum，schema `execution_outcome`）。它没有主体，所以挂在 `level` 上的计划由挂载目标判定，与门/终端事实走的是同一条派发路径的两条分支。

原生侧只有一个读回点：`RundownManager.OnExpeditionEnded(ExpeditionEndState)`（实例方法、非虚）。游戏在这次调用里就给出了结局本身（`Success`/`Fail`/`Abort`），所以事实取参数，不从游戏状态名、人数或卸载顺序反推。映射是通关 `succeeded`、团灭 `failed`、退出 `cancelled`；三种之外的值不发布，只报一次原因。签名与 `0x13E2100` 登记在 `Map/evidence/map-hooks.json`，由布局审计按名与证据核对。

发布规则（`ExpeditionModule`，`MapObjectModule` 在同一条注册上组合它）：主机才发布（原生钩子与模块各自把关一次）；事件 id 是 `forge.trigger.session.expedition_ended:<world epoch>:<结局>`，所以同一世界同一结局的重复上报是内核的 `duplicate`（只派发一次），同一世界的另一个结局是它自己的事件，不同世界的同一结局是新事件。载荷只有 `outcome`，值是枚举成员下标（框架 enum 端口的线上形式），不是名字。

`forge.trigger.session.expedition_started` **未注册**：它的 `map` 输出是 resource 端口，而本运行时没有 resource 值——resource 只作为步骤输入上的编译期常量 `{id, revision}` 存在，触发器声明该端口会让整份计划以 `unsupported-event-port` 被拒（`Map/evidence/expedition-trigger.json` 的 `runtimeResourcePorts` 逐条列出框架位置与实测）。目录行与 manifest 必须同形状，因此既不注册窄化版本，也不为 `map` 填占位值。

**未核验**：没有启动游戏。游戏对一次远征实际报几次结局、检查点重载是否再报、事件是否在关卡清理前进入派发，都只有静态证据与 interop/dump 元数据；确认清单在 `Map/evidence/expedition-trigger.json` 的 `inGameVerificationPlan`。

### 定点物品与定点敌人（implementation-only）

包文档 `plugins/<包>/forge/objective-rounds.forge`（`schemaVersion: "1.0.0"`，`ObjectiveRoundsDocument`）按关卡和目标块写逐轮内容：上传每轮的三段等待和结束前要完成的场景扫描、限时终端每轮的时长、确认时间、接收终端区域、不重复和结束扫描、反应堆由事件验证的波次和提示。没有这份文档或没有这一行的目标完全走原版，门闩钩子也不装。每轮事实走 `forge.trigger.objective.round_completed`、`timed_round_started`、`timed_round_failed`；`forge.action.map.reactor_verify` 走原版验证入口，`forge.action.map.timed_round_time` 由主机把当前阶段累计挪动的秒数写进主机表 `timed-round-time`（见「主机迁移与检查点状态」），各端按这一行挪自己的计时。

包文档 `plugins/<包>/forge/fixed-items.forge` 与 `fixed-enemies.forge`（`schemaVersion: "1.0.0"`）承载作者摆在一个房间里的物品与敌人。两种文档共用同一组公共字段：`id`（`item:` / `enemy:` 前缀，日志与 seed 用）、`level`（与 `level` 挂载同一拼写）、`locator`（与 `trigger-zones.forge`、`forge-project.forge` 同一套房间定位符）、`p_u`（预制体根 Unity 局部坐标，米，含高度）、`yaw_u`（度，绕 +Y）。

- 物品行只写 `itemId`（`ItemDataBlock.persistentID`）或 `targetDoor`（`door/<dimension>/<layer>/<zone>/security`）之一：区域钥匙卡写门，运行时读该门 `LG_SecurityDoor_Locks.m_gateKeyItemNeeded.DataBlockID` 生成——区域钥匙由关卡生成器随机分配，作者写不出那个 id；Bulkhead 钥匙是普通 `itemId` 146。`amount` 是**创作侧单位**：消耗品=剩余次数、资源包=池比例（大于 0，可以大于 1）、其余槽位 0；资源包比例不是原生字段的单位，后端在写原生前换算一次（见下一条）。
- 敌人行写 `typeId`（`EnemyDataBlock.persistentID`）与 `intent`（`hibernate` / `aggressive` / `patrolling` / `scout`），intent 就是原生 `AgentMode`。
- 一行读不出来只拒绝这一行并记诊断，同文档其余行照常放置；文档自己的版本、根字段与数组仍是整体接受或整体拒绝。重复 id 的后一行被拒。
- 放置时机是 `LG_Factory.FactoryDone`（关卡生成阶段：房间世界位姿、course node、门锁要的钥匙都已存在）。顺序是文档顺序，seed 取行 id 的 FNV-1a，因此每台机器对同一行算出同一个 seed。
- 物品由**每台机器各自本地生成**（身份在 `pItemData` 里、只有状态被复制）：可放置槽位 5/6/7/8/9 走 `WorldEventUtils.TrySpawnItemOnAlign`（mod 自建 align Transform，位姿就是 `world = R_geo · p_u + t_geo`、`rot = R_geo · Euler(0, yaw_u, 0)`），资源包槽 4 走 `ItemSpawnManager.SpawnItem`（原生 align 入口按跳表拒绝该槽）；驱雾器 133 放下后由主机补一条 `AttemptInteract(Activate + StaticPosition)` 把它打开。定点敌人**只由主机调一次** `EnemyAllocator.SpawnEnemy`，由游戏自己的分配器生成并复制到各 peer；行的 `typeId` 是敌人数据块，`intent` 是原生 `AgentMode`。
- 资源包的 `amount` 是池比例，可以大于 1，原生 `pItemData_Custom.ammo` 数的是同一个池的**原生弹药点**：100 点是满池，所以后端在写原生前换算一次 `FixedPlacement.NativeAmmoForResourcePack`（`100 × share`），`SpawnResourcePack` 只用这个换算后的值；消耗品与其余槽位不做这个换算（消耗品的原生 ammo 就是它的次数，其余槽位是 0）。同一份比例在文档里是 `0.2 / 0.6 / 1`，写进原生就是 `20 / 60 / 100` 点。证据在 `Map/evidence/fixed-placement-natives.json` 的 `resource-pack-ammo-unit` 行（build 20403457 只读反汇编）：游戏自己的容器生成体 `LG_ResourceContainer_Storage.SpawnResourcePack`（RVA `0x159C310`）把容器 spawn data 的 `m_ammo` 按 `×100 ÷20 round ×20` 写成这个字段（满池 100 点、每次使用 20 点），而它的输入 `ExpeditionBalanceDataBlock.ResourcePackSizes` 就是 `[0.6, 1.0, 0.4]` 这样的池比例；`ResourcePackPickup.UpdateAmmo`（RVA `0x13C2310`）把参数原样存进该字段，`GetNameWithCapacityFormatted`（RVA `0x13C1410`）按 `/100` 显示它。**未核验**：本模块放下的包被捡起、注入后实际补了多少，以及比例不是 `0.2 / 0.6 / 1` 时（例如 `0.5`：本模块线性写 50 点，游戏自己的容器体量化成 60 点）游戏是否同样处理——两条都列在 evidence 的 `unproven` 里。
- 拒绝路径按名给出 code 并每关只记一次：房间五种拒绝原因、`no-course-node`、`door-unresolved`、`no-gate-key`（门没有钥匙需求）、`item-block-missing`、`slot-not-placeable`、`amount-refused`、`items-not-ready`、`pickup-mode-missing`、`spawn-refused`、`no-level-root`。**不猜位置，也不放原点。**
- 每条成功放置同样写一行日志（id、世界位姿、yaw、slot/route、seed，或 type/intent/node），供合同 §4.5 V1 的 A1 图逐件、逐只核对。
- 证据：`Map/evidence/fixed-placement-natives.json`（成员 RVA / dump 行 / 未证清单）、`Map/evidence/map-hooks.json`（FactoryDone 钩子 `FactoryDoneHooks` 与四条写行）、`Map/evidence/native-api-20403457.json`（本次新增的 21 个类型与方法锁）。
- **未核验**：placement 从 `FactoryDone` 调用（而不是在生成 job 里）、检查点回档是否叠加、各 peer 是否逐件一致、`debugEnemyID` 组是否只生成一只以及组被唤醒后的行为、驱雾器全局状态是否已 Setup、晚加入能否看到定点物——逐条列在 `Map/evidence/fixed-placement-natives.json` 的 `unproven`。

### 主机迁移与检查点状态（implementation-only）

- 主机迁移是 `WorldChanged` 且 `RuntimeLifecycleEvent.IsMigration` 为真：同一关、世界号加一。Map 各会话（房间回执、触发区、模型、定点、出口与原版世界对象观察、内部门、扫描、目标文字、失败规则、波次账本）对迁移只把自己盖的世界号跟到新值（`FollowWorld` / `Follow`），不清回执、不销毁对象、不重放；只有换关才清。新增会话保存世界号时按同一规则处理。
- 原生句柄交接（`MapHandleHandover`，schema `map-puzzle-handles`，经 `RegisterProviderHandover`）：警报与警报状态事实各自把内核句柄绑到一个原生连锁谜题；捕获时按槽位记下谜题的 `m_puzzleUID`（以及该句柄是否在 `LevelObjectModule` 的警报句柄表里），接手一侧在自己的实例表里按 uid 唯一查到同一谜题并把原句柄重绑过去（`ChainedPuzzleLookup.Find` + `Liveness`），采用时整表替换警报句柄表，回滚还原。查不到或查到两个以 `migration-native-handle` 拒绝，不会指向别的谜题；源端已回收的谜题改绑一个已结束对象，由内核下一次清扫释放。不是谜题的句柄（`wave_start` 发出的波次句柄带本地拥有的生命期与 Mastermind 事件，新主机无法召回）没有对应行，同样拒绝：活跃波次本来就会让迁移以 `migration-owned-lifetime` 失败，需先结束。
- 区域灯光账本（`forge.map.zone-light-color`，schema 2.0.0）：每条是一个请求键（区加类别），灯上显示的值由同区所有点名该灯类别的条目合成（亮度取最新设置者 `BrightnessRevision`，是相对灯原版强度的设定值，重复设置不累积；颜色取最新设置者 `ColorRevision`），主机记录请求（`ZoneLightColorLedger.Request`）和其他机器恢复账本走同一条合成与渐变（`LightColorFade.Toward`）。带过渡的请求在账本里带着剩余秒数，迟加入或回档的机器从当前进度渐变到合成值。恢复时被撤掉或回到更早版本的键会先把该区的灯交还关卡原值再重放。
- 检查点状态账本（`DomainStateChannel.RegisterNativeState`）里 Map 的键：`forge.map.interaction-policy`（门的四部分策略加交互点与原版交互触发器的开关；回档后逐项重放，取代原 `forge.map.door-interaction`）、`forge.map.trigger-zone`（每个已放置触发区的 `TriggerZoneClockState`，回档重建控制器后按区 id 套回）、`forge.map.door-lock-prompt`（门条件提示的已完成条件，主机由行为轨迹算出，各端从同一份记录写门上的剩余条件文字；晚加入、读档、换主机都沿用）、`forge.map.device-holds`（仍在生效的设备停用：类别、目标和剩余秒数的数组，取自主机的 `DeviceSwitchboard`，只带本世界的条目、最多 256 条；主机每次停用、续期或恢复后标记变化，其他机器收到后补上自己还没有的停用、撤掉文档里已不存在的那些，区域灯光还没建好时每 15 帧重试；换关和会话结束由开关板自己清）、`forge.map.door-alarms`（「更换门的警报」换过警报的门：门地址到所挂场景扫描资源 id，`Map/Doors/DoorAlarmRecord.cs`；主机每次换或换回后标记变化，各机器收到后自己调原版 `SetupForChainedPuzzle` 换上记录里的扫描、对不再列出的门换回本机生成时的那套；换关清空，换主机保留）。
- 主机表（`Map/Native/Host/MapNet.cs`）：Map 不开自己的通道，`MapNet.Open` 在引擎通道 `NAinfini.Forge`（`NetworkBinding.Channel`）上建两张表 `map/trigger-zones` 与 `map/timed-round-time`。只有主机写；ForgeCore 只发变了的行，给晚加入的玩家发整表，读档和换主机后给所有人发整表，换关清空，换主机不清。两张表之外，Map 要各端一致的状态都走上一条的检查点状态账本或领域命令。
  - `map/trigger-zones`（字节表，版本 1）：一行一个已放置触发区，键是该区在本关触发区表里的序号，值是 `TriggerZoneClockState` 的 JSON；引用文本资源的标签只带资源号，各端显示自己语言的文字。主机在放置完成、命令改了状态、路线走完、恢复检查点状态或接手主机后写这一行；其他机器从 `Changed` 应用，控制器放置晚于行到达时从 `Rows` 取。行里带区 id，序号在本机对应另一个区时拒绝（`trigger-zone-missing`）。行按到达顺序直接应用，读档会放回更早的版本号。逐次变化只走这张表：检查点状态账本里的 `forge.map.trigger-zone` 只随整份文档（检查点、迁移镜像、交给加入者的那份）走，不随每次变化进状态流；客机恢复一份文档后回到自己表里的行，因为后到的文档可能比先到的行更早导出。
  - `map/timed-round-time`（`NetTable<int, TimedRoundTime>`）：键是 `层 << 16 | 目标链序号`，值是阶段号（轮次乘二，等待确认时加一）和主机对该阶段计时起点累计挪动的秒数。各端的轮次计时各算各的，没有共用时钟，所以同步的是累计量：每台机器（含主机）把本机起点挪到这一行与自己已挪量的差。一轮开始时主机删行，客机取已经到达的行；主机删行时，已按旧行挪过同一阶段的机器挪回去；读档后主机清表。
- 触发区总数上限按关卡单算（每关 512），超限只拒绝向该关贡献触发区的包。
- `MatchBulkheadKey(keys, layer, dimension, localIndex)`：钥匙行按所在区优先认领同层钥匙，同区没有才取同层第一把。
- 作者出口点被拒时退回关卡自己的出口（日志一条 Error），不再压住原版出口的 Start。
- 被拒的定点敌人（睡眠状态未观察到、状态机缺失、类型不符）由 `FixedEnemyPlacement` 经原生复制的 Despawn 撤出世界。
- `InteractionPointJudge` 构造函数可选参数 `enabled`：交互点被关时 Started / Completed 以 `point-disabled` 拒绝。
- 按住时长由玩家自己的机器计时：完成请求早于房主这边的按住时长到达时（开始和完成两段路程不等长），房主回答 succeeded / `completion-held` 并记下到点时刻，由地图时钟在那一拍发布（`InteractionPointJudge.Tick`）；到点时交互点已撤、已关、玩家已换命或无人认领的，结束这次按住并在房主记一行。客机开始按住前读本机身份表里自己的生命编号（房主随会话状态发来的那份），不向房主问。
### 判定与雾的通用小块

- 触发区字段 `bodyHit`：判玩家或敌人从脚到 1.8 m、半径 0.35 m 的胶囊是否碰到这个体积，默认关：判脚到脚上 0.1 m 的竖直小段（`TriggerZone.FootLift`）碰没碰到体积，水平方向仍要脚点进区，底面贴在作者地面上的区在游戏地面略低时照样进得去，区顶以上不算。两种判定和两次判定之间的穿越检查用同一个线段对形状的算法（`Holds`、`Crossed`）。主机 10 Hz 判定，不增加命令。细长的体积（激光、绊线）打开它。
- `forge.query.environment.fog_at`（位置、维度 → 是否在雾里、当前雾编号）：低于这个维度实时的雾高度，且不在任何感染护盾球内。驱雾器无论放下、扔出、拿在手里都在 `EffectVolumeManager.volumes` 里。**未核验**：护盾体积的 `invert` 是否影响范围。
- `forge.query.entity.eye_position`（实体 → 眼睛位置）：玩家和敌人的观察器把原版 `Agent.EyePosition` 写进快照的 `EyePosition`，其它实体没有，读时以 `eye-position-unavailable` 拒绝。判断人在不在雾里时把它接给 `fog_at`，和原版感染取的点一致。

### 原版缺陷的默认修复（implementation-only）

默认生效，没有开关，不改任何数值。

- 读档重开后波次热度回到关卡开始：原版只在 `Builder.OnLevelCleanup` 里调 `EnemyPopulationManager.ResetHeat` 和 `ResetCooldownSpeed`（RVA 0x14753D0），读档不重建关卡，`EnemyPopulationManager` 也不在检查点捕获里，所以每次重试都带着失败那次攒下的热度。`CheckpointRestored` 生命期里主机调 `GlobalWaveApplier.CheckpointRestored`：`ResetHeat` 写回各类型的 `GetBaseHeatForType`（`Min(m_initialHeatMultiplier × m_heatTable[类型], m_maxInitialHeat)`，即关卡开始的值），本世界有作者开局热度时再写回它们。只在主机做（抽类型的是主机），日志 `map.wave-heat-reset`。

### 已知原版报错（不是生成器造成，不要在生成器里压制）

关卡日志里下面两条是原版在同样配置下就会打的，只要关卡带静态维度或维度传送门房间；排查地图问题时先排除它们。证据：Il2CPP 反编译 build 20403457（GameAssembly RVA 与 `dump.cs` 对照）加一份原版日志。

- `Found zone without sourceGate AND ID over 0, id: N`（`LG_Distribute_ProgressionPuzzles.Build`，RVA 0x14479C0）：循环里对每个 `LG_Zone` 判 `ID > 0` 且 `m_sourceExpander`（`LG_Zone` 偏移 0x70，日志文案写的是 sourceGate）为空就打这条。静态维度的区（`LG_ZoneJob_CreateZoneForStaticLevel`）从不由别的区扩展，没有来源连接件，`ID` 接在现实层各区之后，所以每个静态维度各一条。原版日志 `GTFO.2024.01.06.16.07.19_NA_infini_CLIENT.txt`（无任何模组的客户端）：现实层 17 个区加 4 个静态维度，打了 id 17 至 20 共四条，恰是四个维度区。V1 E1：现实层 9 个区加 `Dimension_1` 的静态区，打 id 9 一条。
- `ERROR : TryGetItemInLevelFromRelplicator : Faulty replicator id incoming! 0`（`LG_DimensionPortal.SyncStatusChanged` 经 `LG_DimensionPortalRoom.OnPostCullingBuildJob`，RVA 0x1442880 / 0x1440B90）：建关后处理直接调 `SyncStatusChanged`，当 `m_instertedPortalKey` 为空（建好时总是空）就用状态里还是全 0 的物品数据去查关内物品，复制器号 0 即报这条。每个维度传送门房间在建关时各一条，Map 不创建也不改这个门的物品数据。

`AssetShardManager.RegisterManifest` 的 NullReferenceException 不在此列：它由分片场景里 `AssetShardManifestLoader.Awake` 或 `AssetShardManifest.RegisterWithManager` 尾调用触发，Forge 代码没有任何地方调用 `RegisterManifest`，只有 ForgeCore `GameAssets.LoadShard` 经原版 `LoadShardAsync` 载入分片；起因（哪个分片的清单 `Assets` 为空）尚未定位，需要进游戏在该方法上记清单名才能区分原版或模组来源。

### 边界

原生适配器、地图 Action、房间与关卡生成器、资源 Adapter 和门/终端/警报/波次/环境等动作已在 `Map/Native/` 中实现，通过 Runtime 的统一入口注册；它们的自动化检查（`Mods/verify.py`）不启动游戏，报告里的 `nativeGameExecuted` 为 false 表示该次检查没有执行游戏，实机行为以合同 §4.5 剩余工作中列出的实测项为准。

所有写世界的动作必须由明确的权威提交。查询与生成批次使用稳定排序、实际 seed、显式预算和完整性结果；缺少合法落点、导航证据或目标 receiver 时返回原因，**不静默换目标**。

Room 不另建独立运行包，资产引用使用统一资源协议。已有的世界与生成观察在 Runtime 与 Development 的诊断代码里，**诊断观察不是可调用的地图执行器**。

### 复跑

所有检查都从仓库根经 [`Mods/verify.py`](../verify.py) 运行，它负责构建参数、环境变量和输出目录（`artifacts/mods/ForgeEngine/<运行名>/`）。`ForgeEngine` 一项跑引擎和四个领域的全部测试工程与原生证据检查：

```powershell
python Mods/verify.py --game E:/SteamLibrary/steamapps/common/GTFO --dump '<同 build 的 dump.cs>' ForgeEngine
```

`--bepinex` 默认是只读的 `Forge-Multiplayer` profile 的 `BepInEx`，这是 interop 身份唯一的冻结来源（`Map/evidence/map-hooks.json` 的 sha256 与 MVID 就是该副本的实际值）。只重跑 Map 的布局审计用 `ForgeEngine:MapNativeLayout`。它读构建出的 `ForgeEngine.dll`；单独用 `dotnet test` 时要自己设 `FORGE_ARTIFACTS`（= `artifacts/dotnet`）与 `FORGE_MAP_HOOK_SPEC`（= `Map/evidence/map-hooks.json`），都用绝对路径，测试进程的工作目录是输出目录。

原生 API 元数据复采（`Map/tools/Capture-NativeApi.ps1`，只用 Mono.Cecil 读元数据）后由 `Map/tools/verify_native_api.py` 与 `Map/evidence/native-api-20403457.json`、`Map/evidence/generation-api-20403457.json` 比对；`Map/tools/verify_native_regions.py` 复核 `GameAssembly.dll` 的原生区域。这三个都在上面的 runner 里。

生成与资源核验见 [Map/GENERATION-SPEC.md](Map/GENERATION-SPEC.md)。发布身份、真正加载、主客机和恢复都没有在游戏里跑过。InfiniTweaks 的 QoL 行为不属于 Map。

## Enemy

敌人实例、健康与战斗接收器、AI 与感知与移动、弱点与技能，以及生成的空间要求。遭遇的数量、时机和分布归 Map；Enemy 不复制地图的遭遇调度。

### 工程结构

源码在 `Enemy/`，命名空间 `ForgeEnemy` 与 `ForgeEnemy.Native`，全部是内部类型。引擎 `Load` 按固定顺序调 `EnemyHost.Start`（`Enemy/Native/Core/EnemyHost.cs`，排在 Player、Map 之后、Weapon 之前），它建 `EnemyPluginSession`、声明回调类、读敌人档案并注册内容，再以顺序 2、失败名 `forge.enemy.frame` 接进三个领域共用的帧入口。唯一生产接收器是 `Enemy/Native/Core/EnemyModule.cs`。`Enemy/` 下不在 `Native/` 里的部分（`ModuleDefinition` 的 provider 元数据、`Enemy/Spawn/` 的内容依赖计算与子代波次合同）也编进不碰游戏的 `ForgeEngine.Framework`。

宿主挂起时 Enemy 不启动，不注册也不声明 Hook。启动失败只记一条 `Forge Enemy domain failed to start`，回滚已建的会话，其他领域照常；原生 IL2CPP Hook 是进程级的，失败后要重启进程。日志级别就是引擎 cfg 的 `[Logging] Level`，随注册交给内核成为 `forge.module.gtfo.enemy` 的级别。

Enemy 自己的诊断都经这个 provider 的注册写成结构化记录（`RuntimeModuleHandle.Log`），代码与级别集中在码表 `catalog/authoring/diagnostic-codes.json`，`EnemyLogCodes` 的常量在构建前由它生成：拒绝与失败是 error（`enemy.disabled`、`enemy.profile-refused`、`enemy.limbs-failed`、`enemy.appearance-failed` 等），每种敌人一次的写入结果是 info（`enemy.content-loaded`、`enemy.profile-applied`、`enemy.appearance-applied`、`enemy.marker-styled`、`enemy.cap-cost-applied`、`enemy.hooks-installed` 等），逐生命的 `enemy.death-started`、`enemy.ability-index` 与 `enemy.cap-cost` 只在 trace。内核已记的事实（`trigger.fired`、`event.rejected`、`budget.exceeded`）Enemy 不重复写。值数组只在级别打开后才构造。BepInEx 日志只留下启动计时，启动失败由引擎记那一行。

### 当前 binding（全部 implementation-only）

Enemy 持有的 binding 以 `catalog/forge-release/runtime-manifest.json` 为准；原生 Hook 全部由引擎的 Hook 安装器装：Enemy 只声明回调类（`EnemyNativeHooks.Types`）并经 Runtime 的共享敌人伤害入口观察子弹与近战接收器，不装任何 Harmony 补丁；`EnemyWakeNoiseWindow` 的 `out float` 与 `EB_Patrolling.OnNoiseDetected` 的 `ref NM_NoiseData` 由 Runtime 对已审核的目标做有界桥接修复，其他按引用形态仍被拒绝。别的包也读的原版方法由 ForgeCore 挂一次、Enemy 只订阅（`EnemyNativeHooks.CoreSubscriptions`）：其中休眠敌人的 `EnemyAI.InjectPropagatedTarget`、`EB_Hibernating.OnNoiseDetected`、`ES_HibernateWakeUp.DoWakeup`、`ES_Hibernate.DoStartDetection` / `DoEndDetection` 与 ForgeReplay 共用。Runtime 保留世界与会话 Hook。

**部位外观与标记图标。** `forge.enemyAppearance.Looks` 的每条记录（`Target`、`Color`、`Glow`、`GlowBrightness`）给全身、六个身体部位、核心、两侧附肢或第 1 至 8 个肿瘤写表面颜色和固定发光；`Neck` 等同头部，全身条目不带发光，部位自己的条目逐分量覆盖全身条目。写入走已有的外观初始化 Hook（`appearance` 功能组），不新增 Hook：材质先由 `EnemyMaterialPass` 复制为这一世私有，随后只对有自己条目的身体部位把蒙皮网格拆成每个表面一个子网格（按每个顶点权重最大的骨骼所属部位分配三角形，同一源网格同一拆法只做一次并共用），再把设了颜色、发光或半透明的表面交给 ForgeVisual `SurfaceLook.Paint` 换成新材质（颜色按 sRGB 给、进游戏换线性；染色用游戏自己的表面材质 `Cell/CellSurface/Default`，只保留原材质的法线图与金属光泽图；发光经 `Glow.Write`，作者亮度 1 对应着色器倍数 0.02；0 到 1 之间的不透明度是 Unity `Standard` 透明模式的真半透明）。这种材质不登记进游戏的材质处理器，所以换了色的部位不再显示原版的察觉发光、泡沫胶、血迹和贴图细节。颜色、发光与不透明度怎样进材质只在 `SurfaceLook.Paint` 一处决定；原材质的着色器不在肉体与表面着色器名单里就按名字拒绝（`part-look-unsupported`），不换别的属性写。标记图标 `forge.eec.Model.Marker.Icon`（三角、圆、方块、菱形）在 `EnemyTagMarkerPlaced` 里把敌人图标的精灵换成游戏已加载的同名精灵并按原版大小对齐，`SpriteScale` 是原版大小的倍数。标记不写文字、距离与血条，也不用 `MaterialSets.ColorProperties`，所以不观察 `EnemyHealthSet`。附肢的左右按渲染器包围盒中心在模型坐标系的 x 符号判定、`core` 与附肢的分类都没有实机核对，登记在 `VALIDATION.md`。

**不透明度与导入模型。** `forge.enemyAppearance.Looks` 的每个目标（`full`、六个人形部位、`neck`、`core`、两侧附肢、`tumor1`..`tumor8`）都可以带 `Opacity`（0 到 1），与颜色、发光一样按 `full` 再到部位逐项覆盖。不透明度 0 的部位不画：身体渲染器沿用按骨骼拆网格（ForgeVisual `SurfaceLook.SplitByBones`），把该组三角形从拆分副本里去掉，全组都为 0 时整个渲染器停画；肉瘤与附肢是整块渲染器，直接停画。`full` 为 0 等于全部不画（肉瘤也一起），部位自己设的值优先。0 与 1 之间的值是真半透明（`SurfaceLook.Paint`：Unity `Standard` 透明模式，关深度写入，队列 3000，读回结果写进日志）。名字以 `NativeModelRuntime.CopyPrefix` 开头的挂件不受部位外观影响；命中盒、部位、动画和能力仍是原版的。作者变体的挂件可以带导入模型（`importedParts`）：模型文档、`.fmesh` 与 PNG 由 ForgeEngine 的 `ImportedModelFiles.ForEntry` 按哈希在配置文档所在包及其依赖包的 `forge/resources` 下找到并核对（配置文档与敌人内容包条目同一入口），缺文件报 Error `resource-missing`，随类型交给 `NativeModelRuntime.Prepare`；读坏的文件逐个写拒绝记录，其余挂件照常装。

**按功能撤回。** 回调类按玩家能感知的功能分组（其中侦察兵能力与扫描者能力各一组）（`Enemy/Native/Core/EnemyHookFeatures.cs` 的 `EnemyHookFeatures.Table`，一个类只属于一组）：敌人清单（所有功能的前提）、出生、伤害及其伤害种类、死亡、肢体打断、肢体数值、模型肢体、行为、侦察兵探测、侦察兵能力字段、扫描者能力字段、标记、胶、能力使用、近战、尖叫、休眠探测、投射物、雾、生产、释放玩家、攻击、噪声、警报、抓取、原生状态、屏幕占用、保持静止与唤醒规则、唤醒听觉、外观、动画、外观提示、波次、子代生成。Runtime 拒绝某个目标时，只有拥有它的那一组停下：该组的 binding 不注册（计划里用到它们的会被拒为 `binding-unavailable`，能力行仍在积木表里），该组的 Hook 函数体直接返回，并写一条 error 级 `enemy.capability-unavailable`（功能名、被撤回的 binding、玩家失去什么、Runtime 给的原因）。每一组都读敌人清单，所以清单组被撤回时 Enemy 的功能都撤回，但引擎照常加载；伤害组还依赖伤害种类组（子弹与近战的共享入口由 Runtime 先探测，被拒就撤回两组，不报一个编造的伤害种类）。已加载的敌人档案里写了内容、但对应功能被撤回时，启动时再写一条同码记录。`EnemyWakeNoiseWindow` 所属的唤醒听觉组因暂缓而每次启动都撤回，并因此每次启动都有一条 `enemy.capability-unavailable`。共享的 canonical 定义在 SDK 的 `CombatContracts` 里。可执行的绑定清单以 `EnemyModule` 注册表与导出的运行清单为准，下表是同一份清单的可读说明。

| binding | 已实现范围 | canonical / 权限 |
| --- | --- | --- |
| `damage_applied` | 观察指定原生承伤调用窗口的实际 HP 损失；未知攻击来源不反推；`equipment` 端口取自武器包写进 SDK 的开火关联（`CombatShotAttribution`，按接收器登记的 inflictor 查），没有在飞的开火时该端口不写 | `forge.trigger.combat.damage_applied` |
| `damage` | 存活敌人的多目标伤害：每个 target 先解析成**本 provider 自己的**存活实例、逐项校验接收器与生命状态，再经唯一原生入口 `Dam_EnemyDamageBase.BulletDamage` 提交，按实际读回的生命差报告；`mitigation_policy` 只支持 `receiver_rules` | `forge.action.combat.damage` |
| `heal` | 存活敌人的多目标治疗：每个 target 独立量化、提交前复核、实际读回；`overheal_policy` 为 clamp/discard/overheal，overheal 在 SFloat16 量化下结构性不支持，整条命令上游拒绝 | `forge.action.combat.heal` |
| `health_changed` | 两个来源：本 Forge 治疗读回的正生命变化；同一 `ProcessReceivedDamage` 调用窗口里观察到的实际 HP 损失（`value` 为调用后夹到 ≥0 的生命，`delta` 为负损失）。窗口里生命不变或上升不发布；其他模组、原生回复、`SetHealth` 等窗口外的变化不覆盖 | `forge.trigger.combat.health_changed` / `gtfo.enemy.health.read` |
| `death_started` | 同生命的 `OnDead` 正常返回且原生状态为 dead | `forge.trigger.enemy.death_started` / `gtfo.enemy.lifecycle.read` |
| `limb_broken` | 同生命、同 receiver 的索引部位在 `DestroyLimb` 窗口中由未破坏变为已破坏 | `forge.trigger.combat.limb_broken` / `gtfo.enemy.limbs.read`；输出 target 与 limb（可空） |
| `awakened` / `target_acquired` / `target_lost` | 在原生入口（`EnemyBehaviour.ChangeState`、`EnemyAI.SetDetectedAgent/SetTarget/InjectPropagatedTarget`）处采样；目标失效这类原生没有调用的下降沿，每 tick 只看持有锁的敌人；采样 `EnemyBehaviour.m_currentStateName` 的休眠边与 `AgentAI.Target`/`IsTargetValid` 得出的行为事实；首次采样是基线，不是转换 | `forge.trigger.enemy.awakened`、`forge.trigger.enemy.target_acquired`、`forge.trigger.enemy.target_lost` / `gtfo.enemy.behavior.read`、`gtfo.enemy.targeting.read` |
| `scout_detection` / `scout_scream` | `ES_ScoutDetection.OnTargetRegistered` 的发现事实，以及 `ES_ScoutScream.m_state` 的阶段变化（只在 `ES_StateEnum.ScoutScream` 下读） | 模块自有 capability（目录暂无行）/ `gtfo.enemy.detection.read`、`gtfo.enemy.behavior.read` |
| `attack_windup` | `ES_EnemyAttackBase.OnAttackWindUp`（vtable slot 33）被游戏分派：前摇开始，目标经内核实例解析器解析，解析不出来就不带该端口 | `forge.trigger.enemy.attack_windup` / `gtfo.enemy.targeting.read`；**目录行声明的 `ability` 端口不注册也不发布**（背后没有 `forge.resource.ability` 实体） |
| `attack_active` | `ES_EnemyAttackBase.OnAttackPerform`（slot 35）被游戏分派：攻击释放。零长度前摇时两个成员可在同一次调用里连发 | 模块自有 capability（目录暂无行）/ `gtfo.enemy.targeting.read` |

攻击三阶段里只有 windup 与 active 有可证实的原生成员：`attack_recovery` 在 build 20403457 里没有进入/离开恢复窗口的成员（`m_attackDoneTimer` 与 `m_attackWindupDuration` 全 dump 无读写点），**没有为此造事件**。同一 binding 同一 attack index 的重复读数不重复发布（水印随生命/世界丢弃），attack index 本身不作为端口输出（两行都未声明该端口）。


治疗使用已核验的 GTFO Steam build `20403457` 的 `SendSetHealth`，预检原生 `SFloat16` 精度并同步读回报告实际变化。小于精度的正治疗不能倒扣血；满血或零有效量不发送变化事实。未知攻击者不推断。

### 伤害动作

每个接收者都由本 provider 自己解析出的存活敌人实例决定：引用不是本模块当前生命（别的域、已退役、指针换了实例）、接收器没有 setup、接收器 owner 与实例不符、目标已死或生命值非正、生命/上限读不成合法值时，各自写一行 `rejected` 加对应 code，不做静默跳过。通过预检的接收者只经与伤害类型对应的原生入口（直击与其余类型 `BulletDamage`、近战 `MeleeDamage`、爆炸 `ExplosionDamage`）提交一次，随后按读回的生命值判定：真正下降才算 `committed`（`amount` 报请求值，实际值只体现在生命差里）；调用抛错、提交后实例或接收器变化、读回非法或上升一律 `unknown`，后续目标照常提交；原生调用已返回而生命没有变化，算该目标实际伤害 0（`succeeded`，code `no-health-change`，可能是锁血或规则抹平），后续目标照常结算。`explosion` 对敌人只有一条结算路径：原生 `ExplosionDamage`（主机与队友发起一致）；其包没有发起者字段，接收端把归因记到各自本机玩家，计划里的发起者只写进结果的 detail。爆炸带非中性的 `precision_multi` / `stagger_multi` 整条拒绝（`explosion-multipliers-unsupported`）。

**为什么主机闸门在本 provider 自己手里。** 冻结的解码证据（`Enemy/evidence/e10-damage-window.json` 的 q4 与 `Enemy/tests/NativeEvidence/NativeHealth.cs` 的窗口校验）显示：`Dam_SyncedDamageBase.Setup`（RVA `0x16201B0`）把 `m_onlyToMaster`（+0x25）写成“所有者类型是否为 PlayerBot”，而 `Dam_EnemyDamageBase.get_DamageBaseOwner`（`0x503EF0`）是 `mov eax,2; ret`（Enemy），所以本 provider 拥有的每个接收器该字段都是 0；于是 `SendLocally`（`0x161F4E0`）与 `SendPacket`（`0x161F5A0`）都在读网络标志之前直接返回真。**原生入口本身不区分主客机**——客机调用它会本地生效并发包——因此“只有主机应用”只能由本 provider 的门槛（`SNet.IsMaster`）保证，命令结果按权限失败语义（`authority-or-phase`，commitState 无）上报。

**来源与种类。** `source` 与 `instigator` 的实体引用由内核校验；提交时经该生命的所有者解析成原生攻击者。合同允许 `instigator` 不接，但敌人接收者无攻击者是否安全未核实（含击杀归属），未接明确拒绝为 `instigator-missing`（与玩家接收者同码），接了但已失效或认不出才是 `stale-or-unsupported-instigator`。硬直与攻击打断同样把 `source` 解析成原生攻击者交给受击反应入口，冲击方向与位置按攻击者相对被击敌人的站位计算；没有任何所有者认领的 `source` 才不传攻击者（方向为未指定），不伪造。`damage_kind` 是声明集合内的下标：本动作没有几何与倍率端口，集合里的每个种类都由同一个入口提交，越界仍拒绝。位置参数全为 0，因为动作没有命中点端口；肢体参数接受 id（`-1` 表示无肢体），由接收器的 `DamageLimbs` 数组解析成入口需要的位置下标，声明不了的 id 拒绝。

**死亡事实不是击杀。** `death_started` 不是 `forge.trigger.combat.killed`：从 `OnDead` 推不出攻击者、致命一击、奖励、伤害原因或尸体清理成功。同一生命只认领一次死亡窗口；重复与嵌套回调、未知完成、缺少消费者或队列拒绝都不会重开旧事件。

**肢体事实要求观察到真实转换。** 由 `Dam_EnemyDamageLimb.DestroyLimb` 的 prefix/postfix 捕获 false→true，同时核对 world/life、敌人指针与 ID、receiver、owner、`m_limbID` 与 `DamageLimbs[id]`。最多接受 256 个索引部位；超限、缺部位、换组件、读回不稳定或权限与阶段丢失时不伪造事实。同一生命内被其他模组恢复的部位不在当前的再破坏支持范围，新生命单独开始。原生调用抛错而没有正常 postfix 时不发布完成事实，也不自动补发。

root 与 cause 由 Runtime 原有的 Publish 调用链维护，native 外部入口不推断 source、owner 或 instigator。排队后如果原 target 失效，Runtime 在动作执行前拒绝旧引用，不改投新生命。

### 原生实体观察

Native 观察返回精确引用、位置、生命状态，以及有效的 `health.heal` 接收能力。**阵营保持 unknown，标签为空**——未知字段不从种类、名称或当前 AI 目标填补。坐标是当前 `EnemyAgent.Position` 值：这不是碰撞净空、出生适配性、历史成员资格、LOS 证据或作者到世界的变换。查询完整性只覆盖显式引用，从不代表枚举了全部敌人或某个空间区域内的全部对象。观察受 provider 现有的玩法门槛限制。

### 主机迁移

游戏先召回世界，Forge 再把内核换到新 world epoch（`WorldChanged` 且 `IsMigration`）。每台机器按同一方式跟随：像世界结束一样清空敌人表和各账本，把仍站着的敌人用 `TrackSpawn` 重新登记到新 epoch（保留 `spawn_origin`），再由内核紧接着还原的 `forge.enemy.action-state` 快照把动作状态、效果体积绑回这些敌人；快照里的句柄文本按新世界改写（`CurrentHandle`，槽位和生命不变）。泡沫记录（`GlueLedger.Follow`）和标记记录随敌人换到新引用；召回后不在的敌人，其泡沫与标记直接结束。属性修饰（`modifier`）在清空时按原生清除收回，进行中的能力使用行丢弃，这两样在迁移时提前结束。Enemy 向内核登记的唯一原生义务是 `marker` 句柄背后的标记批：`forge.enemy.markers/1` 交接在新世界里把同一批次重新绑回同一句柄；本机找不到批次时以 `migration-native-handle` 命名拒绝。敌人的 life 序号各机独立，迁移后内核按旧 life 序号持有的敌人级变量会被内核结束，这是已知边界。多人迁移只能手测，单机无法验证。

### `enemy-type` 挂载

计划的挂载目标 `{kind:"enemy-type", reference:"<十进制>"}` 由 Enemy 自己认领：只有 Enemy 登记的敌人实例、且它的官方 `EnemyDataBlock.persistentID` 正好等于该 id 时才匹配。`reference` 是该 id 的**规范十进制文本**（ruling 61）：`NumberStyles.None` 加往返相等意味着 `007`、`+7`、`-7`、超大值与小数都不是同一个 id 的另一种写法，它们能装进计划但永不匹配；空串与带空白的引用在计划加载期就被拒绝。主体不是本域的实体、实例已退役、或块读不出来（未 setup、空块、getter 抛错、读的中途被换）都答“不匹配”，绝不按名字、种类或当前 AI 目标猜类型。

读取只读 `EnemyAgent.EnemyData` 的 `persistentID`（interop `Modules-ASM.dll`），不写任何东西，读后回读块身份。**没有原生读取器的会话不注册这个 kind**：那种进程里带 `enemy-type` 挂载的计划在加载期以 `attachment-kind` 被拒，而不是被接受却永不派发。运行清单不记录挂载种类，导出工具因此不受影响。

### 内容依赖

`Enemy/Spawn/EnemyContentDependencies.cs` 按计划里实际的 binding 计算内容依赖：只有 provider 为 `forge.module.gtfo.enemy` 的 binding 才要求 Enemy 包，只引用模型或资源的外观内容不带入 Enemy 能力；计划的 `domain` 标签不算依赖。

### 接收器与会话的安全边界

改动时必须保持以下约束：

重复出生观察是幂等的——相同 native 实例保留原生命期，真实销毁后重生才分配新 life，**不按 managed wrapper 身份猜新生命**。销毁令牌捕获生命期：`CaptureDespawn` / `CompleteDespawn` 校验模块所有者、world/life 和 native 指针，旧令牌不能删同指针的新生命。伤害观察只消费一次：同一 token 的重复回调、零损失和被拒绝的回调都不重放，同 tick 两次真实独立伤害不会被合并。提交结果保持准确：发包后异常、receiver 或 owner 或上限或权限变化返回 failed 或 unknown，不伪造 actualAmount 也不重试；量化异常返回 rejected/none 且未发包，不匹配的 receiver owner 在提交前拒绝。

会话的回调与释放先核对模拟线程再执行。启动失败先锁上玩法门槛再回滚，即使注销受阻，残留的 receiver 也是惰性的。`EnemyHost.Start` 失败时在 finally 里清空静态 `Session`；清理失败经 `DomainSession.PreserveCleanupFailure` 附在原异常上，只读或抛异常的 `Exception.Data` 不能让 Session 保持发布状态，也不能掩盖主要失败。异常 Message 抛错、为 null 或过长时仍有有界诊断，不形成反复执行或记录的循环。
### 复跑

从仓库根经 `Mods/verify.py` 运行，`--bepinex` 默认是只读的 `Forge-Multiplayer` profile 的 BepInEx：它与构建引用、`Enemy/evidence/native-api-20403457.json` 冻结的三个 interop 程序集是同一份副本。Enemy 的检查都读构建出的 `ForgeEngine.dll`，输出在 `artifacts/mods/ForgeEngine/<运行名>/`，不写入任何 profile。单项重跑用 `ForgeEngine:NativeEvidence`、`ForgeEngine:NativeLayout`、`ForgeEngine:HookInstall` 与 `ForgeEngine:NativeEvidence verify_negative_cases.py`（`Enemy/tests/NativeEvidence/verify_negative_cases.py`，故意坏的输入必须被拒）。

### 合同类

`EnemyContentDependencies`（由资源修订与计划文本算出一份内容需要哪些包：是否要敌人包、用到的敌人 binding、其他 provider、资源修订）、`EnemyCombatContract`、`EnemySelectorContract`、`EnemyNodeValueContract`、`EnemyNodeEffectContract`、`EnemyVolumeContract`、`EnemyPushContract`、`CombatDamageRuleContract` 是内部类型，给 Enemy 的 binding 声明与校验共用，不复制 canonical 行文本。能力行本身来自生成的积木表（`ForgeEngine.Framework.Blocks`），结果码来自同一份表的 `ResultCode`；这些合同类只带 binding、处理方法形状和权限。

`effect_volume` 只在主机执行，但每台机器各放一份：主机登记的体积随动作状态快照（`forge.enemy.action-state` 的 `volumes`）同步，客机按快照放置自己的雾球和效果体积、释放主机已释放的，跟随敌人的体积由本机用本机敌人移动；锚点敌人在本机尚未生成时等它生成。`mark` 不写时长（缺省或 0）时标记保留到敌人这条命结束或句柄取消。
### 边界

以上全部是 **implementation-only**：源码存在、托管测试通过、原生签名与元数据已静态核对，**没有一条完成 GTFO 实机验收**。原生读取、替身、编译和元数据是不同的证据层，任何一层通过都不代表游戏或多人已通过。下一个门槛是真实游戏加载、原生 Hook 与主客机验证。

Boss 阶段与未进入 `EnemyModule` 注册表的其余敌人事件未实现：它们要么需要 SDK 新增 canonical 合同或事件端口类型，要么没有冻结的原生 Hook 与语义证据。**伤害动作**同样只到静态证据、编译与托管替身等级：真实 `BulletDamage` 调用、护甲与肢体规则对实际数值的影响、宿主与客机的复制时序、以及“无攻击者”在真实接收器上的表现都没有在游戏里跑过。

### Scripted entry

`ForgeEnemy.Native.EnemyControl`（`Enemy/Native/Control/EnemyControl.cs`，`internal static`，由 `drive_cli.py call static:ForgeEnemy.Native.EnemyControl <方法>` 经反射调用，只在主线程，每个方法答一行）：`List(count)` 与 `Tag(globalId)`（游戏自己的 `SyncPlaceNavMarkerTag`，读回标记显示的精灵）。

## Weapon

武器、工具、消耗品共用一个装备领域与 Workshop：装备实例、输入与攻击、弹药与能源与库存、部署与回收、装备表现。时间、状态、目标与事务的基础由 Runtime 提供，**不为每种工具或药剂复制一套框架**。

### 工坊里的"标准武器"本身就是一张图

内置预设与玩家自制的行为共享同一条编译、校验、执行路径。治疗炮台、命中标记、ping 不是三个系统，是同一批原子的三种拼法——治疗炮台 = 部署动作 + 范围查询 + 关系过滤 + 周期 Control + 治疗 Action，外观在工坊里配。**任何为内置预设开的特权通道都是设计缺陷。**

这条对 Weapon 的含义是：Weapon 提供的是真实的装备实例、成本、攻击与部署这些**领域能力**，不是成品功能开关。玩家怎么把它们拼起来由 Trigger 的图决定。

### 运行清单与装备身份

`ModuleDefinition.Create()` 注册 provider `forge.module.gtfo.weapon`，并注册观察型（`role=observe`）与动作型（`role=execute`）binding，完整清单以 `catalog/forge-release/runtime-manifest.json` 为准，这里不写数量；观察型 trigger 包括：装备切入/切出（`forge.trigger.input.equipped`、`forge.trigger.input.unequipped`）、开火与命中候选（`forge.trigger.combat.shot_committed`、`forge.trigger.combat.hit_candidate`）、实体消失（`forge.trigger.entity.despawned`）、部署与回收完成（`forge.trigger.equipment.deploy_completed`、`forge.trigger.equipment.recall_completed`）。这些观察型 trigger 的形状由 runtime 的 trigger 合同 `forge.contract.trigger` 声明，Weapon 不声明 capability 行。bindingSupport 均为 **`implementation-only`**，所需权限 `gtfo.equipment.wield.read`、`gtfo.weapon.combat.read`、`gtfo.equipment.deployable.read`。

只注册 runtimeBinding，没有 previewBinding。**观察即事实**：观察型 binding（开火、命中候选、部署、回收与消失等）都只发布事实，不施加伤害、不消耗弹药、不写背包、不改部署状态；原生观察由 `WeaponHost.Start` 接线（implementation-only，见下文）。

`deploy_completed` 声明 `actor`、`deployed` 与 **`optional` 的 `position`（`vector3`，单位 `m`）**：**`deployment:handle` 端口没有注册**，Weapon 不声明 handle 端口。位置取自**这次 spawn 出来的那个世界实例自己的 transform**，不是放置请求里的坐标：全镜像只有 `SNet_ReplicationManager_Gear::GetInstanceReplicator` 会给部署实例写位置，写的就是 `pGearSpawnData` 带的那一份，部署物自己的代码从不移动自己（见 `Weapon/evidence/w4-deployable-hooks.json` 的 `positionAtSpawn`）。它是 `optional` 而不是必有或者 `nullable`：复制器工厂与同一个复制操作的 spawn 回调之间的顺序是结构性的、没有可执行证据，读不到有限世界坐标时这个端口**整条不出现在事实里**，而不是发一个 null 或用请求坐标冒充完成位置。`recall_completed` 只声明 `actor` 与 `deployed`：**返还数量端口没有注册**，它属于本 provider 刻意不复制的那份背包与弹药状态，声明一个永远发不出的端口等于发不出的承诺。`hit_candidate.target` 在注册图里是 **`optional`（不是 `nullable`）**：命中候选在任何伤害结算之前产生，命中对象的原生类型决定它属于哪个域，解析不出来时这个端口**整条不出现在事实里**，而不是发一个 null 引用冒充真实引用。`hit_candidate.equipment` 则是**必有**端口：命中候选只在开着的开火窗口内产生，所以它一定说得出开这一发的那条装备生命，取值就是同一发 `shot_committed.equipment` 的同一个引用——取自窗口本身，不从命中对象反推，挂在那个装备块上的行为正是靠它认领自己武器的命中。

输入类 trigger（`equipped`/`unequipped`）与其余装备与战斗事实一样：形状由 runtime 的 trigger 合同 `forge.contract.trigger` 声明，Weapon 只注册 binding。一个 id 只有一处声明，所以这里没有第二种拼法。

`ReloadInventoryContract` 绑定另一族观察型 trigger：装填三行（`reload_started`、`reload_transferred`、`reload_completed`）、库存与使用五行（`stack_changed`、`picked_up`、`dropped`、`use_started`、`use_failed`）、大件物品一行（`forge.trigger.player.carried_item_changed`），以及弹药池一行（`forge.trigger.combat.ammo_changed`，`reason` 端口区分补给 `refill`、拾取 `pickup` 与消耗 `consume`）。**十行的形状全部由 runtime 的 trigger 合同（`forge.contract.trigger`）声明**——`Tools/Forge/build-primitive-runtime.ts` 按真源里每条的归属生成积木表（`Blocks`）、`TriggerContracts` 按行读取，Weapon 只提供 binding、handler 名与权限。所以行文本只有 `Tools/Forge/capability-copy.json` 一份、端口表只有 reviewed Primitive 一份；改文案改前者，改端口改后者，这里写不出第二份。

槽位行与弹药行都从**已经装好的 Hook** 对账，不新增观察者：槽位行读 `PlayerInventoryBase.WieldedSlot`（值是原生 `InventorySlot` 数字，`previous` 是这台机器上次读到的值；玩家首次出现、或换世界后的第一次读回只记录不发事实，读不到就不动已记录的值），弹药行读每个槽位的 `BulletsInPack`（`value` 是变化后的发数、`delta` 是变化量，增加与减少都发；槽位没有活装备生命或不是本 provider 的池时只报告、不发）。弹药行的读回点是 `PlayerAmmoStorage.UpdateBulletsInPack`（读回 Hook：投掷成本、放置成本、资源包发放、屏障／绊雷／哨戒收回都走这个体）、`SetClipAmmoInSlot(InventorySlot)`、`SetStorageData` 与 `PlayerBackpack.CreateAndStoreBackpackItem`；**没有读回点的池写入（`SetAmmo`、`PickupAmmo`、`FillAllClips`、`UpdateAmmoInPack`、`Reset` 与远端同步的 `ReceiveAmmoSync`）与其余未核验项记在 `Weapon/evidence/inventory-change-facts.json`**，不在本文里当作已覆盖。

`ammo_changed` 的 `pool` 端口是 resource：kind 是框架词表里的 `pool`，id 是槽位自己的 `InventorySlot` 名字（`GearStandard`、`GearSpecial`、`GearClass`、`ResourcePack`、`Consumable`、`ConsumableHeavy`）。本 provider 因此是这一个资源 kind 的 owner（`ModuleDefinition.Create` 的 `ResourceProviders`）：kernel 通过 kind 的 owner 解析每条已发布事实里的 resource 值，没有 owner 的事实会被 `stale-resource` 拒收。池不按玩家分身份——玩家在事实自己的 `actor` 端口里，因为计划的 resource 输入是装载期解析的编译期引用，按玩家的实例 id 无法写成字面量。

事实载荷的线上形态与 kernel 的校验一致，否则 kernel 把整条事实记成 `event.rejected`：枚举端口（`aim_state.phase`、`ammo_changed.reason`、`use_failed.outcome`、`charge_state.phase`、部署事实的 `equipment_kind`/`equipment_action`）发布的是成员在共享枚举集里的下标，下标由 `WeaponEnumMembers.Index` 从 SDK 自己的表一次解出，不写成名字；`nullable` 但非 `optional` 的端口（`melee_push.target`、`melee_hit.limb`/`charged`）没有值时写显式 null，不能省略。在原生方法体里触发的事实（`flashlight`、`use_started` 等）取装备引用时，`EquipmentNativeAdapter.CurrentEntityOf` 会先让该装备的记录对齐游戏当前状态：装备/卸下/换槽的原生体比 Weapon 的 reconcile 钩子先跑，记录里的「是否在手」会比游戏落后，直接取旧记录发布会被 kernel 判成 `stale-entity`；对齐后若游戏已不再持有这件装备，则不发布这条事实。

生产程序集内的装备身份托管实现只有显式创建 Session 才生效：

`EquipmentObservation.cs` 把 `EntityReference`、资源 ID 与 revision、显式 owner、slot、Inventory/World/Deployed 位置、加载与持有状态分开，没有另造跨域的 EntityReference。`EquipmentIdentityId.cs` 是本 provider 唯一的 id 形状定义：命名空间固定 `gtfo.equipment`，**背包装备生命**是 `gtfo.equipment:<world>.<life>`（两段数字），**部署出去的世界实例**是 `gtfo.equipment:<world>.<life>.<instance>`（三段数字，接在放置它的那条生命之后）。两种形状互不重叠，`EquipmentIdentityIndex.cs` 只接受生命形状，世界实例由原生接线自己的放置表回答。`EquipmentIdentityIndex.cs` 维护有界的活动实例与槽位索引及已观察的生命历史；同槽占用冲突原子拒绝，资源版本不能在同一生命内偷换，移除必须匹配完整引用，旧的 despawn 不会删除复用键上的新生命。`EquipmentIdentitySession.cs` 显式注册同一个 `forge.module.gtfo.weapon` provider 的 `gtfo.equipment` resolver：**一个命名空间、一个 resolver、一个 observer**，收到引用后按 id 形状分派到对应的表（生命查索引与世界背包，世界实例查放置表），因此一次查询不可能把装备生命读成世界实例或反过来；世界切换、失败、停止和失去主机权限之后的记录由 `ObserveLifecycle` 清理。

`EquipmentUseTicket` 是**仅进程内**的前置条件快照：任何已观察到的 owner、slot、location、readiness 或 wield 变化都让旧票据失效，A→B→A 和卸下再装备都不复活旧票据。**它不是权限、不是预留、不是成本收据、不是网络身份、不是检查点数据**，不能序列化成共享的所有权合同。

`Record` 只在同一生命、同一 owner、同一槽位且都在 Inventory 时，直接观察到 `IsWielded` 翻转，才发布 equipped 或 unequipped 事实。初始快照、移动、转交和新生命都不发布。

当前必须在 Runtime 的注册窗口内显式创建 Session 并传入引擎 cfg 的 `Logging.Level`（随 `RegisterModule` 交给内核）与两个真正核验当前原生实例和玩家生命的探测函数。原生接线里这两个函数分别是 `EquipmentNativeAdapter.IsNativeCurrent`（严格读回）与 SDK 的 `RuntimeKernel.IsEntityCurrent`（owner 是 Player 域登记的 `gtfo.player` 引用，由其拥有者核验）。写入与解析要求 Runtime Ready 且已完成首个 host tick，未初始化、客户端、未知权限与注销状态都不接收记录。示例测试里的永真探测**只能用于合成输入，绝不能作为游戏默认实现**。

索引不生成世界、生命或资源身份，也不从 slot、模型、资源名、owner 或裸指针推断另一个身份。新实例必须由原生接线提供经核验的新引用；同一 ID 必须在精确退役之后以更大的 lifeEpoch 再出现。历史到预算上限时明确拒绝，**不淘汰旧记录后放行重放**。探测期间重入 Record / Remove / Dispose 会被拒绝；探测中发生世界切换、停止或注销会重新检查，迟到的观察不能写进新世界。

**别的玩家看到的模型。** 目录记录 `PlayfabItemInstanceId` 只在本机目录里；别的玩家那台机器按复制来的部件分量重建枪，分量里有 `GearCategory`（每把工坊武器一个）而没有这条记录。`WeaponModelAssemblies` 因此另存「类别编号 → 块 id」（`With(..., category)`，注册内容时由其 `GearCategory` 块给出），`BlockOf(holder)` 先读记录、读不到再按类别找，别的玩家手里的同一把枪才挂同一个模型。部件姿态与手电（`GearPartsSpawned` 钩子里取一次 `BlockOf`，交给两个应用器）同样按类别找块，别的玩家那台机器也生效；字段覆盖（伤害、弹药、噪声等，涉及权威）仍只认记录，仅本机生效。

**模型挂件里的导入模型。** 武器内容条目的模型挂件可以带导入模型（`importedParts`）：`WeaponModelAssemblies.Prepare` 用 ForgeEngine 的 `ImportedModelFiles.ForEntry` 读条目所在包 `forge/resources` 下的模型文档、`.fmesh` 与 PNG，交给 `NativeModelRuntime.Prepare`；读坏的文件逐个报出，缺文件的那件在摆放时跳过并点名，其余照常挂。

### 原生观察接线（implementation-only）

原生半边在 `Weapon/Native/`，命名空间 `ForgeWeapon.Native`，只编进 `ForgeEngine.dll`，不进不碰游戏的 `ForgeEngine.Framework`。玩家经 `PlayerHost.Directory`（`IPlayerDirectory`：玩家引用的两个方向、持有者会话号、本机判定、本机玩家）取得；Player 没启动成功时它以 `player-directory-missing` 拒绝，Weapon 只拒绝需要它的那次观察，并只写一条 error（`WeaponLogCodes.ObservationRejected`，reason 为该码）。

组成：
- `WeaponHost`（`Weapon/Native/Host/WeaponHost.cs`）：引擎 `Load` 按固定顺序最后调 `WeaponHost.Start`。宿主挂起时不启动、不装 Hook；启动失败回滚已建的会话并保留原始异常（清理失败经 `DomainSession.PreserveCleanupFailure` 附在上面），引擎记一条 `Forge Weapon domain failed to start`，其他领域照常。日志级别就是引擎 cfg 的 `[Logging] Level`，随注册交给内核成为 `forge.module.gtfo.weapon` 的级别。被撤回的功能各写一条 `weapon.startup` error。
- `WeaponNativeSession` 按固定顺序启动：注册窗口内先注册身份 Session（重复 provider 在装 Hook 前失败），在同一个注册上声明 `definitionFields`（`WeaponDefinitionFields.All`），然后一次性读 `forge/weapons` 文档、装 Hook；失败时回滚，释放时先注销再卸 Hook。任何意外异常都锁存故障，并清空句柄表。
- `WeaponNativeHooks` 有 23 个 Hook：21 个 `Priority.Last` 的 postfix-only Hook 只读回、不改参数或返回值，另加装备池的一对（一个 `Priority.Last` postfix、一个 `Priority.First` prefix，见下节）：
  - `PlayerBackpack.CreateAndStoreBackpackItem` / `TryClearSlot` / `DestroyAllInstance` / `SetDeployed`
  - `PlayerInventoryLocal.DoWieldItem` / `UnWield`
  - `PlayerInventorySynced.DoEquipItem` / `UnWield`
  - `BulletWeapon.Fire` / `Shotgun.Fire` / `BulletWeaponSynced.Fire` / `ShotgunSynced.Fire` / `BulletWeapon.BulletHit`：Slot 151 的四个 `Fire` 体各挂一个 postfix，每个 `Fire` 体记一发并开火，命中候选认领最近一次开火。四个体互不调用（见 `Weapon/evidence/w4-player-fire.json`），所以"一个 `Fire` 体一次调用＝恰好一条 `shot_committed`"，既不需要去重也不会漏记：玩家自己的武器走未同步的两个体（`BulletWeapon.Fire` / `Shotgun.Fire`，它们把这一发登记进 `PlayerSync`），本机持有的"别人的武器副本"走同步的两个体（由 `PlayerInventorySynced.GetSync` 把复制过来的射击数喂给 `OnSyncFire`，再逐发调用 `Fire`），`RifleWeapon` 与 `RifleWeaponSynced` 不重写 `Fire` 因而用各自家族的那个体。没有开火窗口的命中（哨戒自身的 `SentryGunInstance_Firing_Bullets.FireBullet` / `UpdateFireShotgunSemi` 直接调同一个静态命中函数）丢弃并写 `weapon.hit-outside-shot`。
  - `SentryGunInstance` / `MineDeployerInstance` 的 `OnSpawn`、`SyncedPickup`、`OnDespawn`（矿没有）、`OnDestroy`：部署物世界实例的生命。
  - `Gear.GearPartHolder.OnAllPartsSpawned`：写原生状态的表现层 Hook（见下节）。它不读回、不发事实。Weapon 另有七个写原生状态的 postfix（作者写的 Forge 块字段，见下一节）。
- `WeaponFieldDocument` / `WeaponDefinitionFields` / `WeaponFieldApplier` / `WeaponFieldHooks`：**作者写的 Forge 块字段**。网站把 `forge.*` 块字段写进一份领域档案文档 `BepInEx/plugins/<包的插件目录>/forge/weapons/<离线装备 id>.json`——与 `forge/plans/` 同一个包目录规则（`plugins/` 下**一层**目录，不递归）。形状 `{ schemaVersion: 1, weapons: [ { <对象键>, blocks: { <块 id>: { <字段 path>: <值> } } } ] }`，块 id 与每个 path 逐字取自 `catalog/authoring/object-fields.json`（host=weapon）：不在表里的块或 path、形状不符的值按名字拒绝，绝不静默忽略。对象键是 `itemId` / `offlineId` / `archetypeId` 之一，一份文件只用一种拼法；一个原生对象在整个安装里只能有一个条目，跨文件重复时该对象**整体撤回**（`weapon.field-duplicate`），不按扫描顺序决胜。`WeaponDefinitionFields.All` 就是各包在 I-MANIFEST 里报告的那份 `definitionFields`，注册期由 `EquipmentIdentitySession` 交给内核，包测试证明它与应用器处理的块路径逐项相等。
- **字段的应用点**：十个 postfix（`HarmonyPriority.Last`，声明在 `Weapon/Native/` 的 `WeaponFieldHooks.cs`，只编进 `ForgeEngine.dll`）：八个原生 `Setup`（`ItemEquippable`、`EnemyScanner`、`MineDeployerFirstPerson`、`MineDeployerInstance`、`SentryGunFirstPerson`、`SentryGunInstance`、`LockMelterFirstPerson`、`HackingTool`），投出的驱雾器 `FogRepellerInstance.Start`，以及本机玩家选中计时交互时的 `Interact_Timed.OnSelectedChange`（只改锁上的 `Interact_Hack`）。每次都是一次绝对值写入。写的是调研标为 `identical` 的本地表现与本地状态：扫描仪的锥角/范围/延迟、地雷的爆炸与粘胶数值、激光束的长度与颜色宽度、地雷灯的颜色强度范围、哨戒的扫描延迟与扫描面/锁定目标颜色、放置与回收时长、驱雾器球半径、熔锁器熔锁时长与骇入工具的骇入时长（记录在 `HackingTool.Setup`，写到被选中的锁的骇入交互上）。字段表给了 `min` 的数值低于下限时读档即拒（`field-range`）。`host-replicated` 字段一律不写。进程级成员（`EnemyScanner` 的几个 static、骇入时长）在文档里只允许一个值：第一个被写入，之后的另一个值丢弃并写一次 error 记录 `weapon.field-rejected`；实例上缺少对应组件时写一次 `reason=unavailable`。证据链见 `Weapon/evidence/w8-forge-field-apply.json`。
- `GearPartTransformData` / `GearPartTransformApplier`：上面那份文档里的**部件姿态**，块 `forge.gearPartCustomization.Block`。启动时一次性读成不可变快照（不监听、不热重载、没有第二个来源）；坏文件按**文件**粒度拒绝（其它文件照常加载），每份写一次 error 行 `weapon.field-<code> file=<相对路径>: <原因>`，`<code>` 取 `json`、`schema`、`key`、`key-mixed`、`blocks`、`block`、`path`、`type`、`conflict`、`limit`、`size`、`unreadable`、`duplicate`；姿态自身的取值或词表问题按对象拒绝一次，写 `weapon.gear-part-pose file=… block=…: <原因>`。
  - **`Parts`**：每个部件槽一条，`PartType` 是原生 `eGearComponent` 成员号（只有 `GearPartHolder` 真的持有 `GameObject` 的那 21 个槽位，数值与 `Modules-ASM` 逐成员核对），姿态字段是 `Enabled`、`LocalPosition`、`Rotation`、`Scale`，子物体是 `Children[{ChildName, 同样字段}]`；子节点自己还可以再带 `Children`，**整棵树都会应用**，每个节点用它从部件起算的完整路径解析。所有值是**绝对值**（本地空间），所以同一个 holder 再出生一次、图标再渲染一次、检查点重载都写下同一组数，不会累积；**文档没写的字段一个都不写**，游戏自己的姿态原样保留。
  - **上限**：单文件 512 KiB；每文档 ≤ 256 个条目、每条目 ≤ 32 个块；每个列表 ≤ 64 项；`Parts` ≤ 64 条；`AddedParts` ≤ 16 条；`LocalPosition` 每轴绝对值 ≤ 1 m；`Scale` 每轴 ∈ [0.05, 4]；欧拉角规范到 [0, 360)；`ChildName` 按**从部件起算的完整路径**计段（嵌套的父段计入）≤ 8 段，且每段非空、不是 `.` 或 `..`。
  - **`AddedParts`**（作者新加的部件，可选，每个对象 ≤ 16 件）：`{Name, PartType, PartID, Variant?, ParentType?, LocalPosition?, Rotation?, Scale?}`。`Name` 是作者给这件部件起的名字（1–32 位字母数字、`-`、`_`，同一对象内唯一），模组用它给复制品命名 `ForgeExtra.<Name>` 并在下次出生时按名替换；`PartType` 是有自己部件块的 16 个部件组件之一（5 个附件槽没有块，拒绝），`PartID` 是该组件的部件块 id；`ToolDeliveryPart` / `ToolPayloadPart` / `ToolTargetingPart` 的块按类型各存一个模型，**必须**用 `Variant`（`eGearToolPart*Type` 成员名）指明，其它组件**不许**带；`ParentType` 是挂靠的部件槽号，缺省挂在整件装备上；姿态是挂靠节点下的本地绝对值，上限与部件姿态相同，缺省为单位姿态。
  - **新加部件的应用**：在部件姿态之后逐件挂。读部件块的 `General`（或类型条目的 `Data`）拿到模型路径与资源分片；分片已登记 manifest 且模型已加载就立即复制并 `IncreaseShardDependencyCount` 钉住；否则（包括模型仍在查找表里、但分片没有 manifest 也没有正计数——原生 Unload 先移除 manifest、场景稍后才卸，或遗留登记）按分片 `LoadShardAsync` 一次，等待同一分片的所有新加部件在回调里一起挂。等待期间 holder 或挂靠部件被游戏销毁、会话／世界结束、同位置被新请求取代，该件记为取消：写一次 `weapon.gear-part-extra-cancelled: block=… name=… phase=…: <原因>` 控制台 info 行（游戏销毁菜单预览／图标 holder 是常态，不是问题），不是 I-DIAG 错误，也不计入已挂。分片的钉住记录 `weapon.gear-part-shard-pin: … receipt=… state=… reason=…` 每个收据在获取时说一次（info）：游戏尚未加载的分片由 Forge 自己 `LoadShardAsync`，原生把计数置 1 但此时还没有 manifest，没法证明可独立释放，所以保留加载、Forge 从不卸载（`independent-pin-unproven`）；世界／检查点边界后已证明的增量也只保留不释放（`owner-generation-unproven`）。这些都是设计内的保留，同样只写 info；原生读数失败、分片身份变化、复制品存活、非正计数、卸载结果未知等才是控制台警告。从未取得所有权的收据在释放时不再重复说一遍。挂靠部件仍在却不再位于 holder 之下才算 `weapon.gear-part-extra-refused`。复制品在一个不激活的暂存物体下 `Instantiate`，所以部件自带脚本不会 Awake；随后只保留 `Transform`、`MeshFilter`、`MeshRenderer`、`SkinnedMeshRenderer`、`LODGroup`，递归取挂靠节点的 layer，命名 `ForgeExtra.<Name>`（同名旧复制品先 `DestroyImmediate`，再出生不叠加），挂到节点下写姿态，最后用 holder 自己的 `GearPartSpawner.UpdateMaterials` 上装备材质。新加部件不进 holder 的部件槽，不改握把、对齐点、枪口或准星，也不改任何数值。诊断：挂靠槽位为空 `weapon.gear-part-extra-parent-missing`、生成器拒绝 `weapon.gear-part-extra-refused`、材质失败 `weapon.gear-part-extra-materials`，每件一次。生成器是 `Weapon/Native/Content/GearPartExtraAssets.cs`，`WeaponNativeSession` 直接建它交给部件应用器。
  - **写集**：姿态恰好四个成员 `Transform.localPosition`、`Transform.localEulerAngles`、`Transform.localScale` 与 `GameObject.SetActive`（本 build 的 interop 没有 `active` 属性，`SetActive` 是唯一拼法）；新加部件另外用到分片加载与钉住、复制、剥离、命名、layer、父子与装备材质（见 `Weapon/evidence/w6-gear-parts.json` 的 `writeSet`）。不发事实、不走网络、不碰游戏状态，因此多人一致靠"同一份包＝同一份数据"。
  - **挂点**：块 id 从 holder 的 `GearIDRange.PlayfabItemInstanceId` 取，走的是与 `gear-block` 挂载**同一个** `EquipmentNativeAdapter.GearBlockId`，所以块 id 在全包只有一种拼法。子物体按作者写的相对路径用游戏自己的 `Transform.Find` 查找，**嵌套的 `Children` 整棵树都会应用**；找不到写一次 `weapon.gear-part-child-missing: block=… component=… path=…`（每把枪各一次；写了部件而该槽没有部件时同样每把枪一次 `weapon.gear-part-missing: block=… component=…`；缺失节点自己的子树随之跳过：它下面不可能解析得到），并继续处理其它条目。应用成功每个 holder 写一条 info 记录 `weapon.gear-part-applied`（`values`：block、parts，有新加部件时再带 `extras=<已接手>/<总数>`）；启动时一条 `weapon.content-loaded` 记下接受的块数与对象条目数。
  - **作用范围**：所有本机可见实例各组装自己的 holder（第一人称、他人第三人称副本、菜单预览、图标渲染、部署哨戒），各自都会命中这个 Hook，因此都会被同一个文档摆成同一个姿态、挂上同一批新加部件。
  - **宿主挂起**：引擎不启动 Weapon，所以这时 Weapon **既不读这些文件也不装这些 Hook**，任何一个部件都不会被摆位、任何块字段都不会被写入——与其它原生 Hook 完全一样的规则，没有 Weapon 自己的开关。证据链见 `Weapon/evidence/w6-gear-parts.json` 与 `Weapon/evidence/w8-forge-field-apply.json`。
- `GearPartFlashlightApplier`：**武器手电用哪一行**，块 `forge.vanilla.flashlight`，路径 `m_settingsID`（原版 `FlashlightSettingsDataBlock` 的行 id）。
  - **原版引用在哪**：没有任何 DataBlock 字段指向手电行；引用是手电部件预制体上 `Gear.GearPartFlashlight.m_settingsID` 的序列化值（Flashlight_A→2、B→3、C→5、D→6、E→7），`ItemEquippable.OnWield` 把这个组件交给 `PlayerInventoryBase.OnItemEquippableFlashlightWielded`，由它 `GetBlock(uint)` 取行并写灯的距离、光圈、颜色、亮度与 cookie。所以装同一种手电部件的武器共用一行。证据见 `Weapon/evidence/flashlight-settings.json`。
  - **怎么只改一把**：数值只在原版 `FlashlightSettingsDataBlock` 里——网站把该武器手电部件引用的那一行复制成新行，新行 persistentID 取这把武器自己的离线装备 id（与其它复制出的原生行同一规则），Weapon 不读任何数值。文档里这把武器（必须以 `offlineId` 为键）只写新行的 id；`GearPartHolder.OnAllPartsSpawned` 的同一个 postfix 在部件姿态之后，把**这个 holder 自己生成的**手电部件实例上的 `m_settingsID` 改成这个 id。预制体和其它武器的 holder 不动。
  - **拒绝**：以 `itemId`/`archetypeId` 为键的条目按文件拒绝（`weapon.flashlight-settings file=…`）；装备没有手电部件（`weapon.flashlight-part-missing`）、部件上没有 `GearPartFlashlight`（`weapon.flashlight-component-missing`）、`FlashlightSettingsDataBlock.HasBlock` 为假（`weapon.flashlight-settings-missing`）时每个装备报一次、不写，部件保留原来的行。写入成功写 info 记录 `weapon.flashlight-applied`（`values`：block、settings、previous，另有 lightMoved、shellHidden），启动时的块数在 `weapon.content-loaded` 里。同一个 `forge.vanilla.flashlight` 块还可写 `LightPosition` + `LightRotation`（须成对，手电配件本地坐标的位置与欧拉角，写进该配件 `GearPartFlashlight.m_lightAlign`：先挂到配件根下再写本地位姿，光仍是原版的光）和 `HideShell`（只把配件下的 Renderer 关掉，配件物体保持启用，因为光的挂点在它下面）；`m_settingsID` 不再必写。这一步在部件姿态与新加部件之前执行，所以新加的自制外观不会被隐藏。
  - **范围**：与部件姿态一样，每个本机可见的 holder 各自写各自的部件，多人一致靠同一份包；宿主挂起时不读不写。游戏内的灯光实际数值未验证。
- `WeaponFireNoise`：**一把枪自己的枪声噪声半径**，块 `forge.fireNoise`，路径 `radiusMax`（必填）与 `radiusMin`（缺省 0），单位米，下限 0。
  - **原版怎么出声**：四个 Slot 151 `Fire` 本体只把射手的 `Agent.Noise` 设成 `Shoot`，睡眠敌人按自己 `EnemyDetectionDataBlock.weaponDetectionDistanceMin/Max` 衡量，`ShootSilenced` 与 `Shoot` 走同一分支；没有按武器的噪声成员，`MeleeArchetypeDataBlock.NoiseLevel` 只属于近战。证据见 `Weapon/evidence/fire-noise.json`。
  - **怎么只改一把**：只接受以 `offlineId` 为键的条目（开火的枪按 `ItemEquippable.GearIDRange` 的离线记录认块，走同一个 `EquipmentNativeAdapter.GearBlockId`）。四个 `Fire` 本体各一对括号：`Priority.First` 的 prefix 记下拥有者的 `m_noise` 与 `m_noiseTimestamp`，`Priority.Last` 的 postfix 原样写回，本体抬起的 `Shoot` 不留到本体之外（剧情音乐那次通知仍是游戏自己的）。括号在本体运行的每台机器上都跑（主机上客户端那把枪的副本也是）。随后只有射手自己的机器（本机玩家，或主机上的机器人）用 `NoiseManager.MakeNoise` 发一次这把枪的噪声，形状照原版哨戒开枪：无发声者、射手位置与所在路径节点、这把枪的内外半径、竖向比例 1、`InstaDetect`、扩散到相邻区域、不射首节点射线；客户端发出的由游戏自己转交主机。
  - **拒绝与记录**：`itemId`/`archetypeId` 为键、只有 `radiusMin`、`radiusMin` 大于 `radiusMax` 各按文件报一次（`weapon.fire-noise file=…`），不写；射手不在任何路径节点上时每把枪报一次 `weapon.fire-noise-node-missing` 且这一枪不出声。每次替换写 trace 记录 `weapon.fire-noise`（`values`：block、radiusMin、radiusMax），启动时的块数是 `weapon.content-loaded` 的 `fireNoiseBlocks`。没写这个块的枪、以及没有任何这种块的安装，本体原样运行。`crossDoorMode` 没有原生承载，不处理、不报告为定义字段。
  - **验证**：半径外睡眠敌人不醒是 V1 B2 的游戏内检查，未验证。
- `ThermalSightApplier`（`Weapon/Native/Thermal/ThermalSight.cs`）：**作者可选颜色的热成像镜**，块 `forge.thermalSight`，路径 `Enabled`、`HighColor`、`LowColor`（`#RRGGBB`，缺省为原版色带的 `#F5607C`、`#FF0030`），只接受 `offlineId` 为键。
  - **原版怎么做**：只有一个着色器 `Unlit/HolographicSight_Thermal`，镜片网格按屏幕坐标读主相机 G-buffer 算热度再查 `_HeatTex`，没有相机也没有渲染纹理。两个颜色就是一条 256×1 的色带，按颜色对缓存；分界 `BandStart`/`BandEnd` 是从模板参数估算的，要手测确认。
  - **怎么只改一把**：模板是 `Gear_Weapon_Sight` 分片 S1 里 `Sight_10_t` 的材质，复制一份并清空准星。`BulletWeapon.OnWield` 时才判定（`BulletWeaponSynced` 和 `MenuMode` 跳过），因为 `OnAllPartsSpawned` 时 holder 还没有主人。瞄具自带热成像的只换色带；其他瞄具把厚度不超过 2 cm 的玻璃镜片复制一份，用模板材质画在玻璃之前，玻璃留着叠准星，复制件带上原镜片的属性块。
  - **卸载**：分片不释放（游戏没有整体卸载，只有单片），材质副本与色带跨关卡保留。不走网络。
  - **拒绝与记录**：不是 `offlineId` 或颜色写错各按文件报一次（`weapon.thermal-sight file=…`）；找不到模板、瞄具没有镜片各报一次并保持普通镜片；每次生效写 info `weapon.thermal-sight`（block、weapon、sight、lenses、high、low）。
  - **验证**：游戏内未验证。
- `GearLoadoutFilter` / `GearLoadoutSession` / `LoadoutPolicyData` / `LoadoutPolicyWiring`：**装备池策略**（implementation-only），让作者用一份策略文件决定某个 rundown 里可选与可装备的装备。策略在非局内阶段筛选当前可选装备，并在本地装备入口核对选择；不改共享装备池。
  - **发现与激活**：`BepInEx/plugins/<恰好一层目录>/forge/loadout.forge`，与 `forge/plans/`、`forge/weapons/` 同一目录规则（不递归、按相对路径序数排序、单文件 ≤ 1 MiB），启动时**读一次**，改文件需重启。用哪一份由 `Globals.Global.RundownIdToLoad` 决定：一个 rundown 一份策略，同一个 rundown 被两份文件声称时**两份都不生效**（`loadout-policy-conflict`），不按扫描顺序决胜；不匹配当前 rundown 的策略文件不是错误，只是不生效。
  - **内容 pin**：`sources[]` 逐条按 `FrameworkFiles` 同规则解析到 BepInEx 相对路径并比 sha256，`gameAssemblySha256` 比 `Paths.GameRootPath` 下的 `GameAssembly.dll`，`plugins[]` 比 `IL2CPPChainloader.Instance.Plugins[guid].Location` 那个 DLL。任何一条不符，**该文件整份拒收**（`weapon.loadout-policy-<code> file=<相对路径>: <原因>`），其它文件不受影响；pin 只能报告安装不一致，不能拒绝别的客户端。
  - **身份**：一件被提供的装备属于策略里的哪一条，只看两个正向事实——`GearIDRange.PlayfabItemInstanceId` 的 `OfflineGear_ID_<规范十进制>` 文本（与 `gear-block` 挂载同一个拼法，`010001`/`+10001`/`10001.0` 一律不匹配），或 `GearIDRange.GetCompID(eGearComponent.Category)` 等于该 id（网站给自制装备 `setComponent(2, state.blockId)`，分量随副本走）。不解析、不规范化、不猜。
  - **两个入口**：`GearManager.GetAllGearForSlot` 与 `PlayerBackpackManager.EquipLocalGear` 由 ForgeCore 统一打补丁（`Gear.ProjectOffers`，两个补丁都是 `Priority.Last`），Weapon 订阅一次：投影用本次原版返回列表与策略的交集替换返回值，保留原版顺序与重复项；准入核对存档、预设等绕过选择器的请求。请求不在当前允许列表里时，只替换本次调用的装备参数为列表第一件；允许列表为空时拒绝该次装备调用。
  - **缺失与失败**：策略中未出现在当前原版列表里的装备继续不可选，并报告 `weapon.loadout-policy-unmatched slot=<slot> missing=<id,id>`；不会因此重新放入策略排除的装备。身份读取或投影抛异常时保留本次原版返回数组，把该会话的策略锁存失效并报告 `weapon.loadout-policy-failed`，不发布半份投影。
  - **生命周期**：切换 rundown 时重新选择匹配的策略；会话释放时丢弃策略与诊断去重状态。此路径没有共享装备池快照或写回。
  - **不写盘**：策略代码不调用 `SaveFavoritesData` / `RegisterGearInSlotAsEquipped`，不改数据块或 `GTFO_Favorites.txt`；准入后的原版装备流程照常执行。
  - **门**：只处理 `Player.InventorySlot` 1/2/3（主武器、特殊武器、工具），且仅在 `GameStateManager.IsInExpedition` 为假时筛选或准入；近战、固定骇入工具与消耗品不由此策略处理。
  - **诊断**（本 provider 的结构化记录，码是下列第一个词、其余在 `detail`；投影失败 `weapon.loadout-policy-failed` 为 error，其余为 info，每个事实一次）：启动时的策略数在 `weapon.content-loaded` 里、`weapon.loadout-policy-active rundown=<id> policy=<相对路径> slots=<slots>`、`weapon.loadout-policy-inactive reason=rundown-mismatch rundown=<id>`、每槽一次 `weapon.loadout-slot slot=<slot> offered=<n> kept=<n> duplicates=<n> dropped=<n> ids=<…>`、`dropped>0` 且该槽是标准/特种时再写一条 `weapon.loadout-vanilla-dropped count=<n>`。重复项是游戏本来就有的行为，只计数不去重。
  - **多人**：装备池是每客户端本地状态，选择在本地背包做出后随同步副本复制，**没有任何主机校验入口**，所以每个客户端各自过滤，Weapon 不伪造主机校验。
  - 证据链见 `Weapon/evidence/w7-loadout-policy.json`。
- `WeaponCombatObserver` 把开火与命中分成两个 binding：`index` 是该装备生命内的第几发，命中候选的 scope 是 `gtfo.weapon.shot:<equipment id>:<index>`。**同一发只发布一次开火事实**，霰弹枪一个 `Fire` 体打出的多颗弹丸由同一发认领。命中候选的 `equipment` 直接取那条仍开着的窗口，因此与同一发开火事实的 `equipment` 逐字相同；换枪后再开火开的是新生命的窗口，命中随之带上新生命。
- `ClientWeaponReport`（端点 `forge.weapon.client_facts`）：**主客机同一套武器行为**。谁发布只由 `RouteOf` 决定：房主自己和 bot 的物品在钩子发布；客机自己的物品写成记录发给房主，房主校验后在自己的副本上发布；房主钩子对客机物品一律不发布，所以一个动作一条事实。shot_resolved、hit_candidate、aim、charge、burst、dry_fire、push、reload、use、throw、胶枪、手电都走它，只在该行有订阅时才写记录。枪击 100 ms 合批，其余边沿立即发。房主逐条校验并按名拒绝（`origin-drift`、`out-of-range`、`target-drift`、`stale-target`、`no-line-of-sight`、`no-slot-gear`、读不成记录的 `invalid-payload`），不改目标。新增玩家发起的事实必须在钩子处走它。
  - 命中到的东西（`Reached`）一个字节分三种：0 没有、1 敌人或玩家（复制键 2 字节加肢体号 2 字节）、2 地图物体。地图物体用 Map 自己的报文拼法 `MapObjectWire`（`Map/Objects/MapObjectObservation.cs`，与地址语法放在一处）：一个字节的形状，然后是地址本身——`door/…/security` 写三个区坐标，`door/…/weak<序号>`、`terminal/…/<序号>`、`uplink/…/<序号>` 写三个区坐标加一个数，其余地址（摆放模型、房间自带的门这类作者起名的）整条地址写成带长度的 UTF-8 文本——最后是生命号；数都是 7 位变长非负整数。区入口门 6 字节、终端 7 字节。它不另起编号，读回来就是地址拼出的同一个实体 id；报文里不带世界，房主按受理这条请求时的世界盖章，再由内核 `IsEntityCurrent` 判定，这一关没有这个物体或生命号对不上都是 `stale-target`，不会指到别的物体。别的世界发来的请求在 ForgeCore 的请求层就不受理。
- 同一发还写进 SDK 的 `CombatShotAttribution`（`Framework/Entities/CombatAttribution.cs`，按射手引用键、带 world/life epoch，后一发覆盖前一发）：伤害事实的发布方（Enemy 的敌人半、Map 的玩家半）拿不到装备实体，只有看到 `Fire` 的 Weapon 知道是哪条装备生命，所以这条关联放在共享 SDK 里而不是任何一个领域里。伤害事实的 `equipment` 端口就取自它——**只有仍开着的这一发**会被认领，爆炸、近战、部署物自己开火、坠落与持续伤害都读不到装备，因此不写该端口；离开会话（`WeaponNativeSession.Dispose`）时 Weapon 只收回自己记过的射手，不动别的开火方。
- `EquipmentNativeAdapter` 只在 `SNet.IsMaster`、Runtime Ready 且 host 时对账。
  - 背包每个有实例的槽位都读回成 `EquipmentObservation`。
  - 装备生命 ID `gtfo.equipment:<world>.<life>` 由接线按世界递增生成，槽位、指针或资源变化即退役旧生命。
  - 炮台与屏障放置后槽位仍然持有同一件装备，接线用 `PlayerBackpack.IsDeployed(slot)` 读回该槽位的部署标记：标记为真时同一个生命以 `Location=Deployed`（无槽位、未持有）记录，收回后回到 `Inventory`。**部署状态变化不发布 equipped/unequipped 事实**，世界里的部署实例有自己的身份（见下）。
  - 部署物的世界实例用同一个命名空间的第三种形状 `gtfo.equipment:<world>.<life>.<instance>`（接在放置它的那条生命之后，出生序从 1 递增，绝不复用已回收实例的 id）：一个装备生命可以放置并回收多个实例，每个实例一条身份，`SyncedPickup`、`OnDespawn` 与 `OnDestroy` 都走同一条结束路径，第一条到达的路径结束它，其余路径不会重复发布；世界或权威切换时在飞的部署物**不发事实**直接丢弃并写一条 `weapon.lives-cleared`（`values.table=deployments`；把旧世界的结束盖到新世界上会是假事实）。
  - 命中对象 `hit_candidate.target` 由命中物体自己的原生类型决定：从 `rayHit.collider` 起 `GetComponentInParent<Dam_EnemyDamageLimb>()` / `<Dam_PlayerDamageLimb>()` 取 `GetBaseAgent()`，再按 agent 的真实类型选 kind（这一步爬父级全包只有一份：`EquipmentNativeAdapter.Climb`，按命中对象的实例号缓存已定的答案，射线命中、近战、弹道扫掠、射击规则和客户端报告都走它）——敌人交给 `gtfo.enemy`（Enemy 的实例表键就是 agent 本身），玩家经 `IPlayerDirectory.ReferenceOf(agent.Owner)` 取引用（Player 的键是 agent 所属的 `SNet_Player`）。两条腿都不是命中对象的 kind 时，把命中对象本身交给 `gtfo.map_object` 的实例解析器（`ResolveEntityInstance("gtfo.map_object", collider)`，见下）。世界几何、部署物、没有登记实例表的 kind、或没有伤害肢也没有地图对象祖先的碰撞体都解析不出引用，此时 **target 端口整条不出现**，事实本身照常发布（命中候选本来就可能打在墙上）。
  - 地图对象腿只问 kind 的名字，不认门或终端的原生类型：子弹打到的碰撞体属于门的门刀、门框、按钮或终端屏幕，**能从碰撞体爬到自己所属地图对象的只有拥有该域的 Map**，所以 Weapon 把 `rayHit.collider` 原样交出去，由该 kind 的实例解析器自己爬父级、自己判可寻址性。Weapon 因此不调 Map 的类型、不新增任何游戏成员读取（`native.exact-game-reads` 逐字不变），门/终端的知识与地址语法全部留在拥有者一侧。该 kind 没登记（Map 没启动）或爬不到地图对象时同样返回 null，端口不出现。
  - owner 只经 Player 目录（`PlayerHost.Directory`）的 `ReferenceOf(backpack.Owner)` 取得；返回 null 就不记录并警告一次，不从指针、名字或槽位推断玩家。
  - 装备生命还是本 provider 唯一的挂载目标：它注册 `gear-block`（官方装备块 id，该块的全部实例）matcher，判断时按当前背包装备重新读 `BackpackItem.GearIDRange.PlayfabItemInstanceId`——游戏自己的 `GearManager.LoadOfflineGearDatas` 把 `PlayerOfflineGearDataBlock.persistentID` 写成 `OfflineGear_ID_<十进制>` 存在这里，所以这就是网站 `state.blockId`（网站用 `String(blockId)` 写出）的同一个数，不是 checksum。匹配是**序数文本比对**：去掉 `OfflineGear_ID_` 前缀后的文本与 reference 逐字相等才算匹配，脚本不做数值解析、不做规范化，所以 `010001`、`+10001`、` 10001`、`10001.0` 这些第二种拼法都不匹配块 `10001`。reference 不是规范十进制文本，或读不到该记录（非离线块装备、包体重建的 gear、记录后缀不是规范十进制）、生命已退役、跨世界、非 `gtfo.equipment` 生命形状的 subject，一律 false；前一种每份不同文本写一次 `weapon.gear-block-reference-invalid`。挂载按**生命**生效，部署出去的世界实例不匹配。证据链见 `Weapon/evidence/w5-gear-block.json`。

证据文件 `Weapon/evidence/w1-native-hooks.json` 锁定的内容：
- 每个 Hook 的签名、是否 virtual、dump RVA（各自唯一且不共享）。
- 32 条到达或清理路径的直接调用边，其中 15 条属于部署与回收路径（`SentryGunInstance.OnSpawn` 先取 owner 背包再调用部署标记、`SentryGunInstance.SyncedPickup`、`BarrierFirstPerson.PlaceOnGround`、bot 放置、以及矿与投掷物的清槽路径）。
- 3 条离线表现调用边：DoWieldItem 调 `FirstPersonItemHolder.SetWieldedItem` 与 `PlayAnimationsForWieldedItem`，本地 UnWield 调 `FirstPersonItemHolder.UnWield`。**这 3 条不证明模型挂载、rig 或动画的实际结果。**

背包之外的装备路径调研（关卡拾取、世界掉落、部署物、转移）见 `Weapon/evidence/w1-native-world-paths.json`：记录所用 interop 目录与各程序集成员签名（`signatureSha256`）、每个类型的签名与 RVA 唯一性、直接调用方、同步/本地归属，以及为什么只有部署标记被接线。该文件同时记录 `LG_PickupItem`、`ItemInLevel`、`SentryGunInstance`、`MineDeployerInstance` 等真实类型与它们缺少资源定义或 owner 的原因；`PickupItem`、`DeployerInstance` 这类不存在的名字没有被使用。

`gear-block` 挂载键的来源见 `Weapon/evidence/w5-gear-block.json`：记录 `GearManager.LoadOfflineGearDatas`（0x1330250）如何把 `PlayerOfflineGearDataBlock.persistentID` 拼成 `OfflineGear_ID_<十进制>`、`ParseAndStashGear`（0x1330A70）把它写到 `GearIDRange` 的哪个偏移、以及这条记录如何随 `EquipLocalGear`/`EquipSyncGear` 进入 `Player.BackpackItem`；同时记录被拒绝的三个候选（同步包体 `pGearIDRange` 只有 Comps/Mod/MatTrans/publicName、`GearCategory` 组件是另一个块的 id、checksum 不是块 id）。

玩家 owner 的来源：玩家引用 `gtfo.player:<n>` 的编号与 lifeEpoch 由 Player 私有分配（见上文 Player 一节的「玩家实体身份」）；唯一稳定的原生玩家键 `SNet_Player.Lookup` 是 Steam64 账号 ID，不能成为公开键。所以 Weapon 不猜、不自造、不读 `Lookup`，只问 `PlayerHost.Directory`（`IPlayerDirectory`）：`ReferenceOf(player)` 由 Player 按 SNet_Player 指针在已登记表里查找并复核，`PlayerOf(reference)` 是同一张表的反向，持有者会话号是 `SessionOf(player)`、本机判定是 `IsLocal(session)`。Player 没启动成功时目录以 `player-directory-missing` 拒绝，Weapon 只拒绝需要它的那次观察并写一条 error。目录答不出某个玩家（返回 null）时，接线把它当成"这个 owner 答不出来"：不记录、不发布、每个背包写一次 `weapon.owner-unresolved`，会话继续观察。敌人引用仍只问 SDK 的 `ResolveEntityInstance("gtfo.enemy", agent)`，由 Enemy 按 agent 自己的登记表回答；接口规则见 [Framework README](Framework/README.md#从原生实例取得引用)。

Slot 151 的四个 `Fire` 体、它们各自的持有者、`OnSyncFire` 到 `Fire` 的重放链、以及哨戒自己的命中调用方，见 `Weapon/evidence/w4-player-fire.json`：该文件按同一份 dump 与同一个原生镜像（sha256 与其余证据文件逐字相同）记录每个体的签名、Slot、RVA 唯一性、整个体范围内的直接调用（含"没有任何一个体调用另一个"这条否定证据）、il2cpp 虚调用点（`klass + 0x130 + slot*16`，用 Slot 150/151/128 三处已知目标校准），以及仍属运行期未证的五点（重放链是否真的跑起来、`FireCount` 是增量还是累计、bot 开火、本地武器是否可能也是同步类、`GetSync` 由谁调用）。**这仍是静态调用图证据，不是游戏验证。**

有两处行为是设计后果，不是缺陷：
- `InspectEntities` 期间内核禁止嵌套查询，装备的 owner 复核因此失败，装备在该次查询里显示为 `stale-entity`；Weapon 没有注册实体 observer，这不影响记录，也不锁存故障。
- 清表行在世界切换后的下一次 Hook 才写出，不在切换当刻。

结构化记录（Forge Runtime 日志 `BepInEx/forge-logs`，provider `forge.module.gtfo.weapon`，经注册句柄 `RuntimeModuleHandle.Log` 写入，码与级别在码表 `catalog/authoring/diagnostic-codes.json`，`WeaponLogCodes` 的常量在构建前由它生成；默认级别 error，只含 Forge 引用、槽位、资源键与原因码，从不写异常消息或会话号）：
- 事实本身不由 Weapon 写行：内核对每个排队的事件写 `trigger.fired`（info），对每个拒绝写 `event.rejected` / `budget.exceeded`，provider 与 binding 就是 Weapon 的。开火、命中、切枪、部署、换弹、背包等事实没有自己的成功行或被拒行。
- error（Weapon 自己的拒绝与失败）：`weapon.fact-rejected`（Weapon 身份检查在内核之前拒绝，`values.reason` 为检查码）、`weapon.fact-unpublished`（原生事件读不出主体、执行者、目标、种类或数值，`values.reason` 为 `owner-unresolved`、`target-unresolved`、`kind-unknown`、`equipment-unresolved`、`pool-unknown`、`device-not-current`、`velocity-not-finite`、`damage-not-finite`）、`weapon.observation-rejected`、`weapon.placement-ambiguous`、`weapon.mount-reference-invalid`（每个不是规范十进制文本的 gear-block reference 一次，`values.reference` 为截断到 64 字的原文）、`weapon.release-failed`、`weapon.projectile-failed`、`weapon.shot-rule-failed`。
- info（生命与表）：`weapon.owner-unresolved`（每个背包一次）、`weapon.equipment-life`（`values.phase` 为 `started`（带 owner、ownerLife、slot、resource、location）、`location`、`ended`（带 reason））、`weapon.deployable-life`、`weapon.placement-unmatched`、`weapon.object-scope-released`、`weapon.lives-cleared`（`values.table` 为 `equipment`、`deployments`、`reload`、`inventory`）、`weapon.shot-rule`。
- trace（只在 Authoring 提级后写）：`weapon.fact-skipped`（`values.reason` 为 `no-open-shot`、`owner-unresolved`、`equipment-untracked`、`deduplicated`）、`weapon.projectile-flight`、`weapon.shot-rule-pierced`。
- 子弹弹丸（`forge.action.combat.bullet_projectile`）在其他玩家机器上的副本只走一条路：引擎通道 `NAinfini.Forge`（`NetworkBinding.Channel`）上由 `WeaponBulletProjectiles.Bind` 建的不可靠有序消息 `weapon/projectiles`（`NetDelivery.Lossy`，格式版本 1）。世界与检查点代的标记、乱序包的丢弃都由 ForgeCore 做。每一包都是整张表：8 字节位图（哪些槽位有弹丸），随后每颗弹丸 30 字节（槽位 1、序号 4、外观 1、位置 12、朝向 12）；超过 33 颗时分成几包，每包都带完整位图和自己那一部分弹丸。房主在有弹丸飞行时每 0.05 秒发一次，最后一颗结束后再发 1 秒空表。客机只认房主发来的包：位图里没有的槽位销毁副本，没有副本或序号变了的槽位新建副本，其余的移动；解析不出的条目跳过，副本建不出来时写一条 `weapon.projectile-failed`（`phase=replica-spawn`），同一颗弹丸不再重试。不做补发：丢一包只少一步轨迹，迟加入的玩家在下一包拿到当前弹丸，世界切换与检查点读档时各机自己清掉副本。

部件、字段与装备表的记录（同一 provider，info）：
- `weapon.content-loaded`：启动快照接受的 `fieldObjects`、`gearPartBlocks`、`flashlightBlocks`、`loadoutPolicies`，任一非零时写一次。
- `weapon.gear-part-applied`：每个 holder 出生后写一次，同一个 holder 再出生就再写一条；`extras` 的「已接手」含已挂上与等待资源分片的件数。
- `weapon.field-applied`：写入一组块字段后写一次，`values.target` 为 `item`、`scanner`、`mine-interaction`、`mine-explosive`、`mine-foam`、`mine-beam`、`mine-light`、`sentry-interaction`、`sentry`、`fog-repeller`、`hack-duration`，`item` 为物品块，`source` 为那份文档（截断到 64 字）。
- `weapon.override-applied` / `weapon.override-restored`、`weapon.profile-applied` / `weapon.profile-restored`：命名覆盖行与整档写入落到或退出一个武器实例，`entity` 为装备。
- error `weapon.field-rejected`：块字段没写成，`values.reason` 为 `unavailable`、`type`（颜色不是 `#RRGGBB`/`#RRGGBBAA`）、`range`（整数成员放不下），`values.field` 为字段路径，每个事实一次。

警告：`weapon.field-<code> file=<相对路径>: <原因>`（每份被拒绝的文档一次；`<code>` 见上文）、`weapon.gear-part-pose file=… block=…: <原因>`（每个取值或词表不合规的对象一次）、`weapon.gear-part-child-missing: …`、`weapon.gear-part-missing: …`、`weapon.gear-part-extra-parent-missing: …` / `weapon.gear-part-extra-refused: …` / `weapon.gear-part-extra-materials: …`（每件新加部件一次）、`Weapon equipment observation disabled for this level: …`（只停本关，下一关自动恢复）。
已知未核验的点：
- `Slots` 下标是否等于 `InventorySlot` 值。
- `IsLoaded` 的实际语义。
- UnWield 后 `WieldedItem` 的状态。
- 部署后槽位是否仍持有原来的 `BackpackItem` 实例；若实例被销毁，现有 `Matches` 逻辑退役该生命，而不是报出错误的部署位置。
- `forge.action.inventory.recall_sentry`（`Weapon/Framework/Actions/SentryRecallContract.cs`、`Weapon/Native/Profiles/SentryRecallActions.cs`）：对每个接入的玩家，经 `m_sync.WantItemAction(放置者, PickUp)` 请求原版拾取，弹药和工具槽由 `SyncedPickup` 结算。只认部署记录里该玩家放下的炮：关卡自带的炮不收，工具槽标着已部署却没有记录时按名字拒绝。
- `SentryGunInstance.OnDespawn` 与 `SyncedPickup` 不都回写部署标记，原生标记可能在部署物消失后仍为真；读回只镜像原生标记，不替游戏修正。
- `itemID_gearCRC` 在游戏里既可能是 itemID 也可能是 gearCRC；背包路径靠槽位的 `BackpackItem` 解析，世界路径没有对应解析，因此没有接线。
- Bot 等其他 inventory 子类。
- 两次 Hook 之间指针被复用且没有任何清理 Hook。
- 转交时新背包先于旧背包被对账。
- 事件时间取 Hook 时刻。
- 出生时背包存入 Hook 是否早于 Player 玩家目录登记该玩家（若早于，会先出现一条 `weapon.owner-unresolved` 记录，下一次 Hook 才记录）。
- 实际启动顺序，以及宿主挂起、Player 或 Map 没启动时的日志。
- 客户端开火在 host 上的重放：静态链已完整（客户端自己的武器把这一发登记进 `PlayerSync`，复制过去的射击数由 `PlayerInventorySynced.GetSync` 交给该玩家武器在本机的同步副本 `OnSyncFire`，副本的每帧 `Update`/`UpdateRegular` 再逐发调用 `Fire`，见 `Weapon/evidence/w4-player-fire.json`），但**没有任何运行期证据**：这条链是否真的每发跑一次、`FireCount` 是增量还是累计、`GetSync` 由谁按什么频率调用，都只有游戏内一枪一计数才能确认，没有做。接线按 host 权威实现（`Authoritative()` 要求 `SNet.IsMaster`），同一发仍只发布一次。
- `WeaponHitData.owner` 是否由 `Fire` 体填写：命中数据没有射手时写 trace 记录 `weapon.fact-skipped`（`owner-unresolved`）而不发事实。
- `SentryGunInstance.Owner` / `MineDeployerInstance.Owner` 在 `OnSpawn` 时刻是否已可用：不可用时该放置仍被跟踪、仍可结束，但不发 `deploy_completed`，并写 error 记录 `weapon.fact-unpublished`（`owner-unresolved`）。
- 关卡切换走 `OnDespawn` 还是 `OnDestroy`：两条都挂了 Hook，且结束路径只会成功一次；无论哪条先到，结果都是恰好一条结束事实。
- 部署物的 `transform.position` 是否是玩家关心的世界坐标；`GetGroundOffset` 与 `GroundOffset` 在部署后是否参与修正。静态侧已确证**没有任何部署物代码在 spawn 之后移动自己**，位置只在复制器实例化那一次写入（见 `Weapon/evidence/w4-deployable-hooks.json` 的 `positionAtSpawn`），但复制器工厂与同一个复制操作的 spawn 回调之间的先后仍然只能靠游戏内一次部署确认：这就是 `deploy_completed.position` 按 `optional` 注册、读不到就不出这个端口的原因。
- 命中候选的 `limb`：需要把命中碰撞体解析成伤害肢的序号，而候选在任何伤害结算之前产生，本 provider 不做。`target` 已有三条腿：敌人、玩家与地图对象（门、终端），见上文与 [Map 一节](#map)。
- 部件姿态在游戏内是否被原动画覆盖：写发生在 `OnAllPartsSpawned` 之后，但同一类型还有 `Update`（dump.cs 第 614843 行），它的方法体尚未解析。检查点重载、图标渲染频率、与同类部件模组同装时的先后顺序同样未核验。

### 原生 API 证据

只读的元数据核验工具与精确的输入锁：Steam app `493520` / build `20403457` / revision `34873`，GameAssembly SHA-256 `C6A5C3CD8CA5FE2A8C1A71A3D107663E8CBF01404820DBBDA2EA10C4BFD7BF55`，global-metadata SHA-256 `F57AE2790F7AE0A7ACCAD42BD5F5A541EE74C85E96B4D4B64A346B465C23C882`，锁定 8 个文件、34 个类型、164 个方法、47 个属性、22 个字段。

初次名称检索得到 345 个候选类型，之后补读 8 个身份与库存与网络类型；**这不是可运行的能力计数**，最终只按人工选择的精确全名、程序集、基类和签名核验。

这批元数据锁的证据等级是 **metadata-only**，原生 Hook 的静态证据见上一节：读取 BepInEx interop 的元数据并锁定同机游戏与生成的元数据文件，**没有执行原生方法、没有核验完整的原生调用链、没有安装 Hook、没有启动 GTFO**。同机文件存在，不单独证明 interop 的生成来源与原生地址映射正确。工具本身不进入 `ForgeEngine.dll`。

### 边界

网站可以挂载多个部件或预览模型，不证明 GTFO 支持相同数量的活动组件。Enemy 与 Map 分别拥有敌人和玩家与世界的 receiver；Weapon 不能为了省接口直接写它们的私有字段。一个原生攻击或附加效果必须指定唯一的执行 binding——**观察到原生承伤不代表还要再施加一次同额伤害**。

Weapon 的游戏内行为没有实机验收，验证等级只到编译与离线检查。

### 复跑

从仓库根经 `Mods/verify.py` 运行，`--bepinex` 默认是只读的 `Forge-Multiplayer` profile 的 BepInEx，与证据文件冻结的 interop 是同一份副本；输出在 `artifacts/mods/ForgeEngine/<运行名>/`。Weapon 的两项单独重跑：

- `ForgeEngine:verify-w1.py --architecture`：`Weapon/tools/verify-w1.py`，附带跨模块架构断言，不含原生接线的套件。
- `ForgeEngine:WeaponNativeLayout`：`Weapon/tests/NativeEvidence/WeaponNativeEvidence.csproj` 里 `ForgeWeapon.Tests.NativeLayout` 的测试（`Category=Native`），读构建出的 `ForgeEngine.dll`。单独用 `dotnet test` 时要自己设 `FORGE_ARTIFACTS`、`FORGE_WEAPON_HOOK_SPEC`（= `Weapon/evidence/w1-native-hooks.json`）与 `FORGE_WEAPON_LOADOUT_SPEC`（= `Weapon/evidence/w7-loadout-policy.json`），都用绝对路径，测试宿主的当前目录在构建输出下。

## 边界

已提供的是编译后宿主与 SDK 的元数据检查、本地游戏程序集的 Hook 签名检查，以及使用替身的启动与绑定测试。它们不代替实际注入、开销测量或主客机测试。

禁止无限重试、吞掉异常或调低游戏日志级别来伪装成功。

## 概述、设置与日志（来自包页说明）

Forge 的游戏端。它加载 Rundown 和模组附带的内容包（计划），与游戏同步运行，并让多人游戏里的所有人保持同一版本。覆盖：

- **玩家与机器人**：生命、状态、体力、移动、输入、指令和属性变化。
- **地图**：门、终端、目标、警报、遭遇、撤离，以及 Forge Rundown 的房间与关卡。
- **敌人**：生命、受击方式、生成所需空间和行为。
- **武器与装备**：玩家持有、使用和射击的东西。

在 Rundown 或模组为原版关卡附带内容之前，它不改变任何原版关卡。

### 设置

给制作和测试地图的人：`BepInEx/config/NAinfini.ForgeEngine.cfg` 里有 **Rundowns / UnlockAllLevels**（默认关）：打开后每个 Rundown 的每一关都能直接开始，不管这台电脑通关了什么。你的通关记录仍照常计数；关掉后关卡又按你自己的通关记录上锁。改动在 Rundown 页面下次刷新时生效。

### 出问题了？

错误写进 `BepInEx/LogOutput.log`，执行日志写进 `BepInEx/forge-logs/`。`BepInEx/config/NAinfini.ForgeEngine.cfg` 里的 `Logging.Level` 管上面全部日志（默认 `error`；设为 `info` 看更多，改完要重启游戏）。反馈问题时请附上这两份日志。