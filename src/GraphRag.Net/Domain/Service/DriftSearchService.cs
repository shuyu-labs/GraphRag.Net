using System.Text;
using GraphRag.Net.Domain.Interface;
using GraphRag.Net.Domain.Model.Graph;
using GraphRag.Net.Options;
using GraphRag.Net.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

namespace GraphRag.Net.Domain.Service
{
    [ServiceDescription(typeof(IDriftSearchService), ServiceLifetime.Scoped)]
    public class DriftSearchService(
        ISemanticService _semantic,
        ILocalSearchService _localSearch,
        ICommunities_Repositories _communities
        ) : IDriftSearchService
    {
        private const double ConfidenceCutoff = 0.3;

        public async Task<DriftSearchResult> SearchAsync(string index, string input)
        {
            var built = await RunDriftAsync(index, input);
            var hierarchyText = RenderHierarchy(built.Root);
            var answer = await _semantic.GetDriftReduceAsync(
                hierarchyText, input, GraphQueryOption.ResponseType);
            return Finish(built, answer);
        }

        public async IAsyncEnumerable<StreamingKernelContent> SearchStreamAsync(string index, string input)
        {
            var built = await RunDriftAsync(index, input);
            var hierarchyText = RenderHierarchy(built.Root);
            var stream = _semantic.GetDriftReduceStreamAsync(
                hierarchyText, input, GraphQueryOption.ResponseType);
            await foreach (var content in stream)
            {
                yield return content;
            }
        }

        // ------------------------------------------------------------------
        // 三阶段
        // ------------------------------------------------------------------

        private sealed class DriftRun
        {
            public DriftNode Root { get; init; } = new();
            public List<DriftNode> Flat { get; } = new();
            public int FollowupCount { get; set; }
            public int LocalSearches { get; set; }
        }

        private async Task<DriftRun> RunDriftAsync(string index, string input)
        {
            if (string.IsNullOrWhiteSpace(index) || string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("Values required for index and input cannot be null.");

            var run = new DriftRun();
            run.Root.Question = input;

            // ---- 阶段 A: Primer ----
            var reports = LoadPrimerReports(index);
            var folds = SplitFolds(reports, Math.Max(1, GraphQueryOption.DriftPrimerFolds));
            var primerTasks = folds.Select(async fold =>
            {
                try
                {
                    return await _semantic.GetDriftPrimerAsync(
                        input, RenderReports(fold), Math.Max(1, folds.Count),
                        Math.Max(1, GraphQueryOption.DriftKFollowups));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"DRIFT primer 失败：{ex.Message}");
                    return null;
                }
            });
            var primers = await Task.WhenAll(primerTasks).ConfigureAwait(false);
            var valid = primers.Where(p => p is not null).Select(p => p!).ToList();

            run.Root.Answer = string.Join(Environment.NewLine + Environment.NewLine,
                valid.Select(p => p.Answer).Where(a => !string.IsNullOrWhiteSpace(a)));
            if (string.IsNullOrWhiteSpace(run.Root.Answer))
                run.Root.Answer = "基于当前社区报告未能形成初步答案。";
            run.Root.Confidence = valid.Count == 0 ? 0 : valid.Average(p => p.Confidence);

            var followups = Dedupe(valid.SelectMany(p => p.Followups))
                .Take(Math.Max(1, GraphQueryOption.DriftKFollowups))
                .ToList();
            run.FollowupCount = followups.Count;

            var current = followups.Select(q => new DriftNode { Question = q, Depth = 1 }).ToList();
            run.Root.Children = current;
            run.Flat.AddRange(current);

            // ---- 阶段 B: Follow-up（逐层展开，用 Local Search 细化） ----
            using var throttler = new SemaphoreSlim(Math.Max(1, GraphQueryOption.DriftConcurrency));
            int maxDepth = Math.Max(1, GraphQueryOption.DriftNDepth);

            for (int depth = 1; depth <= maxDepth && current.Count > 0; depth++)
            {
                var tasks = current.Select(async node =>
                {
                    await throttler.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var local = await _localSearch.SearchLocalAsync(index, node.Question, new[] { input });
                        run.LocalSearches++;
                        var follow = await _semantic.GetDriftFollowupAsync(
                            input, local.Answer, Math.Max(1, GraphQueryOption.DriftKFollowups));
                        node.Answer = string.IsNullOrWhiteSpace(follow?.Answer) ? local.Answer : follow!.Answer;
                        node.Confidence = follow?.Confidence ?? 1.0;

                        if (follow is not null && node.Confidence >= ConfidenceCutoff && depth < maxDepth)
                        {
                            node.Children = Dedupe(follow.Followups)
                                .Take(Math.Max(1, GraphQueryOption.DriftKFollowups))
                                .Select(q => new DriftNode { Question = q, Depth = depth + 1 })
                                .ToList();
                        }
                        return node.Children;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"DRIFT follow-up 失败：{ex.Message}");
                        node.Answer = "该分支检索失败：" + ex.Message;
                        node.Confidence = 0;
                        return new List<DriftNode>();
                    }
                    finally
                    {
                        throttler.Release();
                    }
                });

                var childrenLists = await Task.WhenAll(tasks).ConfigureAwait(false);
                var next = childrenLists.SelectMany(x => x).ToList();
                run.Flat.AddRange(next);
                current = next;
            }

