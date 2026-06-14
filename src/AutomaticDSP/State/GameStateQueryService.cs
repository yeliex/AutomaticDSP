using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AutomaticDSP.Serialization;
using BepInEx.Logging;
using UnityEngine;

namespace AutomaticDSP.State
{
    internal sealed class GameStateQueryService
    {
        private const int DefaultQueryListLimit = 256;
        private const int MaxQueryListLimit = 2048;
        private const int MaxQueryDepth = 16;
        private const int OneLevelMemberLimit = 128;
        private const int SpaceObjectSampleLimit = 128;
        private readonly ManualLogSource log;
        private readonly int queryIntervalTicks;
        private readonly object queryLock = new object();
        private readonly List<PendingStateQuery> pendingStateQueries = new List<PendingStateQuery>();
        private long lastQueryDispatchGameTick = -1;
        private bool inactiveLogged;
        private string inactiveReasonLogged;
        private JsonObject latestGameStatus;

        public GameStateQueryService(int queryIntervalTicks, ManualLogSource log)
        {
            this.queryIntervalTicks = Math.Max(1, queryIntervalTicks);
            this.log = log;
            latestGameStatus = CaptureGameStatus();
        }

        public JsonObject GetGameStatus()
        {
            return latestGameStatus;
        }

        public bool IsStateQueryReady(out string status)
        {
            var gameStatus = latestGameStatus;
            status = JsonString(gameStatus, "status", "unknown");
            return JsonBool(gameStatus, "ready", false);
        }

        public Task<JsonObject> EnqueueStateQuery(StateQueryPlan plan, CancellationToken cancellationToken)
        {
            var pending = new PendingStateQuery(plan);
            if (cancellationToken.CanBeCanceled)
            {
                pending.Cancellation = cancellationToken.Register(() => CancelPendingStateQuery(pending));
            }

            lock (queryLock)
            {
                if (!pending.Completion.Task.IsCompleted)
                {
                    pendingStateQueries.Add(pending);
                }
            }

            return pending.Completion.Task;
        }

        public void Update()
        {
            latestGameStatus = CaptureGameStatus();
            var unavailableReason = GetSessionUnavailableReason();
            if (unavailableReason != null)
            {
                MarkSessionUnavailable(unavailableReason);
                RejectPendingStateQueries(JsonString(latestGameStatus, "status", "unknown"));
                return;
            }

            var gameTick = GameMain.gameTick;
            if (GameMain.isPaused)
            {
                if (HasPendingStateQueries())
                {
                    ExecutePendingStateQueries(gameTick);
                }

                return;
            }

            if (gameTick == lastQueryDispatchGameTick)
            {
                if (IsPrologueStatus() && HasPendingStateQueries())
                {
                    ExecutePendingStateQueries(gameTick);
                }

                return;
            }

            if (gameTick % queryIntervalTicks != 0)
            {
                return;
            }

            lastQueryDispatchGameTick = gameTick;
            ExecutePendingStateQueries(gameTick);
        }

        private bool HasPendingStateQueries()
        {
            lock (queryLock)
            {
                return pendingStateQueries.Count > 0;
            }
        }

        private bool IsPrologueStatus()
        {
            return string.Equals(JsonString(latestGameStatus, "status", "unknown"), "prologue", StringComparison.OrdinalIgnoreCase);
        }

        private void ExecutePendingStateQueries(long gameTick)
        {
            List<PendingStateQuery> queries;
            lock (queryLock)
            {
                if (pendingStateQueries.Count == 0)
                {
                    return;
                }

                queries = new List<PendingStateQuery>(pendingStateQueries);
                pendingStateQueries.Clear();
            }

            foreach (var query in queries)
            {
                if (query.Completion.Task.IsCompleted)
                {
                    continue;
                }

                try
                {
                    query.TrySetResult(EvaluateStateQuery(query.Plan, gameTick));
                }
                catch (Exception ex)
                {
                    log.LogWarning($"State query failed: {ex}");
                    query.TrySetException(ex);
                }
            }
        }

        private void RejectPendingStateQueries(string status)
        {
            List<PendingStateQuery> queries;
            lock (queryLock)
            {
                if (pendingStateQueries.Count == 0)
                {
                    return;
                }

                queries = new List<PendingStateQuery>(pendingStateQueries);
                pendingStateQueries.Clear();
            }

            var exception = new StateQueryNotReadyException(status);
            foreach (var query in queries)
            {
                query.TrySetException(exception);
            }
        }

        private void CancelPendingStateQuery(PendingStateQuery query)
        {
            lock (queryLock)
            {
                pendingStateQueries.Remove(query);
            }

            query.TrySetCanceled();
        }

        private void MarkSessionUnavailable(string reason)
        {
            if (!inactiveLogged || inactiveReasonLogged != reason)
            {
                log.LogInfo($"AutomaticDSP state query is waiting for a loaded game session: {reason}.");
                inactiveLogged = true;
                inactiveReasonLogged = reason;
            }
        }

        private JsonObject EvaluateStateQuery(StateQueryPlan plan, long gameTick)
        {
            var result = new JsonObject();
            foreach (var field in plan.Fields)
            {
                var source = ResolveRootSource(field.Name, gameTick);
                result[field.ResponseName] = ResolveQueryValue(source, field, 0);
            }

            return result;
        }

        private static object ResolveRootSource(string name, long gameTick)
        {
            switch (name)
            {
                case "metadata":
                    return CaptureQueryMetadata(gameTick);
                case "_schema":
                    return CaptureQuerySchema();
                case "game":
                    return CaptureQueryableGameState();
                case "gameMain":
                    return GameMain.instance;
                case "data":
                    return GameMain.data;
                case "player":
                case "mainPlayer":
                    return GameMain.mainPlayer;
                case "mecha":
                    return GameMain.mainPlayer?.mecha;
                case "inventory":
                case "package":
                    return GameMain.mainPlayer?.package;
                case "forge":
                case "replicator":
                    return GameMain.mainPlayer?.mecha?.forge;
                case "localPlanet":
                case "currentPlanet":
                    return GameMain.localPlanet;
                case "localStar":
                    return GameMain.localStar;
                case "factory":
                case "localFactory":
                    return GameMain.localPlanet?.factory;
                case "factoryDetails":
                case "localFactoryDetails":
                    return CaptureLocalPlanetFactories();
                case "factories":
                    return GameMain.data?.factories;
                case "production":
                    return GameMain.statistics?.production;
                case "power":
                    return GameMain.localPlanet?.factory?.powerSystem;
                case "research":
                case "technology":
                    return CaptureResearch();
                case "techs":
                    return CaptureTechPrototypes();
                case "recipes":
                    return CaptureRecipePrototypes();
                case "items":
                    return CaptureItemPrototypes();
                case "warningSystem":
                case "warnings":
                    return CaptureWarningSystem();
            }

            return GetGameMainStaticMember(name) ??
                MemberValue(GameMain.instance, name) ??
                GetGameDataMember(name);
        }

        private static JsonObject CaptureQueryMetadata(long gameTick)
        {
            return new JsonObject
            {
                ["gameTick"] = gameTick,
                ["queriedAt"] = DateTimeOffset.UtcNow,
                ["localPlanetId"] = GameMain.localPlanet?.id,
                ["localStarId"] = GameMain.localStar?.id,
                ["schemaVersion"] = 1
            };
        }

        private static JsonObject CaptureQuerySchema()
        {
            return new JsonObject
            {
                ["roots"] = new List<object>
                {
                    QueryRoot("metadata", "object", "Query metadata for the current state read."),
                    QueryRoot("game", "object", "Stable game/session summary."),
                    QueryRoot("gameMain", "object", "GameMain instance for selected field reads."),
                    QueryRoot("data", "object", "GameMain.data for selected field reads."),
                    QueryRoot("player", "object", "Current main player."),
                    QueryRoot("mainPlayer", "object", "Alias of player."),
                    QueryRoot("mecha", "object", "Current player mecha."),
                    QueryRoot("inventory", "object", "Current player inventory/package."),
                    QueryRoot("package", "object", "Alias of inventory."),
                    QueryRoot("forge", "object", "Current player replicator/forge."),
                    QueryRoot("replicator", "object", "Alias of forge."),
                    QueryRoot("localPlanet", "object", "Current local planet, if any."),
                    QueryRoot("currentPlanet", "object", "Alias of localPlanet."),
                    QueryRoot("localStar", "object", "Current local star, if any."),
                    QueryRoot("factory", "object", "Current local planet factory, if loaded."),
                    QueryRoot("localFactory", "object", "Alias of factory."),
                    QueryRoot("factoryDetails", "object", "Agent-friendly summary of current local planet factory entities."),
                    QueryRoot("localFactoryDetails", "object", "Alias of factoryDetails."),
                    QueryRoot("factories", "list", "GameData factories array."),
                    QueryRoot("production", "object", "Production statistics."),
                    QueryRoot("power", "object", "Current local planet power system."),
                    QueryRoot("research", "object", "Current technology research state."),
                    QueryRoot("technology", "object", "Alias of research."),
                    QueryRoot("techs", "list", "Technology prototype summaries."),
                    QueryRoot("recipes", "list", "Recipe prototype summaries."),
                    QueryRoot("items", "list", "Item prototype summaries."),
                    QueryRoot("warningSystem", "object", "Current game warning and broadcast summary."),
                    QueryRoot("warnings", "object", "Alias of warningSystem.")
                }
            };
        }

        private static JsonObject QueryRoot(string name, string kind, string description)
        {
            return new JsonObject
            {
                ["name"] = name,
                ["kind"] = kind,
                ["description"] = description
            };
        }

        private static JsonObject CaptureQueryableGameState()
        {
            return new JsonObject
            {
                ["ready"] = IsGameLoaded(),
                ["status"] = GetGameStatusValue(),
                ["gameName"] = GameMain.gameName,
                ["name"] = GameMain.gameName,
                ["creationTime"] = GameMain.creationTime,
                ["gameTick"] = GameMain.gameTick,
                ["gameTime"] = GameMain.gameTime,
                ["onceGameTick"] = GameMain.onceGameTick,
                ["onceGameTime"] = GameMain.onceGameTime,
                ["sandboxToolsEnabled"] = GameMain.sandboxToolsEnabled,
                ["desc"] = CaptureGameDesc(),
                ["localStarId"] = GameMain.localStar?.id,
                ["localStarName"] = GameMain.localStar?.displayName,
                ["localPlanetId"] = GameMain.localPlanet?.id,
                ["localPlanetName"] = GameMain.localPlanet?.displayName
            };
        }

        private static object ResolveQueryValue(object value, StateQueryField field, int depth)
        {
            if (value == null || depth > MaxQueryDepth)
            {
                return null;
            }

            if (field.Children.Count == 0)
            {
                return SerializeQueryLeaf(value, field);
            }

            if (value is JsonObject)
            {
                return ResolveQueryObject(value, field.Children, depth + 1);
            }

            if (value is IEnumerable enumerable && !(value is string))
            {
                return ResolveQueryEnumerable(enumerable, field, depth);
            }

            if (value is UnityEngine.Object)
            {
                return null;
            }

            return ResolveQueryObject(value, field.Children, depth + 1);
        }

        private static JsonObject ResolveQueryObject(object source, List<StateQueryField> fields, int depth)
        {
            var result = new JsonObject();
            foreach (var field in fields)
            {
                var value = ResolveQueryMember(source, field.Name);
                result[field.ResponseName] = ResolveQueryValue(value, field, depth);
            }

            return result;
        }

        private static object ResolveQueryEnumerable(IEnumerable enumerable, StateQueryField field, int depth)
        {
            var result = new List<object>();
            var offset = Math.Max(0, field.Offset ?? 0);
            var limit = Math.Max(0, Math.Min(field.Limit ?? DefaultQueryListLimit, MaxQueryListLimit));
            var index = 0;

            foreach (var item in enumerable)
            {
                if (!MatchesFilters(item, field.Filters))
                {
                    continue;
                }

                if (index++ < offset)
                {
                    continue;
                }

                if (result.Count >= limit)
                {
                    break;
                }

                result.Add(field.Children.Count == 0
                    ? SerializeQueryLeaf(item, field)
                    : ResolveQueryValue(item, field, depth + 1));
            }

            return result;
        }

        private static object ResolveQueryMember(object source, string name)
        {
            if (source == null)
            {
                return null;
            }

            if (name == "_fields")
            {
                return DiscoverFields(source);
            }

            if (source is JsonObject jsonObject)
            {
                return jsonObject.TryGetValue(name, out var value) ? value : null;
            }

            return GetQueryableMemberValue(source, name);
        }

        private static object SerializeQueryLeaf(object value, StateQueryField field)
        {
            if (value == null)
            {
                return null;
            }

            if (IsQueryScalar(value))
            {
                return value;
            }

            if (value is Enum)
            {
                return value.ToString();
            }

            var vector = VectorOrNull(value);
            if (vector != null)
            {
                return vector;
            }

            var quaternion = QuaternionOrNull(value);
            if (quaternion != null)
            {
                return quaternion;
            }

            var pose = PoseOrNull(value);
            if (pose != null)
            {
                return pose;
            }

            if (IsRuntimeValue(value))
            {
                return null;
            }

            if (value is JsonObject jsonObject)
            {
                return SerializeOneLevelJsonObject(jsonObject);
            }

            if (value is IEnumerable enumerable && !(value is string))
            {
                var result = new List<object>();
                var offset = Math.Max(0, field.Offset ?? 0);
                var limit = Math.Max(0, Math.Min(field.Limit ?? DefaultQueryListLimit, MaxQueryListLimit));
                var index = 0;
                foreach (var item in enumerable)
                {
                    if (!MatchesFilters(item, field.Filters))
                    {
                        continue;
                    }

                    if (index++ < offset)
                    {
                        continue;
                    }

                    if (result.Count >= limit)
                    {
                        break;
                    }

                    result.Add(item == null
                        ? null
                        : IsQueryScalar(item) ? SerializeQueryLeaf(item, field) : SerializeOneLevelObject(item));
                }

                return result;
            }

            return SerializeOneLevelObject(value);
        }

        private static JsonObject SerializeOneLevelJsonObject(JsonObject source)
        {
            var result = new JsonObject();
            var count = 0;
            foreach (var pair in source)
            {
                if (count++ >= OneLevelMemberLimit)
                {
                    break;
                }

                result[pair.Key] = SerializeOneLevelMemberValue(pair.Value);
            }

            return result;
        }

        private static JsonObject SerializeOneLevelObject(object source)
        {
            if (source == null || source is UnityEngine.Object)
            {
                return new JsonObject();
            }

            var result = new JsonObject();
            var type = source.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public;

            foreach (var field in type.GetFields(flags))
            {
                if (result.Count >= OneLevelMemberLimit)
                {
                    return result;
                }

                try
                {
                    if (!IsQueryableMember(field.Name, field.FieldType))
                    {
                        continue;
                    }

                    result[field.Name] = SerializeOneLevelMemberValue(field.GetValue(source));
                }
                catch
                {
                    result[field.Name] = null;
                }
            }

            foreach (var property in type.GetProperties(flags))
            {
                if (result.Count >= OneLevelMemberLimit)
                {
                    break;
                }

                if (result.ContainsKey(property.Name) || property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                try
                {
                    if (!IsQueryableMember(property.Name, property.PropertyType))
                    {
                        continue;
                    }

                    result[property.Name] = SerializeOneLevelMemberValue(property.GetValue(source, null));
                }
                catch
                {
                    result[property.Name] = null;
                }
            }

            return result;
        }

        private static object SerializeOneLevelMemberValue(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (IsQueryScalar(value))
            {
                return value;
            }

            if (value is Enum)
            {
                return value.ToString();
            }

            var vector = VectorOrNull(value);
            if (vector != null)
            {
                return vector;
            }

            var quaternion = QuaternionOrNull(value);
            if (quaternion != null)
            {
                return quaternion;
            }

            var pose = PoseOrNull(value);
            if (pose != null)
            {
                return pose;
            }

            if (IsRuntimeValue(value))
            {
                return null;
            }

            if (value is IEnumerable && !(value is string))
            {
                return new JsonObject
                {
                    ["count"] = CountOf(value)
                };
            }

            return new JsonObject();
        }

        private static List<object> DiscoverFields(object source)
        {
            var result = new List<object>();
            if (source == null)
            {
                return result;
            }

            if (source is JsonObject jsonObject)
            {
                foreach (var pair in jsonObject)
                {
                    if (result.Count >= OneLevelMemberLimit)
                    {
                        break;
                    }

                    if (pair.Value != null && (!IsQueryableMember(pair.Key, pair.Value.GetType()) || IsRuntimeValue(pair.Value)))
                    {
                        continue;
                    }

                    result.Add(FieldDescriptor(pair.Key, pair.Value));
                }

                return result;
            }

            var type = source.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public;
            foreach (var field in type.GetFields(flags))
            {
                if (result.Count >= OneLevelMemberLimit)
                {
                    return result;
                }

                if (!IsQueryableMember(field.Name, field.FieldType))
                {
                    continue;
                }

                result.Add(FieldDescriptor(field.Name, field.FieldType));
            }

            foreach (var property in type.GetProperties(flags))
            {
                if (result.Count >= OneLevelMemberLimit)
                {
                    break;
                }

                if (property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                if (!IsQueryableMember(property.Name, property.PropertyType))
                {
                    continue;
                }

                result.Add(FieldDescriptor(property.Name, property.PropertyType));
            }

            return result;
        }

        private static JsonObject FieldDescriptor(string name, object value)
        {
            if (value == null)
            {
                return new JsonObject
                {
                    ["name"] = name,
                    ["kind"] = "null",
                    ["type"] = null
                };
            }

            return FieldDescriptor(name, value.GetType());
        }

        private static JsonObject FieldDescriptor(string name, Type type)
        {
            return new JsonObject
            {
                ["name"] = name,
                ["kind"] = QueryKind(type),
                ["type"] = type == null ? null : FriendlyTypeName(type)
            };
        }

        private static string QueryKind(Type type)
        {
            if (type == null)
            {
                return "null";
            }

            if (IsScalarType(type) || type.IsEnum || IsVectorType(type))
            {
                return "scalar";
            }

            if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string))
            {
                return "list";
            }

            return "object";
        }

