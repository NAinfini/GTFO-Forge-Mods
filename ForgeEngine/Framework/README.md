# ForgeEngine 公共 SDK 1.0.0

本目录的源码编进 `ForgeEngine.dll`；`ForgeEngine.Framework.csproj` 另把它和四个领域不在 `Native/` 下的源码编成不碰游戏的 `ForgeEngine.Framework.dll`，只给服务器准入程序与离线测试用，不进玩家安装包。它提供与 GTFO/Unity 类型无关的注册、计划验证和唯一模拟队列；实际游戏绑定在相邻的 `GameBindings/` 与四个领域的 `Native/` 里。网站侧的 TS 合同与共同正反例在本仓库的 `Web/src/forge/runtime-contracts.ts` 与 `Tests/Forge/fixtures/runtime/`。

当前开发 Runtime 身份是 `forge.runtime / 1.0.0`，SDK API 1.0.0（Forge Standard，与网站同批升级），游戏 build `20403457`。未经游戏验收的绑定一律保持 `implementation-only`。交付要求见唯一开发计划 §6 U-RUNTIME（链接见[仓库 README](../../README.md)），跨包依赖与所有权见 [ForgeEngine 开发者说明](../DEVELOPERS.md)，公开生命周期的完整时序与限制见 [HOST-LIFECYCLE.md](HOST-LIFECYCLE.md)。

共享 `CombatContracts.cs` 当前注册 **5 个** canonical 定义（承伤事实、治疗动作、生命变化事实、死亡流程、肢体破坏），**0 个** binding。Enemy 因此持有 5 个 binding 与 5 个原生 Hook，Runtime 保留 4 个世界与会话 Hook。

## 宿主生命周期

模块取得的 `Plugin.Runtime` 类型是 `IForgeEngine?`。`RuntimeKernel` 的启动、世界与计划控制、推进、迁移、检查点、状态同步及远端命令入口均为 internal；测试通过单独的 `ForgeEngine.Testing` 程序集驱动真实入口。注册、服务查询、实体与资源读取以及生命周期快照保留在公开接口上。

内核在 `Framework/Execution`：`RuntimeKernel` 是门面，持有全部公开成员、线程检查、世界与计划的编排，以及要把内核交给处理器的分发与走步；状态按职责放在它持有的内部类里，只许上层调下层：`KernelScheduler`（逐 tick 记账、延迟结果、待发输出）→ `KernelFlow`（控制帧、流程组、定时表）→ `KernelOwner`（物主指令账）→ `KernelEffects`（效果、句柄、声音）与 `KernelVariables`（变量、触发门、等待、数值租约）→ `KernelExecutor`（结果行、事件单元格、花费、资源）→ `EntityLives`（实体存活）→ `KernelPlans` → `KernelRegistry` → `KernelLog` → `KernelWorld` 与 `EventQueue`（纯数据）。换房主时每个类用自己的 `SwapWith` 与暂存镜像互换状态。

换房主的迁移镜像（`migration` 路由的报文体，版本 `1.0.0`）由各状态类自己给出、自己读回，门面 `RuntimeKernel.Migration.cs` 只按固定顺序汇总：

| 类 | `CaptureMigration` 写进镜像的段 | `RestoreMigration` 读回到暂存镜像的内容 |
|---|---|---|
| `KernelVariables` | `variables`（变量、`g-once`、触发门行；原生领域状态由门面并入这一段） | `PrepareMigration` 在预检时解出变量表和触发门表，交给暂存镜像 |
| `EventQueue` | `sequence`、`history`、`historyFloor` | 去重账本和它的下限；事件序号只增不减 |
| `KernelWorld` | `worldEpoch`、`capturedTick`、`cancelled` | 已取消的范围（世界号和 tick 在建暂存镜像时给定） |
| `KernelEffects` | `providers`、`handles`、`effects`、`effectSequence`、`handleGeneration` | 提供方序号、句柄槽和两个计数；效果行由门面解码后经 `AdoptMigrationEffect` 放入 |
| `KernelFlow` | `scheduleSequence`、`schedules`、`waits`、`pending` | 定时表、等待、流程组成员关系和排队事件（只在接任房主的机器上）；定时序号只增不减 |
| `KernelOwner` | `owner` | 收到的指令账、清理登记、待报告的结束；接任房主的机器另取已发指令账、代理租约、提前结束和回执 |
| `KernelScheduler` | `deferredResults`、`tickUsage` | 本 tick 已用预算；接任房主的机器另取延迟结果账 |

成像前 `KernelFlow.SurvivingMigration` 只判一次哪些定时任务和等待还作数，效果和流程两段都按这一个答案写。门面自己留三件事：整份镜像的预检（物主指令账、提供方交接、效果与已加载计划对不对得上），`providerHandover` 一段（要回调提供方），以及要绑到将来分发它的内核上的解码（效果的输入帧和存活单元、物主在途指令恢复后的那次分发）。线上的值怎么写、怎么读是 `RuntimeKernel` 上的一组无状态静态方法（`SerializeMigration*`、`RestoreMigration*`、`ReadMigration*`），各类直接调用。

`Plugin.Runtime` 是唯一内核入口；`Plugin.CanExecuteGameplay` 提供模拟线程上的当前阶段与主机权威门槛，**不代替目标、权限和成本校验**。宿主首个 FixedUpdate 调用 `StartRuntime`，在导出实际 manifest 与加载离线计划之前冻结注册；失败锁存，不逐帧重读。`StopRuntime` 清除工作并使保留的内核引用不能继续执行。

第三方模块在自己插件的 Load 中登记（四个领域在各自的 `Start` 里登记），用返回句柄的 `ObserveLifecycle` 观察 Snapshot / StartupChanged / WorldChanged / TickAdvanced。通知有界、只读、按模块清理；回调错误被隔离而不是关闭正常玩法。

## 模块接入

在首个 Runtime FixedUpdate 之前，从公开的 `Plugin.Runtime` 取得宿主，调用 `RegisterModule(RuntimeModule, RuntimeLogLevel)`；第一方模块也走同一个接口。级别是必填参数：四个领域用引擎 cfg 的 `Logging.Level`，第三方插件给自己的级别（见下文），**不放进 `RuntimeModule`**；一个包注册多个 provider 时逐个传同一个级别。Runtime 宿主注册的六个内置 provider（CombatContracts、ControlContracts、VariableContracts、TriggerContracts、StatusContracts、ObservationContracts）没有包 cfg，走只对宿主程序集可见的内核内部路径取 Runtime 自己的级别，不经公开参数伪造；SessionContract 另在宿主自己的 provider ID 下注册会话 binding。模块声明一个 provider、自己拥有的 capability，以及自己提供的 bindings，结构与网站 ForgeRegistry 完全一致。扩展 bindings 可以引用已注册的共同 canonical capability，不重新声明也不覆盖它。**计划词汇中的 planned 不能注册成可执行能力。**

`RuntimeModule` 的 `Handlers` 以 `binding.handler` 字符串映射同步 `CommandHandler`。`EntityResolvers` 按精确对象 ID 前缀注册，例如 `gtfo.enemy` 对应 `gtfo.enemy:7`；ID 始终是字符串，完整引用还包含 worldEpoch 与 lifeEpoch。接收者有效性和具体游戏接收能力由所属模块再次检查，核心不按阵营或实体种类推断治疗与伤害目标。`EntityObservers`（读一个已知引用的当前状态）、`EntityInstanceResolvers`（把进程内原生对象映射成当前引用）与 `EntityCandidates`（说出自己的种类现在有哪些实例）是同一份所有权下的另三个可选表，各自要求该模块已经拥有这个种类的 `EntityResolvers`。

实体种类用 `EntityKinds` 的常量，动作结果行用 `ResultRows.Of` 写在 `result` 端口下；不要在包里再写一份字符串。

注册返回 `RuntimeModuleHandle`：

- `BeginEvent(gate)` 开一个自己绑定的事件，按触发器输出位置写值后 `Raise(...)` 发布（见“动作输出接口”）；只能发布自己的绑定，重复身份不会再次执行。返回 queued 只表示已入队，不表示游戏动作已提交。
- `CancelScope(scopeId)` 只取消该模块拥有的来源作用域，同名的其他模块作用域不受影响。
- `Schedule(...)` 与 `AcquireNumericLease(...)` 是同一句柄上的定时与状态入口，见下文。
- `PublishService<T>(service)` 在注册窗口内发布一个类型化服务，每种类型只允许一个发布者（重复以 `service-conflict` 拒绝，所以函数服务要声明具名委托，不用裸 `Func`），注销时撤回；别的插件用 `Plugin.Runtime.TryService<T>(out var service)` 取得。四个领域之间直接调用对方的会话，不经服务；引擎发布给别的插件的服务见 [ForgeEngine 开发者说明](../DEVELOPERS.md)。
- `Dispose()` 只注销自身。如果其他模块仍引用它的公共定义或绑定，整个注销被拒绝。只在启动前尚未冻结注册时允许注销后再登记；同 ID 新登记不会复活旧计划或旧句柄，Ready 之后不开放模块热加载。