            return run;
        }

        private DriftSearchResult Finish(DriftRun run, string answer)
            => new()
            {
                Answer = answer,
                Hierarchy = run.Root,
                FollowupCount = run.FollowupCount,
                LocalSearches = run.LocalSearches,
                FlatNodes = run.Flat
            };

        // ------------------------------------------------------------------
        // 报告与层级渲染
        // ------------------------------------------------------------------

        private List<Communities> LoadPrimerReports(string index)
        {
            var all = _communities.GetList(p => p.Index == index)
                .OrderByDescending(c => c.CommunityRank)
                .ThenByDescending(c => c.Rating)
                .ToList();
            int take = Math.Max(1, GraphQueryOption.DriftPrimerFolds * Math.Max(1, GraphQueryOption.DriftKFollowups));
            return all.Take(take).ToList();
        }

        private static List<List<Communities>> SplitFolds(List<Communities> reports, int folds)
        {
            var result = new List<List<Communities>>();
            if (reports.Count == 0) return result;
            int size = Math.Max(1, (int)Math.Ceiling(reports.Count / (double)folds));
            for (int i = 0; i < reports.Count; i += size)
                result.Add(reports.Skip(i).Take(size).ToList());
            return result;
        }

        private static string RenderReports(IEnumerable<Communities> reports)
        {
            var sb = new StringBuilder();
            foreach (var c in reports)
            {
                sb.AppendLine($"----- 社区报告 (id: {c.CommunitieId}, 层级 {c.Level}, 评分 {c.Rating:0.0}) -----");
                sb.AppendLine(string.IsNullOrWhiteSpace(c.FullContent) ? c.Summaries : c.FullContent);
                sb.AppendLine();
            }
            return sb.Length == 0 ? "（暂无社区报告）" : sb.ToString();
        }

        private static string RenderHierarchy(DriftNode root)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# 根问题: {root.Question}");
            sb.AppendLine($"初步答案: {root.Answer}");
            sb.AppendLine();
            sb.AppendLine("## 后续问题与答案（按展开层级）");
            RenderNode(root, 0, sb);
            return sb.ToString();
        }

        private static void RenderNode(DriftNode node, int depth, StringBuilder sb)
        {
            foreach (var child in node.Children)
            {
                sb.AppendLine($"{new string(' ', depth * 2)}- Q(depth {child.Depth}, 置信度 {child.Confidence:0.00}): {child.Question}");
                sb.AppendLine($"{new string(' ', depth * 2)}  A: {child.Answer}");
                RenderNode(child, depth + 1, sb);
            }
        }

        private static List<string> Dedupe(IEnumerable<string> items)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var item in items)
            {
                var s = (item ?? string.Empty).Trim();
                if (s.Length < 4 || !seen.Add(s)) continue;
                result.Add(s);
            }
            return result;
        }
    }
}
