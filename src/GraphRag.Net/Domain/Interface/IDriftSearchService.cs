using GraphRag.Net.Domain.Model.Graph;
using Microsoft.SemanticKernel;

namespace GraphRag.Net.Domain.Interface
{
    /// <summary>
    /// DRIFT Search（Dynamic Reasoning and Inference with Flexible Traversal）。
    /// 三阶段：Primer（社区报告 → 初步答案 + 后续问题）、
    /// Follow-up（每个问题走 Local Search 并继续展开）、
    /// Output hierarchy（层级归并 + reduce 最终答案）。
    /// </summary>
    public interface IDriftSearchService
    {
        Task<DriftSearchResult> SearchAsync(string index, string input);
        IAsyncEnumerable<StreamingKernelContent> SearchStreamAsync(string index, string input);
    }
}
