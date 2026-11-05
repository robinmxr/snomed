using System.Text.Json;
using Xunit;
using static SnomedSearch.Tests.TestHelpers;

namespace SnomedSearch.Tests;

/// <summary>
/// Golden lexical queries: the expected top results are written down in golden/lexical.json.
/// If ranking changes on purpose, update the file in the same commit and say why.
/// </summary>
[Collection("database")]
public class GoldenQueryTests(DatabaseFixture db)
{
    public sealed record Golden(string Query, string[]? Tags, long[] Expected);

    public static IEnumerable<object[]> Cases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "golden", "lexical.json");
        var cases = JsonSerializer.Deserialize<Golden[]>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        foreach (var c in cases) yield return new object[] { c };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Top_results_match_the_golden_file(Golden golden)
    {
        var url = "/api/concepts/search?q=" + Q(golden.Query) + "&limit=" + golden.Expected.Length;
        if (golden.Tags is not null) url += string.Concat(golden.Tags.Select(t => "&tags=" + Q(t)));

        var r = await db.Client.GetApiAsync(url);
        Assert.Equal(golden.Expected, r.ItemIds());
    }
}
