using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Lararafelagid.Services.MostViewed
{
    public sealed class ViewCountService : IViewCountService, IMostViewedQueryService, IHostedService, IDisposable
    {
        private readonly ILogger<ViewCountService> _log;
        private readonly IConfiguration _cfg;
        private readonly IMemoryCache _cache;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly string _storePath;
        private readonly int _flushSeconds;
        private readonly int _ipThrottleMinutes;
        private readonly int _daysWindow;
        private readonly int _queryCacheMinutes;

        // In-memory rolling buckets: date -> nodeId -> count
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, int>> _inMem
            = new(StringComparer.Ordinal);

        private Timer? _timer;
        private readonly object _fileLock = new();

        public ViewCountService(
            ILogger<ViewCountService> log,
            IConfiguration cfg,
            IMemoryCache cache,
            IHostEnvironment env,
            IHostApplicationLifetime lifetime)
        {
            _log = log;
            _cfg = cfg;
            _cache = cache;
            _lifetime = lifetime;

            var root = env.ContentRootPath;
            _storePath = Path.Combine(root, _cfg["MostViewed:StoragePath"] ?? "App_Data/mostviewed-rolling.json");
            _flushSeconds = int.TryParse(_cfg["MostViewed:FlushSeconds"], out var s) ? Math.Max(15, s) : 60;
            _ipThrottleMinutes = int.TryParse(_cfg["MostViewed:IpThrottleMinutes"], out var m) ? Math.Max(0, m) : 10;
            _daysWindow = int.TryParse(_cfg["MostViewed:DaysWindow"], out var d) ? Math.Clamp(d, 7, 60) : 30;
            _queryCacheMinutes = int.TryParse(_cfg["MostViewed:CacheMinutesForQuery"], out var q) ? Math.Max(1, q) : 15;

            LoadFromDiskSafe();
            TrimWindow();
        }

        // -------- Tracking --------
        public void Track(int nodeId, string? userAgent, string? ip, bool isBackOffice, bool isPreview)
        {
            if (nodeId <= 0) return;
            if (isBackOffice || isPreview) return;
            if (LooksLikeBot(userAgent)) return;

            // Optional per-IP throttle (avoid floods on refresh)
            if (_ipThrottleMinutes > 0 && !string.IsNullOrEmpty(ip))
            {
                var k = $"mvip:{nodeId}:{ip}";
                if (_cache.TryGetValue(k, out _)) return; // recently counted
                _cache.Set(k, true, TimeSpan.FromMinutes(_ipThrottleMinutes));
            }

            var key = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var day = _inMem.GetOrAdd(key, _ => new ConcurrentDictionary<int, int>());
            day.AddOrUpdate(nodeId, 1, (_, old) => checked(old + 1));
        }

        // -------- Querying --------
        public Task<IReadOnlyList<(int nodeId, int views)>> GetTopAsync(int days, int skipTop, int take, string[] allowedAliases)
        {
            var cacheKey = $"mvq:{days}:{skipTop}:{take}:{string.Join(",", allowedAliases ?? Array.Empty<string>())}";
            if (_cache.TryGetValue(cacheKey, out IReadOnlyList<(int nodeId, int views)>? cached) && cached is not null)
                return Task.FromResult(cached);

            var cutoff = DateTime.UtcNow.Date.AddDays(-Math.Max(1, days) + 1);
            var sum = new Dictionary<int, int>();
            foreach (var kvp in _inMem)
            {
                if (!DateTime.TryParse(kvp.Key, out var dt)) continue;
                if (dt.Date < cutoff) continue;

                foreach (var inner in kvp.Value)
                {
                    sum.TryGetValue(inner.Key, out var cur);
                    sum[inner.Key] = checked(cur + inner.Value);
                }
            }

            // We don’t know doc types here; filter later when projecting to content.
            var ordered = sum
                .OrderByDescending(x => x.Value)
                .Skip(Math.Max(0, skipTop))
                .Take(Math.Max(0, take))
                .Select(x => (x.Key, x.Value))
                .ToList()
                .AsReadOnly();

            _cache.Set(cacheKey, ordered, TimeSpan.FromMinutes(_queryCacheMinutes));
            return Task.FromResult((IReadOnlyList<(int nodeId, int views)>)ordered);
        }

        // -------- Persistence --------
        private void LoadFromDiskSafe()
        {
            try
            {
                if (!File.Exists(_storePath)) return;
                var json = File.ReadAllText(_storePath);
                var loaded = JsonSerializer.Deserialize<RollingStore>(json) ?? new RollingStore();
                foreach (var (day, dict) in loaded.Days)
                {
                    var map = _inMem.GetOrAdd(day, _ => new ConcurrentDictionary<int, int>());
                    foreach (var (id, count) in dict)
                        map.AddOrUpdate(id, count, (_, __) => count);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "MostViewed: failed to load rolling store, starting clean.");
            }
        }

        private void FlushToDisk()
        {
            try
            {
                TrimWindow();
                var snap = new RollingStore
                {
                    Days = _inMem.ToDictionary(
                        d => d.Key,
                        d => d.Value.ToDictionary(x => x.Key, x => x.Value),
                        StringComparer.Ordinal)
                };

                Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
                var tmp = _storePath + ".tmp";
                var json = JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = false });
                lock (_fileLock)
                {
                    File.WriteAllText(tmp, json);
                    File.Copy(tmp, _storePath, overwrite: true);
                    File.Delete(tmp);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "MostViewed: flush failed.");
            }
        }

        private void TrimWindow()
        {
            var cutoff = DateTime.UtcNow.Date.AddDays(-_daysWindow + 1);
            foreach (var key in _inMem.Keys)
            {
                if (DateTime.TryParse(key, out var dt) && dt.Date < cutoff)
                    _inMem.TryRemove(key, out _);
            }
        }

        private static bool LooksLikeBot(string? ua)
        {
            if (string.IsNullOrWhiteSpace(ua)) return false;
            ua = ua.ToLowerInvariant();
            if (ua.Contains("bot") || ua.Contains("spider") || ua.Contains("crawler") ||
                ua.Contains("preview") || ua.Contains("headless") || ua.Contains("uptime"))
                return true;
            return false;
        }

        // -------- Hosted service (periodic flush) --------
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _timer = new Timer(_ => FlushToDisk(), null, TimeSpan.FromSeconds(_flushSeconds), TimeSpan.FromSeconds(_flushSeconds));
            _lifetime.ApplicationStopping.Register(() => FlushToDisk());
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _timer?.Change(Timeout.Infinite, 0);
            FlushToDisk();
            return Task.CompletedTask;
        }

        public void Dispose() => _timer?.Dispose();
    }
}