`RuntimeModule.Package` 写本 provider 所在的发行包（`Owner-Name`，不带版本）。provider 行 `dependencies` 里的每个 `Owner-Name-x.y.z` 在注册时核对：没有已注册 provider 属于该包以 `package-missing` 拒绝，版本不等以 `package-version-mismatch` 拒绝，包名格式不对以 `package-name` 拒绝；拒绝是原子的。provider 依赖既约束注册顺序，也随 provider 行进入计划编译时的 `dependency-lock`。

这是受信模块之间的所有权约束，**不是任意第三方 DLL 的安全沙箱**。所有修改与分发都在宿主模拟线程调用；处理器执行过程中不能注册、注销或替换世界与计划，但可以取消或释放自己拥有的句柄以及申请新的 schedule 与 lease（仍走相同的因果深度和共享配额）。

### 积木表

`Blocks` 是全部积木的一张表，构建前由 `Tools/Forge/build-primitive-runtime.ts` 从 `primitives.json` 和 `operators.json` 生成（`artifacts/generated/blocks/Blocks.g.cs`，不入库）。`Blocks.Rows` 每个积木一行，按 id 排序：id、归属的 provider、种类、控制操作码、执行层、接收者权限、允许返回的结果码。每个积木另有一个只含常量的小类，名字是 id 去掉 `forge.` 后的帕斯卡写法：`Blocks.ActionCombatDamage.Id`、`Index`（表里的下标）和端口下标（`InTargets`、`OutResult`、`ParamMitigationPolicy`）。`ResultCode` 是全部结果码的一张词表（`ResultCode.TooManyTargets`）。

provider 用 `new ProviderDeclaration(id, version, package).DeclareBlocks()` 声明表里归自己的全部行；这次注册没有实现的行，把它们的 id 传给 `DeclareBlocks(...)`，这些行不声明。绑定（`ProviderBinding`）仍由 provider 自己写。表里没有的行（第三方 provider 的扩展行）照旧用 `Declare`。

### 第三方最短示例

下面是一个 provider（一个触发器、一个动作）、一份手写的 `.plan.forge`、一个只读观察者。计划是扁平的预解析文件，不是图；`bindings` 按 `bindingId` 排序，`entrypoints[].binding` 与步骤的 `binding` 是该数组的下标。用 `RuntimeKernel` 直接构造只为离线跑通；游戏里用 `Plugin.Runtime.RegisterModule` 取得同一种句柄。

```csharp
const string Id = "example.hello";
var said = 0;
object Graph(string kind) => new
{
    domains = new[] { "logic" }, execution = "host",
    inputs = kind == "action" ? new object[] { new { id = "in", type = "execution" } } : Array.Empty<object>(),
    outputs = new object[] { new { id = "next", type = "execution" } }, parameters = Array.Empty<object>()
};
var declaration = new ProviderDeclaration(Id, "1.0.0") { Kind = "extension" }
    .Declare(
        new { id = Id + ".trigger.ping", owner = Id, kind = "trigger", label = "Ping", version = "1.0.0", parameters = new { }, graph = Graph("trigger") },
        new { id = Id + ".action.say", owner = Id, kind = "action", label = "Say", version = "1.0.0", parameters = new { }, graph = Graph("action") })
    .Bind(
        new ProviderBinding(Id + ".binding.ping", Id + ".trigger.ping", Id + ".handler.ping", "observe", Array.Empty<string>()),
        new ProviderBinding(Id + ".binding.say", Id + ".action.say", Id + ".handler.say", "execute", Array.Empty<string>(), new HandlerShape()));
// A plan names the mount it hangs on; the provider that owns the kind answers whether it is the current one.
var module = new RuntimeModule(RuntimeKernel.ApiVersion, RuntimeApi.Fingerprint, declaration, new Dictionary<string, CommandHandler>
{
    [Id + ".handler.say"] = _ => { said++; return CommandResult.Succeeded(RuntimeJson.EmptyObject); }
})
{
    AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
    {
        ["level"] = AttachmentMatcherRegistration.ByScope((_, reference) => reference == "example.level")
    }
};
var handle = kernel.RegisterModule(module, RuntimeLogLevel.Error);
```

A plan's "wait for event" step may name any `observe` binding of a trigger row that its provider declared with `{ Waitable = true }` (here `ping` would add it to the initializer); the registry, the plan compiler and the website read the same flag, and any other binding is refused with `wait-event-unsupported`. The wait resumes on any event of that binding: it does not filter by subject.

An action whose resolved recipient set is empty returns `CommandResult.NoTargets()`: succeeded and confirmed with no rows, which the kernel treats as a no-op (the recipient resolver, not the action, rejects a recipient that cannot be resolved).

```json
{"schemaVersion":"1.0.0","kind":"forge-engine-plan","planId":"example.hello.plan","domain":"logic",
 "runtime":{"id":"forge.runtime","major":1,"minMinor":0},"resource":{"id":"example.hello","revision":"1"},
 "attachments":[{"kind":"level","reference":"example.level"}],"dependencies":[],"permissions":[],
 "layoutHash":"c71ca8b6fbf820a87ef7a7147ec6628bde145477463bd817e4f0195e77ef76a0",
 "limits":{"maxCausalDepth":16,"maxCommandsPerTick":512,"maxEventsPerTick":128,"maxQueuedEvents":1024},
 "bindings":["example.hello.binding.ping","example.hello.binding.say"],
 "entrypoints":[{"nodeId":"Ping","binding":0,"constants":[],"promoted":[],"start":0,
   "steps":[{"nodeId":"Say","nodeKind":"action","binding":1,"constants":[],"promoted":[],"inputs":[],"successors":[null]}]}]}
```

```csharp
var seen = new List<string>();
using var subscription = handle.ObserveTrigger(Id + ".binding.ping", view => seen.Add(view.EventId));
var result = handle.BeginEvent(handle.SubscriptionGate(Id + ".binding.ping")).Raise("ping-1", 1, 1, "example");
```

`ObserveTrigger` 只读：观察者算作订阅者（`SubscriptionGate` 为真，发布方照常构造事件），看见该绑定上每个被接纳的事件，不论有没有计划消费它；不改派发、不参与调度，回调里不能再发布或改注册，回调抛异常即被移除并写一条日志，派发不受影响。定时脉冲产生的事件不在观察范围内。


## 编译时接口指纹

调用方使用 `declaration.Module(RuntimeApi.Fingerprint, handlers, evaluators, entityResolvers)`，或在 `new RuntimeModule(RuntimeKernel.ApiVersion, RuntimeApi.Fingerprint, declaration, ...)` 的第二个参数传入该常量。常量在调用方编译时捕获，不能由正在运行的 SDK 替调用方填入。版本仍为 `1.0.0`；注册时指纹不一致返回 `api-fingerprint`，状态不部分写入。旧 ABI 在 CLR 加载阶段失败也应重新编译，不提供旧重载。它是接口兼容检查，不是 DLL 安全认证。

构建工具要求 .NET SDK 8 或更新版本；产出的 SDK 仍是 net6.0。完整指纹规范在总计划 §3.2。正常构建总会核对公开元数据；公开 API 有意改变时依次执行：

```sh
dotnet build Mods/ForgeEngine/Framework/ForgeEngine.Framework.csproj -c Release --artifacts-path artifacts/dotnet -t:GenerateRuntimeApiFingerprint
dotnet build Mods/ForgeEngine/Framework/ForgeEngine.Framework.csproj -c Release --artifacts-path artifacts/dotnet
```

## 求值器接口

`EvaluatorHandler` 是 `void (EvaluationContext context, EvaluationOutputs outputs)`：求值器把每个输出端口写进 `outputs`，不返回 JSON。输出按处理器形状 `HandlerShape.Outputs(...)` 里声明的位置寻址（形状没声明输出时按能力合同的输出顺序）；类型化写入有 `Boolean`、`Integer`、`Number`、`Text`（字符串或枚举成员名）、`Enum`（成员下标）、`Vector`、`Entity`、`Entities`、`Resource`、`Null`，没有类型化写法的种类（句柄、标量集合）用 `Json(output, value)` 交出端口的 JSON 形式。求值器返回后内核按端口逐个核对（种类、范围、枚举成员、实体存活与去重、可空），拒绝码与原 JSON 边界相同；合同要的输出没写以 `missing-field` 拒绝。内核为每个 `pure` / `query` 步骤持有一份 `outputs` 和一份上下文并在每次求值时复用，所以两者都不能留到调用之后；`Entities` 会复制传入的列表，调用后可立即重用。值只有在跟踪、表现包或需要 JSON 的下游读取时才写成文档。

