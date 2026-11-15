using System.Text;
using GraphRag.Net.Domain.Interface;
using GraphRag.Net.Domain.Model.Graph;
using GraphRag.Net.Options;
using GraphRag.Net.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

namespace GraphRag.Net.Domain.Service
{
    /// <summary>
    /// Local Search 混合上下文实现。候选集按「向量相关度 + degree/weight/rank」排序，
    /// 再按 text_unit_prop / community_prop / 剩余预算分区裁剪，最后交给 local_search 提示词。
    /// </summary>
    [ServiceDescription(typeof(ILocalSearchService), ServiceLifetime.Scoped)]
    public class LocalSearchService(
        ISemanticService _semantic,
        INodes_Repositories _nodes,
        IEdges_Repositories _edges,
        ITextUnits_Repositories _textUnits,
        ICommunities_Repositories _communities,
        ICommunitieNodes_Repositories _communityNodes,
        IClaims_Repositories _claims
        ) : ILocalSearchService
    {
        public async Task<LocalSearchResult> SearchLocalAsync(
            string index, string input, IReadOnlyList<string>? history = null)
        {
            var built = await BuildContextAsync(index, input, history);
            if (built.Stats.Entities == 0 && built.Stats.TextUnits == 0)
            {
                return new LocalSearchResult
                {
                    Answer = "未在索引中找到可用于回答的实体或文本单元，请先导入文档。",
                    Stats = built.Stats,
                    Context = built.Context
                };
            }

            var answer = await _semantic.GetLocalAnswerAsync(
                built.Context, input, GraphQueryOption.ResponseType, built.HistoryText);
            return new LocalSearchResult
            {
                Answer = answer,
                Stats = built.Stats,
                Context = built.Context,
                MatchedEntityIds = built.MatchedEntityIds
            };
        }

        public async IAsyncEnumerable<StreamingKernelContent> SearchLocalStreamAsync(
            string index, string input, IReadOnlyList<string>? history = null)
        {
            var built = await BuildContextAsync(index, input, history);
            if (built.Stats.Entities == 0 && built.Stats.TextUnits == 0)
            {
                yield return new StreamingKernelContent("未在索引中找到可用于回答的实体或文本单元，请先导入文档。");
                yield break;
            }

            var stream = _semantic.GetLocalAnswerStreamAsync(
                built.Context, input, GraphQueryOption.ResponseType, built.HistoryText);
            await foreach (var content in stream)
            {
                yield return content;
            }
        }

        public async Task<BuiltLocalContext> BuildContextAsync(
            string index, string input, IReadOnlyList<string>? history = null)
        {
            if (string.IsNullOrWhiteSpace(index) || string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("Values required for index and input cannot be null.");

            var historyText = FormatHistory(history);
            var stats = new LocalContextStats();
            var result = new BuiltLocalContext { HistoryText = historyText };

            // ---- 1. 实体检索（向量；离线/无向量时回退到全部节点） ----
            var relevance = await RetrieveEntitiesAsync(index, input);
            var allNodes = _nodes.GetList(p => p.Index == index);
            if (relevance.Count == 0)
            {
                foreach (var n in allNodes.OrderByDescending(n => n.Degree)
                    .Take(Math.Max(1, GraphQueryOption.LocalTopKEntities)))
                {
                    relevance[n.Id] = 0.0;
                }
            }
            var entityIds = relevance.Keys.ToHashSet();
            var nodeById = allNodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());

            var rankedEntities = relevance
                .Where(kv => nodeById.ContainsKey(kv.Key))
                .Select(kv => new { Node = nodeById[kv.Key], Score = kv.Value * 100 + nodeById[kv.Key].Degree })
                .OrderByDescending(x => x.Score)
                .Take(Math.Max(1, GraphQueryOption.LocalTopKEntities))
                .ToList();
            result.MatchedEntityIds = rankedEntities.Select(x => x.Node.Id).ToList();
            var selectedEntityIds = result.MatchedEntityIds.ToHashSet();

            // ---- 2. 关系候选（按 weight + 两端 degree 排序） ----
            var allEdges = _edges.GetList(p => p.Index == index);
            var relationships = allEdges
                .Where(e => selectedEntityIds.Contains(e.Source) || selectedEntityIds.Contains(e.Target))
                .Select(e => new
                {
                    Edge = e,
                    Score = Math.Max(1, e.Weight) * 10
                        + (nodeById.GetValueOrDefault(e.Source)?.Degree ?? 0)
                        + (nodeById.GetValueOrDefault(e.Target)?.Degree ?? 0)
                })
                .OrderByDescending(x => x.Score)
                .Take(Math.Max(1, GraphQueryOption.LocalTopKRelationships))
                .Select(x => x.Edge)
                .ToList();

            // ---- 3. 文本单元候选（按被命中实体引用次数排序） ----
            var unitRefs = new Dictionary<string, int>();
            foreach (var n in rankedEntities.Select(x => x.Node))
            {
                foreach (var tu in (n.TextUnitIds ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    unitRefs[tu] = unitRefs.GetValueOrDefault(tu) + 1;
                }
            }
            // 同时用向量检索补充语义相关但未被实体引用的文本块
            foreach (var id in await RetrieveTextUnitIdsAsync(index, input))
                unitRefs[id] = unitRefs.GetValueOrDefault(id) + 1;

            var allUnits = _textUnits.GetList(p => p.Index == index)
                .ToDictionary(u => u.Id, u => u);
            var rankedUnits = unitRefs
                .Where(kv => allUnits.ContainsKey(kv.Key))
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => allUnits[kv.Key])
                .ToList();

            // ---- 4. 社区报告候选（Level=0，按 CommunityRank 排序） ----
            var communityIds = _communityNodes.GetList(p => p.Index == index && p.Level == 0
                    && selectedEntityIds.Contains(p.NodeId))
                .Select(c => c.CommunitieId).ToHashSet();
            var reports = _communities.GetList(p => p.Index == index)
                .Where(c => communityIds.Contains(c.CommunitieId))
                .OrderByDescending(c => c.CommunityRank)
                .ToList();

            // ---- 5. Claims 候选（主体/客体命中 或 来源文本单元命中） ----
            var entityNames = rankedEntities.Select(x => x.Node.Name).ToHashSet();
            var claims = _claims.GetList(p => p.Index == index)
                .Where(c => (c.SubjectId != null && entityNames.Contains(c.SubjectId))
                    || (c.ObjectId != null && entityNames.Contains(c.ObjectId))
                    || (c.TextUnitId != null && unitRefs.ContainsKey(c.TextUnitId)))
                .Take(Math.Max(1, GraphQueryOption.LocalTopKEntities))
                .ToList();

            // ---- 6. 按预算裁剪并渲染 ----
            int total = Math.Max(500, GraphQueryOption.LocalMaxContextTokens);
            int textUnitBudget = (int)(total * GraphQueryOption.LocalTextUnitProp);
            int communityBudget = (int)(total * GraphQueryOption.LocalCommunityProp);
            int structuredBudget = total - textUnitBudget - communityBudget;

            var sb = new StringBuilder();
            int used = 0;

            sb.AppendLine("--- 实体 (Entities) ---");
            foreach (var x in rankedEntities)
            {
                var line = $"- [id: {x.Node.Id}] {x.Node.Name} | 类型: {x.Node.Type} | 度数: {x.Node.Degree} | 描述: {x.Node.Desc}";
                var cost = EstimateTokens(line);
                if (used + cost > structuredBudget) break;
                sb.AppendLine(line);
                used += cost;
                stats.Entities++;
            }

            sb.AppendLine();
            sb.AppendLine("--- 关系 (Relationships) ---");
            foreach (var e in relationships)
            {
                var s = nodeById.GetValueOrDefault(e.Source)?.Name ?? e.Source;
                var t = nodeById.GetValueOrDefault(e.Target)?.Name ?? e.Target;
                var line = $"- {s} -> {t} | 权重: {e.Weight:0.0} | 描述: {e.Relationship}";
                var cost = EstimateTokens(line);
                if (used + cost > structuredBudget) break;
                sb.AppendLine(line);
                used += cost;
                stats.Relationships++;
            }

            sb.AppendLine();
            sb.AppendLine("--- 声明 (Claims) ---");
            foreach (var c in claims)
            {
                var line = $"- {c.SubjectId} -> {c.ObjectId} | 类型: {c.Type} | 状态: {c.Status} | 描述: {c.Description}";
                var cost = EstimateTokens(line);
                if (used + cost > structuredBudget) break;
                sb.AppendLine(line);
                used += cost;
                stats.Claims++;
            }

            int unitUsed = 0;
            sb.AppendLine();
            sb.AppendLine("--- 文本单元 (Sources) ---");
            foreach (var u in rankedUnits)
            {
                var line = $"----- 文本单元 (id: {u.Id}) -----\n{u.Text}";
                var cost = EstimateTokens(line);
                if (unitUsed + cost > textUnitBudget) break;
                sb.AppendLine(line);
                unitUsed += cost;
                stats.TextUnits++;
            }
            used += unitUsed;

            int reportUsed = 0;
            sb.AppendLine();
            sb.AppendLine("--- 社区报告 (Reports) ---");
            foreach (var c in reports)
            {
                var content = string.IsNullOrWhiteSpace(c.FullContent) ? c.Summaries : c.FullContent!;
                var line = $"----- 报告 (id: {c.CommunitieId}, 层级 {c.Level}, 评分 {c.Rating:0.0}) -----\n{content}";
                var cost = EstimateTokens(line);
                if (reportUsed + cost > communityBudget) break;
                sb.AppendLine(line);
                reportUsed += cost;
                stats.CommunityReports++;
            }
            used += reportUsed;

            sb.AppendLine();
            sb.AppendLine("--- 对话历史 ---");
            sb.AppendLine(string.IsNullOrWhiteSpace(historyText) ? "（无）" : historyText);

            result.Context = sb.ToString();
            stats.EstimatedTokens = used + EstimateTokens(historyText);
            result.Stats = stats;
            return result;
        }

        // ------------------------------------------------------------------
        // 检索辅助
        // ------------------------------------------------------------------

        private async Task<Dictionary<string, double>> RetrieveEntitiesAsync(string index, string input)
        {
            var result = new Dictionary<string, double>();
            try
            {
                var memory = await _semantic.GetTextMemory();
                await foreach (var m in memory.SearchAsync(
                    index, input, limit: Math.Max(1, GraphQueryOption.LocalTopKEntities),
                    minRelevanceScore: GraphSearchOption.SearchMinRelevance))
                {
                    result[m.Metadata.Id] = m.Relevance;
                }
                if (result.Count == 0)
                {
                    await foreach (var m in memory.SearchAsync(
                        index, input, limit: Math.Max(1, GraphQueryOption.LocalTopKEntities),
                        minRelevanceScore: 0.0))
                    {
                        result[m.Metadata.Id] = m.Relevance;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Local 实体向量检索失败，回退图统计排序：{ex.Message}");
            }
            return result;
        }

        private async Task<List<string>> RetrieveTextUnitIdsAsync(string index, string input)
        {
            var ids = new List<string>();
            try
            {
                var memory = await _semantic.GetTextMemory();
                await foreach (var m in memory.SearchAsync(
                    GraphCollections.TextUnits(index), input, limit: GraphQueryOption.BasicSearchTopK,
                    minRelevanceScore: GraphQueryOption.BasicSearchMinRelevance))
                {
                    ids.Add(m.Metadata.Id);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Local 文本单元向量检索失败：{ex.Message}");
            }
            return ids;
        }

        private static string FormatHistory(IReadOnlyList<string>? history)
        {
            if (history is null || history.Count == 0) return string.Empty;
            var turns = history.TakeLast(Math.Max(1, GraphQueryOption.LocalMaxConversationTurns));
            return string.Join(Environment.NewLine, turns.Select((h, i) => $"{i + 1}. {h}"));
        }

        private static int EstimateTokens(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int chinese = text.Count(c => c >= 0x4E00 && c <= 0x9FFF);
            return chinese + (int)((text.Length - chinese) * 0.75);
        }
    }
}
