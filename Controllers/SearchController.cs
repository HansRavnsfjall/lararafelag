using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Examine;
using Examine.Search;
using Umbraco.Cms.Web.Common.Controllers;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Extensions;                     // Value(..., fallback: Fallback.ToLanguage)
using Umbraco.Cms.Core;                       // Constants.UmbracoIndexes
using Umbraco.Cms.Infrastructure.Examine;     // IndexTypes.Content

namespace Lararafelagid.Controllers
{
    public class SearchController : UmbracoApiController
    {
        private readonly IExamineManager _examine;
        private readonly IUmbracoContextAccessor _ctx;
        private readonly IPublishedUrlProvider _urlProvider;
        private readonly IMemoryCache _cache;

        private static readonly string[] AliasFields = new[] { "__NodeTypeAlias", "nodeTypeAlias" };
        private static readonly string[] PathFields  = new[] { "path", "__Path" };

        // Expanded field set (incl. common SEO/tag fields and Faroese culture variants)
        private static readonly string[] DefaultSearchFields = new[]
        {
            // Core
            "nodeName",
            "yvirskrift",
            "inngangstekstur",
            "tekstur",

            // Helpful extras
            "umbracoUrlName",
            "metaTitle",
            "metaDescription",
            "metaKeywords",
            "tags",
            "categories",

            // Faroese variants commonly emitted for culture-variant content
            "nodeName_fo-FO",
            "yvirskrift_fo-FO",
            "inngangstekstur_fo-FO",
            "tekstur_fo-FO",
            "metaTitle_fo-FO",
            "metaDescription_fo-FO",
            "metaKeywords_fo-FO"
        };

        public SearchController(
            IExamineManager examine,
            IUmbracoContextAccessor ctx,
            IPublishedUrlProvider urlProvider,
            IMemoryCache cache)
        {
            _examine = examine;
            _ctx = ctx;
            _urlProvider = urlProvider;
            _cache = cache;
        }

