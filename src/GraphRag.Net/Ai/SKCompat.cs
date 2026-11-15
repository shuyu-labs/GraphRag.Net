// -----------------------------------------------------------------------------
// SKCompat.cs — Semantic Kernel compatibility surface
//
// The Microsoft.SemanticKernel.* packages have been REMOVED. To honour the
// "keep public interfaces / DI signatures / downstream source stable" constraint,
// the handful of SK types that appeared in public signatures are re-declared here
// under their original namespaces. They are now backed by pi.net (LLM calls) and
// native implementations (vector memory, text chunking) — no SK assembly remains.
//
// This is a deliberate adapter layer, not a vendored copy of Semantic Kernel.
// -----------------------------------------------------------------------------
using System.Runtime.CompilerServices;
using GraphRag.Net.Ai;

namespace Microsoft.SemanticKernel
{
    /// <summary>
    /// Streaming text chunk carrier. Kept name-compatible with SK's
    /// <c>StreamingKernelContent</c>; <see cref="ToString"/> returns the chunk text,
    /// which is how every existing caller consumes it.
    /// </summary>
    public sealed class StreamingKernelContent
    {
        public StreamingKernelContent(string text) => Text = text ?? string.Empty;

        public string Text { get; }

        public override string ToString() => Text;
    }
}

namespace Microsoft.SemanticKernel.Memory
{
    /// <summary>Metadata attached to a stored vector record (SK-compatible shape).</summary>
    public sealed class MemoryRecordMetadata
    {
        public MemoryRecordMetadata(
            string id,
            string text,
            string? description = null,
            string? additionalMetadata = null)
        {
            Id = id;
            Text = text;
            Description = description;
            AdditionalMetadata = additionalMetadata;
        }

        public string Id { get; }
        public string Text { get; }
        public string? Description { get; }
        public string? AdditionalMetadata { get; }
        public bool IsReference => false;
        public string ExternalSourceName => string.Empty;
    }

    /// <summary>A single vector-search hit (SK-compatible shape).</summary>
    public sealed class MemoryQueryResult
    {
        public MemoryQueryResult(MemoryRecordMetadata metadata, double relevance)
        {
            Metadata = metadata;
            Relevance = relevance;
        }

        public MemoryRecordMetadata Metadata { get; }
        public double Relevance { get; }
    }

    /// <summary>
    /// Marker interface retained so the original DI registration
    /// (<c>AddSingleton&lt;IMemoryStore, VolatileMemoryStore&gt;</c>) keeps compiling.
    /// Persistence is now handled by <see cref="GraphRag.Net.Ai.IVectorStore"/>.
    /// </summary>
    public interface IMemoryStore { }

    public sealed class VolatileMemoryStore : IMemoryStore { }

    /// <summary>
    /// Drop-in replacement for SK's <c>SemanticTextMemory</c>. Embeddings come from
    /// <see cref="IEmbeddingClient"/>, persistence + cosine search from
    /// <see cref="IVectorStore"/>. The public method surface is unchanged.
    /// </summary>
    public sealed class SemanticTextMemory
    {
        private readonly IEmbeddingClient _embedding;
        private readonly IVectorStore _store;

        public SemanticTextMemory(IEmbeddingClient embedding, IVectorStore store)
        {
            _embedding = embedding ?? throw new ArgumentNullException(nameof(embedding));
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public async Task SaveInformationAsync(
            string collection,
            string text,
            string id,
            string? description = null,
            string? additionalMetadata = null,
            CancellationToken cancellationToken = default)
        {
            var vector = await _embedding.EmbedAsync(text, cancellationToken).ConfigureAwait(false);
            await _store.UpsertAsync(collection, id, text, vector, cancellationToken).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<MemoryQueryResult> SearchAsync(
            string collection,
            string query,
            int limit = 1,
            double minRelevanceScore = 0.0,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (limit <= 0) yield break;

            var vector = await _embedding.EmbedAsync(query, cancellationToken).ConfigureAwait(false);
            var hits = await _store
                .SearchAsync(collection, vector, limit, minRelevanceScore, cancellationToken)
                .ConfigureAwait(false);

            foreach (var hit in hits)
            {
                yield return new MemoryQueryResult(
                    new MemoryRecordMetadata(hit.Id, hit.Text), hit.Relevance);
            }
        }

        public Task RemoveAsync(string collection, string id, CancellationToken cancellationToken = default)
            => _store.RemoveAsync(collection, id, cancellationToken);
    }
}

namespace Microsoft.SemanticKernel.Text
{
    /// <summary>
    /// Minimal native text splitter matching the two SK <c>TextChunker</c> entry
    /// points used by <c>GraphService.InsertTextChunkAsync</c>.
    /// </summary>
    public static class TextChunker
    {
        public static List<string> SplitPlainTextLines(string text, int maxTokensPerLine)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;

            int width = Math.Max(1, maxTokensPerLine);
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                // Split over-long lines on a character budget (rough token estimate).
                for (int i = 0; i < line.Length; i += width)
                {
                    result.Add(line.Substring(i, Math.Min(width, line.Length - i)));
                }
            }
            return result;
        }

        public static List<string> SplitPlainTextParagraphs(IEnumerable<string> lines, int maxTokensPerParagraph)
        {
            var result = new List<string>();
            int width = Math.Max(1, maxTokensPerParagraph);
            var current = new System.Text.StringBuilder();

            foreach (var line in lines)
            {
                if (current.Length > 0 && current.Length + line.Length + 1 > width)
                {
                    result.Add(current.ToString().Trim());
                    current.Clear();
                }
                if (current.Length > 0) current.Append('\n');
                current.Append(line);
            }

            if (current.Length > 0) result.Add(current.ToString().Trim());
            return result;
        }
    }
}
