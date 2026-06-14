using System;
using System.Collections.Generic;
using AutomaticDSP.Serialization;
using AutomaticDSP.State;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private const float MinimumCommandIssueRange = 30f;
        private const float MaximumCommandIssueRange = 80f;
        private const float CommandIssueRangeBuildAreaFactor = 1.5f;

        private static bool TryGetPlayer(out Player player, out string errorCode, out string errorMessage)
        {
            player = GameMain.mainPlayer;
            if (player == null || GameMain.localPlanet == null)
            {
                errorCode = "game_not_ready";
                errorMessage = "Game must be running with player on a local planet.";
                return false;
            }

            errorCode = null;
            errorMessage = null;
            return true;
        }

        private static bool TryValidateCurrentPlanet(CommandState command, Player player, out string errorCode, out string errorMessage)
        {
            errorCode = null;
            errorMessage = null;
            if (!TryGetToken(command, "planetId", out var token))
            {
                return true;
            }

            if (token.Type == JTokenType.String &&
                string.Equals(token.Value<string>(), "current", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (token.Type == JTokenType.Integer && token.Value<int>() == player.planetId)
            {
                return true;
            }

            errorCode = "out_of_range";
            errorMessage = "Only current-planet commands are supported.";
            return false;
        }

        private static bool TryPrepareMineTarget(
            CommandState command,
            PlanetFactory factory,
            string targetType,
            int targetId,
            out string errorCode,
            out string errorMessage)
        {
            errorCode = null;
            errorMessage = null;
            switch (TaskCommandType.Normalize(targetType))
            {
                case "vein":
                    if (targetId >= factory.veinCursor ||
                        factory.veinPool == null ||
                        targetId >= factory.veinPool.Length ||
                        factory.veinPool[targetId].id != targetId)
                    {
                        errorCode = "target_not_found";
                        errorMessage = $"Vein not found: {targetId}";
                        return false;
                    }

                    var vein = factory.veinPool[targetId];
                    if (vein.type == EVeinType.Oil)
                    {
                        errorCode = "invalid_command";
                        errorMessage = "Direct oil mining is not supported.";
                        return false;
                    }

                    command.TargetId = targetId;
                    command.MineObjectType = EObjectType.Vein;
                    command.ObjectPosition = vein.pos;
                    command.TargetPosition = MiningStandPosition(vein.pos);
                    command.TargetItemId = GetInt(command, "itemId", vein.productId);
                    command.MiningProductItemIds = command.TargetItemId > 0
                        ? new List<int> { command.TargetItemId }
                        : new List<int>();
                    return true;
                case "vege":
                    if (targetId >= factory.vegeCursor ||
                        factory.vegePool == null ||
                        targetId >= factory.vegePool.Length ||
                        factory.vegePool[targetId].id != targetId)
                    {
                        errorCode = "target_not_found";
                        errorMessage = $"Vegetable not found: {targetId}";
                        return false;
                    }

                    var vege = factory.vegePool[targetId];
                    command.TargetId = targetId;
                    command.MineObjectType = EObjectType.Vegetable;
                    command.ObjectPosition = vege.pos;
                    command.TargetPosition = MiningStandPosition(vege.pos);
                    var vegeItems = VegeMiningItemIds(vege.protoId);
                    command.TargetItemId = GetInt(command, "itemId", vegeItems.Count == 0 ? 0 : vegeItems[0]);
                    if (command.TargetItemId > 0 && !vegeItems.Contains(command.TargetItemId))
                    {
                        vegeItems.Insert(0, command.TargetItemId);
                    }

                    command.MiningProductItemIds = vegeItems;
                    command.UntilDepleted = true;
                    return true;
                default:
                    errorCode = "invalid_command";
                    errorMessage = $"Unsupported mine target type: {targetType}";
                    return false;
            }
        }

        private static bool IsMineTargetDepleted(CommandState command, PlanetFactory factory)
        {
            if (factory == null)
            {
                return false;
            }

            switch (command.MineObjectType)
            {
                case EObjectType.Vein:
                    if (command.TargetId <= 0 ||
                        factory.veinPool == null ||
                        command.TargetId >= factory.veinPool.Length ||
                        factory.veinPool[command.TargetId].id != command.TargetId)
                    {
                        return true;
                    }

                    return factory.veinPool[command.TargetId].amount <= 0;
                case EObjectType.Vegetable:
                    return command.TargetId <= 0 ||
                        factory.vegePool == null ||
                        command.TargetId >= factory.vegePool.Length ||
                        factory.vegePool[command.TargetId].id != command.TargetId;
                default:
                    return true;
            }
        }

        private static bool IsMineOrderInterrupted(Player player)
        {
            if (player == null)
            {
                return false;
            }

            return ReflectionReader.Get(player, "currentOrder") == null;
        }

        private static Vector3 MiningStandPosition(Vector3 objectPosition)
        {
            var player = GameMain.mainPlayer;
            var from = player == null ? objectPosition.normalized * objectPosition.magnitude : player.position;
            var direction = objectPosition - from;
            if (direction.sqrMagnitude < 0.01f)
            {
                direction = objectPosition.normalized;
            }

            var target = objectPosition - direction.normalized * 3f;
            return target.normalized * objectPosition.magnitude;
        }

        private static int FirstVegeMiningItem(int protoId)
        {
            var items = VegeMiningItemIds(protoId);
            return items.Count == 0 ? 0 : items[0];
        }

        private static List<int> VegeMiningItemIds(int protoId)
        {
            var result = new List<int>();
            var proto = LDB.veges.Select(protoId);
            if (proto?.MiningItem == null)
            {
                return result;
            }

            foreach (var itemId in proto.MiningItem)
            {
                if (itemId > 0 && !result.Contains(itemId))
                {
                    result.Add(itemId);
                }
            }

            return result;
        }

        private static bool CanAssemblerUseRecipe(int entityProtoId, RecipeProto recipe)
        {
            var itemProto = LDB.items.Select(entityProtoId);
            if (itemProto == null)
            {
                return false;
            }

            return itemProto.prefabDesc.assemblerRecipeType == recipe.Type;
        }

        private static bool IsPlayerInInteractionRange(Player player, Vector3 target, out float distance, out float range)
        {
            distance = player == null ? float.MaxValue : (player.position - target).magnitude;
            range = InteractionRange(player);
            return distance <= range;
        }

        private static bool IsWithinCommandIssueRange(Player player, Vector3 target, out float distance, out float range)
        {
            distance = player == null ? float.MaxValue : (player.position - target).magnitude;
            range = CommandIssueRange(player);
            return distance <= range;
        }

        private static bool AreAllWithinCommandIssueRange(
            Player player,
            IEnumerable<Vector3> targets,
            out Vector3 failedTarget,
            out float distance,
            out float range)
        {
            failedTarget = Vector3.zero;
            distance = 0f;
            range = CommandIssueRange(player);
            if (player == null || targets == null)
            {
                distance = float.MaxValue;
                return false;
            }

            foreach (var target in targets)
            {
                var currentDistance = (player.position - target).magnitude;
                if (currentDistance <= range)
                {
                    continue;
                }

                failedTarget = target;
                distance = currentDistance;
                return false;
            }

            return true;
        }

        private static float CommandIssueRange(Player player)
        {
            var mecha = player?.mecha;
            var buildArea = Math.Max(0f, mecha?.buildArea ?? 0f);
            var baseRange = Math.Max(InteractionRange(player), buildArea);
            var range = baseRange * CommandIssueRangeBuildAreaFactor;
            return Math.Min(MaximumCommandIssueRange, Math.Max(MinimumCommandIssueRange, range));
        }

        private static JsonObject RangeFailureResult(float distance, float range, Vector3 target)
        {
            return new JsonObject
            {
                ["distance"] = distance,
                ["range"] = range,
                ["target"] = Vector(target)
            };
        }

        private static float InteractionRange(Player player)
        {
            var mecha = player?.mecha;
            var range = (float)ReflectionReader.GetDouble(mecha, 0, "reactorArea", "actionArea", "interactArea");
            if (range > 0)
            {
                return range;
            }

            return Math.Max(5f, mecha?.buildArea ?? 5f);
        }

        private sealed class SorterEndpoint
        {
            public SorterEndpoint(int entityId, int slot, bool hasPosition, Vector3 position)
            {
                EntityId = entityId;
                Slot = slot;
                HasPosition = hasPosition;
                Position = position;
            }

            public int EntityId { get; }

            public int Slot { get; }

            public bool HasPosition { get; }

            public Vector3 Position { get; }
        }

        private sealed class BeltEndpoint
        {
            public BeltEndpoint(int entityId, int slot, bool hasPosition, Vector3 position)
            {
                EntityId = entityId;
                Slot = slot;
                HasPosition = hasPosition;
                Position = position;
            }

            public int EntityId { get; }

            public int Slot { get; }

            public bool HasPosition { get; }

            public Vector3 Position { get; }
        }
    }
}
