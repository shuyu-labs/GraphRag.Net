using Newtonsoft.Json;

namespace GraphRag.Net.Domain.Model.Graph
{
    /// <summary>
    /// 结构化社区报告，对齐 microsoft/graphrag 的 community_reports 产物：
    /// title / summary / rating / rating_explanation / findings。
    /// </summary>
    public class CommunityReportModel
    {
        [JsonProperty("title")]
        public string Title { get; set; } = string.Empty;

        [JsonProperty("summary")]
        public string Summary { get; set; } = string.Empty;

        /// <summary>影响力评分（0-10），用于社区排序与动态选择回退。</summary>
        [JsonProperty("rating")]
        public double Rating { get; set; }

        [JsonProperty("rating_explanation")]
        public string RatingExplanation { get; set; } = string.Empty;

        [JsonProperty("findings")]
        public List<CommunityFinding> Findings { get; set; } = new();
    }

    public class CommunityFinding
    {
        [JsonProperty("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonProperty("explanation")]
        public string Explanation { get; set; } = string.Empty;
    }
}
