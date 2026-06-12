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
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace AutomaticDSP.Api
{
    internal sealed class HttpApiServer : IDisposable
    {
        private readonly HistoryStore historyStore;
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
            int port,
            StateSnapshotService snapshotService,
            TaskStateStore taskStateStore,
            HistoryStore historyStore,
            ManualLogSource log)
        {
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
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            listenTask = Task.Run(() => ListenLoop(cancellation.Token));
            log.LogInfo($"AutomaticDSP HTTP API listening on http://127.0.0.1:{port}/");
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
                if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(context, 405, Error("method_not_allowed", "Only GET is supported in M1."));
                    return;
                }

                var path = context.Request.Url.AbsolutePath;
                if (path == "/health")
                {
                    WriteJson(context, 200, snapshotService.GetHealth());
                }
                else if (path == "/state")
                {
                    var snapshot = snapshotService.GetLatestSnapshot();
                    if (snapshot == null)
                    {
                        WriteJson(context, 503, Error("state_unavailable", "State snapshot is not available yet."));
                    }
                    else
                    {
                        WriteJson(context, 200, snapshot.Data);
                    }
                }
                else if (path == "/tasks")
                {
                    WriteJson(context, 200, taskStateStore.GetActiveTasksResponse());
                }
                else if (path == "/history")
                {
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
                response.Headers["Access-Control-Allow-Origin"] = "*";
                response.OutputStream.Write(body, 0, body.Length);
            }
            finally
            {
                response.OutputStream.Close();
            }
        }
    }
}
