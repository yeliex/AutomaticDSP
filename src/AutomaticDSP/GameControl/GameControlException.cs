using System;

namespace AutomaticDSP.GameControl
{
    internal sealed class GameControlException : Exception
    {
        public GameControlException(string code, string message, string status)
            : base(message)
        {
            Code = code;
            Status = status;
        }

        public string Code { get; }

        public string Status { get; }
    }
}
