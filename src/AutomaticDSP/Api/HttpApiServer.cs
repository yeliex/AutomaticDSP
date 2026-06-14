using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutomaticDSP.Serialization;
using AutomaticDSP.GameControl;
using AutomaticDSP.State;
using AutomaticDSP.Storage;
using AutomaticDSP.Tasks;
using BepInEx.Logging;
using GraphQLParser.Exceptions;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace AutomaticDSP.Api
{
    internal sealed class HttpApiServer : IDisposable
    {
        private static readonly TimeSpan GameControlTimeout = TimeSpan.FromSeconds(120);
        private static readonly TimeSpan StateQueryTimeout = TimeSpan.FromSeconds(10);
        private readonly GameControlService gameControlService;
        private readonly HistoryStore historyStore;
        private readonly string host;
        private readonly ManualLogSource log;
        private readonly int port;
        private readonly GameStateQueryService stateQueryService;
        private readonly TaskQueueService taskQueueService;
        private CancellationTokenSource cancellation;
        private HttpListener listener;
        private Task listenTask;
        private readonly JsonSerializerSettings jsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Include
        };

        public HttpApiServer(
            string host,
            int port,
            GameStateQueryService stateQueryService,
            GameControlService gameControlService,
            TaskQueueService taskQueueService,
            HistoryStore historyStore,
            ManualLogSource log)
        {
            this.host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
            this.port = port;
            this.stateQueryService = stateQueryService;
            this.gameControlService = gameControlService;
            this.taskQueueService = taskQueueService;
            this.historyStore = historyStore;
            this.log = log;
        }

        public void Start()
        {
            if (listener != null)
            {
                return;
            }

            cancellation = new CancellationTokenSource();
            listener = new HttpListener();
            listener.Prefixes.Add($"http://{PrefixHost(this.host)}:{port}/");
            listener.Start();
            listenTask = Task.Run(() => ListenLoop(cancellation.Token));
            log.LogInfo($"AutomaticDSP HTTP API listening on http://{DisplayHost(this.host)}:{port}/");
        }

        public void Dispose()
        {
            cancellation?.Cancel();
            listener?.Stop();
            cancellation?.Dispose();
            cancellation = null;
            listener = null;
        }

        private async Task ListenLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var context = await listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleContext(context), token);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        log.LogWarning($"HTTP accept failed: {ex.Message}");
                    }
                }
            }
        }

        private void HandleContext(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.Url.AbsolutePath;
                if (IsOptions(context))
                {
                    WriteNoContent(context, 204);
                    return;
                }

                if (path == "/game")
                {
                    if (IsGet(context))
                    {
                        HandleGameStatus(context);
                    }
                    else if (string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        HandleCreateGame(context);
                    }
                    else
                    {
                        WriteJson(context, 405, Error("method_not_allowed", "Only GET and POST are supported for /game."));
                    }
                }
                else if (path == "/game/state")
                {
                    if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteJson(context, 405, Error("method_not_allowed", "Only POST is supported for /game/state."));
                    }
                    else
                    {
                        HandleStateQuery(context);
                    }
                }
                else if (path == "/game/saves")
                {
                    if (!IsGet(context))
                    {
                        WriteJson(context, 405, Error("method_not_allowed", "Only GET is supported for /game/saves."));
                        return;
                    }

                    WriteJson(context, 200, gameControlService.ListSaves());
                }
                else if (path == "/game/save")
                {
                    if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteJson(context, 405, Error("method_not_allowed", "Only POST is supported for /game/save."));
                    }
                    else
                    {
                        HandleSaveGame(context);
                    }
                }
                else if (path == "/game/load")
                {
                    if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteJson(context, 405, Error("method_not_allowed", "Only POST is supported for /game/load."));
                    }
                    else
                    {
                        HandleLoadGame(context);
                    }
                }
                else if (path == "/game/prologue/skip")
                {
                    if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteJson(context, 405, Error("method_not_allowed", "Only POST is supported for /game/prologue/skip."));
                    }
                    else
                    {
                        HandleGameControl(context, () => gameControlService.SkipPrologue());
                    }
                }
                else if (path == "/tasks")
                {
                    if (IsGet(context))
                    {
                        WriteJson(context, 200, taskQueueService.GetActiveTasksResponse());
                    }
                    else if (string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        HandleSubmitTask(context);
                    }
                    else
                    {
                        WriteJson(context, 405, Error("method_not_allowed", "Only GET and POST are supported for /tasks."));
                    }
                }
                else if (IsTaskCancelPath(path, out var taskId))
                {
                    if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteJson(context, 405, Error("method_not_allowed", "Only POST is supported for /tasks/{id}/cancel."));
                        return;
                    }

                    HandleCancelTask(context, taskId);
                }
                else if (IsTaskPath(path, out taskId))
                {
                    if (!IsGet(context))
                    {
                        WriteJson(context, 405, Error("method_not_allowed", "Only GET is supported for /tasks/{id}."));
                        return;
                    }

                    HandleGetTask(context, taskId);
                }
                else if (path == "/history")
                {
                    if (!IsGet(context))
                    {
                        WriteJson(context, 405, Error("method_not_allowed", "Only GET is supported for /history."));
                        return;
                    }

                    WriteJson(context, 200, historyStore.GetHistoryResponse(100));
                }
                else
                {
                    WriteJson(context, 404, Error("not_found", $"Unknown endpoint: {path}"));
                }
            }
            catch (ThreadAbortException ex)
            {
                Thread.ResetAbort();
                log.LogWarning($"HTTP request aborted: {ex.Message}");
            }
            catch (Exception ex)
            {
                log.LogWarning($"HTTP request failed: {ex}");
                try
                {
                    WriteJson(context, 500, Error("internal_error", ex.Message));
                }
                catch (Exception writeEx)
                {
                    log.LogWarning($"Failed to write HTTP error response: {writeEx.Message}");
                }
            }
        }

        private void HandleGameStatus(HttpListenerContext context)
        {
            var source = stateQueryService.GetGameStatus();
            var response = new JsonObject();
            foreach (var pair in source)
            {
                response[pair.Key] = pair.Value;
            }

            var status = response.TryGetValue("status", out var value) ? value?.ToString() : "unknown";
            response["controls"] = gameControlService.ControlAvailability(status);
            response["newGameDefaults"] = gameControlService.NewGameDefaults();
            response["newGameParameters"] = gameControlService.NewGameParameters();
            WriteJson(context, 200, response);
        }

        private void HandleCreateGame(HttpListenerContext context)
        {
            NewGameRequest request;
            try
            {
                request = ReadJsonRequest<NewGameRequest>(context, allowEmpty: true) ?? new NewGameRequest();
            }
            catch (JsonException ex)
            {
                WriteJson(context, 400, Error("bad_json", ex.Message));
                return;
            }

            HandleGameControl(context, timeout =>
            {
                var options = gameControlService.NormalizeNewGameOptions(request);
                return gameControlService.EnqueueCreateNewGame(options, timeout);
            });
        }

        private void HandleSaveGame(HttpListenerContext context)
        {
            SaveNameRequest request;
            try
            {
                request = ReadJsonRequest<SaveNameRequest>(context, allowEmpty: false);
            }
            catch (JsonException ex)
            {
                WriteJson(context, 400, Error("bad_json", ex.Message));
                return;
            }

            HandleGameControl(context, () => gameControlService.SaveCurrentGame(request?.SaveName));
        }

        private void HandleLoadGame(HttpListenerContext context)
        {
            SaveNameRequest request;
            try
            {
                request = ReadJsonRequest<SaveNameRequest>(context, allowEmpty: false);
            }
            catch (JsonException ex)
            {
                WriteJson(context, 400, Error("bad_json", ex.Message));
                return;
            }

            HandleGameControl(context, timeout => gameControlService.EnqueueLoadGame(request?.SaveName, timeout));
        }

        private void HandleGameControl(HttpListenerContext context, Func<JsonObject> action)
        {
            HandleGameControl(context, timeout => gameControlService.Enqueue(action, timeout));
        }

        private void HandleGameControl(HttpListenerContext context, Func<CancellationToken, Task<JsonObject>> action)
        {
            try
            {
                using (var timeout = new CancellationTokenSource(GameControlTimeout))
                {
                    var data = action(timeout.Token).GetAwaiter().GetResult();
                    WriteJson(context, 200, data);
                }
            }
            catch (GameControlException ex)
            {
                WriteJson(context, GameControlStatusCode(ex.Code), ControlError(ex));
            }
            catch (OperationCanceledException)
            {
                WriteJson(context, 504, Error("control_timeout", "Timed out waiting for the next game control tick."));
            }
        }

        private void HandleStateQuery(HttpListenerContext context)
        {
            if (!stateQueryService.IsStateQueryReady(out var status))
            {
                WriteJson(context, 409, StateNotReady(status));
                return;
            }

            StateQueryRequest request;
            try
            {
                request = ReadStateQueryRequest(context);
                if (request == null || string.IsNullOrWhiteSpace(request.Query))
                {
                    WriteJson(context, 400, Error("bad_request", "Request body must include a non-empty query string."));
                    return;
                }
            }
            catch (JsonException ex)
            {
                WriteJson(context, 400, Error("bad_json", ex.Message));
                return;
            }

            StateQueryPlan plan;
            try
            {
                plan = StateQueryParser.Parse(request.Query, request.OperationName);
            }
            catch (GraphQLParserException ex)
            {
                WriteJson(context, 400, Error("graphql_parse_error", ex.Message));
                return;
            }
            catch (StateQueryParseException ex)
            {
                WriteJson(context, 400, Error("graphql_parse_error", ex.Message));
                return;
            }

            try
            {
                using (var timeout = new CancellationTokenSource(StateQueryTimeout))
                {
                    var data = stateQueryService.EnqueueStateQuery(plan, timeout.Token).GetAwaiter().GetResult();
                    WriteJson(context, 200, new JsonObject { ["data"] = data });
                }
            }
            catch (StateQueryNotReadyException ex)
            {
                WriteJson(context, 409, StateNotReady(ex.Status));
            }
            catch (OperationCanceledException)
            {
                WriteJson(context, 504, Error("query_timeout", "Timed out waiting for the next game-driven state query tick."));
            }
        }

        private void HandleSubmitTask(HttpListenerContext context)
        {
            TaskSubmitRequest request;
            try
            {
                request = ReadJsonRequest<TaskSubmitRequest>(context, allowEmpty: false);
            }
            catch (JsonException ex)
            {
                WriteJson(context, 400, Error("bad_json", ex.Message));
                return;
            }

            try
            {
                WriteJson(context, 200, taskQueueService.Enqueue(request));
            }
            catch (TaskQueueException ex)
            {
                WriteJson(context, TaskQueueStatusCode(ex.Code), Error(ex.Code, ex.Message));
            }
        }

        private void HandleCancelTask(HttpListenerContext context, string taskId)
        {
            try
            {
                WriteJson(context, 200, taskQueueService.Cancel(taskId));
            }
            catch (TaskQueueException ex)
            {
                WriteJson(context, TaskQueueStatusCode(ex.Code), Error(ex.Code, ex.Message));
            }
        }

        private void HandleGetTask(HttpListenerContext context, string taskId)
        {
            try
            {
                WriteJson(context, 200, taskQueueService.GetTaskResponse(taskId));
            }
            catch (TaskQueueException ex)
            {
                WriteJson(context, TaskQueueStatusCode(ex.Code), Error(ex.Code, ex.Message));
            }
        }

        private static StateQueryRequest ReadStateQueryRequest(HttpListenerContext context)
        {
            return ReadJsonRequest<StateQueryRequest>(context, allowEmpty: false);
        }

        private static T ReadJsonRequest<T>(HttpListenerContext context, bool allowEmpty)
            where T : class
        {
            using (var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8))
            {
                var body = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(body))
                {
                    return allowEmpty ? null : throw new JsonException("Request body is empty.");
                }

                return JsonConvert.DeserializeObject<T>(body);
            }
        }

        private static object Error(string code, string message)
        {
            return new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["code"] = code,
                    ["message"] = message
                }
            };
        }

        private static object StateNotReady(string status)
        {
            return new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["code"] = "game_not_ready",
                    ["message"] = "Game state is not ready for query.",
                    ["status"] = status
                }
            };
        }

        private static object ControlError(GameControlException ex)
        {
            return new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["code"] = ex.Code,
                    ["message"] = ex.Message,
                    ["status"] = ex.Status
                }
            };
        }

        private static int GameControlStatusCode(string code)
        {
            switch (code)
            {
                case "bad_request":
                    return 400;
                case "save_not_found":
                    return 404;
                case "invalid_game_status":
                    return 409;
                default:
                    return 400;
            }
        }

        private static int TaskQueueStatusCode(string code)
        {
            switch (code)
            {
                case "bad_request":
                case "invalid_command":
                    return 400;
                case "task_not_found":
                    return 404;
                case "invalid_task_status":
                    return 409;
                default:
                    return 400;
            }
        }

        private static bool IsGet(HttpListenerContext context)
        {
            return string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOptions(HttpListenerContext context)
        {
            return string.Equals(context.Request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTaskCancelPath(string path, out string taskId)
        {
            taskId = null;
            const string prefix = "/tasks/";
            const string suffix = "/cancel";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            taskId = Uri.UnescapeDataString(path.Substring(prefix.Length, path.Length - prefix.Length - suffix.Length));
            return !string.IsNullOrWhiteSpace(taskId);
        }

        private static bool IsTaskPath(string path, out string taskId)
        {
            taskId = null;
            const string prefix = "/tasks/";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var rawTaskId = path.Substring(prefix.Length);
            if (string.IsNullOrWhiteSpace(rawTaskId) || rawTaskId.IndexOf('/') >= 0)
            {
                return false;
            }

            taskId = Uri.UnescapeDataString(rawTaskId);
            return !string.IsNullOrWhiteSpace(taskId);
        }

        private static string DisplayHost(string value)
        {
            return IsAnyHost(value) ? "0.0.0.0" : value;
        }

        private static bool IsAnyHost(string value)
        {
            return value == "0.0.0.0" || value == "*" || value == "+";
        }

        private static string PrefixHost(string value)
        {
            return IsAnyHost(value) ? "*" : value;
        }

        private static void ApplyCorsHeaders(HttpListenerResponse response)
        {
            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
            response.Headers["Access-Control-Max-Age"] = "86400";
        }

        private static void WriteNoContent(HttpListenerContext context, int statusCode)
        {
            var response = context.Response;
            try
            {
                response.StatusCode = statusCode;
                response.ContentLength64 = 0;
                ApplyCorsHeaders(response);
            }
            finally
            {
                response.OutputStream.Close();
            }
        }

        private void WriteJson(HttpListenerContext context, int statusCode, object payload)
        {
            var json = JsonConvert.SerializeObject(payload, Formatting.None, jsonSettings);
            var body = Encoding.UTF8.GetBytes(json);
            var response = context.Response;
            try
            {
                response.StatusCode = statusCode;
                response.ContentType = "application/json; charset=utf-8";
                response.ContentLength64 = body.Length;
                ApplyCorsHeaders(response);
                response.OutputStream.Write(body, 0, body.Length);
            }
            finally
            {
                response.OutputStream.Close();
            }
        }

        private sealed class StateQueryRequest
        {
            public string Query { get; set; }

            public string OperationName { get; set; }
        }

        private sealed class SaveNameRequest
        {
            public string SaveName { get; set; }
        }
    }
}
