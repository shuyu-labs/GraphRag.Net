using GraphRag.Net.Repositories;

namespace GraphRag.Net.Domain.Interface
{
    /// <summary>
    /// 动态社区选择。对齐 microsoft/graphrag 的 DynamicCommunitySelection：
    /// LLM 对社区报告按问题相关度评分，支持 keep_parent / num_repeats / max_level 回退。
    /// </summary>
    public interface IDynamicCommunitySelectionService
    {
        Task<List<Communities>> SelectAsync(string index, string input, int? maxReports = null);

        /// <summary>索引中的最大社区层级。</summary>
        int MaxLevel(string index);
    }
}
