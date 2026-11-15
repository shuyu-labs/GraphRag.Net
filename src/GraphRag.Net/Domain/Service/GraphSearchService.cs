using System.Text;
using System.Text.RegularExpressions;
using GraphRag.Net.Domain.Interface;
using GraphRag.Net.Domain.Model.Graph;
using GraphRag.Net.Options;
using GraphRag.Net.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Memory;

namespace GraphRag.Net.Domain.Service
{
    /// <summary>
    /// 对齐 microsoft/graphrag Query Engine 的检索服务：
    /// 1) Basic Search —— 原始文本单元的向量 RAG（可作基线对照与兜底）；
    /// 2) Global Search —— 动态社区选择 + map-reduce（对应 global_search.md）；
    /// 3) Question Generation —— 生成候选追问（对应 question_generation.md）。
    /// </summary>
    [ServiceDescription(typeof(IGraphSearchService), ServiceLifetime.Scoped)]
    public class GraphSearchService(
        ISemanticService _semanticService,
        ITextUnits_Repositories _textUnits_Repositories,
        ICommunities_Repositories _communities_Repositories,
        IGlobals_Repositories _globals_Repositories,
        IDynamicCommunitySelectionService _dynamicSelection
        ) : IGraphSearchService
    {
        // ------------------------------------------------------------------
        // Basic Search
        // ------------------------------------------------------------------

        public async Task<BasicSearchResult> SearchBasicAsync(string index, string input, int? topK = null)
        {
            EnsureQuery(index, input);
            var sources = await RetrieveTextUnitsAsync(index, input, Math.Max(1, topK ?? GraphQueryOption.BasicSearchTopK));
            if (sources.Count == 0)
            {
                return new BasicSearchResult
                {
                    Answer = "未在索引中找到可用于回答的文本单元，请先导入文档。",
                    TotalCandidates = 0
                };
            }

            var answer = await _semanticService.GetBasicAnswerAsync(
                FormatSources(sources), input, GraphQueryOption.ResponseType);
            return new BasicSearchResult
            {
                Answer = answer,
                Sources = sources,
                TotalCandidates = sources.Count
            };
        }

        public async IAsyncEnumerable<StreamingKernelContent> SearchBasicStreamAsync(string index, string input, int? topK = null)
        {
            EnsureQuery(index, input);
            var sources = await RetrieveTextUnitsAsync(index, input, Math.Max(1, topK ?? GraphQueryOption.BasicSearchTopK));
            if (sources.Count == 0)
            {
                yield return new StreamingKernelContent("未在索引中找到可用于回答的文本单元，请先导入文档。");
                yield break;
            }

            var stream = _semanticService.GetBasicAnswerStreamAsync(
                FormatSources(sources), input, GraphQueryOption.ResponseType);
            await foreach (var content in stream)
            {
                yield return content;
            }
        }

        /// <summary>向量检索文本单元；结果不足时降阈值重试，集合为空时回退到数据库直接取块。</summary>
        private async Task<List<TextUnitSource>> RetrieveTextUnitsAsync(string index, string input, int topK)
        {
            var hits = new List<TextUnitSource>();
            var collection = GraphCollections.TextUnits(index);
            try
            {
                var memory = await _semanticService.GetTextMemory();
                await foreach (var m in memory.SearchAsync(
                    collection, input, limit: topK, minRelevanceScore: GraphQueryOption.BasicSearchMinRelevance))
                {
                    hits.Add(new TextUnitSource { Id = m.Metadata.Id, Text = m.Metadata.Text, Relevance = m.Relevance });
                }

                if (hits.Count == 0)
                {
                    await foreach (var m in memory.SearchAsync(collection, input, limit: topK, minRelevanceScore: 0.0))
                    {
                        hits.Add(new TextUnitSource { Id = m.Metadata.Id, Text = m.Metadata.Text, Relevance = m.Relevance });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"文本单元向量检索失败，回退数据库检索：{ex.Message}");
            }

            if (hits.Count == 0)
            {
                // 兼容尚未生成文本单元向量的旧索引
                foreach (var row in _textUnits_Repositories.GetList(p => p.Index == index).Take(topK))
                {
                    hits.Add(new TextUnitSource { Id = row.Id, Text = row.Text ?? string.Empty, Relevance = 0 });
                }
            }

            return hits
                .Where(h => !string.IsNullOrWhiteSpace(h.Text))
                .Take(topK)
                .ToList();
        }

        private static string FormatSources(IEnumerable<TextUnitSource> sources)
        {
            var sb = new StringBuilder();
            sb.AppendLine("--- 原始文本单元数据表 ---");
            foreach (var s in sources)
            {
                sb.AppendLine($"----- 文本单元 (id: {s.Id}) -----");
                sb.AppendLine(s.Text);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Global Search（Map-Reduce）
        // ------------------------------------------------------------------

        public async Task<GlobalSearchResult> SearchGlobalAsync(
            string index, string input, string? responseType = null, bool? dynamicSelection = null)
        {
            var prepared = await PrepareGlobalAsync(index, input, dynamicSelection);
            if (prepared.Error is not null)
                return prepared.Error;

            var reduceData = FormatPoints(prepared.RankedPoints!);
            var answer = await _semanticService.GetGlobalReduceAsync(
                reduceData, input, responseType ?? GraphQueryOption.ResponseType, GraphQueryOption.ReduceMaxLength);
            prepared.Result!.Answer = answer;
            return prepared.Result!;
        }

        public async IAsyncEnumerable<StreamingKernelContent> SearchGlobalStreamAsync(
            string index, string input, string? responseType = null, bool? dynamicSelection = null)
        {
            var prepared = await PrepareGlobalAsync(index, input, dynamicSelection);
            if (prepared.Error is not null)
            {
                yield return new StreamingKernelContent(prepared.Error.Answer);
                yield break;
            }

            var reduceData = FormatPoints(prepared.RankedPoints!);
            var stream = _semanticService.GetGlobalReduceStreamAsync(
                reduceData, input, responseType ?? GraphQueryOption.ResponseType, GraphQueryOption.ReduceMaxLength);
            await foreach (var content in stream)
            {
                yield return content;
            }
        }

        private sealed class GlobalPreparation
        {
            public GlobalSearchResult? Error { get; init; }
            public GlobalSearchResult? Result { get; init; }
            public List<KeyPoint>? RankedPoints { get; init; }
        }

        /// <summary>动态社区选择 → 分批 → map → 排序/裁剪，供单次与流式 reduce 复用。</summary>
        private async Task<GlobalPreparation> PrepareGlobalAsync(string index, string input, bool? dynamicSelection)
        {
            EnsureQuery(index, input);

            var allReports = LoadCommunityReports(index);
            var all = GraphQueryOption.GlobalCommunityLevel >= 0
                ? allReports.Where(c => c.Level == GraphQueryOption.GlobalCommunityLevel).ToList()
                : allReports;
            if (all.Count == 0) all = allReports; // 指定层级无报告时回退全部
            if (all.Count == 0)
            {
                return new GlobalPreparation
                {
                    Error = new GlobalSearchResult
                    {
                        Answer = "该索引尚未生成社区报告，请先调用 GraphCommunities / GraphGlobal 完成索引。",
                        TotalCommunityReports = 0
                    }
                };
            }

            var selected = await SelectCommunitiesAsync(
                index, input, all, dynamicSelection ?? GraphQueryOption.DynamicCommunitySelection);
            var batches = BatchReports(selected, Math.Max(500, GraphQueryOption.MapBatchTokens));
            var points = await MapAsync(batches, input);
            var ranked = RankAndTrim(points);

            var result = new GlobalSearchResult
            {
                Points = points,
                SelectedCommunityIds = selected.Select(s => s.Id).ToList(),
                TotalCommunityReports = all.Count,
                MapBatches = batches.Count
            };

            if (ranked.Count == 0)
            {
                result.Answer = "抱歉，基于现有社区报告无法回答该问题（所有关键点的重要性评分均为 0）。";
                return new GlobalPreparation { Error = result };
            }

            return new GlobalPreparation { Result = result, RankedPoints = ranked };
        }

        /// <summary>
        /// 动态社区选择：委托 IDynamicCommunitySelectionService
        /// （LLM 评分 + keep_parent + 层级回退）；关闭时按评分取前 N。
        /// </summary>
        private async Task<List<CommunityReportData>> SelectCommunitiesAsync(
            string index, string input, List<CommunityReportData> all, bool dynamicSelection)
        {
            int max = Math.Max(1, GraphQueryOption.MaxCommunityReports);
            if (!dynamicSelection)
                return all.OrderByDescending(c => c.Rating).ThenByDescending(c => c.Rank).Take(max).ToList();

            var selected = await _dynamicSelection.SelectAsync(index, input, max);
            var byId = all.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
            var mapped = selected
                .Where(c => byId.ContainsKey(c.CommunitieId))
                .Select(c => byId[c.CommunitieId])
                .ToList();
            return mapped.Count > 0
                ? mapped
                : all.OrderByDescending(c => c.Rating).ThenByDescending(c => c.Rank).Take(max).ToList();
        }

        /// <summary>按 token 预算把社区报告切分为 map 批次。</summary>
        private static List<List<CommunityReportData>> BatchReports(List<CommunityReportData> reports, int maxTokens)
        {
            var batches = new List<List<CommunityReportData>>();
            var current = new List<CommunityReportData>();
            int tokens = 0;
            foreach (var r in reports)
            {
                int cost = EstimateTokens(r.Content) + 20;
                if (current.Count > 0 && tokens + cost > maxTokens)
                {
                    batches.Add(current);
                    current = new List<CommunityReportData>();
                    tokens = 0;
                }
                current.Add(r);
                tokens += cost;
            }
            if (current.Count > 0) batches.Add(current);
            return batches;
        }

        private static string FormatReportBatch(IEnumerable<CommunityReportData> reports)
        {
            var sb = new StringBuilder();
            sb.AppendLine("--- 社区报告数据表 ---");
            int i = 1;
            foreach (var r in reports)
            {
                sb.AppendLine($"----- 报告 (id: {r.Id}, 序号: {i++}) -----");
                sb.AppendLine(r.Content);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        /// <summary>map 阶段：并发调用 LLM，为每个批次产出带评分的关键点。</summary>
        private async Task<List<KeyPoint>> MapAsync(IReadOnlyList<List<CommunityReportData>> batches, string input)
        {
            using var throttler = new SemaphoreSlim(Math.Max(1, GraphQueryOption.ConcurrentCoroutines));
            var tasks = batches.Select(async batch =>
            {
                await throttler.WaitAsync().ConfigureAwait(false);
                try
                {
                    var result = await _semanticService.GetGlobalMapAsync(
                        FormatReportBatch(batch), input, GraphQueryOption.MapMaxLength);
                    return result?.Points ?? new List<KeyPoint>();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Global map 批次失败：{ex.Message}");
                    return new List<KeyPoint>();
                }
                finally
                {
                    throttler.Release();
                }
            });

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results
                .SelectMany(p => p)
                .Where(p => !string.IsNullOrWhiteSpace(p.Description))
                .ToList();
        }

        /// <summary>过滤低分点、按重要性降序、按 reduce token 预算裁剪。</summary>
        private static List<KeyPoint> RankAndTrim(List<KeyPoint> points)
        {
            var result = new List<KeyPoint>();
            int tokens = 0;
            foreach (var p in points
                .Where(p => p.Score >= GraphQueryOption.MinPointScore)
                .OrderByDescending(p => p.Score))
            {
                int cost = EstimateTokens(p.Description) + 12;
                if (tokens + cost > GraphQueryOption.MaxReduceTokens) break;
                result.Add(p);
                tokens += cost;
            }
            return result;
        }

        private static string FormatPoints(IReadOnlyList<KeyPoint> points)
        {
            var sb = new StringBuilder();
            sb.AppendLine("以下分析师要点按重要性降序排列：");
            int i = 1;
            foreach (var p in points)
            {
                sb.AppendLine($"----- 要点 {i++} (重要性 {p.Score}/100) -----");
                sb.AppendLine(p.Description);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private sealed class CommunityReportData
        {
            public string Id { get; init; } = string.Empty;
            public string Content { get; init; } = string.Empty;
            public double Rating { get; init; }
            public double Rank { get; init; }
            public int Size { get; init; }
            public int Level { get; init; }
        }

        private List<CommunityReportData> LoadCommunityReports(string index)
            => _communities_Repositories.GetList(p => p.Index == index)
                .Select(c => new CommunityReportData
                {
                    Id = c.CommunitieId,
                    Content = string.IsNullOrWhiteSpace(c.FullContent) ? (c.Summaries ?? string.Empty) : c.FullContent!,
                    Rating = c.Rating,
                    Rank = c.CommunityRank,
                    Size = c.Size,
                    Level = c.Level
                })
                .Where(c => !string.IsNullOrWhiteSpace(c.Content))
                .ToList();

        // ------------------------------------------------------------------
        // Question Generation
        // ------------------------------------------------------------------

        public async Task<QuestionGenerationResult> GenerateQuestionsAsync(
            string index, IReadOnlyList<string>? history = null, int? count = null)
        {
            if (string.IsNullOrWhiteSpace(index))
                throw new ArgumentException("Index required value cannot be null.");

            int n = Math.Max(1, count ?? GraphQueryOption.QuestionCount);
            var context = BuildQuestionContext(index);
            var historyText = history is null || history.Count == 0
                ? "（暂无历史问题，请直接围绕数据中最重要的主题生成）"
                : string.Join(Environment.NewLine, history.Select((h, i) => $"{i + 1}. {h}"));

            var raw = await _semanticService.GenerateQuestionsAsync(context, historyText, n);
            return new QuestionGenerationResult
            {
                Questions = ParseBullets(raw).Take(n).ToList(),
                Context = context
            };
        }

        private string BuildQuestionContext(string index)
        {
            var sb = new StringBuilder();
            var global = _globals_Repositories.GetFirst(p => p.Index == index)?.Summaries;
            if (!string.IsNullOrWhiteSpace(global))
            {
                sb.AppendLine("--- 全局摘要 ---");
                sb.AppendLine(global);
            }
            foreach (var r in LoadCommunityReports(index).OrderByDescending(r => r.Rating).Take(10))
            {
                sb.AppendLine($"--- 社区报告 {r.Id} ---");
                sb.AppendLine(r.Content.Length > 1200 ? r.Content[..1200] : r.Content);
            }
            if (sb.Length == 0) sb.AppendLine("（暂无社区报告，请基于索引主题生成通用问题）");
            var text = sb.ToString();
            return text.Length > 12000 ? text[..12000] : text;
        }

        private static List<string> ParseBullets(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
            var result = new List<string>();
            foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
            {
                var s = line.Trim().TrimStart('•').Trim();
                if (s.StartsWith('-') || s.StartsWith('*')) s = s[1..].Trim();
                s = Regex.Replace(s, @"^\d+[\.\)、]\s*", string.Empty);
                s = s.Trim().Trim('"', '“', '”');
                if (s.Length >= 4) result.Add(s);
            }
            return result.Distinct().ToList();
        }

        // ------------------------------------------------------------------
        // 检视接口
        // ------------------------------------------------------------------

        public Task<List<TextUnits>> GetTextUnitsAsync(string index)
        {
            if (string.IsNullOrWhiteSpace(index))
                throw new ArgumentException("Index required value cannot be null.");
            return Task.FromResult(_textUnits_Repositories.GetList(p => p.Index == index));
        }

        public Task<List<CommunityReportInfo>> GetCommunityReportsAsync(string index)
        {
            if (string.IsNullOrWhiteSpace(index))
                throw new ArgumentException("Index required value cannot be null.");
            var rows = _communities_Repositories.GetList(p => p.Index == index)
                .Select(c => new CommunityReportInfo
                {
                    CommunityId = c.CommunitieId,
                    Title = c.Title,
                    Summary = c.Summaries,
                    Rating = c.Rating,
                    RatingExplanation = c.RatingExplanation,
                    Size = c.Size,
                    Level = c.Level,
                    Findings = c.Findings
                }).ToList();
            return Task.FromResult(rows);
        }

        // ------------------------------------------------------------------
        // 工具
        // ------------------------------------------------------------------

        private static void EnsureQuery(string index, string input)
        {
            if (string.IsNullOrWhiteSpace(index) || string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("Values required for index and input cannot be null.");
        }

        /// <summary>粗略 token 估算：汉字按 1、其它字符按 0.75。</summary>
        private static int EstimateTokens(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int chinese = text.Count(c => c >= 0x4E00 && c <= 0x9FFF);
            return chinese + (int)((text.Length - chinese) * 0.75);
        }
    }
}