        private static string FriendlyTypeName(Type type)
        {
            if (type == null)
            {
                return null;
            }

            if (!type.IsArray)
            {
                return type.Name;
            }

            var elementType = type.GetElementType();
            return (elementType == null ? "Object" : elementType.Name) + "[]";
        }

        private static bool MatchesFilters(object item, List<StateQueryFilter> filters)
        {
            if (filters.Count == 0)
            {
                return true;
            }

            foreach (var filter in filters)
            {
                if (!MatchesFilter(item, filter))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool MatchesFilter(object item, StateQueryFilter filter)
        {
            if (!TryResolveFilterValue(item, filter.Path, out var actual))
            {
                return false;
            }

            switch (filter.Operation)
            {
                case StateQueryFilterOperator.NotEquals:
                    return filter.Values.Count > 0 && !ValuesEqual(actual, filter.Values[0]);
                case StateQueryFilterOperator.GreaterThan:
                    return filter.Values.Count > 0 && CompareNumbers(actual, filter.Values[0], out var greaterThan) && greaterThan > 0;
                case StateQueryFilterOperator.GreaterThanOrEqual:
                    return filter.Values.Count > 0 && CompareNumbers(actual, filter.Values[0], out var greaterThanOrEqual) && greaterThanOrEqual >= 0;
                case StateQueryFilterOperator.LessThan:
                    return filter.Values.Count > 0 && CompareNumbers(actual, filter.Values[0], out var lessThan) && lessThan < 0;
                case StateQueryFilterOperator.LessThanOrEqual:
                    return filter.Values.Count > 0 && CompareNumbers(actual, filter.Values[0], out var lessThanOrEqual) && lessThanOrEqual <= 0;
                case StateQueryFilterOperator.Contains:
                    return filter.Values.Count > 0 && TextContains(actual, filter.Values[0]);
                case StateQueryFilterOperator.StartsWith:
                    return filter.Values.Count > 0 && TextStartsWith(actual, filter.Values[0]);
                case StateQueryFilterOperator.EndsWith:
                    return filter.Values.Count > 0 && TextEndsWith(actual, filter.Values[0]);
                case StateQueryFilterOperator.In:
                    foreach (var expected in filter.Values)
                    {
                        if (ValuesEqual(actual, expected))
                        {
                            return true;
                        }
                    }

                    return false;
                default:
                    return filter.Values.Count > 0 && ValuesEqual(actual, filter.Values[0]);
            }
        }

        private static bool TryResolveFilterValue(object source, string[] path, out object value)
        {
            value = source;
            foreach (var segment in path)
            {
                if (value == null)
                {
                    return false;
                }

                if (value is JsonObject jsonObject)
                {
                    if (!jsonObject.TryGetValue(segment, out value))
                    {
                        return false;
                    }

                    if (IsRuntimeValue(value))
                    {
                        return false;
                    }

                    continue;
                }

                if (!TryGetQueryableMemberValue(value, segment, out value))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ValuesEqual(object actual, object expected)
        {
            if (actual == null || expected == null)
            {
                return actual == null && expected == null;
            }

            if (TryToDouble(actual, out var actualNumber) && TryToDouble(expected, out var expectedNumber))
            {
                return actualNumber.CompareTo(expectedNumber) == 0;
            }

            if (actual is bool actualBool && expected is bool expectedBool)
            {
                return actualBool == expectedBool;
            }

            return string.Equals(FilterText(actual), FilterText(expected), StringComparison.OrdinalIgnoreCase);
        }

        private static bool CompareNumbers(object actual, object expected, out int result)
        {
            result = 0;
            if (!TryToDouble(actual, out var actualNumber) || !TryToDouble(expected, out var expectedNumber))
            {
                return false;
            }

            result = actualNumber.CompareTo(expectedNumber);
            return true;
        }

        private static bool TextContains(object actual, object expected)
        {
            if (actual is IEnumerable enumerable && !(actual is string))
            {
                foreach (var item in enumerable)
                {
                    if (ValuesEqual(item, expected))
                    {
                        return true;
                    }
                }

                return false;
            }

            var actualText = FilterText(actual);
            var expectedText = FilterText(expected);
            return actualText != null &&
                expectedText != null &&
                actualText.IndexOf(expectedText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TextStartsWith(object actual, object expected)
        {
            var actualText = FilterText(actual);
            var expectedText = FilterText(expected);
            return actualText != null &&
                expectedText != null &&
                actualText.StartsWith(expectedText, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TextEndsWith(object actual, object expected)
        {
            var actualText = FilterText(actual);
            var expectedText = FilterText(expected);
            return actualText != null &&
                expectedText != null &&
                actualText.EndsWith(expectedText, StringComparison.OrdinalIgnoreCase);
        }

        private static string FilterText(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is Enum)
            {
                return value.ToString();
            }

            return value is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : value.ToString();
        }

        private static bool TryToDouble(object value, out double number)
        {
            switch (value)
            {
                case byte byteValue:
                    number = byteValue;
                    return true;
                case sbyte sbyteValue:
                    number = sbyteValue;
                    return true;
                case short shortValue:
                    number = shortValue;
                    return true;
                case ushort ushortValue:
                    number = ushortValue;
                    return true;
                case int intValue:
                    number = intValue;
                    return true;
                case uint uintValue:
                    number = uintValue;
                    return true;
                case long longValue:
                    number = longValue;
                    return true;
                case ulong ulongValue:
                    number = ulongValue;
                    return true;
                case float floatValue:
                    number = floatValue;
                    return true;
                case double doubleValue:
                    number = doubleValue;
                    return true;
                case decimal decimalValue:
                    number = (double)decimalValue;
                    return true;
                case string stringValue:
                    return double.TryParse(stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
                default:
                    number = 0;
                    return false;
            }
        }

        private static object GetQueryableMemberValue(object target, string name)
        {
            return TryGetQueryableMemberValue(target, name, out var value) ? value : null;
        }

        private static bool TryGetQueryableMemberValue(object target, string name, out object value)
        {
            if (target == null)
            {
                value = null;
                return false;
            }

            if (TryGetDerivedQueryableMemberValue(target, name, out value))
            {
                return true;
            }

            var type = target.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var field = type.GetField(name, flags);
            if (field != null)
            {
                if (!IsQueryableMember(field.Name, field.FieldType))
                {
                    value = null;
                    return false;
                }

                try
                {
                    value = field.GetValue(target);
                    if (IsRuntimeValue(value))
                    {
                        value = null;
                        return false;
                    }

                    return true;
                }
                catch
                {
                    value = null;
                    return false;
                }
            }

            var property = type.GetProperty(name, flags);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                if (!IsQueryableMember(property.Name, property.PropertyType))
                {
                    value = null;
                    return false;
                }

                try
                {
                    value = property.GetValue(target, null);
                    if (IsRuntimeValue(value))
                    {
                        value = null;
                        return false;
                    }

                    return true;
                }
                catch
                {
                    value = null;
                    return false;
                }
            }

            value = null;
            return false;
        }

        private static bool TryGetDerivedQueryableMemberValue(object target, string name, out object value)
        {
            value = null;
            if (string.Equals(name, "items", StringComparison.OrdinalIgnoreCase) &&
                TryGetStorageSummary(target, out var items, out _))
            {
                value = items;
                return true;
            }

            if (string.Equals(name, "summary", StringComparison.OrdinalIgnoreCase) &&
                TryGetStorageSummary(target, out _, out var summary))
            {
                value = summary;
                return true;
            }

            if (!string.Equals(name, "distanceToPlayer", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var player = GameMain.mainPlayer;
            if (player == null)
            {
                return false;
            }

            var position = MemberValue(target, "pos", "localPos", "localPosition", "position");
            switch (position)
            {
                case Vector3 localPosition:
                    value = Vector3.Distance(player.position, localPosition);
                    return true;
                case VectorLF3 universalPosition:
                    var dx = player.uPosition.x - universalPosition.x;
                    var dy = player.uPosition.y - universalPosition.y;
                    var dz = player.uPosition.z - universalPosition.z;
                    value = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryGetStorageSummary(object target, out List<object> items, out JsonObject summary)
        {
            items = null;
            summary = null;
            var grids = MemberValue(target, "grids") as Array;
            if (grids == null)
            {
                return false;
            }

            var byItem = new Dictionary<int, int>();
            var byItemInc = new Dictionary<int, int>();
            var byItemSlots = new Dictionary<int, int>();
            for (var i = 0; i < grids.Length; i++)
            {
                var grid = grids.GetValue(i);
                var itemId = MemberInt(grid, 0, "itemId");
                var count = MemberInt(grid, 0, "count");
                if (itemId <= 0 || count <= 0)
                {
                    continue;
                }

                byItem.TryGetValue(itemId, out var existingCount);
                byItem[itemId] = existingCount + count;
                byItemInc.TryGetValue(itemId, out var existingInc);
                byItemInc[itemId] = existingInc + MemberInt(grid, 0, "inc");
                byItemSlots.TryGetValue(itemId, out var existingSlots);
                byItemSlots[itemId] = existingSlots + 1;
            }

            items = InventorySummaryItems(byItem, byItemInc, byItemSlots);
            summary = new JsonObject
            {
                ["items"] = items,
                ["totalItemCount"] = TotalItemCount(byItem),
                ["distinctItemCount"] = byItem.Count
            };
            return true;
        }

        private static List<object> InventorySummaryItems(
            Dictionary<int, int> items,
            Dictionary<int, int> incs,
            Dictionary<int, int> slots)
        {
            var result = new List<object>();
            foreach (var pair in items)
            {
                incs.TryGetValue(pair.Key, out var inc);
                slots.TryGetValue(pair.Key, out var slotCount);
                result.Add(new JsonObject
                {
                    ["itemId"] = pair.Key,
                    ["name"] = ItemName(pair.Key),
                    ["count"] = pair.Value,
                    ["inc"] = inc,
                    ["slots"] = slotCount
                });
            }

            return result;
        }

        private static bool IsQueryableMember(string name, Type type)
        {
            return !IsRuntimeMemberName(name) && !IsRuntimeType(type);
        }

        private static bool IsRuntimeValue(object value)
        {
            return value != null && IsRuntimeType(value.GetType());
        }

        private static bool IsRuntimeType(Type type)
        {
            if (type == null)
            {
                return false;
            }

            if (typeof(Delegate).IsAssignableFrom(type))
            {
                return true;
            }

            return typeof(UnityEngine.Object).IsAssignableFrom(type);
        }

        private static bool IsRuntimeMemberName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            var lower = name.ToLowerInvariant();
            return lower == "gameobject" ||
                lower == "transform" ||
                lower == "material" ||
                lower == "terrainmaterial" ||
                lower == "mesh" ||
                lower == "renderer" ||
                lower == "shader" ||
                lower == "texture" ||
                lower == "camera" ||
                lower == "audio" ||
                lower == "animator" ||
                lower == "collider" ||
                lower == "rigidbody" ||
                lower == "recttransform" ||
                lower == "canvas" ||
                lower == "sprite" ||
                lower.EndsWith("gameobject") ||
                lower.EndsWith("material") ||
                lower.EndsWith("renderer") ||
                lower.EndsWith("texture");
        }

        private static bool IsQueryScalar(object value)
        {
            return value is string ||
                value is bool ||
                value is byte ||
                value is sbyte ||
                value is short ||
                value is ushort ||
                value is int ||
                value is uint ||
                value is long ||
                value is ulong ||
                value is float ||
                value is double ||
                value is decimal ||
                value is DateTime ||
                value is DateTimeOffset;
        }

        private static bool IsScalarType(Type type)
        {
            return type == typeof(string) ||
                type == typeof(bool) ||
                type == typeof(byte) ||
                type == typeof(sbyte) ||
                type == typeof(short) ||
                type == typeof(ushort) ||
                type == typeof(int) ||
                type == typeof(uint) ||
                type == typeof(long) ||
                type == typeof(ulong) ||
                type == typeof(float) ||
                type == typeof(double) ||
                type == typeof(decimal) ||
                type == typeof(DateTime) ||
                type == typeof(DateTimeOffset);
        }

        private static bool IsVectorType(Type type)
        {
            if (type == null)
            {
                return false;
            }

            var name = type.Name;
            return name == "Vector2" ||
                name == "Vector3" ||
                name == "Vector4" ||
                name == "VectorLF3" ||
                name == "Quaternion";
        }

        private JsonObject CaptureGameState()
        {
            var data = GameMain.data;
            var galaxy = GameMain.galaxy;

            return new JsonObject
            {
                ["name"] = GameMain.gameName,
                ["creationTime"] = GameMain.creationTime,
                ["gameTick"] = GameMain.gameTick,
                ["gameTime"] = GameMain.gameTime,
                ["onceGameTick"] = GameMain.onceGameTick,
                ["onceGameTime"] = GameMain.onceGameTime,
                ["sandboxToolsEnabled"] = GameMain.sandboxToolsEnabled,
                ["desc"] = CaptureGameDesc(),
                ["lifecycle"] = new JsonObject
                {
                    ["notNull"] = GameMain.notNull,
                    ["isRunning"] = GameMain.isRunning,
                    ["isLoading"] = GameMain.isLoading,
                    ["isPaused"] = GameMain.isPaused,
                    ["isFullscreenPaused"] = GameMain.isFullscreenPaused,
                    ["isEnded"] = GameMain.isEnded,
                    ["loadErrored"] = GameMain.loadErrored,
                    ["inOtherScene"] = GameMain.inOtherScene,
                    ["isMenuDemo"] = DSPGame.IsMenuDemo,
                    ["menuDemoLoaded"] = DSPGame.MenuDemoLoaded,
                    ["gameMainIsMenuDemo"] = IsGameMainMenuDemo()
                },
                ["location"] = new JsonObject
                {
                    ["localStarId"] = GameMain.localStar?.id,
                    ["localStarName"] = GameMain.localStar?.displayName,
                    ["localPlanetId"] = GameMain.localPlanet?.id,
                    ["localPlanetName"] = GameMain.localPlanet?.displayName,
                    ["playerOnPlanet"] = GameMain.localPlanet != null
                },
                ["galaxy"] = new JsonObject
                {
                    ["starCount"] = ReflectionReader.GetInt(galaxy, 0, "starCount")
                },
                ["data"] = new JsonObject
                {
                    ["factoryCount"] = ReflectionReader.GetInt(data, 0, "factoryCount"),
                    ["dysonSphereCount"] = data?.dysonSpheres?.Length ?? 0,
                    ["guideRunning"] = ReflectionReader.GetBool(data, false, "guideRunning"),
                    ["guideComplete"] = ReflectionReader.GetBool(data, false, "guideComplete"),
                    ["isLandReady"] = ReflectionReader.GetBool(data, false, "isLandReady")
                },
                ["systems"] = new JsonObject
                {
                    ["hasMainPlayer"] = GameMain.mainPlayer != null,
                    ["hasHistory"] = GameMain.history != null,
                    ["hasStatistics"] = GameMain.statistics != null,
                    ["hasPreferences"] = GameMain.preferences != null,
                    ["hasSpaceSector"] = GameMain.spaceSector != null,
                    ["hasGalacticTransport"] = data?.galacticTransport != null,
                    ["hasWarningSystem"] = data?.warningSystem != null,
                    ["hasTrashSystem"] = data?.trashSystem != null
                }
            };
        }

        private static JsonObject CaptureGameStatus()
        {
            var status = GetGameStatusValue();
            var ready = status == "running" || status == "paused" || status == "prologue";
            var result = new JsonObject
            {
                ["ready"] = ready,
                ["status"] = status
            };

            if (!ready)
            {
                return result;
            }

            var desc = GetGameDataMember("gameDesc");
            var combatSettings = MemberValue(desc, "combatSettings");
            result["gameName"] = GameMain.gameName;
            result["gameTick"] = GameMain.gameTick;
            result["gameTime"] = GameMain.gameTime;
            result["onceGameTick"] = GameMain.onceGameTick;
            result["onceGameTime"] = GameMain.onceGameTime;
            result["sandboxToolsEnabled"] = GameMain.sandboxToolsEnabled;
            result["creationTime"] = GameMain.creationTime;
            result["isCombatMode"] = MemberBool(desc, false, "isCombatMode");
            result["combatModeDifficulty"] = combatSettings == null ? (object)null : MemberDouble(combatSettings, 0, "difficulty");
            result["resourceMultiplier"] = desc == null ? (object)null : MemberDouble(desc, 0, "resourceMultiplier");
            result["oilAmountMultiplier"] = desc == null ? (object)null : MemberDouble(desc, 0, "oilAmountMultiplier");
            result["starCount"] = desc == null ? (object)null : MemberInt(desc, 0, "starCount");
            return result;
        }

        private static string GetGameStatusValue()
        {
            if (SafeBool(() => GameMain.loadErrored))
            {
                return "error";
            }

            if (!IsPreloadReady())
            {
                return "loading";
            }

            if (SafeBool(() => GameMain.isLoading))
            {
                return "loading";
            }

            var data = SafeValue(() => GameMain.data);
            if (data == null)
            {
                return IsGameStartRequested() ? "loading" : "menu";
            }

            if (SafeBool(() => DSPGame.IsMenuDemo) || IsGameMainMenuDemo())
            {
                return "menu";
            }

            if (SafeBool(() => GameMain.isEnded))
            {
                return "ended";
            }

            if (SafeBool(() => DSPGame.IsCombatCutscene))
            {
                return "cutscene";
            }

            if (MemberBool(data, false, "guideRunning") && !MemberBool(data, false, "guideComplete"))
            {
                return "prologue";
            }

            if (SafeBool(() => GameMain.isPaused) ||
                SafeBool(() => GameMain.isFullscreenPaused) ||
                SafeBool(() => GameMain.inOtherScene))
            {
                return "paused";
            }

            if (SafeBool(() => GameMain.isRunning))
            {
                return "running";
            }

            return "unknown";
        }

        private static bool SafeBool(Func<bool> read)
        {
            try
            {
                return read();
            }
            catch
            {
                return false;
            }
        }

        private static long SafeLong(Func<long> read, long defaultValue)
        {
            try
            {
                return read();
            }
            catch
            {
                return defaultValue;
            }
        }

        private static object SafeValue(Func<object> read)
        {
            try
            {
                return read();
            }
            catch
            {
                return null;
            }
        }

        private static JsonObject CaptureGameDesc()
        {
            var desc = GetGameDataMember("gameDesc");
            if (desc == null)
            {
                return Unavailable("game_desc_missing");
            }

            return new JsonObject
            {
                ["creationTime"] = MemberValue(desc, "creationTime"),
                ["creationVersion"] = MemberValue(desc, "creationVersion")?.ToString(),
                ["galaxyAlgo"] = MemberInt(desc, 0, "galaxyAlgo"),
                ["galaxySeed"] = MemberInt(desc, 0, "galaxySeed"),
                ["starCount"] = MemberInt(desc, 0, "starCount"),
                ["clusterString"] = MemberString(desc, "clusterString"),
                ["clusterStringLong"] = MemberString(desc, "clusterStringLong"),
                ["seedKey64"] = MemberLong(desc, 0, "seedKey64"),
                ["playerProto"] = MemberInt(desc, 0, "playerProto"),
                ["resourceMultiplier"] = MemberDouble(desc, 0, "resourceMultiplier"),
                ["oilAmountMultiplier"] = MemberDouble(desc, 0, "oilAmountMultiplier"),
                ["isInfiniteResource"] = MemberBool(desc, false, "isInfiniteResource"),
                ["isRareResource"] = MemberBool(desc, false, "isRareResource"),
                ["achievementEnable"] = MemberBool(desc, false, "achievementEnable"),
                ["isPeaceMode"] = MemberBool(desc, false, "isPeaceMode"),
                ["isSandboxMode"] = MemberBool(desc, false, "isSandboxMode"),
                ["isCombatMode"] = MemberBool(desc, false, "isCombatMode"),
                ["goalLevel"] = MemberValue(desc, "goalLevel")?.ToString(),
                ["hasCombatSettings"] = MemberValue(desc, "combatSettings") != null,
                ["enemyDropMultiplier"] = MemberDouble(desc, 0, "enemyDropMultiplier"),
                ["propertyMultiplier"] = MemberDouble(desc, 0, "propertyMultiplier"),
                ["combatModeDifficultyNumber"] = MemberInt(desc, 0, "combatModeDifficultyNumber"),
                ["savedThemeIds"] = IntArray(MemberValue(desc, "savedThemeIds") as Array, 128, true)
            };
        }

        private static JsonObject CapturePreferences()
        {
            var prefs = (object)GameMain.preferences ?? GetGameDataMember("preferences");
            if (prefs == null)
            {
                return Unavailable("preferences_missing");
            }

            return new JsonObject
            {
                ["camera"] = new JsonObject
                {
                    ["uPosition"] = VectorOrNull(MemberValue(prefs, "cameraUPos")),
                    ["uRotation"] = QuaternionOrNull(MemberValue(prefs, "cameraURot"))
                },
                ["reformBrush"] = new JsonObject
                {
                    ["size"] = MemberInt(prefs, 0, "reformBrushSize"),
                    ["type"] = MemberInt(prefs, 0, "reformBrushType"),
                    ["decalType"] = MemberInt(prefs, 0, "reformBrushDecalType"),
                    ["color"] = MemberInt(prefs, 0, "reformBrushColor"),
                    ["buryVeins"] = MemberBool(prefs, false, "reformBuryVeins")
                },
                ["details"] = new JsonObject
                {
                    ["power"] = MemberBool(prefs, false, "detailPower"),
                    ["vein"] = MemberBool(prefs, false, "detailVein"),
                    ["spaceGuide"] = MemberBool(prefs, false, "detailSpaceGuide"),
                    ["defense"] = MemberBool(prefs, false, "detailDefense"),
                    ["sign"] = MemberBool(prefs, false, "detailSign"),
                    ["icon"] = MemberBool(prefs, false, "detailIcon"),
                    ["light"] = MemberBool(prefs, false, "detailLight"),
                    ["hpBar"] = MemberBool(prefs, false, "detailHpBar")
                },
                ["upgrade"] = new JsonObject
                {
                    ["level"] = MemberInt(prefs, 0, "upgradeLevel"),
                    ["cursorType"] = MemberInt(prefs, 0, "upgradeCursorType"),
                    ["cursorSize"] = MemberInt(prefs, 0, "upgradeCursorSize"),
                    ["filterFacility"] = MemberBool(prefs, false, "upgradeFilterFacility"),
                    ["filterBelt"] = MemberBool(prefs, false, "upgradeFilterBelt"),
                    ["filterInserter"] = MemberBool(prefs, false, "upgradeFilterInserter")
                },
                ["dismantle"] = new JsonObject
                {
                    ["cursorType"] = MemberInt(prefs, 0, "dismantleCursorType"),
                    ["cursorSize"] = MemberInt(prefs, 0, "dismantleCursorSize"),
                    ["filterFacility"] = MemberBool(prefs, false, "dismantleFilterFacility"),
                    ["filterBelt"] = MemberBool(prefs, false, "dismantleFilterBelt"),
                    ["filterInserter"] = MemberBool(prefs, false, "dismantleFilterInserter"),
                    ["instantDismantle"] = MemberBool(prefs, false, "instantDismantle")
                },
                ["blueprint"] = new JsonObject
                {
                    ["selectReform"] = MemberBool(prefs, false, "blueprintSelectReform"),
                    ["selectUndecalReforms"] = MemberBool(prefs, false, "blueprintSelectUndecalReforms"),
                    ["reformPartialPaste"] = MemberBool(prefs, false, "blueprintReformPartialPaste"),
                    ["reformFillOnly"] = MemberBool(prefs, false, "blueprintReformFillOnly"),
                    ["saveReform"] = MemberBool(prefs, false, "blueprintSaveReform"),
                    ["quickPaste"] = MemberBool(prefs, false, "blueprintQuickPaste"),
                    ["useReforms"] = MemberBool(prefs, false, "blueprintUseReforms"),
                    ["usePalette"] = MemberBool(prefs, false, "blueprintUsePalette"),
                    ["autoBuryBase"] = MemberBool(prefs, false, "blueprintAutoBuryBase")
                },
                ["build"] = new JsonObject
                {
                    ["fastBuildBatchSize"] = MemberInt(prefs, 0, "fastBuildBatchSize")
                },
                ["signalPicker"] = new JsonObject
                {
                    ["autoClose"] = MemberBool(prefs, false, "signalPickerAutoClose"),
                    ["showUnlock"] = MemberBool(prefs, false, "signalPickerShowUnlock")
                },
                ["techTree"] = new JsonObject
                {
                    ["showProperty"] = MemberBool(prefs, false, "techTreeShowProperty")
                },
                ["dysonSphereEditor"] = new JsonObject
                {
                    ["hideFarSide"] = MemberBool(prefs, false, "dysonSphereHideFarSideInEditor"),
                    ["hideRocketBodies"] = MemberBool(prefs, false, "dysonSphereHideRocketBodies")
                },
                ["sandbox"] = new JsonObject
                {
                    ["isDirectlyObtain"] = MemberBool(prefs, false, "sandboxIsDirectlyObtain"),
                    ["directlyObtainIsStack"] = MemberBool(prefs, false, "sandboxDirectlyObtainIsStack")
                },
                ["combat"] = new JsonObject
                {
                    ["turretBurstMode"] = MemberInt(prefs, 0, "turretBurstMode"),
                    ["dfMonitorDisplayMode"] = MemberInt(prefs, 0, "dfMonitorDisplayMode"),
                    ["trackedEnemyClusterCount"] = CountOf(MemberValue(prefs, "trackedEnemyClusters")),
                    ["enemyDropBanCount"] = CountOf(MemberValue(prefs, "enemyDropBans"))
                },
                ["ui"] = new JsonObject
                {
                    ["goalPanelState"] = MemberValue(prefs, "uiGoalPanelState")?.ToString(),
                    ["controlPanelInterstellarPairing"] = MemberInt(prefs, 0, "uiControlPanelInterstellarPairing"),
                    ["controlPanelIntraplanetaryPairing"] = MemberInt(prefs, 0, "uiControlPanelIntraplanetaryPairing"),
                    ["controlPanelIntraplanetaryPairingPlanetId"] = MemberInt(prefs, 0, "uiControlPanelIntraplanetaryPairingPlanetId"),
                    ["controlPanelDispenserPairing"] = MemberInt(prefs, 0, "uiControlPanelDispenserPairing"),
                    ["controlPanelDispenserPairingPlanetId"] = MemberInt(prefs, 0, "uiControlPanelDispenserPairingPlanetId"),
                    ["controlPanelFilter"] = MemberInt(prefs, 0, "uiControlPanelFilter"),
                    ["autoOpenDashboardOnAddStatPlan"] = MemberBool(prefs, false, "autoOpenDashboardOnAddStatPlan")
                },
                ["collections"] = new JsonObject
                {
                    ["replicatorMultiplierCount"] = CountOf(MemberValue(prefs, "replicatorMultipliers")),
                    ["tutorialShowingCount"] = CountOf(MemberValue(prefs, "tutorialShowing")),
                    ["astroNameOverrideCount"] = CountOf(MemberValue(prefs, "astroNameOverride")),
                    ["pickFilterCount"] = CountOf(MemberValue(prefs, "pickFilters")),
                    ["colorPanelRecentColorCount"] = CountOf(MemberValue(prefs, "colorPanelRecentColors")),
                    ["uiControlPanelAstroExpandCount"] = CountOf(MemberValue(prefs, "uiControlPanelAstroExpands"))
                }
            };
        }

        private static JsonObject CaptureStatistics()
        {
            var statistics = (object)GameMain.statistics ?? GetGameDataMember("statistics");
            if (statistics == null)
            {
                return Unavailable("statistics_missing");
            }

            return new JsonObject
            {
                ["tech"] = new JsonObject
                {
                    ["hashedThisFrame"] = MemberInt(statistics, 0, "techHashedThisFrame"),
                    ["hashedFor10Frames"] = MemberInt(statistics, 0, "techHashedFor10Frames"),
                    ["hashedRecorded"] = MemberInt(statistics, 0, "techHashedRecorded"),
                    ["recentHashes"] = IntArray(MemberValue(statistics, "techHashedHistory") as Array, 60, true)
                },
                ["systems"] = new JsonObject
                {
                    ["production"] = MemberValue(statistics, "production") != null,
                    ["kill"] = MemberValue(statistics, "kill") != null,
                    ["traffic"] = MemberValue(statistics, "traffic") != null,
                    ["charts"] = MemberValue(statistics, "charts") != null
                }
            };
        }

        private static JsonObject CaptureHistory()
        {
            var history = (object)GameMain.history ?? GetGameDataMember("history");
            if (history == null)
            {
                return Unavailable("history_missing");
            }

            return new JsonObject
            {
                ["currentTechId"] = MemberInt(history, 0, "currentTech"),
                ["currentTechName"] = TechName(MemberInt(history, 0, "currentTech")),
                ["techQueueLength"] = MemberInt(history, 0, "techQueueLength"),
                ["techQueue"] = TechQueue(MemberValue(history, "techQueue") as IEnumerable),
                ["counts"] = new JsonObject
                {
                    ["recipeUnlocked"] = CountOf(MemberValue(history, "recipeUnlocked")),
                    ["enemyDropItemUnlocked"] = CountOf(MemberValue(history, "enemyDropItemUnlocked")),
                    ["tutorialUnlocked"] = CountOf(MemberValue(history, "tutorialUnlocked")),
                    ["techStates"] = CountOf(MemberValue(history, "techStates")),
                    ["features"] = CountOf(MemberValue(history, "featureKeys")),
                    ["featureValues"] = CountOf(MemberValue(history, "featureValues")),
                    ["pinnedPlanets"] = CountOf(MemberValue(history, "pinnedPlanets"))
                },
                ["unlocks"] = new JsonObject
                {
                    ["logisticsSystem"] = MemberBool(history, false, "logisticsSystemUnlocked"),
                    ["interstellarStation"] = MemberBool(history, false, "interstellarStationUnlocked"),
                    ["intraplanetaryStation"] = MemberBool(history, false, "intraplanetaryStationUnlocked"),
                    ["dispenser"] = MemberBool(history, false, "dispenserUnlocked"),
                    ["marker"] = MemberBool(history, false, "markerUnlocked"),
                    ["foundation"] = MemberBool(history, false, "foundationUnlocked"),
                    ["dysonSphereSystem"] = MemberBool(history, false, "dysonSphereSystemUnlocked"),
                    ["ultraPhoton"] = MemberBool(history, false, "ultraPhotonUnlocked"),
                    ["dysonSphereLayerPanel"] = MemberBool(history, false, "dysonSphereLayerPanelUnlocked"),
                    ["verticalConstruct"] = MemberBool(history, false, "verticalConstructUnlocked"),
                    ["combatDrone"] = MemberBool(history, false, "combatDroneUnlocked"),
                    ["combatShip"] = MemberBool(history, false, "combatShipUnlocked")
                },
                ["upgrades"] = new JsonObject
                {
                    ["constructionDroneSpeed"] = MemberDouble(history, 0, "constructionDroneSpeed"),
                    ["constructionDroneMovement"] = MemberInt(history, 0, "constructionDroneMovement"),
                    ["autoReconstructSpeed"] = MemberInt(history, 0, "autoReconstructSpeed"),
                    ["logisticDroneSpeed"] = MemberDouble(history, 0, "logisticDroneSpeed"),
                    ["logisticDroneSpeedModified"] = MemberDouble(history, 0, "logisticDroneSpeedModified"),
                    ["logisticDroneCarries"] = MemberInt(history, 0, "logisticDroneCarries"),
                    ["logisticShipSailSpeed"] = MemberDouble(history, 0, "logisticShipSailSpeed"),
                    ["logisticShipSailSpeedModified"] = MemberDouble(history, 0, "logisticShipSailSpeedModified"),
                    ["logisticShipWarpSpeed"] = MemberDouble(history, 0, "logisticShipWarpSpeed"),
                    ["logisticShipWarpSpeedModified"] = MemberDouble(history, 0, "logisticShipWarpSpeedModified"),
                    ["logisticShipWarpDrive"] = MemberBool(history, false, "logisticShipWarpDrive"),
                    ["logisticShipCarries"] = MemberInt(history, 0, "logisticShipCarries"),
                    ["logisticCourierSpeed"] = MemberDouble(history, 0, "logisticCourierSpeed"),
                    ["logisticCourierSpeedModified"] = MemberDouble(history, 0, "logisticCourierSpeedModified"),
                    ["logisticCourierCarries"] = MemberInt(history, 0, "logisticCourierCarries"),
                    ["miningCostRate"] = MemberDouble(history, 0, "miningCostRate"),
                    ["miningSpeedScale"] = MemberDouble(history, 0, "miningSpeedScale"),
                    ["storageLevel"] = MemberInt(history, 0, "storageLevel"),
                    ["labLevel"] = MemberInt(history, 0, "labLevel"),
                    ["techSpeed"] = MemberInt(history, 0, "techSpeed"),
                    ["buildMaxHeight"] = MemberDouble(history, 0, "buildMaxHeight"),
                    ["beltVerticalConstruction"] = MemberBool(history, false, "beltVerticalConstruction"),
                    ["inserterBidirectional"] = MemberBool(history, false, "inserterBidirectional"),
                    ["inserterStackInput"] = MemberInt(history, 0, "inserterStackInput"),
                    ["inserterStackOutput"] = MemberInt(history, 0, "inserterStackOutput"),
                    ["stationPilerLevel"] = MemberInt(history, 0, "stationPilerLevel"),
                    ["localStationExtraStorage"] = MemberInt(history, 0, "localStationExtraStorage"),
                    ["remoteStationExtraStorage"] = MemberInt(history, 0, "remoteStationExtraStorage"),
                    ["dispenserDeliveryMaxAngle"] = MemberDouble(history, 0, "dispenserDeliveryMaxAngle"),
                    ["planetaryATFieldEnergyRate"] = MemberDouble(history, 0, "planetaryATFieldEnergyRate"),
                    ["universeObserveLevel"] = MemberInt(history, 0, "universeObserveLevel"),
                    ["autoManageLabItems"] = MemberBool(history, false, "autoManageLabItems"),
                    ["logisticDroneSpeedScale"] = MemberDouble(history, 0, "logisticDroneSpeedScale"),
                    ["logisticShipSpeedScale"] = MemberDouble(history, 0, "logisticShipSpeedScale"),
                    ["logisticCourierSpeedScale"] = MemberDouble(history, 0, "logisticCourierSpeedScale"),
                    ["fighterInitializeSpeedScale"] = MemberDouble(history, 0, "fighterInitializeSpeedScale")
                },
                ["dyson"] = new JsonObject
                {
                    ["solarSailLife"] = MemberDouble(history, 0, "solarSailLife"),
                    ["solarEnergyLossRate"] = MemberDouble(history, 0, "solarEnergyLossRate"),
                    ["useIonLayer"] = MemberBool(history, false, "useIonLayer"),
                    ["dysonNodeLatitude"] = MemberDouble(history, 0, "dysonNodeLatitude"),
                    ["dysonNodeAbsorbInterval"] = MemberInt(history, 0, "dysonNodeAbsorbInterval"),
                    ["universeMatrixPointUploaded"] = MemberLong(history, 0, "universeMatrixPointUploaded")
                },
                ["blueprint"] = new JsonObject
                {
                    ["blueprintLimit"] = MemberInt(history, 0, "blueprintLimit"),
                    ["reformLimit"] = MemberInt(history, 0, "bpReformLimit")
                },
                ["combat"] = new JsonObject
                {
                    ["globalHpScale"] = MemberDouble(history, 0, "globalHPScale"),
                    ["kineticDamageScale"] = MemberDouble(history, 0, "kineticDamageScale"),
                    ["energyDamageScale"] = MemberDouble(history, 0, "energyDamageScale"),
                    ["blastDamageScale"] = MemberDouble(history, 0, "blastDamageScale"),
                    ["magneticDamageScale"] = MemberDouble(history, 0, "magneticDamageScale"),
                    ["enemyDropScale"] = MemberDouble(history, 0, "enemyDropScale"),
                    ["groundFleetPortCount"] = MemberInt(history, 0, "groundFleetPortCount"),
                    ["spaceFleetPortCount"] = MemberInt(history, 0, "spaceFleetPortCount"),
                    ["dfTruceTimer"] = MemberLong(history, 0, "dfTruceTimer"),
                    ["minimalDifficulty"] = MemberDouble(history, 0, "minimalDifficulty"),
                    ["minimalPropertyMultiplier"] = MemberDouble(history, 0, "minimalPropertyMultiplier"),
                    ["currentPropertyMultiplier"] = MemberDouble(history, 0, "currentPropertyMultiplier"),
                    ["globalHpEnhancement"] = MemberDouble(history, 0, "globalHpEnhancement"),
                    ["combatDroneDamageRatio"] = MemberDouble(history, 0, "combatDroneDamageRatio"),
                    ["combatDroneDurabilityRatio"] = MemberDouble(history, 0, "combatDroneDurabilityRatio"),
                    ["combatDroneROFRatio"] = MemberDouble(history, 0, "combatDroneROFRatio"),
                    ["combatDroneSpeedRatio"] = MemberDouble(history, 0, "combatDroneSpeedRatio"),
                    ["combatShipDamageRatio"] = MemberDouble(history, 0, "combatShipDamageRatio"),
                    ["combatShipDurabilityRatio"] = MemberDouble(history, 0, "combatShipDurabilityRatio"),
                    ["combatShipROFRatio"] = MemberDouble(history, 0, "combatShipROFRatio"),
                    ["combatShipSpeedRatio"] = MemberDouble(history, 0, "combatShipSpeedRatio")
                },
                ["hasTechLock"] = MemberValue(history, "techLock") != null,
                ["hasPropertyData"] = MemberValue(history, "propertyData") != null,
                ["hasCombatSettings"] = MemberValue(history, "combatSettings") != null,
                ["hasJournalSystem"] = MemberValue(history, "journalSystem") != null,
                ["missionAccomplished"] = MemberBool(history, false, "missionAccomplished"),
                ["createWithSandboxMode"] = MemberBool(history, false, "createWithSandboxMode"),
                ["hasUsedPropertyBanAchievement"] = MemberBool(history, false, "hasUsedPropertyBanAchievement")
            };
        }

        private static JsonObject CaptureGalaxy()
        {
            var galaxy = (object)GameMain.galaxy ?? GetGameDataMember("galaxy");
            if (galaxy == null)
            {
                return Unavailable("galaxy_missing");
            }

            var stars = MemberValue(galaxy, "stars") as Array;
            return new JsonObject
            {
                ["seed"] = MemberInt(galaxy, 0, "seed"),
                ["starCount"] = MemberInt(galaxy, stars?.Length ?? 0, "starCount"),
                ["habitableCount"] = MemberInt(galaxy, 0, "habitableCount"),
                ["birthStarId"] = MemberInt(galaxy, 0, "birthStarId"),
                ["birthPlanetId"] = MemberInt(galaxy, 0, "birthPlanetId"),
                ["unscannedStarCount"] = MemberInt(galaxy, 0, "unscannedStarCount"),
                ["needAutoScanning"] = MemberBool(galaxy, false, "_need_auto_scanning", "<_need_auto_scanning>k__BackingField"),
                ["scanPreparing"] = MemberBool(galaxy, false, "scan_preparing", "<scan_preparing>k__BackingField"),
                ["planetCount"] = TotalPlanetCount(stars),
                ["astroDataCount"] = CountOf(MemberValue(galaxy, "astrosData")),
                ["astroFactoryCount"] = CountOf(MemberValue(galaxy, "astrosFactory")),
                ["graphNodeCount"] = CountOf(MemberValue(galaxy, "graphNodes")),
                ["factoryPlanetCount"] = ReflectionReader.GetInt(GameMain.data, 0, "factoryCount"),
                ["stars"] = StarSummaries(stars, 128)
            };
        }

        private static JsonObject CaptureDysonSpheres()
        {
            var spheres = GetGameDataMember("dysonSpheres") as Array;
            if (spheres == null)
            {
                return Unavailable("dyson_spheres_missing");
            }

            var items = new List<object>();
            for (var i = 0; i < spheres.Length; i++)
            {
                var sphere = spheres.GetValue(i);
                if (sphere == null)
                {
                    continue;
                }

                items.Add(DysonSphereSummary(sphere, i));
            }

            return new JsonObject
            {
                ["capacity"] = spheres.Length,
                ["activeCount"] = items.Count,
                ["items"] = items
            };
        }

        private static JsonObject CaptureGalacticTransport()
        {
            var transport = GetGameDataMember("galacticTransport");
            if (transport == null)
            {
                return Unavailable("galactic_transport_missing");
            }

            var stationPool = MemberValue(transport, "stationPool") as Array;
            var stationSummary = StationSummaries(stationPool);
            return new JsonObject
            {
                ["stationCursor"] = MemberInt(transport, 0, "stationCursor"),
                ["stationCapacity"] = MemberInt(transport, stationPool?.Length ?? 0, "stationCapacity"),
                ["stationRecycleCursor"] = MemberInt(transport, 0, "stationRecycleCursor"),
                ["stationCount"] = Convert.ToInt32(stationSummary["count"]),
                ["stationsByPlanet"] = stationSummary["byPlanet"],
                ["stations"] = stationSummary["items"],
                ["remotePairCount"] = MemberInt(transport, 0, "remotePairCount"),
                ["stationToStationRouteCount"] = CountOf(MemberValue(transport, "station2stationRoutes")),
                ["astroToAstroRouteCount"] = CountOf(MemberValue(transport, "astro2astroRoutes")),
                ["astroToAstroBanCount"] = CountOf(MemberValue(transport, "astro2astroBans"))
            };
        }

        private static JsonObject CaptureSpaceSector()
        {
            var sector = (object)GameMain.spaceSector ?? GetGameDataMember("spaceSector");
            if (sector == null)
            {
                return Unavailable("space_sector_missing");
            }

            var astros = MemberValue(sector, "astros") as Array;
            var galaxyAstros = MemberValue(sector, "galaxyAstros") as Array;
            var enemyPool = MemberValue(sector, "enemyPool") as Array;
            var craftPool = MemberValue(sector, "craftPool") as Array;
            var dfHives = MemberValue(sector, "dfHives") as Array;
            var dfHivesByAstro = MemberValue(sector, "dfHivesByAstro") as Array;
            var spaceRuins = MemberValue(sector, "spaceRuins");

            return new JsonObject
            {
                ["isCombatMode"] = MemberBool(sector, false, "isCombatMode"),
                ["astroCursor"] = MemberInt(sector, 0, "astroCursor"),
                ["astroCount"] = ActiveReferenceCount(astros),
                ["astros"] = SpaceObjectSummaries(astros, SpaceObjectSampleLimit),
                ["galaxyAstroCount"] = ActiveReferenceCount(galaxyAstros),
                ["galaxyAstros"] = SpaceObjectSummaries(galaxyAstros, SpaceObjectSampleLimit),
                ["enemyCount"] = MemberInt(sector, 0, "enemyCount"),
                ["enemyCursor"] = MemberInt(sector, 0, "enemyCursor"),
                ["enemyCapacity"] = MemberInt(sector, ArrayLength(enemyPool), "enemyCapacity"),
                ["enemyRecycleCursor"] = MemberInt(sector, 0, "enemyRecycleCursor"),
                ["enemyRecycleCount"] = CountOf(MemberValue(sector, "enemyRecycle")),
                ["enemies"] = SpaceObjectSummaries(enemyPool, SpaceObjectSampleLimit),
                ["craftCount"] = MemberInt(sector, 0, "craftCount"),
                ["craftCursor"] = MemberInt(sector, 0, "craftCursor"),
                ["craftCapacity"] = MemberInt(sector, ArrayLength(craftPool), "craftCapacity"),
                ["craftRecycleCursor"] = MemberInt(sector, 0, "craftRecycleCursor"),
                ["craftRecycleCount"] = CountOf(MemberValue(sector, "craftRecycle")),
                ["crafts"] = SpaceObjectSummaries(craftPool, SpaceObjectSampleLimit),
                ["maxHiveCount"] = MemberInt(sector, 0, "maxHiveCount"),
                ["lastAliveHiveCount"] = MemberInt(sector, 0, "lastAliveHiveCount"),
                ["spaceRuins"] = SpacePoolSummary(spaceRuins, SpaceObjectSampleLimit),
                ["dfHiveCount"] = ActiveReferenceCount(dfHives),
                ["dfHiveByAstroCount"] = ActiveReferenceCount(dfHivesByAstro),
                ["dfHives"] = DfHiveSummaries(dfHives, SpaceObjectSampleLimit),
                ["enemyPoolCapacity"] = ArrayLength(enemyPool),
                ["craftPoolCapacity"] = ArrayLength(craftPool)
            };
        }

        private static JsonObject CaptureWarningSystem()
        {
            var warnings = GetGameDataMember("warningSystem");
            if (warnings == null)
            {
                return Unavailable("warning_system_missing");
            }

            var warningPool = MemberValue(warnings, "warningPool") as Array;
            return new JsonObject
            {
                ["warningTotalCount"] = MemberInt(warnings, 0, "warningTotalCount"),
                ["broadcastUIAlertCount"] = MemberInt(warnings, 0, "broadcastUIAlertCount"),
                ["hasCriticalWarning"] = MemberBool(warnings, false, "hasCriticalWarning"),
                ["criticalWarningTexts"] = MemberString(warnings, "criticalWarningTexts"),
                ["focusTargetId"] = MemberInt(warnings, 0, "focusTargetId"),
                ["focusSignalId"] = MemberInt(warnings, 0, "focusSignalId"),
                ["focusDetailSignalCount"] = MemberInt(warnings, 0, "focusDetailSignalCount"),
                ["warningSignalCount"] = MemberInt(warnings, 0, "warningSignalCount"),
                ["warningCursor"] = MemberInt(warnings, 0, "warningCursor"),
                ["warningCapacity"] = MemberInt(warnings, warningPool?.Length ?? 0, "warningCapacity"),
                ["warningRecycleCursor"] = MemberInt(warnings, 0, "warningRecycleCursor"),
                ["activeWarnings"] = WarningSummaries(warningPool, 64),
                ["activeBroadcasts"] = BroadcastSummaries(MemberValue(warnings, "broadcasts"), 64),
                ["criticalWarningCount"] = CountOf(MemberValue(warnings, "criticalWarnings")),
                ["broadcastCount"] = CountOf(MemberValue(warnings, "broadcasts")),
                ["broadcastConfigCount"] = CountOf(MemberValue(warnings, "broadcastConfigs")),
                ["warningCountEntries"] = CountOf(MemberValue(warnings, "warningCounts"))
            };
        }

        private static JsonObject CaptureTrashSystem()
        {
            var trash = GetGameDataMember("trashSystem");
            if (trash == null)
            {
                return Unavailable("trash_system_missing");
            }

            return new JsonObject
            {
                ["trashCount"] = MemberInt(trash, 0, "trashCount"),
                ["randSeed"] = MemberInt(trash, 0, "randSeed"),
                ["enemyDropBanCount"] = CountOf(MemberValue(trash, "enemyDropBans"))
            };
        }

        private static JsonObject CaptureGoalSystem()
        {
            var goals = GetGameDataMember("goalSystem");
            if (goals == null)
            {
                return Unavailable("goal_system_missing");
            }

            return new JsonObject
            {
                ["goalCount"] = CountOf(MemberValue(goals, "goalDatas")),
                ["queueCursor"] = MemberInt(goals, 0, "queueCursor"),
                ["queuedGoalIds"] = IntArray(MemberValue(goals, "goalQueue") as Array, 64, false)
            };
        }

        private static JsonObject CaptureMilestoneSystem()
        {
            var milestones = GetGameDataMember("milestoneSystem");
            if (milestones == null)
            {
                return Unavailable("milestone_system_missing");
            }

            return new JsonObject
            {
                ["milestoneCount"] = CountOf(MemberValue(milestones, "milestoneDatas"))
            };
        }

        private static JsonObject CaptureGameAchievement()
        {
            var achievement = GetGameDataMember("gameAchievement");
            var desc = GetGameDataMember("gameDesc");
            var history = (object)GameMain.history ?? GetGameDataMember("history");
            if (achievement == null)
            {
                return Unavailable("game_achievement_missing");
            }

            return new JsonObject
            {
                ["runtimeAsmLoaded"] = MemberValue(achievement, "runtimeAsm") != null,
                ["achievementEnable"] = MemberBool(desc, false, "achievementEnable"),
                ["runtimeDataCount"] = CountOf(MemberValue(achievement, "runtimeDatas")),
                ["propertyMultiplier"] = MemberDouble(desc, 0, "propertyMultiplier"),
                ["currentPropertyMultiplier"] = MemberDouble(history, 0, "currentPropertyMultiplier"),
                ["hasUsedPropertyBanAchievement"] = MemberBool(history, false, "hasUsedPropertyBanAchievement")
            };
        }

        private static object GetGameDataMember(params string[] names)
        {
            return GetMemberValue(GameMain.data, names);
        }

        private static object GetGameMainStaticMember(params string[] names)
        {
            var type = typeof(GameMain);
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var name in names)
            {
                var field = type.GetField(name, flags);
                if (field != null)
                {
                    try
                    {
                        return field.GetValue(null);
                    }
                    catch
                    {
                        continue;
                    }
                }

                var property = type.GetProperty(name, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    try
                    {
                        return property.GetValue(null, null);
                    }
                    catch
                    {
                        continue;
                    }
                }
            }

            return null;
        }

        private static object GetMemberValue(object target, params string[] names)
        {
            if (target == null)
            {
                return null;
            }

            var type = target.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var name in names)
            {
                var field = type.GetField(name, flags);
                if (field != null)
                {
                    try
                    {
                        return field.GetValue(target);
                    }
                    catch
                    {
                        continue;
                    }
                }

                var property = type.GetProperty(name, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    try
                    {
                        return property.GetValue(target, null);
                    }
                    catch
                    {
                        continue;
                    }
                }
            }

            return null;
        }

        private static object MemberValue(object target, params string[] names)
        {
            return GetMemberValue(target, names);
        }

        private static string MemberString(object target, params string[] names)
        {
            return MemberValue(target, names)?.ToString();
        }

        private static int MemberInt(object target, int defaultValue, params string[] names)
        {
            var value = MemberValue(target, names);
            if (value == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static long MemberLong(object target, long defaultValue, params string[] names)
        {
            var value = MemberValue(target, names);
            if (value == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToInt64(value);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static double MemberDouble(object target, double defaultValue, params string[] names)
        {
            var value = MemberValue(target, names);
            if (value == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToDouble(value);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static bool MemberBool(object target, bool defaultValue, params string[] names)
        {
            var value = MemberValue(target, names);
            if (value == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToBoolean(value);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static object NullableInt(object target, params string[] names)
        {
            var value = MemberValue(target, names);
            if (value == null)
            {
                return null;
            }

            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return null;
            }
        }

        private static object NullableDouble(object target, params string[] names)
        {
            var value = MemberValue(target, names);
            if (value == null)
            {
                return null;
            }

            try
            {
                return Convert.ToDouble(value);
            }
            catch
            {
                return null;
            }
        }

        private static object NullableBool(object target, params string[] names)
        {
            var value = MemberValue(target, names);
            if (value == null)
            {
                return null;
            }

            try
            {
                return Convert.ToBoolean(value);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsPositive(object value)
        {
            if (value == null)
            {
                return false;
            }

            try
            {
                return Convert.ToInt64(value) > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool EntityHasStatus(JsonObject item, string expectedStatus)
        {
            if (string.Equals(expectedStatus, "all", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!item.ContainsKey("status") || !(item["status"] is IEnumerable statuses))
            {
                return false;
            }

            foreach (var status in statuses)
            {
                if (string.Equals(status?.ToString(), expectedStatus, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool EntityInRadius(JsonObject item, double? x, double? y, double? z, double? radius)
        {
            if (!x.HasValue || !y.HasValue || !z.HasValue || !radius.HasValue)
            {
                return true;
            }

            if (!item.ContainsKey("position") || !(item["position"] is JsonObject position))
            {
                return false;
            }

            var dx = JsonDouble(position, "x", 0) - x.Value;
            var dy = JsonDouble(position, "y", 0) - y.Value;
            var dz = JsonDouble(position, "z", 0) - z.Value;
            return dx * dx + dy * dy + dz * dz <= radius.Value * radius.Value;
        }

        private static int JsonInt(JsonObject data, string key, int defaultValue)
        {
            if (data == null || !data.ContainsKey(key) || data[key] == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToInt32(data[key]);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static double JsonDouble(JsonObject data, string key, double defaultValue)
        {
            if (data == null || !data.ContainsKey(key) || data[key] == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToDouble(data[key]);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static bool JsonBool(JsonObject data, string key, bool defaultValue)
        {
            if (data == null || !data.ContainsKey(key) || data[key] == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToBoolean(data[key]);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static string JsonString(JsonObject data, string key, string defaultValue)
        {
            if (data == null || !data.ContainsKey(key) || data[key] == null)
            {
                return defaultValue;
            }

            return data[key].ToString();
        }

        private static int CountOf(object value)
        {
            if (value == null)
            {
                return 0;
            }

            if (value is Array array)
            {
                return array.Length;
            }

            if (value is ICollection collection)
            {
                return collection.Count;
            }

            return MemberInt(value, 0, "count", "Count", "length", "Length");
        }

        private static int ArrayLength(Array array)
        {
            return array?.Length ?? 0;
        }

        private static int ActiveReferenceCount(Array array)
        {
            if (array == null)
            {
                return 0;
            }

            var count = 0;
            for (var i = 0; i < array.Length; i++)
            {
                if (array.GetValue(i) != null)
                {
                    count++;
                }
            }

            return count;
        }

        private static List<object> IntArray(Array array, int limit, bool includeZeros)
        {
            var result = new List<object>();
            if (array == null)
            {
                return result;
            }

            for (var i = 0; i < array.Length && result.Count < limit; i++)
            {
                var value = Convert.ToInt32(array.GetValue(i));
                if (!includeZeros && value == 0)
                {
                    continue;
                }

                result.Add(value);
            }

            return result;
        }

        private static List<object> TechQueue(IEnumerable values)
        {
            var result = new List<object>();
            if (values == null)
            {
                return result;
            }

            foreach (var value in values)
            {
                var techId = Convert.ToInt32(value);
                if (techId <= 0)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["techId"] = techId,
                    ["name"] = TechName(techId)
                });
            }

            return result;
        }

        private static List<object> CaptureTechPrototypes()
        {
            var result = new List<object>();
            var values = ReflectionReader.Get(LDB.techs, "dataArray") as IEnumerable;
            if (values == null)
            {
                return result;
            }

            foreach (var value in values)
            {
                if (!(value is TechProto tech) || tech.ID <= 0)
                {
                    continue;
                }

                var history = GameMain.history;
                object techState = null;
                if (history != null)
                {
                    techState = history.TechState(tech.ID);
                }

                result.Add(new JsonObject
                {
                    ["id"] = tech.ID,
                    ["name"] = tech.name,
                    ["isHidden"] = tech.IsHiddenTech,
                    ["isLabTech"] = tech.IsLabTech,
                    ["unlocked"] = history != null && history.TechUnlocked(tech.ID),
                    ["inQueue"] = history != null && history.TechInQueue(tech.ID),
                    ["canEnqueue"] = history != null && history.CanEnqueueTech(tech.ID),
                    ["preTechs"] = IntArray(tech.PreTechs, 64, false),
                    ["preItems"] = IntArray(tech.PreItem, 64, false),
                    ["items"] = TechPrototypeItems(tech),
                    ["unlockRecipes"] = IntArray(tech.UnlockRecipes, 128, false),
                    ["unlockFunctions"] = IntArray(tech.UnlockFunctions, 128, false),
                    ["addItems"] = TechPrototypeAddItems(tech),
                    ["hashUploaded"] = ReflectionReader.GetLong(techState, 0, "hashUploaded"),
                    ["hashNeeded"] = ReflectionReader.GetLong(techState, SafeLong(() => tech.GetHashNeeded(0), 0), "hashNeeded"),
                    ["metadataBuyoutCost"] = TechMetadataBuyoutCosts(tech, techState)
                });
            }

            return result;
        }

        private static List<object> CaptureRecipePrototypes()
        {
            var result = new List<object>();
            var values = ReflectionReader.Get(LDB.recipes, "dataArray") as IEnumerable;
            if (values == null)
            {
                return result;
            }

            foreach (var value in values)
            {
                if (!(value is RecipeProto recipe) || recipe.ID <= 0)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["id"] = recipe.ID,
                    ["name"] = recipe.name,
                    ["type"] = recipe.Type.ToString(),
                    ["handcraft"] = recipe.Handcraft,
                    ["explicit"] = recipe.Explicit,
                    ["unlocked"] = GameMain.history != null && GameMain.history.RecipeUnlocked(recipe.ID),
                    ["items"] = RecipeItems(recipe.Items, recipe.ItemCounts),
                    ["results"] = RecipeItems(recipe.Results, recipe.ResultCounts)
                });
            }

            return result;
        }

        private static List<object> CaptureItemPrototypes()
        {
            var result = new List<object>();
            var values = ReflectionReader.Get(LDB.items, "dataArray") as IEnumerable;
            if (values == null)
            {
                return result;
            }

            foreach (var value in values)
            {
                if (!(value is ItemProto item) || item.ID <= 0)
                {
                    continue;
                }

                var desc = item.prefabDesc;
                result.Add(new JsonObject
                {
                    ["id"] = item.ID,
                    ["name"] = item.name,
                    ["type"] = item.Type.ToString(),
                    ["stackSize"] = item.StackSize,
                    ["isEntity"] = item.IsEntity,
                    ["canBuild"] = item.CanBuild,
                    ["unlocked"] = GameMain.history != null && GameMain.history.ItemUnlocked(item.ID),
                    ["handcraftRecipeId"] = item.handcraft?.ID ?? 0,
                    ["handcraftProductCount"] = item.handcraftProductCount,
                    ["isBelt"] = desc != null && desc.isBelt,
                    ["isInserter"] = desc != null && desc.isInserter,
                    ["isAssembler"] = desc != null && desc.isAssembler,
                    ["isPowerGen"] = desc != null && desc.isPowerGen
                });
            }

            return result;
        }

        private static List<object> TechPrototypeItems(TechProto tech)
        {
            var result = new List<object>();
            if (tech?.Items == null || tech.ItemPoints == null)
            {
                return result;
            }

            var itemCount = Math.Min(tech.Items.Length, tech.ItemPoints.Length);
            for (var i = 0; i < itemCount; i++)
            {
                var itemId = tech.Items[i];
                if (itemId <= 0)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["itemId"] = itemId,
                    ["itemName"] = ItemName(itemId),
                    ["points"] = tech.ItemPoints[i]
                });
            }

            return result;
        }

        private static List<object> TechMetadataBuyoutCosts(TechProto tech, object techState)
        {
            var result = new List<object>();
            if (tech == null)
            {
                return result;
            }

            var hashUploaded = ReflectionReader.GetLong(techState, 0, "hashUploaded");
            var hashNeeded = ReflectionReader.GetLong(techState, SafeLong(() => tech.GetHashNeeded(0), 0), "hashNeeded");
            var progress = hashNeeded <= 0 ? 1.0 : Math.Max(0.0, Math.Min(1.0, (double)hashUploaded / hashNeeded));
            if (tech.PropertyOverrideItemArray != null)
            {
                foreach (var entry in tech.PropertyOverrideItemArray)
                {
                    var itemId = ReflectionReader.GetInt(entry, 0, "id");
                    var overrideCount = ReflectionReader.GetInt(entry, 0, "count");
                    var required = (int)Math.Ceiling(overrideCount * (1.0 - progress));
                    AddTechMetadataBuyoutCost(result, itemId, required);
                }

                return result;
            }

            if (tech.Items == null || tech.ItemPoints == null)
            {
                return result;
            }

            var count = Math.Min(tech.Items.Length, tech.ItemPoints.Length);
            for (var i = 0; i < count; i++)
            {
                var itemId = tech.Items[i];
                if (itemId <= 0)
                {
                    continue;
                }

                var remainingHash = Math.Max(0, hashNeeded - hashUploaded);
                AddTechMetadataBuyoutCost(result, itemId, tech.ItemPoints[i] * remainingHash / 3600);
            }

            return result;
        }

        private static void AddTechMetadataBuyoutCost(List<object> result, int itemId, long required)
        {
            if (itemId <= 0)
            {
                return;
            }

            result.Add(new JsonObject
            {
                ["itemId"] = itemId,
                ["itemName"] = ItemName(itemId),
                ["required"] = required,
                ["available"] = MetadataAvailableProperty(itemId)
            });
        }

        private static long MetadataAvailableProperty(int itemId)
        {
            try
            {
                var propertySystem = DSPGame.propertySystem;
                var data = GameMain.data;
                if (propertySystem == null || data == null)
                {
                    return 0;
                }

                return propertySystem.GetItemAvaliableProperty(data.GetClusterSeedKey(), itemId);
            }
            catch
            {
                return 0;
            }
        }

        private static List<object> TechPrototypeAddItems(TechProto tech)
        {
            var result = new List<object>();
            if (tech?.AddItems == null || tech.AddItemCounts == null)
            {
                return result;
            }

            var count = Math.Min(tech.AddItems.Length, tech.AddItemCounts.Length);
            for (var i = 0; i < count; i++)
            {
                var itemId = tech.AddItems[i];
                if (itemId <= 0)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["itemId"] = itemId,
                    ["itemName"] = ItemName(itemId),
                    ["count"] = tech.AddItemCounts[i]
                });
            }

            return result;
        }

        private static List<object> RecipeItems(int[] itemIds, int[] counts)
        {
            var result = new List<object>();
            if (itemIds == null || counts == null)
            {
                return result;
            }

            var count = Math.Min(itemIds.Length, counts.Length);
            for (var i = 0; i < count; i++)
            {
                var itemId = itemIds[i];
                if (itemId <= 0 || counts[i] <= 0)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["itemId"] = itemId,
                    ["itemName"] = ItemName(itemId),
                    ["count"] = counts[i]
                });
            }

            return result;
        }

        private static List<object> StarSummaries(Array stars, int limit)
        {
            var result = new List<object>();
            if (stars == null)
            {
                return result;
            }

            for (var i = 0; i < stars.Length && result.Count < limit; i++)
            {
                var star = stars.GetValue(i);
                if (star == null)
                {
                    continue;
                }

                var planets = MemberValue(star, "planets") as Array;
                result.Add(new JsonObject
                {
                    ["id"] = MemberInt(star, 0, "id"),
                    ["index"] = MemberInt(star, i, "index"),
                    ["name"] = MemberString(star, "name"),
                    ["displayName"] = MemberString(star, "displayName"),
                    ["type"] = MemberValue(star, "type")?.ToString(),
                    ["spectr"] = MemberValue(star, "spectr")?.ToString(),
                    ["planetCount"] = MemberInt(star, planets?.Length ?? 0, "planetCount"),
                    ["luminosity"] = MemberDouble(star, 0, "luminosity"),
                    ["level"] = MemberDouble(star, 0, "level"),
                    ["planets"] = PlanetSummaries(planets)
                });
            }

            return result;
        }

        private static int TotalPlanetCount(Array stars)
        {
            if (stars == null)
            {
                return 0;
            }

            var count = 0;
            for (var i = 0; i < stars.Length; i++)
            {
                var star = stars.GetValue(i);
                var planets = MemberValue(star, "planets") as Array;
                count += MemberInt(star, planets?.Length ?? 0, "planetCount");
            }

            return count;
        }

        private static List<object> PlanetSummaries(Array planets)
        {
            var result = new List<object>();
            if (planets == null)
            {
                return result;
            }

            for (var i = 0; i < planets.Length; i++)
            {
                var planet = planets.GetValue(i);
                if (planet == null)
                {
                    continue;
                }

                var waterItemId = MemberInt(planet, 0, "waterItemId");
                result.Add(new JsonObject
                {
                    ["id"] = MemberInt(planet, 0, "id"),
                    ["index"] = MemberInt(planet, i, "index"),
                    ["number"] = MemberInt(planet, 0, "number"),
                    ["orbitIndex"] = MemberInt(planet, 0, "orbitIndex"),
                    ["orbitAround"] = MemberInt(planet, 0, "orbitAround"),
                    ["orbitAroundPlanetId"] = MemberInt(MemberValue(planet, "orbitAroundPlanet"), 0, "id"),
                    ["name"] = MemberString(planet, "name"),
                    ["displayName"] = MemberString(planet, "displayName"),
                    ["type"] = MemberValue(planet, "type")?.ToString(),
                    ["typeString"] = MemberString(planet, "typeString"),
                    ["singularity"] = MemberValue(planet, "singularity")?.ToString(),
                    ["themeId"] = MemberInt(planet, 0, "theme"),
                    ["themeName"] = ThemeName(MemberInt(planet, 0, "theme")),
                    ["algoId"] = MemberInt(planet, 0, "algoId"),
                    ["style"] = MemberInt(planet, 0, "style"),
                    ["seed"] = MemberInt(planet, 0, "seed"),
                    ["infoSeed"] = MemberInt(planet, 0, "infoSeed"),
                    ["radius"] = MemberDouble(planet, 0, "radius"),
                    ["realRadius"] = MemberDouble(planet, 0, "realRadius"),
                    ["orbitRadius"] = MemberDouble(planet, 0, "orbitRadius"),
                    ["sunDistance"] = MemberDouble(planet, 0, "sunDistance"),
                    ["orbitalPeriod"] = MemberDouble(planet, 0, "orbitalPeriod"),
                    ["rotationPeriod"] = MemberDouble(planet, 0, "rotationPeriod"),
                    ["windStrength"] = MemberDouble(planet, 0, "windStrength"),
                    ["luminosity"] = MemberDouble(planet, 0, "luminosity"),
                    ["landPercent"] = MemberDouble(planet, 0, "landPercent"),
                    ["waterItemId"] = waterItemId,
                    ["waterName"] = ItemName(waterItemId),
                    ["waterHeight"] = MemberDouble(planet, 0, "waterHeight"),
                    ["factoryIndex"] = MemberInt(planet, -1, "factoryIndex"),
                    ["hasFactory"] = MemberValue(planet, "factory") != null,
                    ["loaded"] = MemberBool(planet, false, "loaded"),
                    ["wanted"] = MemberBool(planet, false, "wanted"),
                    ["loading"] = MemberBool(planet, false, "loading"),
                    ["scanning"] = MemberBool(planet, false, "scanning"),
                    ["scanned"] = MemberBool(planet, false, "scanned"),
                    ["factoryLoaded"] = MemberBool(planet, false, "factoryLoaded"),
                    ["factoryLoading"] = MemberBool(planet, false, "factoryLoading"),
                    ["uPosition"] = VectorOrNull(MemberValue(planet, "uPosition")),
                    ["runtimePosition"] = VectorOrNull(MemberValue(planet, "runtimePosition")),
                    ["birthPoint"] = VectorOrNull(MemberValue(planet, "birthPoint")),
                    ["veinGroupCount"] = CountOf(MemberValue(planet, "runtimeVeinGroups", "veinGroups")),
                    ["veins"] = PlanetVeinGroups(planet),
                    ["gasItems"] = GasItems(planet)
                });
            }

            return result;
        }

        private static List<object> PlanetVeinGroups(object planet)
        {
            var result = new List<object>();
            var groups = MemberValue(planet, "runtimeVeinGroups", "veinGroups") as Array;
            if (groups == null)
            {
                return result;
            }

            for (var i = 0; i < groups.Length; i++)
            {
                var group = groups.GetValue(i);
                if (group == null)
                {
                    continue;
                }

                var count = MemberInt(group, 0, "count");
                var amount = MemberLong(group, 0, "amount");
                if (count <= 0 && amount <= 0)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["index"] = i,
                    ["type"] = MemberValue(group, "type")?.ToString(),
                    ["typeId"] = Convert.ToInt32(MemberValue(group, "type") ?? 0),
                    ["count"] = count,
                    ["amount"] = amount
                });
            }

            return result;
        }

        private static JsonObject DysonSphereSummary(object sphere, int index)
        {
            var stars = GameMain.galaxy == null ? null : MemberValue(GameMain.galaxy, "stars") as Array;
            var star = MemberValue(sphere, "starData") ?? (stars != null && index >= 0 && index < stars.Length ? stars.GetValue(index) : null);
            var layers = FirstArray(sphere, "layersIdBased", "layersSorted", "layerPool", "layers");
            var swarm = MemberValue(sphere, "swarm");

            return new JsonObject
            {
                ["index"] = index,
                ["starId"] = star == null ? 0 : MemberInt(star, 0, "id"),
                ["starName"] = star == null ? null : MemberString(star, "displayName", "name"),
                ["randSeed"] = MemberInt(sphere, 0, "randSeed"),
                ["defOrbitRadius"] = MemberDouble(sphere, 0, "defOrbitRadius"),
                ["minOrbitRadius"] = MemberDouble(sphere, 0, "minOrbitRadius"),
                ["maxOrbitRadius"] = MemberDouble(sphere, 0, "maxOrbitRadius"),
                ["avoidOrbitRadius"] = MemberDouble(sphere, 0, "avoidOrbitRadius"),
                ["grossRadius"] = MemberDouble(sphere, 0, "grossRadius"),
                ["gravity"] = MemberDouble(sphere, 0, "gravity"),
                ["layerCount"] = ActiveReferenceCount(layers),
                ["rocketCount"] = MemberInt(sphere, 0, "rocketCount"),
                ["rocketCursor"] = MemberInt(sphere, 0, "rocketCursor"),
                ["rocketCapacity"] = MemberInt(sphere, 0, "rocketCapacity"),
                ["rocketRecycleCursor"] = MemberInt(sphere, 0, "rocketRecycleCursor"),
                ["autoNodeCount"] = MemberInt(sphere, 0, "autoNodeCount"),
                ["nrdCursor"] = MemberInt(sphere, 0, "nrdCursor"),
                ["nrdCapacity"] = MemberInt(sphere, 0, "nrdCapacity"),
                ["energyGenCurrentTick"] = MemberLong(sphere, 0, "energyGenCurrentTick"),
                ["energyGenOriginalCurrentTick"] = MemberLong(sphere, 0, "energyGenOriginalCurrentTick"),
                ["energyReqCurrentTick"] = MemberLong(sphere, 0, "energyReqCurrentTick"),
                ["energyGenPerSail"] = MemberLong(sphere, 0, "energyGenPerSail"),
                ["energyGenPerNode"] = MemberLong(sphere, 0, "energyGenPerNode"),
                ["energyGenPerFrame"] = MemberLong(sphere, 0, "energyGenPerFrame"),
                ["energyGenPerShell"] = MemberLong(sphere, 0, "energyGenPerShell"),
                ["energyRespCoef"] = MemberDouble(sphere, 0, "energyRespCoef"),
                ["totalNodeCount"] = MemberInt(sphere, 0, "totalNodeCount"),
                ["totalConstructedNodeCount"] = MemberInt(sphere, 0, "totalConstructedNodeCount"),
                ["totalFrameCount"] = MemberInt(sphere, 0, "totalFrameCount"),
                ["totalConstructedFrameCount"] = MemberInt(sphere, 0, "totalConstructedFrameCount"),
                ["totalStructurePoint"] = MemberInt(sphere, 0, "totalStructurePoint"),
                ["totalConstructedStructurePoint"] = MemberInt(sphere, 0, "totalConstructedStructurePoint"),
                ["totalCellPoint"] = MemberLong(sphere, 0, "totalCellPoint"),
                ["totalConstructedCellPoint"] = MemberLong(sphere, 0, "totalConstructedCellPoint"),
                ["layers"] = DysonLayerSummaries(layers),
                ["swarm"] = DysonSwarmSummary(swarm)
            };
        }

        private static List<object> DysonLayerSummaries(Array layers)
        {
            var result = new List<object>();
            if (layers == null)
            {
                return result;
            }

            for (var i = 0; i < layers.Length; i++)
            {
                var layer = layers.GetValue(i);
                if (layer == null)
                {
                    continue;
                }

                var id = MemberInt(layer, 0, "id");
                if (id <= 0)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["id"] = id,
                    ["orbitRadius"] = MemberDouble(layer, 0, "orbitRadius"),
                    ["orbitAngularSpeed"] = MemberDouble(layer, 0, "orbitAngularSpeed"),
                    ["currentAngle"] = MemberDouble(layer, 0, "currentAngle"),
                    ["drawingGridMode"] = MemberInt(layer, 0, "drawingGridMode"),
                    ["paintingGridMode"] = MemberInt(layer, 0, "paintingGridMode"),
                    ["nodeCount"] = MemberInt(layer, 0, "nodeCount"),
                    ["nodeCursor"] = MemberInt(layer, 0, "nodeCursor"),
                    ["nodeCapacity"] = MemberInt(layer, 0, "nodeCapacity"),
                    ["nodeRecycleCursor"] = MemberInt(layer, 0, "nodeRecycleCursor"),
                    ["frameCount"] = MemberInt(layer, 0, "frameCount"),
                    ["frameCursor"] = MemberInt(layer, 0, "frameCursor"),
                    ["frameCapacity"] = MemberInt(layer, 0, "frameCapacity"),
                    ["frameRecycleCursor"] = MemberInt(layer, 0, "frameRecycleCursor"),
                    ["shellCount"] = MemberInt(layer, 0, "shellCount"),
                    ["shellCursor"] = MemberInt(layer, 0, "shellCursor"),
                    ["shellCapacity"] = MemberInt(layer, 0, "shellCapacity"),
                    ["shellRecycleCursor"] = MemberInt(layer, 0, "shellRecycleCursor"),
                    ["energyGenCurrentTick"] = MemberLong(layer, 0, "energyGenCurrentTick")
                });
            }

            return result;
        }

        private static JsonObject DysonSwarmSummary(object swarm)
        {
            if (swarm == null)
            {
                return null;
            }

            return new JsonObject
            {
                ["sailCount"] = MemberInt(swarm, 0, "sailCount"),
                ["sailCursor"] = MemberInt(swarm, 0, "sailCursor"),
                ["sailCapacity"] = MemberInt(swarm, 0, "sailCapacity"),
                ["sailRecycleCursor"] = MemberInt(swarm, 0, "sailRecycleCursor"),
                ["orbitCursor"] = MemberInt(swarm, 0, "orbitCursor"),
                ["orbitCapacity"] = MemberInt(swarm, 0, "orbitCapacity"),
                ["expiryCursor"] = MemberInt(swarm, 0, "expiryCursor"),
                ["expiryEnding"] = MemberInt(swarm, 0, "expiryEnding"),
                ["absorbCursor"] = MemberInt(swarm, 0, "absorbCursor"),
                ["absorbEnding"] = MemberInt(swarm, 0, "absorbEnding"),
                ["bulletCursor"] = MemberInt(swarm, 0, "bulletCursor"),
                ["bulletCapacity"] = MemberInt(swarm, 0, "bulletCapacity"),
                ["bulletRecycleCursor"] = MemberInt(swarm, 0, "bulletRecycleCursor"),
                ["energyGenCurrentTick"] = MemberLong(swarm, 0, "energyGenCurrentTick"),
                ["grossRadius"] = MemberDouble(swarm, 0, "grossRadius"),
                ["eternal"] = MemberBool(swarm, false, "eternal"),
                ["orbits"] = SailOrbitSummaries(MemberValue(swarm, "orbits") as Array)
            };
        }

        private static List<object> SailOrbitSummaries(Array orbits)
        {
            var result = new List<object>();
            if (orbits == null)
            {
                return result;
            }

            for (var i = 0; i < orbits.Length; i++)
            {
                var orbit = orbits.GetValue(i);
                var id = MemberInt(orbit, 0, "id");
                if (id <= 0)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["id"] = id,
                    ["radius"] = MemberDouble(orbit, 0, "radius"),
                    ["count"] = MemberInt(orbit, 0, "count"),
                    ["enabled"] = MemberBool(orbit, false, "enabled"),
                    ["rotation"] = QuaternionOrNull(MemberValue(orbit, "rotation"))
                });
            }

            return result;
        }

        private static JsonObject StationSummaries(Array stationPool)
        {
            var items = new List<object>();
            var byPlanet = new Dictionary<int, int>();
            var count = 0;

            if (stationPool != null)
            {
                for (var i = 0; i < stationPool.Length; i++)
                {
                    var station = stationPool.GetValue(i);
                    if (station == null)
                    {
                        continue;
                    }

                    var id = MemberInt(station, 0, "id");
                    if (id <= 0)
                    {
                        continue;
                    }

                    count++;
                    var planetId = MemberInt(station, 0, "planetId");
                    byPlanet.TryGetValue(planetId, out var planetCount);
                    byPlanet[planetId] = planetCount + 1;

                    items.Add(new JsonObject
                    {
                        ["index"] = i,
                        ["id"] = id,
                        ["gid"] = MemberInt(station, 0, "gid"),
                        ["entityId"] = MemberInt(station, 0, "entityId"),
                        ["planetId"] = planetId,
                        ["pcId"] = MemberInt(station, 0, "pcId"),
                        ["minerId"] = MemberInt(station, 0, "minerId"),
                        ["isStellar"] = MemberBool(station, false, "isStellar"),
                        ["isCollector"] = MemberBool(station, false, "isCollector"),
                        ["isVeinCollector"] = MemberBool(station, false, "isVeinCollector"),
                        ["energy"] = MemberLong(station, 0, "energy"),
                        ["energyPerTick"] = MemberLong(station, 0, "energyPerTick"),
                        ["energyMax"] = MemberLong(station, 0, "energyMax"),
                        ["warperCount"] = MemberInt(station, 0, "warperCount"),
                        ["warperMaxCount"] = MemberInt(station, 0, "warperMaxCount"),
                        ["idleDroneCount"] = MemberInt(station, 0, "idleDroneCount"),
                        ["workDroneCount"] = MemberInt(station, 0, "workDroneCount"),
                        ["idleShipCount"] = MemberInt(station, 0, "idleShipCount"),
                        ["workShipCount"] = MemberInt(station, 0, "workShipCount"),
                        ["renderShipCount"] = MemberInt(station, 0, "renderShipCount"),
                        ["localPairCount"] = MemberInt(station, 0, "localPairCount"),
                        ["remotePairTotalCount"] = MemberInt(station, 0, "remotePairTotalCount"),
                        ["tripRangeDrones"] = MemberDouble(station, 0, "tripRangeDrones"),
                        ["tripRangeShips"] = MemberDouble(station, 0, "tripRangeShips"),
                        ["includeOrbitCollector"] = MemberBool(station, false, "includeOrbitCollector"),
                        ["warpEnableDist"] = MemberDouble(station, 0, "warpEnableDist"),
                        ["warperNecessary"] = MemberBool(station, false, "warperNecessary"),
                        ["deliveryDrones"] = MemberInt(station, 0, "deliveryDrones"),
                        ["deliveryShips"] = MemberInt(station, 0, "deliveryShips"),
                        ["pilerCount"] = MemberInt(station, 0, "pilerCount"),
                        ["droneAutoReplenish"] = MemberBool(station, false, "droneAutoReplenish"),
                        ["shipAutoReplenish"] = MemberBool(station, false, "shipAutoReplenish"),
                        ["routePriority"] = MemberValue(station, "routePriority")?.ToString(),
                        ["storage"] = StationStorage(MemberValue(station, "storage") as Array),
                        ["slots"] = StationSlots(MemberValue(station, "slots") as Array),
                        ["localPairs"] = StationPairs(MemberValue(station, "localPairs") as Array),
                        ["remotePairs"] = StationPairs(MemberValue(station, "remotePairs") as Array),
                        ["needs"] = IntArray(MemberValue(station, "needs") as Array, 128, false),
                        ["collections"] = StationCollections(station)
                    });
                }
            }

            var byPlanetItems = new List<object>();
            foreach (var pair in byPlanet)
            {
                byPlanetItems.Add(new JsonObject
                {
                    ["planetId"] = pair.Key,
                    ["count"] = pair.Value
                });
            }

            return new JsonObject
            {
                ["count"] = count,
                ["items"] = items,
                ["byPlanet"] = byPlanetItems
            };
        }

        private static List<object> StationStorage(Array storage)
        {
            var result = new List<object>();
            if (storage == null)
            {
                return result;
            }

            for (var i = 0; i < storage.Length; i++)
            {
                var store = storage.GetValue(i);
                var itemId = MemberInt(store, 0, "itemId");
                if (itemId <= 0)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["index"] = i,
                    ["itemId"] = itemId,
                    ["name"] = ItemName(itemId),
                    ["count"] = MemberInt(store, 0, "count"),
                    ["inc"] = MemberInt(store, 0, "inc"),
                    ["localOrder"] = MemberInt(store, 0, "localOrder"),
                    ["remoteOrder"] = MemberInt(store, 0, "remoteOrder"),
                    ["max"] = MemberInt(store, 0, "max"),
                    ["keepMode"] = MemberInt(store, 0, "keepMode"),
                    ["keepIncRatio"] = MemberDouble(store, 0, "keepIncRatio"),
                    ["localLogic"] = MemberValue(store, "localLogic")?.ToString(),
                    ["remoteLogic"] = MemberValue(store, "remoteLogic")?.ToString()
                });
            }

            return result;
        }

        private static List<object> StationSlots(Array slots)
        {
            var result = new List<object>();
            if (slots == null)
            {
                return result;
            }

            for (var i = 0; i < slots.Length; i++)
            {
                var slot = slots.GetValue(i);
                var beltId = MemberInt(slot, 0, "beltId");
                var storageIdx = MemberInt(slot, 0, "storageIdx");
                if (beltId == 0 && storageIdx == 0)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["index"] = i,
                    ["dir"] = MemberValue(slot, "dir")?.ToString(),
                    ["beltId"] = beltId,
                    ["storageIdx"] = storageIdx,
                    ["counter"] = MemberInt(slot, 0, "counter")
                });
            }

            return result;
        }

        private static List<object> StationPairs(Array pairs)
        {
            var result = new List<object>();
            if (pairs == null)
            {
                return result;
            }

            for (var i = 0; i < pairs.Length; i++)
            {
                var pair = pairs.GetValue(i);
                var supplyId = MemberInt(pair, 0, "supplyId");
                var demandId = MemberInt(pair, 0, "demandId");
                if (supplyId == 0 && demandId == 0)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["index"] = i,
                    ["supplyId"] = supplyId,
                    ["supplyIndex"] = MemberInt(pair, 0, "supplyIndex"),
                    ["demandId"] = demandId,
                    ["demandIndex"] = MemberInt(pair, 0, "demandIndex"),
                    ["runtimeState"] = MemberInt(pair, 0, "runtimeState")
                });
            }

            return result;
        }

        private static List<object> StationCollections(object station)
        {
            var result = new List<object>();
            var ids = MemberValue(station, "collectionIds") as Array;
            var perTick = MemberValue(station, "collectionPerTick") as Array;
            var current = MemberValue(station, "currentCollections") as Array;
            if (ids == null)
            {
                return result;
            }

            for (var i = 0; i < ids.Length; i++)
            {
                var itemId = ArrayInt(ids, i, 0);
                if (itemId <= 0)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["itemId"] = itemId,
                    ["name"] = ItemName(itemId),
                    ["perTick"] = ArrayDouble(perTick, i, 0),
                    ["current"] = ArrayDouble(current, i, 0)
                });
            }

            return result;
        }

        private static List<object> SpaceObjectSummaries(Array pool, int limit)
        {
            var result = new List<object>();
            if (pool == null)
            {
                return result;
            }

            for (var i = 0; i < pool.Length && result.Count < limit; i++)
            {
                var item = pool.GetValue(i);
                if (item == null)
                {
                    continue;
                }

                var id = NullableInt(item, "id");
                var astroId = NullableInt(item, "astroId", "hiveAstroId");
                var protoId = NullableInt(item, "protoId");
                var modelIndex = NullableInt(item, "modelIndex");
                if (!IsPositive(id) && !IsPositive(astroId) && !IsPositive(protoId) && !IsPositive(modelIndex))
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["index"] = i,
                    ["id"] = id,
                    ["type"] = MemberValue(item, "type", "prototype")?.ToString(),
                    ["protoId"] = protoId,
                    ["modelIndex"] = modelIndex,
                    ["astroId"] = astroId,
                    ["originAstroId"] = NullableInt(item, "originAstroId"),
                    ["parentId"] = NullableInt(item, "parentId"),
                    ["owner"] = NullableInt(item, "owner"),
                    ["port"] = NullableInt(item, "port"),
                    ["stateFlags"] = NullableInt(item, "stateFlags"),
                    ["dynamic"] = NullableBool(item, "dynamic"),
                    ["isSpace"] = NullableBool(item, "isSpace"),
                    ["localized"] = NullableBool(item, "localized"),
                    ["uRadius"] = NullableDouble(item, "uRadius"),
                    ["position"] = VectorOrNull(MemberValue(item, "uPos", "uPosition", "pos", "position")),
                    ["nextPosition"] = VectorOrNull(MemberValue(item, "uPosNext", "uPositionNext")),
                    ["rotation"] = QuaternionOrNull(MemberValue(item, "uRot", "uRotation", "rot", "rotation")),
                    ["nextRotation"] = QuaternionOrNull(MemberValue(item, "uRotNext", "uRotationNext")),
                    ["velocity"] = VectorOrNull(MemberValue(item, "vel", "velocity"))
                });
            }

            return result;
        }

        private static JsonObject SpacePoolSummary(object pool, int limit)
        {
            if (pool == null)
            {
                return null;
            }

            var buffer = MemberValue(pool, "buffer") as Array;
            return new JsonObject
            {
                ["count"] = NullableInt(pool, "count", "Count"),
                ["cursor"] = NullableInt(pool, "cursor"),
                ["capacity"] = NullableInt(pool, "capacity") ?? (object)ArrayLength(buffer),
                ["recycleCursor"] = NullableInt(pool, "recycleCursor"),
                ["items"] = SpaceObjectSummaries(buffer, limit)
            };
        }

        private static List<object> DfHiveSummaries(Array hives, int limit)
        {
            var result = new List<object>();
            if (hives == null)
            {
                return result;
            }

            var visited = new HashSet<object>();
            for (var i = 0; i < hives.Length && result.Count < limit; i++)
            {
                var hive = hives.GetValue(i);
                while (hive != null && result.Count < limit && visited.Add(hive))
                {
                    var star = MemberValue(hive, "starData");
                    result.Add(new JsonObject
                    {
                        ["index"] = i,
                        ["starId"] = NullableInt(star, "id"),
                        ["starIndex"] = NullableInt(star, "index"),
                        ["starName"] = MemberString(star, "displayName", "name"),
                        ["hiveAstroId"] = NullableInt(hive, "hiveAstroId"),
                        ["hiveOrbitIndex"] = NullableInt(hive, "hiveOrbitIndex"),
                        ["orbitRadius"] = NullableDouble(hive, "orbitRadius"),
                        ["seed"] = NullableInt(hive, "seed"),
                        ["rtseed"] = NullableInt(hive, "rtseed"),
                        ["ticks"] = NullableInt(hive, "ticks"),
                        ["realized"] = NullableBool(hive, "realized"),
                        ["isEmpty"] = NullableBool(hive, "isEmpty"),
                        ["isPreview"] = NullableBool(hive, "isPreview"),
                        ["isLocal"] = NullableBool(hive, "isLocal"),
                        ["localPlayerInRange"] = NullableBool(hive, "local_player_in_range"),
                        ["playerUPosition"] = VectorOrNull(MemberValue(hive, "player_upos")),
                        ["playerLocalPosition"] = VectorOrNull(MemberValue(hive, "player_local_pos")),
                        ["rootEnemyId"] = NullableInt(hive, "rootEnemyId"),
                        ["idleRelayCount"] = NullableInt(hive, "idleRelayCount"),
                        ["idleTinderCount"] = NullableInt(hive, "idleTinderCount"),
                        ["tindersArrivingInTransit"] = NullableInt(hive, "tindersArrivingInTransit"),
                        ["currentIncomingAttackingUnitCount"] = NullableInt(hive, "currentIncomingAttackingUnitCount"),
                        ["currentIncomingAttackingPlayerUnitCount"] = NullableInt(hive, "currentIncomingAttackingPlayerUnitCount"),
                        ["currentIncomingAssaultingUnitCount"] = NullableInt(hive, "currentIncomingAssaultingUnitCount"),
                        ["currentReadyLancerCount"] = NullableInt(hive, "currentReadyLancerCount"),
                        ["matterProduction"] = NullableInt(hive, "matterProduction"),
                        ["matterConsumption"] = NullableInt(hive, "matterConsumption"),
                        ["evolve"] = EvolveSummary(MemberValue(hive, "evolve")),
                        ["builders"] = PoolSummary(MemberValue(hive, "builders")),
                        ["cores"] = PoolSummary(MemberValue(hive, "cores")),
                        ["nodes"] = PoolSummary(MemberValue(hive, "nodes")),
                        ["connectors"] = PoolSummary(MemberValue(hive, "connectors")),
                        ["replicators"] = PoolSummary(MemberValue(hive, "replicators")),
                        ["gammas"] = PoolSummary(MemberValue(hive, "gammas")),
                        ["turrets"] = PoolSummary(MemberValue(hive, "turrets")),
                        ["relays"] = PoolSummary(MemberValue(hive, "relays")),
                        ["tinders"] = PoolSummary(MemberValue(hive, "tinders")),
                        ["units"] = PoolSummary(MemberValue(hive, "units"))
                    });

                    hive = MemberValue(hive, "nextSibling");
                }
            }

            return result;
        }

        private static JsonObject EvolveSummary(object evolve)
        {
            if (evolve == null)
            {
                return null;
            }

            return new JsonObject
            {
                ["level"] = NullableInt(evolve, "level"),
                ["expl"] = NullableInt(evolve, "expl"),
                ["expf"] = NullableInt(evolve, "expf"),
                ["expp"] = NullableInt(evolve, "expp"),
                ["threat"] = NullableInt(evolve, "threat"),
                ["maxThreat"] = NullableInt(evolve, "maxThreat"),
                ["waves"] = NullableInt(evolve, "waves"),
                ["waveTicks"] = NullableInt(evolve, "waveTicks"),
                ["waveAsmTicks"] = NullableInt(evolve, "waveAsmTicks"),
                ["rankBase"] = NullableInt(evolve, "rankBase")
            };
        }

        private static List<object> WarningSummaries(Array warningPool, int limit)
        {
            var result = new List<object>();
            if (warningPool == null)
            {
                return result;
            }

            for (var i = 0; i < warningPool.Length && result.Count < limit; i++)
            {
                var warning = warningPool.GetValue(i);
                var id = MemberInt(warning, 0, "id");
                if (id <= 0)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["index"] = i,
                    ["id"] = id,
                    ["type"] = MemberValue(warning, "type", "warningType")?.ToString(),
                    ["signalId"] = MemberInt(warning, 0, "signalId"),
                    ["detailId1"] = MemberInt(warning, 0, "detailId1"),
                    ["detailId2"] = MemberInt(warning, 0, "detailId2"),
                    ["astroId"] = MemberInt(warning, 0, "astroId"),
                    ["factoryId"] = MemberInt(warning, 0, "factoryId"),
                    ["objectId"] = MemberInt(warning, 0, "objectId", "entityId"),
                    ["entityId"] = MemberInt(warning, 0, "entityId", "objectId"),
                    ["itemId"] = MemberInt(warning, 0, "itemId"),
                    ["localPosition"] = VectorOrNull(MemberValue(warning, "localPos", "localPosition"))
                });
            }

            return result;
        }

        private static List<object> BroadcastSummaries(object broadcasts, int limit)
        {
            var result = new List<object>();
            if (!(broadcasts is IEnumerable enumerable))
            {
                return result;
            }

            foreach (var entry in enumerable)
            {
                if (result.Count >= limit)
                {
                    break;
                }

                var broadcast = MemberValue(entry, "Value", "value") ?? entry;
                if (MemberValue(broadcast, "isNull") is bool isNull && isNull)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["vocal"] = MemberValue(broadcast, "vocal")?.ToString(),
                    ["context"] = MemberInt(broadcast, 0, "context"),
                    ["duration"] = MemberInt(broadcast, 0, "duration"),
                    ["lifeTime"] = MemberInt(broadcast, 0, "lifeTime"),
                    ["factoryIndex"] = MemberInt(broadcast, 0, "factoryIndex"),
                    ["astroId"] = MemberInt(broadcast, 0, "astroId"),
                    ["count"] = MemberInt(broadcast, 0, "count"),
                    ["localPosition"] = VectorOrNull(MemberValue(broadcast, "lpos", "localPos")),
                    ["focused"] = MemberBool(broadcast, false, "focused"),
                    ["hidden"] = MemberBool(broadcast, false, "hidden")
                });
            }

            return result;
        }

