namespace GraphRag.Net.Options
{
    /// <summary>
    /// 对齐 microsoft/graphrag 新增的检索能力配置项。
    /// 与项目既有 *Option 一致，采用静态属性；Program 启动时通过
    /// ConfigurationBinder.GetValue 显式绑定 "GraphQuery" 配置节。
    /// </summary>
    public class GraphQueryOption
    {
        // ---- Basic Search（基础向量 RAG） ----
        /// <summary>基础检索取回的文本单元数量。</summary>
        public static int BasicSearchTopK { get; set; } = 5;

        /// <summary>基础检索向量最小相关度；结果为空时会自动降低阈值重试。</summary>
        public static double BasicSearchMinRelevance { get; set; } = 0.3;

        // ---- Global Search（Map-Reduce 全局检索） ----
        /// <summary>是否按问题语义动态挑选社区报告（dynamic community selection）。</summary>
        public static bool DynamicCommunitySelection { get; set; } = true;

        /// <summary>参与全局检索的社区报告上限；关闭动态选择时按影响力评分取前 N 个。</summary>
        public static int MaxCommunityReports { get; set; } = 20;

        /// <summary>map 阶段每个批次的预估 token 预算。</summary>
        public static int MapBatchTokens { get; set; } = 6000;

        /// <summary>map 阶段单批次答案的字数上限。</summary>
        public static int MapMaxLength { get; set; } = 800;

        /// <summary>reduce 阶段最终答案的字数上限。</summary>
        public static int ReduceMaxLength { get; set; } = 1200;

        /// <summary>reduce 阶段输入的预估 token 预算。</summary>
        public static int MaxReduceTokens { get; set; } = 12000;

        /// <summary>关键点保留的最低重要性评分（0-100），低于该值的点被过滤。</summary>
        public static int MinPointScore { get; set; } = 1;

        /// <summary>map 阶段并发批次数量。</summary>
        public static int ConcurrentCoroutines { get; set; } = 4;

        // ---- Question Generation（候选问题生成） ----
        /// <summary>生成的候选追问数量。</summary>
        public static int QuestionCount { get; set; } = 5;

        // ---- 结构化社区报告 ----
        /// <summary>单个社区报告最多保留的关键发现条数。</summary>
        public static int MaxReportFindings { get; set; } = 8;

        /// <summary>默认回答长度与格式描述（response_type）。</summary>
        public static string ResponseType { get; set; } = "多段落、结构清晰的中文回答";
    }
}
