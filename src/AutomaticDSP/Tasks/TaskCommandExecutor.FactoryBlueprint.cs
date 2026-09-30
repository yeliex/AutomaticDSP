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
            if (command.BuildTargets != null)
            {
                var waitingFactory = GameMain.localPlanet?.factory;
                if (waitingFactory != null)
                {
                    // 分拣器等待两端预建落成后，使用实际实体 ID 核对连接。
                    foreach (var target in command.BuildTargets.Where(t => !t.Preview.desc.isInserter && t.EntityId == 0))
                        if (TryFindBuiltEntity(waitingFactory, target.ItemId, target.Position, out var id, target.Preview)) target.EntityId = id;
                    foreach (var target in command.BuildTargets.Where(t => t.Preview.desc.isInserter))
                    {
                        var input = command.BuildTargets.Find(t => t.Preview == target.Preview.input);
                        var output = command.BuildTargets.Find(t => t.Preview == target.Preview.output);
                        if (input?.EntityId > 0) target.Preview.inputObjId = input.EntityId;
                        if (output?.EntityId > 0) target.Preview.outputObjId = output.EntityId;
                    }
                }
                WaitForBuiltObjectsLocked(command, now);
                return;
            }
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
            if (!code.StartsWith("BLUEPRINT:", StringComparison.Ordinal) || !CheckBlueprintCompression(code, out errorMessage))
            {
                finishCommand(command, CommandFailed, "invalid_blueprint", "需要有效的原生工厂蓝图压缩数据。", now, null);
                return;
            }
            var blueprint = new BlueprintData();
            var parse = blueprint.FromBase64String(code);
            if (parse != BlueprintDataIOError.OK || !blueprint.isValid || blueprint.buildings == null || blueprint.buildings.Length == 0)
            {
                finishCommand(command, CommandFailed, "invalid_blueprint", "原生工厂蓝图解析失败或没有建筑。", now,
                    new JsonObject { ["nativeCondition"] = parse.ToString() });
                return;
            }
            // 初版只应用建筑布局，不静默丢弃蓝图中的地基数据。
            if (blueprint.reformData != null && blueprint.reformData.reformCount > 0)
            {
                finishCommand(command, CommandFailed, "unsupported_blueprint_reform", "当前接口不应用含地基的工厂蓝图。", now, null);
                return;
            }
            var factory = GameMain.localPlanet?.factory;
            var actionBuild = player.controller?.actionBuild;
            if (factory == null || actionBuild == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "当前行星建造系统未就绪。", now, null);
                return;
            }
            if (GameMain.history.blueprintLimit < blueprint.buildings.Length ||
                blueprint.buildings.Any(b => !GameMain.history.ItemUnlocked(b.itemId) ||
                    (b.recipeId > 0 && !GameMain.history.RecipeUnlocked(b.recipeId))))
            {
                finishCommand(command, CommandFailed, "tech_locked", "蓝图规模、建筑或配方尚未解锁。", now, null);
                return;
            }
            var snapped = factory.planet.aux.Snap(position, onTerrain: true);
            if (Vector3.Distance(player.position, snapped) > player.mecha.buildArea)
            {
                finishCommand(command, CommandFailed, "out_of_range", "请先移动到蓝图落点的建造范围内。", now, null);
                return;
            }
            var tool = new BuildTool_BlueprintPaste();
            try
            {
                tool._Init(GameMain.data);
                actionBuild.SetFactoryReferences();
                tool.SetFactoryReferences();
                tool.blueprint = blueprint;
                // 独立工具不打开玩家面板，补齐原生 OnOpen 初始化的预览配色状态。
                tool.highlightItems = new Dictionary<int, uint>();
                tool.useReforms = false;
                tool.autoBuryBase = false;
                tool.yaw = rotation;
                tool.anchorType = 0;
                tool.cursorValid = true;
                tool.castGroundPosSnapped = snapped;
                tool.dotsCursor = 1;
                tool.dotsSnapped[0] = BlueprintUtils.RecalculateCursorPos(snapped, rotation, blueprint, 0, tool.segment);
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
                if (previews.Length == 0 || previews.Any(p => Vector3.Distance(player.position, p.lpos) > player.mecha.buildArea))
                {
                    finishCommand(command, CommandFailed, "out_of_range", "蓝图内全部建筑必须位于当前建造范围内。", now, null);
                    return;
                }
                tool.ActiveColliders(actionBuild.model);
                bool valid;
                try { valid = tool.CheckBuildConditions(); }
                finally { tool.DeactiveColliders(actionBuild.model); }
                var failures = previews.Where(p => p.condition != EBuildCondition.Ok && p.condition != EBuildCondition.NotEnoughItem)
                    .Select(p => new JsonObject { ["itemId"] = p.item.ID, ["condition"] = p.condition.ToString(), ["position"] = Vector(p.lpos) }).ToArray();
                if (!valid || failures.Length > 0)
                {
                    finishCommand(command, CommandFailed, "native_validation_failed", "原生蓝图建造校验拒绝。", now,
                        new JsonObject { ["conditions"] = failures });
                    return;
                }
                if (previews.Any(p => p.coverObjId != 0))
                {
                    finishCommand(command, CommandFailed, "unsupported_blueprint_overlap", "当前接口只支持空地粘贴，不覆盖现有建筑。", now, null);
                    return;
                }
                var missing = previews.GroupBy(p => p.item.ID).Where(g =>
                    player.package.GetItemCount(g.Key) + (player.inhandItemId == g.Key ? player.inhandItemCount : 0) < g.Count())
                    .Select(g => new JsonObject { ["itemId"] = g.Key, ["required"] = g.Count() }).ToArray();
                if (missing.Length > 0)
                {
                    finishCommand(command, CommandFailed, "not_enough_items", "背包与手持建筑不足。", now, new JsonObject { ["items"] = missing });
                    return;
                }
                player.controller.cmd.type = ECommand.Build;
                command.EnteredBuildMode = true;
                tool.CreatePrebuilds();
                if (previews.Any(p => p.objId == 0))
                {
                    finishCommand(command, CommandFailed, "build_failed", "原生蓝图未创建全部预建；检查现场，已创建部分不会撤销。", now,
                        new JsonObject { ["objectIds"] = previews.Select(p => p.objId).ToArray(), ["stateMayHaveChanged"] = true });
                    return;
                }
                command.BuildTargets = new List<BuildWaitTarget>();
                foreach (var preview in previews)
                {
                    // 原生工具释放时清空预览，保留独立副本供落成与连接匹配使用。
                    var retained = new BuildPreview();
                    retained.Clone(preview);
                    command.BuildTargets.Add(new BuildWaitTarget(preview.objId, preview.item.ID, preview.lpos, retained));
                }
                for (var i = 0; i < previews.Length; i++)
                {
                    var retained = command.BuildTargets[i].Preview;
                    var inputIndex = Array.IndexOf(previews, previews[i].input);
                    var outputIndex = Array.IndexOf(previews, previews[i].output);
                    retained.input = inputIndex < 0 ? null : command.BuildTargets[inputIndex].Preview;
                    retained.output = outputIndex < 0 ? null : command.BuildTargets[outputIndex].Preview;
                }
            }
            catch (Exception ex)
            {
                // 原生预览依赖游戏版本与场景；异常必须终止本次命令，不能逐帧重复下达。
                finishCommand(command, CommandFailed, "native_blueprint_error", ex.Message, now,
                    new JsonObject { ["nativeException"] = ex.GetType().Name, ["stateMayHaveChanged"] = command.EnteredBuildMode });
                return;
            }
            finally { tool._Free(); }
            WaitForBuiltObjectsLocked(command, now);
        }
    }
}
