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

            if (!TryGetVector(command, "position", out var position, out errorMessage))
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
            var veinMiningBuilding = IsVeinMiningBuilding(desc);
            var snappedPosition = veinMiningBuilding
                ? position.normalized * (factory.planet.realRadius + 0.2f)
                : factory.planet.aux.Snap(position, onTerrain: true);
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

            var rotation = Maths.SphericalRotation(snappedPosition, yaw);
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
                tool.buildPreviews.Add(preview);
                if (!veinMiningBuilding)
                {
                    ActivateBuildColliders(factory, preview);
                }

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
                (ReflectionReader.GetBool(item.prefabDesc, false, "isMiner", "isVeinMiner") ||
                    item.prefabDesc.minerType != EMinerType.None);
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
                if (!TryGetMiningBuildVein(factory, requestedVeinId, out var vein))
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
                if (!TryGetMiningBuildVein(factory, i, out var vein))
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

            errorMessage = "Mining building requires veinId or a nearby non-oil vein.";
            return false;
        }

        private static bool TryGetMiningBuildVein(PlanetFactory factory, int veinId, out VeinData vein)
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
            return vein.amount > 0 && vein.type != EVeinType.Oil && vein.productId > 0;
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
