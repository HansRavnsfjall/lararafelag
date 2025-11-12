// Adjust the namespace to your project
namespace YourProject.Controllers;

using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;                 // <-- already present
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.Media;          // <-- already present
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Web.BackOffice.Controllers;
using Umbraco.Cms.Web.Common.Attributes;

[PluginController("Tools")]
public class TidindaImportController : UmbracoAuthorizedApiController
{
    // --- CSV Row shape ---
    internal class TidindaRow
    {
        public string? Yvirskrift { get; set; }
        public string? Dagfesting { get; set; }
        public string? Inngangstekstur { get; set; }
        public string? Tekstur { get; set; }
        public string? WrittenBy { get; set; }
        public string? ImageUrl { get; set; }
    }

    // --- Explicit header mapping (after sanitize/trim of quotes) ---
    internal sealed class TidindaRowMap : ClassMap<TidindaRow>
    {
        public TidindaRowMap()
        {
            Map(m => m.Yvirskrift).Name("Yvirskrift");
            Map(m => m.Dagfesting).Name("Dagfesting");
            Map(m => m.Inngangstekstur).Name("Inngangstekstur");
            Map(m => m.Tekstur).Name("Tekstur");
            Map(m => m.WrittenBy).Name("WrittenBy");
            Map(m => m.ImageUrl).Name("ImageUrl");
        }
    }

    private readonly IContentService _contentService;
    private readonly IMediaService _mediaService;
    private readonly IHttpClientFactory _httpClientFactory;

    // Required by Umbraco 13 upload API
    private readonly MediaFileManager _mediaFileManager;
    private readonly MediaUrlGeneratorCollection _mediaUrlGenerators;
    private readonly IShortStringHelper _shortStringHelper;
    private readonly IContentTypeBaseServiceProvider _contentTypeBaseServiceProvider;

    // Serialize media writes to avoid "Recursive locks not allowed"
    private static readonly SemaphoreSlim MediaWriteGate = new(1, 1);
    // NEW: Serialize content saves (and allow a short retry on lock)
    private static readonly SemaphoreSlim ContentWriteGate = new(1, 1);

    private static readonly string[] DateFormats = new[]
    {
        "yyyy-MM-dd HH:mm:ss", // exporter now uses this
        "dd-MM-yyyy HH:mm:ss.fff",
        "dd-MM-yyyy HH:mm:ss",
        "dd-MM-yyyy",
        "dd.MM.yyyy HH:mm",
        "dd.MM.yyyy",
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.fff",
        "dd/MM/yyyy HH:mm",
        "dd/MM/yyyy"
    };

    public TidindaImportController(
        IContentService contentService,
        IMediaService mediaService,
        IHttpClientFactory httpClientFactory,
        MediaFileManager mediaFileManager,
        MediaUrlGeneratorCollection mediaUrlGenerators,
        IShortStringHelper shortStringHelper,
        IContentTypeBaseServiceProvider contentTypeBaseServiceProvider)
    {
        _contentService = contentService;
        _mediaService = mediaService;
        _httpClientFactory = httpClientFactory;

        _mediaFileManager = mediaFileManager;
        _mediaUrlGenerators = mediaUrlGenerators;
        _shortStringHelper = shortStringHelper;
        _contentTypeBaseServiceProvider = contentTypeBaseServiceProvider;
    }

    /// <summary>
    /// Import/repair CSV into 'tidindaelement' under a parent.
    /// CSV path: /umbraco/Data/import/tidindi.csv
    ///
    /// Query params:
    ///  - parentId=2047                (required)
    ///  - dryRun=true|false            (dry-run does not download or save)
    ///  - baseImageUrl=https://...     (used when ImageUrl is /Files/... or file:///Files/...)
    ///  - skip=0&limit=500             (batching)
    ///  - updateExisting=true          (match by 'yvirskrift' and update instead of create)
    ///  - fixImagesOnly=true           (when updating, only set 'mynd' from ImageUrl)
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Run(
        [FromQuery] int parentId,
        [FromQuery] bool dryRun = false,
        [FromQuery] int? limit = null,
        [FromQuery] int? skip = null,
        [FromQuery] string? baseImageUrl = null,
        [FromQuery] bool updateExisting = false,
        [FromQuery] bool fixImagesOnly = false)
    {
        // --- Locate & sanitize CSV ---
        var importDir = Path.Combine(Directory.GetCurrentDirectory(), "umbraco", "Data", "import");
        Directory.CreateDirectory(importDir);
        var path = Path.Combine(importDir, "tidindi.csv");
        if (!System.IO.File.Exists(path))
            return NotFound($"CSV file not found at {path}");

        var raw = await System.IO.File.ReadAllTextAsync(path, Encoding.UTF8);
        var cleaned = SanitizeExport(raw); // DO NOT strip quotes

        // --- Per-run media cache (no fake GUIDs) ---
        var mediaCache = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        // --- HTTP client (user-agent helps avoid hotlink blockers) ---
        var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; UmbracoImporter/1.0)");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        if (!string.IsNullOrWhiteSpace(baseImageUrl) && Uri.TryCreate(baseImageUrl, UriKind.Absolute, out var bu))
            http.DefaultRequestHeaders.Referrer = bu;

