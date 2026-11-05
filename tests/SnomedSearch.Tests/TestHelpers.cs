using System.Net;
using System.Text.Json;

namespace SnomedSearch.Tests;

public sealed record ApiResult(HttpStatusCode Status, string? ContentType, JsonElement Body)
{
    public long[] ItemIds() =>
        Body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("conceptId").GetInt64()).ToArray();

    public int? TotalCount =>
        Body.TryGetProperty("totalCount", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : null;

    public string? NextCursor =>
        Body.TryGetProperty("nextCursor", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;

    public bool Fuzzy => Body.TryGetProperty("fuzzy", out var f) && f.GetBoolean();
}

public static class TestHelpers
{
    public static async Task<ApiResult> GetApiAsync(this HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        return new ApiResult(response.StatusCode, response.Content.Headers.ContentType?.MediaType, doc.RootElement.Clone());
    }

    public static string Q(string s) => Uri.EscapeDataString(s);
}
