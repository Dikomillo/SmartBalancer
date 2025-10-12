using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;

namespace SmartFilter
{
    public class ProviderStatus
    {
        public string Name { get; set; }
        public string Plugin { get; set; }
        public int? Index { get; set; }
        public string Status { get; set; } = "pending";
        public int Items { get; set; }
        public int ResponseTime { get; set; }
        public string Error { get; set; }

        [JsonIgnore]
        public bool HasContent => Items > 0;

        [JsonIgnore]
        public bool Completed => Status == "completed" || Status == "empty" || Status == "error";

        public ProviderStatus Clone()
        {
            return new ProviderStatus
            {
                Name = Name,
                Plugin = Plugin,
                Index = Index,
                Status = Status,
                Items = Items,
                ResponseTime = ResponseTime,
                Error = Error
            };
        }
    }

    public class AggregationResult
    {
        public string Type { get; set; }
        public JToken Data { get; set; }
        public string Html { get; set; }
        public List<ProviderStatus> Providers { get; set; } = new List<ProviderStatus>();
        public string ProgressKey { get; set; }
    }

    public class ProgressSnapshot
    {
        public bool Ready { get; set; }
        public int Total { get; set; }
        public int Completed { get; set; }
        public int Items { get; set; }
        public List<ProviderStatus> Providers { get; set; } = new List<ProviderStatus>();

        [JsonProperty("progress")]
        public int ProgressPercentage => Total == 0 ? 0 : (int)System.Math.Round((double)Completed * 100 / Total);
    }
}
