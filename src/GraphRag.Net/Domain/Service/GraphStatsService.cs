using GraphRag.Net.Domain.Interface;
using GraphRag.Net.Options;
using GraphRag.Net.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace GraphRag.Net.Domain.Service
{
    [ServiceDescription(typeof(IGraphStatsService), ServiceLifetime.Scoped)]
    public class GraphStatsService(
        INodes_Repositories _nodes,
        IEdges_Repositories _edges,
        ICommunities_Repositories _communities
        ) : IGraphStatsService
    {
        public Task RecomputeAsync(string index)
        {
            if (!GraphQueryOption.EnableGraphStats) return Task.CompletedTask;

            var nodes = _nodes.GetList(p => p.Index == index);
            var edges = _edges.GetList(p => p.Index == index);

            // degree：关联边数
            var degree = new Dictionary<string, int>();
            foreach (var e in edges)
            {
                degree[e.Source] = degree.GetValueOrDefault(e.Source) + 1;
                degree[e.Target] = degree.GetValueOrDefault(e.Target) + 1;
            }

            foreach (var n in nodes)
            {
                n.Degree = degree.GetValueOrDefault(n.Id);
                n.Frequency = string.IsNullOrWhiteSpace(n.TextUnitIds)
                    ? 0
                    : n.TextUnitIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
            }
            if (nodes.Count > 0) _nodes.UpdateRange(nodes);

            // 边权重：从未显式赋值过的边补 1（合并时累加的值保持不变）
            var lightEdges = edges.Where(e => e.Weight <= 0).ToList();
            foreach (var e in lightEdges) e.Weight = 1;
            if (lightEdges.Count > 0) _edges.UpdateRange(lightEdges);

            // 社区 rank：影响力评分 + 成员度数 + 规模
            var communities = _communities.GetList(p => p.Index == index);
            foreach (var c in communities)
            {
                var memberDegree = 0;
                if (!string.IsNullOrWhiteSpace(c.Children))
                {
                    // 粗粒度社区用子社区节点覆盖度近似
                    memberDegree = c.Size;
                }
                else
                {
                    memberDegree = c.Size * 2;
                }
                c.CommunityRank = c.Rating * 10 + Math.Log(c.Size + 1) * 5 + Math.Min(memberDegree, 100) * 0.1;
            }
            if (communities.Count > 0) _communities.UpdateRange(communities);

            return Task.CompletedTask;
        }
    }
}
