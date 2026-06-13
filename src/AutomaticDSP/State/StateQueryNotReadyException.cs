using System;

namespace AutomaticDSP.State
{
    internal sealed class StateQueryNotReadyException : Exception
    {
        public StateQueryNotReadyException(string status)
            : base("Game state is not ready for query.")
        {
            Status = status;
        }

        public string Status { get; }
    }
}
