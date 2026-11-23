# Trigger 提供者（ForgeEngine 的一部分）

> 实施顺序与原版内容完整覆盖见[唯一开发计划](../../../../Docs/forge-contract/FORGE-FRAMEWORK.md) §4，分步验收见 §7；本文件仅说明实现与用法。

跨域逻辑节点（provider `forge.module.trigger`），使用唯一的 Runtime SDK。它不再是独立的包：纯计算与目标筛选的代码在 ForgeEngine.Framework 里（`Mods/ForgeEngine/Framework/Trigger/`，命名空间 `ForgeEngine.Trigger`），原生一侧的启动与自测适配在 ForgeEngine 宿主里（`Mods/ForgeEngine/Native/Trigger/`），注册由 ForgeEngine 插件完成；provider、能力与节点 id 与并入前逐字相同。

Trigger 的节点词汇是覆盖全部领域的横切基座——Map、Weapon、Enemy 各自的机制都落在它提供的原子上，玩家拼出的每一张图都从这里取积木。

交付要求见唯一开发计划 §6 U-TRIGGER（链接见[仓库 README](../../../README.md)）。

## 当前能力

生产 `ModuleDefinition` 由一组声明表组合而成：`Pure/PureModule.cs`（11 行纯计算）与 `Targeting/ObservedQueryModule.cs`、`Targeting/ObservedSpaceNodes.cs`、`Targeting/AreaFieldNodes.cs`（22 行观察）。每行同时给出能力（id、kind、label、description、graph）、binding 与求值器/handler，注册的能力、绑定与 handler 因此不会互相漂移。注册走内核的内置注册（`RegisterBuiltinModule`）：ForgeEngine 插件在 Load 里经 `Native/Trigger/TriggerHost.cs` 注册 `ModuleDefinition.Create(fields, LevelSight.Visible)`，离线清单导出用 `Create()`（没有安装在前面时的诚实空场表）。provider 属于 ForgeEngine 自己，不带包名、插件 GUID 或自己的 cfg。方法存在不等于节点已注册。

## 已注册节点

33 行，全部 `execution` 与 binding role 一致：11 行 `pure` 绑定为 `evaluate`，22 行 `query`（需要世界端口或事件角色）绑定为 `observe`。类别、端口、参数与枚举集合逐字取自网站 `catalog/authoring/capability-catalog.json` 的对应行——`tests/TriggerContracts` 带网站目录运行时逐字段比对，目录缺失直接失败，不做跳过。binding 名为 `forge.module.trigger.binding.<名字>`。

| 能力（pure） | handler |
| --- | --- |
| `forge.condition.predicate.all` | `trigger.condition.all` |
| `forge.modifier.list.element_at` | `trigger.modifier.element_at` |
| `forge.modifier.text.fragment` | `trigger.modifier.fragment` |
| `forge.modifier.value.add` | `trigger.modifier.add` |
| `forge.modifier.value.clamp` | `trigger.modifier.clamp` |
| `forge.modifier.value.constant` | `trigger.modifier.constant` |
| `forge.modifier.value.direction` | `trigger.modifier.direction` |
| `forge.modifier.value.distance` | `trigger.modifier.distance` |
| `forge.modifier.value.map_range` | `trigger.modifier.map_range` |
| `forge.modifier.value.random_range` | `trigger.modifier.random_range` |
| `forge.modifier.value.text` | `trigger.modifier.text` |

| 能力（query） | handler |
| --- | --- |
| `forge.condition.list.contains` | `trigger.condition.contains` |
| `forge.condition.list.is_empty` | `trigger.condition.is_empty` |
| `forge.condition.predicate.compare` | `trigger.condition.compare` |
| `forge.modifier.list.count` | `trigger.modifier.count` |
| `forge.query.entity.kind` | `trigger.state.kind` |
| `forge.query.entity.zone` | `trigger.state.zone` |
| `forge.selector.target.area` | `trigger.selector.area` |
| `forge.selector.target.chain` | `trigger.selector.chain` |
| `forge.selector.target.distinct` | `trigger.selector.distinct` |
| `forge.selector.target.event_target` | `trigger.selector.event_target` |
| `forge.selector.target.filter` | `trigger.selector.filter` |
| `forge.selector.target.instigator` | `trigger.selector.instigator` |
| `forge.selector.target.limit` | `trigger.selector.limit` |
| `forge.selector.target.nearest` | `trigger.selector.nearest` |
| `forge.selector.target.owner` | `trigger.selector.owner` |
| `forge.selector.target.partition` | `trigger.selector.partition` |
| `forge.selector.target.random` | `trigger.selector.random` |
| `forge.selector.target.self` | `trigger.selector.self` |
| `forge.selector.target.shape_overlap` | `trigger.selector.shape_overlap` |
| `forge.selector.target.source` | `trigger.selector.source` |
| `forge.selector.target.union` | `trigger.selector.union` |
| `forge.selector.target.zone_members` | `trigger.selector.zone_members` |

