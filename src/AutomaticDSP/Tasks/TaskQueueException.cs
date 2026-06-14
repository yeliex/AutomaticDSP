using System;

namespace AutomaticDSP.Tasks
{
    internal sealed class TaskQueueException : Exception
    {
        public TaskQueueException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
