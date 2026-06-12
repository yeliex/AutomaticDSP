using System.Collections.Generic;
using AutomaticDSP.Serialization;

namespace AutomaticDSP.Tasks
{
    internal sealed class TaskStateStore
    {
        public JsonObject GetActiveTasksResponse()
        {
            return new JsonObject
            {
                ["tasks"] = new List<object>()
            };
        }
    }
}