        private static JsonObject PoolSummary(object pool)
        {
            if (pool == null)
            {
                return Unavailable("pool_missing");
            }

            return new JsonObject
            {
                ["count"] = CountOf(pool),
                ["cursor"] = MemberInt(pool, 0, "cursor")
            };
        }

        private static Array FirstArray(object target, params string[] names)
        {
            foreach (var name in names)
            {
                var array = MemberValue(target, name) as Array;
                if (array != null)
                {
                    return array;
                }
            }

            return null;
        }

        private static object VectorOrNull(object value)
        {
            if (value is Vector3 vector3)
            {
                return Vector(vector3);
            }

            if (value is VectorLF3 vectorLf3)
            {
                return Vector(vectorLf3);
            }

            return null;
        }

        private static object QuaternionOrNull(object value)
        {
            if (value is Quaternion quaternion)
            {
                return new JsonObject
                {
                    ["x"] = quaternion.x,
                    ["y"] = quaternion.y,
                    ["z"] = quaternion.z,
                    ["w"] = quaternion.w
                };
            }

            return null;
        }

        private static object PoseOrNull(object value)
        {
            if (value is Pose pose)
            {
                return new JsonObject
                {
                    ["position"] = Vector(pose.position),
                    ["rotation"] = QuaternionOrNull(pose.rotation)
                };
            }

