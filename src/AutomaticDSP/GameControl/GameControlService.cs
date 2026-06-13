using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutomaticDSP.Serialization;
using BepInEx.Logging;

namespace AutomaticDSP.GameControl
{
    internal sealed class GameControlService
    {
        private readonly ManualLogSource log;
        private readonly object queueLock = new object();
        private readonly object randomLock = new object();
        private readonly List<PendingGameControlRequest> pendingRequests = new List<PendingGameControlRequest>();
        private readonly Random random = new Random();
        private DelayedGameStart pendingGameStart;

        public GameControlService(ManualLogSource log)
        {
            this.log = log;
        }

        public Task<JsonObject> Enqueue(Func<JsonObject> action, CancellationToken cancellationToken)
        {
            var pending = new PendingGameControlRequest(action);
            EnqueuePending(pending, cancellationToken);
            return pending.Completion.Task;
        }

        public Task<JsonObject> EnqueueCreateNewGame(NewGameOptions options, CancellationToken cancellationToken)
        {
            var pending = new PendingGameControlRequest(request => BeginCreateNewGame(options, request));
            EnqueuePending(pending, cancellationToken);
            return pending.Completion.Task;
        }

        public Task<JsonObject> EnqueueLoadGame(string saveName, CancellationToken cancellationToken)
        {
            var pending = new PendingGameControlRequest(request => BeginLoadGame(saveName, request));
            EnqueuePending(pending, cancellationToken);
            return pending.Completion.Task;
        }

        private void EnqueuePending(PendingGameControlRequest pending, CancellationToken cancellationToken)
        {
            if (cancellationToken.CanBeCanceled)
            {
                pending.Cancellation = cancellationToken.Register(() => CancelPending(pending));
            }

            lock (queueLock)
            {
                if (!pending.Completion.Task.IsCompleted)
                {
                    pendingRequests.Add(pending);
                }
            }
        }

        public void Update()
        {
            ProcessDelayedGameStart();

            List<PendingGameControlRequest> requests;
            lock (queueLock)
            {
                if (pendingRequests.Count == 0)
                {
                    return;
                }

                requests = new List<PendingGameControlRequest>(pendingRequests);
                pendingRequests.Clear();
            }

            foreach (var request in requests)
            {
                if (request.Completion.Task.IsCompleted)
                {
                    continue;
                }

                ExecuteRequest(request);
            }
        }

        private void ExecuteRequest(PendingGameControlRequest request)
        {
            if (request.Completion.Task.IsCompleted)
            {
                return;
            }

            try
            {
                request.Action(request);
            }
            catch (Exception ex)
            {
                log.LogWarning($"Game control request failed: {ex}");
                request.TrySetException(ex);
            }
        }

        private void BeginCreateNewGame(NewGameOptions options, PendingGameControlRequest request)
        {
            var status = GameStatus();
            if (status != "menu")
            {
                throw new GameControlException("invalid_game_status", "New game can only be created from menu.", status);
            }

            var desc = BuildGameDesc(options);
            ScheduleGameStart(
                request,
                () =>
                {
                    if (options.SkipPrologue)
                    {
                        DSPGame.StartGameSkipPrologue(desc);
                    }
                    else
                    {
                        DSPGame.StartGame(desc);
                    }
                },
                () => new JsonObject
                {
                    ["started"] = true,
                    ["status"] = GameStatus(),
                    ["desc"] = GameDescJson(desc),
                    ["skipPrologue"] = options.SkipPrologue
                });
        }

        private void BeginLoadGame(string saveName, PendingGameControlRequest request)
        {
            if (string.IsNullOrWhiteSpace(saveName))
            {
                throw new GameControlException("bad_request", "saveName is required.", GameStatus());
            }

            var status = GameStatus();
            if (status != "menu")
            {
                throw new GameControlException("invalid_game_status", "Save files can only be loaded from menu.", status);
            }

            var normalized = saveName.Trim();
            if (!GameSave.SaveExist(normalized))
            {
                throw new GameControlException("save_not_found", "Save file does not exist.", status);
            }

            ScheduleGameStart(
                request,
                () => DSPGame.StartGame(normalized),
                () => new JsonObject
                {
                    ["started"] = true,
                    ["saveName"] = normalized,
                    ["status"] = GameStatus()
                });
        }

        private void ScheduleGameStart(PendingGameControlRequest request, Action start, Func<JsonObject> response)
        {
            lock (queueLock)
            {
                if (pendingGameStart != null)
                {
                    throw new GameControlException("invalid_game_status", "A game start request is already pending.", "loading");
                }

                if (DSPGame.Game != null && SafeBool(() => DSPGame.Game.isMenuDemo))
                {
                    DSPGame.EndGame();
                    pendingGameStart = new DelayedGameStart(request, start, response, 2);
                    return;
                }
            }

            start();
            request.TrySetResult(response());
        }

