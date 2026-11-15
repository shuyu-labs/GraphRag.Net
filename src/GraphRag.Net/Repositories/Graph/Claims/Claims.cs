using SqlSugar;

namespace GraphRag.Net.Repositories
{
    /// <summary>
    /// 声明（Covariate），对齐 microsoft/graphrag 的 covariates 产物。
    /// 默认关闭抽取，可通过 GraphQueryOption.EnableClaimExtraction 开启。
    /// </summary>
    [SugarTable("Claims")]
    [SugarIndex("i_claims_index", nameof(Claims.Index), OrderByType.Asc)]
    [SugarIndex("i_claims_textunit", nameof(Claims.TextUnitId), OrderByType.Asc)]
    public class Claims
    {
        [SugarColumn(IsPrimaryKey = true)]
        public string Id { get; set; } = string.Empty;

        public string Index { get; set; } = string.Empty;

        /// <summary>主体（执行动作的一方）</summary>
        [SugarColumn(ColumnDataType = "varchar(512)")]
        public string? SubjectId { get; set; }

        /// <summary>客体（受影响的一方）</summary>
        [SugarColumn(ColumnDataType = "varchar(512)")]
        public string? ObjectId { get; set; }

        /// <summary>声明类型</summary>
        [SugarColumn(ColumnDataType = "varchar(256)")]
        public string? Type { get; set; }

        /// <summary>TRUE / FALSE / SUSPECTED</summary>
        [SugarColumn(ColumnDataType = "varchar(32)")]
        public string? Status { get; set; }

        [SugarColumn(ColumnDataType = "text")]
        public string? Description { get; set; }

        [SugarColumn(ColumnDataType = "varchar(64)")]
        public string? StartDate { get; set; }

        [SugarColumn(ColumnDataType = "varchar(64)")]
        public string? EndDate { get; set; }

        [SugarColumn(ColumnDataType = "text")]
        public string? SourceText { get; set; }

        [SugarColumn(ColumnDataType = "varchar(128)")]
        public string? TextUnitId { get; set; }
    }
}