**结构化记录**：`TriggerLogCodes`（常量在构建前由码表 `catalog/authoring/diagnostic-codes.json`生成），经 trigger provider 的注册句柄（`TriggerHost.Start` 注册后 `TriggerLog.Attach`）写入 Forge Runtime 日志。`trigger.zone-members`（trace，只在 Authoring 提级后写）：每次 `zone_members` 求值一条，带候选数、保留数、是否按区域过滤与本机逐候选读区域的耗时 `elapsedUs`；级别关闭时不读时钟、不分配。离线套件没有附着句柄，什么都不写。日志级别就是 Runtime 自己的 `Logging.Level`（没有 trigger 专属 cfg）；启动信息用 `trigger.startup`（info），区域场读取拒绝用 `trigger.area-field-refused`（error），也使用同一写入器。

`add`（计算）与 `all`（逻辑）是目录声明的可变端口行：端口按 `input_count`（1…32，缺省 2；取反、绝对值、取整用 1 个输入，只剩第一个输入口）在计划里展开，注册时没有固定端口布局，所以它们不声明 shape，由框架按计划解析。观察行的 handler 不复制任何算法：四个集合选择器直接调用 `Pure/ReferenceCollections.cs`，空间、筛选与分区的行调用 `Targeting/ObservedSpaceNodes`/`ObservedRecipientFilter` 的同一份算法，`forge.query.entity.*`、两个 list 条件与 `forge.modifier.list.count` 从自己声明的端口或 `context.Query` 取值——`Targeting/ObservedEntityNodes` 是 kernel 侧助手，签名需要 `RuntimeKernel`，handler 拿不到它，其算法未被改动，仍由 R3 消费者测试直接验证。

**五个角色选择器**（`self`、`owner`、`source`、`instigator`、`event_target`）从求值上下文交付的事件角色取值（`EvaluationContext.Actors`，由框架的角色表从被派发的事件读出），本包不自己拼角色到端口的映射：`self` 是唯一不可缺席的角色，缺席按 `actor-missing` 拒绝该步；其余四个缺席时输出 null。

**纯计算**（`Pure/ScalarNodes.cs`、`TextNodes.cs`、`VectorNodes.cs`、`SeededNodes.cs`、`VariadicNodes.cs`）覆盖表里 11 行 `pure` 语义：常量、一张按 `operation` 选加减乘除、最值、绝对值或取整的计算卡（相减与相除从 A 开始依次作用，除数为 0 时按 `zero_policy` 报错、得 0 或跳过该除数，绝对值与取整只读 A）、clamp、区间映射（含指数曲线、翻转与越界策略）、文字模板、原版文本片段占位替换、两点距离与方向、一张按 `operation` 选全部满足/任一满足/取反（只读 A）的逻辑卡、随机数。选项值的等于/不等于比较由 `query` 表里的 `forge.condition.predicate.compare`（`value_type = enum`）承担，有序运算符对选项值一律拒绝。所有数值输入必须有限；NaN、正负无穷、除零、溢出、反向区间、非法种子、文字模板与长度越界和未知枚举分别拒绝，不以 0 或默认值兜底。

舍入与网站 `Math.round` 对齐：中点朝正无穷，所以 -1.5 → -1、2.5 → 3，**不用 .NET 默认的 ties-to-even**；实现不使用 `Floor(value + 0.5)`，避免大整数加 0.5 时被错误递增。输出的负零按 JSON 边界规范为正零。种子随机使用网站同一次 Mulberry32 采样并显式保留 modulo-2^32 位运算，相同种子与参数重复调用返回相同结果，其他调用不推进隐式随机状态。**种子派生（工程、epoch、实例、事件、用途）由未来的公共上下文提供，本包不自造 seed context 或全局种子。**