第三方迁移：把 `JsonElement F(EvaluationContext c) => RuntimeJson.From(new { a, b })` 改成 `void F(EvaluationContext c, EvaluationOutputs o) { o.Number(0, a); o.Boolean(1, b); }`，下标就是形状里该输出的位置；只返回现成 JSON 文档的求值器可逐个端口用 `o.Json(i, frame.GetProperty(id))`。输入优先用形状的 `HandlerSlot` 读（`context.Integer/Number/Text/Vector/TryEntity/TryResource`），不渲染整份输入文档。直接调用求值器的测试或工具用 `new EvaluationOutputs("a", "b")` 作答题纸，再用 `ToJson()` 读回。旧的单参数委托不保留；旧二进制以 `api-fingerprint` 拒绝，重新编译即可。

## 动作输出接口

`CommandHandler` 的签名不变（`CommandResult (CommandContext context)`），动作的输出有类型化写法：

- 结果行用 `context.Rows`（`RowWriter`）写：每个显式接收者一行、按接收者顺序。`Begin(index)` 开一行，目标列由内核按该位置的接收者写入；`Begin(index, target)` 额外核对处理方法实际作用的对象就是该位置的接收者。自有列按积木的生成常量 `Blocks.<积木>.Row<列名>` 写（`Boolean`、`Integer`、`Number`、`Text`、`Enum`、`Vector`、`Entity`、`Null`），`End(Status, Commit, code)` 结束这一行；只有四个固定列的行用 `Row(index, status, commit, code)`。行直接写进激活的结果区，不建行对象、不序列化、不解析。
- 结论用 `Outcome`：`rows.Conclude()` 按统一聚合规则从已写的行得出（表现类动作用 `ConcludePresented()`），或用 `Outcome.Rejected(code)` 等工厂；`Outcome` 隐式转成 `CommandResult`，不分配。要带诊断说明用 `outcome.WithDetail(detail)`：同一个字符串（常量，或每种情况只拼一次的）转换时不分配；每次调用都新拼的说明每次建一个结果对象，超过 512 字符的不缓存。
- 多目标动作用 `context.EachTarget(ref state, step, kind, maximum, exceptionCode, ..., countColumn)`：权限、数量上限、逐个目标的种类和世界期检查、异常转成该行 `failed + unknown`、聚合都在骨架里，`step` 只做提交、回读、写自有列和 `End`。`step` 存成静态字段，状态经 `ref` 传入，不产生闭包。`countColumn` 传积木的接收者总数列（`Row…TargetCount`）时，骨架给每一行写这一列，它自己结束的行也带，`step` 不再写它。骨架自己写的码（`unsupported-recipient`、`stale-entity`、`exceptionCode`）和处理方法写的码一样要通过码校验：端口声明的、共用的或内核登记的。
- 表现类多目标动作用 `context.EachTargetPresented(ref state, step, kind, maximum, exceptionCode, report, countColumn)`：同一个骨架，行不提交任何东西，异常行是 `failed + none`，结论走 `ConcludePresented()`。
- 行以外的输出（数量、求得的点等）用 `context.Outputs` 按合同输出位置 `Blocks.<积木>.Out<端口>` 写，它就是求值器用的那张答题纸 `EvaluationOutputs`；枚举输出按成员名用 `Text` 写。
- 命令确认的事实用 `context.BeginFact(gate)` 开（`gate` 是本模块该触发器绑定的 `SubscriptionGate`），在 `Values` 上按触发器输出位置写值，再 `Add()` 交给命令；值的写法和事件是同一套。内核在处理方法返回后按添加顺序发布：结果不允许带事实、行被拒或超过 256 条（`CommandResult.MaximumFacts`，与集合宽度 `Wire.MaxSetWidth` 相同，超出即 `fact-budget`，不截断）时都不发布。事实在内核里就是写下的那组值，发布时不经文档；只有把结果发回房主或写进迁移镜像时才写成线上形式，而且只写一次。同一绑定的事实写完并 `Add()` 后再开下一个。
- 延迟结果用 `handle.CompleteDeferredResult(token, ref state, row)` 完成：`row` 是一个 `TargetStep`，拿到已为该目标开好的终态行，按 `Row…` 常量写自有列并 `End`；这一行的状态、提交态和码就是命令的结论，必须是终态（`succeeded`、`failed`、`cancelled`、`expired`）。没有人读回执时（房主逐帧推进不取回执、也没有行为跟踪的观察者）这一行不渲染成 JSON，回执只带状态、提交态和码。只有四个固定列的行用 `CompleteDeferredResult(token, status, commit, code)`。行被拒时向调用方抛出行 ABI 的码，目标仍然待完成；返回 `false` 表示该目标已有终态结果。可以在另一个命令的处理方法里调用。
- 事件发布方用 `handle.BeginEvent(gate)` 开一个事件（`gate` 是发布方已持有的 `SubscriptionGate`），在 `Values` 上按触发器输出位置写值，再 `Raise(eventId, worldEpoch, tick, scopeId, ...)` 发布；同一绑定的事件写完并发布后再开下一个。绑定和挂在它上面的入口在注册表或订阅表变化时就解析到 `gate` 上，开事件和发布都不按名字查表；按位置写的值已逐个校验，发布时不再对整份载荷校验一遍。事件载荷在内核里是一组格子：触发器的每个值输出一格，按注册顺序排，在事件被接纳时定下来；挂载过滤、枚举过滤、等待、角色和步骤输入都按装载时算好的下标读格子，步骤拿到的就是发布方写的那个值。模块没有交文档的发布入口；从线上解出来的事实和迁移镜像里的事件是文档，在接纳时读进同一组格子，之后走同一条路。载荷只在要文档的地方才写成 JSON，而且只写一次：重放账本和大小预算只要它的文本（写进内核自己的缓冲，不建文档），迁移镜像、trace 和读 `RuntimeEvent.Outputs` 的调用方要文档。
- 接收者恢复读取（`DomainStateChannel.WithRecipientContext` / `RebuildRecipientContext`）交给读取方的上下文带结果区：`context.Rows.Count` 是接收者数，把上下文交给该步骤自己的处理方法时，它照常逐行写、照常得出结论；这些行随读取结束，不进回执。
- 拥有者动作晚些回答用 `context.DeferOwnerResult(timeoutTicks, state, settle)`：`settle` 是一个 `OwnerSettlement<TState>`（`Outcome? (ref TState state, bool expired, RowWriter rows)`），内核每次推进问一次，拿到的 `rows` 已按该步骤当时的接收者备好；它逐行写、返回结论（通常是 `rows.Conclude()`），返回 `null` 表示继续等，等待期间写下的行作废。行数必须等于接收者数，否则以 `result-row-incomplete` 结束。接收者必须是实体。内核只在把结果发回房主时渲染一次。
- 句柄的身份是值类型 `RuntimeHandle`（世界期、代、槽、提供方序号），可比较、可做字典键、不分配。模块用 `handle.CastEffectHandle/CastChargeHandle/CastPoolHandle/CastRequestHandle/CastProjectileHandle(lifetime)` 铸造，`RegisterNative`、`TryNative`、`IsLive`、`RegisterCancel`、`RegisterOwnedLifetime`、`ReleaseEndedHandle`、`CancelHandle` 都只收这个值；`ReleaseEndedHandle` 和 `CancelHandle` 另收一个 `HandlePort(kind, lifetime)` 说明端口声明的种类和生命期，`ReportOwnerEnd(handle, reason)` 报告拥有者一侧的结束，迁移接管用 `handle.InWorld(worldEpoch)` 得到新世界里的同一个句柄；处理方法用 `context.TryRuntimeHandle(slot, out handle)` 读句柄输入，用 `context.EffectHandleId` 读内核为本命令铸的效果句柄，效果结束回调读 `RuntimeEffectContext.HandleId`，输出用 `context.Outputs.Handle(output, handle)` 写，句柄数组用 `Handles(output, values)`。要线上形式时用 `ToJson()` 或 `Write(writer)`，读回用 `RuntimeHandle.TryRead`。
- 判断实体种类用 `EntityKinds.Is(reference, kind)` 或 `EntityKinds.Is(entityId, kind)`：原地比较，不切字符串。
- 领域回执 `IncomingDamageReceipt`、`PlayerHealReceipt`、`EnemyAttributeReceipt` 的状态和提交态是 `Status`、`Commit`，可以直接交给 `RowWriter.End`。

写入不会向处理方法抛异常：第一条违规被记下，处理方法返回后命令以 `failed + unknown` 结束，码与 JSON 边界相同——行数不等于接收者数、目标不是该位置的接收者、缺列为 `result-row-incomplete`，状态与提交态不配对或码未声明为 `result-row-status`。回执和跟踪只在有读者时才把行渲染成 JSON；下游步骤读结果行直接取结果区。

返回 `CommandResult(JsonElement)` 并用 `ResultRows.Of(rows)` 带行的旧写法仍被接受，经同一个描述符写进同一个结果区，行为不变。

## 实体观察

