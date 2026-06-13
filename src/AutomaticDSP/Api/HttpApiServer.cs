using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutomaticDSP.Serialization;
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
        private static readonly TimeSpan StateQueryTimeout = TimeSpan.FromSeconds(10);
        private readonly HistoryStore historyStore;
        private readonly string host;
        private readonly ManualLogSource log;
        private readonly int port;
        private readonly StateSnapshotService snapshotService;
        private readonly TaskStateStore taskStateStore;
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
            StateSnapshotService snapshotService,
            TaskStateStore taskStateStore,
            HistoryStore historyStore,
            ManualLogSource log)
        {
            this.host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
            this.port = port;
            this.snapshotService = snapshotService;
            this.taskStateStore = taskStateStore;
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
                    if (!IsGet(context))
                    {
                        WriteJson(context, 405, Error("method_not_allowed", "Only GET is supported for /game."));
                        return;
                    }

                    WriteJson(context, 200, snapshotService.GetGameStatus());
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
                else if (path == "/tasks")
                {
                    if (!IsGet(context))
                    {
                        WriteJson(context, 405, Error("method_not_allowed", "Only GET is supported for /tasks."));
                        return;
                    }

                    WriteJson(context, 200, taskStateStore.GetActiveTasksResponse());
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
            catch (Exception ex)
            {
                log.LogWarning($"HTTP request failed: {ex}");
                WriteJson(context, 500, Error("internal_error", ex.Message));
            }
        }

        private void HandleStateQuery(HttpListenerContext context)
        {
            if (!snapshotService.IsStateQueryReady(out var status))
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
                    var data = snapshotService.EnqueueStateQuery(plan, timeout.Token).GetAwaiter().GetResult();
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

        private static StateQueryRequest ReadStateQueryRequest(HttpListenerContext context)
        {
            using (var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8))
            {
                var body = reader.ReadToEnd();
                return JsonConvert.DeserializeObject<StateQueryRequest>(body);
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

        private static bool IsGet(HttpListenerContext context)
        {
            return string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOptions(HttpListenerContext context)
        {
            return string.Equals(context.Request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase);
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
    }
}