**集合**（`Pure/ReferenceCollections.cs`）覆盖 distinct、union/intersection/difference、limit、random（含权重）、count；它们已注册为 4 个 `forge.selector.target.*` 选择器行（`union` 按 `operation` 选并、交、差；`random` 接上 `weights` 时走权重抽样）与 `forge.modifier.list.count` 行，handler 直接调用这里的助手，集合运算保持有序与去重语义。按 id + worldEpoch + lifeEpoch 的完整身份去重，不按裸 ID 合并；输入在过滤和截取前完整校验。distinct 与集合运算按 JSON 身份字符串序稳定排序，limit 保留首次输入顺序。`ReferenceSelection` 分别记录 InputCount、AvailableCount、RequestedCount、SelectedCount 和 UnfilledCount——请求 5 个而只有 1 个候选时明确显示缺额 4，不假称足额完成。输入与完整输出上限 4096，显式 limit/random 的选择量 1…256；输入超限在去重前拒绝，合并输出超限不静默截断。选择器的 `empty` 策略由 `ApplyEmptyPolicy` 执行：emit-empty 原样、fail 拒绝。网站编辑器对这五行都不提供值预览，所以它们的跨语言向量仍是待办：`tests/Pure/CollectionTests.cs` 保留 C# 侧覆盖，`tools/collection-vectors.mjs` 只核对目录合同与编辑器的预览覆盖声明，并把网站与 C# 的数值端点差异记录在 `website-numeric-boundary.json`。

**目标与关系筛选**（`Targeting/ObservedRecipientFilter.cs`，行声明在 `Targeting/ObservedSpaceDeclarations.cs`）实现 `forge.selector.target.filter`：关系只相对该行自己的 `anchor` 输入测量（`RuntimeFactionRelations.Resolve(anchor, candidate)`），不从事件里再找一个角色，也不回退到 `source`/`owner`；世界没有声明的对一个关系保持 `unknown`，只在作者选了 `unknown` 时命中。候选先按 `include_self`、`damageable_doors`、`state`、`dimension` 过滤，再判关系：`state` 读候选自己的 `AiState`（`sleeping` 是 `hibernating`，读不到按 `recipient-state-unknown` 拒绝整行），`dimension` 从候选所在区域的 id 解析（解析不出按 `recipient-dimension` 拒绝），`damageable_doors=false` 按 `map-object.category=` 标签筛掉门与终端。答案按完整身份（worldEpoch + id + lifeEpoch）排序，候选集合由计划显式给出，本行不枚举世界也不截断。本行**没有**种类、生命状态、必需/排除标签或接收能力过滤，也没有 `maxTargets`：那些是网站 `targeting.ts` 动作接收者策略的一部分，由动作行在提交前自己核对，不在选择行里；本包内只有这一份关系筛选实现。

**空间**（`Targeting/ObservedSpatialNodes.cs`、`Targeting/ObservedSpaceNodes.cs`）实现 sphere/cylinder/capsule/box 点位置过滤、按 `direction` 取最近或最远的 nearest 与有限 chain，每次读取都走该步自己的 `RuntimeQuerySession.TrySnapshots`，完整性由 `RuntimeEntityQueryResult.RequireComplete` 判定。中心显式，候选完整性显式。四种形状都是世界轴对齐的闭判定——位置正好落在边界上算命中，没有额外容差。形状先分派、再读它自己的尺寸：sphere 只读 `radius`；cylinder 与 capsule 读 `radius` 与 `height`（capsule 的 `height` 是含两端半球的总长，直线段为 `height - 2·radius`，`height ≤ 2·radius` 时退化为球，与网站 `logic-evaluator.ts` 和 `AreaFieldDefinition.Contains` 同一公式）；box 只读计划自己的三个 `extents` 半尺寸，x/y/z 各自独立，不用 `radius` 或 `height` 代理其中任何一个轴。每个被用到的尺寸都在查询原生观察之前完成有限、正数、上界校验，形状不使用的量根本不会被读取。`angle` 是 cone 的输入而本包没有 cone 体积，因此从不读取；`query_shape` 集合里本包没有诚实语义的成员（`room`、`area`、`zone`、`cone`、`path`）一律以 `spatial-shape` 显式拒绝，不映射到相近体积。与网站目录的差距：shape_overlap 在网站上查询整个世界样本，C# 只能过滤调用方给出的完整候选；nearest 的 anchor 实体需先由调用方观察出位置。网站编辑器只对 `shape_overlap`、`nearest` 提供值预览，`chain` 没有：`tools/spatial-vectors.mjs` 按同一规则自己生成 chain 行、把期望值算在夹具里并记进 `unimplemented`（带原因），`tests/R3Consumers/SpatialTests.cs` 对这些行照常逐行比对。**不把 unknown 吞成 false**。不提供世界枚举、LOS、碰撞或导航证据。