模块可以在 `RuntimeModule.EntityObservers` 中登记观察函数，由 `RuntimeKernel.InspectEntity` / `InspectEntities` 消费。注册时校验回调、命名空间归属、已拥有的 resolver、重复冲突和容量，失败不部分改变 Registry；注销时清除该模块拥有的观察器并释放委托引用。

每帧读取的调用方改用不分配的三个入口：`TryEnumerateEntityCandidates`（候选引用写进调用方的列表，拒绝时列表不变）、`TryInspectEntitySnapshots`（按去重后的请求顺序写快照，读不到的位置为 null）和单实体的 `TryInspectEntitySnapshot`。它们与 `EnumerateEntityCandidates` / `InspectEntities` 用同一套校验、拒绝码与查询预算。

只要位置与存活状态的读者（区域判定这类空间检测）用 `TryInspectEntityPoses(IReadOnlyList<EntityReference> references, List<EntityPose> poses, out string code)`：同一套校验、去重、拒绝码与预算，按去重后的请求顺序每个实体写一份 `EntityPose`（`X`、`Y`、`Z`、`LifeState`，读不到的位置为默认值，`Observed` 为 false）。提供者在 `RuntimeModule.EntityPoseObservers` 里为自己已登记快照观察的种类再登记 `EntityPoseObserver`：`delegate bool EntityPoseObserver(EntityReference entity, out EntityPose pose)`，填调用方的结构体、不得分配，位置与存活状态须与同一实体的快照一致（`LifeState` 只取 `alive`、`downed`、`dead`）；返回 false 等同快照观察返回 null。内核在读取前后各核对一次引用仍然存活，坐标非有限或状态词不对一律按读不到处理，观察器抛出的异常记日志后同样按读不到处理。没有登记轻量观察的种类由内核从快照观察读出，结果相同，只是分配快照。

返回的快照不可变、引用精确，并显式标注 complete / partial / rejected。**空的完整请求不等于观察不可用。** actor 永远不在 self / source / owner / instigator / event-target 之间回退；关系是有向的，实体种类与伤害治疗极性都不隐含阵营。观察回调是只读的：它不能变更内核，也不能在实体观察期间注销生命周期订阅（普通清理、停止后清理与生命周期回调自注销仍然允许）。

正在派发的触发事件的角色上下文与当前世界的阵营关系随 `EvaluationContext` 一起交给 `evaluate` 处理器：`Actors` 是按角色命名的显式引用集合，`Relations` 是有界有向的关系表，处理器不必也不应自己再查一次。角色与事件槽的对应只有 `RuntimeActorRoles` 这一张表，每个角色只有一个槽位：`source`、`owner`、`instigator` 各自读载荷里同名的端口，`event-target` 读载荷的 `target`（角色名与 `event_target` 这个选择器端口拼写只在这张表里关联），`self` 不读载荷，取派发时算出的挂载主体。端口存在但为 null 与端口缺席都是该角色缺席，某个槽位的值永远不会被拿去回答相邻的角色，事件上也没有第二条事件主体通道。挂载主体来自本计划自己的挂载目标：派发时把「有主体匹配器接受的实体」随排队的工作项带走，按 id 与两个 epoch 去重后恰好一个才是 `self`（无主体挂载如 level 不提供，0 个即缺席），多于一个不同实体时读取 `self` 的步骤以 `actor-ambiguous` 拒绝，既不任选也不报成缺席。缺席语义随后续合同声明的可空性：可空输出写 null，不可空输出（`self`）在处理器运行之前以 `actor-missing` 拒绝该步，不会拿到占位值。角色名也用于 `recipient_anchor` 枚举，两个拼写的映射同样只在这一张表里。

一个种类的候选集合只能由拥有该种类的 provider 交出：`RuntimeModule.EntityCandidates` 登记该种类自己的 `Func<IReadOnlyList<EntityReference>>`，注册校验与 observer 同组——key 必须是合法种类且值非空（`entity-candidate-source`），同一模块必须拥有该种类的 `EntityResolvers`（`entity-candidate-source-owner`），一个种类只能有一个来源，重复登记原子拒绝（`entity-candidate-source-conflict`），注销时连同该 provider 的全部来源一起移除，manifest 不导出。

查询完成性只覆盖显式请求的引用，**从不代表扫描了整个世界或某个空间区域**。候选集合也一样：内核不枚举、不发现，`RuntimeQuerySession.TryCandidates(kind)` 只是把 provider 自己的清单按预算读回。带范围的 `TryCandidates(kind, scope, …)`（`EntityCandidateScope`：一个区域、一个球，或两者）先把清单缩到范围内再计数：区域问该种类自己的区域应答器，位置问它自己的观察器，各只查一次表；缩小后仍超过 256 个照样以 `entity-query-budget` 整体拒绝，不截断；存在却放不进区域或读不到位置的实体让整次读取拒绝，不从集合里漏掉。分页仍属后续工作。

### 从原生实例取得引用

编号与 lifeEpoch 由拥有该实体种类的模块私有分配，其他模块不能猜，也不能拿原生键（例如账号 ID）自造。因此拥有者可以在 `RuntimeModule.EntityInstanceResolvers` 里按种类登记 `Func<object, EntityReference?>`，把进程内活的原生对象映射为当前引用。注册规则与 observer 相同：key 必须是合法种类且值非空（`entity-instance-resolver`），同一模块必须拥有该种类的 `EntityResolvers`（`entity-instance-resolver-owner`），重复登记原子拒绝（`entity-instance-resolver-conflict`）；注销时移除，manifest 不导出。

`RuntimeKernel.ResolveEntityInstance(kind, instance)` 只问该种类的唯一拥有者，不枚举、不回退到其他模块：
- 所属线程；Failed / Stopped 抛 `runtime-not-ready`；注册期或世界未开始时返回 null。
- 种类没有实例解析器抛 `entity-resolver`；解析器抛出的任何异常换成 `entity-resolver-failed`，消息只有种类名、不带内部异常，**实例和由它导出的键不进入错误文本**。
- 答案必须以 `kind:` 开头，并通过与已发布引用相同的路由核验（当前世界、拥有者 resolver），否则返回 null。
- 解析器在实体观察保护内执行：不能变更内核，也不能再查询内核（`entity-observer-mutation`）。

`RuntimeKernel.IsEntityCurrent(reference)` 公开同一条路由核验，线程与状态限制相同，返回 bool，不抛出核验失败。

### 实体结束上报

拥有一个种类的模块可以在 `RuntimeModule.EntityEndReports` 里承诺自己上报这个种类每一条生命的结束：死亡、消失、销毁、被收起、离开会话和世界结束，每条路径都必须调用一次 `RuntimeModuleHandle.EntityEnded(reference)`，此后该引用的 `EntityResolvers` 也必须答 false。生命延续（同一 lifeEpoch 的状态变化）不是结束。注册规则：种类必须合法（`entity-end-report`），同一模块必须拥有该种类的 `EntityResolvers`（`entity-end-report-owner`），一个种类只能被承诺一次（`entity-end-report-conflict`）；注销时移除，manifest 不导出。

- 承诺过的种类，内核每 tick 的生命周期扫描不再调用它的 resolver，只查本世界已上报的结束集合；没有承诺的种类照旧每 tick 询问 resolver。所有种类都承诺且没有新结束时，扫描不调用任何 provider。时间期限（租约到期、调度、效果周期）与世界纪元、注销、作用域取消的行为不变。
- 准入（首次绑定、注册持有、测试夹具核验）仍然问 resolver；迁移提交后的第一次扫描也问一遍，因为接手的生命来自旧主机。
- `EntityEnded` 在所属线程调用，返回 true 表示这条生命第一次被记为结束；世界未开始、纪元不是当前世界或已记过时返回 false。只能上报自己承诺过的种类（`entity-end-owner`），模块已注销时抛 `module-unregistered`。
- 结束在下一个安全点生效：调度、数值租约、句柄、延迟结果与效果在下一次主机 tick 的生命周期扫描里以原来的语义结束（`stale-entity`、回执、效果回收）；该实体的 Player / Weapon / Object / Enemy 变量作用域在调用时释放，若正处于推进、生命周期通知或实体观察中则排队到本次推进结束。
- 提供者撤下注册后，已捕获的检查回到逐 tick 询问原 resolver。

## 定时脉冲

`Schedule` 返回 `ScheduleResult`：`Status`（`scheduled` / `duplicate` / `rejected`）、`Code` 和**可空**的 `Handle`。只有成功时才是 `RuntimeScheduleHandle`（`NextTick`、`DispatchedPulses`、`SkippedPulses`、`Status`/`Code` 和幂等的 `Cancel()`）；重复（`duplicate-schedule`）、同 ID 改定义（`schedule-id-conflict`）和各种拒绝的 `Handle` 都是 null，此时不产生任何定时工作。

