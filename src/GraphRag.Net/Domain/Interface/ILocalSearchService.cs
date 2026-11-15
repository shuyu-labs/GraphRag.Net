using GraphRag.Net.Domain.Model.Graph;
using Microsoft.SemanticKernel;

namespace GraphRag.Net.Domain.Interface
{
    /// <summary>
    /// Local Search（混合上下文），对齐 microsoft/graphrag 的
    /// local_search + mixed_context：把 entities / relationships / text units /
    /// community reports / covariates / 对话历史按预算融合成一个上下文窗口。
    /// </summary>
    public interface ILocalSearchService
    {
        Task<LocalSearchResult> SearchLocalAsync(
            string index, string input, IReadOnlyList<string>? history = null);

        IAsyncEnumerable<StreamingKernelContent> SearchLocalStreamAsync(
            string index, string input, IReadOnlyList<string>? history = null);

        /// <summary>只构建混合上下文（DRIFT follow-up 与回归测试使用）。</summary>
        Task<BuiltLocalContext> BuildContextAsync(
            string index, string input, IReadOnlyList<string>? history = null);
    }
}
