using AutomaticDSP.Serialization;

namespace AutomaticDSP.State
{
    internal sealed class StateSnapshot
    {
        public StateSnapshot(long id, long gameTick, JsonObject data)
        {
            Id = id;
            GameTick = gameTick;
            Data = data;
        }

        public JsonObject Data { get; }
        public long GameTick { get; }
        public long Id { get; }
    }
}
