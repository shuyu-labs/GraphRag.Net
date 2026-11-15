using Microsoft.SemanticKernel.Memory;

namespace GraphRag.Net.Ai
{
    /// <summary>
    /// 向量记忆的构造出口。默认使用 OpenAI 兼容 EmbeddingClient + SqlSugar VectorStore；
    /// 可通过 SetFactory 注入自定义实现（离线测试、自定义 embedding 服务、其它向量库）。
    /// </summary>
    public static class VectorMemoryProvider
    {
        private static Func<SemanticTextMemory> _factory = Default;

        private static SemanticTextMemory Default()
            => new SemanticTextMemory(new EmbeddingClient(), new VectorStore());

        public static void SetFactory(Func<SemanticTextMemory> factory)
            => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

        public static void Reset() => _factory = Default;

        public static SemanticTextMemory Create() => _factory();
    }
}
