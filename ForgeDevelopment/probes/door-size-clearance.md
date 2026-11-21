# 原版门运行态净空与通行

本配方属于合同 §4.5 Y1 / U-DEV-MOD。入口为 `commands/door-size-clearance.forge`；这里只规定取证步骤，不宣称已经取得游戏结论。

## 输入、输出与身份

逐种采 4×4、8×4、8×8；尺寸标签只作待查分组。记录官方关卡、种子、游戏 revision/build、依赖与 DLL SHA-256。每组分别锁定原生 prefab 完整名、源文件/hash、geomorph、门框和门体在该资源中的子树、房间世界 TRS、门序号、门型与两侧区域。门框与 collider 必须沿同一 prefab 实例的完整父链回指；不能用名称相似或最近对象配对。8×8 没有真实样本时保留该行“证据不足”，不能把 8×4 放大替代。

按合同 §3.5，原始录像、测量和结论保存在本地 `artifacts/mods/ForgeDevelopment/door-size-clearance-<运行编号>/`，不写 Docs，不把缺样写成尺寸结论。`conclusions.json` 使用以下字段：`schemaVersion=1`、`gameBuild`、`gameRevision`、`dependencies`、`inputHashes`、`runId`、`samples[]`。每个样本含 `nominalSize`、`nativeAsset`、`framePath`、`doorPath`、`colliders[]`、`phase`、`clearWidthMetres`、`clearHeightMetres`、`measurementMethod`、`uncertaintyMetres`、`playerStanding`、`playerCrouched`、`enemyPassage[]`、`evidence[]`。结论统一为 `pass/fail/insufficient-evidence` 加理由、日志 seq/录像时间段；数值未知填 null，不能填 0。此格式为实验结果，不是新的产品合同。

## 执行

1. 两端安装同一配方并冷启动隔离 Authoring 会话；开启 Recorder/Trace，关闭 everything/TraceAllProfiles。F5 → reload files，确认无 running 命令和实验对象，再执行 `door-size-enable-observation`。
2. 正常进入官方关卡，先从原生来源定位唯一目标，再瞄准运行 `door-size-selected-alignment`；执行 gate/weak/security 摘要核两侧区域。选择的对齐点是测量坐标系，GetBoundingSize 仅作来源旁证。
3. 用现有场景检视工具导出该实例完整树（包括 inactive），逐层记录 sibling index、父链、组件、TRS、renderer 和 collider 的共同根。逐 collider 记录 Box/Capsule/Sphere 局部形状或 MeshCollider 实际网格、convex、enabled、isTrigger、layer；不把活动全场摘要当完整清单。
4. 关门静止、开门动画结束、正常关门结束及可破坏门正常破坏后分别 F6 书签，运行状态/碰撞摘要并导出完整树。安全门没有的关闭/破坏阶段明确不适用；不调用强制开门、导航重建或写状态。
5. 在同一门局部坐标下，依据实际非 trigger 碰撞表面测量通行截面：沿通行深度和高度扫描，记录约束净宽、净高的两侧/上下碰撞表面、局部采样点、世界换算、测量分辨率和误差。旋转、缩放及门体残留均纳入。复杂洞口同时保存截面轮廓；不能把最大宽和最大高合成一个实际不存在的矩形。AABB、mesh bounds、门框外尺寸或 enum 不能填净空值；仅用有限射线时报告采样范围，不能给全洞口的无条件结论。
6. 同一玩家正常站立、蹲伏从 A→B/B→A 走过，至少三次。每次运行 `door-size-crossing-state` 前后读回并录连续录像，核没有绕路、传送或穿模。再由已存在且已点名 EnemyDataID 的真实敌人自然追击从两侧穿门；记录其姿态、AI 状态、两侧节点及卡住位置，不拿敌人 bounds 或导航路径代替实走。
7. 主机和客机独立采相同原生资产、局部路径、位姿与场次；Pointer/InstanceID/UID 只用于同端关联。迟加入、检查点恢复、重进、主机迁移分别记录。结束关闭专项观察，保存全部分段与 patches；本配方不创建或销毁门。

## 逐行判据

| 项目 | 通过 | 失败 | 证据不足 |
| --- | --- | --- | --- |
| 资产对应 | 门框、门体、碰撞沿完整父链归于同一已锁定原生资源 | 完整证据显示混用资源/实例 | 路径歧义、只按名字或活动枚举匹配 |
| 运行净宽/净高 | 该阶段真实碰撞截面可复算，单位与误差完整 | 测量与原始截面不符、遗漏有效阻挡 | 只有 bounds、缺 collider、测量范围不完整 |
| 玩家通行 | 指定姿态双向正常穿过且录像/读回一致 | 已完整观察到阻挡、卡住或穿模 | 未跑一侧、缺连续录像或身份丢失 |
| 敌人通行 | 指定种类真实 AI 双向完成且路线穿目标洞口 | 完整刺激下反复卡门/穿模 | 敌人无追击目标、未执行、改走他路 |

净空测量、玩家、各敌人种类及每个网络/恢复场景各自判定。导航、剔除、灯光没有被本表顺带认证。丢行、截断、异常、null-before-leaf 或 native-read-skipped 不记通过；命令绿色只表示采样命令完成。