**区域场**（`Targeting/AreaFieldNodes.cs`、`AreaFieldSession.cs`、`AreaFieldPump.cs`）实现 `forge.selector.target.area`：从计划给出的候选集合里保留落在指定 `area_field` 定义体积内的对象，场只声明形状与尺寸，中心由可选的 `center` 端口给出，形状判定与空间行同一套闭判定。`area_field` 这个资源种类由本包拥有：`ModuleDefinition.Create(fields, LevelSight.Visible)` 注册 `ResourceProviders`，定义由各包发布的运行时资源读出（`ForgeEngine.Framework.AreaFieldTable` 是唯一的读取器、语法与重复 id 规则），生命周期订阅在注册后首读，并按世界纪元重读，所以新世界不解析已撤下的文档。全引擎只有 `TriggerHost.Fields` 这一处读区域场文档，Player 与 Enemy 的区域动作也从它取定义；读不出的文档只写一条 `trigger.area-field-refused`。`empty` 策略用于场已量到而场内没有候选；`unloaded` 用于本世界没有该定义、或没有中心可读的请求——`fail` 以 `area-field-unloaded` 拒绝，另两个成员交出策略要求的空选择，绝不交出量了一半的集合。场没有自己的原生体，因此没有 `center` 端口的行只走 `unloaded`；体积的创建/移动/释放助手已下沉到 `GameBindings/GameAreaVolumes.cs`（+ `Framework/AreaVolumePlacement.cs` 的账本），由效果体积行使用。游戏内行为未验证，见 [evidence/area-field-row.json](../../evidence/area-field-row.json)。

**可变端口与权重抽样**（`Pure/VariadicNodes.cs`、`WeightedSampling.cs`）实现网站目录行（1.0.0）的 1–32 输入计算卡与逻辑卡（缺省 2 个输入）；集合并交集在 1.0.0 是二元合同，由 `ReferenceCollections` 实现。顺序遵循作者端口顺序，不按端口名排序也不重排浮点求和。权重抽样显式区分有放回与无放回，处理零权重、候选不足、重复身份拒绝、确定种子和有限预算；算法版本 `binary64-integer-mass-mulberry32-v1` 把有限非负 binary64 权重化为精确整数质量、用最大公约数约简，再用整数拒绝抽样，避免归一化浮点求和的溢出与微小正权重丢失。输入上限 4096、抽取上限 256、随机字上限 65536，每次抽取最多 64 轮拒绝尝试；预算耗尽不返回半份成功。

`forge.selector.target.random` 接上 `weights`（每个候选一个权重）时走的就是上面的权重抽样，`allow_repeats` 选有放回或无放回；它尚未在游戏内验证。

## 边界

这些方法只处理传入的引用，不能证明目标当前有效、查询完整、存在接收能力或获得权限。本包内没有第二个世界查询表、时钟或 Registry；所有实体状态通过公共 `InspectEntities` 获取，完整性只覆盖所请求的引用。任何筛选结果都是观察证据，不是权限或成本凭证——实际动作提交前必须由对应领域重新核对身份、关系、接收能力、权限与费用。

Trigger provider 随 ForgeEngine 发行，没有独立的插件入口、manifest、图标或发行条目。图执行与原生动作在 GTFO 主客机与恢复下的行为未验证。
