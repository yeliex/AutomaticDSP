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
        private void ExecuteDysonStructure(CommandState command, DateTimeOffset now)
        {
            if (GameMain.history == null || !GameMain.history.dysonSphereLayerPanelUnlocked)
            {
                finishCommand(command, CommandFailed, "tech_locked", "戴森球层编辑尚未解锁。", now, null);
                return;
            }
            if (!TryDysonId(command, "starId", out var starId) || !TryDysonId(command, "layerId", out var layerId) || layerId > 10)
            {
                finishCommand(command, CommandFailed, "invalid_command", "需要整数 starId、layerId（1–10）。", now, null);
                return;
            }
            var star = GameMain.galaxy?.StarById(starId);
            var sphere = star == null ? null : GameMain.data?.dysonSpheres[star.index];
            var layer = sphere?.layersIdBased[layerId];
            if (layer == null)
            {
                finishCommand(command, CommandFailed, "target_not_found", "戴森球层不存在。", now, null);
                return;
            }
            var nodeCommand = command.NormalizedType.EndsWith("node", StringComparison.Ordinal);
            var frameCommand = command.NormalizedType.EndsWith("frame", StringComparison.Ordinal);
            var create = command.NormalizedType.StartsWith("create", StringComparison.Ordinal);
            var componentId = 0;
            var idName = nodeCommand ? "nodeId" : frameCommand ? "frameId" : "shellId";
            var protoId = 0;
            if (create && TryGetToken(command, "protoId", out var protoToken) &&
                (protoToken.Type != JTokenType.Integer || !int.TryParse(protoToken.ToString(), out protoId) ||
                protoId < 0 || protoId >= (nodeCommand ? UIDysonEditor.nodeProtoCnt : frameCommand ? UIDysonEditor.frameProtoCnt : UIDysonEditor.shellProtoCnt)))
            {
                finishCommand(command, CommandFailed, "invalid_command", "protoId 超出原生样式范围。", now, null);
                return;
            }
            if (!create)
            {
                if (!TryDysonId(command, idName, out componentId))
                {
                    finishCommand(command, CommandFailed, "invalid_command", "需要正整数 " + idName + "。", now, null);
                    return;
                }
                var node = nodeCommand && componentId < layer.nodeCursor ? layer.nodePool[componentId] : null;
                var frame = frameCommand && componentId < layer.frameCursor ? layer.framePool[componentId] : null;
                var shell = !nodeCommand && !frameCommand && componentId < layer.shellCursor ? layer.shellPool[componentId] : null;
                if (node == null && frame == null && shell == null)
                {
                    finishCommand(command, CommandFailed, "target_not_found", "结构不存在。", now, null);
                    return;
                }
                // 删除关联壳面及框架的顺序与原生移除笔刷一致。
                if (node != null)
                {
                    foreach (var linked in node.frames.ToArray())
                    {
                        foreach (var face in linked.nodeA.shells.Where(s => s.frames.Contains(linked)).ToArray())
                            layer.RemoveDysonShell(face.id);
                        layer.RemoveDysonFrame(linked.id);
                    }
                    foreach (var face in node.shells.ToArray()) layer.RemoveDysonShell(face.id);
                    var built = node.sp == node.spMax;
                    layer.RemoveDysonNode(node.id);
                    if (built) GameMain.gameScenario.NotifyOnDeleteDysonNode();
                }
                else if (frame != null)
                {
                    foreach (var face in frame.nodeA.shells.Where(s => s.frames.Contains(frame)).ToArray())
                        layer.RemoveDysonShell(face.id);
                    var built = frame.spA + frame.spB > 20;
                    layer.RemoveDysonFrame(frame.id);
                    if (built) GameMain.gameScenario.NotifyOnDeleteDysonFrame();
                }
                else
                {
                    var built = shell.cellPoint > 20;
                    layer.RemoveDysonShell(shell.id);
                    if (built) GameMain.gameScenario.NotifyOnDeleteDysonShell();
                }
            }
            else if (nodeCommand)
            {
                if (!TryGetToken(command, "position", out var positionToken) || !(positionToken is JArray coordinates) ||
                    coordinates.Count != 3 || coordinates.Any(t => t.Type != JTokenType.Float && t.Type != JTokenType.Integer) ||
                    coordinates.Any(t => double.IsNaN(t.Value<double>()) || double.IsInfinity(t.Value<double>()) || Math.Abs(t.Value<double>()) > float.MaxValue))
                {
                    finishCommand(command, CommandFailed, "invalid_command", "position 必须为层局部坐标系的有限数值 [x,y,z]。", now, null);
                    return;
                }
                var position = new Vector3(coordinates[0].Value<float>(), coordinates[1].Value<float>(), coordinates[2].Value<float>());
                if (position.sqrMagnitude < 0.0001f || float.IsInfinity(position.sqrMagnitude))
                {
                    finishCommand(command, CommandFailed, "invalid_command", "position 方向无效。", now, null);
                    return;
                }
                var direction = position.normalized;
                var condition = DysonGeometryValidation.CheckNode(layer, direction);
                if (condition != "Ok")
                {
                    finishCommand(command, CommandFailed, "native_validation_failed", "原生节点校验拒绝。", now,
                        new JsonObject { ["nativeCondition"] = condition });
                    return;
                }
                componentId = layer.NewDysonNode(protoId, direction * layer.orbitRadius);
            }
            else if (frameCommand)
            {
                if (!TryDysonId(command, "nodeAId", out var aId) || !TryDysonId(command, "nodeBId", out var bId) ||
                    aId >= layer.nodeCursor || bId >= layer.nodeCursor || layer.nodePool[aId] == null || layer.nodePool[bId] == null ||
                    (TryGetToken(command, "euler", out var eulerToken) && eulerToken.Type != JTokenType.Boolean))
                {
                    finishCommand(command, CommandFailed, "invalid_command", "需要有效 nodeAId、nodeBId；euler 如提供必须为布尔值。", now, null);
                    return;
                }
                var euler = eulerToken != null && eulerToken.Value<bool>();
                var condition = DysonGeometryValidation.CheckFrame(layer, layer.nodePool[aId], layer.nodePool[bId], euler);
                if (condition != "Ok")
                {
                    finishCommand(command, CommandFailed, "native_validation_failed", "原生框架校验拒绝。", now,
                        new JsonObject { ["nativeCondition"] = condition });
                    return;
                }
                componentId = layer.NewDysonFrame(protoId, aId, bId, euler);
            }
            else
            {
                if (!TryGetToken(command, "nodeIds", out var nodesToken) || !(nodesToken is JArray nodeIds) || nodeIds.Count < 3 ||
                    nodeIds.Any(t => t.Type != JTokenType.Integer || !int.TryParse(t.ToString(), out var id) || id <= 0 ||
                        id >= layer.nodeCursor || layer.nodePool[id] == null))
                {
                    finishCommand(command, CommandFailed, "invalid_command", "nodeIds 必须为至少三个有效节点 ID 的有序闭环。", now, null);
                    return;
                }
                var ids = nodeIds.Select(t => t.Value<int>()).ToList();
                if (ids.Distinct().Count() != ids.Count)
                {
                    finishCommand(command, CommandFailed, "invalid_command", "nodeIds 不得包含重复节点。", now, null);
                    return;
                }
                var condition = DysonGeometryValidation.CheckShell(layer, ids.Select(id => layer.nodePool[id]).ToList());
                if (condition != "Ok")
                {
                    finishCommand(command, CommandFailed, "native_validation_failed", "壳面闭环校验拒绝。", now,
                        new JsonObject { ["nativeCondition"] = condition });
                    return;
                }
                componentId = layer.NewDysonShell(protoId, ids);
                if (componentId > 0) GameMain.gameScenario.NotifyOnPlanDysonShell();
            }
            finishCommand(command, componentId > 0 ? CommandSucceeded : CommandFailed,
                componentId > 0 ? null : "native_creation_failed", componentId > 0 ? null : "原生结构创建失败。", now,
                new JsonObject { ["starId"] = starId, ["layerId"] = layerId, [idName] = componentId,
                    ["exists"] = create && componentId > 0, ["completion"] = "design", ["nativeCondition"] = "Ok" });
        }

        private static bool TryDysonId(CommandState command, string name, out int value)
        {
            value = 0;
            return TryGetToken(command, name, out var token) && token.Type == JTokenType.Integer &&
                int.TryParse(token.ToString(), out value) && value > 0;
        }
    }
}
