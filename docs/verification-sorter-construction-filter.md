# 2026-09-25 分拣器建造筛选验证

新增 `placeSorter.filterItemId`，按原生 `BuildTool_Inserter.CreatePrebuilds` 的 `BuildPreview.filterId → PrebuildData.filterId` 路径保存。省略或 0 保持无筛选，负数及不存在的物品拒绝。

- `dotnet build src/AutomaticDSP/AutomaticDSP.csproj --no-restore -v minimal`：0 错误、0 警告。
- 游戏内负数筛选返回 `invalid_command`，未生成预建。
- 炼油厂 177 槽 2 → 精炼油带 386：实体 387 落成后 `filter=1114`，观测到 `itemId=1114`，单格分拣器 `stt=200000`；储液罐精炼油计数由 293 增到 302。
- 第二座炼油厂 377 的精炼油筛选分拣器 391、氢筛选分拣器 409 均建成并正常输出，两座炼油厂处于加工状态。
- 无筛选的原油、煤矿输入分拣器仍可正常建成并搬运。

原始证据：`.tools/opening-validation/construction-filter-verification.json`、同目录任务与产量采样 JSON。
