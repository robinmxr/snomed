using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using dotenv.net;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SnomedSearch.Core.Interfaces;
using SnomedSearch.Core.Models;
using SnomedSearch.Infrastructure.Data;
using SnomedSearch.Infrastructure.Embeddings;

namespace SnomedSearch.ConsoleApp
{
    /// <summary>
    /// Command line tool. Runs the same on Windows, macOS and Linux.
    ///   search                     interactive lexical search (default)
    ///   embed  [options]           resumable embedding backfill
    ///   eval   [options]           recall@1/5/10 for lexical, semantic and hybrid, written to docs/evaluation.md
    /// Embedding options (flags override the Embedding__* environment variables):
    ///   --profile nomic|sapbert    preset model, URL and prefixes
    ///   --url URL --model NAME --key KEY --query-prefix TEXT --doc-prefix TEXT --tags finding,disorder
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            // Values already in the environment win over the .env file.
            DotEnv.Load(new DotEnvOptions(ignoreExceptions: true, overwriteExistingVars: false, probeForEnv: true, probeLevelsToSearch: 4));
            var command = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "search";
            var opts = Options.Parse(args.SkipWhile(a => !a.StartsWith("--")).ToArray());

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("\nstopping after the current batch..."); };

            try
            {
                return command switch
                {
                    "search" => await InteractiveSearch(),
                    "embed" => await Embed(opts, cts.Token),
                    "eval" => await Evaluate(opts, cts.Token),
                    _ => Usage()
                };
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("cancelled. Re-run the same command to continue.");
                return 130;
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException or HttpRequestException)
            {
                Console.Error.WriteLine("error: " + e.Message);
                return 1;
            }
        }

        private static int Usage()
        {
            Console.WriteLine("usage: SnomedSearch.Console [search|embed|eval] [options]  (see Program.cs header)");
            return 2;
        }

        // ---------------------------------------------------------------- shared setup

        private static string ConnectionString() => DatabaseSettings.FromEnvironment().ToConnectionString();

        private static SnomedDbContext NewContext() =>
            new(new DbContextOptionsBuilder<SnomedDbContext>().UseNpgsql(ConnectionString()).Options);

        private static EmbeddingOptions EmbeddingFrom(Options o)
        {
            string? Env(string name) => Environment.GetEnvironmentVariable("Embedding__" + name);

            var e = new EmbeddingOptions
            {
                BaseUrl = Env("BaseUrl"),
                Model = Env("Model"),
                ModelKey = Env("ModelKey"),
                QueryPrefix = Env("QueryPrefix") ?? "",
                DocumentPrefix = Env("DocumentPrefix") ?? "",
            };

            switch (o.Get("profile"))
            {
                case null: break;
                case "nomic":
                    e.BaseUrl ??= "http://localhost:11434";
                    e.Model = "nomic-embed-text";
                    e.QueryPrefix = "search_query: ";
                    e.DocumentPrefix = "search_document: ";
                    break;
                case "sapbert":
                    e.BaseUrl = "http://localhost:8000";
                    e.Model = "sapbert";
                    e.QueryPrefix = "";
                    e.DocumentPrefix = "";
                    break;
                default: throw new ArgumentException("--profile must be nomic or sapbert");
            }

            e.BaseUrl = o.Get("url") ?? e.BaseUrl;
            e.Model = o.Get("model") ?? e.Model;
            e.ModelKey = o.Get("key") ?? e.ModelKey;
            e.QueryPrefix = o.Get("query-prefix") ?? e.QueryPrefix;
            e.DocumentPrefix = o.Get("doc-prefix") ?? e.DocumentPrefix;
            if (o.Get("tags") is { } tags) e.Tags = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return e;
        }

        // ---------------------------------------------------------------- embed

        private static async Task<int> Embed(Options o, CancellationToken ct)
        {
            var e = EmbeddingFrom(o);
            if (!e.IsConfigured) throw new ArgumentException("Set --profile, or --url and --model (or Embedding__BaseUrl and Embedding__Model).");

            using var http = new HttpClient();
            var client = new OllamaEmbeddingClient(http, e);
            var backfill = new EmbeddingBackfill(ConnectionString(), client, e);

            Console.WriteLine($"embedding tags [{string.Join(", ", e.Tags)}] with {e.Model} at {e.BaseUrl} as key '{e.EffectiveModelKey}'");
            var last = Stopwatch.StartNew();
            var result = await backfill.RunAsync(
                batchSize: o.GetInt("batch") ?? 64,
                reembed: o.Has("reembed"),
                maxRows: o.GetInt("max"),
                progress: p =>
                {
                    if (last.Elapsed.TotalSeconds < 2 && p.Remaining > 0) return;
                    last.Restart();
                    var eta = p.RowsPerSecond > 0 ? TimeSpan.FromSeconds(p.Remaining / p.RowsPerSecond) : TimeSpan.Zero;
                    Console.WriteLine($"  {p.Done,9:N0} done  {p.Remaining,9:N0} left  {p.RowsPerSecond,7:N1}/s  eta {eta:hh\\:mm\\:ss}");
                },
                log: Console.WriteLine,
                ct: ct);

            Console.WriteLine($"done: {result.Embedded:N0} embedded, {result.Deleted:N0} stale removed, dim {result.Dimension}, " +
                              $"{result.Elapsed:hh\\:mm\\:ss}{(result.IndexCreated ? ", HNSW index built" : "")}");
            return 0;
        }

        // ---------------------------------------------------------------- eval

        private sealed record Pair(string Phrase, long Expected);

        private sealed record ModeResult(string Mode, int?[] Ranks, double[] LatenciesMs);

        private static async Task<int> Evaluate(Options o, CancellationToken ct)
        {
            var pairsPath = o.Get("pairs") ?? FindUp("eval/pairs.csv");
            var outPath = o.Get("out") ?? Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(pairsPath))!, "docs", "evaluation.md");
            var pairs = ReadPairs(pairsPath);
            var tags = (o.Get("eval-tags") ?? "finding,disorder").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var e = EmbeddingFrom(o);
            var hybrid = new HybridOptions
            {
                RrfK = o.GetInt("rrf-k") ?? 60,
                LexicalCandidates = o.GetInt("lexical-candidates") ?? 50,
                SemanticCandidates = o.GetInt("semantic-candidates") ?? 50,
            };
            var semanticOpts = new SemanticOptions { EfSearch = o.GetInt("ef-search") ?? 100 };

            var modes = (o.Get("modes") ?? (e.IsConfigured ? "lexical,semantic,hybrid" : "lexical"))
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(m => Enum.Parse<SearchMode>(m, ignoreCase: true)).ToList();
            if (modes.Any(m => m != SearchMode.Lexical) && !e.IsConfigured)
                throw new ArgumentException("Semantic and hybrid need an embedding model: pass --profile, or --url and --model.");

            using var http = new HttpClient();
            await using var db = NewContext();
            SemanticSearch? semantic = e.IsConfigured
                ? new SemanticSearch(db, new OllamaEmbeddingClient(http, e), e, semanticOpts) : null;
            ISnomedRepository repo = new SnomedRepository(db, null, semantic, hybrid);

            Console.WriteLine($"{pairs.Count} pairs from {pairsPath}");
            var results = new List<ModeResult>();
            foreach (var mode in modes)
            {
                // One untimed warm-up query per mode (connection, JIT, first embedding call).
                await repo.SearchAsync(new ConceptSearchQuery("warm up", mode, tags, 10, 0, null), ct);

                var ranks = new int?[pairs.Count];
                var lat = new double[pairs.Count];
                for (var i = 0; i < pairs.Count; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var r = await repo.SearchAsync(new ConceptSearchQuery(pairs[i].Phrase, mode, tags, 10, 0, null), ct);
                    lat[i] = sw.Elapsed.TotalMilliseconds;
                    var idx = r.Items.FindIndex(x => x.ConceptId == pairs[i].Expected);
                    ranks[i] = idx < 0 ? null : idx + 1;
                }
                results.Add(new ModeResult(mode.ToString().ToLowerInvariant(), ranks, lat));
                Console.WriteLine($"  {mode,-8} R@10 {Recall(ranks, 10):P0}");
            }

            var label = o.Get("label") ?? (e.IsConfigured ? e.EffectiveModelKey : "lexical-only");
            var section = await RenderSectionAsync(label, pairs, results, tags, e, hybrid, semanticOpts, o.Get("hardware"), ct);
            WriteSection(outPath, label, section);
            Console.WriteLine($"wrote section '{label}' to {outPath}");
            return 0;
        }

        private static double Recall(int?[] ranks, int k) => ranks.Count(r => r is not null && r <= k) / (double)ranks.Length;

        private static double Mrr(int?[] ranks, int k) => ranks.Sum(r => r is not null && r <= k ? 1.0 / r.Value : 0) / ranks.Length;

        private static double Percentile(double[] values, double p)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            var idx = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
            return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
        }

        private static async Task<string> RenderSectionAsync(string label, List<Pair> pairs, List<ModeResult> results,
            string[] tags, EmbeddingOptions e, HybridOptions h, SemanticOptions s, string? hardware, CancellationToken ct)
        {
            await using var conn = new NpgsqlConnection(ConnectionString());
            await conn.OpenAsync(ct);
            async Task<long> Count(string sql, string? key = null)
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                if (key is not null) cmd.Parameters.AddWithValue("k", key);
                try { return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)); } catch (PostgresException) { return 0; }
            }
            var searchRows = await Count("SELECT count(*) FROM search_term");
            var embedded = e.IsConfigured
                ? await Count("SELECT count(*) FROM description_embedding WHERE model_key = @k", e.EffectiveModelKey) : 0;

            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine($"### {label}").AppendLine();
            sb.AppendLine($"- Date: {DateTime.UtcNow:yyyy-MM-dd} (UTC)");
            sb.AppendLine($"- Pairs: {pairs.Count} from `eval/pairs.csv`; tag filter: {string.Join(", ", tags)}; top 10 retrieved");
            sb.AppendLine(string.Create(inv, $"- Data: {searchRows:N0} searchable descriptions") +
                          (e.IsConfigured ? string.Create(inv, $"; {embedded:N0} embedded with `{e.EffectiveModelKey}` (model `{e.Model}`)") : ""));
            sb.AppendLine($"- Machine: {RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical CPUs" +
                          (hardware is null ? "" : $", {hardware}"));
            if (e.IsConfigured)
                sb.AppendLine($"- Settings: ef_search {s.EfSearch}; RRF k {h.RrfK}; candidates lexical {h.LexicalCandidates}, semantic {h.SemanticCandidates}; query prefix \"{e.QueryPrefix}\"");
            sb.AppendLine("- Latency: wall time per query from the client to the database, after one warm-up query" +
                          (e.IsConfigured ? "; semantic and hybrid include the query embedding call (each phrase is new, so the cache does not help)." : "."));
            sb.AppendLine();
            sb.AppendLine("| Mode | Recall@1 | Recall@5 | Recall@10 | MRR@10 | p50 ms | p95 ms |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var r in results)
            {
                sb.AppendLine(string.Create(inv,
                    $"| {r.Mode} | {Recall(r.Ranks, 1):0.00} | {Recall(r.Ranks, 5):0.00} | {Recall(r.Ranks, 10):0.00} | {Mrr(r.Ranks, 10):0.00} | {Percentile(r.LatenciesMs, 50):0.0} | {Percentile(r.LatenciesMs, 95):0.0} |"));
            }
            sb.AppendLine();
            sb.AppendLine("<details><summary>Rank of the expected concept per phrase (blank = not in top 10)</summary>").AppendLine();
            sb.AppendLine("| Phrase | " + string.Join(" | ", results.Select(r => r.Mode)) + " |");
            sb.AppendLine("|---|" + string.Concat(results.Select(_ => "---|")));
            for (var i = 0; i < pairs.Count; i++)
                sb.AppendLine($"| {pairs[i].Phrase} | " + string.Join(" | ", results.Select(r => r.Ranks[i]?.ToString() ?? "")) + " |");
            sb.AppendLine().AppendLine("</details>");
            return sb.ToString();
        }

        /// <summary>Replaces the section with the same label, or appends it, so several runs share one file.</summary>
        private static void WriteSection(string path, string label, string body)
        {
            var start = $"<!-- eval:{label} -->";
            var end = $"<!-- /eval:{label} -->";
            var block = start + "\n" + body.TrimEnd() + "\n" + end;
            var text = File.Exists(path)
                ? File.ReadAllText(path)
                : "# Evaluation\n\nGenerated by `SnomedSearch.Console eval`. Each section is one run; re-running with the same label replaces it.\n" +
                  "The phrases and expected concepts are in `eval/pairs.csv` (see `eval/README.md`).\n";
            var a = text.IndexOf(start, StringComparison.Ordinal);
            var b = text.IndexOf(end, StringComparison.Ordinal);
            text = a >= 0 && b > a
                ? text[..a] + block + text[(b + end.Length)..]
                : text.TrimEnd() + "\n\n" + block + "\n";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, text.Replace("\r\n", "\n"));
        }

        private static List<Pair> ReadPairs(string path)
        {
            var pairs = new List<Pair>();
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
                var cut = line.LastIndexOf(',');
                pairs.Add(new Pair(line[..cut].Trim().Trim('"'), long.Parse(line[(cut + 1)..].Trim(), CultureInfo.InvariantCulture)));
            }
            return pairs;
        }

        private static string FindUp(string relative)
        {
            for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate)) return candidate;
            }
            throw new ArgumentException($"Could not find {relative}; pass --pairs PATH.");
        }

        // ---------------------------------------------------------------- interactive search

        private static async Task<int> InteractiveSearch()
        {
            await using var db = NewContext();
            ISnomedRepository repository = new SnomedRepository(db);

            Console.WriteLine("SNOMED CT search. Type 'exit' to quit.");
            while (true)
            {
                Console.Write("\nsearch: ");
                var query = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(query) || query.Equals("exit", StringComparison.OrdinalIgnoreCase)) return 0;

                var response = await repository.SearchAsync(new ConceptSearchQuery(query, SearchMode.Lexical, null, 20, 0, null));
                if (response.Items.Count == 0) { Console.WriteLine("no results"); continue; }

                Console.WriteLine($"{response.TotalCount} matches{(response.Fuzzy ? " (typo tolerant)" : "")}");
                for (var i = 0; i < response.Items.Count; i++)
                    Console.WriteLine($"{i + 1,3}. {response.Items[i].PreferredTerm}  [{response.Items[i].SemanticTag}] {response.Items[i].ConceptId}");

                Console.Write("number for details, or Enter: ");
                if (int.TryParse(Console.ReadLine(), out var n) && n >= 1 && n <= response.Items.Count)
                {
                    var d = await repository.GetConceptDetailsAsync(response.Items[n - 1].ConceptId);
                    if (d is null) continue;
                    Console.WriteLine($"  {d.Fsn}\n  synonyms: {string.Join("; ", d.Synonyms)}\n  children: {d.ChildrenCount}");
                    foreach (var p in d.Parents) Console.WriteLine($"  parent: {p.PreferredTerm} ({p.ConceptId})");
                }
            }
        }

        // ---------------------------------------------------------------- tiny option parser

        private sealed class Options
        {
            private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

            public static Options Parse(string[] args)
            {
                var o = new Options();
                for (var i = 0; i < args.Length; i++)
                {
                    if (!args[i].StartsWith("--")) throw new ArgumentException($"unexpected argument '{args[i]}'");
                    var name = args[i][2..];
                    string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
                    o._values[name] = value;
                }
                return o;
            }

            public bool Has(string name) => _values.ContainsKey(name);
            public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;
            public int? GetInt(string name) => Get(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : null;
        }
    }
}
