using System.Collections.Generic;

namespace AutomaticDSP.State
{
    internal sealed class StateQueryPlan
    {
        public StateQueryPlan()
        {
            Fields = new List<StateQueryField>();
        }

        public List<StateQueryField> Fields { get; }
    }
}
