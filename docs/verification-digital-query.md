# 全息信标与备忘录查询验证

2026-09-25，在 `AutomaticDSP-redblue-opening-20260925` 实机验证。

- `digitalSystem` 与 `factory.digitalSystem` 的查询结果一致。
- `galacticDigital` 与 `data.galacticDigital` 的查询结果一致。
- 当前行星 103 的 `planetTodo` 为 id 1、ownerType `Astro`、isEmpty true；title、content 和 contentColorIndex 为 null，hasReminder 为 false。
- 全局 `todos.buffer` 按 `ownerType: "Astro"` 和 `ownerId: 103` 筛选，得到同一份备忘录；不存在的 ownerId 返回空数组。
- 本地信标数量为 0，全局有效信标池为空；没有为验证创建或修改存档内容。
- `_schema.roots` 包含两个新入口。构建 0 警告、0 错误，skill 格式检查通过。

非空信标内容、颜色索引和跨星球已有记录尚未实机覆盖；接口直接使用已有通用字段选择、序列化和分页机制，不生成派生规划数据。

本地验证脚本及原始结果位于 `.tools/opening-validation/verify-digital-query.mjs` 和 `digital-query-verification.json`。