            return null;
        }

        private JsonObject CapturePlayer()
        {
            var player = GameMain.mainPlayer;
            if (player == null)
            {
                return Unavailable("main_player_missing");
            }

            return new JsonObject
            {
                ["planetId"] = GameMain.localPlanet?.id,
                ["starId"] = GameMain.localStar?.id,
                ["position"] = Vector(player.position),
                ["uPosition"] = Vector(player.uPosition),
                ["rotation"] = Vector(player.transform.eulerAngles),
                ["isOnPlanet"] = GameMain.localPlanet != null,
                ["movementState"] = player.movementState.ToString(),
                ["buildRange"] = player.mecha?.buildArea,
                ["interactionRange"] = ReflectionReader.GetDouble(player.mecha, 0, "reactorArea", "actionArea", "interactArea")
            };
        }

        private JsonObject CaptureMecha()
        {
            var player = GameMain.mainPlayer;
            var mecha = player?.mecha;
            if (mecha == null)
            {
                return Unavailable("mecha_missing");
            }

            var construction = mecha.constructionModule;
            return new JsonObject
            {
                ["hp"] = mecha.hp,
                ["hpMax"] = mecha.hpMaxApplied,
                ["coreEnergy"] = mecha.coreEnergy,
                ["coreEnergyCap"] = mecha.coreEnergyCap,
                ["reactorEnergy"] = mecha.reactorEnergy,
                ["sandCount"] = player.sandCount,
                ["buildArea"] = mecha.buildArea,
                ["constructionDroneEnabled"] = construction.droneEnabled,
                ["constructionDroneCount"] = ReflectionReader.GetInt(construction, 0, "droneCount", "droneCursor"),
                ["idleConstructionDroneCount"] = ReflectionReader.GetInt(construction, 0, "idleDroneCount"),
                ["workingConstructionDroneCount"] = ReflectionReader.GetInt(construction, 0, "workDroneCount", "workingDroneCount")
            };
        }

