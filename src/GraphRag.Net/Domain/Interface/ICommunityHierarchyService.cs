using GraphRag.Net.Domain.Model.Graph;

namespace GraphRag.Net.Domain.Interface
{
    /// <summary>
    /// 分层社区检测与层级社区报告。对齐 microsoft/graphrag 的
    /// Hierarchical Leiden（create_communities）+ Community Reports（每层一份）。
    /// 本实现用「折叠超节点 + 加权标签传播递归」近似层级聚类，保持确定性与零外部依赖。
    /// </summary>
    public interface ICommunityHierarchyService
    {
        /// <summary>重建指定索引的层级社区与报告，返回层级数。</summary>
        Task<int> RebuildAsync(string index);

        /// <summary>查询各层级的统计信息。</summary>
        List<CommunityLevelInfo> GetLevels(string index);
    }
}