`PulseSchedule` 必须显式给出首次时机（`Immediate` / `AfterInterval`）、错过策略（`SkipMissed` / `CatchUp`），并且至少有一个有限上界：`MaxPulses`（≤65536）或 `LifetimeTicks`（结束点不包含，两者同时存在取最早边界）。没有上界的 interval、零 interval、越过 JavaScript 安全整数的到期 tick、lifetime 内脉冲数超过 65536，全部明确拒绝。首脉冲落在结束点之后时仍返回 `Status = scheduled`，但 `Handle` 已经是 `Status = completed / Code = no-pulses-before-end`，没有排队 job，也不补脉冲。

- `SkipMissed` 只执行最新到期的一次 occurrence，用 `SkippedPulses` 和 `missed-pulses` 收据累计跳过数，结束点之后不补历史动作。
- `CatchUp` 只在同一实际 Registry 中 Trigger 及其全部 Action 都声明 `capability.parameters.scheduleReplay = "fixed-inputs"` 时接受，并按 due tick 顺序补发原定结束前的有限 fixed-input backlog。缺声明、需要世界查询或空间选择的效果只能使用 `SkipMissed`。声明的真实性由受信模块负责，核心不推断某效果是否可重放，也不为通过测试添加声明。
- 创建时捕获已加载的 plan 集合：之后载入的 plan 不会追溯加入，卸载任一被捕获的 plan 会在下一次 `Advance` 取消该 schedule。
- 配额：活跃 schedule 上限 256，单 schedule 计划 pulse 上限 65536，每次 `Advance` 最多 64 个 scheduled pulse，捕获 entity 引用最多 16 且不截断。同时受 queue、plan、events、commands、causal、history 的原有配额约束。更低一级的预算优先拒绝或延后，同一 tick 重复 `Advance` 不会重置任何预算。
- 事件身份包含 provider、模块 generation、world、scope、schedule id 的摘要与 occurrence index；queued/dispatched 不是 committed。其它工作占用同一 occurrence 身份时报 `event-id-conflict`，ledger 在回放窗口内写满时报 `event-history-budget`；两者都停止该 schedule，不重试、不二次执行。
- 第一个 pulse 的 handler 使捕获载荷里的实体进入新 life 之后，剩余 catch-up 不再调用旧对象的 handler；该 schedule 以 `stale-entity` 收据结束，不会逐个 pulse 反复失败。

模块原生续延使用 `RuntimeModuleHandle.Schedule(scope, dueTick, work, lifetime?)`，返回同一个 `ScheduleResult` / `RuntimeScheduleHandle`，不要求额外事件订阅。`IRuntimeModuleContinuation.Resume(tick)` 每次只执行一个工作单元，返回下一次绝对到期 tick，返回 null 结束；允许返回当前 tick，但所有模块、计划续延与事件脉冲合用每 tick 64 次预算，同 tick 重复 Advance 不增加额度。队列项只创建一次，续延重排不构造事件、帧或新队列对象。单工作最多 65536 次 Resume，达到时以 `schedule-pulse-budget` 明确拒绝。

`End(status, code)` 恰好调用一次：正常结束 `completed`；句柄取消 `explicit-cancel`；作用域结束 `scope-cancelled`；模块注销 `module-unregistered`；换世界 `world-ended`；捕获生命失效 `stale-entity`；失去主机 `not-host`；异常 `module-continuation-failed`。可选 lifetime 在创建时解析实体核验器，之后只核验固定引用；注册 generation、world epoch 与现有队列共用失效入口。`RandomSeed` 是 BeginWorld 的同一个确定性种子，模块在固定工作时读取。此原生续延没有可迁移的序列化载荷；检查点 / 主机迁移的原生工作续接尚未交付，不能把它序列化成事件计划或宣称可恢复。

## 控制步骤与 query 档位

第一步的控制词汇表由内核 `ControlContracts` 拥有（`forge.control.flow.*`：7 个 canonical 定义、0 个 binding handler）。内核在走后继表的同一处按控制 id 分派它们，没有第二个路由器、也没有控制专用的执行路径；每种控制的执行输出个数与端口形状以注册合同为准（`next` 是出口，`pulse`/`body` 是下一次激活的入口），计划里 `successors` 的项数与顺序必须与之逐项相符（`successor-shape`）。表外的控制按 id 拒绝（`control-unsupported`）。

| 控制 | 进入时 | 之后 |
| --- | --- | --- |
| `branch` | 读 `condition` | 真走第一个后继、假走第二个 |
| `sequence` | 按执行输出顺序深度优先逐个走完：每个后继及其整条链走完，才走下一个 | — |
| `delay` | 写出 `timer` 句柄并登记 `MaxPulses = 1` 的续延 | 到点才从 `next` 继续，进入时不走 |
| `interval` | 写出 `timer` 句柄并登记 `PulseSchedule{IntervalTicks = interval, FirstPulse = first_pulse, MissedPulsePolicy = SkipMissed, MaxPulses = count}`（`count ≥ 1`） | 立即走 `next`；每跳把该跳的 `timer` 重新写进控制帧后从 `pulse` 走一遍 |
| `repeat` / `for_each` | 同一派发内同步循环：每轮写 `index`（`for_each` 另写 `item`）后走 `body` | 轮数走完走 `next`；零轮直接走 `next` |
| `cancel` | 取消 `task` 句柄持有的调度 | 实际取消数写进 `cancelled`；句柄已失效报 `stale-handle`、计数为 0 |
| `present` | 读 `value`：不是 null 走 `present`，是 null（或没接线）走 `missing`；实体引用已被所属方证明失效（`stale-entity` / `stale-world`）时同样走 `missing`，不取消整个入口，其他观察失败照旧拒绝 | 测过的值按计划自己声明的端口 id 写进控制帧；只有这一步自己的 `present` 出口区域内的步骤能读它，区域外（包括另一个 `present` 步骤的区域）读取按 `present-branch` 拒绝；`carried` 端口在区域内按非空用，别的可空 / 可选来源在区域内照旧按 `nullable-port` / `optional-event-port` 拒绝；`pure`/`query` 步骤的全部读取者（递归）都在同一个 `present` 区域内时，它也属于该区域 |

- 续延由内核自己保存（planId、控制步下标、输出序号、帧快照、句柄），重入时帧里的实体与句柄照常做 world/life epoch 校验，槽位或世代不符报 `stale-handle`/`stale-world`。`BeginWorld` 清空全部续延、调度与句柄池。
- 每次派发、每个循环轮、每次调度重入各算一次“激活”：`query`/`pure` 的 memo 以激活为作用域，重入即清空。每次激活至多执行 `MaxStepExecutionsPerDispatch = 2048` 步，超出报 `dispatch-step-budget`。循环控制的轮数另有 `MaxControlIterations = 1024` 上限：`repeat` 的 `count`、`for_each` 的候选数或它自己的 `budget` 超过上限时整个控制步骤被拒（`iteration-budget`），不跑一部分、也不静默截断。
- 句柄是“池地址 + 世代”：分配时写 `worldEpoch`/`lifeEpoch`/`local` 三元组加创建 provider 的注册下标，由消费它的那一步校验一次（种类与生命期来自端口声明，不符报 `handle-kind`/`handle-lifetime`）；槽位回收后再读同一值报 `stale-handle`，不会命中新占用者。活跃句柄超过 `MaxLiveHandles` 报 `handle-budget`。
- `query` 步骤经只在本次求值内有效的查询会话读世界：每 tick 64 次查询 / 1024 个实体引用、单次最多 256 个引用，额度耗尽或观察者不可用时该步显式拒绝（`query-budget`/`query-observer-unavailable`），**不返回截断后的半份结果**。`pure` 步骤拿到的是拒绝一切读取的会话，注册期也要求它零世界端口（`pure-world-port`）；带 entity/resource/handle 端口的 selector/condition/modifier 必须是 `query`，只读值的观察不得自称 `query`（`query-authority`）。
- 按种类枚举候选走同一份预算：`RuntimeQuerySession.TryCandidates(kind)` 每次计入一次查询和它返回的引用数，单次最多 256 个候选（超限报 `entity-query-budget`，**不截断**）；没有 provider 暴露该种类时以 `entity-candidates-unavailable` 拒绝，provider 自己的清单给出别种类的引用、抛异常或返回空时报 `entity-candidate-kind`/`entity-candidates-failed`。枚举只拿回引用，不附带快照。
- 挂载过滤发生在入队之前：每个挂载目标问拥有该 kind 的 provider 注册的匹配器——有主体的 kind 拿事件载荷里每一个实体端口的值去问（同一实体出现在两个端口上仍算一个主体），无主体的 kind（`level`）只按挂载目标判定；`category` 与 `reference` 原样交给匹配器。事件没有被任何挂载命中时按 `ignored` + `attachment-mismatch` 返回，不进队列；没有匹配器的 kind 在加载期就被拒，所以计划不会“加载成功但永不派发”。

## 数值来源租约

