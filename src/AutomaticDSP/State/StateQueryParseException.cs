using System;

namespace AutomaticDSP.State
{
    internal sealed class StateQueryParseException : Exception
    {
        public StateQueryParseException(string message)
            : base(message)
        {
        }
    }
}
