// /Controllers/DedupeController.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Cms.Web.BackOffice.Controllers;
using Umbraco.Cms.Web.Common.Attributes;

namespace YourNamespace.Tools
{
    [IsBackOffice]
    [Produces("application/json")]
    [Route("umbraco/backoffice/api/dedupe")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public class DedupeController : UmbracoAuthorizedApiController
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IContentService _contentService;

        public DedupeController(IScopeProvider scopeProvider, IContentService contentService)
        {
            _scopeProvider = scopeProvider;
            _contentService = contentService;
        }

        // -------- DTOs --------
        public class DuplicateGroup
        {
            public string Name { get; set; } = "";
            public DateTime? Dagfesting { get; set; }
            public int KeepId { get; set; }
            public List<int> DeleteIds { get; set; } = new();
            public List<int> AllIds { get; set; } = new();
        }

        private class Row
        {
            public int NodeId { get; set; }
            public string NodeName { get; set; } = "";
            public string NameKey { get; set; } = "";
            public DateTime? Dagfesting { get; set; }
            public DateTime CreateDate { get; set; }
        }

        // -------- Helpers --------
        private IActionResult JsonMaybeRaw(object payload, bool raw) =>
            raw ? Content(JsonSerializer.Serialize(payload), "application/json")
                : Ok(payload);

        // -------- Health --------
        [HttpGet("ping")]
        public IActionResult Ping([FromQuery] bool raw = false) =>
            JsonMaybeRaw(new { ok = true, controller = nameof(DedupeController) }, raw);

        // -------- Preview --------
        // GET /umbraco/backoffice/api/dedupe/preview?typeAlias=tidindaElement&dateAlias=dagfesting&keep=newest&includeTrashed=false&rootId=1234&publishedOnly=true&raw=true
        [HttpGet("preview")]
        public IActionResult Preview(
            string typeAlias = "tidindaElement",
            string dateAlias = "dagfesting",
            string keep = "newest",          // or "oldest"
            bool includeTrashed = false,     // <— NEW: exclude trashed by default
            int? rootId = null,
            bool publishedOnly = false,
            [FromQuery] bool raw = false)
        {
            var groups = FindDuplicateGroups(typeAlias, dateAlias, keep, includeTrashed, rootId, publishedOnly);
            var response = new
            {
                ContentType = typeAlias,
                DateAlias = dateAlias,
                Keep = keep,
                IncludeTrashed = includeTrashed,
                RootId = rootId,
                PublishedOnly = publishedOnly,
                DuplicateGroups = groups.Count,
                ItemsToDelete = groups.Sum(g => g.DeleteIds.Count),
                Groups = groups
            };
            return JsonMaybeRaw(response, raw);
        }

        // -------- Commit (POST) --------
        // POST /umbraco/backoffice/api/dedupe/commit?typeAlias=...&dateAlias=...&keep=newest&includeTrashed=false&recycleBin=true&rootId=1234&publishedOnly=true&batchSize=10&commandTimeoutSec=300&delayMs=50&raw=true
        [HttpPost("commit")]
        public IActionResult Commit(
            string typeAlias = "tidindaElement",
            string dateAlias = "dagfesting",
            string keep = "newest",
            bool includeTrashed = false,     // <— NEW
            bool recycleBin = true,
            int? rootId = null,
            bool publishedOnly = false,
            int batchSize = 10,
            int commandTimeoutSec = 180,
            int delayMs = 0,
            [FromQuery] bool raw = false)
        {
            if (batchSize <= 0) batchSize = 10;
            if (commandTimeoutSec < 30) commandTimeoutSec = 30;
            if (delayMs < 0) delayMs = 0;

            // 1) Find candidates (outside write scope)
            var groups = FindDuplicateGroups(typeAlias, dateAlias, keep, includeTrashed, rootId, publishedOnly);
            var allDeleteIds = groups.SelectMany(g => g.DeleteIds).ToList();
            var toDelete = allDeleteIds.Take(batchSize).ToList();

            int deleted = 0, binned = 0, missing = 0, errors = 0;
            var failures = new List<object>();

            // 2) Single write scope for the whole batch
            using (var scope = _scopeProvider.CreateScope())
            {
                scope.Database.CommandTimeout = commandTimeoutSec;

                foreach (var id in toDelete)
                {
                    try
                    {
                        var content = _contentService.GetById(id);
                        if (content == null) { missing++; continue; }

                        if (recycleBin)
                        {
                            var result = _contentService.MoveToRecycleBin(content);
                            if (result.Success) binned++;
                            else { errors++; failures.Add(new { id, error = "MoveToRecycleBin failed" }); }
                        }
                        else
                        {
                            _contentService.Delete(content);
                            deleted++;
                        }

                        if (delayMs > 0) Thread.Sleep(delayMs);
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        failures.Add(new { id, error = ex.Message });
                    }
                }

                scope.Complete();
            }

            var response = new
            {
                ContentType = typeAlias,
                DateAlias = dateAlias,
                Keep = keep,
                IncludeTrashed = includeTrashed,
                RootId = rootId,
                PublishedOnly = publishedOnly,
                DuplicateGroupsThisScan = groups.Count,
                ItemsFoundThisScan = allDeleteIds.Count,
                AttemptedThisCall = toDelete.Count,
                MovedToRecycleBin = binned,
                PermanentlyDeleted = deleted,
                NotFound = missing,
                Errors = errors,
                Failures = failures,
                RemainingEstimate = Math.Max(0, allDeleteIds.Count - toDelete.Count)
            };

            return JsonMaybeRaw(response, raw);
        }

        // -------- GET alias (for easy testing) --------
        [HttpGet("commit")]
        public IActionResult CommitGet(
            string typeAlias = "tidindaElement",
            string dateAlias = "dagfesting",
            string keep = "newest",
            bool includeTrashed = false,
            bool recycleBin = true,
            int? rootId = null,
            bool publishedOnly = false,
            int batchSize = 10,
            int commandTimeoutSec = 180,
            int delayMs = 0,
            [FromQuery] bool raw = false)
            => Commit(typeAlias, dateAlias, keep, includeTrashed, recycleBin, rootId, publishedOnly, batchSize, commandTimeoutSec, delayMs, raw);

        // -------- Core finder (now excludes trashed by default) --------
        private List<DuplicateGroup> FindDuplicateGroups(
            string typeAlias, string dateAlias, string keep, bool includeTrashed, int? rootId, bool publishedOnly)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            // Resolve content type & property type
            var contentTypeId = db.FirstOrDefault<int?>(
                "SELECT TOP 1 ct.nodeId FROM cmsContentType ct WHERE ct.alias=@0", typeAlias);
            if (contentTypeId == null) return new();

            var propTypeId = db.FirstOrDefault<int?>(
                "SELECT TOP 1 pt.id FROM cmsPropertyType pt WHERE pt.contentTypeId=@0 AND pt.Alias=@1",
                contentTypeId.Value, dateAlias);
            if (propTypeId == null) return new();

            var publishedJoin = publishedOnly
                ? "JOIN umbracoDocument d ON d.nodeId = n.id AND d.published = 1"
                : "";

            var trashedFilter = includeTrashed ? "" : "AND n.trashed = 0";

            var rows = db.Fetch<Row>($@"
SELECT  NodeId, NodeName, NameKey, Dagfesting, CreateDate
FROM (
    SELECT
        n.id                                   AS NodeId,
        n.[text]                               AS NodeName,
        LOWER(n.[text])                        AS NameKey,
        COALESCE(
            pd.dateValue,
            TRY_CONVERT(datetime, pd.varcharValue),
            TRY_CONVERT(datetime, pd.textValue)
        )                                      AS Dagfesting,
        n.createDate                           AS CreateDate
    FROM umbracoNode n
    JOIN umbracoContent c           ON c.nodeId = n.id
    JOIN cmsContentType ct          ON ct.nodeId = c.contentTypeId AND ct.alias = @0
    JOIN umbracoContentVersion v    ON v.nodeId = n.id AND v.[current] = 1
    JOIN cmsPropertyType pt         ON pt.contentTypeId = ct.nodeId AND pt.Alias = @1
    JOIN umbracoPropertyData pd     ON pd.versionId = v.id AND pd.propertyTypeId = pt.id
    {publishedJoin}
    WHERE 1=1
      {trashedFilter}
      AND (@2 IS NULL OR n.path LIKE '%,' + CONVERT(VARCHAR(20), @2) + ',%')
) AS Base
", typeAlias, dateAlias, rootId);

            var groups = rows
                .GroupBy(r => new { r.NameKey, r.Dagfesting })
                .Where(g => g.Count() > 1)
                .Select(g =>
                {
                    var ordered = (keep?.Equals("oldest", StringComparison.OrdinalIgnoreCase) ?? false)
                        ? g.OrderBy(x => x.CreateDate).ThenBy(x => x.NodeId)
                        : g.OrderByDescending(x => x.CreateDate).ThenByDescending(x => x.NodeId);

                    var keepRow = ordered.First();
                    var allIds = g.Select(x => x.NodeId).ToList();
                    var deleteIds = allIds.Where(id => id != keepRow.NodeId).ToList();

                    return new DuplicateGroup
                    {
                        Name = g.First().NodeName,
                        Dagfesting = g.Key.Dagfesting,
                        KeepId = keepRow.NodeId,
                        DeleteIds = deleteIds,
                        AllIds = allIds
                    };
                })
                .OrderByDescending(x => x.DeleteIds.Count)
                .ToList();

            return groups;
        }

        // OPTIONAL: empty the whole recycle bin (be careful!)
        // POST /umbraco/backoffice/api/dedupe/emptybin?raw=true
        // Uncomment if you want a one-click purge after verifying items:
        /*
        [HttpPost("emptybin")]
        public IActionResult EmptyBin([FromQuery] bool raw = false)
        {
            using var scope = _scopeProvider.CreateScope();
            _contentService.EmptyRecycleBin();
            scope.Complete();
            return JsonMaybeRaw(new { ok = true, message = "Recycle bin emptied." }, raw);
        }
        */
    }
}