        var mediaFolderId = EnsureMediaFolder("Imported Tidindi Images");

        // --- Parse CSV (semicolon + explicit mapping). Try header-based, fallback to index-based ---
        List<TidindaRow> rows;
        try
        {
            rows = ParseWithHeaders(cleaned);
        }
        catch
        {
            rows = ParseByIndex(cleaned); // uses fixed column order 0..5 as exported
            if (rows.Count == 0)
                return BadRequest(new { Error = "Failed to parse CSV by header and by index." });
        }

        if (skip.HasValue && skip.Value > 0) rows = rows.Skip(skip.Value).ToList();
        if (limit.HasValue) rows = rows.Take(limit.Value).ToList();

        // --- If updating, index existing children by yvirskrift/name ---
        Dictionary<string, IContent>? existingByHeading = null;
        if (updateExisting || fixImagesOnly)
        {
            existingByHeading = new Dictionary<string, IContent>(StringComparer.OrdinalIgnoreCase);
            long total; var page = 0; const int pageSize = 2000;
            do
            {
                var kids = _contentService.GetPagedChildren(parentId, page, pageSize, out total, null);
                foreach (var c in kids)
                {
                    var key = (c.GetValue<string>("yvirskrift") ?? c.Name ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(key) && !existingByHeading.ContainsKey(key))
                        existingByHeading[key] = c;
                }
                page++;
            } while (page * pageSize < total);
        }

        // --- Diagnostics ---
        int processed = 0, created = 0, updated = 0, failures = 0;
        int imagesAttempted = 0, imageCreated = 0, imageFailed = 0, imagesSetOnNodes = 0;
        int datesParsed = 0, datesFailed = 0;
        var imageFailSamples = new List<string>(5);
        var dateFailSamples = new List<string>(5);

        // --- Main loop ---
        foreach (var row in rows)
        {
            processed++;
            try
            {
                if (string.IsNullOrWhiteSpace(row.Yvirskrift)) continue;
                var heading = row.Yvirskrift.Trim();

                // Create or update
                IContent? node = null; var isUpdate = false;
                if (existingByHeading != null && existingByHeading.TryGetValue(heading, out var exist))
                {
                    node = exist; isUpdate = true;
                }
                if (node == null)
                    node = _contentService.Create(heading, parentId, "tidindaelement");

                // Content fields (unless we are only fixing images)
                if (!fixImagesOnly)
                {
                    node.SetValue("yvirskrift", heading);
                    node.SetValue("inngangstekstur", row.Inngangstekstur ?? string.Empty);
                    node.SetValue("tekstur", row.Tekstur ?? string.Empty);
                    node.SetValue("writtenBy", row.WrittenBy ?? string.Empty);

                    var dateStr = row.Dagfesting;
                    if (!string.IsNullOrWhiteSpace(dateStr))
                    {
                        if (TryParseDagfesting(dateStr, out var dt))
                        {
                            node.SetValue("dagfesting", dt);
                            datesParsed++;
                        }
                        else
                        {
                            datesFailed++;
                            if (dateFailSamples.Count < 5) dateFailSamples.Add(dateStr);
                        }
                    }
                }

                // Image
                bool imageWasSetThisRow = false;   // NEW: count after a successful save
                var imgRaw = row.ImageUrl?.Trim();
                if (!string.IsNullOrWhiteSpace(imgRaw))
                {
                    imagesAttempted++;

                    // Treat file:///Files/... like /Files/... and combine with baseImageUrl
                    var absUrl = BuildAbsoluteUrl(imgRaw!, baseImageUrl);
                    var normalizedUrl = NormalizeUrl(absUrl);

                    var mediaKey = await GetOrCreateMediaAsync(
                        http, normalizedUrl, mediaFolderId, heading, dryRun, mediaCache,
                        onCreated: () => imageCreated++,
                        onFail: (msg) =>
                        {
                            imageFailed++;
                            if (imageFailSamples.Count < 5) imageFailSamples.Add($"{normalizedUrl} :: {msg}");
                        });

                    if (mediaKey != null && !dryRun)
                    {
                        var udi = Udi.Create(Constants.UdiEntityType.Media, mediaKey.Value);
                        node.SetValue("mynd", udi);
                        imageWasSetThisRow = true;   // defer counting until after save
                    }
                }

                if (!dryRun)
                {
                    // NEW: use lock-aware, retrying save
                    SaveAndPublishSafe(node);

                    if (imageWasSetThisRow)
                        imagesSetOnNodes++;

                    if (isUpdate) updated++; else created++;
                }

                if (processed % 200 == 0)
                    await Task.Delay(200);
            }
            catch (Exception ex)
            {
                failures++;
                if (imageFailSamples.Count < 5) imageFailSamples.Add(ex.Message);
            }
        }