        private JsonObject CaptureInventory()
        {
            var package = GameMain.mainPlayer?.package;
            if (package == null)
            {
                return Unavailable("inventory_missing");
            }

            var items = new List<object>();
            var byItem = new Dictionary<int, int>();
            var byItemInc = new Dictionary<int, int>();
            var byItemSlots = new Dictionary<int, int>();
            var emptySlots = 0;
            var lockedSlots = 0;
            var gridLength = package.grids?.Length ?? 0;
            var slotCount = Math.Max(package.size, gridLength);

            for (var i = 0; i < slotCount; i++)
            {
                if (i >= gridLength)
                {
                    if (i < package.size)
                    {
                        emptySlots++;
                    }

                    continue;
                }

                var grid = package.grids[i];
                if (grid.itemId <= 0 || grid.count <= 0)
                {
                    if (i < package.size)
                    {
                        emptySlots++;
                    }
                    else
                    {
                        lockedSlots++;
                    }

                    continue;
                }

                items.Add(new JsonObject
                {
                    ["slot"] = i,
                    ["locked"] = i >= package.size,
                    ["itemId"] = grid.itemId,
                    ["name"] = ItemName(grid.itemId),
                    ["count"] = grid.count,
                    ["stackSize"] = grid.stackSize,
                    ["inc"] = grid.inc
                });

                byItem.TryGetValue(grid.itemId, out var count);
                byItem[grid.itemId] = count + grid.count;
                byItemInc.TryGetValue(grid.itemId, out var inc);
                byItemInc[grid.itemId] = inc + grid.inc;
                byItemSlots.TryGetValue(grid.itemId, out var slots);
                byItemSlots[grid.itemId] = slots + 1;
            }

            return new JsonObject
            {
                ["size"] = package.size,
                ["gridLength"] = gridLength,
                ["slotCount"] = slotCount,
                ["emptySlots"] = emptySlots,
                ["lockedSlots"] = lockedSlots,
                ["nonEmptySlots"] = items.Count,
                ["totalItemCount"] = TotalItemCount(byItem),
                ["items"] = items,
                ["summary"] = ItemSummary(byItem, byItemInc, byItemSlots)
            };
        }

