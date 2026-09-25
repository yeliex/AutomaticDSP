using System;
using AutomaticDSP.Serialization;
using AutomaticDSP.State;
using UnityEngine;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private void ExecutePlaceBuildingLocked(CommandState command, DateTimeOffset now)
        {
            if (command.BuildObjectId != 0)
            {
                WaitForBuiltObjectLocked(command, now);
                return;
            }

            if (!TryGetPlayer(out var player, out var errorCode, out var errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            if (!TryValidateCurrentPlanet(command, player, out errorCode, out errorMessage))
            {
                finishCommand(command, CommandFailed, errorCode, errorMessage, now, null);
                return;
            }

            var factory = GameMain.localPlanet?.factory;
            if (factory == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Current planet factory is not loaded.", now, null);
                return;
            }

            if (!TryGetInt(command, "itemId", out var itemId) || itemId <= 0)
            {
                finishCommand(command, CommandFailed, "invalid_command", "placeBuilding requires a positive itemId.", now, null);
                return;
            }

            var stackOnEntityId = GetInt(command, "stackOnEntityId", 0);
            var position = Vector3.zero;
            if (stackOnEntityId == 0 && !TryGetVector(command, "position", out position, out errorMessage))
            {
                finishCommand(command, CommandFailed, "invalid_command", errorMessage, now, null);
                return;
            }

            var item = LDB.items.Select(itemId);
            if (item == null || !item.IsEntity || !item.CanBuild)
            {
                finishCommand(command, CommandFailed, "invalid_command", $"Item is not buildable: {itemId}", now, null);
                return;
            }

            var desc = item.prefabDesc;
            if (desc.isBelt || desc.isInserter)
            {
                finishCommand(command, CommandFailed, "invalid_command", "Use placeBelt or placeSorter for belts and sorters.", now, null);
                return;
            }

            if (GameMain.history == null || !GameMain.history.ItemUnlocked(itemId))
            {
                finishCommand(command, CommandFailed, "tech_locked", $"Item is locked: {itemId}", now, null);
                return;
            }

            var actionBuild = player.controller?.actionBuild;
            if (actionBuild == null || GameMain.data == null)
            {
                finishCommand(command, CommandFailed, "game_not_ready", "Build tool is not ready.", now, null);
                return;
            }

            var yaw = (float)GetDouble(command, "rotation", 0);
            if (desc.addonType == EAddonType.Belt)
            {
                ExecutePlaceBeltAddonLocked(command, player, factory, item, position, yaw, stackOnEntityId, now);
                return;
            }
            var veinMiningBuilding = IsVeinMiningBuilding(desc);
            var snappedPosition = stackOnEntityId != 0 ? Vector3.zero : (veinMiningBuilding
                ? position.normalized * (factory.planet.realRadius + 0.2f)
                : factory.planet.aux.Snap(position, onTerrain: true));
            var rotation = stackOnEntityId != 0 ? Quaternion.identity : Maths.SphericalRotation(snappedPosition, yaw);
            if (stackOnEntityId != 0)
            {
                if (!desc.multiLevel || stackOnEntityId < 1 || stackOnEntityId >= factory.entityCursor ||
                    factory.entityPool[stackOnEntityId].id != stackOnEntityId)
                {
                    finishCommand(command, CommandFailed, "invalid_command", "Stacking requires a stackable item and an existing base entity.", now, null);
                    return;
                }

                var baseEntity = factory.entityPool[stackOnEntityId];
                var baseDesc = LDB.items.Select(baseEntity.protoId)?.prefabDesc;
                var alternativeIndex = desc.multiLevelAlternativeIds == null ? -1 : Array.IndexOf(desc.multiLevelAlternativeIds, (int)baseEntity.protoId);
                if (baseDesc == null || (baseEntity.protoId != itemId && alternativeIndex < 0))
                {
                    finishCommand(command, CommandFailed, "invalid_command", "The item cannot stack on this base entity.", now, null);
                    return;
                }

                if (!TryEnsureObjectSlotAvailable(factory, stackOnEntityId, 15, out errorMessage))
                {
                    finishCommand(command, CommandFailed, "slot_occupied", errorMessage, now, null);
                    return;
                }

                // 与原生叠放预览一致：由下层搭接点决定位置，层数仍交给原生校验。
                snappedPosition = baseEntity.pos + baseEntity.rot * baseDesc.lapJoint;
                rotation = desc.multiLevelAllowRotate ? Maths.SphericalRotation(snappedPosition, yaw) : baseEntity.rot;
                if (!desc.multiLevelAllowRotate && baseEntity.protoId != itemId && alternativeIndex >= 0 &&
                    desc.multiLevelAlternativeYawTransposes[alternativeIndex])
                    rotation *= Quaternion.Euler(0f, 90f, 0f);
            }
            if (!IsWithinCommandIssueRange(player, snappedPosition, out var commandDistance, out var commandRange))
            {
                finishCommand(
                    command,
                    CommandFailed,
                    "out_of_range",
                    "Build target is outside command issue range. Move closer before submitting placeBuilding.",
                    now,
                    RangeFailureResult(commandDistance, commandRange, snappedPosition));
                return;
            }

            var tool = new AutomationClickBuildTool();
            try
            {
                tool._Init(GameMain.data);
                actionBuild.SetFactoryReferences();
                tool.SetFactoryReferences();
                tool.PrepareInventorySnapshot();

                if (IsMiningBuilding(item))
                {
                    var requestedVeinId = GetInt(command, "veinId", 0);
                    if (!TryResolveMiningBuildVein(
                        factory,
                        snappedPosition,
                        requestedVeinId,
                        desc.oilMiner,
                        out var veinId,
                        out var veinPosition,
                        out errorMessage))
                    {
                        finishCommand(command, CommandFailed, "invalid_command", errorMessage, now, null);
                        return;
                    }

                    tool.SetCastVein(veinId, veinPosition);
                }

                player.controller.cmd.type = ECommand.Build;
                command.EnteredBuildMode = true;
                player.controller.cmd.mode = item.BuildMode;
                player.controller.cmd.refId = item.ID;
                player.controller.cmd.target = snappedPosition;
                player.controller.cmd.test = snappedPosition;
                tool.handItem = item;
                tool.handPrefabDesc = desc;
                tool.yaw = yaw;
                tool.buildPreviews.Clear();

                var preview = new BuildPreview();
                preview.ResetAll();
                preview.item = item;
                preview.desc = desc;
                preview.lpos = snappedPosition;
                preview.lpos2 = snappedPosition;
                preview.lrot = rotation;
                preview.lrot2 = rotation;
                preview.recipeId = GetInt(command, "recipeId", 0);
                preview.filterId = GetInt(command, "filterId", 0);
                preview.needModel = desc.lodCount > 0 && desc.lodMeshes[0] != null;
                var colliderArea = desc.buildCollider.ext.magnitude + 4f;
                preview.genNearColliderArea2 = colliderArea * colliderArea;
                preview.condition = EBuildCondition.Ok;
                if (stackOnEntityId > 0)
                {
                    tool.multiLevelCovering = true;
                    tool.castObjectId = stackOnEntityId;
                    preview.inputObjId = stackOnEntityId;
                    preview.inputFromSlot = 15;
                    preview.inputToSlot = 14;
                }
                tool.buildPreviews.Add(preview);
                // 矿机同样需要附近建筑的碰撞体，否则原生校验会漏掉已有建筑。
                ActivateBuildColliders(factory, preview);

                command.Phase = "validating";
                var canBuild = tool.CheckBuildConditions();
                if (!canBuild || preview.condition != EBuildCondition.Ok)
                {
                    if (preview.condition == EBuildCondition.OutOfReach &&
                        TryApproachBuildTarget(player, snappedPosition))
                    {
                        command.Phase = "approaching";
                        return;
                    }

                    finishCommand(
                        command,
                        CommandFailed,
                        BuildConditionCode(preview.condition),
                        preview.conditionText,
                        now,
                        new JsonObject { ["condition"] = preview.condition.ToString() });
                    return;
                }

                command.Phase = "creatingPrebuild";
                tool.CreatePrebuilds();
                if (preview.objId == 0)
                {
                    finishCommand(command, CommandFailed, "build_failed", "Build tool did not create a prebuild.", now, null);
                    return;
                }

                command.BuildObjectId = preview.objId;
                command.BuildPreview = preview;
                command.BuildItemId = itemId;
                command.BuildPosition = snappedPosition;
            }
            finally
            {
                tool._Free();
            }

            WaitForBuiltObjectLocked(command, now);
        }

        private static bool IsMiningBuilding(ItemProto item)
        {
            return item != null &&
                item.prefabDesc != null &&
                (IsVeinMiningBuilding(item.prefabDesc) || item.prefabDesc.oilMiner);
        }

        private static bool IsVeinMiningBuilding(PrefabDesc desc)
        {
            return desc != null &&
                (ReflectionReader.GetBool(desc, false, "veinMiner", "isVeinMiner") ||
                    desc.minerType == EMinerType.Vein);
        }

        private static bool TryResolveMiningBuildVein(
            PlanetFactory factory,
            Vector3 buildPosition,
            int requestedVeinId,
            bool oilMiner,
            out int veinId,
            out Vector3 veinPosition,
            out string errorMessage)
        {
            veinId = 0;
            veinPosition = Vector3.zero;
            errorMessage = null;
            if (factory?.veinPool == null)
            {
                errorMessage = "Current planet vein pool is not loaded.";
                return false;
            }

            if (requestedVeinId > 0)
            {
                if (!TryGetMiningBuildVein(factory, requestedVeinId, oilMiner, out var vein))
                {
                    errorMessage = $"Vein not found or not suitable for a mining building: {requestedVeinId}";
                    return false;
                }

                veinId = requestedVeinId;
                veinPosition = vein.pos;
                return true;
            }

            var bestDistance = float.MaxValue;
            for (var i = 1; i < factory.veinCursor; i++)
            {
                if (!TryGetMiningBuildVein(factory, i, oilMiner, out var vein))
                {
                    continue;
                }

                var distance = (vein.pos - buildPosition).magnitude;
                if (distance > 8.5f || distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                veinId = i;
                veinPosition = vein.pos;
            }

            if (veinId > 0)
            {
                return true;
            }

            errorMessage = oilMiner ? "Oil extractor requires veinId or a nearby oil seep." : "Mining building requires veinId or a nearby non-oil vein.";
            return false;
        }

        private static bool TryGetMiningBuildVein(PlanetFactory factory, int veinId, bool oilMiner, out VeinData vein)
        {
            vein = default;
            if (veinId <= 0 ||
                veinId >= factory.veinCursor ||
                factory.veinPool == null ||
                veinId >= factory.veinPool.Length ||
                factory.veinPool[veinId].id != veinId)
            {
                return false;
            }

            vein = factory.veinPool[veinId];
            return vein.amount > 0 && (vein.type == EVeinType.Oil) == oilMiner && vein.productId > 0;
        }

        private static void ActivateBuildColliders(PlanetFactory factory, BuildPreview preview)
        {
            var physics = factory?.planet?.physics;
            if (physics == null || preview == null)
            {
                return;
            }

            var areaRadius = Mathf.Sqrt(Mathf.Max(0f, preview.genNearColliderArea2));
            physics.nearColliderLogic?.ActiveCollidersInArea(preview.lpos, areaRadius);
        }
    }
}