`AcquireNumericLease(NumericLeaseRequest)` 返回 `NumericLeaseResult`：`Status`（`acquired` / `duplicate` / `rejected`）、`Code` 和**可空**的 `Handle`。只有成功时才是 `RuntimeStateLeaseHandle`（`ExpiresAtTick`、幂等的 `Release()`）。定义必须已通过同一个 Registry 注册为 `kind = "state"` 且 `parameters.valueType = "numeric-contribution"`，版本严格锁定；未知定义报 `state-definition`，缺类型的定义报 `state-value-type`。

唯一 key 是 provider + 完整 target（含 world/life）+ 完整 source 或 null + scope + definition + stackGroup。同一 key 的第二份贡献以 `state-source-conflict` 拒绝，不隐式 refresh、不叠加、不替换；不同 stackGroup、不同 source、不同 life 之间互相隔离。

读取由 `EvaluateNumericState(target, definitionId, stackGroup, baseValue)` 完成：每次从传入的 base 重新计算 `(base + Σ additive) × Π multiplier`，按稳定的 source 顺序排序，因此结果与获取顺序无关；溢出拒绝，零乘数精确表示，**移除贡献不做除法回滚**。

`DurationTicks` 为正，到期点不包含。到期、release、来源或目标的 life 变化、world 变更、模块注销、作用域取消都会移除贡献。活跃上限 512，超过以 `state-lease-budget` 拒绝。申请与读取都要求宿主已以 `isHost = true` 推进过该世界。核心不写游戏属性、不按阵营或实体种类推断目标，也不承诺已提交副作用的回滚。

## 执行帧形状

每个计划装载时都按端口合同生成一份位置化的帧描述符：每个步骤的输入区、`pure`/`query` 的 memo 区或结果行区，每个入口的事件载荷帧与结果行区，以及一个写一次的计划级常量池。`ValueKind` 的编号是 wire 的一部分，新类型只在尾部追加：`Resource = 10`、`Event = 11`、`Result = 12`，前十个值不变；`RuntimeValueTypes` 与之一一对应（`ValueKind - 2` 就是端口类型的密集下标）。

一个值的槽宽：`execution` 不占槽；`boolean`/`integer`/`number`/`string`/`enum`/`entity`/`handle` 各一槽（`handle` 把创建它的 provider 注册下标放在同一个槽里）；`vector3` 三槽，头槽带 x、后面两槽带 y 和 z；`resource` 两槽，头槽是资源种类在共享词表里的下标、第二槽是 id 的字符串槽；`event` 一槽，是本次派发事件行的下标；`result` 没有自己的头槽，行宽等于它声明字段的宽度之和（前四列固定 `entity`/`enum`/`enum`/`string`，各一槽）。

集合（`many`）是「一个头槽带元素个数 + 256 个元素槽」，元素按自己的宽度走，所以 `vector3` 集合按 3 槽一个元素预留。只有元素类型的七种（`boolean`/`integer`/`number`/`string`/`vector3`/`entity`/`handle`）有集合形式：`resource`、`event`、`result` 是引用或行，`enum` 元素不开放，声明成集合即在帧层按 `unsupported-port` 拒绝。`FrameWriter.SetSegment` 开头槽、按类型的 `SetBoolean`…`SetHandle` 逐元素写，`SetResource`/`SetEvent` 写单值；`Frame` 侧对称地给出 `Count`/`Element` 与各类型的元素读取。

每步的 66 值槽预算只算它解码的值：集合预留的整段、`handle`、`resource` 与结果行都不计入（超限报 `frame-slot-budget`），它们连同常量池一起只受计划帧字节预算约束（每槽 24 字节加常量池字符串字节，超限报 `frame-bytes-budget`）。常量池只接受编译期 `resource` 引用（种类取端口的 `resourceKind`，id 取字面量的 `id`）；`handle`、`event`、`result` 在帧层没有字面量形式，分别按 `handle-literal` 和 `unsupported-port` 明确拒绝，不会静默写成别的值。

枚举集合的声明顺序就是端口布局（算进计划 `layoutHash` 的那份）里的 `valueSet` 下标，因此只允许尾部追加：既有 22 个集合的下标不变，`commit_state` 接在其后（下标 22），`agent_modifier`/`rundown_tier`/`door_state` 依次是 23/24/25。集合内的成员同样只尾加：`ai_state` 追加 `patrolling`/`hibernating`，`commit_state` 是 `none`/`confirmed`/`unknown`/`partial`。`ResourceKinds` 追加 `chained-puzzle`/`zone`、`HandleKinds` 追加 `pool`/`request`，旧成员（含 `room`、`pool_membership`、`transaction`）在引用它们的目录行改形之前一律保留。

## 宿主边界

`ExportManifest()` 导出实际注册快照。`LoadPlans(candidates)`（I-PACK）批量从宿主发现的离线计划文件重新验证运行时版本、领域、绑定闭包与端口布局；运行时未就绪时和其余调用方错误一样直接抛出，不吞掉、也不逐文件报告。每个文件独立产生一条 `PlanLoadOutcome`，互不影响——同一 planId 出现在多个文件中，全部按 `plan-conflict` 拒绝，写出的记录消息带上该冲突组全部相对路径；无法解析 planId 的文件按自己的错误单独拒绝，不计入冲突分组；解析后的计划总数上限 128（`plan-budget`），超出部分按传入顺序依次拒绝。**权限不再由宿主授予或过滤**，计划声明的 `permissions` 只用于 `permission-lock`：必须与其绑定闭包全部 `RequiredPermissions` 的并集精确相等（`ExactSet`），多一个少一个都拒绝。计划格式是 schemaVersion 1（形状变了就升版，不做兼容）：`bindings` 只列绑定号，节点以它的下标引用；计划不带端口布局，只带一个 `layoutHash`：Runtime 按已注册合同（含可变端口与端口组展开）推导每个节点的输入、输出布局，按入口顺序、每个入口先触发器再各步骤，逐节点拼接“积木 id、换行、输入布局的规范 JSON、换行、输出布局的规范 JSON、换行”，取 SHA-256 的小写十六进制，与计划里的不等就以 `layout-mismatch` 整份拒绝；节点的 `constants` 按参数声明顺序排列（null 表示未填写），数据输入是 `{slot, fromEventSlot}`、`{slot, fromStepSlot: {step, port}}` 或 `{slot, value}` 字面量三选一。计划声明 `limits` 仍是 `maxEventsPerTick`、`maxCommandsPerTick`、`maxQueuedEvents`、`maxCausalDepth` 四个键；查询、控制与句柄的额度是内核常量，没有计划级字段。顶层 `attachments[]` 必填且非空，每项是 `{kind, category?, reference}`，`kind` 属于 `level`/`map-object`/`gear-block`/`enemy-type`，按 `(kind, category, reference)` 序数升序且不重复（`attachment-empty`/`attachment-order`/`attachment-duplicate`/`attachment-budget`）；`level` 由内核匹配当前世界，其余 kind 在加载期要求已有 provider 注册的匹配器，否则报 `attachment-kind`。加载时把下标解析回端口名，处理器仍拿到按名称组织的 `Parameters` 与 `Inputs`；位置化的执行帧 SDK 已提供（`PlanFrames` 描述符加 `Frame`/`FrameWriter` 读写），端口名在装载期就已经用完。注册按 v0.2 校验端口的 cardinality、resourceKind、handleKind 与 lifetime，参数必须声明 role，每个 action 必须声明完整的 recipients（target、cardinality、result），每个 result 端口必须声明行字段——前四个固定为 `target`/`status`/`committed`/`code`，其后字段各自给出 id、值类型、单位、可空与值集，形状不符报 `result-schema`。入口带 `start`，步骤带 `nodeKind`（`action`/`control`/`query`/`pure`）与 `successors`：`successors` 只列该步骤的执行输出，按声明顺序一一对应，`query`/`pure` 恒为空；派发从 `start` 起沿后继前进，`control` 由内核在同一处按控制 id 分派（不查 handler），`query`/`pure` 步骤只在消费者用 `fromStepSlot` 读取时按需求值，可读同一次激活内任意更早步骤的帧（含控制步骤写出的 `timer`/`index`/`item`/`cancelled`），同一次激活内每个步骤最多求值一次，求值失败把消费者变成 rejected 回执，求值器自己抛的合同错误保留它的码，只有不带码的失败才统一编码为 `pure-evaluation-failed`。`handle` 只能来自更早的步骤或事件端口，计划里的字面量报 `handle-literal`，消费端口声明的种类与生命期不符报 `handle-kind`/`handle-lifetime`；`resource` 只接受编译期常量引用（帧里占两个槽：种类下标加 id 的字符串槽），运行期构造报 `resource-runtime-value`。`steps` 数组顺序必须是 Kahn 拓扑序（`nodeId` ordinal 决胜），加载器按同一算法重算后逐项比对；入口只在 rejected/failed/cancelled/expired 或 partial+unknown 时停止。节点的 `promoted` 提升的 value 参数按声明顺序追加为输入，dispatch 时并回 `Parameters` 并按注册合同重新校验，越界拒绝不钳制。`evaluate` 角色把 selector/condition/modifier 绑定到 `RuntimeModule.Evaluators`，只读 `EvaluationContext` 有 NodeId、Parameters、Inputs、预算内的 `Query` 会话，以及本次派发事件的 `Actors` 与当前世界的 `Relations`。未知控制流、动态结果依赖、recipient-policy 与未实现的数据引用都明确拒绝；运行期数据端口只接受 boolean、integer、number、string、vector3、entity、enum（enum 的 wire 值是集合成员下标），`many` 只允许 entity；帧层本身已经有 handle、resource、event 三种形状与七种元素类型的集合形式，端口白名单尚未随之放开。

