using Microsoft.EntityFrameworkCore;
using Npgsql;
using SnomedSearch.Core.Models;
using SnomedSearch.Infrastructure.Data;
using SnomedSearch.Infrastructure.Embeddings;
using Xunit;

namespace SnomedSearch.Tests;

/// <summary>
/// Deterministic stand-in for an embedding model, used only in tests. It maps a few invented words to fixed
/// directions, so "gnawing" lands next to "nibble" although the two share no letters.
/// </summary>
internal sealed class FakeEmbeddingClient(int dimension = 4) : IEmbeddingClient
{
    public int Calls { get; private set; }

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(inputs.Select(Vector).ToArray());
    }

    private float[] Vector(string text)
    {
        var t = text.ToLowerInvariant();
        float[] v =
            t.Contains("nibble") || t.Contains("gnawing") ? [1f, 0.1f, 0f, 0f] :
            t.Contains("quillon") ? [0f, 1f, 0f, 0f] :
            t.Contains("brenwick") ? [0f, 0f, 1f, 0f] :
            t.Contains("frabjous") ? [0.1f, 0f, 0f, 1f] :
            [0.3f, 0.3f, 0.3f, 0.3f];
        return dimension == 4 ? v : v.Concat(Enumerable.Repeat(0.01f, dimension - 4)).ToArray();
    }
}

[Collection("database")]
public class SemanticTests(DatabaseFixture db)
{
    private static EmbeddingOptions Options(string key, string docPrefix = "") => new()
    {
        BaseUrl = "http://unused", Model = "fake", ModelKey = key, DocumentPrefix = docPrefix,
        Tags = ["finding", "disorder"]
    };

    private async Task<long> CountAsync(string sql, string? key = null)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (key is not null) cmd.Parameters.AddWithValue("k", key);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private SnomedDbContext NewContext() =>
        new(new DbContextOptionsBuilder<SnomedDbContext>().UseNpgsql(db.ConnectionString).Options);

    [Fact]
    public async Task Backfill_embeds_every_configured_description_and_resumes_without_redoing_work()
    {
        var expected = await CountAsync("SELECT count(*) FROM search_term WHERE semantic_tag IN ('finding','disorder')");
        var client = new FakeEmbeddingClient();
        var backfill = new EmbeddingBackfill(db.ConnectionString, client, Options("fake-resume"));

        var first = await backfill.RunAsync(batchSize: 3);
        Assert.Equal(expected, first.Embedded);
        Assert.Equal(4, first.Dimension);
        Assert.True(first.IndexCreated);
        Assert.Equal(expected, await CountAsync("SELECT count(*) FROM description_embedding WHERE model_key = @k", "fake-resume"));

        var second = await backfill.RunAsync(batchSize: 3);
        Assert.Equal(0, second.Embedded);
        Assert.False(second.IndexCreated);
    }

    [Fact]
    public async Task Backfill_stopped_midway_continues_where_it_left_off()
    {
        var expected = await CountAsync("SELECT count(*) FROM search_term WHERE semantic_tag IN ('finding','disorder')");
        var backfill = new EmbeddingBackfill(db.ConnectionString, new FakeEmbeddingClient(), Options("fake-partial"));

        var partial = await backfill.RunAsync(batchSize: 2, maxRows: 4);
        Assert.Equal(4, partial.Embedded);

        var rest = await backfill.RunAsync(batchSize: 2);
        Assert.Equal(expected - 4, rest.Embedded);
    }

    [Fact]
    public async Task Changing_the_embedded_text_triggers_a_re_embed()
    {
        var expected = await CountAsync("SELECT count(*) FROM search_term WHERE semantic_tag IN ('finding','disorder')");
        await new EmbeddingBackfill(db.ConnectionString, new FakeEmbeddingClient(), Options("fake-prefix")).RunAsync();

        var withPrefix = await new EmbeddingBackfill(db.ConnectionString, new FakeEmbeddingClient(), Options("fake-prefix", "doc: "))
            .RunAsync();
        Assert.Equal(expected, withPrefix.Embedded);
    }

