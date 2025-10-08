using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace SmartFilter
{
    /// <summary>
    /// Minimal implementation of the original Lampac BaseOnlineController required by the SmartFilter module.
    /// Provides helper accessors for the current host, memory cache and cached async execution.
    /// </summary>
    public abstract class BaseOnlineController : Controller
    {
        private IMemoryCache _memoryCache;
        private string _host;

        /// <summary>
        /// Gets the application memory cache from the request services container.
        /// </summary>
        protected IMemoryCache memoryCache
        {
            get
            {
                if (_memoryCache != null)
                    return _memoryCache;

                var cache = HttpContext?.RequestServices?.GetService<IMemoryCache>();
                if (cache == null)
                    throw new InvalidOperationException("IMemoryCache service is not registered in the current request.");

                _memoryCache = cache;
                return _memoryCache;
            }
        }

        /// <summary>
        /// Returns the current host that should be used for building internal module links.
        /// </summary>
        protected string host
        {
            get
            {
                if (!string.IsNullOrEmpty(_host))
                    return _host;

                var request = HttpContext?.Request;
                if (request == null)
                    return string.Empty;

                string scheme = GetForwardedHeader(request.Headers, "X-Forwarded-Proto") ?? request.Scheme;
                string hostHeader = GetForwardedHeader(request.Headers, "X-Forwarded-Host") ?? request.Host.Value;

                if (string.IsNullOrWhiteSpace(hostHeader))
                    return string.Empty;

                _host = $"{scheme}://{hostHeader}";
                return _host;
            }
        }

        /// <summary>
        /// Executes the provided asynchronous factory and caches the resulting task for the specified duration.
        /// Subsequent calls reuse the cached task; failures automatically clear the cache entry.
        /// </summary>
        protected Task<T> InvokeCache<T>(string cacheKey, TimeSpan lifetime, Func<Task<T>> factory)
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));

            if (string.IsNullOrWhiteSpace(cacheKey) || lifetime <= TimeSpan.Zero)
                return factory();

            if (memoryCache.TryGetValue<Task<T>>(cacheKey, out var cachedTask))
                return cachedTask;

            var task = factory();

            memoryCache.Set(cacheKey, task, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = lifetime
            });

            task.ContinueWith(t =>
            {
                if (t.IsFaulted || t.IsCanceled)
                    memoryCache.Remove(cacheKey);
            }, TaskScheduler.Default);

            return task;
        }

        protected ContentResult ContentTo(string content, string contentType = "text/html; charset=utf-8")
            => Content(content ?? string.Empty, contentType);

        private static string GetForwardedHeader(IHeaderDictionary headers, string key)
        {
            if (!headers.TryGetValue(key, out var values))
                return null;

            var value = values.ToString();
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return value.Split(',').Select(v => v.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        }
    }
}
