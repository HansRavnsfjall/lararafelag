using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Threading.Tasks;
using Umbraco.Cms.Web.Common.Controllers;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Lararafelagid.Models;
using Umbraco.Cms.Web.Website.Controllers;
using Lararafelagid.Services;

namespace Lararafelagid.Controllers
{
    public class MostViewedBlogsController : SurfaceController
    {
        private readonly PlausibleService _plausible;
        private readonly IUmbracoContextAccessor _ctxAccessor;

        public MostViewedBlogsController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            PlausibleService plausible)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _plausible = plausible;
            _ctxAccessor = umbracoContextAccessor; // reuse the same accessor we pass to base
        }

        [HttpGet]
        public async Task<IActionResult> Render(int limit = 5, string period = "30d", string? pathStartsWith = "/blog")
        {
            var results = await _plausible.GetMostViewedPages(limit, period);

            var items = new List<MostViewedItem>();

            foreach (var r in results)
            {
                var path = NormalizePath(r.Page);

                if (!string.IsNullOrWhiteSpace(pathStartsWith) &&
                    !path.StartsWith(pathStartsWith, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                IPublishedContent? content = null;
                if (_ctxAccessor.TryGetUmbracoContext(out var umbCtx) && umbCtx?.Content != null)
                {
                    content = umbCtx.Content.GetByRoute(path);
                }

                items.Add(new MostViewedItem
                {
                    Content = content,
                    UrlPath = path,
                    Visitors = r.Visitors
                });
            }

            var vm = new MostViewedViewModel
            {
                Title = "Mest lisið",
                Items = items
            };

            return PartialView("~/Views/Partials/MostViewedBlogs.cshtml", vm);
        }

        private static string NormalizePath(string urlOrPath)
        {
            if (string.IsNullOrWhiteSpace(urlOrPath)) return "/";
            var s = urlOrPath.Trim();

            // strip scheme + host if present
            if (s.StartsWith("http://") || s.StartsWith("https://"))
            {
                var idx = s.IndexOf('/', s.IndexOf("//") + 2);
                s = idx >= 0 ? s.Substring(idx) : "/";
            }

            // strip query/hash
            var q = s.IndexOf('?');
            if (q >= 0) s = s[..q];
            var h = s.IndexOf('#');
            if (h >= 0) s = s[..h];

            if (!s.StartsWith('/')) s = "/" + s;
            if (s.Length > 1 && s.EndsWith('/')) s = s.TrimEnd('/');

            return s;
        }
    }
}
