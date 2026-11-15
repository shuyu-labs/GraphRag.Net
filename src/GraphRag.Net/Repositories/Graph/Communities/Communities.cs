using SqlSugar;

namespace GraphRag.Net.Repositories
{
    /// <summary>
    /// 社区
    /// </summary>
    [SugarTable("Communities")]
    [SugarIndex("i_communities_index", nameof(Communities.Index), OrderByType.Asc)]
    public class Communities
    {
        /// <summary>
        /// 社区ID
        /// </summary>
        [SugarColumn(IsPrimaryKey = true)]
        public string CommunitieId { get; set; }

        /// <summary>
        /// 索引信息
        /// </summary>
        public string Index { get; set; }

        /// <summary>
        /// 社区摘要（纯文本，兼容既有 community_search 提示词）
        /// </summary>
        [SugarColumn(ColumnDataType = "varchar(4000)")]
        public string Summaries { get; set; }

        /// <summary>社区报告标题（community_reports.title）</summary>
        [SugarColumn(ColumnDataType = "varchar(512)")]
        public string? Title { get; set; }

        /// <summary>完整社区报告（渲染后文本，global search 的 map 阶段直接使用）</summary>
        [SugarColumn(ColumnDataType = "text")]
        public string? FullContent { get; set; }

        /// <summary>结构化关键发现 JSON 数组（community_reports.findings）</summary>
        [SugarColumn(ColumnDataType = "text")]
        public string? Findings { get; set; }

        /// <summary>影响力评分 0-10（community_reports.rank）</summary>
        public double Rating { get; set; }

        /// <summary>评分说明（community_reports.rating_explanation）</summary>
        [SugarColumn(ColumnDataType = "varchar(2000)")]
        public string? RatingExplanation { get; set; }

        /// <summary>社区内实体数量（community_reports.size）</summary>
        public int Size { get; set; }

        /// <summary>社区层级：0 为最细粒度，数值越大越粗</summary>
        public int Level { get; set; }

        /// <summary>父社区 ID（上一层）</summary>
        [SugarColumn(ColumnDataType = "varchar(128)")]
        public string? Parent { get; set; }

        /// <summary>子社区 ID 列表（逗号分隔）</summary>
        [SugarColumn(ColumnDataType = "text")]
        public string? Children { get; set; }

        /// <summary>社区排序分：成员实体度数之和 + 影响力评分，用于动态选择回退</summary>
        public double CommunityRank { get; set; }
    }
}