using GraphRag.Net.Domain.Model.Graph;
using GraphRag.Net.Repositories;

namespace GraphRag.Net.Domain.Interface
{
    /// <summary>
    /// 索引侧增强能力：Claims 抽取、增量索引、自动提示词调优、索引统计。
    /// </summary>
    public interface IGraphIndexService
    {
        /// <summary>对文本单元抽取 claims（重复调用默认跳过已处理块）。</summary>
        Task<ClaimExtractionSummary> ExtractClaimsAsync(string index, bool force = false);

        /// <summary>列出索引下的 claims。</summary>
        Task<List<Claims>> GetClaimsAsync(string index);

        /// <summary>
        /// 增量索引：按内容哈希检测未变/新增/失效块，只重新抽取变化部分并重建社区。
        /// </summary>
        Task<IncrementalIndexResult> IncrementalIndexAsync(string index, string input, string? documentId = null);

        /// <summary>自动提示词调优，结果写入运行时 PromptOverrides 立即生效。</summary>
        Task<PromptTuneResult> AutoTunePromptsAsync(string index, string? domain = null);

        IReadOnlyDictionary<string, string> GetPromptOverrides();
        void ClearPromptOverrides();

        /// <summary>索引统计（供回归测试与运维观测）。</summary>
        Task<IndexStats> GetIndexStatsAsync(string index);
    }
}
