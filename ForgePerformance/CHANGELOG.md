# Changelog

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
- `CombatDecals` 去掉 `Decal.Shown` 钩子：游戏只在回池时隐藏贴花，回池入口已处理，这个钩子每次回池都白扫一遍全部登记贴花。

- 修复 `LowQualitySpitter`：建关时已生成的喷吐体在进关后补扫处理，原来只处理进关后才出现的。
- 修复 `CorpseShadows`：复用的敌人重新出生时恢复被关掉的投影，不再等到退图。
- `LeakCleanup` 的实例化渲染缓冲只在退图拆除时销毁，关内销毁的特征（含敌人身上的）不碰共享缓冲。
- `CombatDecals` 只对确认的战斗贴花检查层级，结果按特效对象缓存到退图；`DebrisFreeze` 的随机延迟改用本地随机数，不推进 Unity 全局随机序列。
- 退图不再把被 `RagdollCap` 停掉的布娃娃重新启用（离开关卡本就销毁它们，一次启用大量布娃娃会有物理尖峰）；退图异常只记日志，不再从状态事件里抛出，不打断其他订阅者。
