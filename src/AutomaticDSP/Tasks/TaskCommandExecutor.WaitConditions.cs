using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        private bool TryEvaluateWaitCondition(
            CommandState command,
            DateTimeOffset now,
            out bool isSatisfied,
            out string errorCode,
            out string errorMessage)
        {
            isSatisfied = false;
            errorCode = null;
            errorMessage = null;

            var condition = command.Request.Condition as JObject;
            if (condition == null)
            {
                errorCode = "invalid_command";
                errorMessage = "waitUntil condition must be an object.";
                return false;
            }

            var type = condition.Value<string>("type");
            if (string.IsNullOrWhiteSpace(type))
            {
                errorCode = "invalid_command";
                errorMessage = "waitUntil condition is missing required field: type.";
                return false;
            }

            switch (TaskCommandType.Normalize(type))
            {
                case "always":
                    isSatisfied = true;
                    return true;
                case "never":
                    isSatisfied = false;
                    return true;
                case "elapsedseconds":
                    isSatisfied = command.StartedAt.HasValue &&
                        now >= command.StartedAt.Value.AddSeconds(Math.Max(0, condition.Value<double?>("seconds") ?? 0));
                    return true;
                case "elapsedticks":
                    var elapsedTicks = Math.Max(0, condition.Value<long?>("ticks") ?? 0);
                    var currentTick = CurrentGameTick();
                    isSatisfied = command.StartGameTick.HasValue &&
                        currentTick.HasValue &&
                        currentTick.Value >= command.StartGameTick.Value + elapsedTicks;
                    return true;
                case "gametickatleast":
                    var targetTick = condition.Value<long?>("gameTick");
                    if (!targetTick.HasValue)
                    {
                        errorCode = "invalid_command";
                        errorMessage = "gameTickAtLeast condition requires gameTick.";
                        return false;
                    }

                    var gameTick = CurrentGameTick();
                    isSatisfied = gameTick.HasValue && gameTick.Value >= targetTick.Value;
                    return true;
                case "inventoryatleast":
                    return TryEvaluateInventoryAtLeast(condition, out isSatisfied, out errorCode, out errorMessage);
                case "inventorydeltaatleast":
                    return TryEvaluateInventoryDeltaAtLeast(command, condition, out isSatisfied, out errorCode, out errorMessage);
                case "techunlocked":
                    return TryEvaluateTechUnlocked(condition, out isSatisfied, out errorCode, out errorMessage);
                case "recipeunlocked":
                    return TryEvaluateRecipeUnlocked(condition, out isSatisfied, out errorCode, out errorMessage);
                case "itemunlocked":
                    return TryEvaluateItemUnlocked(condition, out isSatisfied, out errorCode, out errorMessage);
                case "factoryproductdeltaatleast":
                case "itemproduced":
                case "nearbyitemproduced":
                    return TryEvaluateFactoryStatDeltaAtLeast(
                        command,
                        condition,
                        product: true,
                        out isSatisfied,
                        out errorCode,
                        out errorMessage);
                case "factoryconsumedeltaatleast":
                case "itemconsumed":
                    return TryEvaluateFactoryStatDeltaAtLeast(
                        command,
                        condition,
                        product: false,
                        out isSatisfied,
                        out errorCode,
                        out errorMessage);
                default:
                    errorCode = "unsupported_condition";
                    errorMessage = $"Unsupported waitUntil condition: {type}";
                    return false;
            }
        }

        private static bool TryEvaluateInventoryAtLeast(
            JObject condition,
            out bool isSatisfied,
            out string errorCode,
            out string errorMessage)
        {
            isSatisfied = false;
            if (!TryReadConditionItemCount(condition, out var itemId, out var count, out errorCode, out errorMessage))
            {
                return false;
            }

            isSatisfied = InventoryCount(itemId) >= count;
            return true;
        }

        private static bool TryEvaluateInventoryDeltaAtLeast(
            CommandState command,
            JObject condition,
            out bool isSatisfied,
            out string errorCode,
            out string errorMessage)
        {
            isSatisfied = false;
            if (!TryReadConditionItemCount(condition, out var itemId, out var count, out errorCode, out errorMessage))
            {
                return false;
            }

            var key = $"inventory:{itemId}";
            var baseline = GetOrSetWaitBaseline(command, key, InventoryCount(itemId));
            isSatisfied = InventoryCount(itemId) - baseline >= count;
            return true;
        }

        private static bool TryEvaluateTechUnlocked(
            JObject condition,
            out bool isSatisfied,
            out string errorCode,
            out string errorMessage)
        {
            isSatisfied = false;
            var techId = condition.Value<int?>("techId") ?? 0;
            if (techId <= 0)
            {
                errorCode = "invalid_command";
                errorMessage = "techUnlocked condition requires a positive techId.";
                return false;
            }

            if (GameMain.history == null)
            {
                errorCode = "game_not_ready";
                errorMessage = "Game history is not available.";
                return false;
            }

            isSatisfied = GameMain.history.TechUnlocked(techId);
            errorCode = null;
            errorMessage = null;
            return true;
        }

        private static bool TryEvaluateRecipeUnlocked(
            JObject condition,
            out bool isSatisfied,
            out string errorCode,
            out string errorMessage)
        {
            isSatisfied = false;
            var recipeId = condition.Value<int?>("recipeId") ?? 0;
            if (recipeId <= 0)
            {
                errorCode = "invalid_command";
                errorMessage = "recipeUnlocked condition requires a positive recipeId.";
                return false;
            }

            if (GameMain.history == null)
            {
                errorCode = "game_not_ready";
                errorMessage = "Game history is not available.";
                return false;
            }

            isSatisfied = GameMain.history.RecipeUnlocked(recipeId);
            errorCode = null;
            errorMessage = null;
            return true;
        }

        private static bool TryEvaluateItemUnlocked(
            JObject condition,
            out bool isSatisfied,
            out string errorCode,
            out string errorMessage)
        {
            isSatisfied = false;
            var itemId = condition.Value<int?>("itemId") ?? 0;
            if (itemId <= 0)
            {
                errorCode = "invalid_command";
                errorMessage = "itemUnlocked condition requires a positive itemId.";
                return false;
            }

            if (GameMain.history == null)
            {
                errorCode = "game_not_ready";
                errorMessage = "Game history is not available.";
                return false;
            }

            isSatisfied = GameMain.history.ItemUnlocked(itemId);
            errorCode = null;
            errorMessage = null;
            return true;
        }

        private static bool TryEvaluateFactoryStatDeltaAtLeast(
            CommandState command,
            JObject condition,
            bool product,
            out bool isSatisfied,
            out string errorCode,
            out string errorMessage)
        {
            isSatisfied = false;
            if (!TryReadConditionItemCount(condition, out var itemId, out var count, out errorCode, out errorMessage))
            {
                return false;
            }

            if (!TryReadFactoryStatCount(itemId, product, out var currentCount, out errorMessage))
            {
                errorCode = "game_not_ready";
                return false;
            }

            var key = $"{(product ? "factoryProduct" : "factoryConsume")}:{itemId}";
            var baseline = GetOrSetWaitBaseline(command, key, currentCount);
            isSatisfied = currentCount - baseline >= count;
            return true;
        }

        private static bool TryReadConditionItemCount(
            JObject condition,
            out int itemId,
            out int count,
            out string errorCode,
            out string errorMessage)
        {
            itemId = condition.Value<int?>("itemId") ?? 0;
            count = Math.Max(1, condition.Value<int?>("count") ?? 1);
            if (itemId > 0)
            {
                errorCode = null;
                errorMessage = null;
                return true;
            }

            errorCode = "invalid_command";
            errorMessage = "waitUntil item condition requires a positive itemId.";
            return false;
        }

        private static long GetOrSetWaitBaseline(CommandState command, string key, long value)
        {
            if (command.WaitBaselines == null)
            {
                command.WaitBaselines = new Dictionary<string, long>();
            }

            if (!command.WaitBaselines.TryGetValue(key, out var baseline))
            {
                baseline = value;
                command.WaitBaselines[key] = baseline;
            }

            return baseline;
        }

        private static bool TryReadFactoryStatCount(
            int itemId,
            bool product,
            out long value,
            out string errorMessage)
        {
            value = 0;
            var planet = GameMain.localPlanet;
            var pool = GameMain.statistics?.production?.factoryStatPool;
            if (planet == null || pool == null || planet.factoryIndex < 0 || planet.factoryIndex >= pool.Length)
            {
                errorMessage = "Current factory production statistics are not available.";
                return false;
            }

            var stat = pool[planet.factoryIndex];
            if (stat == null ||
                stat.productIndices == null ||
                itemId < 0 ||
                itemId >= stat.productIndices.Length)
            {
                errorMessage = $"Production statistics are not available for item: {itemId}";
                return false;
            }

            var index = stat.productIndices[itemId];
            if (index <= 0 || stat.productPool == null || index >= stat.productPool.Length || stat.productPool[index] == null)
            {
                value = 0;
                errorMessage = null;
                return true;
            }

            var productStat = stat.productPool[index];
            var totalIndex = product ? 6 : 13;
            if (productStat.total == null || totalIndex >= productStat.total.Length)
            {
                errorMessage = $"Production total statistics are not available for item: {itemId}";
                return false;
            }

            value = productStat.total[totalIndex];
            errorMessage = null;
            return true;
        }
    }
}
