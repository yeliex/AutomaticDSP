using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutomaticDSP.Tasks
{
    internal sealed class TaskSubmitRequest
    {
        public string ClientRequestId { get; set; }

        public bool? StopOnFailure { get; set; }

        public bool Immediate { get; set; }

        public List<TaskCommandRequest> Commands { get; set; }
    }

    internal sealed class TaskCommandRequest
    {
        public string Id { get; set; }

        public string Type { get; set; }

        public double? TimeoutSeconds { get; set; }

        public double? DurationSeconds { get; set; }

        public long? DurationTicks { get; set; }

        public JToken Condition { get; set; }

        public List<string> DependsOn { get; set; }

        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }
}
