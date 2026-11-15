namespace GraphRag.Net.Domain.Model.Graph
{
    /// <summary>
    /// 向量集合命名。既有代码直接用 index 作为「节点描述」集合名，
    /// 这里为新增的文本单元 / 社区报告使用独立集合，避免相互污染。
    /// </summary>
    public static class GraphCollections
    {
        /// <summary>原始文本块集合（Basic Search 与溯源使用）。</summary>
        public static string TextUnits(string index) => index + "_textunit";

        /// <summary>社区报告集合（动态社区选择使用）。</summary>
        public static string Communities(string index) => index + "_community";
    }
}
