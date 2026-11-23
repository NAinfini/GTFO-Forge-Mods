# 公开宿主生命周期合同

对应 SDK API 1.0.0 与未发布的 Runtime 1.0.0。这是宿主与模块之间的 API 合同，**不是新发行版，也不是原生玩法验证**。实际执行结果见 [VALIDATION.md](../VALIDATION.md)。

## 启动配置输入

`Plugin.ConfiguredMode` 是宿主程序集里冻结的 Off / Authoring / Play 选择，**不是 SDK 能力，也不是权威标志**。宿主现在独立于诊断绑定自己的计划路径、权限与模式设置。内核只在 `Plugin.Load` 成功之后发布；失败的启动不暴露任何公共内核入口。依赖插件的注册顺序不变。

## 依赖模块的启动

真实 BepInEx 插件在自己的 Load 里、在硬依赖的 Runtime 加载之后取得 `ForgeEngine.Plugin.Runtime`，类型为 `IForgeEngine?`。为 null 表示被禁用或不可用——**不要另建内核，也不要循环等待**。下文的启动、推进、世界切换和停止方法是宿主 internal 入口，模块通过接口读取状态并通过自己的句柄订阅生命周期。

在 Runtime 首个 FixedUpdate 之前注册真实的 `RuntimeModule` 并保留返回句柄。宿主调用一次 `StartRuntime`，在写出实际 manifest 或加载显式配置的离线计划之前关闭注册。状态流转是 Registering → Starting → Ready 或 Failed。`StartRuntime` 在第一次尝试之后返回 false；Generating、大厅切换或修好的文件都不会重试 Failed 的启动。Stop 对该内核实例是终态。

迟到的注册抛 `registration-closed`，不会部分注册一个模块。Ready 之后没有新的模块热加载窗口。启动前的注销与重新注册保持 generation 隔离。

## 观察

`handle.ObserveLifecycle(callback, replayCurrent: true)` 返回 `RuntimeLifecycleSubscription`。初始 Snapshot 是同步的；传 false 可以抑制这次重放。

`RuntimeLifecycleEvent` 包含 Kind、Current 快照、可选的 PreviousWorldEpoch 和 `IsMigration`。Kind 有 Snapshot、StartupChanged、WorldChanged、TickAdvanced、CheckpointRestored 五种。`IsMigration` 只在宿主迁移提交的那次 WorldChanged 上为真：同一关卡、同一批对象，只是世界期从 PreviousWorldEpoch 变成 Current.WorldEpoch；持有原生对象的 provider 此时只跟随世界期，不当作新世界清表。快照包含 StartupState、WorldEpoch、SimulationTick 和可空的 IsHost。**Tick 为 -1、权威为 null 表示该世界尚未推进**，不是伪造的生成期游戏测量值。

WorldChanged 在取消旧世界的排队工作、schedule 与 lease 之后发出。正常的世界切换不会卸载已加载的计划。TickAdvanced 在派发之后发出，且只在 tick 真正改变时发送。

回调运行在所属的模拟线程上。**从生命周期回调里修改内核、发布、调度、获取或释放状态、递归 Advance 都会被拒绝**；读取 Lifecycle、`ExportManifest` 或已缓存订阅门的 `HasSubscribers` 是允许的。领域自有的缓存可以清理，但原生副作用仍然需要它自己的合法阶段。

回调可以注销自己的订阅。模块注销会移除它拥有的订阅；`StopRuntime` 在清理之后通知，然后移除全部订阅。旧的订阅 ID 不能移除替换模块的观察器。观察器上限是每内核 256 个、每模块 32 个。回调抛异常时其他观察器继续，同一个观察器累计抛错 8 次才被移除（偶发的一次错误不让它整局失效），`LifecycleFaultCount` 与 `LastLifecycleFault` 保留有界证据；宿主只在错误计数变化时记录一次摘要，不是每帧记录。

## 提交与拆卸

`Plugin.CanExecuteGameplay` 必须在模拟线程上读取。它要求 Ready、当前处于 InLevel 阶段、当前机器是原版主机、无迁移、无挂起、无待处理的世界失效或停止。`SNet.MasterManagement.IsMigrating` 为真时内核保留到达事件但不派发；原版 recall 与新主机回调完成后才以新 epoch 重新开放。

**它是阶段门槛，不是权限授予、接收者检查，也不是原生提交会成功的承诺。** 每个动作都要重新校验当前 world 与 life、显式接收者、权限和成本。

派发过程中发生原生拆卸时，门槛立即关闭；桥接在派发器的安全点冲刷世界失效或 `StopRuntime`，不在 handler 内部重入 `BeginWorld` 或 `StopRuntime`。

`StopRuntime` 清除计划、排队事件、schedule 与 lease。Failed 或 Stopped 的内核拒绝新工作。终态 schedule 与 lease 的释放保持幂等，但仍然要求模拟线程，且不能从生命周期观察器里发起。

## 证据与限制

HostIntegration 测试编译后的 SDK 与实际宿主元数据。GameBindings 的 `--bridge <游戏目录> <runtime-fixtures>` 用游戏替身驱动生产桥接源码，并读取核对实际的 GameAssembly hash（不符或读不出时宿主挂起为 `startup-failed`，依赖包读 `Plugin.IsSuspended` 后不注册）；它**不调用原生游戏方法**。

回归覆盖：启动失败不重复 IO、LoadPlan 之前冻结、观察器故障隔离、错误线程访问、保留内核在停止后的行为、动作执行期间的拆卸、检查点恢复、迁移成功接管与 ForceExitToLobby 失败、客户端在原版完成回调前被提升、重复的 InLevel 通知。

本文件不宣称独立插件加载、配置迁移、发行打包、原生 detour 安全或多人实机通过。迁移路径有托管测试与原生成员元数据证据，仍需按 `evidence/host-migration.json` 的步骤做三人局验收。
