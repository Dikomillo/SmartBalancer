using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
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
        public AggregationMetadata Metadata { get; set; }
    }

    public class AggregationMetadata
    {
        public Dictionary<string, AggregationFacet> Qualities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, AggregationFacet> Voices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public int TotalItems { get; set; }

        public AggregationMetadata Clone()
        {
            return new AggregationMetadata
            {
                TotalItems = TotalItems,
                Qualities = Qualities?.ToDictionary(k => k.Key, v => v.Value.Clone(), StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, AggregationFacet>(StringComparer.OrdinalIgnoreCase),
                Voices = Voices?.ToDictionary(k => k.Key, v => v.Value.Clone(), StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, AggregationFacet>(StringComparer.OrdinalIgnoreCase)
            };
        }
    }

    public class AggregationFacet
    {
        public string Code { get; set; }
        public string Label { get; set; }
        public int Count { get; set; }

        public AggregationFacet Clone() => new AggregationFacet { Code = Code, Label = Label, Count = Count };
    }

    public class ProgressSnapshot
    {
        public bool Ready { get; set; }
        public int Total { get; set; }
        public int Completed { get; set; }
        public int Items { get; set; }
        public List<ProviderStatus> Providers { get; set; } = new List<ProviderStatus>();
        public AggregationMetadata Metadata { get; set; }
        public JArray Partial { get; set; }

        [JsonProperty("progress")]
        public int ProgressPercentage => Total == 0 ? 0 : (int)System.Math.Round((double)Completed * 100 / Total);
    }
}
