# Map 生成与资源核验

产品目标、三步交付、完整原版范围、接口和验收要求见
[唯一开发计划](../../../Docs/forge-contract/FORGE-FRAMEWORK.md) §3 / §4 / §6 / §7。
本文件提供生成相关代码与检查工具入口。

## 生成与对象入口

- 网站的 `Web/src/forge/map/map-native-authoring.ts`、`Web/src/forge/map/map-game.ts`：官方层、布局与 DataBlock 数据。
- Map 的身份与原生对象接线：[ForgeEngine 开发者说明的 Map 一节](../DEVELOPERS.md#map)。
- `tools/generation-api-targets.json`：需要核对的游戏生成 API。
- `tools/Capture-NativeApi.ps1`、`tools/verify_native_api.py`：采集与核验指定游戏构建的 API 元数据。
- `Generation/`、`Content/GenerationPackage.cs` 与 `Native/Generation/`：Forge 生成计划、包准入与原生生成执行。

对象位置依据实际生成规则、放置点或实例身份。碰撞、导航和合法出生空间分别核验；API 签名检查与游戏行为验证分别记录。

## 资源描述检查

资源引用、来源、授权和空间描述要求见唯一计划 §3.2，第三方选型见 §8。

真实资源测试使用对应包的来源、版本和 hash。格式测试与真实包加载各自保留输入和结果。

## 游戏核验

按计划对应步骤验证生成阶段、对象创建、层与区域、门、控制器与钥匙引用、空间合法性和主客机一致性。新房间另测连接、NavMesh、AIGraph、遮挡和灯光。

记录游戏构建、seed、依赖锁、真实操作与输出。执行命令和原始结果放在 [ForgeEngine 的 VALIDATION.md](../VALIDATION.md) 或相应证据目录。
