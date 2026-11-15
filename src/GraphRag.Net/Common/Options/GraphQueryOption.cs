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

        // ---- P0-1 分层社区 ----
        /// <summary>最大社区层级数（含 level 0）。</summary>
        public static int CommunityMaxLevels { get; set; } = 3;

        /// <summary>层级递归停止阈值：上一层社区数不再减少时停止。</summary>
        public static int CommunityMinSize { get; set; } = 1;

        /// <summary>Global Search 默认使用的社区层级；-1 表示自动选择最粗层级。</summary>
        public static int GlobalCommunityLevel { get; set; } = -1;

        // ---- P0-3 图统计 ----
        /// <summary>是否维护 degree / frequency / weight / community rank。</summary>
        public static bool EnableGraphStats { get; set; } = true;

        // ---- P0-2 Local Search 混合上下文 ----
        /// <summary>上下文预算中原文文本单元的比例。</summary>
        public static double LocalTextUnitProp { get; set; } = 0.5;

        /// <summary>上下文预算中社区报告的比例。</summary>
        public static double LocalCommunityProp { get; set; } = 0.2;

        /// <summary>Local Search 上下文 token 预算。</summary>
        public static int LocalMaxContextTokens { get; set; } = 12000;

        /// <summary>Local Search 候选实体数上限。</summary>
        public static int LocalTopKEntities { get; set; } = 10;

        /// <summary>Local Search 候选关系数上限。</summary>
        public static int LocalTopKRelationships { get; set; } = 10;

        /// <summary>对话历史最大轮数。</summary>
        public static int LocalMaxConversationTurns { get; set; } = 5;

        // ---- P1-5 Claims ----
        /// <summary>导入时是否抽取 claims（默认关闭，与参考实现一致）。</summary>
        public static bool EnableClaimExtraction { get; set; } = false;

        /// <summary>单个文本单元最多抽取的 claim 数。</summary>
        public static int ClaimMaxPerTextUnit { get; set; } = 10;

        // ---- P1-6 动态社区选择 ----
        /// <summary>off / vector / llm。llm 为完整实现（评分 + 层级回退）。</summary>
        public static string DynamicSelectionMode { get; set; } = "llm";

        /// <summary>LLM 评分阈值（0-100），高于该值视为相关。</summary>
        public static int DynamicSearchThreshold { get; set; } = 50;

        /// <summary>子社区相关时是否保留父社区。</summary>
        public static bool DynamicKeepParent { get; set; } = true;

        /// <summary>对同一社区重复评分次数，取最高分。</summary>
        public static int DynamicNumRepeats { get; set; } = 1;

        /// <summary>无相关社区时向更粗层级回退的最大层级；-1 为不限制。</summary>
        public static int DynamicMaxLevel { get; set; } = -1;

        // ---- P1-4 DRIFT Search ----
        /// <summary>每个阶段展开的后续问题数。</summary>
        public static int DriftKFollowups { get; set; } = 3;

        /// <summary>Primer 阶段社区报告折数。</summary>
        public static int DriftPrimerFolds { get; set; } = 2;

        /// <summary>DRIFT 递归深度。</summary>
        public static int DriftNDepth { get; set; } = 2;

        /// <summary>DRIFT follow-up 并发度。</summary>
        public static int DriftConcurrency { get; set; } = 3;

        // ---- P1-7 增量索引 ----
        /// <summary>是否启用内容哈希变更检测。</summary>
        public static bool IncrementalEnabled { get; set; } = true;

        // ---- P1-8 Prompt Tune ----
        /// <summary>调优时采样的文本单元数量。</summary>
        public static int PromptTuneSampleSize { get; set; } = 5;

        /// <summary>生成的领域实体类型数量。</summary>
        public static int PromptTuneEntityTypes { get; set; } = 8;
    }
}