        /// GET /umbraco/api/search/query
        /// Common:
        ///   q (also accepts Q), alias, siteRoots, groupRoots, primaryRoot,
        ///   takePerGroup, skip, take, mode=typeahead|full
        /// Alias bucket:
        ///   otherAlias=undirsida, otherLabel=Aðrar síður, primaryAlias=undirsida
        /// Optional dynamic grouping:
        ///   groupByAncestorAlias=newsListPage
        /// Debug:
        ///   diag=1|2|3|4|5, ignoreHide=1, ignoreSiteScope=1, noRegex=1
        [HttpGet]
        public IActionResult Query(
            [FromQuery(Name = "q")] string q = "",
            [FromQuery(Name = "Q")] string qUpper = "",
            [FromQuery] string alias = "tidindaelement",
            [FromQuery] string? siteRoots = null,
            [FromQuery] string? groupRoots = null,
            [FromQuery] string? primaryRoot = null,
            [FromQuery] int takePerGroup = 3,
            [FromQuery] int skip = 0,
            [FromQuery] int take = 20,
            [FromQuery] string mode = "typeahead",
            [FromQuery] int diag = 0,
            [FromQuery] string otherAlias = "undirsida",
            [FromQuery] string otherLabel = "Aðrar síður",
            [FromQuery] string? primaryAlias = null,
            [FromQuery] int ignoreHide = 0,
            [FromQuery] int ignoreSiteScope = 0,
            [FromQuery] int noRegex = 0,
            [FromQuery] string? groupByAncestorAlias = null,
            [FromQuery] int maxGroups = 0
        )
        {
            var term = string.IsNullOrWhiteSpace(q) ? (qUpper ?? "") : q;

            if (!_examine.TryGetIndex(Constants.UmbracoIndexes.ExternalIndexName, out var index) || index == null)
                return Ok(new SearchResponse());

            if (!_ctx.TryGetUmbracoContext(out var umb) || umb?.Content == null)
                return Ok(new SearchResponse());
            var umbCtx = umb; // non-null beyond this point

            var siteRootIds   = ParseKeysToIds(umbCtx, siteRoots);
            var groupRootIds  = ParseKeysToIds(umbCtx, groupRoots);
            var primaryRootId = ParseKeyToId(umbCtx, primaryRoot);

            // Use a safe array for groupRootIds to avoid nullable warnings
            var groupRootIdsSafe = groupRootIds ?? Array.Empty<int>();

            // Auto-discover groups if requested and none provided
            if ((groupRootIdsSafe.Length == 0) && !string.IsNullOrWhiteSpace(groupByAncestorAlias))
            {
                groupRootIdsSafe = DiscoverGroupRoots(umbCtx, siteRootIds, groupByAncestorAlias);
                if (maxGroups > 0 && groupRootIdsSafe.Length > maxGroups)
                    groupRootIdsSafe = groupRootIdsSafe.Take(maxGroups).ToArray();
            }

            // Allow alias="*" to disable alias constraint; default remains "tidindaelement"
            var rawAliases = alias.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool aliasIsWildcard = rawAliases.Length == 1 && rawAliases[0] == "*";
            var newsAliases = aliasIsWildcard
                ? Array.Empty<string>() // no alias constraint if user explicitly passed *
                : (rawAliases.Length == 0 ? new[] { "tidindaelement" } : rawAliases);

            var cacheKey = $"search::{term}::{alias}::{siteRoots}::{groupRoots}::{groupByAncestorAlias}::{primaryRoot}::{takePerGroup}::{skip}::{take}::{mode}::{diag}::{otherAlias}::{otherLabel}::{primaryAlias}::{ignoreHide}::{ignoreSiteScope}::{noRegex}::{maxGroups}";
            if (diag == 0 && _cache.TryGetValue(cacheKey, out var cachedObj) && cachedObj is SearchResponse cached)
                return Ok(cached);

            var searcher = index.Searcher;

            // Diagnostics
            if (diag == 1) return Ok(DiagIndex(searcher, siteRootIds, groupRootIdsSafe, newsAliases.Length == 0 ? new[] { "ALL" } : newsAliases));
            if (diag == 2) return Ok(DiagPath(umbCtx, searcher, groupRootIdsSafe, newsAliases.Length == 0 ? new[] { "ALL" } : newsAliases));
            if (diag == 3) return Ok(DiagFields(searcher));
            if (diag == 4) return Ok(DiagUndirsida(searcher, siteRootIds));
            if (diag == 5)
            {
                var aliasForDiag = !string.IsNullOrWhiteSpace(primaryAlias) ? primaryAlias : otherAlias;
                return Ok(DiagAliasBucket(searcher, aliasForDiag ?? "undirsida", siteRootIds, term, umbCtx, includeHidden: ignoreHide == 1, applySiteScope: ignoreSiteScope == 0, useRegex: noRegex == 0));
            }

            var response = new SearchResponse
            {
                Query  = term,
                Groups = new List<SearchGroup>(),
                Totals = new Totals()
            };

            try
            {
                // 1) Primary (left column)
                if (!string.IsNullOrWhiteSpace(primaryAlias))
                {
                    var primQuery = BuildAliasBucket(searcher, primaryAlias, siteRootIds, term, applySiteScope: ignoreSiteScope == 0, useRegex: noRegex == 0);
                    var primExec  = primQuery.Execute();

                    var primTotal = ignoreHide == 1 ? primExec.TotalItemCount : CountVisible(umbCtx, primExec);
                    var primItems = ignoreHide == 1 ? MapTop(umbCtx, primExec.Skip(skip).Take(take), _urlProvider)
                                                    : PickTopVisible(umbCtx, primExec, take, skip, _urlProvider);

                    response.Primary = new SearchGroup
                    {
                        Kind     = "alias",
                        Alias    = primaryAlias,
                        RootId   = 0,
                        RootKey  = null,
                        RootName = primaryAlias.Equals(otherAlias, StringComparison.OrdinalIgnoreCase) ? otherLabel : primaryAlias,
                        Total    = primTotal,
                        Items    = primItems
                    };
                }
                else if (primaryRootId > 0)
                {
                    var prim = BuildNewsBase(searcher.CreateQuery(IndexTypes.Content), newsAliases, siteRootIds, term,
                                     applySiteScope: ignoreSiteScope == 0, useRegex: noRegex == 0)
                               .And().NativeQuery(AnyPathClause(primaryRootId));

                    var primRes = prim.Execute();
                    var primaryNode = umbCtx.Content?.GetById(primaryRootId);

                    response.Primary = new SearchGroup
                    {
                        Kind     = "root",
                        Alias    = null,
                        RootId   = primaryRootId,
                        RootKey  = primaryNode?.Key.ToString(),
                        RootName = primaryNode?.Name ?? string.Empty,
                        Total    = primRes.TotalItemCount,
                        Items    = MapTop(umbCtx, primRes.Skip(skip).Take(take), _urlProvider)
                    };
                }

                // 2) Right column: groups
            var effectiveGroupRoots = (groupRootIds != null && groupRootIds.Length > 0)
    ? groupRootIds
    : (primaryRootId > 0
        ? siteRootIds.Where(id => id != primaryRootId).ToArray()
        : siteRootIds);


                foreach (var rootId in effectiveGroupRoots)
                {
                    var gq  = BuildNewsBase(searcher.CreateQuery(IndexTypes.Content), newsAliases, siteRootIds, term,
                                     applySiteScope: ignoreSiteScope == 0, useRegex: noRegex == 0)
                              .And().NativeQuery(AnyPathClause(rootId));

                    var grs = gq.Execute();
                    var rootNode = umbCtx.Content?.GetById(rootId);

                    response.Groups.Add(new SearchGroup
                    {
                        Kind     = "root",
                        Alias    = null,
                        RootId   = rootId,
                        RootKey  = rootNode?.Key.ToString(),
                        RootName = rootNode?.Name ?? string.Empty,
                        Total    = grs.TotalItemCount,
                        Items    = MapTop(umbCtx, grs.Take(takePerGroup), _urlProvider)
                    });
                }

                // 3) Append alias bucket "Aðrar síður" last
                if (!string.IsNullOrWhiteSpace(otherAlias))
                {
                    var aliasQuery = BuildAliasBucket(searcher, otherAlias, siteRootIds, term, applySiteScope: ignoreSiteScope == 0, useRegex: noRegex == 0);
                    var aliasExec  = aliasQuery.Execute();

                    var aliasTotal = ignoreHide == 1 ? aliasExec.TotalItemCount : CountVisible(umbCtx, aliasExec);
                    var aliasItems = ignoreHide == 1 ? MapTop(umbCtx, aliasExec.Take(takePerGroup), _urlProvider)
                                                     : PickTopVisible(umbCtx, aliasExec, takePerGroup, 0, _urlProvider);

                    response.Groups.Add(new SearchGroup
                    {
                        Kind     = "alias",
                        Alias    = otherAlias,
                        RootId   = 0,
                        RootKey  = null,
                        RootName = otherLabel,
                        Total    = aliasTotal,
                        Items    = aliasItems
                    });
                }

                // 4) Overall totals (news scope only)
                var totalsQuery = BuildNewsBase(searcher.CreateQuery(IndexTypes.Content), newsAliases, siteRootIds, term,
                                      applySiteScope: ignoreSiteScope == 0, useRegex: noRegex == 0).Execute();
                response.Totals.Overall = totalsQuery.TotalItemCount;
            }
            catch (Exception)
            {
                // soft-fail on parse errors
                response.Query = term;
            }

            _cache.Set(cacheKey, response, TimeSpan.FromSeconds(mode == "typeahead" ? 10 : 30));
            return Ok(response);
        }

