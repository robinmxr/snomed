using System.Net;
using System.Text;
using SnomedSearch.Core.Entities;
using SnomedSearch.Infrastructure.Embeddings;
using Xunit;

namespace SnomedSearch.Tests;

/// <summary>Pure unit tests, no database.</summary>
public class EmbeddingUnitTests
{
    private static List<ConceptSummary> Items(params long[] ids) => ids.Select(i => new ConceptSummary { ConceptId = i }).ToList();

    [Fact]
    public void Rrf_puts_items_found_by_both_lists_above_items_found_by_one()
    {
        var fused = HybridFusion.Fuse(Items(1, 2, 3), Items(4, 2, 5), k: 60, limit: 10);
        Assert.Equal(2, fused[0].ConceptId);
        Assert.Equal(5, fused.Count);
    }

    [Fact]
    public void Rrf_respects_the_limit_and_breaks_ties_deterministically()
    {
        // 1 and 4 both have a single rank-1 contribution: equal score, equal best rank, so the lower id wins.
        var fused = HybridFusion.Fuse(Items(1), Items(4), k: 60, limit: 1);
        Assert.Equal(new long[] { 1 }, fused.Select(f => f.ConceptId));
    }

    [Fact]
    public void Rrf_constant_controls_how_much_rank_position_matters()
    {
        // Item 1 is rank 1 in the lexical list only. Item 2 is rank 4 in both lists.
        //   k=60: item 2 scores 2/64 = 0.031, item 1 scores 1/61 = 0.016, so agreement wins.
        //   k=1:  item 2 scores 2/5 = 0.40,  item 1 scores 1/2 = 0.50, so the top rank wins.
        //   (Item 99, rank 1 in the semantic list, also scores 0.50 and loses the tie on id.)
        var lex = Items(1, 10, 11, 2);
        var sem = Items(99, 20, 21, 2);
        Assert.Equal(2, HybridFusion.Fuse(lex, sem, k: 60, limit: 1)[0].ConceptId);
        Assert.Equal(1, HybridFusion.Fuse(lex, sem, k: 1, limit: 1)[0].ConceptId);
    }

    [Fact]
    public void Rrf_rejects_k_below_one() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HybridFusion.Fuse(Items(1), Items(2), k: 0, limit: 5));

    [Fact]
    public async Task Query_cache_calls_the_factory_once_per_key()
    {
        var cache = new QueryEmbeddingCache(10);
        var calls = 0;
        Task<float[]> Factory() { calls++; return Task.FromResult(new[] { 1f }); }
        await cache.GetOrAddAsync("a", Factory);
        await cache.GetOrAddAsync("a", Factory);
        await cache.GetOrAddAsync("b", Factory);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("nomic-embed-text")]
    [InlineData("ollama:nomic-embed-text:v1.5")]
    [InlineData("sapbert/run-2")]
    public void Model_keys_with_safe_characters_are_accepted(string key) =>
        Assert.Equal(key, VectorSql.ValidateModelKey(key));

    [Theory]
    [InlineData("x'; DROP TABLE concept; --")]
    [InlineData("has space")]
    [InlineData("")]
    public void Model_keys_that_could_break_sql_are_rejected(string key) =>
        Assert.Throws<ArgumentException>(() => VectorSql.ValidateModelKey(key));

    [Fact]
    public void Vector_text_uses_invariant_decimal_points()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("[0.5,-1.25]", VectorSql.ToText(new[] { 0.5f, -1.25f }));
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }

    // ---- HTTP client retry behaviour ----

    private sealed class ScriptedHandler(params HttpStatusCode[] codes) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var code = codes[Math.Min(Calls, codes.Length - 1)];
            Calls++;
            var body = code == HttpStatusCode.OK ? "{\"embeddings\":[[0.1,0.2],[0.3,0.4]]}" : "{}";
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static OllamaEmbeddingClient Client(ScriptedHandler handler) =>
        new(new HttpClient(handler), new EmbeddingOptions { BaseUrl = "http://test", Model = "m" },
            maxAttempts: 4, delay: _ => TimeSpan.Zero);

    [Fact]
    public async Task Transient_errors_are_retried_until_success()
    {
        var handler = new ScriptedHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        var vectors = await Client(handler).EmbedAsync(new[] { "a", "b" });
        Assert.Equal(3, handler.Calls);
        Assert.Equal(2, vectors.Length);
    }

    [Fact]
    public async Task Client_errors_are_not_retried()
    {
        var handler = new ScriptedHandler(HttpStatusCode.BadRequest);
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler).EmbedAsync(new[] { "a", "b" }));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Retries_stop_after_the_attempt_limit()
    {
        var handler = new ScriptedHandler(HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler).EmbedAsync(new[] { "a", "b" }));
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task A_wrong_number_of_vectors_is_an_error()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK); // returns 2 vectors
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).EmbedAsync(new[] { "only one" }));
    }
}
