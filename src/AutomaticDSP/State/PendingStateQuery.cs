using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticDSP.Serialization;

namespace AutomaticDSP.State
{
    internal sealed class PendingStateQuery
    {
        public PendingStateQuery(StateQueryPlan plan)
        {
            Plan = plan;
            Completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public StateQueryPlan Plan { get; }

        public TaskCompletionSource<JsonObject> Completion { get; }

        public CancellationTokenRegistration Cancellation { get; set; }

        public void TrySetResult(JsonObject result)
        {
            try
            {
                if (!Completion.Task.IsCompleted)
                {
                    Completion.TrySetResult(result);
                }
            }
            finally
            {
                Cancellation.Dispose();
            }
        }

        public void TrySetException(Exception exception)
        {
            try
            {
                if (!Completion.Task.IsCompleted)
                {
                    Completion.TrySetException(exception);
                }
            }
            finally
            {
                Cancellation.Dispose();
            }
        }

        public void TrySetCanceled()
        {
            try
            {
                if (!Completion.Task.IsCompleted)
                {
                    Completion.TrySetCanceled();
                }
            }
            finally
            {
                Cancellation.Dispose();
            }
        }
    }
}
