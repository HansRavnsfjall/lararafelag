using System.Linq;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;

namespace Lararafelagid
{
    public static class SiteSettingsExtensions
    {
        /// <summary>
        /// Returns the first child with alias "siteSettings" under the root.
        /// </summary>
        public static IPublishedContent? GetSiteSettings(this IPublishedContent? content, string alias = "siteSettings")
        {
            if (content is null) return null;

            var root = content.AncestorOrSelf(1);
            if (root is null) return null;

            // Coalesce to an empty sequence so FirstOrDefault never receives a null enumerable
            var candidates = root.ChildrenOfType(alias) ?? Enumerable.Empty<IPublishedContent>();
            return candidates.FirstOrDefault();
        }
    }
}
