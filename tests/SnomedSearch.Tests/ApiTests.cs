using System.Net;
using Xunit;
using static SnomedSearch.Tests.TestHelpers;

namespace SnomedSearch.Tests;

[Collection("database")]
public class ApiTests(DatabaseFixture db)
{
    private readonly HttpClient _http = db.Client;

    // ---- hierarchy ----

    [Fact]
    public async Task Children_lists_direct_active_is_a_children()
    {
        var r = await _http.GetApiAsync("/api/concepts/9100001/children");
        Assert.Equal(new long[] { 9100002, 9100003 }, r.ItemIds().OrderBy(x => x));
        Assert.Equal(2, r.Body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Parents_include_every_active_parent_and_ignore_inactive_relationships()
    {
        // 9200002 has two active parents. 9200003 -> 9200004 exists only as an inactive relationship.
        var multi = await _http.GetApiAsync("/api/concepts/9200002/parents");
        Assert.Equal(new long[] { 9200001, 9200004 }, multi.ItemIds().OrderBy(x => x));

        var single = await _http.GetApiAsync("/api/concepts/9200003/parents");
        Assert.Equal(new long[] { 9200001 }, single.ItemIds());
    }

    [Fact]
    public async Task Children_respects_limit()
    {
        var r = await _http.GetApiAsync("/api/concepts/9000001/children?limit=2");
        Assert.Equal(2, r.ItemIds().Length);
    }

    [Fact]
    public async Task Details_combine_names_definition_parents_and_child_count()
    {
        var r = await _http.GetApiAsync("/api/concepts/9100001");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal("Quillon disease (disorder)", r.Body.GetProperty("fsn").GetString());
        Assert.Equal("disorder", r.Body.GetProperty("semanticTag").GetString());
        Assert.Equal("An invented condition used only in tests.", r.Body.GetProperty("definition").GetString());
        Assert.Equal(2, r.Body.GetProperty("childrenCount").GetInt32());
        Assert.Equal(9000001, r.Body.GetProperty("parents")[0].GetProperty("conceptId").GetInt64());
    }

    [Fact]
    public async Task Synonyms_are_active_synonyms_in_alphabetical_order()
    {
        var r = await _http.GetApiAsync("/api/concepts/9200001/synonyms");
        Assert.Equal(new[] { "Nibble ache", "Nibble pain" }, r.Body.EnumerateArray().Select(e => e.GetString()).ToArray());

        // The inactive synonym of 9200004 must not appear.
        var inactive = await _http.GetApiAsync("/api/concepts/9200004/synonyms");
        Assert.Equal(new[] { "Frabjous ache" }, inactive.Body.EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task Preferred_term_is_the_shortest_synonym_and_agrees_with_search()
    {
        var details = await _http.GetApiAsync("/api/concepts/9200001");
        var search = await _http.GetApiAsync("/api/concepts/search?q=" + Q("nibble pain") + "&limit=1");

        var fromDetails = details.Body.GetProperty("preferredTerm").GetString();
        var fromSearch = search.Body.GetProperty("items")[0].GetProperty("preferredTerm").GetString();
        Assert.Equal("Nibble ache", fromDetails); // "Nibble ache" (11 chars) is shorter than "Nibble pain" (11)? tie, so alphabetical
        Assert.Equal(fromDetails, fromSearch);
    }

    [Fact]
    public async Task Semantic_tag_stats_count_only_searchable_tags()
    {
        var r = await _http.GetApiAsync("/api/concepts/semantic-tags");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(5, r.Body.GetProperty("finding").GetInt32());
        Assert.Equal(5, r.Body.GetProperty("disorder").GetInt32());
        Assert.False(r.Body.TryGetProperty("special concept", out _));
    }

    // ---- errors are problem+json ----

    [Theory]
    [InlineData("/api/concepts/1")]
    [InlineData("/api/concepts/1/children")]
    [InlineData("/api/concepts/1/parents")]
    [InlineData("/api/concepts/1/synonyms")]
    [InlineData("/api/concepts/9200005")] // inactive
    public async Task Unknown_or_inactive_concepts_return_404_problem_details(string url)
    {
        var r = await _http.GetApiAsync(url);
        Assert.Equal(HttpStatusCode.NotFound, r.Status);
        Assert.Equal("application/problem+json", r.ContentType);
    }

    [Theory]
    [InlineData("/api/concepts/search")]                                   // missing q
    [InlineData("/api/concepts/search?q=%20%20")]                          // blank q
    [InlineData("/api/concepts/search?q=pain&limit=0")]
    [InlineData("/api/concepts/search?q=pain&limit=101")]
    [InlineData("/api/concepts/search?q=pain&cursor=not-a-cursor")]
    [InlineData("/api/concepts/search?q=pain&limit=100&page=1000")]        // offset beyond the cap
    [InlineData("/api/concepts/suggest?q=pain&limit=21")]
    [InlineData("/api/concepts/9100001/children?limit=0")]
    public async Task Bad_input_returns_400_problem_details(string url)
    {
        var r = await _http.GetApiAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.Equal("application/problem+json", r.ContentType);
    }

    [Theory]
    [InlineData("semantic")]
    [InlineData("hybrid")]
    public async Task Modes_that_are_not_built_yet_return_501(string mode)
    {
        var r = await _http.GetApiAsync("/api/concepts/search?q=pain&mode=" + mode);
        Assert.Equal(HttpStatusCode.NotImplemented, r.Status);
        Assert.Equal("application/problem+json", r.ContentType);
    }
}
