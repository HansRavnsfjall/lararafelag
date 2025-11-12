using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Core.Models.PublishedContent;
using Lararafelagid.Models;
using Lararafelagid.Services;

public class MostViewedBlogsViewComponent : ViewComponent
{
    private readonly PlausibleService _plausible;
    private readonly IUmbracoContextAccessor _ctxAccessor;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MostViewedBlogsViewComponent> _logger;

    public MostViewedBlogsViewComponent(
        PlausibleService plausible,
        IUmbracoContextAccessor ctxAccessor,
        IMemoryCache cache,
        ILogger<MostViewedBlogsViewComponent> logger)
    {
        _plausible = plausible;
        _ctxAccessor = ctxAccessor;
        _cache = cache;
        _logger = logger;
    }

    public class MostViewedBlogsParams
    {
        public int Limit { get; init; } = 5;          // items to show
        public string Period { get; init; } = "30d";  // Plausible period: 7d, 30d, month, all
        public string? PathStartsWith { get; init; }  // optional filter like "/blog" or "/tidindi"
        public string Title { get; init; } = "Mest lisið";
        public string[] AllowedDocTypeAliases { get; init; } = Array.Empty<string>(); // optional filter, e.g. new[]{"blogPost","tidindaelement"}
        public int CacheMinutes { get; init; } = 10;  // cache to avoid hitting API every request
    }

    public async Task<IViewComponentResult> InvokeAsync(
        int limit = 5,
        string period = "30d",
        string? pathStartsWith = null,
        string title = "Mest lisið",
        string[]? allowedDocTypeAliases = null,
        int cacheMinutes = 10)
    {
        var prms = new MostViewedBlogsParams
        {
            Limit = limit,
            Period = period,
            PathStartsWith = pathStartsWith,
            Title = title,
            AllowedDocTypeAliases = allowedDocTypeAliases ?? Array.Empty<string>(),
            CacheMinutes = cacheMinutes
        };

        var cacheKey = $"most-viewed:{limit}:{period}:{pathStartsWith}:{string.Join(",", prms.AllowedDocTypeAliases)}";
        var vm = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(prms.CacheMinutes);
            return await BuildViewModelAsync(prms);
        });

        return View("~/Views/Shared/Components/MostViewedBlogs/Default.cshtml", vm);
    }

    private async Task<MostViewedViewModel> BuildViewModelAsync(MostViewedBlogsParams prms)
    {
        var plausible = await _plausible.GetMostViewedPages(prms.Limit, prms.Period);

        // Optional filter: only keep paths starting with given prefix
        var filtered = plausible
            .Where(r =>
            {
                var path = NormalizePath(r.Page);
                if (!string.IsNullOrWhiteSpace(prms.PathStartsWith))
                {
                    var pfx = prms.PathStartsWith!.Trim();
                    if (!pfx.StartsWith("/")) pfx = "/" + pfx;
                    return path.StartsWith(pfx, StringComparison.OrdinalIgnoreCase);
                }
                return true;
            })
            .ToList();

        var items = new List<MostViewedItem>();

        foreach (var row in filtered)
        {
            var path = NormalizePath(row.Page);
            IPublishedContent? content = null;

            try
            {
                if (_ctxAccessor.TryGetUmbracoContext(out var umbCtx) && umbCtx?.Content != null)
                {
                    // Try to find content by route (culture-agnostic)
                    content = umbCtx.Content.GetByRoute(path);

                    // Optionally filter by doctype alias
                    if (content != null && prms.AllowedDocTypeAliases.Length > 0)
                    {
                        var alias = content.ContentType?.Alias ?? string.Empty;
                        if (!prms.AllowedDocTypeAliases.Any(a => a.Equals(alias, StringComparison.OrdinalIgnoreCase)))
                        {
                            content = null; // reject if not in allow-list
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve Umbraco node for path {Path}", path);
            }

            items.Add(new MostViewedItem
            {
                Content = content,
                UrlPath = path,
                Visitors = row.Visitors
            });
        }

        // keep order from Plausible (already sorted by visitors desc)
        return new MostViewedViewModel
        {
            Title = prms.Title,
            Items = items
        };
    }

    private static string NormalizePath(string urlOrPath)
    {
        if (string.IsNullOrWhiteSpace(urlOrPath)) return "/";
        var s = urlOrPath.Trim();

        // remove scheme + host if present
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var idx = s.IndexOf('/', s.IndexOf("//", StringComparison.Ordinal) + 2);
            s = idx >= 0 ? s.Substring(idx) : "/";
        }

        // strip query/hash
        var q = s.IndexOf('?');
        if (q >= 0) s = s[..q];
        var h = s.IndexOf('#');
        if (h >= 0) s = s[..h];

        // ensure it starts with /
        if (!s.StartsWith('/')) s = "/" + s;

        // trim trailing slash (except root)
        if (s.Length > 1 && s.EndsWith('/')) s = s.TrimEnd('/');

        return s;
    }
}
