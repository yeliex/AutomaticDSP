using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using AutomaticDSP.Serialization;
using BepInEx.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace AutomaticDSP.State
{
    internal sealed class StateSnapshotService
    {
        private const int NearbyBuildingLimit = 200;
        private const float NearbyBuildingRadius = 160f;
        private readonly ManualLogSource log;
        private readonly int snapshotIntervalTicks;
        private readonly JsonSerializerSettings jsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Include
        };
        private readonly string diagnosticsDirectory;
        private readonly string snapshotDirectory;
        private DateTimeOffset lastDiagnosticsWriteAt = DateTimeOffset.MinValue;
        private long lastCaptureGameTick = -1;
        private long nextSnapshotId = 1;
        private bool inactiveLogged;
        private bool inactiveSnapshotFileChecked;
        private bool latestGameLoaded;
        private StateSnapshot latestSnapshot;

        public StateSnapshotService(int snapshotIntervalTicks, string cacheRootPath, ManualLogSource log)
        {
            this.snapshotIntervalTicks = Math.Max(1, snapshotIntervalTicks);
            diagnosticsDirectory = Path.Combine(cacheRootPath, "diagnostics");
            snapshotDirectory = Path.Combine(cacheRootPath, "snapshots");
            this.log = log;
            ClearPersistedSnapshots();
        }

        public StateSnapshot GetLatestSnapshot()
        {
            return latestSnapshot;
        }

        public JsonObject GetHealth()
        {
            var snapshot = latestSnapshot;
            return new JsonObject
            {
                ["status"] = "ok",
                ["gameLoaded"] = latestGameLoaded,
                ["hasState"] = snapshot != null,
                ["latestSnapshotId"] = snapshot?.Id,
                ["latestGameTick"] = snapshot?.GameTick,
                ["snapshotIntervalTicks"] = snapshotIntervalTicks
            };
        }

        public void Update()
        {
            if (!IsGameSessionLoaded())
            {
                MarkSessionUnavailable();
                return;
            }

            var gameTick = GameMain.gameTick;
            if (latestSnapshot != null && gameTick >= lastCaptureGameTick &&
                gameTick - lastCaptureGameTick < snapshotIntervalTicks)
            {
                return;
            }

            Capture(gameTick);
        }

        private void Capture(long gameTick)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var data = new JsonObject();
                var metadata = CaptureMetadata(gameTick);
                data["metadata"] = metadata;
                data["game"] = CaptureGameState();
                data["player"] = CapturePlayer();
                data["mecha"] = CaptureMecha();
                data["inventory"] = CaptureInventory();
                data["replicator"] = CaptureReplicator();
                data["research"] = CaptureResearch();
                data["currentPlanet"] = CaptureCurrentPlanet();
                data["factory"] = CaptureFactory();
                data["production"] = CaptureProduction();
                data["power"] = CapturePower();
                data["alerts"] = CaptureAlerts(data);
                data["buildContext"] = CaptureBuildContext(data);
                var debug = CaptureDebugState();
                data["debug"] = debug;

                stopwatch.Stop();
                ((JsonObject)data["metadata"])["captureDurationMs"] = stopwatch.Elapsed.TotalMilliseconds;

                var snapshot = new StateSnapshot(nextSnapshotId++, gameTick, data);
                latestGameLoaded = Convert.ToBoolean(metadata["gameLoaded"]);
                latestSnapshot = snapshot;
                lastCaptureGameTick = gameTick;
                inactiveLogged = false;
                inactiveSnapshotFileChecked = false;
                WriteLatestSnapshot(snapshot);
                WriteGameMainDiagnostics(debug);

                if (snapshot.Id == 1 || snapshot.Id % 60 == 0)
                {
                    log.LogInfo($"Captured state snapshot {snapshot.Id} at gameTick {gameTick}.");
                }
            }
            catch (Exception ex)
            {
                log.LogWarning($"State snapshot capture failed: {ex}");
            }
        }

        private void MarkSessionUnavailable()
        {
            latestGameLoaded = false;
            latestSnapshot = null;
            lastCaptureGameTick = -1;

            if (!inactiveSnapshotFileChecked)
            {
                ClearPersistedSnapshots();
                inactiveSnapshotFileChecked = true;
            }

            if (!inactiveLogged)
            {
                log.LogInfo("AutomaticDSP state snapshot is waiting for a loaded game session.");
                inactiveLogged = true;
            }
        }

        private void WriteLatestSnapshot(StateSnapshot snapshot)
        {
            try
            {
                Directory.CreateDirectory(snapshotDirectory);
                var snapshotPath = Path.Combine(snapshotDirectory, "latest.json");
                var tempPath = snapshotPath + ".tmp";
                var json = JsonConvert.SerializeObject(snapshot.Data, Formatting.None, jsonSettings);
                File.WriteAllText(tempPath, json, Encoding.UTF8);

                if (File.Exists(snapshotPath))
                {
                    File.Delete(snapshotPath);
                }

                File.Move(tempPath, snapshotPath);
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed to write latest state snapshot: {ex.Message}");
            }
        }

        private void WriteGameMainDiagnostics(JsonObject debug)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - lastDiagnosticsWriteAt < TimeSpan.FromSeconds(1))
            {
                return;
            }

            lastDiagnosticsWriteAt = now;
            try
            {
                WriteJsonFile(diagnosticsDirectory, "gameMain.json", debug);
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed to write GameMain diagnostics: {ex.Message}");
            }
        }

        private void WriteJsonFile(string directory, string fileName, object payload)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            var tempPath = path + ".tmp";
            var json = JsonConvert.SerializeObject(payload, Formatting.None, jsonSettings);
            File.WriteAllText(tempPath, json, Encoding.UTF8);

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(tempPath, path);
        }

        private void ClearPersistedSnapshots()
        {
            DeleteLatestSnapshot();
            DeleteGameMainDiagnostics();
        }

        private void DeleteLatestSnapshot()
        {
            try
            {
                var snapshotPath = Path.Combine(snapshotDirectory, "latest.json");
                var tempPath = snapshotPath + ".tmp";
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                if (File.Exists(snapshotPath))
                {
                    File.Delete(snapshotPath);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed to delete stale state snapshot: {ex.Message}");
            }
        }

        private void DeleteGameMainDiagnostics()
        {
            try
            {
                lastDiagnosticsWriteAt = DateTimeOffset.MinValue;
                var diagnosticsPath = Path.Combine(diagnosticsDirectory, "gameMain.json");
                var tempPath = diagnosticsPath + ".tmp";
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                if (File.Exists(diagnosticsPath))
                {
                    File.Delete(diagnosticsPath);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed to delete stale GameMain diagnostics: {ex.Message}");
            }
        }

        private JsonObject CaptureMetadata(long gameTick)
        {
            return new JsonObject
            {
                ["snapshotId"] = nextSnapshotId,
                ["gameTick"] = gameTick,
                ["capturedAt"] = DateTimeOffset.UtcNow,
                ["captureDurationMs"] = 0,
                ["gameLoaded"] = IsGameLoaded(),
                ["currentPlanetId"] = GameMain.localPlanet?.id,
                ["currentStarId"] = GameMain.localStar?.id,
                ["schemaVersion"] = 1
            };
        }

        private JsonObject CaptureGameState()
        {
            var data = GameMain.data;
            var galaxy = GameMain.galaxy;

            return new JsonObject
            {
                ["available"] = true,
                ["name"] = GameMain.gameName,
                ["creationTime"] = GameMain.creationTime,
                ["gameTick"] = GameMain.gameTick,
                ["gameTime"] = GameMain.gameTime,
                ["onceGameTick"] = GameMain.onceGameTick,
                ["onceGameTime"] = GameMain.onceGameTime,
                ["sandboxToolsEnabled"] = GameMain.sandboxToolsEnabled,
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
                    ["menuDemoLoaded"] = DSPGame.MenuDemoLoaded
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
                    ["available"] = galaxy != null,
                    ["starCount"] = ReflectionReader.GetInt(galaxy, 0, "starCount")
                },
                ["data"] = new JsonObject
                {
                    ["available"] = data != null,
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

        private JsonObject CaptureDebugState()
        {
            var data = SafeGet(() => GameMain.data);
            var instance = SafeGet(() => GameMain.instance);

            return new JsonObject
            {
                ["capturedAt"] = DateTimeOffset.UtcNow,
                ["gameSessionLoaded"] = IsGameSessionLoaded(),
                ["gameMainStatic"] = CaptureMembers(null, typeof(GameMain), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, 120),
                ["gameMainInstance"] = instance == null ? null : CaptureMembers(instance, typeof(GameMain), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, 160),
                ["gameData"] = data == null ? null : CaptureMembers(data, typeof(GameData), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, 180),
                ["dspGameStatic"] = CaptureMembers(null, typeof(DSPGame), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, 120)
            };
        }

        private static JsonObject CaptureMembers(object target, Type type, BindingFlags flags, int limit)
        {
            var result = new JsonObject
            {
                ["type"] = type.FullName
            };
            var members = new JsonObject();
            var count = 0;

            foreach (var field in type.GetFields(flags))
            {
                if (count >= limit)
                {
                    break;
                }

                members[field.Name] = ReadMember(() => field.GetValue(target));
                count++;
            }

            foreach (var property in type.GetProperties(flags))
            {
                if (count >= limit)
                {
                    break;
                }

                if (property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                members[property.Name] = ReadMember(() => property.GetValue(target, null));
                count++;
            }

            result["memberCount"] = count;
            result["members"] = members;
            return result;
        }

        private static object ReadMember(Func<object> read)
        {
            try
            {
                return ToDebugValue(read());
            }
            catch (Exception ex)
            {
                return new JsonObject
                {
                    ["error"] = ex.GetType().Name,
                    ["message"] = ex.Message
                };
            }
        }

        private static object ToDebugValue(object value)
        {
            if (value == null)
            {
                return null;
            }

            var type = value.GetType();
            if (type.IsPrimitive || value is string || value is decimal || value is DateTime || value is DateTimeOffset)
            {
                return value;
            }

            if (type.IsEnum)
            {
                return value.ToString();
            }

            if (value is Vector3 vector3)
            {
                return Vector(vector3);
            }

            if (value is VectorLF3 vectorLf3)
            {
                return Vector(vectorLf3);
            }

            if (value is Array array)
            {
                return ArraySummary(array);
            }

            if (value is ICollection collection)
            {
                return new JsonObject
                {
                    ["type"] = type.FullName,
                    ["count"] = collection.Count
                };
            }

            return ObjectSummary(value);
        }

        private static JsonObject ArraySummary(Array array)
        {
            var result = new JsonObject
            {
                ["type"] = array.GetType().FullName,
                ["elementType"] = array.GetType().GetElementType()?.FullName,
                ["length"] = array.Length,
                ["rank"] = array.Rank
            };

            var sample = new List<object>();
            if (array.Rank == 1)
            {
                for (var i = 0; i < array.Length && sample.Count < 16; i++)
                {
                    var value = array.GetValue(i);
                    if (value == null)
                    {
                        sample.Add(null);
                    }
                    else
                    {
                        var valueType = value.GetType();
                        sample.Add(valueType.IsPrimitive || value is string || valueType.IsEnum ? ToDebugValue(value) : ObjectSummary(value));
                    }
                }
            }

            result["sample"] = sample;
            return result;
        }

        private static JsonObject ObjectSummary(object value)
        {
            var type = value.GetType();
            var result = new JsonObject
            {
                ["type"] = type.FullName
            };

            AddSummaryMember(result, value, "id");
            AddSummaryMember(result, value, "index");
            AddSummaryMember(result, value, "name");
            AddSummaryMember(result, value, "displayName");
            AddSummaryMember(result, value, "planetId");
            AddSummaryMember(result, value, "starId");
            AddSummaryMember(result, value, "factoryIndex");
            AddSummaryMember(result, value, "count");
            AddSummaryMember(result, value, "length");
            AddSummaryMember(result, value, "cursor");
            AddSummaryMember(result, value, "entityCursor");
            AddSummaryMember(result, value, "factoryCount");
            AddSummaryMember(result, value, "starCount");
            return result;
        }

        private static void AddSummaryMember(JsonObject result, object target, string name)
        {
            var value = ReflectionReader.Get(target, name);
            if (value == null)
            {
                return;
            }

            var type = value.GetType();
            if (type.IsPrimitive || value is string || type.IsEnum)
            {
                result[name] = ToDebugValue(value);
            }
        }

        private static T SafeGet<T>(Func<T> read)
        {
            try
            {
                return read();
            }
            catch
            {
                return default;
            }
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
                ["available"] = true,
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
                ["available"] = true,
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
            var emptySlots = 0;

            for (var i = 0; i < package.size; i++)
            {
                var grid = package.grids[i];
                if (grid.itemId <= 0 || grid.count <= 0)
                {
                    emptySlots++;
                    continue;
                }

                items.Add(new JsonObject
                {
                    ["slot"] = i,
                    ["itemId"] = grid.itemId,
                    ["name"] = ItemName(grid.itemId),
                    ["count"] = grid.count,
                    ["stackSize"] = grid.stackSize,
                    ["inc"] = grid.inc
                });

                byItem.TryGetValue(grid.itemId, out var count);
                byItem[grid.itemId] = count + grid.count;
            }

            return new JsonObject
            {
                ["available"] = true,
                ["size"] = package.size,
                ["emptySlots"] = emptySlots,
                ["items"] = items,
                ["summary"] = ItemSummary(byItem)
            };
        }

        private JsonObject CaptureReplicator()
        {
            var forge = GameMain.mainPlayer?.mecha?.forge;
            if (forge == null)
            {
                return Unavailable("replicator_missing");
            }

            return new JsonObject
            {
                ["available"] = true,
                ["queue"] = new List<object>(),
                ["current"] = null,
                ["notes"] = new List<object>
                {
                    "Replicator queue shape is reserved for M2 field mapping."
                }
            };
        }

        private JsonObject CaptureResearch()
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
            return new JsonObject
            {
                ["available"] = true,
                ["currentTechId"] = currentTech,
                ["currentTechName"] = TechName(currentTech),
                ["queueLength"] = ReflectionReader.GetInt(history, queue.Count, "techQueueLength"),
                ["queue"] = queue,
                ["techHashedFor10Frames"] = GameMain.statistics?.techHashedFor10Frames ?? 0,
                ["isStalled"] = currentTech > 0 && (GameMain.statistics?.techHashedFor10Frames ?? 0) == 0
            };
        }

        private JsonObject CaptureCurrentPlanet()
        {
            var planet = GameMain.localPlanet;
            if (planet == null)
            {
                return Unavailable("current_planet_missing");
            }

            return new JsonObject
            {
                ["available"] = true,
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
                ["resources"] = CaptureVeins(planet.factory)
            };
        }

        private JsonObject CaptureFactory()
        {
            var factory = GameMain.localPlanet?.factory;
            if (factory == null)
            {
                return Unavailable("factory_missing");
            }

            var playerPosition = GameMain.mainPlayer != null ? GameMain.mainPlayer.position : Vector3.zero;
            var buildings = new List<object>();
            var byProto = new Dictionary<int, int>();
            var missingPower = 0;

            for (var i = 1; i < factory.entityCursor && buildings.Count < NearbyBuildingLimit; i++)
            {
                var entity = factory.entityPool[i];
                if (entity.id != i)
                {
                    continue;
                }

                byProto.TryGetValue(entity.protoId, out var protoCount);
                byProto[entity.protoId] = protoCount + 1;

                if ((entity.pos - playerPosition).sqrMagnitude > NearbyBuildingRadius * NearbyBuildingRadius)
                {
                    continue;
                }

                if (entity.powerNodeId == 0)
                {
                    missingPower++;
                }

                buildings.Add(new JsonObject
                {
                    ["entityId"] = entity.id,
                    ["protoId"] = entity.protoId,
                    ["name"] = ItemName(entity.protoId),
                    ["modelIndex"] = entity.modelIndex,
                    ["position"] = Vector(entity.pos),
                    ["powerNodeId"] = entity.powerNodeId
                });
            }

            return new JsonObject
            {
                ["available"] = true,
                ["planetId"] = factory.planetId,
                ["entityCursor"] = factory.entityCursor,
                ["nearbyRadius"] = NearbyBuildingRadius,
                ["nearbyBuildings"] = buildings,
                ["buildingSummary"] = ItemSummary(byProto),
                ["missingPowerBuildingCountNearby"] = missingPower,
                ["beltCount"] = ReflectionReader.GetInt(factory.cargoTraffic, 0, "beltCursor"),
                ["sorterCount"] = ReflectionReader.GetInt(factory.cargoTraffic, 0, "sorterCursor")
            };
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
                ["available"] = true,
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
                ["available"] = true,
                ["networkCount"] = powerSystem.netCursor,
                ["storedEnergy"] = storedEnergy,
                ["generationRegister"] = stat == null ? 0 : ReflectionReader.GetLong(stat, 0, "powerGenRegister"),
                ["consumptionRegister"] = stat == null ? 0 : ReflectionReader.GetLong(stat, 0, "powerConRegister"),
                ["chargingRegister"] = stat == null ? 0 : ReflectionReader.GetLong(stat, 0, "powerChaRegister"),
                ["dischargingRegister"] = stat == null ? 0 : ReflectionReader.GetLong(stat, 0, "powerDisRegister")
            };
        }

        private static JsonObject CaptureAlerts(JsonObject data)
        {
            var alerts = new List<object>();
            var power = data["power"] as JsonObject;
            var research = data["research"] as JsonObject;

            if (power != null && Convert.ToBoolean(power["available"]) &&
                Convert.ToInt64(power["generationRegister"]) < Convert.ToInt64(power["consumptionRegister"]))
            {
                alerts.Add(new JsonObject
                {
                    ["type"] = "power_shortage",
                    ["severity"] = "warning"
                });
            }

            if (research != null && Convert.ToBoolean(research["available"]) &&
                Convert.ToBoolean(research["isStalled"]))
            {
                alerts.Add(new JsonObject
                {
                    ["type"] = "research_stalled",
                    ["severity"] = "info"
                });
            }

            return new JsonObject
            {
                ["items"] = alerts
            };
        }

        private static JsonObject CaptureBuildContext(JsonObject data)
        {
            return new JsonObject
            {
                ["available"] = true,
                ["notes"] = new List<object>
                {
                    "Build-context facts are reserved for M2. M1 exposes inventory, planet, factory, production, and power inputs."
                }
            };
        }

        private static JsonObject CaptureVeins(PlanetFactory factory)
        {
            if (factory == null)
            {
                return Unavailable("factory_missing");
            }

            var veins = new List<object>();
            var byType = new Dictionary<int, int>();
            var byTypeAmount = new Dictionary<int, long>();
            var totalVeinCount = 0;

            for (var i = 1; i < factory.veinCursor; i++)
            {
                var vein = factory.veinPool[i];
                if (vein.id != i)
                {
                    continue;
                }

                totalVeinCount++;
                var type = (int)vein.type;
                byType.TryGetValue(type, out var count);
                byType[type] = count + 1;
                byTypeAmount.TryGetValue(type, out var amount);
                byTypeAmount[type] = amount + vein.amount;

                if (veins.Count < 200)
                {
                    veins.Add(new JsonObject
                    {
                        ["id"] = vein.id,
                        ["type"] = vein.type.ToString(),
                        ["typeId"] = type,
                        ["amount"] = vein.amount,
                        ["position"] = Vector(vein.pos)
                    });
                }
            }

            var summary = new List<object>();
            foreach (var pair in byType)
            {
                summary.Add(new JsonObject
                {
                    ["typeId"] = pair.Key,
                    ["count"] = pair.Value,
                    ["amount"] = byTypeAmount[pair.Key]
                });
            }

            return new JsonObject
            {
                ["veinCount"] = totalVeinCount,
                ["veins"] = veins,
                ["summary"] = summary
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
            try
            {
                return GameMain.data != null &&
                       GameMain.notNull &&
                       GameMain.isRunning &&
                       !GameMain.isLoading &&
                       !GameMain.isEnded &&
                       !GameMain.loadErrored &&
                       !GameMain.inOtherScene &&
                       !DSPGame.IsMenuDemo &&
                       !DSPGame.MenuDemoLoaded &&
                       GameMain.mainPlayer != null &&
                       GameMain.history != null &&
                       GameMain.statistics != null &&
                       GameMain.galaxy != null &&
                       GameMain.gameTick > 0;
            }
            catch
            {
                return false;
            }
        }

        private static JsonObject ItemSummary(Dictionary<int, int> items)
        {
            var result = new List<object>();
            foreach (var pair in items)
            {
                result.Add(new JsonObject
                {
                    ["itemId"] = pair.Key,
                    ["name"] = ItemName(pair.Key),
                    ["count"] = pair.Value
                });
            }

            return new JsonObject
            {
                ["items"] = result
            };
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
            return new JsonObject
            {
                ["available"] = false,
                ["reason"] = reason
            };
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
