using System;
using AutomaticDSP.Serialization;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private void ExecuteDysonOrbit(CommandState command, DateTimeOffset now)
        {
            if (GameMain.data == null || GameMain.history == null || !GameMain.history.dysonSphereSystemUnlocked)
            {
                finishCommand(command, CommandFailed, "tech_locked", "戴森球系统尚未解锁或对局未就绪。", now, null);
                return;
            }
            if (!TryGetToken(command, "starId", out var starToken) || starToken.Type != JTokenType.Integer ||
                !int.TryParse(starToken.ToString(), out var starId) || GameMain.galaxy?.StarById(starId) == null)
            {
                finishCommand(command, CommandFailed, "invalid_command", "需要有效的整数 starId。", now, null);
                return;
            }
            var create = command.NormalizedType == "createdysonorbit";
            var edit = command.NormalizedType == "editdysonorbit";
            var orbitId = 0;
            if (!create && (!TryGetToken(command, "orbitId", out var idToken) || idToken.Type != JTokenType.Integer ||
                !int.TryParse(idToken.ToString(), out orbitId) || orbitId < 1 || orbitId > 20))
            {
                finishCommand(command, CommandFailed, "invalid_command", "orbitId 必须为 1 至 20 的整数。", now, null);
                return;
            }
            var radius = 0f;
            var inclination = 0f;
            var longitude = 0f;
            if ((create || edit) && (!TryDysonNumber(command, "radius", 1f, float.MaxValue, out radius) ||
                !TryDysonNumber(command, "inclination", 0f, 180f, out inclination) ||
                !TryDysonNumber(command, "longitude", 0f, 360f, out longitude)))
            {
                finishCommand(command, CommandFailed, "invalid_command", "需要有限数值 radius、inclination（0–180 度）、longitude（0–360 度）。", now, null);
                return;
            }
            var enabled = false;
            if (command.NormalizedType == "setdysonorbitenabled")
            {
                if (!TryGetToken(command, "enabled", out var enabledToken) || enabledToken.Type != JTokenType.Boolean)
                {
                    finishCommand(command, CommandFailed, "invalid_command", "enabled 必须为布尔值。", now, null);
                    return;
                }
                enabled = enabledToken.Value<bool>();
            }
            var sphere = GameMain.data.CreateDysonSphere(GameMain.galaxy.StarById(starId).index);
            var swarm = sphere.swarm;
            if (!create && !swarm.OrbitExist(orbitId))
            {
                finishCommand(command, CommandFailed, "target_not_found", "戴森云轨道不存在。", now, null);
                return;
            }
            if (create || edit)
            {
                var condition = sphere.CheckSwarmRadius(radius);
                if (condition != 0)
                {
                    finishCommand(command, CommandFailed, "invalid_orbit", "原生轨道半径校验拒绝。", now,
                        new JsonObject { ["nativeCondition"] = condition });
                    return;
                }
                var rotation = Quaternion.Euler(0f, -longitude, -inclination);
                if (create) orbitId = swarm.NewOrbit(radius, rotation);
                else swarm.EditOrbit(orbitId, radius, rotation);
                if (orbitId < 1)
                {
                    finishCommand(command, CommandFailed, "orbit_limit", "已达到原生 20 条轨道上限。", now, null);
                    return;
                }
            }
            else
            {
                // 原生轨道面板保护默认轨道；有太阳帆的轨道只能停用，不能直接清除。
                if (orbitId == 1 && (!enabled || command.NormalizedType == "removedysonorbit"))
                {
                    finishCommand(command, CommandFailed, "protected_orbit", "默认轨道不能停用或删除。", now, null);
                    return;
                }
                if (command.NormalizedType == "removedysonorbit")
                {
                    if (swarm.SailCountOnOrbit(orbitId) > 0)
                    {
                        finishCommand(command, CommandFailed, "orbit_not_empty", "轨道仍有太阳帆，请先停用并等待原生寿命结束。", now, null);
                        return;
                    }
                    swarm.RemoveOrbit(orbitId);
                }
                else swarm.SetOrbitEnable(orbitId, enabled);
            }
            var exists = swarm.OrbitExist(orbitId);
            finishCommand(command, CommandSucceeded, null, null, now, new JsonObject
            {
                ["starId"] = starId, ["orbitId"] = orbitId, ["exists"] = exists,
                ["enabled"] = exists && swarm.OrbitEnabled(orbitId),
                ["radius"] = exists ? (object)swarm.orbits[orbitId].radius : null,
                ["nativeCondition"] = 0
            });
        }

        private static bool TryDysonNumber(CommandState command, string name, float min, float max, out float value)
        {
            value = 0f;
            if (!TryGetToken(command, name, out var token) ||
                (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)) return false;
            var number = token.Value<double>();
            if (double.IsNaN(number) || double.IsInfinity(number) || number < min || number > max) return false;
            value = (float)number;
            return true;
        }

        private void ExecuteDysonLayer(CommandState command, DateTimeOffset now)
        {
            if (GameMain.data == null || GameMain.history == null || !GameMain.history.dysonSphereLayerPanelUnlocked)
            {
                finishCommand(command, CommandFailed, "tech_locked", "戴森球层编辑尚未解锁或对局未就绪。", now, null);
                return;
            }
            if (!TryGetToken(command, "starId", out var starToken) || starToken.Type != JTokenType.Integer ||
                !int.TryParse(starToken.ToString(), out var starId) || GameMain.galaxy?.StarById(starId) == null)
            {
                finishCommand(command, CommandFailed, "invalid_command", "需要有效的整数 starId。", now, null);
                return;
            }
            var create = command.NormalizedType == "createdysonlayer";
            var remove = command.NormalizedType == "removedysonlayer";
            var layerId = 0;
            if (!create && (!TryGetToken(command, "layerId", out var idToken) || idToken.Type != JTokenType.Integer ||
                !int.TryParse(idToken.ToString(), out layerId) || layerId < 1 || layerId > 10))
            {
                finishCommand(command, CommandFailed, "invalid_command", "layerId 必须为 1 至 10 的整数。", now, null);
                return;
            }
            var radius = 0f;
            var inclination = 0f;
            var longitude = 0f;
            if (!remove && (!TryDysonNumber(command, "inclination", 0f, 180f, out inclination) ||
                !TryDysonNumber(command, "longitude", 0f, 360f, out longitude) ||
                (create && !TryDysonNumber(command, "radius", 1f, float.MaxValue, out radius)) ||
                (!create && TryGetToken(command, "radius", out _))))
            {
                finishCommand(command, CommandFailed, "invalid_command", "需要有效倾角和升交点经度；仅创建层时允许并要求 radius，原生编辑不支持改半径。", now, null);
                return;
            }
            var sphere = GameMain.data.CreateDysonSphere(GameMain.galaxy.StarById(starId).index);
            var layer = create ? null : sphere.layersIdBased[layerId];
            if (!create && layer == null)
            {
                finishCommand(command, CommandFailed, "target_not_found", "戴森球层不存在。", now, null);
                return;
            }
            var rotation = Quaternion.Euler(0f, -longitude, -inclination);
            if (create)
            {
                var condition = sphere.CheckLayerRadius(radius);
                if (condition != 0 || sphere.QueryLayerId() == 0)
                {
                    finishCommand(command, CommandFailed, condition != 0 ? "invalid_orbit" : "layer_limit", "原生层半径或层数校验拒绝。", now,
                        new JsonObject { ["nativeCondition"] = condition });
                    return;
                }
                // 与原生新建层一致，角速度由恒星和半径计算，不接受调用方指定。
                if (!sphere.QueryLayerRadius(ref radius, out var speed))
                {
                    finishCommand(command, CommandFailed, "invalid_orbit", "原生层轨道计算失败。", now, null);
                    return;
                }
                layer = sphere.AddLayer(radius, rotation, speed);
                layerId = layer.id;
            }
            else if (remove)
            {
                // 原生先拆结构再删除层，保留已建结构转太阳帆等副作用。
                layer.RemoveAllStructure();
                sphere.RemoveLayer(layer);
            }
            else
            {
                layer.targetOrbitRotation = rotation;
                layer.InitOrbitRotation(layer.orbitRotation, rotation);
            }
            finishCommand(command, CommandSucceeded, null, null, now, new JsonObject
            {
                ["starId"] = starId, ["layerId"] = layerId, ["exists"] = !remove,
                ["radius"] = remove ? null : (object)layer.orbitRadius,
                ["nativeCondition"] = 0,
                ["completion"] = "design"
            });
        }
    }
}
