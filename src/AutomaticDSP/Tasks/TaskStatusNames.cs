namespace AutomaticDSP.Tasks
{
    internal static class TaskStatusNames
    {
        public const string TaskQueued = "QUEUED";
        public const string TaskRunning = "RUNNING";
        public const string TaskSucceeded = "SUCCEEDED";
        public const string TaskFailed = "FAILED";
        public const string TaskCancelRequested = "CANCEL_REQUESTED";
        public const string TaskCancelled = "CANCELLED";

        public const string CommandPending = "PENDING";
        public const string CommandRunning = "RUNNING";
        public const string CommandSucceeded = "SUCCEEDED";
        public const string CommandFailed = "FAILED";
        public const string CommandSkipped = "SKIPPED";
        public const string CommandCancelled = "CANCELLED";
    }
}
