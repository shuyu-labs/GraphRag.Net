using SqlSugar;

namespace GraphRag.Net.Repositories
{
    /// <summary>
    /// 文本单元（对齐 microsoft/graphrag 的 TextUnit）：保存原始文本块，
    /// 为 Basic Search 提供检索语料，并为实体/边提供溯源依据。
    /// </summary>
    [SugarTable("TextUnits")]
    [SugarIndex("i_textunits_index", nameof(TextUnits.Index), OrderByType.Asc)]
    public class TextUnits
    {
        /// <summary>主键</summary>
        [SugarColumn(IsPrimaryKey = true)]
        public string Id { get; set; } = string.Empty;

        /// <summary>索引</summary>
        public string Index { get; set; } = string.Empty;

        /// <summary>文本块原文</summary>
        [SugarColumn(ColumnDataType = "text")]
        public string? Text { get; set; }

        /// <summary>预估 token 数</summary>
        public int NTokens { get; set; }

        /// <summary>来源文档标识（预留：多文档导入时使用）</summary>
        [SugarColumn(ColumnDataType = "varchar(512)")]
        public string? DocumentId { get; set; }
    }
}
