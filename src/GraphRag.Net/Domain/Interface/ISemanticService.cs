using GraphRag.Net.Domain.Model.Graph;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Memory;

namespace GraphRag.Net.Domain.Interface
{
    public interface ISemanticService
    {
        Task<GraphModel> CreateGraphAsync(string input);

        Task<string> GetGraphAnswerAsync(string graph, string input);
        IAsyncEnumerable<StreamingKernelContent> GetGraphAnswerStreamAsync(string graph, string input);
        Task<string> GetGraphCommunityAnswerAsync(string graph, string community, string global, string input);

        IAsyncEnumerable<StreamingKernelContent> GetGraphCommunityAnswerStreamAsync(string graph, string community, string global, string input);
        Task<RelationShipModel> GetRelationship(string node1, string node2);
        Task<string> MergeDesc(string desc1, string desc2);
        Task<string> CommunitySummaries(string nodes);
        Task<string> GlobalSummaries(string community);
        Task<SemanticTextMemory> GetTextMemory();

        // ---- 对齐 microsoft/graphrag 新增能力 ----

        /// <summary>生成结构化社区报告（title/summary/rating/findings），失败返回 null。</summary>
        Task<CommunityReportModel?> CommunityReportAsync(string context);

        /// <summary>Basic Search：基于原始文本单元回答，单次/流式。</summary>
        Task<string> GetBasicAnswerAsync(string context, string input, string responseType);
        IAsyncEnumerable<StreamingKernelContent> GetBasicAnswerStreamAsync(string context, string input, string responseType);

        /// <summary>Global Search map 阶段：从一批社区报告中抽取带评分的关键点。</summary>
        Task<MapPointsResult?> GetGlobalMapAsync(string context, string input, int maxLength);

        /// <summary>Global Search reduce 阶段：汇总关键点生成最终答案，单次/流式。</summary>
        Task<string> GetGlobalReduceAsync(string reportData, string input, string responseType, int maxLength);
        IAsyncEnumerable<StreamingKernelContent> GetGlobalReduceStreamAsync(string reportData, string input, string responseType, int maxLength);

        /// <summary>Question Generation：基于上下文与历史问题生成候选追问（原始文本）。</summary>
        Task<string> GenerateQuestionsAsync(string context, string history, int count);

        // ---- P0 / P1 新增 ----

        /// <summary>Claims 抽取（extract_covariates）。</summary>
        Task<ClaimExtractionResult?> ExtractClaimsAsync(string context, int maxClaims);

        /// <summary>Local Search 混合上下文问答，单次/流式。</summary>
        Task<string> GetLocalAnswerAsync(string context, string input, string responseType, string history);
        IAsyncEnumerable<StreamingKernelContent> GetLocalAnswerStreamAsync(string context, string input, string responseType, string history);

        /// <summary>把子社区报告汇总为上一层社区报告（结构化）。</summary>
        Task<CommunityReportModel?> RollupCommunityReportsAsync(string childReports, int level);

        /// <summary>动态社区选择：LLM 对单个社区报告与问题的相关度打分。</summary>
        Task<CommunityRateResult?> RateCommunityAsync(string query, string report, string title);

        /// <summary>DRIFT primer：基于社区报告产出初步答案与后续问题。</summary>
        Task<DriftResponse?> GetDriftPrimerAsync(string query, string communityReports, int folds, int followups);

        /// <summary>DRIFT follow-up：基于局部答案产出细化答案与新的后续问题。</summary>
        Task<DriftResponse?> GetDriftFollowupAsync(string query, string localAnswer, int followups);

        /// <summary>DRIFT reduce：汇总层级问答树，单次/流式。</summary>
        Task<string> GetDriftReduceAsync(string hierarchy, string query, string responseType);
        IAsyncEnumerable<StreamingKernelContent> GetDriftReduceStreamAsync(string hierarchy, string query, string responseType);

        /// <summary>自动提示词调优：产出领域实体类型与三套调优提示词。</summary>
        Task<PromptTuneOutput?> TunePromptsAsync(string sample, string domain, int entityTypesCount);
    }
}
