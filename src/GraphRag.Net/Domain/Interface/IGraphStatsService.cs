namespace GraphRag.Net.Domain.Interface
{
    /// <summary>
    /// 维护图统计信号：实体 degree / frequency、关系 weight、社区 rank。
    /// 对齐 microsoft/graphrag 输出表中的 degree / frequency / weight / rank 字段。
    /// </summary>
    public interface IGraphStatsService
    {
        /// <summary>重算指定索引的全部统计信号。</summary>
        Task RecomputeAsync(string index);
    }
}