        private JsonObject CaptureForge()
        {
            var forge = GameMain.mainPlayer?.mecha?.forge;
            if (forge == null)
            {
                return Unavailable("forge_missing");
            }

            var tasks = MemberValue(forge, "tasks") as IEnumerable;
            return new JsonObject
            {
                ["taskCount"] = CountOf(MemberValue(forge, "tasks")),
                ["totalTime"] = MemberDouble(forge, 0, "totalTime"),
                ["actualReplicateSpeed"] = MemberDouble(forge, 0, "actualReplicateSpeed"),
                ["extraItems"] = ItemBundleItems(MemberValue(forge, "extraItems")),
                ["bottleneckItems"] = IntEnumerable(MemberValue(forge, "bottleneckItems") as IEnumerable, 128),
                ["tasks"] = ForgeTaskSummaries(tasks)
            };
        }

        private static List<object> ForgeTaskSummaries(IEnumerable tasks)
        {
            var result = new List<object>();
            if (tasks == null)
            {
                return result;
            }

            var index = 0;
            foreach (var task in tasks)
            {
                if (task != null)
                {
                    result.Add(ForgeTaskSummary(task, index));
                }

                index++;
            }

            return result;
        }

        private static JsonObject ForgeTaskSummary(object task, int index)
        {
            var recipeId = MemberInt(task, 0, "recipeId");
            var tick = MemberInt(task, 0, "tick");
            var tickSpend = MemberInt(task, 0, "tickSpend");
            return new JsonObject
            {
                ["index"] = index,
                ["recipeId"] = recipeId,
                ["recipeName"] = RecipeName(recipeId),
                ["count"] = MemberInt(task, 0, "count"),
                ["tick"] = tick,
                ["tickSpend"] = tickSpend,
                ["progress"] = tickSpend <= 0 ? 0 : Math.Min(1.0, (double)tick / tickSpend),
                ["parentTaskIndex"] = MemberInt(task, -1, "parentTaskIndex"),
                ["itemEnough"] = MemberBool(task, false, "itemEnough"),
                ["productEmpty"] = MemberBool(task, false, "productEmpty"),
                ["ingredients"] = ForgeIngredients(task),
                ["products"] = ForgeProducts(task)
            };
        }

