using System;
using AutomaticDSP.Serialization;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private void ExecuteProductionSetting(TaskState task, CommandState command, DateTimeOffset now)
        {
            if (!TryGetPlayer(out var player, out var errorCode, out var errorMessage) ||
                !TryValidateCurrentPlanet(command, player, out errorCode, out errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }
            var factory = GameMain.localPlanet?.factory;
            if (factory == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "当前行星工厂尚未加载。", now, null);
                return;
            }
            if (!TryGetTargetEntityId(task, command, out var entityId, out errorMessage))
            {
                finishCommand(command, CommandFailed, "invalid_command", errorMessage, now, null);
                return;
            }
            if (!TryGetEntity(factory, entityId, out var entity))
            {
                finishCommand(command, CommandFailed, "target_not_found", "目标实体不存在。", now, null);
                return;
            }
            var distance = Vector3.Distance(player.position, entity.pos);
            if (distance > player.mecha.buildArea)
            {
                finishCommand(command, CommandFailed, "out_of_range", "请先移动到建筑范围内再设置。", now,
                    RangeFailureResult(distance, player.mecha.buildArea, entity.pos));
                return;
            }
            switch (command.NormalizedType)
            {
                case "setrayreceivermode":
                    ExecuteRayReceiverMode(command, player, factory, entity, now);
                    return;
                case "setejectororbit":
                    ExecuteEjectorOrbit(command, factory, entity, now);
                    return;
                default:
                    ExecuteProliferatorMode(command, player, factory, entity, now);
                    return;
            }
        }

        private void ExecuteRayReceiverMode(CommandState command, Player player, PlanetFactory factory, EntityData entity, DateTimeOffset now)
        {
            if (entity.powerGenId <= 0 || factory.powerSystem?.genPool == null ||
                entity.powerGenId >= factory.powerSystem.genPool.Length ||
                factory.powerSystem.genPool[entity.powerGenId].id != entity.powerGenId ||
                !factory.powerSystem.genPool[entity.powerGenId].gamma)
            {
                finishCommand(command, CommandFailed, "invalid_target", "目标不是射线接收站。", now, null);
                return;
            }
            if (!TryGetToken(command, "mode", out var token) || token.Type != JTokenType.String ||
                (token.Value<string>() != "power" && token.Value<string>() != "photon"))
            {
                finishCommand(command, CommandFailed, "invalid_command", "mode 必须为 power 或 photon。", now, null);
                return;
            }
            var productId = token.Value<string>() == "photon" ? LDB.items.Select(entity.protoId).prefabDesc.powerProductId : 0;
            if (productId != 0 && (LDB.items.Select(productId) == null || GameMain.history == null || !GameMain.history.ItemUnlocked(productId)))
            {
                finishCommand(command, CommandFailed, "item_locked", "光子产物尚未解锁。", now, null);
                return;
            }
            ref var generator = ref factory.powerSystem.genPool[entity.powerGenId];
            if (generator.productId != productId)
            {
                // 与原生发电模式按钮一致：只返还整数产物，背包溢出由原生垃圾机制处理。
                if (productId == 0)
                {
                    var count = (int)generator.productCount;
                    if (generator.productId != 0 && count > 0)
                        player.TryAddItemToPackage(generator.productId, count, 0, throwTrash: true);
                    generator.productCount = 0f;
                }
                generator.productId = productId;
            }
            finishCommand(command, CommandSucceeded, null, null, now,
                new JsonObject { ["entityId"] = entity.id, ["mode"] = generator.productId == 0 ? "power" : "photon", ["productId"] = generator.productId });
        }

        private void ExecuteEjectorOrbit(CommandState command, PlanetFactory factory, EntityData entity, DateTimeOffset now)
        {
            if (entity.ejectorId <= 0 || factory.factorySystem?.ejectorPool == null ||
                entity.ejectorId >= factory.factorySystem.ejectorPool.Length ||
                factory.factorySystem.ejectorPool[entity.ejectorId].id != entity.ejectorId)
            {
                finishCommand(command, CommandFailed, "invalid_target", "目标不是电磁轨道弹射器。", now, null);
                return;
            }
            if (!TryGetToken(command, "orbitId", out var orbitToken) || orbitToken.Type != JTokenType.Integer ||
                !int.TryParse(orbitToken.ToString(), out var orbitId) || orbitId < 0 ||
                (TryGetToken(command, "autoOrbit", out var autoToken) && autoToken.Type != JTokenType.Boolean))
            {
                finishCommand(command, CommandFailed, "invalid_command", "需要非负 orbitId，autoOrbit 如提供必须为布尔值。", now, null);
                return;
            }
            var swarm = factory.dysonSphere?.swarm;
            if (swarm == null || (orbitId > 0 && (orbitId >= swarm.orbitCursor ||
                swarm.orbits[orbitId].id != orbitId || !swarm.orbits[orbitId].enabled)))
            {
                finishCommand(command, CommandFailed, "invalid_orbit", "轨道不存在或未启用。", now, null);
                return;
            }
            ref var ejector = ref factory.factorySystem.ejectorPool[entity.ejectorId];
            ejector.SetOrbit(orbitId);
            if (autoToken != null) ejector.autoOrbit = autoToken.Value<bool>();
            finishCommand(command, CommandSucceeded, null, null, now,
                new JsonObject { ["entityId"] = entity.id, ["orbitId"] = ejector.orbitId, ["autoOrbit"] = ejector.autoOrbit });
        }

        private void ExecuteProliferatorMode(CommandState command, Player player, PlanetFactory factory, EntityData entity, DateTimeOffset now)
        {
            if (!TryGetToken(command, "mode", out var token) || token.Type != JTokenType.String ||
                (token.Value<string>() != "extra" && token.Value<string>() != "speed"))
            {
                finishCommand(command, CommandFailed, "invalid_command", "mode 必须为 extra 或 speed。", now, null);
                return;
            }
            // 原生窗口在增产剂科技解锁前隐藏模式按钮，不能只复刻按钮回调。
            if (GameMain.history == null || !GameMain.history.TechUnlocked(1151))
            {
                finishCommand(command, CommandFailed, "tech_locked", "尚未解锁增产剂科技。", now, null);
                return;
            }
            var forceAccMode = token.Value<string>() == "speed";
            var system = factory.factorySystem;
            if (entity.assemblerId > 0 && system?.assemblerPool != null && entity.assemblerId < system.assemblerPool.Length &&
                system.assemblerPool[entity.assemblerId].id == entity.assemblerId)
            {
                ref var assembler = ref system.assemblerPool[entity.assemblerId];
                if (assembler.recipeExecuteData == null || !assembler.recipeExecuteData.productive)
                {
                    finishCommand(command, CommandFailed, "invalid_recipe", "当前配方不支持切换增产模式。", now, null);
                    return;
                }
                assembler.forceAccMode = forceAccMode;
            }
            else if (entity.labId > 0 && system?.labPool != null && entity.labId < system.labPool.Length &&
                system.labPool[entity.labId].id == entity.labId)
            {
                if (!system.labPool[entity.labId].matrixMode)
                {
                    finishCommand(command, CommandFailed, "invalid_recipe", "研究站必须处于矩阵生产模式。", now, null);
                    return;
                }
                system.labPool[entity.labId].forceAccMode = forceAccMode;
                // 原生研究站按钮会同步整组堆叠研究站。
                system.SyncLabForceAccMode(player, entity.labId);
            }
            else
            {
                finishCommand(command, CommandFailed, "invalid_target", "目标不支持增产模式设置。", now, null);
                return;
            }
            finishCommand(command, CommandSucceeded, null, null, now,
                new JsonObject { ["entityId"] = entity.id, ["mode"] = forceAccMode ? "speed" : "extra", ["forceAccMode"] = forceAccMode });
        }
    }
}
