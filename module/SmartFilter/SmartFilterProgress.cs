using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SmartFilter
{
    internal static class SmartFilterProgress
    {
        private const string ProgressSuffix = ":progress";
        private static readonly TimeSpan ProgressTtl = TimeSpan.FromMinutes(5);

        public static string BuildProgressKey(string cacheKey)
        {
            if (string.IsNullOrEmpty(cacheKey))
                return null;

            return cacheKey + ProgressSuffix;
        }

        public static void Initialize(IMemoryCache cache, string progressKey, IEnumerable<ProviderDescriptor> providers)
        {
            if (cache == null || string.IsNullOrEmpty(progressKey) || providers == null)
                return;

            var statuses = providers
                .Select(p => new ProviderStatus
                {
                    Name = p.Name,
                    Plugin = p.Plugin,
                    Index = p.Index,
                    Status = "pending"
                })
                .ToList();

            var state = new ProgressState(statuses);
            cache.Set(progressKey, state, ProgressTtl);
        }

        public static void MarkRunning(IMemoryCache cache, string progressKey, string providerName)
        {
            Update(cache, progressKey, providerName, status =>
            {
                if (status.Status == "pending")
                    status.Status = "running";
            });
        }

        public static void MarkResult(IMemoryCache cache, string progressKey, ProviderFetchResult result)
        {
            if (result == null)
                return;

            Update(cache, progressKey, result.ProviderName, status =>
            {
                status.ResponseTime = result.ResponseTime;
                status.Items = result.ItemsCount;
                status.Error = result.Success ? null : result.ErrorMessage;
                status.Status = result.Success
                    ? (result.HasContent ? "completed" : "empty")
                    : "error";
            });
        }

        public static void UpdatePartial(IMemoryCache cache, string progressKey, JArray partial, AggregationMetadata metadata, bool ready)
        {
            if (cache == null || string.IsNullOrEmpty(progressKey))
                return;

            if (!cache.TryGetValue(progressKey, out ProgressState state))
                return;

            lock (state.SyncRoot)
            {
                state.Partial = partial != null ? (JArray)partial.DeepClone() : null;
                state.Metadata = metadata?.Clone();
                if (ready)
                    state.MarkReady();

                cache.Set(progressKey, state, ProgressTtl);
            }
        }

        public static void PublishFinal(IMemoryCache cache, string progressKey, IEnumerable<ProviderStatus> statuses, JArray finalPayload = null, AggregationMetadata metadata = null)
        {
            if (cache == null || string.IsNullOrEmpty(progressKey))
                return;

            var list = statuses?.Select(s => s.Clone()).ToList() ?? new List<ProviderStatus>();
            if (!cache.TryGetValue(progressKey, out ProgressState state))
                state = new ProgressState(list);

            lock (state.SyncRoot)
            {
                if (list.Count == 0 && state.Providers.Count > 0)
                    list = state.Providers.Select(p => p.Clone()).ToList();

                foreach (var status in list)
                {
                    if (string.IsNullOrEmpty(status.Status))
                        status.Status = status.HasContent ? "completed" : (string.IsNullOrEmpty(status.Error) ? "empty" : "error");
                }

                state.Replace(list, ready: true, finalPayload, metadata);
                cache.Set(progressKey, state, ProgressTtl);
            }
        }

        public static ProgressSnapshot Snapshot(IMemoryCache cache, string progressKey)
        {
            if (cache == null || string.IsNullOrEmpty(progressKey))
                return null;

            if (!cache.TryGetValue(progressKey, out ProgressState state))
                return null;

            lock (state.SyncRoot)
            {
                return state.ToSnapshot();
            }
        }

        private static void Update(IMemoryCache cache, string progressKey, string providerName, Action<ProviderStatus> updater)
        {
            if (cache == null || string.IsNullOrEmpty(progressKey) || string.IsNullOrEmpty(providerName) || updater == null)
                return;

            if (!cache.TryGetValue(progressKey, out ProgressState state))
                return;

            bool updated = false;

            lock (state.SyncRoot)
            {
                foreach (var status in state.Providers)
                {
                    if (string.Equals(status.Name, providerName, StringComparison.OrdinalIgnoreCase))
                    {
                        updater(status);
                        updated = true;
                        break;
                    }
                }

                if (updated)
                    cache.Set(progressKey, state, ProgressTtl);
            }
        }

        private sealed class ProgressState
        {
            public ProgressState(List<ProviderStatus> providers)
            {
                Providers = providers ?? new List<ProviderStatus>();
            }

            public List<ProviderStatus> Providers { get; private set; }
            public bool Ready { get; private set; }
            public AggregationMetadata Metadata { get; set; }
            public JArray Partial { get; set; }
            public object SyncRoot { get; } = new object();

            public void Replace(List<ProviderStatus> providers, bool ready, JArray partial, AggregationMetadata metadata)
            {
                Providers = providers ?? new List<ProviderStatus>();
                Ready = ready;
                if (partial != null)
                    Partial = (JArray)partial.DeepClone();
                if (metadata != null)
                    Metadata = metadata.Clone();
            }

            public void MarkReady()
            {
                Ready = true;
            }

            public ProgressSnapshot ToSnapshot()
            {
                var completed = Providers.Count(p => p.Completed);
                return new ProgressSnapshot
                {
                    Ready = Ready || (Providers.Count > 0 && completed == Providers.Count),
                    Total = Providers.Count,
                    Completed = completed,
                    Items = Providers.Sum(p => Math.Max(0, p.Items)),
                    Providers = Providers.Select(p => p.Clone()).ToList(),
                    Metadata = Metadata?.Clone(),
                    Partial = Partial != null ? (JArray)Partial.DeepClone() : null
                };
            }
        }
    }
}