        private void ProcessDelayedGameStart()
        {
            DelayedGameStart gameStart = null;
            lock (queueLock)
            {
                if (pendingGameStart == null)
                {
                    return;
                }

                if (pendingGameStart.Request.Completion.Task.IsCompleted)
                {
                    pendingGameStart = null;
                    return;
                }

                pendingGameStart.FramesRemaining--;
                if (pendingGameStart.FramesRemaining > 0)
                {
                    return;
                }

                gameStart = pendingGameStart;
                pendingGameStart = null;
            }

            try
            {
                gameStart.Start();
                gameStart.Request.TrySetResult(gameStart.Response());
            }
            catch (Exception ex)
            {
                log.LogWarning($"Delayed game start failed: {ex}");
                gameStart.Request.TrySetException(ex);
            }
        }

        public JsonObject ListSaves()
        {
            var items = new List<object>();
            var folder = GameConfig.gameSaveFolder;
            if (Directory.Exists(folder))
            {
                foreach (var filePath in Directory.GetFiles(folder, "*" + GameSave.saveExt, SearchOption.TopDirectoryOnly))
                {
                    var fileInfo = new FileInfo(filePath);
                    var saveName = Path.GetFileNameWithoutExtension(fileInfo.Name);
                    GameSave.ReadHeaderAndDescAndProperty(saveName, false, out var header, out var desc, out var property);
                    items.Add(SaveJson(saveName, fileInfo, header, desc, property));
                }
            }

            return new JsonObject
            {
                ["saveFolder"] = folder,
                ["count"] = items.Count,
                ["items"] = items
            };
        }

        public JsonObject SaveCurrentGame(string saveName)
        {
            if (string.IsNullOrWhiteSpace(saveName))
            {
                throw new GameControlException("bad_request", "saveName is required.", GameStatus());
            }

            var status = GameStatus();
            if (status != "running" && status != "paused" && status != "prologue")
            {
                throw new GameControlException("invalid_game_status", "Current game can only be saved while a game session is loaded.", status);
            }

            var normalized = saveName.Trim();
            var saved = GameSave.SaveCurrentGame(normalized);
            return new JsonObject
            {
                ["saved"] = saved,
                ["saveName"] = normalized,
                ["path"] = GameSave.SavePath(normalized),
                ["status"] = GameStatus()
            };
        }

        public JsonObject SkipPrologue()
        {
            var status = GameStatus();
            if (status != "prologue")
            {
                return new JsonObject
                {
                    ["skipped"] = false,
                    ["status"] = status
                };
            }

            GameMain.data.SkipStandardModeGuide();
            return new JsonObject
            {
                ["skipped"] = true,
                ["status"] = GameStatus()
            };
        }

        public JsonObject NewGameDefaults()
        {
            return NewGameDefaults(null);
        }

        public JsonObject NewGameDefaults(int? seed)
        {
            var galaxySeed = seed ?? NextGalaxySeed();
            return new JsonObject
            {
                ["galaxyAlgo"] = UniverseGen.algoVersion,
                ["galaxySeed"] = galaxySeed,
                ["galaxySeedText"] = galaxySeed.ToString("00000000"),
                ["starCount"] = 64,
                ["playerProto"] = 1,
                ["resourceMultiplier"] = 1f,
                ["mode"] = "combat",
                ["isPeaceMode"] = false,
                ["isCombatMode"] = true,
                ["isSandboxMode"] = false,
                ["skipPrologue"] = true,
                ["goalLevel"] = EGoalLevel.Full.ToString(),
                ["combatSettings"] = CombatSettingsJson(DefaultCombatSettings())
            };
        }

        public JsonObject NewGameParameters()
        {
            return new JsonObject
            {
                ["setForNewGame"] = new List<object>
                {
                    "galaxyAlgo",
                    "galaxySeed",
                    "starCount",
                    "playerProto",
                    "resourceMultiplier"
                },
                ["mode"] = new List<object> { "combat", "peace" },
                ["booleans"] = new List<object>
                {
                    "isCombatMode",
                    "combatMode",
                    "isPeaceMode",
                    "peaceMode",
                    "isSandboxMode",
                    "sandbox",
                    "skipPrologue"
                },
                ["goalLevel"] = new List<object>
                {
                    EGoalLevel.None.ToString(),
                    EGoalLevel.Off.ToString(),
                    EGoalLevel.Key.ToString(),
                    EGoalLevel.Full.ToString()
                },
                ["combatSettings"] = new List<object>
                {
                    "aggressiveness",
                    "initialLevel",
                    "initialGrowth",
                    "initialColonize",
                    "maxDensity",
                    "growthSpeedFactor",
                    "powerThreatFactor",
                    "battleThreatFactor",
                    "battleExpFactor"
                }
            };
        }

