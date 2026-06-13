using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticDSP.Serialization;

namespace AutomaticDSP.GameControl
{
    internal sealed class PendingGameControlRequest
    {
        public PendingGameControlRequest(Func<JsonObject> action)
            : this(request => request.TrySetResult(action()))
        {
        }

        public PendingGameControlRequest(Action<PendingGameControlRequest> action)
        {
            Action = action;
            Completion = new TaskCompletionSource<JsonObject>();
        }

        public Action<PendingGameControlRequest> Action { get; }

        public TaskCompletionSource<JsonObject> Completion { get; }

        public CancellationTokenRegistration Cancellation { get; set; }

        public void TrySetResult(JsonObject value)
        {
            Cancellation.Dispose();
            Completion.TrySetResult(value);
        }

        public void TrySetException(Exception exception)
        {
            Cancellation.Dispose();
            Completion.TrySetException(exception);
        }

        public void TrySetCanceled()
        {
            Cancellation.Dispose();
            Completion.TrySetCanceled();
        }
    }
}
