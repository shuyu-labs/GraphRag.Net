using GraphRag.Net.Domain.Model.Graph;
using GraphRag.Net.Repositories;
using Microsoft.SemanticKernel;

namespace GraphRag.Net.Domain.Interface
{
    /// <summary>
    /// 对齐 microsoft/graphrag Query Engine 的新增检索能力：
    /// Basic Search、Global Search（Map-Reduce）、Question Generation。
    /// 与既有 IGraphService 分离，避免破坏已有调用方。
    /// </summary>
    public interface IGraphSearchService
    {
        /// <summary>基础向量 RAG：直接对原始文本单元检索并回答，返回可追溯来源。</summary>
        Task<BasicSearchResult> SearchBasicAsync(string index, string input, int? topK = null);

        /// <summary>基础向量 RAG，流式返回答案。</summary>
        IAsyncEnumerable<StreamingKernelContent> SearchBasicStreamAsync(string index, string input, int? topK = null);

        /// <summary>
        /// Map-Reduce 全局检索：动态社区选择 → map 关键点打分 → 排序过滤 → reduce 汇总。
        /// </summary>
        Task<GlobalSearchResult> SearchGlobalAsync(string index, string input, string? responseType = null, bool? dynamicSelection = null);

        /// <summary>Map-Reduce 全局检索，reduce 阶段流式返回。</summary>
        IAsyncEnumerable<StreamingKernelContent> SearchGlobalStreamAsync(string index, string input, string? responseType = null, bool? dynamicSelection = null);

        /// <summary>基于数据集主题与历史问题生成候选追问。</summary>
        Task<QuestionGenerationResult> GenerateQuestionsAsync(string index, IReadOnlyList<string>? history = null, int? count = null);

        /// <summary>列出索引下的原始文本单元（溯源检视）。</summary>
        Task<List<TextUnits>> GetTextUnitsAsync(string index);

        /// <summary>列出索引下的结构化社区报告。</summary>
        Task<List<CommunityReportInfo>> GetCommunityReportsAsync(string index);
    }
}
