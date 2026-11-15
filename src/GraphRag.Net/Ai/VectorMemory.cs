// -----------------------------------------------------------------------------
// VectorMemory.cs — native replacement for SK's memory connectors
// (Microsoft.SemanticKernel.Plugins.Memory / Connectors.Sqlite / Connectors.Postgres
//  / OpenAITextEmbeddingGenerationService).
//
//  * Embeddings: OpenAI-compatible /v1/embeddings over the SAME endpoint-rewriting
//    HttpClient handler the project already used (config-compatible).
//  * Persistence: a SqlSugar [SugarTable] entity, auto-created by the existing
//    CodeFirst() pass; cosine similarity is computed in-process.
// -----------------------------------------------------------------------------
using System.Globalization;
using System.Text;
using System.Text.Json;
using GraphRag.Net.Base;
using GraphRag.Net.Options;
using GraphRag.Net.Utils;
using SqlSugar;

namespace GraphRag.Net.Ai
{
    public interface IEmbeddingClient
    {
        Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
    }

    public sealed record VectorHit(string Id, string Text, double Relevance);

    public interface IVectorStore
    {
        Task UpsertAsync(string collection, string id, string text, float[] vector, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<VectorHit>> SearchAsync(string collection, float[] query, int limit, double minRelevanceScore, CancellationToken cancellationToken = default);
        Task RemoveAsync(string collection, string id, CancellationToken cancellationToken = default);
    }

    /// <summary>Persistent vector record. Picked up automatically by CodeFirst().</summary>
    [SugarTable("GraphRagVectorRecords")]
    public class VectorRecord
    {
        [SugarColumn(IsPrimaryKey = true, Length = 512)]
        public string Key { get; set; } = string.Empty;

        [SugarColumn(Length = 256)]
        public string Collection { get; set; } = string.Empty;

        [SugarColumn(Length = 256)]
        public string RecordId { get; set; } = string.Empty;

        [SugarColumn(ColumnDataType = "text")]
        public string Text { get; set; } = string.Empty;

        [SugarColumn(ColumnDataType = "text")]
        public string Embedding { get; set; } = string.Empty;
    }

    /// <summary>OpenAI-compatible embedding client (keeps the existing endpoint handler).</summary>
    public sealed class EmbeddingClient : IEmbeddingClient
    {
        private readonly string _model;
        private readonly string _apiKey;
        private readonly string _endpoint;
        private readonly HttpClient? _httpClient;

        public EmbeddingClient(string? model = null, string? apiKey = null, string? endpoint = null, HttpClient? httpClient = null)
        {
            _model = model ?? GraphOpenAIOption.EmbeddingModel ?? "text-embedding-ada-002";
            _apiKey = apiKey ?? GraphOpenAIOption.Key ?? string.Empty;
            _endpoint = endpoint ?? GraphOpenAIOption.EndPoint ?? string.Empty;
            _httpClient = httpClient;
        }

        public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_endpoint))
                throw new InvalidOperationException("GraphOpenAI:EndPoint is not configured; cannot compute embeddings.");

            var client = _httpClient ?? BuildClient();
            var baseUri = new Uri(_endpoint.EndsWith("/", StringComparison.Ordinal) ? _endpoint : _endpoint + "/");
            var uri = new Uri(baseUri, "v1/embeddings");

            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { model = _model, input = text }),
                    Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            var data = payload.RootElement.GetProperty("data")[0].GetProperty("embedding");
            var result = new float[data.GetArrayLength()];
            int i = 0;
            foreach (var item in data.EnumerateArray()) result[i++] = item.GetSingle();
            return result;
        }

        private static HttpClient BuildClient()
        {
            var client = new HttpClient(new OpenAIHttpClientHandler()) { Timeout = TimeSpan.FromMinutes(10) };
            return client;
        }
    }

    /// <summary>SqlSugar-backed vector store with in-process cosine similarity.</summary>
    public sealed class VectorStore : IVectorStore
    {
        private readonly SqlSugarScope _db;
        private readonly object _initLock = new();
        private bool _initialized;

        public VectorStore(SqlSugarScope? db = null)
        {
            _db = db ?? new Repository<VectorRecord>().GetDB();
        }

        private void EnsureTable()
        {
            if (_initialized) return;
            lock (_initLock)
            {
                if (_initialized) return;
                _db.CodeFirst.InitTables<VectorRecord>();
                _initialized = true;
            }
        }

        public Task UpsertAsync(string collection, string id, string text, float[] vector, CancellationToken cancellationToken = default)
        {
            EnsureTable();
            cancellationToken.ThrowIfCancellationRequested();
            var key = MakeKey(collection, id);
            var record = new VectorRecord
            {
                Key = key,
                Collection = collection,
                RecordId = id,
                Text = text,
                Embedding = Serialize(vector)
            };
            _db.Deleteable<VectorRecord>().Where(x => x.Key == key).ExecuteCommand();
            _db.Insertable(record).ExecuteCommand();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VectorHit>> SearchAsync(
            string collection, float[] query, int limit, double minRelevanceScore,
            CancellationToken cancellationToken = default)
        {
            EnsureTable();
            cancellationToken.ThrowIfCancellationRequested();

            var rows = _db.Queryable<VectorRecord>()
                .Where(x => x.Collection == collection)
                .ToList();

            double queryNorm = Norm(query);
            var hits = new List<VectorHit>();
            foreach (var row in rows)
            {
                var vector = Deserialize(row.Embedding);
                double relevance = Cosine(query, queryNorm, vector);
                if (relevance >= minRelevanceScore)
                    hits.Add(new VectorHit(row.RecordId, row.Text, relevance));
            }

            IReadOnlyList<VectorHit> result = hits
                .OrderByDescending(h => h.Relevance)
                .Take(Math.Max(1, limit))
                .ToList();
            return Task.FromResult(result);
        }

        public Task RemoveAsync(string collection, string id, CancellationToken cancellationToken = default)
        {
            EnsureTable();
            cancellationToken.ThrowIfCancellationRequested();
            _db.Deleteable<VectorRecord>().Where(x => x.Key == MakeKey(collection, id)).ExecuteCommand();
            return Task.CompletedTask;
        }

        private static string MakeKey(string collection, string id) => collection + "\u001f" + id;

        private static double Cosine(float[] a, double aNorm, float[] b)
        {
            if (a.Length == 0 || b.Length == 0 || a.Length != b.Length || aNorm == 0) return 0;
            double dot = 0, bNorm = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                bNorm += b[i] * b[i];
            }
            bNorm = Math.Sqrt(bNorm);
            return bNorm == 0 ? 0 : dot / (aNorm * bNorm);
        }

        private static double Norm(float[] v)
        {
            double sum = 0;
            for (int i = 0; i < v.Length; i++) sum += v[i] * v[i];
            return Math.Sqrt(sum);
        }

        private static string Serialize(float[] v)
        {
            var bytes = new byte[v.Length * sizeof(float)];
            Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
            return Convert.ToBase64String(bytes);
        }

        private static float[] Deserialize(string? s)
        {
            if (string.IsNullOrEmpty(s)) return Array.Empty<float>();
            try
            {
                var bytes = Convert.FromBase64String(s);
                var v = new float[bytes.Length / sizeof(float)];
                Buffer.BlockCopy(bytes, 0, v, 0, v.Length * sizeof(float));
                return v;
            }
            catch (FormatException)
            {
                // Tolerate legacy comma-separated vectors.
                return s.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => float.Parse(x, CultureInfo.InvariantCulture))
                    .ToArray();
            }
        }
    }
}
