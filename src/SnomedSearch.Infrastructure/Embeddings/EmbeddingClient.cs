using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SnomedSearch.Infrastructure.Embeddings
{
    public sealed class EmbeddingOptions
    {
        /// <summary>Base URL of a server speaking Ollama's /api/embed protocol, e.g. http://localhost:11434.</summary>
        public string? BaseUrl { get; set; }

        /// <summary>Model name sent to the server, e.g. nomic-embed-text.</summary>
        public string? Model { get; set; }

        /// <summary>Key the vectors are stored under. Defaults to the model name. Change it to keep two runs apart.</summary>
        public string? ModelKey { get; set; }

        /// <summary>Text put before every query (nomic-embed-text expects "search_query: ").</summary>
        public string QueryPrefix { get; set; } = "";

        /// <summary>Text put before every description when embedding (nomic-embed-text expects "search_document: ").</summary>
        public string DocumentPrefix { get; set; } = "";

        /// <summary>Semantic tags whose descriptions are embedded.</summary>
        public string[] Tags { get; set; } = { "finding", "disorder" };

        public int TimeoutSeconds { get; set; } = 120;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);

        public string EffectiveModelKey => string.IsNullOrWhiteSpace(ModelKey) ? Model! : ModelKey!;
    }

    public interface IEmbeddingClient
    {
        Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default);
    }

    /// <summary>
    /// Client for Ollama's POST /api/embed. The SapBERT server in tools/embed-server speaks the same protocol,
    /// so one client covers both models in the bake-off.
    /// Transient failures (network errors, timeouts, 429, 5xx) are retried with exponential backoff.
    /// </summary>
    public sealed class OllamaEmbeddingClient : IEmbeddingClient
    {
        private readonly HttpClient _http;
        private readonly EmbeddingOptions _options;
        private readonly int _maxAttempts;
        private readonly Func<int, TimeSpan> _delay;

        public OllamaEmbeddingClient(HttpClient http, EmbeddingOptions options, int maxAttempts = 5, Func<int, TimeSpan>? delay = null)
        {
            if (!options.IsConfigured) throw new ArgumentException("Embedding BaseUrl and Model must be set.", nameof(options));
            _http = http;
            _http.BaseAddress ??= new Uri(options.BaseUrl!.TrimEnd('/') + "/");
            _http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            _options = options;
            _maxAttempts = maxAttempts;
            _delay = delay ?? (attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250)));
        }

        public async Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
        {
            if (inputs.Count == 0) return Array.Empty<float[]>();

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var response = await _http.PostAsJsonAsync("api/embed", new EmbedRequest(_options.Model!, inputs), ct);
                    if (IsTransient(response.StatusCode) && attempt < _maxAttempts)
                    {
                        await Task.Delay(_delay(attempt), ct);
                        continue;
                    }
                    response.EnsureSuccessStatusCode();

                    var body = await response.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken: ct)
                               ?? throw new InvalidOperationException("Empty response from embedding server.");
                    if (body.Embeddings is null || body.Embeddings.Length != inputs.Count)
                    {
                        throw new InvalidOperationException(
                            $"Embedding server returned {body.Embeddings?.Length ?? 0} vectors for {inputs.Count} inputs.");
                    }
                    return body.Embeddings;
                }
                // Only network failures and timeouts are retried here. An HttpRequestException with a status code
                // comes from EnsureSuccessStatusCode, i.e. a non-transient response such as 400, and is not retried.
                catch (Exception e) when (attempt < _maxAttempts && !ct.IsCancellationRequested &&
                                          e is HttpRequestException { StatusCode: null } or TaskCanceledException)
                {
                    await Task.Delay(_delay(attempt), ct);
                }
            }
        }

        private static bool IsTransient(HttpStatusCode code) =>
            code == HttpStatusCode.TooManyRequests || (int)code >= 500;

        private sealed record EmbedRequest(
            [property: JsonPropertyName("model")] string Model,
            [property: JsonPropertyName("input")] IReadOnlyList<string> Input);

        private sealed record EmbedResponse(
            [property: JsonPropertyName("embeddings")] float[][]? Embeddings);
    }
}
