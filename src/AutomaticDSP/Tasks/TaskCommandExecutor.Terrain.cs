using System;
using System.Collections.Generic;
using AutomaticDSP.Serialization;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private bool TryGetTerrainPlayer(CommandState command, DateTimeOffset now, out Player player)
        {
            if (!TryGetPlayer(out player, out var code, out var message) ||
                !TryValidateCurrentPlanet(command, player, out code, out message))
            {
                finishCommand(command, CommandFailed, code, message, now, null);
                return false;
            }
            if (!player.isAlive || player.sailing || player.factory == null || !GameMain.localPlanet.factoryLoaded ||
                GameMain.localPlanet.type == EPlanetType.Gas || player.controller?.actionBuild == null || GameMain.data.guideRunning)
            {
                finishCommand(command, CommandFailed, "invalid_player_state", "需要存活的机甲位于已加载的固态行星上，且不在序幕或航行中。", now, null);
                return false;
            }
            return true;
        }

        private void ExecuteReformTerrain(CommandState command, DateTimeOffset now)
        {
            if (!TryGetTerrainPlayer(command, now, out var player)) return;
            var mode = GetString(command, "mode", "flatten");
            foreach (var name in new[] { "brushSize", "brushType", "brushColor" })
                if (TryGetToken(command, name, out var token) && (token.Type != JTokenType.Integer ||
                    token.Value<double>() < (name == "brushColor" ? 0 : 1) ||
                    token.Value<double>() > (name == "brushSize" ? 10 : name == "brushType" ? 7 : 31)))
                {
                    finishCommand(command, CommandFailed, "invalid_command", name + " 必须是原生范围内的整数。", now, null);
                    return;
                }
            if (TryGetToken(command, "buryVeins", out var buryToken) && buryToken.Type != JTokenType.Boolean)
            {
                finishCommand(command, CommandFailed, "invalid_command", "buryVeins 必须是布尔值。", now, null);
                return;
            }
            var size = GetInt(command, "brushSize", 1);
            var type = GetInt(command, "brushType", 1);
            var color = GetInt(command, "brushColor", 0);
            if ((mode != "flatten" && mode != "restore") || size < 1 || size > 10 || type < 1 || type > 7 || color < 0 || color > 31 ||
                !TryGetVector(command, "position", out var position, out _) ||
                position.sqrMagnitude < 0.001f || float.IsNaN(position.sqrMagnitude) || float.IsInfinity(position.sqrMagnitude))
            {
                finishCommand(command, CommandFailed, "invalid_command", "需要有限非零 position、flatten/restore 模式、1–10 笔刷、1–7 类型和 0–31 颜色。", now, null);
                return;
            }
            // 科技限制原本位于 UI 入口，直接调用执行方法时必须保留这项检查。
            if (!GameMain.history.ItemUnlocked(1131))
            {
                finishCommand(command, CommandFailed, "tech_locked", "尚未解锁地基。", now, null);
                return;
            }
            if (player.inhandItemId != 0 && player.inhandItemId != 1131)
            {
                finishCommand(command, CommandFailed, "hand_item_conflict", "请先收起手持的非地基物品。", now, null);
                return;
            }
            if (!AutomationReformTool.Available)
            {
                finishCommand(command, CommandFailed, "native_api_unavailable", "当前游戏缺少所需的原生地形改造方法。", now, null);
                return;
            }

            var factory = player.factory;
            // 使用独立工具，避免覆盖玩家当前 UI 的笔刷设置。
            var tool = new AutomationReformTool();
            var selectedFoundation = false;
            try
            {
                tool._Init(GameMain.data);
                player.controller.actionBuild.SetFactoryReferences();
                tool.SetFactoryReferences();
                tool.handItem = LDB.items.Select(1131);
                tool.reformMode = mode == "flatten" ? 0 : 1;
                tool.brushSize = size;
                tool.brushType = type;
                tool.brushColor = color;
                tool.buryVeins = GetBool(command, "buryVeins", false);
                // 原生吸附与区域裁剪决定中心和实际网格数，距离以吸附中心为准，费用不能用 size² 代替。
                tool.Prepare(position);
                var distance = Vector3.Distance(player.position, tool.reformCenterPoint);
                if (distance > player.mecha.buildArea)
                {
                    finishCommand(command, CommandFailed, "out_of_range", "请先移动到地形笔刷中心的建造范围内。", now,
                        RangeFailureResult(distance, player.mecha.buildArea, tool.reformCenterPoint));
                    return;
                }

                // 原生准备会裁剪基地禁改区；保留其网格与额外基地坑费用，不重新生成点列。
                var needSand = tool.CalculateSand();
                if (mode == "flatten" && GameMain.sandboxToolsEnabled && GameMain.history.HasFeatureKey(1100001)) needSand = 0;
                var foundationBefore = player.package.GetItemCount(1131) + player.inhandItemCount;
                var sandBefore = player.sandCount;
                if (mode == "flatten" && foundationBefore < tool.cursorPointCount)
                {
                    finishCommand(command, CommandFailed, "missing_item", "地基不足。", now,
                        new JsonObject { ["required"] = tool.cursorPointCount, ["available"] = foundationBefore });
                    return;
                }
                if (sandBefore < needSand)
                {
                    finishCommand(command, CommandFailed, "insufficient_sand", "沙土不足。", now,
                        new JsonObject { ["required"] = needSand, ["available"] = sandBefore });
                    return;
                }

                var platform = factory.platformSystem;
                var before = (byte[])platform.reformData.Clone();
                var heightBefore = factory.planet.data.QueryModifiedHeight(tool.reformCenterPoint);
                // 还原向手持地基返还；使用原生空手选择，不创建物品，也不挪动已有地基。
                if (mode == "restore" && player.inhandItemId == 0)
                {
                    selectedFoundation = true;
                    player.SetHandItems(1131, 0);
                }
                player.ClearOrders();
                player.controller.actionMine.miningType = EObjectType.None;
                player.controller.actionMine.miningId = 0;
                player.controller.actionMine.miningTick = 0;
                GameMain.gameScenario?.NotifyOnDoReformOpt(1);
                try { tool.Apply(needSand); }
                finally { GameMain.gameScenario?.NotifyOnDoReformOpt(2); }

                var changed = new List<object>();
                var restoredCount = 0;
                for (var i = 0; i < platform.maxReformCount; i++)
                    if (before[i] != platform.reformData[i])
                    {
                        changed.Add(new JsonObject { ["index"] = i, ["before"] = before[i], ["after"] = platform.reformData[i] });
                        // 类型 7 是无装饰的已改造地基，只有类型 0 才表示还原。
                        if ((before[i] >> 5) > 0 && platform.GetReformType(i) == 0) restoredCount++;
                    }
                var foundationDelta = player.package.GetItemCount(1131) + player.inhandItemCount - foundationBefore;
                var valid = player.sandCount == sandBefore - needSand &&
                    foundationDelta == (mode == "flatten" ? -tool.cursorPointCount : restoredCount);
                if (mode == "flatten")
                    for (var i = 0; i < size * size; i++)
                        if (tool.cursorIndices[i] >= 0)
                            valid &= platform.GetReformType(tool.cursorIndices[i]) == type && platform.GetReformColor(tool.cursorIndices[i]) == color;
                finishCommand(command, valid ? CommandSucceeded : CommandFailed, valid ? null : "reform_failed",
                    valid ? null : "原生执行后的地基、沙土或网格状态不符合预期。", now,
                    new JsonObject
                    {
                        ["mode"] = mode, ["position"] = Vector(tool.reformCenterPoint), ["brushSize"] = size,
                        ["foundationDelta"] = foundationDelta, ["sandDelta"] = player.sandCount - sandBefore,
                        ["heightBefore"] = heightBefore, ["heightAfter"] = factory.planet.data.QueryModifiedHeight(tool.reformCenterPoint),
                        ["changedCells"] = changed, ["nativeAreaMode"] = tool.disableOrExtra
                    });
            }
            finally
            {
                try
                {
                    // 手持地基会在下一帧重新触发原生建造模式；只收起本次临时选择，保留玩家原有手持与指令。
                    // 原生收起会将返还地基放入背包，满包时按游戏规则掉落，不能直接清空物品字段。
                    if (selectedFoundation && player.inhandItemId == 1131) player.SetHandItems(0, 0);
                }
                finally { tool._Free(); }
            }
        }
    }
}