        return Ok(new
        {
            processed, created, updated, failures,
            imagesAttempted, imageCreated, imageFailed, imagesSetOnNodes,
            datesParsed, datesFailed,
            imageFailSamples, dateFailSamples,
            dryRun, updateExisting, fixImagesOnly
        });
    }

    // ---------- lock-aware content save/publish ----------
    private void SaveAndPublishSafe(IContent node)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            bool locked = false;
            try
            {
                ContentWriteGate.Wait();
                locked = true;

                _contentService.SaveAndPublish(node);
                return; // success
            }
            catch (Exception ex) when (ex.Message.Contains("Recursive locks not allowed", StringComparison.OrdinalIgnoreCase))
            {
                if (attempt == maxAttempts) throw;
                Thread.Sleep(150 * attempt); // small backoff 150/300 ms
            }
            finally
            {
                if (locked) ContentWriteGate.Release();
            }
        }
    }

    // ----------------- CSV Parsing -----------------

    private static List<TidindaRow> ParseWithHeaders(string cleanedCsv)
    {
        using var reader = new StringReader(cleanedCsv);
        var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = ";",
            HasHeaderRecord = true,
            TrimOptions = TrimOptions.Trim,
            BadDataFound = null,
            MissingFieldFound = null,
            IgnoreBlankLines = true,
            DetectColumnCountChanges = false,
            PrepareHeaderForMatch = a => a.Header.Trim().Trim('"')
        };

        using var csv = new CsvReader(reader, cfg);
        csv.Context.RegisterClassMap<TidindaRowMap>();
        return csv.GetRecords<TidindaRow>().ToList();
    }

    // Fallback when headers get mangled: map by index (expected export order)
    private static List<TidindaRow> ParseByIndex(string cleanedCsv)
    {
        using var reader = new StringReader(cleanedCsv);
        var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = ";",
            HasHeaderRecord = false,
            TrimOptions = TrimOptions.Trim,
            BadDataFound = null,
            MissingFieldFound = null,
            IgnoreBlankLines = true,
            DetectColumnCountChanges = false
        };

        using var csv = new CsvReader(reader, cfg);
        var rows = new List<TidindaRow>();
        while (csv.Read())
        {
            // 0 Yvirskrift; 1 Inngangstekstur; 2 WrittenBy; 3 Dagfesting; 4 ImageUrl; 5 Tekstur
            string? F(int i) { try { return csv.GetField(i); } catch { return null; } }

            // Skip a potential header-like first row (starts with "Yvirskrift")
            var c0 = F(0);
            if (rows.Count == 0 && string.Equals(c0?.Trim('"'), "Yvirskrift", StringComparison.OrdinalIgnoreCase))
                continue;

            var row = new TidindaRow
            {
                Yvirskrift = c0,
                Inngangstekstur = F(1),
                WrittenBy = F(2),
                Dagfesting = F(3),
                ImageUrl = F(4),
                Tekstur = F(5)
            };
            if (!string.IsNullOrWhiteSpace(row.Yvirskrift))
                rows.Add(row);
        }
        return rows;
    }

    // ----------------- Helpers -----------------

    // Keep quoted CSV intact: only normalize newlines and remove tabs; DO NOT strip quotes or unescape.
    private static string SanitizeExport(string raw)
    {
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder(raw.Length + 1024);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var s = line.TrimEnd('\r').Replace("\t", "");
            sb.AppendLine(s);
        }
        return sb.ToString();
    }

    private int EnsureMediaFolder(string folderName)
    {
        var existing = _mediaService.GetRootMedia()
            .FirstOrDefault(x => x.ContentType.Alias == Constants.Conventions.MediaTypes.Folder && x.Name == folderName);

        if (existing != null) return existing.Id;

        var folder = _mediaService.CreateMedia(folderName, -1, Constants.Conventions.MediaTypes.Folder);
        _mediaService.Save(folder);
        return folder.Id;
    }

    // Turn relative and file:// URLs into usable http(s) URLs via baseImageUrl
    private static string BuildAbsoluteUrl(string img, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(img)) return img;

        if (Uri.TryCreate(img, UriKind.Absolute, out var abs))
        {
            if (abs.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                // file:///Files/... -> {base}/Files/...
                var path = abs.AbsolutePath; // keeps leading '/'
                if (!string.IsNullOrWhiteSpace(baseUrl))
                    return baseUrl!.TrimEnd('/') + path;
                return path; // fallback (may still be combined later)
            }

            // Already absolute http/https → return as-is
            return img;
        }

        // Not absolute -> combine with base
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            var p = img.StartsWith("/") ? img : "/" + img;
            return baseUrl!.TrimEnd('/') + p;
        }

        return img;
    }

    // Percent-encode each path segment; keep query/fragment
    private static string NormalizeUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var rawPath = Uri.UnescapeDataString(uri.AbsolutePath);
            var segs = rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                              .Select(s => Uri.EscapeDataString(s));
            var path = "/" + string.Join("/", segs);

            var ub = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port, path)
            {
                Query = uri.Query.TrimStart('?')
            };
            return ub.Uri.ToString();
        }
        catch
        {
            // Minimal fallback
            return url.Replace(" ", "%20");
        }
    }

    private static bool TryParseDagfesting(string dateStr, out DateTime dt)
    {
        // Normalize whitespace/newlines
        var s = dateStr.ReplaceLineEndings(" ").Trim();
        s = Regex.Replace(s, @"\s+", " ");
        s = NormalizeMs(s);

        // 1) Try explicit formats and cultural fallbacks
        if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt) ||
            DateTime.TryParse(s, CultureInfo.GetCultureInfo("da-DK"), DateTimeStyles.AssumeLocal, out dt) ||
            DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt))
        {
            return true;
        }

        // 2) Excel serial fallback (optionally with HHmm time after a space)
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 1 && long.TryParse(parts[0], out var serial))
        {
            // guard range (roughly 1980..2189) to avoid misreading small garbage numbers
            if (serial >= 29000 && serial <= 80000)
            {
                dt = new DateTime(1899, 12, 30).AddDays(serial);

                if (parts.Length >= 2 && Regex.IsMatch(parts[1], @"^\d{3,4}$"))
                {
                    var t = parts[1].PadLeft(4, '0');
                    var hh = int.Parse(t[..2]);
                    var mm = int.Parse(t[2..]);
                    if (hh >= 0 && hh < 24 && mm >= 0 && mm < 60)
                        dt = dt.AddHours(hh).AddMinutes(mm);
                }
                return true;
            }
        }

        dt = default;
        return false;

        static string NormalizeMs(string s0)
        {
            // convert 07:22:00:000 -> 07:22:00.000 if present
            var idx = s0.LastIndexOf(':');
            if (idx > 0 && s0.Length - idx - 1 == 3 && int.TryParse(s0[(idx + 1)..], out _))
                return s0[..idx] + "." + s0[(idx + 1)..];
            return s0;
        }
    }

    private async Task<Guid?> GetOrCreateMediaAsync(
        HttpClient http,
        string url,
        int parentId,
        string fallbackName,
        bool dryRun,
        Dictionary<string, Guid> cache,
        Action? onCreated = null,
        Action<string>? onFail = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) { onFail?.Invoke("Invalid URL"); return null; }

        if (cache.TryGetValue(url, out var existingKey)) return existingKey;
        if (dryRun) return null; // never cache fake keys in dry-run

        try
        {
            // Download to memory first
            using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
            {
                onFail?.Invoke($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return null;
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (bytes == null || bytes.Length == 0)
            {
                onFail?.Invoke("Empty image response");
                return null;
            }

            var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.LocalPath));
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "image.jpg";

            // Serialize media writes to avoid re-entrant file locks
            await MediaWriteGate.WaitAsync();
            try
            {
                using var ms = new MemoryStream(bytes, writable: false);
                ms.Position = 0;

                var media = _mediaService.CreateMedia(
                    string.IsNullOrWhiteSpace(fallbackName) ? fileName : fallbackName,
                    parentId,
                    Constants.Conventions.MediaTypes.Image);

                // Umbraco 13 upload overload (requires the 4 services)
                media.SetValue(
                    _mediaFileManager,
                    _mediaUrlGenerators,
                    _shortStringHelper,
                    _contentTypeBaseServiceProvider,
                    Constants.Conventions.Media.File,
                    fileName,
                    ms);

                _mediaService.Save(media);

                cache[url] = media.Key;
                onCreated?.Invoke();
                return media.Key;
            }
            finally
            {
                MediaWriteGate.Release();
            }
        }
        catch (Exception ex)
        {
            onFail?.Invoke(ex.Message);
            return null;
        }
    }

    // Optional quick auth test (open in same logged-in tab)
    [HttpGet]
    public IActionResult Ping() => Ok(new { ok = true, now = DateTime.UtcNow });
}
