using System.Text;
using GraphRag.Net.Domain.Interface;
using GraphRag.Net.Domain.Model.Graph;
using GraphRag.Net.Options;
using GraphRag.Net.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.Memory;
using Microsoft.SemanticKernel.Text;

namespace GraphRag.Net.Domain.Service
{
    [ServiceDescription(typeof(IGraphIndexService), ServiceLifetime.Scoped)]
    public class GraphIndexService(
        IGraphService _graphService,
        ISemanticService _semantic,
        ITextUnits_Repositories _textUnits,
        IClaims_Repositories _claims,
        INodes_Repositories _nodes,
        IEdges_Repositories _edges,
        ICommunities_Repositories _communities,
        IGraphStatsService _graphStats
        ) : IGraphIndexService
    {
        // ------------------------------------------------------------------
        // P1-5 Claims / Covariates
        // ------------------------------------------------------------------

        public async Task<ClaimExtractionSummary> ExtractClaimsAsync(string index, bool force = false)
        {
            if (string.IsNullOrWhiteSpace(index))
                throw new ArgumentException("Index required value cannot be null.");

            var units = _textUnits.GetList(p => p.Index == index);
            var processed = new HashSet<string>();
            if (!force)
            {
                processed = _claims.GetList(p => p.Index == index)
                    .Where(c => c.TextUnitId != null)
                    .Select(c => c.TextUnitId!)
                    .ToHashSet();
            }
            else
            {
                _claims.Delete(p => p.Index == index);
            }

            var summary = new ClaimExtractionSummary { TextUnitsProcessed = units.Count };
            foreach (var unit in units)
            {
                if (processed.Contains(unit.Id))
                {
                    summary.Skipped++;
                    continue;
                }

                ClaimExtractionResult? result = null;
                try
                {
                    result = await _semantic.ExtractClaimsAsync(
                        unit.Text ?? string.Empty, GraphQueryOption.ClaimMaxPerTextUnit);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Claims 抽取失败，跳过该文本单元：{ex.Message}");
                }
                var claims = result?.Claims ?? new List<ClaimModel>();
                foreach (var c in claims.Take(Math.Max(1, GraphQueryOption.ClaimMaxPerTextUnit)))
                {
                    _claims.Insert(new Claims
                    {
                        Id = Guid.NewGuid().ToString(),
                        Index = index,
                        SubjectId = c.SubjectId,
                        ObjectId = c.ObjectId,
                        Type = c.Type,
                        Status = string.IsNullOrWhiteSpace(c.Status) ? "SUSPECTED" : c.Status,
                        Description = c.Description,
                        StartDate = c.StartDate,
                        EndDate = c.EndDate,
                        SourceText = c.SourceText,
                        TextUnitId = unit.Id
                    });
                    summary.ClaimsExtracted++;
                }
            }
            return summary;
        }

        public Task<List<Claims>> GetClaimsAsync(string index)
        {
            if (string.IsNullOrWhiteSpace(index))
                throw new ArgumentException("Index required value cannot be null.");
            return Task.FromResult(_claims.GetList(p => p.Index == index));
        }

        // ------------------------------------------------------------------
        // P1-7 增量索引
        // ------------------------------------------------------------------

        public async Task<IncrementalIndexResult> IncrementalIndexAsync(
            string index, string input, string? documentId = null)
        {
            if (string.IsNullOrWhiteSpace(index) || string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("Values required for index and input cannot be null.");

            documentId ??= "default";
            var result = new IncrementalIndexResult { DocumentId = documentId };

            if (!GraphQueryOption.IncrementalEnabled)
            {
                // 关闭变更检测时退化为常规全量导入
                await _graphService.InsertTextChunkAsync(index, input);
                await _graphService.GraphCommunitiesAsync(index);
                await _graphService.GraphGlobalAsync(index);
                result.TotalChunks = 1;
                result.Added = 1;
                result.CommunitiesRebuilt = true;
                return result;
            }

            var chunks = SplitIntoChunks(input);
            result.TotalChunks = chunks.Count;

            var newByHash = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var chunk in chunks)
                newByHash[GraphService.ComputeHash(chunk)] = chunk;

            var existing = _textUnits.GetList(p => p.Index == index && p.DocumentId == documentId);
            var existingByHash = existing
                .Where(u => !string.IsNullOrWhiteSpace(u.ContentHash))
                .GroupBy(u => u.ContentHash!)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            // 未变块：直接跳过
            foreach (var hash in newByHash.Keys.Intersect(existingByHash.Keys, StringComparer.Ordinal))
                result.Unchanged++;

            // 失效块：移除文本单元、向量与节点溯源引用
            SemanticTextMemory? memory = null;
            try { memory = await _semantic.GetTextMemory(); }
            catch (Exception ex) { Console.WriteLine($"增量索引获取向量存储失败：{ex.Message}"); }

            var removedIds = new HashSet<string>();
            foreach (var old in existing)
            {
                if (old.ContentHash != null && newByHash.ContainsKey(old.ContentHash)) continue;
                removedIds.Add(old.Id);
                if (memory is not null)
                {
                    try { await memory.RemoveAsync(GraphCollections.TextUnits(index), old.Id); }
                    catch (Exception ex) { Console.WriteLine($"移除旧文本单元向量失败：{ex.Message}"); }
                }
            }
            if (removedIds.Count > 0)
            {
                _textUnits.Delete(p => p.Index == index && removedIds.Contains(p.Id));
                _claims.Delete(p => p.Index == index && p.TextUnitId != null && removedIds.Contains(p.TextUnitId));
                foreach (var node in _nodes.GetList(p => p.Index == index))
                {
                    if (string.IsNullOrWhiteSpace(node.TextUnitIds)) continue;
                    var kept = node.TextUnitIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Where(id => !removedIds.Contains(id)).ToList();
                    if (kept.Count == node.TextUnitIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Length) continue;
                    node.TextUnitIds = string.Join(",", kept);
                    _nodes.Update(node);
                }
                result.Removed = removedIds.Count;
            }

            // 新增块：只抽取变化部分
            foreach (var (hash, chunk) in newByHash)
            {
                if (existingByHash.ContainsKey(hash)) continue;
                await _graphService.InsertGraphDataWithDocumentAsync(index, chunk, documentId);
                result.Added++;
            }

            // 重建社区与全局摘要（层级社区 + 图统计）
            if (result.Added > 0 || result.Removed > 0)
            {
                await _graphService.GraphCommunitiesAsync(index);
                await _graphService.GraphGlobalAsync(index);
                result.CommunitiesRebuilt = true;
            }
            else
            {
                await _graphStats.RecomputeAsync(index);
            }

            if (GraphQueryOption.EnableClaimExtraction)
                await ExtractClaimsAsync(index);

            return result;
        }

        /// <summary>与 InsertTextChunkAsync 一致的重叠窗口切块（3 段落/块，1 段落重叠）。</summary>
        private static List<string> SplitIntoChunks(string input)
        {
            var lines = TextChunker.SplitPlainTextLines(input, TextChunkerOption.LinesToken);
            var paragraphs = TextChunker.SplitPlainTextParagraphs(lines, TextChunkerOption.ParagraphsToken);
            const int maxChunkSize = 3;
            const int overlapSize = 1;
            var chunks = new List<string>();
            if (paragraphs.Count <= maxChunkSize)
            {
                if (paragraphs.Count > 0) chunks.Add(string.Join("\n\n", paragraphs));
            }
            else
            {
                for (int i = 0; i < paragraphs.Count; i += (maxChunkSize - overlapSize))
                {
                    var part = paragraphs.Skip(i).Take(maxChunkSize).ToList();
                    if (part.Count > 0)
                    {
                        var chunk = string.Join("\n\n", part);
                        if (!chunks.Contains(chunk)) chunks.Add(chunk);
                    }
                    if (i + maxChunkSize >= paragraphs.Count) break;
                }
            }
            return chunks;
        }

        // ------------------------------------------------------------------
        // P1-8 自动提示词调优
        // ------------------------------------------------------------------

        public async Task<PromptTuneResult> AutoTunePromptsAsync(string index, string? domain = null)
        {
            if (string.IsNullOrWhiteSpace(index))
                throw new ArgumentException("Index required value cannot be null.");

            var units = _textUnits.GetList(p => p.Index == index)
                .OrderBy(u => u.Id)
                .Take(Math.Max(1, GraphQueryOption.PromptTuneSampleSize))
                .ToList();
            var sample = string.Join(Environment.NewLine + Environment.NewLine,
                units.Select(u => u.Text ?? string.Empty));
            if (sample.Length > 12000) sample = sample[..12000];

            PromptTuneOutput? tuned = null;
            try
            {
                tuned = await _semantic.TunePromptsAsync(
                    sample, domain ?? "通用领域", Math.Max(1, GraphQueryOption.PromptTuneEntityTypes));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"提示词调优失败：{ex.Message}");
            }
            var result = new PromptTuneResult { SampleText = sample };

            if (tuned is null) return result;

            result.Domain = tuned.Domain;
            result.EntityTypes = tuned.EntityTypes ?? new List<string>();

            var overrides = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(tuned.CreatePrompt))
            {
                var p = EnsurePlaceholders(tuned.CreatePrompt, "input");
                PromptOverrides.Set("graph", "create", p);
                overrides["create"] = p;
            }
            if (!string.IsNullOrWhiteSpace(tuned.CommunityReportPrompt))
            {
                var p = EnsurePlaceholders(tuned.CommunityReportPrompt, "context");
                PromptOverrides.Set("graph", "community_report", p);
                overrides["community_report"] = p;
            }
            if (!string.IsNullOrWhiteSpace(tuned.SummarizePrompt))
            {
                var p = EnsurePlaceholders(tuned.SummarizePrompt, "desc1", "desc2");
                PromptOverrides.Set("graph", "mergedesc", p);
                overrides["mergedesc"] = p;
            }
            result.Overrides = overrides;
            return result;
        }

        /// <summary>调优后的提示词必须保留原占位符，否则输入无法注入。</summary>
        private static string EnsurePlaceholders(string prompt, params string[] placeholders)
        {
            var sb = new StringBuilder(prompt);
            foreach (var p in placeholders)
            {
                if (prompt.Contains("{{$" + p + "}}", StringComparison.Ordinal) ||
                    prompt.Contains("{{" + p + "}}", StringComparison.Ordinal)) continue;
                sb.AppendLine().AppendLine($"[{p}]").AppendLine("{{$" + p + "}}");
            }
            return sb.ToString();
        }

        public IReadOnlyDictionary<string, string> GetPromptOverrides() => PromptOverrides.Snapshot();

        public void ClearPromptOverrides() => PromptOverrides.Clear();

        // ------------------------------------------------------------------
        // 统计
        // ------------------------------------------------------------------

        public Task<IndexStats> GetIndexStatsAsync(string index)
        {
            if (string.IsNullOrWhiteSpace(index))
                throw new ArgumentException("Index required value cannot be null.");

            var nodes = _nodes.GetList(p => p.Index == index);
            var edges = _edges.GetList(p => p.Index == index);
            var communities = _communities.GetList(p => p.Index == index);
            return Task.FromResult(new IndexStats
            {
                Index = index,
                Nodes = nodes.Count,
                Edges = edges.Count,
                TextUnits = _textUnits.GetList(p => p.Index == index).Count,
                Communities = communities.Count,
                CommunityLevels = communities.Count == 0 ? 0 : communities.Max(c => c.Level) + 1,
                Claims = _claims.GetList(p => p.Index == index).Count,
                MaxDegree = nodes.Count == 0 ? 0 : nodes.Max(n => n.Degree),
                TotalEdgeWeight = edges.Sum(e => Math.Max(1, e.Weight))
            });
        }
    }
}