        // ---------------------- Builders ----------------------

        private IBooleanOperation BuildNewsBase(
            IQuery qb,
            string[] aliasesForThisQuery,
            int[] siteRootIds,
            string term,
            bool applySiteScope,
            bool useRegex)
        {
            var b = qb.NativeQuery("*:*");

            // Constrain by alias only if we actually have aliases (alias="*" disables)
            if (aliasesForThisQuery != null && aliasesForThisQuery.Length > 0)
            {
                b = b.And().GroupedOr(AliasFields, aliasesForThisQuery);
            }

            if (applySiteScope && siteRootIds.Length > 0)
            {
                var siteOr = "(" + string.Join(" OR ", siteRootIds.Select(AnyPathClause)) + ")";
                b = b.And().NativeQuery(siteOr);
            }

            if (!string.IsNullOrWhiteSpace(term))
            {
                b = b.And().NativeQuery(BuildTextNativeClause(term, useRegex));
            }

            return b;
        }

        private IBooleanOperation BuildAliasBucket(
            ISearcher searcher,
            string alias,
            int[] siteRootIds,
            string term,
            bool applySiteScope,
            bool useRegex)
        {
            var qb = searcher.CreateQuery(IndexTypes.Content);
            var b = qb.NativeQuery("*:*");

            // Support CSV and wildcard in alias bucket
            var aliases = alias.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool wildcard = aliases.Length == 1 && aliases[0] == "*";
            if (!wildcard && aliases.Length > 0)
            {
                b = b.And().GroupedOr(AliasFields, aliases);
            }

            if (applySiteScope && siteRootIds.Length > 0)
            {
                var siteOr = "(" + string.Join(" OR ", siteRootIds.Select(AnyPathClause)) + ")";
                b = b.And().NativeQuery(siteOr);
            }

            if (!string.IsNullOrWhiteSpace(term))
            {
                b = b.And().NativeQuery(BuildTextNativeClause(term, useRegex));
            }

            return b;
        }