`BeginWorld(epoch)` 需要递增的安全整数 epoch，取消所有旧世界的排队事件并清理去重与取消记录；**这不是检查点或网络恢复**。`Advance(simulationTick, isHost)` 使用游戏提供的模拟时间，按 due tick 与入队序号调度。重复 tick 不会重置预算，积压以有界延后保留；非主机不调用 handler。主机迁移由 `BeginHostMigration` 暂停派发、`CaptureHostMigration` 只读成像，并在原版 recall 后由 `RestoreHostMigration` 以新 epoch 接管；它不调用 `BeginWorld`，也不重放原生效果动作。模组注册后缓存自己绑定的 `SubscriptionGate(bindingId)`；游戏 hook 先读门的 `HasSubscribers`，无订阅时立即返回。

提供方通过公开的 `RuntimeModuleHandle.RegisterProviderHandover(schema, capture, prepare)` 注册一个接管格式；注销会移除它。`capture` 接收本方未结束的句柄与资源预约，返回不超过 65536 UTF-8 字节的对象。`prepare` 接收 `sourceEpoch`、`targetEpoch`、`isHost`、`source`／`retained` 数据、目标句柄及 `owner`／`receiver` 义务，只读构造 `ProviderHandoverAdoption`：每项 `ProviderHandleAdoption` 必须逐一使用内核给出的完整目标句柄，保留原对象身份、存活判定和取消义务。无法唯一对应原生回忆后的对象必须明确拒绝，不得按可复用编号换绑。只有 `Adopt` 切换准备好的私有表；捕获、预检与采用都不得重放游戏效果、重新预约、结算或清理。公开 SDK 不开放宿主迁移入口，具体领域包仍须先证明原生身份和状态可恢复。

`CommandContext` 携带 plan 与 resource revision 与 nodeId、原始事件与根因果、命令 ID、world epoch、计划与执行 tick、因果深度和显式 Inputs。处理器通过 `Succeeded(outputs, facts)`、`Rejected(code, detail)` 或 `Failed(code, detail)` 返回。请求量与实际量由领域结果区分，实际变化为 0 仍可成功。提交后的事实只能由该处理器所属模块发布并继承真实因果深度；异常不会自动重试，也不承诺已提交副作用的回滚。

`TickResult` 另带 `Schedules` 和 `StateLeases` 收据，但**只包含该次 `Advance` 期间**产生的条目。主动调用 `Cancel()`、`Release()` 或 `CancelScope` 不保证出现在之后的 `TickResult` 里；这些调用应读方法自身的返回值和句柄的 `Status`/`Code`。

## 执行日志 sink 与级别

合同在 `RuntimeLogContracts.cs`，内核侧在 `KernelLog.cs`（日志类，持有 sink、各 provider 的级别门和全部记录点），门面入口在 `RuntimeKernel.Logging.cs`。宿主用 `RuntimeKernel(identity, limits, IRuntimeLogSink sink, RuntimeLogLevel runtimeLogLevel)` 构造内核，sink 在内核生命周期内固定，没有注册或替换接口。`RuntimeLogLevel` 为 `Off < Error < Info < Trace`，构造参数只接受 off、error、info（否则 `log-level`），trace 只能经提级得到。`RuntimeLogConfiguration.ParseLevel(string)` 是引擎 cfg 与 ForgeDevelopment cfg 共用的文本词表：off、error、info，大小写与首尾空白不敏感，非法值抛同一条消息；SDK 自己不读任何 cfg。

- `RuntimeLogRecord` 是 `readonly struct`，以 `in` 传给 `IRuntimeLogSink.Write(in record, RuntimeLogLevels levels)`。字段：Level、Code、Provider、SubjectProvider?、Tick、WorldEpoch、Frame?、CommandId?、EventId?、CauseId?、RootEventId?、Plan?（planId 与 resource id/revision）、Path?、Permissions?、Entry?、Step?、Binding?、Result?（status、commit?、reason）、FixedDeltaTime?/TicksPerSecond?（只有 `runtime.tick_rate` 用：宿主读到的固定步长与由它算出的 tick 率）、Layout?、Entity?/Values?（只有领域记录点用：实体引用 id 与具名标量）、Detail?（自由文本，只并入 sink 拼出的消息）。**没有 message 与 inputs**：消息由 sink 在后台线程拼，调用点不构造字符串；inputs 属于 Development 的 trace recorder，未实现。status 与 commit 是不做词表校验的字符串。
- 级别表按 provider 保存。每个条目在注册时建立：`RegisterModule(module, level)` 的级别只接受 off、error、info（否则 `log-level`），注册成功后该 provider 的 `LogGate` 返回这个级别；未注册的 provider 仍然抛 `log-provider-unregistered`，不静默当作 off 或 error。注销时移除条目，之后再查同样报错，不允许句柄失效后继续写。Runtime 自己的条目（`Identity.Id`）由宿主构造建立、也不由模块注销删除：模块即便声明同一个 provider id 也替换或删除不了它。表变化（注册、注销、提级）都立即发布新快照；注册时**先发布新表再写 `binding.registered`**，所以每条记录携带的表都已经列出它自己的 provider。
- `ElevateLogging()` 把所有条目升为 Trace 并切换到提级限流档。只在注册窗口内接受一次；窗口关闭（Ready、Failed、Stopped）或第二次调用都抛 `log-elevation-rejected`，不可撤销。级别门是同一个可变对象，提级前取得的门也会看到 Trace；提级之后注册的 provider 也是 Trace，不是它的 cfg 值。
- `WriteLog(in record)` 是记录点到 sink 的唯一通道：所属线程（`wrong-thread`）；无 sink 的内核抛 `log-unconfigured`；缺 code、provider 或不完整的 plan/result 抛 `log-record`；Off 或门未开抛 `log-level-disabled`。调用点应先比对门，再构造记录。
- 每次级别表变化都会发布新的 `RuntimeLogLevels` 快照（按 provider 排序、带 tier）。sink 只在 **tier 变化**时写新的 `log.level` 行（首行 + 提级行），所以注册多少 provider 都不会多出行；提级之前的快照只是让后续记录带上最新的 provider 列表。宿主 writer 的构造参数里带 Runtime 的 provider id：首条记录就被限流丢掉时，`log.dropped` 仍要能写出归属。
- 诊断码常量不手写：各包的码常量类（`RuntimeLogCodes`、`RuntimeLogReasonCodes`、`TriggerLogCodes`，以及宿主和各领域的 `*LogCodes`）只留一个 `partial` 声明，常量在构建前由 `Tools/Forge/build-diagnostic-constants.ts` 从 `catalog/authoring/diagnostic-codes.json` 生成（`artifacts/generated/diagnostics/<类名>.g.cs`，不入库；码表行的 `csharpConstants` 写类名和常量名，`meaning` 成为文档注释）。编译某个类的工程在 `ForgeDiagnosticConstants` 项里列出类名（`Mods/Build/DiagnosticConstants.targets`）。`RuntimeLogCodes` 含 `log.level`、`log.dropped` 与内核每个记录点的码；`RuntimeLogReasonCodes` 只有 `invalid-handler-result`（结果组合非法时内核改记 failed/unknown）与 `lifecycle-observer-failed`（观察者故障结果码），其余 reason 都是产生它的异常码或结果码本身。`adapter.*` 待 I-ADAPTER-SCHEMA，未声明。
- 内核记录点（归属规则）：`registration.rejected`（被拒注册与被拒容量都记，被拒注册的 provider 写进 `subjectProvider`；解析 seed 才知道 provider id 的失败没有 subject）、注册时每个 binding 一条 `binding.registered`、`BeginWorld` 一条 `world.began`、每次入队（发布、事实转发、计划 pulse 放行）一条 `trigger.fired`、事件被拒按 reason 是否以 `-budget` 结尾分流成 `event.rejected`（error）与 `budget.exceeded`（error，发布期的 `queue-budget`、`event-history-budget` 也走这一条）、每 tick 最多一条 `event.deferred`（trace，reason 是第一个拦下它的预算码，含 `scheduled-tick-budget`）、排队事件在派发前被丢弃就写一条 `event.cancelled`（trace，覆盖发布方注销、scope 取消、计划卸载、计划脉冲被跳过期丢弃）、每次调用的 `step.started`（trace）与 `step.finished`（`failed` 或 commit 为 `unknown` 记 error，其余 info，唯一带 `commit` 的记录）、结果停下且该步仍有后继时一条 `entry.stopped`（info，归 Runtime）、观察者抛异常时一条 `observer.failed`（error，归该观察者 provider）、以及 `LogSuspended(code, detail?)`（error，每次暂停一条；`StartRuntime` 失败写 `startup-failed`，正常停止不写——停止不是暂停）。
- `step.*` 与 `trigger.fired` 的 provider 取自**执行该步的 binding**（`step.BindingId`），不是入口的触发器 binding：跨包触发时归属和级别门都属于真正执行的那一方。
- 内核直接写事件与步骤记录，不再把 `TickResult` 回执订阅转成日志；error/info 不带 inputs，trace 的 inputs 复制仍由 Development 的 trace 记录器负责，本层只留记录点。
- 生成布局记录点：`ReportGeneratedLayout(in RuntimeLogLayout)`（info，归 Runtime）写 `map.layout-generated`，字段为 `Layout?`（`complete`、`elevatorLandedTick`、`zones[]`、`connections[]`）。调用方是观察关卡生成的那一层，它的事实由 `RuntimeLogLayoutCheck` 在写之前校验（区域 128、连线 512、每区域瓦片 64、方向词表 north/south/east/west/up/down/same/unknown），不合格以 `log-record` 拒绝而不写出一行；级别关闭时不写也不抛。记录格式见 `Docs/forge-contract/FORGE-FRAMEWORK.md` §3.2。
- tick 率记录点：`ReportTickRate(double fixedDeltaTime, double ticksPerSecond)`（info，归 Runtime）写 `runtime.tick_rate`，字段为 `FixedDeltaTime?`/`TicksPerSecond?`。调用方是唯一能读 Unity 的宿主绑定：它在世界纪元建立时读 `Time.fixedDeltaTime` 并算出 `1/那个步长`（合同 §3.2「时间单位」把 tick 定义为宿主的一次 `FixedUpdate`），框架层只带两个数、不引用 Unity。两个值必须有限且为正，否则以 `log-record` 拒绝而不写出一行（0 或 NaN 会让整局的秒↔tick 换算失真）；级别关闭时不写也不抛。

