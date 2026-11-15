using SqlSugar;

namespace GraphRag.Net.Repositories
{
    /// <summary>
    /// 节点
    /// </summary>
    [SugarTable("Nodes")]
    [SugarIndex("i_nodes_index", nameof(Nodes.Index), OrderByType.Asc)]
    public class Nodes
    {
        /// <summary>
        /// 主键
        /// </summary>
        [SugarColumn(IsPrimaryKey = true)]
        public string Id { get; set; }
        /// <summary>
        /// 索引
        /// </summary>
        public string Index { get; set; }
        /// <summary>
        /// 名称
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// 类型
        /// </summary>
        public string Type { get; set; }

        /// <summary>
        /// 节点描述
        /// </summary>
        [SugarColumn(ColumnDataType = "varchar(2000)")]
        public string? Desc { get; set; }

        /// <summary>
        /// 来源文本单元ID（逗号分隔）。对齐 GraphRAG 的 entity.text_unit_ids，
        /// 用于把实体回溯到原始文本块。
        /// </summary>
        [SugarColumn(ColumnDataType = "text")]
        public string? TextUnitIds { get; set; }

        /// <summary>节点度数（关联边数），对齐 entities.degree</summary>
        public int Degree { get; set; }

        /// <summary>出现过的文本单元数量，对齐 entities.frequency</summary>
        public int Frequency { get; set; }
    }
}