        // ---------------------- Diagnostics ----------------------

        private object DiagIndex(ISearcher searcher, int[] siteRootIds, int[] groupRootIds, string[] newsAliases)
        {
            var qb = searcher.CreateQuery(IndexTypes.Content);

            var aliasOnly = (newsAliases.Length == 0)
                ? qb.NativeQuery("*:*").Execute().TotalItemCount
                : qb.NativeQuery("*:*").And().GroupedOr(AliasFields, newsAliases).Execute().TotalItemCount;

            long aliasPlusSite = -1;
            if (siteRootIds.Length > 0)
            {
                var siteOr = "(" + string.Join(" OR ", siteRootIds.Select(AnyPathClause)) + ")";
                var q = qb.NativeQuery("*:*");
                if (newsAliases.Length > 0) q = q.And().GroupedOr(AliasFields, newsAliases);
                aliasPlusSite = q.And().NativeQuery(siteOr).Execute().TotalItemCount;
            }

            var groupsDiag = new List<object>();
            foreach (var gr in groupRootIds)
            {
                var q = qb.NativeQuery("*:*");
                if (newsAliases.Length > 0) q = q.And().GroupedOr(AliasFields, newsAliases);
                var grRes = q.And().NativeQuery(AnyPathClause(gr)).Execute();
                groupsDiag.Add(new { groupRootId = gr, total = grRes.TotalItemCount });
            }

            return new
            {
                aliasRequested = newsAliases.Length == 0 ? new[] { "ALL" } : newsAliases,
                aliasOnly,
                aliasPlusSiteRoots = aliasPlusSite,
                groupRootBreakdown = groupsDiag
            };
        }

        private object DiagPath(IUmbracoContext umb, ISearcher searcher, int[] groupRootIds, string[] newsAliases)
        {
            var qb = searcher.CreateQuery(IndexTypes.Content);
            var q = qb.NativeQuery("*:*");
            if (newsAliases.Length > 0) q = q.And().GroupedOr(AliasFields, newsAliases);
            var aliasResults = q.Execute();

            var sample = aliasResults.Take(300).ToList();
            var counts = groupRootIds.ToDictionary(id => id, _ => 0);

            foreach (var hit in sample)
            {
                if (!int.TryParse(hit.Id, out var id)) continue;
                var c = umb.Content?.GetById(id);
                if (c == null) continue;
                var path = (c.Path ?? string.Empty).Replace(" ", string.Empty);
                foreach (var gr in groupRootIds)
                {
                    var needle = $",{gr},";
                    if (path.Contains(needle, StringComparison.Ordinal) ||
                        path.EndsWith(needle.TrimEnd(','), StringComparison.Ordinal) ||
                        path.StartsWith(needle.TrimStart(','), StringComparison.Ordinal))
                        counts[gr]++;
                }
            }

            return new
            {
                sampled = sample.Count,
                groupsByPathMembership = counts.Select(kv => new { groupRootId = kv.Key, sampleCount = kv.Value })
            };
        }

        private object DiagFields(ISearcher searcher)
        {
            var any = searcher.CreateQuery(IndexTypes.Content).NativeQuery("*:*").Execute();
            var first = any.FirstOrDefault();

            var undirsidaResults = searcher.CreateQuery(IndexTypes.Content)
                .NativeQuery("*:*")
                .And().NativeQuery("__NodeTypeAlias:undirsida")
                .Execute();

            var undirsidaFirst = undirsidaResults.FirstOrDefault();

            var sampleAny = new Dictionary<string, string>();
            if (first != null)
                foreach (var kv in first.Values.Take(25)) sampleAny[kv.Key] = kv.Value;

