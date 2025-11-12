namespace Lararafelagid.Services.MostViewed
{
    public sealed class ViewHit
    {
        public int NodeId { get; set; }
        public int Count { get; set; }
    }

    // Serialized shape on disk (rolling daily buckets)
    public sealed class RollingStore
    {
        public Dictionary<string, Dictionary<int, int>> Days { get; set; } = new(); 
        // Key: yyyy-MM-dd -> (nodeId -> count)
    }

    public interface IViewCountService
    {
        void Track(int nodeId, string? userAgent, string? ip, bool isBackOffice, bool isPreview);
    }

    public interface IMostViewedQueryService
    {
        Task<IReadOnlyList<(int nodeId, int views)>> GetTopAsync(int days, int skipTop, int take, string[] allowedAliases);
    }
}