        private static List<object> ForgeIngredients(object task)
        {
            var result = new List<object>();
            var itemIds = MemberValue(task, "itemIds") as Array;
            var itemCounts = MemberValue(task, "itemCounts") as Array;
            var served = MemberValue(task, "served") as Array;
            if (itemIds == null)
            {
                return result;
            }

            for (var i = 0; i < itemIds.Length; i++)
            {
                var itemId = ArrayInt(itemIds, i, 0);
                result.Add(new JsonObject
                {
                    ["itemId"] = itemId,
                    ["name"] = ItemName(itemId),
                    ["required"] = ArrayInt(itemCounts, i, 0),
                    ["served"] = ArrayInt(served, i, 0)
                });
            }

            return result;
        }

        private static List<object> ForgeProducts(object task)
        {
            var result = new List<object>();
            var productIds = MemberValue(task, "productIds") as Array;
            var productCounts = MemberValue(task, "productCounts") as Array;
            var produced = MemberValue(task, "produced") as Array;
            if (productIds == null)
            {
                return result;
            }

            for (var i = 0; i < productIds.Length; i++)
            {
                var itemId = ArrayInt(productIds, i, 0);
                result.Add(new JsonObject
                {
                    ["itemId"] = itemId,
                    ["name"] = ItemName(itemId),
                    ["count"] = ArrayInt(productCounts, i, 0),
                    ["produced"] = ArrayInt(produced, i, 0)
                });
            }

            return result;
        }

        private static JsonObject CaptureResearch()
        {
            var history = GameMain.history;
            if (history == null)
            {
                return Unavailable("history_missing");
            }

            var queue = new List<object>();
            var rawQueue = ReflectionReader.Get(history, "techQueue") as IEnumerable;
            if (rawQueue != null)
            {
                foreach (var item in rawQueue)
                {
                    var techId = Convert.ToInt32(item);
                    if (techId > 0)
                    {
                        queue.Add(new JsonObject
                        {
                            ["techId"] = techId,
                            ["name"] = TechName(techId)
                        });
                    }
                }
            }

            var currentTech = ReflectionReader.GetInt(history, 0, "currentTech");
            object currentState = null;
            if (currentTech > 0)
            {
                currentState = history.TechState(currentTech);
            }

            return new JsonObject
            {
                ["currentTechId"] = currentTech,
                ["currentTechName"] = TechName(currentTech),
                ["hashUploaded"] = ReflectionReader.GetLong(currentState, 0, "hashUploaded"),
                ["hashNeeded"] = ReflectionReader.GetLong(currentState, 0, "hashNeeded"),
                ["queueLength"] = ReflectionReader.GetInt(history, queue.Count, "techQueueLength"),
                ["queue"] = queue,
                ["techHashedFor10Frames"] = GameMain.statistics?.techHashedFor10Frames ?? 0,
                ["isStalled"] = currentTech > 0 && (GameMain.statistics?.techHashedFor10Frames ?? 0) == 0
            };
        }

        private JsonObject CaptureLocalPlanet(JsonObject factoryDetails)
        {
            var planet = GameMain.localPlanet;
            if (planet == null)
            {
                return Unavailable("local_planet_missing");
            }

            return new JsonObject
            {
                ["id"] = planet.id,
                ["name"] = planet.displayName,
                ["type"] = planet.typeString,
                ["radius"] = planet.radius,
                ["realRadius"] = planet.realRadius,
                ["starId"] = planet.star?.id,
                ["starName"] = planet.star?.displayName,
                ["windEnergyFactor"] = planet.windStrength,
                ["solarEnergyFactor"] = planet.luminosity,
                ["factoryIndex"] = planet.factoryIndex,
                ["loaded"] = planet.loaded,
                ["factoryLoaded"] = planet.factoryLoaded,
                ["factory"] = LocalPlanetFactoryOverview(factoryDetails)
            };
        }

        private static JsonObject CaptureLocalPlanetFactories()
        {
            var planet = GameMain.localPlanet;
            var factory = planet?.factory;
            if (factory == null)
            {
                return Unavailable("local_planet_factory_missing");
            }

            var items = new List<object>();
            var byProto = new Dictionary<int, int>();
            var byStatus = new Dictionary<string, int>();
            var entityCount = 0;

            for (var i = 1; i < factory.entityCursor; i++)
            {
                var entity = factory.entityPool[i];
                if (entity.id != i)
                {
                    continue;
                }

                entityCount++;
                byProto.TryGetValue(entity.protoId, out var protoCount);
                byProto[entity.protoId] = protoCount + 1;

                var item = FactoryEntityDetail(factory, entity);
                foreach (var status in (List<object>)item["status"])
                {
                    var key = status.ToString();
                    byStatus.TryGetValue(key, out var statusCount);
                    byStatus[key] = statusCount + 1;
                }

                items.Add(item);
            }

            return new JsonObject
            {
                ["planetId"] = planet.id,
                ["planetName"] = planet.displayName,
                ["factoryIndex"] = planet.factoryIndex,
                ["entityCount"] = entityCount,
                ["entityCursor"] = factory.entityCursor,
                ["items"] = items,
                ["buildingSummary"] = ItemSummary(byProto),
                ["statusSummary"] = StatusSummary(byStatus),
                ["beltCount"] = ReflectionReader.GetInt(factory.cargoTraffic, 0, "beltCursor"),
                ["sorterCount"] = ReflectionReader.GetInt(factory.cargoTraffic, 0, "sorterCursor")
            };
        }

