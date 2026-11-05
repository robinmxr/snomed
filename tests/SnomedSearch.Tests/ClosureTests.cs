using System.Net;
using System.Text.Json;
using Xunit;
using static SnomedSearch.Tests.TestHelpers;

namespace SnomedSearch.Tests;

/// <summary>
/// Transitive closure (concept_ancestor) against the synthetic fixture. Shape used here:
///   9000001 root
///     9200001 Nibble pain          9200004 Frabjous ache
///        9200002 Throbbing nibble pain (child of BOTH 9200001 and 9200004)
///        9200003 Nibble pain of left knee   (9200003 -> 9200004 exists only as an INACTIVE relationship)
///     9100001 Quillon disease -> 9100002, 9100003
/// 13 active concepts, so the root has 12 descendants.
/// </summary>
[Collection("database")]
public class ClosureTests(DatabaseFixture db)
{
    private readonly HttpClient _http = db.Client;

    private static long[] Ids(JsonElement items) =>
        items.EnumerateArray().Select(i => i.GetProperty("conceptId").GetInt64()).ToArray();

    [Fact]
    public async Task Descendants_are_transitive_and_each_appears_once()
    {
        var r = await _http.GetApiAsync("/api/concepts/9000001/descendants?limit=100");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var ids = Ids(r.Body.GetProperty("items"));
        Assert.Equal(12, ids.Length);
        Assert.Equal(ids.Distinct().Count(), ids.Length);          // 9200002 has two paths, listed once
        Assert.Contains(9100002L, ids);                            // grandchild
        Assert.Equal(12, r.Body.GetProperty("totalCount").GetInt32());
        Assert.Equal(ids.OrderBy(x => x), ids);                    // ordered by id for keyset paging
    }

    [Fact]
    public async Task Depth_is_the_shortest_path()
    {
        var r = await _http.GetApiAsync("/api/concepts/9000001/descendants?limit=100");
        var item = r.Body.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("conceptId").GetInt64() == 9200002);
        Assert.Equal(2, item.GetProperty("depth").GetInt32());
    }

    [Fact]
    public async Task Inactive_relationships_do_not_create_descendants()
    {
        var r = await _http.GetApiAsync("/api/concepts/9200004/descendants");
        Assert.Equal(new long[] { 9200002 }, Ids(r.Body.GetProperty("items")));
    }

    [Fact]
    public async Task Descendants_page_with_after_and_cover_everything_once()
    {
        var seen = new List<long>();
        long? after = null;
        var pages = 0;
        do
        {
            var r = await _http.GetApiAsync("/api/concepts/9000001/descendants?limit=5" + (after is null ? "" : "&after=" + after));
            if (pages == 0) Assert.Equal(12, r.Body.GetProperty("totalCount").GetInt32());
            else Assert.Equal(JsonValueKind.Null, r.Body.GetProperty("totalCount").ValueKind);
            seen.AddRange(Ids(r.Body.GetProperty("items")));
            var next = r.Body.GetProperty("nextAfter");
            after = next.ValueKind == JsonValueKind.Null ? null : next.GetInt64();
            pages++;
        } while (after is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(12, seen.Count);
        Assert.Equal(12, seen.Distinct().Count());
    }

    [Fact]
    public async Task Descendants_can_be_filtered_by_semantic_tag()
    {
        var r = await _http.GetApiAsync("/api/concepts/9000001/descendants?tags=disorder");
        Assert.Equal(new long[] { 9100001, 9100002, 9100003, 9100004, 9100005 }, Ids(r.Body.GetProperty("items")));
        Assert.Equal(5, r.Body.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Ancestors_are_transitive_nearest_first()
    {
        var r = await _http.GetApiAsync("/api/concepts/9200002/ancestors");
        var items = r.Body.EnumerateArray().Select(i => (Id: i.GetProperty("conceptId").GetInt64(), Depth: i.GetProperty("depth").GetInt32())).ToArray();
        Assert.Equal(new[] { (9200001L, 1), (9200004L, 1), (9000001L, 2) }, items);
    }

    [Fact]
    public async Task Search_within_a_subtree_includes_the_concept_itself_and_its_descendants_only()
    {
        var withinNibble = await _http.GetApiAsync("/api/concepts/search?q=pain&within=9200001");
        Assert.Equal(new long[] { 9200001, 9200002, 9200003 }, withinNibble.ItemIds().OrderBy(x => x));

        var withinFrabjous = await _http.GetApiAsync("/api/concepts/search?q=pain&within=9200004");
        Assert.Equal(new long[] { 9200002 }, withinFrabjous.ItemIds());
        Assert.Equal(1, withinFrabjous.TotalCount);

        var outside = await _http.GetApiAsync("/api/concepts/search?q=quillon&within=9200001");
        Assert.Empty(outside.ItemIds());
    }

    [Fact]
    public async Task Suggest_supports_the_subtree_filter()
    {
        var r = await _http.GetApiAsync("/api/concepts/suggest?q=" + Q("nib") + "&within=9200004");
        Assert.Equal(new long[] { 9200002 }, r.Body.EnumerateArray().Select(i => i.GetProperty("conceptId").GetInt64()));
    }

    [Theory]
    [InlineData("/api/concepts/1/descendants")]
    [InlineData("/api/concepts/1/ancestors")]
    public async Task Unknown_concepts_return_404(string url)
    {
        var r = await _http.GetApiAsync(url);
        Assert.Equal(HttpStatusCode.NotFound, r.Status);
    }

    [Theory]
    [InlineData("/api/concepts/9000001/descendants?limit=0")]
    [InlineData("/api/concepts/9000001/descendants?limit=1001")]
    [InlineData("/api/concepts/search?q=pain&within=0")]
    public async Task Bad_hierarchy_input_returns_400(string url)
    {
        var r = await _http.GetApiAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
    }
}