        public JsonObject ControlAvailability(string status)
        {
            return new JsonObject
            {
                ["canCreateNewGame"] = status == "menu",
                ["canLoadSave"] = status == "menu",
                ["canSave"] = status == "running" || status == "paused" || status == "prologue",
                ["canSkipPrologue"] = status == "prologue"
            };
        }

        public NewGameOptions NormalizeNewGameOptions(NewGameRequest request)
        {
            var options = new NewGameOptions();
            options.GalaxyAlgo = request?.GalaxyAlgo ?? UniverseGen.algoVersion;
            options.GalaxySeed = request?.GalaxySeed ?? NextGalaxySeed();
            if (options.GalaxySeed < 0)
            {
                options.GalaxySeed = 0;
            }
            else if (options.GalaxySeed > 99999999)
            {
                options.GalaxySeed = 99999999;
            }

            options.StarCount = request?.StarCount ?? 64;
            options.PlayerProto = request?.PlayerProto ?? 1;
            options.ResourceMultiplier = request?.ResourceMultiplier ?? 1f;
            options.IsSandboxMode = request?.IsSandboxMode ?? request?.Sandbox ?? false;
            options.SkipPrologue = request?.SkipPrologue ?? true;
            options.GoalLevel = ParseGoalLevel(request?.GoalLevel);

            var mode = string.IsNullOrWhiteSpace(request?.Mode) ? null : request.Mode.Trim().ToLowerInvariant();
            var isPeaceMode = request?.IsPeaceMode ?? request?.PeaceMode;
            var isCombatMode = request?.IsCombatMode ?? request?.CombatMode;
            if (mode == "peace")
            {
                options.IsPeaceMode = true;
            }
            else if (mode == "combat")
            {
                options.IsPeaceMode = false;
            }
            else if (isPeaceMode.HasValue)
            {
                options.IsPeaceMode = isPeaceMode.Value;
            }
            else if (isCombatMode.HasValue)
            {
                options.IsPeaceMode = !isCombatMode.Value;
            }
            else
            {
                options.IsPeaceMode = false;
            }

            options.CombatSettings = BuildCombatSettings(request?.CombatSettings);
            return options;
        }

        private GameDesc BuildGameDesc(NewGameOptions options)
        {
            var desc = new GameDesc();
            desc.SetForNewGame(
                options.GalaxyAlgo,
                options.GalaxySeed,
                options.StarCount,
                options.PlayerProto,
                options.ResourceMultiplier);
            desc.isPeaceMode = options.IsPeaceMode;
            desc.isSandboxMode = options.IsSandboxMode;
            desc.combatSettings = options.CombatSettings;
            desc.goalLevel = options.GoalLevel;
            return desc;
        }

        private static CombatSettings BuildCombatSettings(CombatSettingsRequest request)
        {
            var settings = DefaultCombatSettings();
            if (request == null)
            {
                return settings;
            }

            if (request.Aggressiveness.HasValue)
            {
                settings.aggressiveness = request.Aggressiveness.Value;
            }

            if (request.InitialLevel.HasValue)
            {
                settings.initialLevel = request.InitialLevel.Value;
            }

            if (request.InitialGrowth.HasValue)
            {
                settings.initialGrowth = request.InitialGrowth.Value;
            }

            if (request.InitialColonize.HasValue)
            {
                settings.initialColonize = request.InitialColonize.Value;
            }

            if (request.MaxDensity.HasValue)
            {
                settings.maxDensity = request.MaxDensity.Value;
            }

            if (request.GrowthSpeedFactor.HasValue)
            {
                settings.growthSpeedFactor = request.GrowthSpeedFactor.Value;
            }

            if (request.PowerThreatFactor.HasValue)
            {
                settings.powerThreatFactor = request.PowerThreatFactor.Value;
            }

            if (request.BattleThreatFactor.HasValue)
            {
                settings.battleThreatFactor = request.BattleThreatFactor.Value;
            }

            if (request.BattleExpFactor.HasValue)
            {
                settings.battleExpFactor = request.BattleExpFactor.Value;
            }

            return settings;
        }

        private static CombatSettings DefaultCombatSettings()
        {
            var settings = new CombatSettings();
            settings.SetDefault();
            return settings;
        }

        private static EGoalLevel ParseGoalLevel(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return EGoalLevel.Full;
            }

