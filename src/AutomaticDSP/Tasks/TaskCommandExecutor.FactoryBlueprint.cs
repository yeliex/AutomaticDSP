using System;
using System.Collections.Generic;
using System.Linq;
using AutomaticDSP.Serialization;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private void ExecuteFactoryBlueprint(CommandState command, DateTimeOffset now)
        {
            if (!TryGetPlayer(out var player, out var errorCode, out var errorMessage) ||
                !TryValidateCurrentPlanet(command, player, out errorCode, out errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }
            if (!TryGetToken(command, "blueprint", out var codeToken) || codeToken.Type != JTokenType.String ||
                string.IsNullOrWhiteSpace(codeToken.Value<string>()) || codeToken.Value<string>().Length > 16 * 1024 * 1024 ||
                !TryGetVector(command, "position", out var position, out errorMessage) ||
                !TryDysonNumber(command, "rotation", 0f, 270f, out var rotation) || rotation % 90f != 0f ||
                float.IsNaN(position.sqrMagnitude) || float.IsInfinity(position.sqrMagnitude) || position.sqrMagnitude < 1f)
            {
                finishCommand(command, CommandFailed, "invalid_command", "需要工厂 blueprint、有效 position 与 rotation（0/90/180/270 度）。", now, null);
                return;
            }
            var code = codeToken.Value<string>().Trim();
            foreach (var name in new[] { "allowPartial", "useReforms", "usePalette", "autoReform", "buryVeins" })
                if (TryGetToken(command, name, out var option) && option.Type != JTokenType.Boolean)
                {
                    finishCommand(command, CommandFailed, "invalid_command", name + " 必须为布尔值。", now, null);
                    return;
                }
            if (TryGetToken(command, "anchorType", out var anchorToken) &&
                (anchorToken.Type != JTokenType.Integer || anchorToken.Value<double>() < 0 || anchorToken.Value<double>() > 4))
            {
                finishCommand(command, CommandFailed, "invalid_command", "anchorType 必须是 0–4 的整数。", now, null);
                return;
            }
            var anchor = GetInt(command, "anchorType", 0);
            var allowPartial = GetBool(command, "allowPartial", false);
            if (!code.StartsWith("BLUEPRINT:", StringComparison.Ordinal) || !CheckBlueprintCompression(code, out errorMessage))
            {
                finishCommand(command, CommandFailed, "invalid_blueprint", "需要有效的原生工厂蓝图压缩数据。", now, null);
                return;
            }
            var blueprint = new BlueprintData();
            var parse = blueprint.FromBase64String(code);
            if (parse != BlueprintDataIOError.OK || !blueprint.isValid || BlueprintData.IsNullOrEmpty(blueprint))
            {
                finishCommand(command, CommandFailed, "invalid_blueprint", "原生工厂蓝图解析失败或没有可粘贴的建筑与地基。", now,
                    new JsonObject { ["nativeCondition"] = parse.ToString() });
                return;
            }
            if (blueprint.areas == null || blueprint.areas.Length == 0 ||
                (blueprint.areas.Length > 1 && (anchor > 2 || rotation % 180f != 0f)) ||
                (blueprint.areas.Length == 1 && (blueprint.areas[0].width == 1 || blueprint.areas[0].height == 1) && anchor != 0 && anchor != 2 && anchor != 3))
            {
                finishCommand(command, CommandFailed, "invalid_command", "锚点或旋转不符合该蓝图的原生区域限制。", now, null);
                return;
            }
            var factory = GameMain.localPlanet?.factory;
            var actionBuild = player.controller?.actionBuild;
            if (factory == null || actionBuild == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "当前行星建造系统未就绪。", now, null);
                return;
            }
            if (GameMain.history.blueprintLimit < blueprint.buildings.Length)
            {
                finishCommand(command, CommandFailed, "tech_locked", "蓝图规模超过已解锁上限。", now, null);
                return;
            }
            var snapped = factory.planet.aux.Snap(position, onTerrain: true);
            if (Vector3.Distance(player.position, snapped) > player.mecha.buildArea)
            {
                finishCommand(command, CommandFailed, "out_of_range", "请先移动到蓝图落点的建造范围内。", now, null);
                return;
            }
            if (blueprint.areas.Length > 1 &&
                (blueprint.areas.Length % 2 != 1 || blueprint.areas.Length / 2 != blueprint.primaryAreaIdx))
            {
                var expectedRotation = (BlueprintUtils.GetLatitudeRad(snapped.normalized) > 0f) !=
                    (blueprint.primaryAreaIdx < blueprint.areas.Length / 2) ? 180f : 0f;
                if (rotation != expectedRotation)
                {
                    finishCommand(command, CommandFailed, "invalid_command", "该跨区域蓝图的原生朝向由落点半球决定。", now,
                        new JsonObject { ["requiredRotation"] = expectedRotation });
                    return;
                }
            }
            if (!AutomationBlueprintTool.Available)
            {
                finishCommand(command, CommandFailed, "native_api_unavailable", "原生蓝图地基方法不可用。", now, null);
                return;
            }
            var tool = new AutomationBlueprintTool();
            var stateMayHaveChanged = false;
            try
            {
                tool._Init(GameMain.data);
                // 原生碰撞激活要求建造系统已打开，否则远处已有对象不会参与覆盖和碰撞校验。
                player.controller.cmd.type = ECommand.Build;
                command.EnteredBuildMode = true;
                actionBuild.Open();
                actionBuild.SetFactoryReferences();
                tool.SetFactoryReferences();
                tool.blueprint = blueprint;
                // 独立工具不打开玩家面板，显式初始化原生每帧库存快照和预览状态。
                tool.PrepareInventory();
                tool.useReforms = GetBool(command, "useReforms", true);
                tool.usePalette = GetBool(command, "usePalette", false);
                tool.skipAutoReform = !GetBool(command, "autoReform", false);
                tool.autoBuryBase = false;
                tool.yaw = rotation;
                tool.anchorType = anchor;
                tool.cursorValid = true;
                tool.castGroundPosSnapped = snapped;
                tool.dotsCursor = 1;
                tool.dotsSnapped[0] = BlueprintUtils.RecalculateCursorPos(snapped, rotation, blueprint, anchor, tool.segment);
                BlueprintUtils.SnapTropic(actionBuild, blueprint, tool.dotsSnapped, 1, rotation, tool.segment);
                tool.GenerateBlueprintGratBoxes();
                if (!tool.CheckBuildConditionsPrestage())
                {
                    finishCommand(command, CommandFailed, "native_validation_failed", "原生蓝图区域校验拒绝。", now,
                        new JsonObject { ["nativeConditions"] = tool._tmp_error_types?.Select(c => c.ToString()).ToArray() });
                    return;
                }
                tool.DeterminePreviewsPrestage(_forceRefreshBP: true);
                var previews = tool.bpPool.Take(tool.bpCursor).Where(p => p != null && p.bpgpuiModelId > 0).ToArray();
                if (previews.Length == 0 && (!tool.useReforms || blueprint.reformData.reformCount == 0))
                {
                    finishCommand(command, CommandFailed, "invalid_blueprint", "原生蓝图没有生成建筑预览。", now, null);
                    return;
                }
                tool.ActiveColliders(actionBuild.model);
                bool valid;
                try { valid = tool.CheckBuildConditions(); }
                finally { tool.DeactiveColliders(actionBuild.model); }
                tool.CalculateReforms();
                var reformCount = tool.estReformCount;
                var foundationBefore = player.package.GetItemCount(1131);
                var sandBefore = player.sandCount;
                if ((tool.result & (EBlueprintPasteResult.HasReform | EBlueprintPasteResult.BuildingNeedReform)) != 0)
                {
                    // 地基先执行，再按更新后的地形重新校验建筑；失败也必须反馈已发生的地形变化。
                    var reformTool = actionBuild.reformTool;
                    var previousBury = reformTool.buryVeins;
                    try
                    {
                        reformTool.buryVeins = GetBool(command, "buryVeins", previousBury);
                        stateMayHaveChanged = true;
                        if (!tool.ApplyReforms())
                        {
                            finishCommand(command, CommandFailed, "native_reform_failed", "原生地基粘贴拒绝，请核对地基和沙土。", now,
                                new JsonObject { ["requiredFoundations"] = reformCount, ["estimatedSand"] = tool.estNeedSandCount,
                                    ["stateMayHaveChanged"] = true });
                            return;
                        }
                    }
                    finally { reformTool.buryVeins = previousBury; }
                    tool.ClearErrorMessage(force: true);
                    tool.DeterminePreviewsPrestage(_forceRefreshBP: false, retry: true);
                    previews = tool.bpPool.Take(tool.bpCursor).Where(p => p != null && p.bpgpuiModelId > 0).ToArray();
                    tool.ActiveColliders(actionBuild.model);
                    try { valid = tool.CheckBuildConditions(); }
                    finally { tool.DeactiveColliders(actionBuild.model); }
                }
                var failures = new List<JsonObject>();
                for (var i = 0; i < previews.Length; i++)
                {
                    var preview = previews[i];
                    if (preview.condition != EBuildCondition.Ok && preview.condition != EBuildCondition.NotEnoughItem)
                        failures.Add(new JsonObject { ["index"] = i, ["itemId"] = preview.item.ID,
                            ["condition"] = preview.condition.ToString(), ["position"] = Vector(preview.lpos) });
                }
                if (!allowPartial && (!valid || failures.Count > 0))
                {
                    finishCommand(command, CommandFailed, "native_validation_failed", "原生蓝图建造校验拒绝。", now,
                        new JsonObject { ["conditions"] = failures, ["stateMayHaveChanged"] = stateMayHaveChanged });
                    return;
                }
                // 原生允许缺料预建；施工半径和后续补料由原生无人机系统处理。
                command.BuildPlanetId = factory.planet.id;
                stateMayHaveChanged = true;
                tool.CreatePrebuilds();
                // 原生升级缺料时仍返回覆盖对象 ID，必须核对型号，不能当作已下达的升级。
                var skipped = GetBlueprintUpgradeFailures(factory, previews);
                var upgradeFailed = skipped.Count > 0;
                var failedUpgradeObjects = new HashSet<int>(skipped.Select(s => (int)s["objectId"]));
                var placed = previews.Where(p => p.objId != 0 && !failedUpgradeObjects.Contains(p.objId)).ToArray();
                for (var i = 0; i < previews.Length; i++)
                {
                    var preview = previews[i];
                    if (preview.objId == 0)
                        skipped.Add(new JsonObject { ["index"] = i, ["itemId"] = preview.item.ID,
                            ["condition"] = preview.condition.ToString(), ["position"] = Vector(preview.lpos) });
                }
                var result = new JsonObject
                {
                    ["planetId"] = factory.planet.id, ["buildingCount"] = previews.Length,
                    ["placedCount"] = placed.Length, ["skipped"] = skipped, ["partial"] = skipped.Count > 0,
                    ["reusedCount"] = placed.Count(p => p.coverObjId != 0),
                    ["upgradedCount"] = placed.Count(p => p.coverObjId != 0 && p.willRemoveCover),
                    ["foundationDelta"] = player.package.GetItemCount(1131) - foundationBefore,
                    ["sandDelta"] = player.sandCount - sandBefore, ["stateMayHaveChanged"] = true
                };
                command.Result = result;
                if ((!allowPartial && skipped.Count > 0) || (placed.Length == 0 && previews.Length > 0))
                {
                    finishCommand(command, CommandFailed, upgradeFailed ? "upgrade_failed" : "build_failed",
                        "原生蓝图未完成全部下达或覆盖升级；检查 skipped，已发生的改动不会撤销。", now,
                        result);
                    return;
                }
                if (placed.Length == 0)
                {
                    result["submitted"] = true;
                    result["objectIds"] = new int[0];
                    result["prebuildIds"] = new int[0];
                    result["entityIds"] = new int[0];
                    finishCommand(command, CommandSucceeded, null, null, now, result);
                    return;
                }
                command.BuildTargets = new List<BuildWaitTarget>();
                var retainedIndices = new Dictionary<BuildPreview, int>();
                foreach (var preview in placed)
                {
                    // 原生工具释放时清空预览，保留独立副本供落成与连接匹配使用。
                    retainedIndices.Add(preview, command.BuildTargets.Count);
                    command.BuildTargets.Add(new BuildWaitTarget(preview.objId, preview.item.ID, preview.lpos, preview));
                }
                for (var i = 0; i < placed.Length; i++)
                {
                    var retained = command.BuildTargets[i].Preview;
                    var inputIndex = placed[i].input != null && retainedIndices.TryGetValue(placed[i].input, out var input) ? input : -1;
                    var outputIndex = placed[i].output != null && retainedIndices.TryGetValue(placed[i].output, out var output) ? output : -1;
                    retained.input = inputIndex < 0 ? null : command.BuildTargets[inputIndex].Preview;
                    retained.output = outputIndex < 0 ? null : command.BuildTargets[outputIndex].Preview;
                }
            }
            catch (Exception ex)
            {
                // 原生预览依赖游戏版本与场景；异常必须终止本次命令，不能逐帧重复下达。
                finishCommand(command, CommandFailed, "native_blueprint_error", ex.Message, now,
                    new JsonObject { ["nativeException"] = ex.GetType().Name, ["stateMayHaveChanged"] = stateMayHaveChanged });
                return;
            }
            finally
            {
                try { tool.RegisterItemConsumption(); }
                finally { tool._Free(); }
            }
            CompleteBuildSubmission(command, now);
        }

        private static List<JsonObject> GetBlueprintUpgradeFailures(PlanetFactory factory, BuildPreview[] previews)
        {
            var failures = new List<JsonObject>();
            for (var i = 0; i < previews.Length; i++)
            {
                var preview = previews[i];
                if (preview.coverObjId == 0 || !preview.willRemoveCover || preview.objId == 0) continue;
                var actualItemId = preview.objId > 0 ? factory.entityPool[preview.objId].protoId : factory.prebuildPool[-preview.objId].protoId;
                if (actualItemId != preview.item.ID)
                    failures.Add(new JsonObject { ["index"] = i, ["objectId"] = preview.objId,
                        ["itemId"] = preview.item.ID, ["actualItemId"] = actualItemId,
                        ["condition"] = "UpgradeNotApplied", ["position"] = Vector(preview.lpos) });
            }
            return failures;
        }
    }
}
