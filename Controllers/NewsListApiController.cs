// /Controllers/NewsListApiController.cs
using System;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Web.Common.Controllers;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace lararafelagid
{
    [AllowAnonymous]
    public class NewsListApiController : UmbracoApiController
    {
        private readonly IUmbracoContextFactory _contextFactory;

        public NewsListApiController(IUmbracoContextFactory contextFactory) => _contextFactory = contextFactory;

        // GET /umbraco/api/newslistapi/load
        // Supports:
        //   - containerId=<guid>                       (single root; backward compatible)
        //   - containerIds=<guid,guid,...>             (multiple roots)
        //   - skip, take, includeDescendants, year
        [HttpGet]
        public IActionResult Load(
            Guid? containerId = null,
            string? containerIds = null,
            int skip = 0,
            int take = 12,
            bool includeDescendants = false,
            int? year = null)
        {
            using var cref = _contextFactory.EnsureUmbracoContext();
            var cache = cref.UmbracoContext.Content;

            // Collect root GUIDs: prefer containerIds if provided; otherwise fallback to single containerId
            var rootKeys = new List<Guid>();

            if (!string.IsNullOrWhiteSpace(containerIds))
            {
                foreach (var part in containerIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (Guid.TryParse(part, out var g)) rootKeys.Add(g);
                }
            }
            else if (containerId.HasValue)
            {
                rootKeys.Add(containerId.Value);
            }

            if (rootKeys.Count == 0)
            {
                return BadRequest(new { message = "No container id(s) provided. Use containerId or containerIds." });
            }

            var faroeTz = GetFaroeTimeZone();
            var nowUtc  = DateTime.UtcNow;

            // Helper: prefer dagfesting; fallback to CreateDate; interpret as Faroe-local clock time and normalize to UTC
            DateTime GetDateUtc(Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent x)
            {
                var d = x.Value<DateTime?>("dagfesting") ?? x.CreateDate;

                // If it is explicitly UTC, respect that
                if (d.Kind == DateTimeKind.Utc)
                    return d;

                // For Local or Unspecified: treat the stored clock time as Faroe local
                var naive = new DateTime(d.Year, d.Month, d.Day, d.Hour, d.Minute, d.Second, DateTimeKind.Unspecified);
                return TimeZoneInfo.ConvertTimeToUtc(naive, faroeTz);
            }

            // Build combined source from one or more roots
            IEnumerable<Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent> combined = Enumerable.Empty<Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent>();

            foreach (var key in rootKeys.Distinct())
            {
                var root = cache?.GetById(key);
                if (root is null) continue;

                // Allow picking either a folder/section OR a single tidindaelement
                var selfIfItem = root.ContentType.Alias.InvariantEquals("tidindaelement")
                    ? new[] { root }
                    : Enumerable.Empty<Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent>();

                var branch = includeDescendants
                    ? root.DescendantsOfType("tidindaelement")
                    : root.ChildrenOfType("tidindaelement");

                combined = (combined ?? Enumerable.Empty<IPublishedContent>())
                    .Concat(selfIfItem ?? Enumerable.Empty<IPublishedContent>())
                    .Concat(branch ?? Enumerable.Empty<IPublishedContent>());
            }

            if (!combined.Any())
            {
                return Ok(new { total = 0, items = Array.Empty<object>() });
            }

            // Merge duplicates across roots (if any) by content Key
            var source = combined
                .GroupBy(x => x.Key)
                .Select(g => g.First());

            // Base visibility + hideInMenu, then enforce UTC "now/past"
            var filtered = source
                .Where(x => x.IsVisible() && x.Value<bool?>("hideInMenu") != true)
                .Where(x => GetDateUtc(x) <= nowUtc);

            // Optional year filter (also based on normalized UTC date)
            if (year.HasValue)
            {
                filtered = filtered.Where(x => GetDateUtc(x).Year == year.Value);
            }

            // Order newest first by normalized date
            var ordered = filtered
                .OrderByDescending(GetDateUtc)
                .ToList();

            var total = ordered.Count;

            // Page
            if (skip < 0) skip = 0;
            if (take <= 0) take = 12;

            var page = ordered.Skip(skip).Take(take);

            // Culture: prefer fo-FO, fallback da-DK, then invariant
            var culture = TryGetCulture("fo-FO") ?? TryGetCulture("da-DK") ?? CultureInfo.InvariantCulture;

            // For display, show as Faroe local date text
            var items = page.Select(x =>
            {
                var dateLocal = TimeZoneInfo.ConvertTimeFromUtc(GetDateUtc(x), faroeTz);
                return new
                {
                    url        = x.Url(),
                    title      = x.Value<string>("yvirskrift") ?? x.Name,
                    intro      = x.Value<string>("inngangstekstur"),
                    dateText   = dateLocal.ToString("dd.MM.yyyy", culture),
                    originRootKey = x.Root().Key // expose origin site root for UI decisions
                };
            });

            return Ok(new { total, items });
        }

        // Try to obtain the Faroe timezone on both Linux (IANA) and Windows (TZDB/Windows ID)
        private static TimeZoneInfo GetFaroeTimeZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Atlantic/Faroe"); } catch { /* linux/iana */ }
            try { return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); } catch { /* windows alt */ }
            return TimeZoneInfo.Utc; // last resort (won't break filtering)
        }

        private static CultureInfo? TryGetCulture(string name)
        {
            try { return new CultureInfo(name); } catch { return null; }
        }
    }
}
