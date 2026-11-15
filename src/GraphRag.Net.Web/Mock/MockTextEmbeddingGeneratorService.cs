// Deterministic offline embedding stub (replaces SK's ITextEmbeddingGenerationService mock).
// Useful to exercise vector memory without any API key.
using GraphRag.Net.Ai;

namespace GraphRag.Net.Web.Mock
{
    public sealed class MockTextEmbeddingGeneratorService : IEmbeddingClient
    {
        private const int Dimensions = 32;

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            var vector = new float[Dimensions];
            foreach (var ch in text ?? string.Empty)
            {
                int bucket = Math.Abs(ch) % Dimensions;
                vector[bucket] += 1f;
            }

            double norm = Math.Sqrt(vector.Sum(v => v * v));
            if (norm > 0)
                for (int i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);

            return Task.FromResult(vector);
        }
    }
}
