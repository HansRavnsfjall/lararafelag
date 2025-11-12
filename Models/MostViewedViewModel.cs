using System.Collections.Generic;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace Lararafelagid.Models;

public sealed class MostViewedItem
{
    public IPublishedContent? Content { get; init; }
    public string UrlPath { get; init; } = string.Empty; // fallback if not mapped
    public int Visitors { get; init; }
}

public sealed class MostViewedViewModel
{
    public string Title { get; init; } = "Mest lisið";
    public IReadOnlyList<MostViewedItem> Items { get; init; } = new List<MostViewedItem>();
}