            var sampleUndirsida = new Dictionary<string, string>();
            if (undirsidaFirst != null)
                foreach (var kv in undirsidaFirst.Values.Take(25)) sampleUndirsida[kv.Key] = kv.Value;

            return new
            {
                anyCount           = any.TotalItemCount,
                hasNodeTypeAlias   = first?.Values.ContainsKey("nodeTypeAlias") ?? false,
                has__NodeTypeAlias = first?.Values.ContainsKey("__NodeTypeAlias") ?? false,
                hasPath            = first?.Values.ContainsKey("path") ?? false,
                has__Path          = first?.Values.ContainsKey("__Path") ?? false,
                undirsidaCount     = undirsidaResults.TotalItemCount,
                sampleAny,
                sampleUndirsida
            };
        }

        private object DiagUndirsida(ISearcher searcher, int[] siteRootIds)
        {
            var qb = searcher.CreateQuery(IndexTypes.Content);

            IBooleanOperation Base()
            {
                var b = qb.NativeQuery("*:*")
                          .And().NativeQuery("__NodeTypeAlias:undirsida");
                if (siteRootIds.Length > 0)
                {
                    var siteOr = "(" + string.Join(" OR ", siteRootIds.Select(AnyPathClause)) + ")";
                    b = b.And().NativeQuery(siteOr);
                }
                return b;
            }

            var baseRes = Base().Execute();
            var total   = baseRes.TotalItemCount;

            // ✅ parentheses around the ORs for NativeQuery
            var hidden = Base()
                .And().NativeQuery("(umbracoNaviHide:1 OR umbracoNaviHide:true OR umbracoNaviHide:True OR umbracoNaviHide:yes)")
                .Execute().TotalItemCount;

            var visible = total - hidden;

            var sample = baseRes.Take(5).Select(r => new
            {
                id = r.Id,
                name = r.Values.TryGetValue("nodeName", out var nm) ? nm : "",
                path = r.Values.TryGetValue("path", out var p) ? p : (r.Values.TryGetValue("__Path", out var pp) ? pp : "")
            });

            return new { undirsida = new { total, hidden, visible, sample } };
        }

        private object DiagAliasBucket(ISearcher searcher, string alias, int[] siteRootIds, string term, IUmbracoContext umb, bool includeHidden, bool applySiteScope, bool useRegex)
        {
            var q = BuildAliasBucket(searcher, alias, siteRootIds, term, applySiteScope, useRegex).Execute();
            var rawTotal = q.TotalItemCount;
            var visibleTotal = includeHidden ? rawTotal : CountVisible(umb, q);

            var sampleVisible = includeHidden
                ? MapTop(umb, q.Take(5), _urlProvider)
                : PickTopVisible(umb, q, 5, 0, _urlProvider);

            return new { alias, rawTotal, visibleTotal, sampleVisible };
        }

        // ---------------------- Helpers ----------------------

        private static string AnyPathClause(int id)
        {
            string Regex(string field) => $"{field}:/(^|.*[,\\s]){id}([,\\s].*|$)/";
            return "(" + string.Join(" OR ", PathFields.Select(Regex)) + ")";
        }

        private static int[] DiscoverGroupRoots(IUmbracoContext umb, int[] siteRootIds, string ancestorAlias)
        {
            var list = new List<int>();

            if (siteRootIds.Length > 0)
            {
                foreach (var rootId in siteRootIds)
                {
                    var root = umb.Content?.GetById(rootId);
                    if (root == null) continue;
                    list.AddRange(root.DescendantsOfType(ancestorAlias).Select(x => x.Id));
                }
            }
            else
            {
                if (umb.Content != null)
{
    foreach (var root in umb.Content.GetAtRoot())
        list.AddRange(root.DescendantsOfType(ancestorAlias).Select(x => x.Id));
}
            }

            return list.Distinct().ToArray();
        }

        private static int[] ParseKeysToIds(IUmbracoContext umb, string? csvKeys)
        {
            if (string.IsNullOrWhiteSpace(csvKeys)) return Array.Empty<int>();
            return csvKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Select(s => Guid.TryParse(s, out var g) ? umb.Content?.GetById(g)?.Id ?? 0 : 0)
                          .Where(id => id > 0)
                          .Distinct()
                          .ToArray();
        }

        private static int ParseKeyToId(IUmbracoContext umb, string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return 0;
            return Guid.TryParse(key, out var g) ? (umb.Content?.GetById(g)?.Id ?? 0) : 0;
        }

