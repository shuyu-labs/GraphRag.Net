using GraphRag.Net.Domain.Interface;
using GraphRag.Net.Domain.Model.Graph;
using GraphRag.Net.Options;
using GraphRag.Net.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace GraphRag.Net.Domain.Service
{
    [ServiceDescription(typeof(IDynamicCommunitySelectionService), ServiceLifetime.Scoped)]
    public class DynamicCommunitySelectionService(
        ISemanticService _semantic,
        ICommunities_Repositories _communities
        ) : IDynamicCommunitySelectionService
    {
        public int MaxLevel(string index)
        {
            var all = _communities.GetList(p => p.Index == index);
            return all.Count == 0 ? 0 : all.Max(c => c.Level);
        }

        public async Task<List<Communities>> SelectAsync(string index, string input, int? maxReports = null)
        {
            if (string.IsNullOrWhiteSpace(index) || string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("Values required for index and input cannot be null.");

            int max = Math.Max(1, maxReports ?? GraphQueryOption.MaxCommunityReports);
            var all = _communities.GetList(p => p.Index == index);
            if (all.Count == 0) return new List<Communities>();

            var mode = GraphQueryOption.DynamicSelectionMode?.Trim().ToLowerInvariant() ?? "llm";
            if (mode == "off" || all.Count <= max)
                return RankFallback(all, max);

            if (mode == "vector")
                return await VectorSelectAsync(index, input, all, max);

            return await LlmSelectAsync(index, input, all, max);
        }

        // ------------------------------------------------------------------
        // LLM 评分 + 层级回退
        // ------------------------------------------------------------------

        private async Task<List<Communities>> LlmSelectAsync(
            string index, string input, List<Communities> all, int max)
        {
            int maxLevel = MaxLevel(index);
            // 从最细粒度开始评分；若无相关社区，逐级向更粗层级回退
            int targetLevel = 0;
            int ceiling = GraphQueryOption.DynamicMaxLevel >= 0
                ? Math.Min(GraphQueryOption.DynamicMaxLevel, maxLevel)
                : maxLevel;

            var byId = all.ToDictionary(c => c.CommunitieId, c => c);

            for (int level = targetLevel; level <= ceiling; level++)
            {
                var candidates = all
                    .Where(c => c.Level == level)
                    .OrderByDescending(c => c.CommunityRank)
                    .ToList();
                if (candidates.Count == 0) continue;

                // 先用向量预筛缩小 LLM 调用量；失败则全量评分
                var shortlist = await VectorPreselectAsync(index, input, candidates, max * 2);

                using var throttler = new SemaphoreSlim(Math.Max(1, GraphQueryOption.ConcurrentCoroutines));
                var tasks = shortlist.Select(async c =>
                {
                    await throttler.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        int best = 0;
                        for (int r = 0; r < Math.Max(1, GraphQueryOption.DynamicNumRepeats); r++)
                        {
                            var rate = await _semantic.RateCommunityAsync(
                                input,
                                string.IsNullOrWhiteSpace(c.FullContent) ? c.Summaries : c.FullContent!,
                                c.Title ?? c.CommunitieId);
                            if (rate is null) continue;
                            best = Math.Max(best, rate.Score);
                            if (rate.Relevant) best = Math.Max(best, GraphQueryOption.DynamicSearchThreshold);
                        }
                        return (Community: c, Score: best);
                    }
                    finally
                    {
                        throttler.Release();
                    }
                });

                var rated = await Task.WhenAll(tasks).ConfigureAwait(false);
                var selected = rated
                    .Where(x => x.Score >= GraphQueryOption.DynamicSearchThreshold)
                    .OrderByDescending(x => x.Score)
                    .Take(max)
                    .Select(x => x.Community)
                    .ToList();

                if (selected.Count == 0) continue; // 向更粗层级回退

                if (GraphQueryOption.DynamicKeepParent)
                {
                    foreach (var c in selected.ToList())
                    {
                        if (!string.IsNullOrWhiteSpace(c.Parent) && byId.TryGetValue(c.Parent!, out var parent)
                            && !selected.Any(s => s.CommunitieId == parent.CommunitieId))
                        {
                            selected.Add(parent);
                        }
                    }
                }

                return selected.Take(max + 2).ToList();
            }

            return RankFallback(all, max);
        }

        // ------------------------------------------------------------------
        // 向量快速路径 / 预筛
        // ------------------------------------------------------------------

        private async Task<List<Communities>> VectorSelectAsync(
            string index, string input, List<Communities> all, int max)
            => await VectorPreselectAsync(index, input, all, max);

        private async Task<List<Communities>> VectorPreselectAsync(
            string index, string input, List<Communities> candidates, int limit)
        {
            try
            {
                var memory = await _semantic.GetTextMemory();
                var ids = new List<string>();
                await foreach (var m in memory.SearchAsync(
                    GraphCollections.Communities(index), input, limit: Math.Max(1, limit),
                    minRelevanceScore: 0.0))
                {
                    ids.Add(m.Metadata.Id);
                }
                var picked = ids
                    .Select(id => candidates.FirstOrDefault(c => c.CommunitieId == id))
                    .Where(c => c is not null)
                    .Select(c => c!)
                    .ToList();
                if (picked.Count > 0) return picked;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"动态社区选择向量预筛失败，改用全量评分：{ex.Message}");
            }
            return candidates;
        }

        private static List<Communities> RankFallback(List<Communities> all, int max)
            => all.OrderByDescending(c => c.CommunityRank)
                .ThenByDescending(c => c.Rating)
                .Take(max)
                .ToList();
    }
}