- 领域记录点：`RuntimeModuleHandle.Logs(level)` 与 `Log(level, code, in RuntimeLogFacts)`。句柄第一次用时取到本 provider 的级别门并缓存，之后判断只是一次比较；提级改的是同一个门对象，注销时门被关到 Off，所以失效句柄自己停写。provider 由句柄盖，tick 与 world epoch 由内核盖，包写不出别的 provider 或别的时刻的记录。`RuntimeLogFacts` 只有 Binding?、EventId?、Entity?、Values?（`RuntimeLogValue` 具名标量：文本、整数、有限数、布尔）与 Detail?（只进消息）。写出前校验：code 是至少两段的点分小写名、不能是 `RuntimeLogCodes` 里的内核码（包伪造不了 `trigger.fired`、`event.rejected`）；values 至多 16 个、名字为小驼峰且不重复、文本至多 256 字符、数必须有限、整数绝对值不超过 2^53−1（JSON 读者能精确保留的上限，Steam64 都在它之上）；entity、eventId、Detail 与文本值里出现 Steam64 形状的数字（17 位且以 7656119 或 7656120 开头；生成的十六进制 id 里偶然出现的 17 位数字串不算）时，该字段整段换成 `account-shaped`，记录照写，诊断不会因此丢失，日志不带账号标识；其余不合格抛 `log-record`。引用作者或原生文本（plan 里的引用、文件名）用 `RuntimeLogValue.Quote`：截到 64 字，含账号形状数字时整段换成 `account-shaped`，所以这种记录点不会因别人写的内容被拒。级别关闭时 `Log` 直接返回；要构造数组、字符串或载荷的调用点先问 `Logs(level)`，关闭时零分配（`tests/Perf` 的 `domain-log` 块给数字）。宿主程序集用同一路径的 `LogRuntime`（归 Runtime），写门槛、检查点与网络握手这些只有宿主看得到的事实。writer 把 `entity` 与 `values` 对象写成记录字段，并把它们并入消息。
- 与内核记录的分工：领域事实被内核拒绝时内核已经写了 `event.rejected` / `budget.exceeded`，入队已经写了 `trigger.fired`；领域不再为同一事实另写成功或被拒行，只写内核看不到的东西（发布前自己判定的拒绝、生命与放置的开合、逐发逐命中的 trace 诊断）。

原有构造函数 `RuntimeKernel(identity, limits)` 不带 sink，测试与领域消费方仍用它；它的日志接口一律拒绝，不写任何东西，领域记录点在这种内核上永远关闭。

## 预算

运行预算由已锁定的 manifest 约束：入口 32、每入口 128 步、总 512 步；每 tick 128 事件 / 512 命令、队列 1024、因果深度 16，作者计划可以要求更低的上界。单事件 64 KiB，命令最多 128 事实、总结果 256 KiB；每世界去重账本最多 65536 项，只记住回放窗口（3600 tick）内的身份：事件按自身 tick 计，lease 与 schedule 按最后一次仍活跃的 tick 计，每 256 tick 或账本写满时清掉窗口外的行；早于窗口的事件以 `event-stale` 拒绝，所以被清掉的身份不会被重放。窗口内写满时明确拒绝新事件（包括新的 schedule 与 lease）。定时与状态另有 provider 共用 256 活跃 schedule（`schedule-budget`）、每个计划自己 256 个挂起（delay、带间隔的 repeat、ramp、动作组与 wait_event 合计，超出只拒该计划，`plan-suspension-budget`）、65536 计划 pulse、每 tick 64 scheduled pulse、16 捕获引用、512 活跃 lease 的上限；控制另有每控制 1024 轮（`iteration-budget`）与每次激活 2048 步（`dispatch-step-budget`），查询另有每 tick 64 次查询 / 1024 个引用、单次 256 个引用、单种类候选枚举 256 项（`query-budget`）与活跃句柄上限（`handle-budget`）。所有 epoch 与 tick 限制在 JavaScript 安全整数范围内。

## 当前不支持的边界

执行范围是 host 的 Trigger → Action/控制步骤图：第一步的七种控制（含 `pulse`/`body` 区段的续延）已可执行，`query` 步骤按需求值。动作结果行按装载期编译的位置化描述符写入：每接收者一行、顺序与接收者集合一致，`target` 取同位置接收者的完整 entity / resource / handle 身份（含 `entityKinds`、`resourceKind`/`schema`、`handleKind`/`lifetime` 约束），其中行内 entity 值（target 与后续 entity 列同一形态）占三个槽位——id 文本加两个 epoch——写入与渲染都按这三个槽位往返，不合成 id、不窄化 epoch，边界就是契约自身的 `[0, MaxSafeInteger]`（越界明确拒绝而非截断）；四个固定列按规范词表与配对校验，成本列由结算写在该步自己的行上，越界、畸形行与非法状态配对分别以 `result-row-budget`、`result-row-incomplete`、`result-row-status` 拒绝并保守落成 `failed + unknown`（不发布事实、不保留未校验输出）；内核在调用前拒绝的步骤同样按同一描述符合成每接收者一行。下游步骤读取结果行也走同一条类型化路径：`fromStepSlot` 指向源步 `recipients.result` 命名的端口，内核按该描述符从本激活的行区按位置取行（每接收者一行、顺序不变），JSON 只在这两处边界产生——handler 入参与 receipt/trace/网络；内部执行帧不含行端口，收据不是内部表示，也没有第二套行协议或回退。仍不在范围内的是十二个非 entity 目标的生产者迁移（三个 handle 行 `effect.cancel`、`map.spawn_commit`、`map.wave_stop` 与九个 resource 行仍由编译器写成 `target: entity`，当前由 recipients 声明提供身份；这些生产者写的行目前不含 `target` 列，因此被本 ABI 以 `result-row-incomplete` 拒绝、命令保守落成 `failed + unknown` 且不发布事实——迁移完成前这 12 个动作不报告结果行，这是明确拒绝而不是兼容回退）、触发器自身的结果行写入、第二步的 `cancel_scope` 与 join/parallel/retry/timeout/transaction、可变历史世界快照、完整 VM，以及持续效果（Buff 层级的作者 UI、refresh 策略、属性回写或原生 modifier lowering）；也没有第二套内核、时钟、Registry 或兼容入口。numeric lease 只是 SDK 内部的重算贡献记录，不代表游戏属性已改变。`BeginWorld` 不处理断线重连、状态同步或多人复制——整个网络层属于框架 §6 U-NET。

上述任何测试通过都不证明游戏内注入、多人复制、全部状态与周期效果或性能已验收。
