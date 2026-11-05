using System.Net;
using Xunit;
using static SnomedSearch.Tests.TestHelpers;

namespace SnomedSearch.Tests;

/// <summary>
/// Lexical search behaviour against the synthetic fixture (invented ids and terms).
/// Concepts used: 9200001 "Nibble pain" (synonym "Nibble ache"), 9200002 "Throbbing nibble pain",
/// 9200003 "Nibble pain of left knee", 9200004 "Frabjous ache", 9200005 inactive "Retired wobble".
/// </summary>
[Collection("database")]
public class SearchTests(DatabaseFixture db)
{
    private readonly HttpClient _http = db.Client;

    [Fact]
    public async Task Word_order_does_not_matter()
    {
        var a = await _http.GetApiAsync("/api/concepts/search?q=" + Q("nibble pain"));
        var b = await _http.GetApiAsync("/api/concepts/search?q=" + Q("pain nibble"));

        Assert.Equal(HttpStatusCode.OK, a.Status);
        Assert.Equal(new long[] { 9200001, 9200002, 9200003 }, a.ItemIds().OrderBy(x => x));
        Assert.Equal(a.ItemIds().OrderBy(x => x), b.ItemIds().OrderBy(x => x));
    }

    [Fact]
    public async Task Exact_term_ranks_before_prefix_before_contains()
    {
        // 9200001 "Nibble pain" is exact; 9200002 has the prefix term "Nibble pain, throbbing";
        // 9200003 "Nibble pain of left knee" is a longer prefix match.
        var r = await _http.GetApiAsync("/api/concepts/search?q=" + Q("nibble pain"));
        Assert.Equal(new long[] { 9200001, 9200002, 9200003 }, r.ItemIds());
    }

    [Fact]
    public async Task Each_word_may_be_a_prefix()
    {
        var r = await _http.GetApiAsync("/api/concepts/search?q=" + Q("nibb pa"));
        Assert.Equal(new long[] { 9200001, 9200002, 9200003 }, r.ItemIds().OrderBy(x => x));
        Assert.False(r.Fuzzy);
    }

    [Fact]
    public async Task Typo_falls_back_to_trigram_similarity()
    {
        var r = await _http.GetApiAsync("/api/concepts/search?q=" + Q("frabjus ache"));
        Assert.True(r.Fuzzy);
        Assert.Contains(9200004L, r.ItemIds());
    }

    [Fact]
    public async Task Inactive_concepts_and_descriptions_are_never_returned()
    {
        // 9200005 is an inactive concept; "Frabjous obsolete term" is an inactive description of an active one.
        var inactiveConcept = await _http.GetApiAsync("/api/concepts/search?q=" + Q("retired wobble"));
        Assert.DoesNotContain(9200005L, inactiveConcept.ItemIds());

        var inactiveTerm = await _http.GetApiAsync("/api/concepts/search?q=" + Q("frabjous obsolete"));
        Assert.All(inactiveTerm.Body.GetProperty("items").EnumerateArray(),
            i => Assert.NotEqual("Frabjous obsolete term", i.GetProperty("matchedTerm").GetString()));
    }

    [Fact]
    public async Task Semantic_tag_filter_restricts_results()
    {
        var disorders = await _http.GetApiAsync("/api/concepts/search?q=quillon&tags=disorder");
        Assert.Equal(new long[] { 9100001, 9100002, 9100003 }, disorders.ItemIds().OrderBy(x => x));

        var findings = await _http.GetApiAsync("/api/concepts/search?q=quillon&tags=finding");
        Assert.Empty(findings.ItemIds());
    }

    [Fact]
    public async Task Concepts_outside_the_configured_tags_are_not_searchable()
    {
        // 9000001 has the tag "special concept", which is not in search_config.
        var r = await _http.GetApiAsync("/api/concepts/search?q=" + Q("synthetic root"));
        Assert.DoesNotContain(9000001L, r.ItemIds());
    }

    [Fact]
    public async Task Page_past_the_end_returns_an_empty_page_not_unrelated_fuzzy_results()
    {
        // Regression: the fuzzy fallback used to run whenever the requested page was empty, so asking for a
        // page beyond the exact results returned unrelated typo matches.
        var r = await _http.GetApiAsync("/api/concepts/search?q=nibble&limit=2&page=5");

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Empty(r.ItemIds());
        Assert.Equal(3, r.TotalCount);
        Assert.False(r.Fuzzy);
    }

    [Fact]
    public async Task Cursor_paging_visits_every_concept_once()
    {
        var seen = new List<long>();
        string? cursor = null;
        int pages = 0;
        do
        {
            var url = "/api/concepts/search?q=nibble&limit=2" + (cursor is null ? "" : "&cursor=" + Q(cursor));
            var r = await _http.GetApiAsync(url);
            Assert.Equal(HttpStatusCode.OK, r.Status);
            if (pages == 0) Assert.Equal(3, r.TotalCount); else Assert.Null(r.TotalCount);
            seen.AddRange(r.ItemIds());
            cursor = r.NextCursor;
            pages++;
        } while (cursor is not null && pages < 10);

        Assert.Equal(2, pages);
        Assert.Equal(new long[] { 9200001, 9200002, 9200003 }, seen);
    }

    [Fact]
    public async Task A_fuzzy_cursor_never_switches_to_word_results()
    {
        var first = await _http.GetApiAsync("/api/concepts/search?q=" + Q("frabjus ache") + "&limit=1");
        Assert.True(first.Fuzzy);
        if (first.NextCursor is null) return; // only one fuzzy match in the tiny fixture

        var next = await _http.GetApiAsync("/api/concepts/search?q=" + Q("frabjus ache") + "&limit=1&cursor=" + Q(first.NextCursor));
        Assert.True(next.Fuzzy);
    }

    [Fact]
    public async Task Typed_wildcards_are_literal_not_patterns()
    {
        var percentOnly = await _http.GetApiAsync("/api/concepts/search?q=" + Q("%"));
        Assert.Empty(percentOnly.ItemIds());
        Assert.Equal(0, percentOnly.TotalCount);

        var underscoreOnly = await _http.GetApiAsync("/api/concepts/search?q=" + Q("_"));
        Assert.Empty(underscoreOnly.ItemIds());

        // The digits in "100%" still match, the percent sign is just a separator.
        var withDigits = await _http.GetApiAsync("/api/concepts/search?q=" + Q("100%"));
        Assert.Contains(9500001L, withDigits.ItemIds());
    }

    [Fact]
    public async Task Suggest_returns_prefix_matches_without_counts_or_fuzzy()
    {
        var r = await _http.GetApiAsync("/api/concepts/suggest?q=" + Q("nib") + "&limit=2");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(2, r.Body.GetArrayLength());

        var typo = await _http.GetApiAsync("/api/concepts/suggest?q=" + Q("frabjus"));
        Assert.Equal(0, typo.Body.GetArrayLength()); // suggest does not do typo matching
    }
}
