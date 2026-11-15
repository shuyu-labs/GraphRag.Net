using Newtonsoft.Json;

namespace GraphRag.Net.Domain.Model.Graph
{
    /// <summary>全局检索 map 阶段产出的关键点。</summary>
    public class KeyPoint
    {
        [JsonProperty("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>重要性评分 0-100。</summary>
        [JsonProperty("score")]
        public int Score { get; set; }
    }

    /// <summary>global_map 提示词的 JSON 输出结构。</summary>
    public class MapPointsResult
    {
        [JsonProperty("points")]
        public List<KeyPoint> Points { get; set; } = new();
    }

    /// <summary>基础向量检索命中的原文片段。</summary>
    public class TextUnitSource
    {
        public string Id { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public double Relevance { get; set; }
    }

    /// <summary>Basic Search 结果：答案 + 可追溯的来源片段。</summary>
    public class BasicSearchResult
    {
        public string Answer { get; set; } = string.Empty;
        public List<TextUnitSource> Sources { get; set; } = new();
        public int TotalCandidates { get; set; }
    }

    /// <summary>Map-Reduce 全局检索结果。</summary>
    public class GlobalSearchResult
    {
        public string Answer { get; set; } = string.Empty;
        public List<KeyPoint> Points { get; set; } = new();
        public List<string> SelectedCommunityIds { get; set; } = new();
        public int TotalCommunityReports { get; set; }
        public int MapBatches { get; set; }
    }

    /// <summary>候选问题生成结果。</summary>
    public class QuestionGenerationResult
    {
        public List<string> Questions { get; set; } = new();
        public string Context { get; set; } = string.Empty;
    }

    /// <summary>结构化社区报告的对外视图（列表查询用）。</summary>
    public class CommunityReportInfo
    {
        public string CommunityId { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? Summary { get; set; }
        public double Rating { get; set; }
        public string? RatingExplanation { get; set; }
        public int Size { get; set; }
        public int Level { get; set; }
        /// <summary>关键发现 JSON 字符串</summary>
        public string? Findings { get; set; }
    }
}