    [Fact]
    public async Task A_new_dimension_for_an_existing_key_is_refused_unless_reembed_is_set()
    {
        await new EmbeddingBackfill(db.ConnectionString, new FakeEmbeddingClient(4), Options("fake-dim")).RunAsync();

        var bigger = new EmbeddingBackfill(db.ConnectionString, new FakeEmbeddingClient(8), Options("fake-dim"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bigger.RunAsync());

        var replaced = await bigger.RunAsync(reembed: true);
        Assert.Equal(8, replaced.Dimension);
        Assert.Equal(8, await CountAsync("SELECT dimension FROM embedding_model WHERE model_key = @k", "fake-dim"));
    }

    private async Task<SnomedRepository> RepositoryAsync(string key)
    {
        await new EmbeddingBackfill(db.ConnectionString, new FakeEmbeddingClient(), Options(key)).RunAsync();
        var ctx = NewContext();
        var semantic = new SemanticSearch(ctx, new FakeEmbeddingClient(), Options(key));
        return new SnomedRepository(ctx, null, semantic, new HybridOptions { RrfK = 60, LexicalCandidates = 10, SemanticCandidates = 10 });
    }

    [Fact]
    public async Task Semantic_mode_finds_concepts_that_share_no_words_with_the_query()
    {
        var repo = await RepositoryAsync("fake-search");

        var lexical = await repo.SearchAsync(new ConceptSearchQuery("gnawing", SearchMode.Lexical, null, 10, 0, null));
        Assert.DoesNotContain(lexical.Items, i => i.ConceptId is 9200001 or 9200002 or 9200003);

        var semantic = await repo.SearchAsync(new ConceptSearchQuery("gnawing", SearchMode.Semantic, null, 3, 0, null));
        Assert.Equal("semantic", semantic.Mode);
        Assert.Equal(new long[] { 9200001, 9200002, 9200003 }, semantic.Items.Select(i => i.ConceptId).OrderBy(x => x));
    }

    [Fact]
    public async Task Semantic_mode_applies_the_tag_filter()
    {
        var repo = await RepositoryAsync("fake-tags");
        var r = await repo.SearchAsync(new ConceptSearchQuery("gnawing", SearchMode.Semantic, ["disorder"], 5, 0, null));
        Assert.All(r.Items, i => Assert.Equal("disorder", i.SemanticTag));
    }

    [Fact]
    public async Task Hybrid_mode_ranks_concepts_found_by_both_lists_first()
    {
        var repo = await RepositoryAsync("fake-hybrid");
        var r = await repo.SearchAsync(new ConceptSearchQuery("nibble pain", SearchMode.Hybrid, null, 5, 0, null));
        Assert.Equal("hybrid", r.Mode);
        Assert.Equal(9200001, r.Items[0].ConceptId); // exact lexical match, also semantically closest
        Assert.Null(r.NextCursor);
    }

    [Fact]
    public async Task Semantic_and_hybrid_modes_reject_paging()
    {
        var repo = await RepositoryAsync("fake-paging");
        await Assert.ThrowsAsync<InvalidSearchRequestException>(() =>
            repo.SearchAsync(new ConceptSearchQuery("nibble", SearchMode.Semantic, null, 5, 5, null)));
        await Assert.ThrowsAsync<InvalidSearchRequestException>(() =>
            repo.SearchAsync(new ConceptSearchQuery("nibble", SearchMode.Hybrid, null, 5, 0, "abc")));
    }

    [Fact]
    public async Task Query_embeddings_are_cached()
    {
        await new EmbeddingBackfill(db.ConnectionString, new FakeEmbeddingClient(), Options("fake-cache")).RunAsync();
        var client = new FakeEmbeddingClient();
        await using var ctx = NewContext();
        var semantic = new SemanticSearch(ctx, client, Options("fake-cache"));

        await semantic.SearchAsync("gnawing", null, 3);
        await semantic.SearchAsync("Gnawing", null, 3); // normalizes to the same key
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task Semantic_search_without_stored_vectors_says_so()
    {
        await using var ctx = NewContext();
        var semantic = new SemanticSearch(ctx, new FakeEmbeddingClient(), Options("never-embedded"));
        await Assert.ThrowsAsync<NotSupportedException>(() => semantic.SearchAsync("nibble", null, 3));
    }
}
