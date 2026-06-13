using System;
using AutomaticDSP.Serialization;

namespace AutomaticDSP.GameControl
{
    internal sealed class DelayedGameStart
    {
        public DelayedGameStart(PendingGameControlRequest request, Action start, Func<JsonObject> response, int framesRemaining)
        {
            Request = request;
            Start = start;
            Response = response;
            FramesRemaining = framesRemaining;
        }

        public PendingGameControlRequest Request { get; }

        public Action Start { get; }

        public Func<JsonObject> Response { get; }

        public int FramesRemaining { get; set; }
    }
}