        private static JsonObject LocalPlanetFactoryOverview(JsonObject factoryDetails)
        {
            if (factoryDetails == null)
            {
                return null;
            }

            return new JsonObject
            {
                ["factoryIndex"] = factoryDetails["factoryIndex"],
                ["planetId"] = factoryDetails["planetId"],
                ["entityCount"] = factoryDetails["entityCount"],
                ["entityCursor"] = factoryDetails["entityCursor"],
                ["buildingSummary"] = factoryDetails["buildingSummary"],
                ["statusSummary"] = factoryDetails["statusSummary"],
                ["beltCount"] = factoryDetails["beltCount"],
                ["sorterCount"] = factoryDetails["sorterCount"]
            };
        }

        private static JsonObject FactoryEntityDetail(PlanetFactory factory, EntityData entity)
        {
            var status = new List<object>();
            var component = FactoryEntityComponent(factory, entity, status);
            return new JsonObject
            {
                ["entityId"] = entity.id,
                ["protoId"] = entity.protoId,
                ["name"] = ItemName(entity.protoId),
                ["modelIndex"] = entity.modelIndex,
                ["position"] = Vector(entity.pos),
                ["powerNodeId"] = entity.powerNodeId,
                ["beltId"] = entity.beltId,
                ["minerId"] = entity.minerId,
                ["inserterId"] = entity.inserterId,
                ["assemblerId"] = entity.assemblerId,
                ["fractionatorId"] = entity.fractionatorId,
                ["ejectorId"] = entity.ejectorId,
                ["siloId"] = entity.siloId,
                ["labId"] = entity.labId,
                ["stationId"] = entity.stationId,
                ["storageId"] = entity.storageId,
                ["tankId"] = entity.tankId,
                ["component"] = component,
                ["status"] = status
            };
        }

        private static JsonObject FactoryEntityComponent(PlanetFactory factory, EntityData entity, List<object> status)
        {
            var system = factory.factorySystem;
            if (system == null)
            {
                return null;
            }

            if (entity.assemblerId > 0 && system.assemblerPool != null && entity.assemblerId < system.assemblerPool.Length)
            {
                var assembler = system.assemblerPool[entity.assemblerId];
                if (assembler.id == entity.assemblerId)
                {
                    if (assembler.recipeId <= 0)
                    {
                        AddStatus(status, "noRecipe");
                    }

                    if (HasShortage(assembler.needs, assembler.served))
                    {
                        AddStatus(status, "materialShortage");
                    }

                    if (HasPositive(assembler.produced))
                    {
                        AddStatus(status, "outputBlocked");
                    }

                    return new JsonObject
                    {
                        ["type"] = "assembler",
                        ["id"] = assembler.id,
                        ["recipeId"] = assembler.recipeId,
                        ["recipeName"] = RecipeName(assembler.recipeId),
                        ["replicating"] = assembler.replicating,
                        ["time"] = assembler.time,
                        ["speed"] = assembler.speed,
                        ["needs"] = IntArray(assembler.needs, 32, true),
                        ["served"] = IntArray(assembler.served, 32, true),
                        ["produced"] = IntArray(assembler.produced, 32, true)
                    };
                }
            }

            if (entity.labId > 0 && system.labPool != null && entity.labId < system.labPool.Length)
            {
                var lab = system.labPool[entity.labId];
                if (lab.id == entity.labId)
                {
                    if (!lab.researchMode && lab.recipeId <= 0)
                    {
                        AddStatus(status, "noRecipe");
                    }

                    if (lab.researchMode && lab.techId <= 0)
                    {
                        AddStatus(status, "noResearch");
                    }

                    if (HasShortage(lab.needs, lab.served))
                    {
                        AddStatus(status, "materialShortage");
                    }

                    if (HasPositive(lab.produced))
                    {
                        AddStatus(status, "outputBlocked");
                    }

                    return new JsonObject
                    {
                        ["type"] = "lab",
                        ["id"] = lab.id,
                        ["researchMode"] = lab.researchMode,
                        ["recipeId"] = lab.recipeId,
                        ["recipeName"] = RecipeName(lab.recipeId),
                        ["techId"] = lab.techId,
                        ["techName"] = TechName(lab.techId),
                        ["replicating"] = lab.replicating,
                        ["time"] = lab.time,
                        ["speed"] = lab.speed,
                        ["needs"] = IntArray(lab.needs, 32, true),
                        ["served"] = IntArray(lab.served, 32, true),
                        ["produced"] = IntArray(lab.produced, 32, true),
                        ["matrixServed"] = IntArray(lab.matrixServed, 32, true)
                    };
                }
            }

            if (entity.minerId > 0 && system.minerPool != null && entity.minerId < system.minerPool.Length)
            {
                var miner = system.minerPool[entity.minerId];
                if (miner.id == entity.minerId)
                {
                    if (miner.productCount >= 50)
                    {
                        AddStatus(status, "outputBlocked");
                    }

                    return new JsonObject
                    {
                        ["type"] = "miner",
                        ["id"] = miner.id,
                        ["minerType"] = miner.type.ToString(),
                        ["workState"] = miner.workstate.ToString(),
                        ["productId"] = miner.productId,
                        ["productName"] = ItemName(miner.productId),
                        ["productCount"] = miner.productCount,
                        ["veinCount"] = miner.veinCount,
                        ["totalVeinAmount"] = miner.totalVeinAmount
                    };
                }
            }

            if (entity.fractionatorId > 0 && system.fractionatorPool != null && entity.fractionatorId < system.fractionatorPool.Length)
            {
                var fractionator = system.fractionatorPool[entity.fractionatorId];
                if (fractionator.id == entity.fractionatorId)
                {
                    if (fractionator.fluidInputCount <= 0)
                    {
                        AddStatus(status, "materialShortage");
                    }

                    if (fractionator.productOutputCount >= fractionator.productOutputMax ||
                        fractionator.fluidOutputCount >= fractionator.fluidOutputMax)
                    {
                        AddStatus(status, "outputBlocked");
                    }

                    return new JsonObject
                    {
                        ["type"] = "fractionator",
                        ["id"] = fractionator.id,
                        ["isWorking"] = fractionator.isWorking,
                        ["fluidId"] = fractionator.fluidId,
                        ["fluidName"] = ItemName(fractionator.fluidId),
                        ["productId"] = fractionator.productId,
                        ["productName"] = ItemName(fractionator.productId),
                        ["fluidInputCount"] = fractionator.fluidInputCount,
                        ["productOutputCount"] = fractionator.productOutputCount,
                        ["productOutputMax"] = fractionator.productOutputMax
                    };
                }
            }

            if (entity.ejectorId > 0 && system.ejectorPool != null && entity.ejectorId < system.ejectorPool.Length)
            {
                var ejector = system.ejectorPool[entity.ejectorId];
                if (ejector.id == entity.ejectorId)
                {
                    if (ejector.bulletId > 0 && ejector.bulletCount <= 0)
                    {
                        AddStatus(status, "materialShortage");
                    }

                    return new JsonObject
                    {
                        ["type"] = "ejector",
                        ["id"] = ejector.id,
                        ["bulletId"] = ejector.bulletId,
                        ["bulletName"] = ItemName(ejector.bulletId),
                        ["bulletCount"] = ejector.bulletCount,
                        ["orbitId"] = ejector.orbitId,
                        ["targetState"] = ejector.targetState.ToString()
                    };
                }
            }

            if (entity.siloId > 0 && system.siloPool != null && entity.siloId < system.siloPool.Length)
            {
                var silo = system.siloPool[entity.siloId];
                if (silo.id == entity.siloId)
                {
                    if (silo.bulletId > 0 && silo.bulletCount <= 0)
                    {
                        AddStatus(status, "materialShortage");
                    }

                    return new JsonObject
                    {
                        ["type"] = "silo",
                        ["id"] = silo.id,
                        ["bulletId"] = silo.bulletId,
                        ["bulletName"] = ItemName(silo.bulletId),
                        ["bulletCount"] = silo.bulletCount,
                        ["hasNode"] = silo.hasNode
                    };
                }
            }

            if (entity.stationId > 0)
            {
                return new JsonObject
                {
                    ["type"] = "station",
                    ["id"] = entity.stationId
                };
            }

            return null;
        }

        private JsonObject CaptureProduction()
        {
            var stat = CurrentFactoryStat();
            if (stat == null)
            {
                return Unavailable("production_stat_missing");
            }

            return new JsonObject
            {
                ["productRegister"] = NonZeroArray(ReflectionReader.Get(stat, "productRegister") as Array, 120),
                ["consumeRegister"] = NonZeroArray(ReflectionReader.Get(stat, "consumeRegister") as Array, 120),
                ["powerGenerationRegister"] = ReflectionReader.GetLong(stat, 0, "powerGenRegister"),
                ["powerConsumptionRegister"] = ReflectionReader.GetLong(stat, 0, "powerConRegister"),
                ["powerChargingRegister"] = ReflectionReader.GetLong(stat, 0, "powerChaRegister"),
                ["powerDischargingRegister"] = ReflectionReader.GetLong(stat, 0, "powerDisRegister")
            };
        }

        private JsonObject CapturePower()
        {
            var factory = GameMain.localPlanet?.factory;
            var stat = CurrentFactoryStat();
            if (factory == null)
            {
                return Unavailable("factory_missing");
            }

            long storedEnergy = 0;
            var powerSystem = factory.powerSystem;
            for (var i = 1; i < powerSystem.netCursor; i++)
            {
                storedEnergy += powerSystem.netPool[i].energyStored;
            }

            return new JsonObject
            {
                ["networkCount"] = powerSystem.netCursor,
                ["storedEnergy"] = storedEnergy,
                ["generationRegister"] = stat == null ? 0 : ReflectionReader.GetLong(stat, 0, "powerGenRegister"),
                ["consumptionRegister"] = stat == null ? 0 : ReflectionReader.GetLong(stat, 0, "powerConRegister"),
                ["chargingRegister"] = stat == null ? 0 : ReflectionReader.GetLong(stat, 0, "powerChaRegister"),
                ["dischargingRegister"] = stat == null ? 0 : ReflectionReader.GetLong(stat, 0, "powerDisRegister")
            };
        }

        private static List<object> NonZeroArray(Array values, int limit)
        {
            var result = new List<object>();
            if (values == null)
            {
                return result;
            }

            for (var i = 0; i < values.Length && result.Count < limit; i++)
            {
                var value = Convert.ToInt64(values.GetValue(i));
                if (value == 0)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["itemId"] = i,
                    ["name"] = ItemName(i),
                    ["value"] = value
                });
            }

            return result;
        }

        private static object CurrentFactoryStat()
        {
            var planet = GameMain.localPlanet;
            if (planet == null || GameMain.statistics?.production?.factoryStatPool == null)
            {
                return null;
            }

            var index = planet.factoryIndex;
            var pool = GameMain.statistics.production.factoryStatPool;
            return index >= 0 && index < pool.Length ? pool[index] : null;
        }

        private static bool IsGameLoaded()
        {
            return IsGameSessionLoaded();
        }

        private static bool IsGameSessionLoaded()
        {
            return GetSessionUnavailableReason() == null;
        }

        private static string GetSessionUnavailableReason()
        {
            try
            {
                if (!IsPreloadReady())
                {
                    return "game_preloading";
                }

                if (GameMain.data == null)
                {
                    return IsGameStartRequested() ? "game_loading" : "game_data_missing";
                }

                if (GameMain.isLoading)
                {
                    return "game_loading";
                }

                if (GameMain.isEnded)
                {
                    return "game_ended";
                }

                if (GameMain.loadErrored)
                {
                    return "game_load_errored";
                }

                if (DSPGame.IsMenuDemo || IsGameMainMenuDemo())
                {
                    return "menu_demo";
                }

                return null;
            }
            catch (Exception ex)
            {
                return "exception_" + ex.GetType().Name;
            }
        }

        private static bool IsPreloadReady()
        {
            try
            {
                return VFPreload.done && VFPreload.dbDone;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsGameStartRequested()
        {
            return SafeValue(() => DSPGame.GameDesc) != null ||
                !string.IsNullOrEmpty(SafeValue(() => DSPGame.LoadFile) as string);
        }

        private static bool IsGameMainMenuDemo()
        {
            try
            {
                return ReflectionReader.GetBool(GameMain.instance, false, "isMenuDemo");
            }
            catch
            {
                return false;
            }
        }

        private static int TotalItemCount(Dictionary<int, int> items)
        {
            var total = 0;
            foreach (var pair in items)
            {
                total += pair.Value;
            }

            return total;
        }

        private static JsonObject ItemSummary(Dictionary<int, int> items)
        {
            return ItemSummary(items, null, null);
        }

        private static JsonObject ItemSummary(Dictionary<int, int> items, Dictionary<int, int> incs, Dictionary<int, int> slots)
        {
            var result = new List<object>();
            foreach (var pair in items)
            {
                var item = new JsonObject
                {
                    ["itemId"] = pair.Key,
                    ["name"] = ItemName(pair.Key),
                    ["count"] = pair.Value
                };

                if (incs != null)
                {
                    incs.TryGetValue(pair.Key, out var inc);
                    item["inc"] = inc;
                }

                if (slots != null)
                {
                    slots.TryGetValue(pair.Key, out var slotCount);
                    item["slots"] = slotCount;
                }

                result.Add(item);
            }

            return new JsonObject
            {
                ["items"] = result
            };
        }

        private static JsonObject StatusSummary(Dictionary<string, int> statuses)
        {
            var result = new List<object>();
            foreach (var pair in statuses)
            {
                result.Add(new JsonObject
                {
                    ["status"] = pair.Key,
                    ["count"] = pair.Value
                });
            }

            return new JsonObject
            {
                ["items"] = result
            };
        }

        private static void AddStatus(List<object> statuses, string status)
        {
            if (!statuses.Contains(status))
            {
                statuses.Add(status);
            }
        }

        private static bool HasShortage(int[] needs, int[] served)
        {
            if (needs == null || served == null)
            {
                return false;
            }

            var length = Math.Min(needs.Length, served.Length);
            for (var i = 0; i < length; i++)
            {
                if (needs[i] > 0 && served[i] < needs[i])
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasPositive(int[] values)
        {
            if (values == null)
            {
                return false;
            }

            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static List<object> GasItems(object planet)
        {
            var result = new List<object>();
            var itemIds = MemberValue(planet, "gasItems") as Array;
            var speeds = MemberValue(planet, "gasSpeeds") as Array;
            var heatValues = MemberValue(planet, "gasHeatValues") as Array;
            if (itemIds == null)
            {
                return result;
            }

            for (var i = 0; i < itemIds.Length; i++)
            {
                var itemId = ArrayInt(itemIds, i, 0);
                if (itemId <= 0)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["itemId"] = itemId,
                    ["name"] = ItemName(itemId),
                    ["speed"] = ArrayDouble(speeds, i, 0),
                    ["heatValue"] = ArrayDouble(heatValues, i, 0)
                });
            }

            return result;
        }

        private static List<object> ItemBundleItems(object bundle)
        {
            var result = new List<object>();
            var items = MemberValue(bundle, "items") as IEnumerable;
            if (items == null)
            {
                return result;
            }

            foreach (var pair in items)
            {
                var itemId = MemberInt(pair, 0, "Key", "key");
                result.Add(new JsonObject
                {
                    ["itemId"] = itemId,
                    ["name"] = ItemName(itemId),
                    ["count"] = MemberInt(pair, 0, "Value", "value")
                });
            }

            return result;
        }

        private static List<object> IntEnumerable(IEnumerable values, int limit)
        {
            var result = new List<object>();
            if (values == null)
            {
                return result;
            }

            foreach (var value in values)
            {
                if (result.Count >= limit)
                {
                    break;
                }

                try
                {
                    var itemId = Convert.ToInt32(value);
                    result.Add(new JsonObject
                    {
                        ["itemId"] = itemId,
                        ["name"] = ItemName(itemId)
                    });
                }
                catch
                {
                }
            }

            return result;
        }

        private static int ArrayInt(Array array, int index, int defaultValue)
        {
            if (array == null || index < 0 || index >= array.Length)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToInt32(array.GetValue(index));
            }
            catch
            {
                return defaultValue;
            }
        }

        private static double ArrayDouble(Array array, int index, double defaultValue)
        {
            if (array == null || index < 0 || index >= array.Length)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToDouble(array.GetValue(index));
            }
            catch
            {
                return defaultValue;
            }
        }

        private static string ThemeName(int themeId)
        {
            if (themeId <= 0)
            {
                return null;
            }

            try
            {
                return LDB.themes.Select(themeId)?.displayName;
            }
            catch
            {
                return null;
            }
        }

        private static string RecipeName(int recipeId)
        {
            if (recipeId <= 0)
            {
                return null;
            }

            try
            {
                return LDB.recipes.Select(recipeId)?.name;
            }
            catch
            {
                return null;
            }
        }

        private static string ItemName(int itemId)
        {
            if (itemId <= 0)
            {
                return null;
            }

            try
            {
                return LDB.items.Select(itemId)?.name;
            }
            catch
            {
                return null;
            }
        }

        private static string TechName(int techId)
        {
            if (techId <= 0)
            {
                return null;
            }

            try
            {
                return LDB.techs.Select(techId)?.name;
            }
            catch
            {
                return null;
            }
        }

        private static JsonObject Unavailable(string reason)
        {
            return null;
        }

        private static JsonObject Vector(Vector3 value)
        {
            return new JsonObject
            {
                ["x"] = value.x,
                ["y"] = value.y,
                ["z"] = value.z
            };
        }

        private static JsonObject Vector(VectorLF3 value)
        {
            return new JsonObject
            {
                ["x"] = value.x,
                ["y"] = value.y,
                ["z"] = value.z
            };
        }

    }
}
