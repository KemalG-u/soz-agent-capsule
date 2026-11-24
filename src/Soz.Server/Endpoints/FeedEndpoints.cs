using Soz.Core;
using Soz.Core.Feed;

namespace Soz.Server.Endpoints;

/// <summary>
/// "Ne oldu": olay defterinin okuma yüzü (yalnız okur, yazma ucu yok).
///   GET /api/events?limit=50&amp;before=&lt;ISO zaman&gt;&amp;kind=job|guard|setting|system
/// Satırlar yeniden eskiye gelir: <c>{ items:[{ id, at, group, groupLabel, kind, sentence, jobId, jobTitle, projectId, projectName, folder }], hasMore, next, limit, group, groups:[{id,label}] }</c>.
/// <c>next</c> doluysa bir sonraki sayfa için <c>before=next</c> verilir. <c>kind</c> süzgeci bir tür grubudur; başka değerde 400.
/// Canlı akış ayrıdır (<c>/api/events/stream</c>).
/// </summary>
public static class FeedEndpoints
{
    public static RouteGroupBuilder MapFeed(this RouteGroupBuilder api, SozService service)
    {
        api.MapGet("/events", async (int? limit, string? before, string? kind) => Results.Json(await Task.Run(() =>
        {
            using var c = service.Db.Open();
            var g = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim().ToLowerInvariant();
            var page = EventFeed.Read(c, limit, before, g);
            return new
            {
                items = page.Items, hasMore = page.HasMore, next = page.Next, limit = page.Limit, group = page.Group,
                groups = EventFeed.Groups.Select(x => new { id = x, label = EventFeed.GroupLabel(x) }).ToArray(),
            };
        })));
        return api;
    }
}
