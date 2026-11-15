using Newtonsoft.Json;

namespace GraphRag.Net.Domain.Model.Graph
{
    // ------------------------------------------------------------------
    // Local Search（混合上下文）
    // ------------------------------------------------------------------

    /// <summary>混合上下文的各部分统计，便于回归测试与调优观测。</summary>
    public class LocalContextStats
    {
        public int Entities { get; set; }
        public int Relationships { get; set; }
        public int TextUnits { get; set; }
        public int CommunityReports { get; set; }
        public int Claims { get; set; }
        public int EstimatedTokens { get; set; }
    }

    public class LocalSearchResult
    {
        public string Answer { get; set; } = string.Empty;
        public LocalContextStats Stats { get; set; } = new();
        public string Context { get; set; } = string.Empty;
        public List<string> MatchedEntityIds { get; set; } = new();
    }

    /// <summary>构建好的混合上下文（含渲染文本与统计）。</summary>
    public class BuiltLocalContext
    {
        public string Context { get; set; } = string.Empty;
        public string HistoryText { get; set; } = string.Empty;
        public LocalContextStats Stats { get; set; } = new();
        public List<string> MatchedEntityIds { get; set; } = new();
    }

    // ------------------------------------------------------------------
    // Claims / Covariates
    // ------------------------------------------------------------------

    /// <summary>extract_claims 提示词的单条输出。</summary>
    public class ClaimModel
    {
        [JsonProperty("subject_id")]
        public string SubjectId { get; set; } = string.Empty;

        [JsonProperty("object_id")]
        public string ObjectId { get; set; } = string.Empty;

        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;

        [JsonProperty("status")]
        public string Status { get; set; } = "SUSPECTED";

        [JsonProperty("description")]
        public string Description { get; set; } = string.Empty;

        [JsonProperty("start_date")]
        public string? StartDate { get; set; }

        [JsonProperty("end_date")]
        public string? EndDate { get; set; }

        [JsonProperty("source_text")]
        public string SourceText { get; set; } = string.Empty;
    }

    public class ClaimExtractionResult
    {
        [JsonProperty("claims")]
        public List<ClaimModel> Claims { get; set; } = new();
    }

    /// <summary>claim 抽取批次结果。</summary>
    public class ClaimExtractionSummary
    {
        public int TextUnitsProcessed { get; set; }
        public int ClaimsExtracted { get; set; }
        public int Skipped { get; set; }
    }

    // ------------------------------------------------------------------
    // 动态社区选择
    // ------------------------------------------------------------------

    /// <summary>community_rate 提示词输出。</summary>
    public class CommunityRateResult
    {
        [JsonProperty("score")]
        public int Score { get; set; }

        [JsonProperty("relevant")]
        public bool Relevant { get; set; }

        [JsonProperty("reason")]
        public string Reason { get; set; } = string.Empty;
    }

    // ------------------------------------------------------------------
    // DRIFT Search
    // ------------------------------------------------------------------

    /// <summary>DRIFT primer / follow-up 的结构化输出。</summary>
    public class DriftResponse
    {
        [JsonProperty("answer")]
        public string Answer { get; set; } = string.Empty;

        [JsonProperty("followups")]
        public List<string> Followups { get; set; } = new();

        [JsonProperty("confidence")]
        public double Confidence { get; set; } = 1.0;
    }

    /// <summary>DRIFT 层级树节点。</summary>
    public class DriftNode
    {
        public string Question { get; set; } = string.Empty;
        public string Answer { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public int Depth { get; set; }
        public List<DriftNode> Children { get; set; } = new();
    }

    public class DriftSearchResult
    {
        public string Answer { get; set; } = string.Empty;
        public DriftNode Hierarchy { get; set; } = new();
        public int FollowupCount { get; set; }
        public int LocalSearches { get; set; }
        public List<DriftNode> FlatNodes { get; set; } = new();
    }

    // ------------------------------------------------------------------
    // 增量索引
    // ------------------------------------------------------------------

    public class IncrementalIndexResult
    {
        public int TotalChunks { get; set; }
        public int Unchanged { get; set; }
        public int Added { get; set; }
        public int Removed { get; set; }
        public bool CommunitiesRebuilt { get; set; }
        public string? DocumentId { get; set; }
    }

    public class IndexStats
    {
        public string Index { get; set; } = string.Empty;
        public int Nodes { get; set; }
        public int Edges { get; set; }
        public int TextUnits { get; set; }
        public int Communities { get; set; }
        public int CommunityLevels { get; set; }
        public int Claims { get; set; }
        public int MaxDegree { get; set; }
        public double TotalEdgeWeight { get; set; }
    }

    // ------------------------------------------------------------------
    // Prompt Tune
    // ------------------------------------------------------------------

    public class PromptTuneResult
    {
        public string Domain { get; set; } = string.Empty;
        public List<string> EntityTypes { get; set; } = new();
        public string SampleText { get; set; } = string.Empty;
        public Dictionary<string, string> Overrides { get; set; } = new();
    }

    public class PromptTuneOutput
    {
        [JsonProperty("domain")]
        public string Domain { get; set; } = string.Empty;

        [JsonProperty("entity_types")]
        public List<string> EntityTypes { get; set; } = new();

        [JsonProperty("create_prompt")]
        public string CreatePrompt { get; set; } = string.Empty;

        [JsonProperty("community_report_prompt")]
        public string CommunityReportPrompt { get; set; } = string.Empty;

        [JsonProperty("summarize_prompt")]
        public string SummarizePrompt { get; set; } = string.Empty;
    }

    // ------------------------------------------------------------------
    // 层级社区
    // ------------------------------------------------------------------

    public class CommunityLevelInfo
    {
        public int Level { get; set; }
        public int CommunityCount { get; set; }
        public int NodeCount { get; set; }
    }

    /// <summary>层级社区中的一个簇。</summary>
    public class CommunityCluster
    {
        public string Id { get; set; } = string.Empty;
        public int Level { get; set; }
        public string? Parent { get; set; }
        public List<string> Children { get; set; } = new();
        public List<string> NodeIds { get; set; } = new();
        public int Size => NodeIds.Count;
    }

    public class CommunityLevel
    {
        public int Level { get; set; }
        public List<CommunityCluster> Clusters { get; set; } = new();
    }
}