            try
            {
                return (EGoalLevel)Enum.Parse(typeof(EGoalLevel), value, true);
            }
            catch
            {
                return EGoalLevel.Full;
            }
        }

        private int NextGalaxySeed()
        {
            lock (randomLock)
            {
                return random.Next(100000000);
            }
        }

        private static JsonObject SaveJson(string saveName, FileInfo fileInfo, GameSaveHeader header, GameDesc desc, ClusterPropertyData property)
        {
            return new JsonObject
            {
                ["saveName"] = saveName,
                ["fileName"] = fileInfo.Name,
                ["path"] = fileInfo.FullName,
                ["isUserSave"] = GameSave.IsUserSavedFile(fileInfo),
                ["fileSize"] = header?.fileSize ?? fileInfo.Length,
                ["lastWriteTimeUtc"] = fileInfo.LastWriteTimeUtc,
                ["header"] = HeaderJson(header),
                ["desc"] = GameDescJson(desc),
                ["property"] = property == null ? null : new JsonObject
                {
                    ["seedKey"] = property.seedKey,
                    ["hasUsedProperty"] = property.hasUsedProperty,
                    ["totalProductionCount"] = property.totalProduction?.Count ?? 0,
                    ["totalConsumptionCount"] = property.totalConsumption?.Count ?? 0
                }
            };
        }

        private static JsonObject HeaderJson(GameSaveHeader header)
        {
            if (header == null)
            {
                return null;
            }

            return new JsonObject
            {
                ["headerVersion"] = header.headerVersion,
                ["fileSize"] = header.fileSize,
                ["lastSaveVersion"] = VersionText(header.lastSaveVersion),
                ["gameTick"] = header.gameTick,
                ["saveTime"] = header.saveTime,
                ["clusterGeneration"] = header.clusterGeneration
            };
        }

        private static JsonObject GameDescJson(GameDesc desc)
        {
            if (desc == null)
            {
                return null;
            }

            return new JsonObject
            {
                ["creationTime"] = desc.creationTime,
                ["creationVersion"] = VersionText(desc.creationVersion),
                ["galaxyAlgo"] = desc.galaxyAlgo,
                ["galaxySeed"] = desc.galaxySeed,
                ["galaxySeedText"] = desc.galaxySeed.ToString("00000000"),
                ["starCount"] = desc.starCount,
                ["playerProto"] = desc.playerProto,
                ["resourceMultiplier"] = desc.resourceMultiplier,
                ["oilAmountMultiplier"] = desc.oilAmountMultiplier,
                ["isPeaceMode"] = desc.isPeaceMode,
                ["isCombatMode"] = desc.isCombatMode,
                ["isSandboxMode"] = desc.isSandboxMode,
                ["goalLevel"] = desc.goalLevel.ToString(),
                ["clusterString"] = desc.clusterString,
                ["combatModeDifficulty"] = desc.combatModeDifficultyNumber,
                ["combatSettings"] = CombatSettingsJson(desc.combatSettings)
            };
        }

        private static JsonObject CombatSettingsJson(CombatSettings settings)
        {
            return new JsonObject
            {
                ["aggressiveness"] = settings.aggressiveness,
                ["initialLevel"] = settings.initialLevel,
                ["initialGrowth"] = settings.initialGrowth,
                ["initialColonize"] = settings.initialColonize,
                ["maxDensity"] = settings.maxDensity,
                ["growthSpeedFactor"] = settings.growthSpeedFactor,
                ["powerThreatFactor"] = settings.powerThreatFactor,
                ["battleThreatFactor"] = settings.battleThreatFactor,
                ["battleExpFactor"] = settings.battleExpFactor,
                ["difficulty"] = settings.difficulty,
                ["aggressiveLevel"] = settings.aggressiveLevel.ToString()
            };
        }

        private static string VersionText(Version version)
        {
            return version == null ? null : version.ToString();
        }

        private static string GameStatus()
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

            if (data.guideRunning && !data.guideComplete)
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
                !string.IsNullOrEmpty(SafeValue(() => DSPGame.LoadFile));
        }

        private static bool IsGameMainMenuDemo()
        {
            try
            {
                return GameMain.instance != null && GameMain.instance.isMenuDemo;
            }
            catch
            {
                return false;
            }
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

        private static T SafeValue<T>(Func<T> read)
        {
            try
            {
                return read();
            }
            catch
            {
                return default(T);
            }
        }

        private void CancelPending(PendingGameControlRequest request)
        {
            lock (queueLock)
            {
                pendingRequests.Remove(request);
            }

            request.TrySetCanceled();
        }
    }
}