        private static string EscapeTerm(string input)
        {
            var specials = new[] { "+", "-", "&&", "||", "!", "(", ")", "{", "}", "[", "]", "^", "\"", "~", "*", "?", ":", "\\", "/" };
            foreach (var s in specials) input = input.Replace(s, $"\\{s}");
            return input;
        }

        private static string EscapeRegexLiteral(string input)
        {
            var sb = new StringBuilder(input.Length * 2);
            foreach (var ch in input)
            {
                if ("\\.^$|()[]{}*+?".Contains(ch)) sb.Append('\\');
                sb.Append(ch);
            }
            return sb.ToString();
        }

        private static bool HasWhitespace(string s) => s.Any(char.IsWhiteSpace);

        private static string QuoteForQuery(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (var ch in s)
            {
                if (ch == '\\' || ch == '"') sb.Append('\\');
                sb.Append(ch);
            }
            sb.Append('"');
            return sb.ToString();
        }

        // Fold accents/diacritics so "skulabladid" matches "Skúlablaðið"
        private static string RemoveDiacritics(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            var normalized = input.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (uc != UnicodeCategory.NonSpacingMark) sb.Append(ch);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        // CASE-robust & SAFE (limited regex/fuzzy for short tokens) + diacritic folding
        private static string BuildTextNativeClause(string term, bool useRegex = true)
        {
            if (string.IsNullOrWhiteSpace(term)) return "*:*";

            var t        = term.Trim();
            var tFold    = RemoveDiacritics(t);
            var esc      = EscapeTerm(t);
            var escFold  = EscapeTerm(tFold);
            var tLower   = t.ToLowerInvariant();
            var tFoldLo  = tFold.ToLowerInvariant();
            var escLower = EscapeTerm(tLower);
            var escFoldLo= EscapeTerm(tFoldLo);
            var hasWs    = HasWhitespace(t);
            int len      = t.Length;

            static bool IsTitleField(string f) =>
                f.Equals("nodeName", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("yvirskrift", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("nodeName_fo-FO", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("yvirskrift_fo-FO", StringComparison.OrdinalIgnoreCase);

            string regexOrig   = ".*" + EscapeRegexLiteral(t)       + ".*";
            string regexOrigLo = ".*" + EscapeRegexLiteral(tLower)  + ".*";
            string regexFold   = ".*" + EscapeRegexLiteral(tFold)   + ".*";
            string regexFoldLo = ".*" + EscapeRegexLiteral(tFoldLo) + ".*";

            bool allowFuzzyToken   = !hasWs && len >= 4;
            bool allowRegexToken   = useRegex && !hasWs && len >= 5;
            bool allowRegexPhrase  = useRegex &&  hasWs && len >= 8;

            var fieldClauses = new List<string>();
            foreach (var f in DefaultSearchFields)
            {
                var parts = new List<string>();

                if (hasWs)
                {
                    parts.Add($"{f}:{QuoteForQuery(t)}^5");
                    if (!tFold.Equals(t, StringComparison.Ordinal)) parts.Add($"{f}:{QuoteForQuery(tFold)}^4");

                    if (allowRegexPhrase && IsTitleField(f))
                    {
                        parts.Add($"{f}:/{regexOrig}/^2");
                        parts.Add($"{f}:/{regexOrigLo}/");
                        if (!t.Equals(tFold, StringComparison.Ordinal))
                        {
                            parts.Add($"{f}:/{regexFold}/^2");
                            parts.Add($"{f}:/{regexFoldLo}/");
                        }
                    }
                }
                else
                {
                    parts.Add($"{f}:{esc}^5");
                    parts.Add($"{f}:{escLower}^4");
                    if (!escFold.Equals(esc, StringComparison.Ordinal)) parts.Add($"{f}:{escFold}^4");
                    if (!escFoldLo.Equals(escLower, StringComparison.Ordinal)) parts.Add($"{f}:{escFoldLo}^3");

                    parts.Add($"{f}:{esc}*^3");
                    parts.Add($"{f}:{escLower}*^3");
                    if (!escFold.Equals(esc, StringComparison.Ordinal)) parts.Add($"{f}:{escFold}*^3");
                    if (!escFoldLo.Equals(escLower, StringComparison.Ordinal)) parts.Add($"{f}:{escFoldLo}*^3");

                    if (allowFuzzyToken && IsTitleField(f))
                    {
                        parts.Add($"{f}:{escLower}~1^2");
                        if (!escFoldLo.Equals(escLower, StringComparison.Ordinal)) parts.Add($"{f}:{escFoldLo}~1^2");
                    }

                    if (allowRegexToken && IsTitleField(f))
                    {
                        parts.Add($"{f}:/{regexOrig}/");
                        parts.Add($"{f}:/{regexOrigLo}/");
                        if (!t.Equals(tFold, StringComparison.Ordinal))
                        {
                            parts.Add($"{f}:/{regexFold}/");
                            parts.Add($"{f}:/{regexFoldLo}/");
                        }
                    }
                }

                fieldClauses.Add("(" + string.Join(" OR ", parts) + ")");
            }

            return "(" + string.Join(" OR ", fieldClauses) + ")";
        }

        private static bool IsVisible(IPublishedContent? c)
        {
            if (c == null) return false;
            var hide = c.Value<bool?>("umbracoNaviHide");
            return hide != true;
        }

        private static long CountVisible(IUmbracoContext umb, ISearchResults results)
        {
            long count = 0;
            foreach (var r in results)
            {
                if (!int.TryParse(r.Id, out var id)) continue;
                var c = umb.Content?.GetById(id);
                if (c != null && c.IsPublished() && IsVisible(c)) count++;
            }
            return count;
        }

        // Culture-aware snippet getter with sane fallbacks
        private static string? GetTekstur(IPublishedContent c)
        {
            var t = c.Value<string>("tekstur", culture: null, fallback: Fallback.ToLanguage);
            if (!string.IsNullOrWhiteSpace(t)) return t;

            var intro = c.Value<string>("inngangstekstur", culture: null, fallback: Fallback.ToLanguage);
            if (!string.IsNullOrWhiteSpace(intro)) return intro;

            return null;
        }

        private static List<SearchItem> PickTopVisible(IUmbracoContext umb, ISearchResults results, int take, int skip, IPublishedUrlProvider urlProvider)
        {
            var items = new List<SearchItem>();
            int skipped = 0;
            foreach (var r in results)
            {
                if (!int.TryParse(r.Id, out var id)) continue;
                var c = umb.Content?.GetById(id);
                if (c == null || !c.IsPublished() || !IsVisible(c)) continue;

                if (skipped < skip) { skipped++; continue; }

                items.Add(new SearchItem
                {
                    Id      = c.Id,
                    Name    = c.Name ?? string.Empty,
                    Url     = urlProvider.GetUrl(c) ?? string.Empty,
                    Tekstur = GetTekstur(c)
                });

                if (items.Count >= take) break;
            }
            return items;
        }

        private static List<SearchItem> MapTop(IUmbracoContext umb, IEnumerable<ISearchResult> results, IPublishedUrlProvider urlProvider)
        {
            var items = new List<SearchItem>();
            foreach (var hit in results)
            {
                if (!int.TryParse(hit.Id, out var id)) continue;
                var c = umb.Content?.GetById(id);
                if (c == null || !c.IsPublished()) continue;

                items.Add(new SearchItem
                {
                    Id      = c.Id,
                    Name    = c.Name ?? string.Empty,
                    Url     = urlProvider.GetUrl(c) ?? string.Empty,
                    Tekstur = GetTekstur(c)
                });
            }
            return items;
        }
    }

    // ---------------------- DTOs ----------------------

    public class SearchResponse
    {
        public string? Query { get; set; }
        public SearchGroup? Primary { get; set; }
        public List<SearchGroup> Groups { get; set; } = new List<SearchGroup>();
        public Totals Totals { get; set; } = new Totals();
    }

    public class SearchGroup
    {
        public string Kind { get; set; } = "root";   // "root" or "alias"
        public string? Alias { get; set; }           // when Kind == "alias"
        public int RootId { get; set; }              // 0 when Kind == "alias"
        public string? RootKey { get; set; }         // GUID string
        public string RootName { get; set; } = string.Empty;
        public long Total { get; set; }
        public List<SearchItem> Items { get; set; } = new List<SearchItem>();
    }

    public class SearchItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string? Tekstur { get; set; }          // raw rich-text (frontend truncates)
    }

    public class Totals
    {
        public long Overall { get; set; }
    }
}
