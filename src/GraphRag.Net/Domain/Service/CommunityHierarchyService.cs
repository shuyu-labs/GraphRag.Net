using System.Text;
using GraphRag.Net.Domain.Interface;
using GraphRag.Net.Domain.Model.Graph;
using GraphRag.Net.Options;
using GraphRag.Net.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.Memory;
using Newtonsoft.Json;

namespace GraphRag.Net.Domain.Service
{
    /// <summary>
    /// 分层社区：level 0 在原始图上做加权标签传播；之后把上一层社区折叠为超节点，
    /// 以跨社区边数为权重在超节点图上再传播，直到社区数不再减少或达到上限。
    /// 每层都产出结构化社区报告（level 0 由实体/关系生成，上层由子报告汇总）。
    /// </summary>
    [ServiceDescription(typeof(ICommunityHierarchyService), ServiceLifetime.Scoped)]
    public class CommunityHierarchyService(
        ISemanticService _semanticService,
        INodes_Repositories _nodes,
        IEdges_Repositories _edges,
        ICommunities_Repositories _communities,
        ICommunitieNodes_Repositories _communityNodes,
        IGraphStatsService _graphStats
        ) : ICommunityHierarchyService
    {
        public async Task<int> RebuildAsync(string index)
        {
            var nodes = _nodes.GetList(p => p.Index == index);
            var edges = _edges.GetList(p => p.Index == index);
            var levels = BuildHierarchy(nodes, edges, Math.Max(1, GraphQueryOption.CommunityMaxLevels));

            // 清理旧社区 / 旧报告向量
            SemanticTextMemory? textMemory = null;
            try
            {
                textMemory = await _semanticService.GetTextMemory();
                foreach (var old in _communities.GetList(p => p.Index == index))
                    await textMemory.RemoveAsync(GraphCollections.Communities(index), old.CommunitieId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"清理旧社区向量失败（不影响重建）：{ex.Message}");
            }

            _communityNodes.Delete(p => p.Index == index);
            _communities.Delete(p => p.Index == index);

            // 1) 写入各层级的 节点-社区 关系
            foreach (var level in levels)
            {
                foreach (var cluster in level.Clusters)
                {
                    foreach (var nodeId in cluster.NodeIds)
                    {
                        _communityNodes.Insert(new CommunitieNodes
                        {
                            Index = index,
                            CommunitieId = cluster.Id,
                            NodeId = nodeId,
                            Level = level.Level
                        });
                    }
                }
            }

            // 2) 由细到粗生成报告：上层依赖下层的 FullContent
            var rendered = new Dictionary<string, CommunityReportModel>(StringComparer.Ordinal);
            var nodeMap = nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());

            foreach (var level in levels)
            {
                foreach (var cluster in level.Clusters)
                {
                    CommunityReportModel? report;
                    if (level.Level == 0)
                    {
                        try
                        {
                            report = await _semanticService.CommunityReportAsync(
                                BuildLevelZeroContext(cluster, nodeMap, edges));
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"社区报告生成失败，回退纯文本摘要：{ex.Message}");
                            report = null;
                        }
                        if (report is null)
                        {
                            // 回退纯文本摘要链路
                            var nodeDescs = string.Join(Environment.NewLine,
                                cluster.NodeIds.Where(nodeMap.ContainsKey)
                                    .Select(id => $"Name:{nodeMap[id].Name}; Type:{nodeMap[id].Type}; Desc:{nodeMap[id].Desc}"));
                            var summary = await _semanticService.CommunitySummaries(nodeDescs);
                            report = new CommunityReportModel
                            {
                                Title = cluster.Id,
                                Summary = summary,
                                Rating = 0,
                                Findings = new List<CommunityFinding>()
                            };
                        }
                    }
                    else
                    {
                        var childText = string.Join(Environment.NewLine + Environment.NewLine,
                            cluster.Children.Where(rendered.ContainsKey)
                                .Select(childId => RenderReport(rendered[childId], childId)));
                        try
                        {
                            report = string.IsNullOrWhiteSpace(childText)
                                ? null
                                : await _semanticService.RollupCommunityReportsAsync(childText, level.Level);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"层级社区报告汇总失败，回退拼接文本：{ex.Message}");
                            report = null;
                        }
                        report ??= new CommunityReportModel
                        {
                            Title = $"第 {level.Level} 层社区汇总",
                            Summary = childText.Length > 2000 ? childText[..2000] : childText,
                            Rating = cluster.Children.Where(rendered.ContainsKey)
                                .Select(c => rendered[c].Rating).DefaultIfEmpty(0).Max(),
                            Findings = new List<CommunityFinding>()
                        };
                    }

                    report.Findings = (report.Findings ?? new List<CommunityFinding>())
                        .Take(Math.Max(1, GraphQueryOption.MaxReportFindings)).ToList();
                    rendered[cluster.Id] = report;

                    var fullContent = RenderReport(report, cluster.Id);
                    _communities.Insert(new Communities
                    {
                        CommunitieId = cluster.Id,
                        Index = index,
                        Summaries = BuildCommunitySummary(report),
                        Title = report.Title,
                        FullContent = fullContent,
                        Findings = JsonConvert.SerializeObject(report.Findings),
                        Rating = report.Rating,
                        RatingExplanation = report.RatingExplanation,
                        Size = cluster.Size,
                        Level = level.Level,
                        Parent = cluster.Parent,
                        Children = cluster.Children.Count == 0 ? null : string.Join(",", cluster.Children),
                        CommunityRank = 0
                    });

                    if (textMemory is not null)
                    {
                        try
                        {
                            await textMemory.SaveInformationAsync(
                                collection: GraphCollections.Communities(index),
                                id: cluster.Id,
                                text: fullContent);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"社区报告向量化失败（不影响重建）：{ex.Message}");
                        }
                    }
                }
            }

            // 3) 图统计信号（依赖 Communities 已落盘）
            await _graphStats.RecomputeAsync(index);

            return levels.Count;
        }

        public List<CommunityLevelInfo> GetLevels(string index)
            => _communities.GetList(p => p.Index == index)
                .GroupBy(c => c.Level)
                .Select(g => new CommunityLevelInfo
                {
                    Level = g.Key,
                    CommunityCount = g.Count(),
                    NodeCount = g.Sum(c => c.Size)
                })
                .OrderBy(l => l.Level)
                .ToList();

        // ------------------------------------------------------------------
        // 层级构建
        // ------------------------------------------------------------------

        private static List<CommunityLevel> BuildHierarchy(
            List<Nodes> nodes, List<Edges> edges, int maxLevels)
        {
            var levels = new List<CommunityLevel>();
            if (nodes.Count == 0) return levels;

            // level 0：原始图
            var adj = new Dictionary<string, Dictionary<string, int>>();
            foreach (var n in nodes) adj[n.Id] = new Dictionary<string, int>();
            foreach (var e in edges)
            {
                if (!adj.ContainsKey(e.Source) || !adj.ContainsKey(e.Target)) continue;
                adj[e.Source][e.Target] = adj[e.Source].GetValueOrDefault(e.Target) + 1;
                adj[e.Target][e.Source] = adj[e.Target].GetValueOrDefault(e.Source) + 1;
            }
            var labels0 = WeightedLabelPropagation(adj, 12);
            levels.Add(GroupNodes(labels0, 0));

            // level >= 1：折叠上一层社区为超节点
            for (int k = 1; k < maxLevels; k++)
            {
                var prev = levels[k - 1];
                if (prev.Clusters.Count <= Math.Max(1, GraphQueryOption.CommunityMinSize)) break;

                var nodeToCluster = new Dictionary<string, string>();
                foreach (var c in prev.Clusters)
                    foreach (var id in c.NodeIds)
                        nodeToCluster[id] = c.Id;

                var super = new Dictionary<string, Dictionary<string, int>>();
                foreach (var c in prev.Clusters) super[c.Id] = new Dictionary<string, int>();
                foreach (var e in edges)
                {
                    if (!nodeToCluster.TryGetValue(e.Source, out var a) ||
                        !nodeToCluster.TryGetValue(e.Target, out var b) || a == b) continue;
                    super[a][b] = super[a].GetValueOrDefault(b) + 1;
                    super[b][a] = super[b].GetValueOrDefault(a) + 1;
                }

                var labels = WeightedLabelPropagation(super, 12);
                var collapsed = Collapse(prev, labels, k);
                if (collapsed.Clusters.Count >= prev.Clusters.Count)
                {
                    // 超节点图无跨社区边时无法自然合并：生成一个覆盖全部子社区的顶层。
                    // 对应参考实现「最粗层级覆盖全图」的语义，同时保证层级结构可用。
                    var root = new CommunityCluster
                    {
                        Id = $"L{k}_ROOT",
                        Level = k,
                        Children = prev.Clusters.Select(c => c.Id)
                            .OrderBy(x => x, StringComparer.Ordinal).ToList(),
                        NodeIds = prev.Clusters.SelectMany(c => c.NodeIds).Distinct()
                            .OrderBy(x => x, StringComparer.Ordinal).ToList()
                    };
                    levels.Add(new CommunityLevel
                    {
                        Level = k,
                        Clusters = new List<CommunityCluster> { root }
                    });
                    break;
                }
                levels.Add(collapsed);
            }

            // 回填 parent / children
            for (int k = 0; k < levels.Count - 1; k++)
            {
                foreach (var parent in levels[k + 1].Clusters)
                {
                    foreach (var child in levels[k].Clusters.Where(c => parent.Children.Contains(c.Id)))
                        child.Parent = parent.Id;
                }
            }

            return levels;
        }

        /// <summary>确定性加权标签传播（固定种子，便于回归测试）。</summary>
        private static Dictionary<string, string> WeightedLabelPropagation(
            Dictionary<string, Dictionary<string, int>> adjacency, int iterations)
        {
            var labels = adjacency.Keys.ToDictionary(n => n, n => n);
            var rng = new Random(20240611);
            var order = adjacency.Keys.ToList();

            for (int iter = 0; iter < iterations; iter++)
            {
                foreach (var node in order.OrderBy(_ => rng.Next()))
                {
                    var counts = new Dictionary<string, int>();
                    foreach (var (neighbor, weight) in adjacency[node])
                    {
                        var label = labels[neighbor];
                        counts[label] = counts.GetValueOrDefault(label) + weight;
                    }
                    if (counts.Count == 0) continue;
                    var max = counts.Values.Max();
                    labels[node] = counts.Where(c => c.Value == max)
                        .Select(c => c.Key).OrderBy(x => x, StringComparer.Ordinal).First();
                }
            }
            return labels;
        }

        private static CommunityLevel GroupNodes(Dictionary<string, string> labels, int level)
        {
            var groups = new Dictionary<string, List<string>>();
            foreach (var (nodeId, label) in labels)
            {
                if (!groups.TryGetValue(label, out var list)) groups[label] = list = new List<string>();
                list.Add(nodeId);
            }
            return new CommunityLevel
            {
                Level = level,
                Clusters = groups.Select(g => new CommunityCluster
                {
                    Id = $"L{level}_{g.Key}",
                    Level = level,
                    NodeIds = g.Value.OrderBy(x => x, StringComparer.Ordinal).ToList()
                }).OrderBy(c => c.Id, StringComparer.Ordinal).ToList()
            };
        }

        private static CommunityLevel Collapse(
            CommunityLevel prev, Dictionary<string, string> clusterLabel, int level)
        {
            var groups = new Dictionary<string, List<CommunityCluster>>();
            foreach (var c in prev.Clusters)
            {
                var label = clusterLabel.GetValueOrDefault(c.Id, c.Id);
                if (!groups.TryGetValue(label, out var list)) groups[label] = list = new List<CommunityCluster>();
                list.Add(c);
            }

            var clusters = groups.Select(g => new CommunityCluster
            {
                Id = $"L{level}_{g.Key}",
                Level = level,
                Children = g.Value.Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                NodeIds = g.Value.SelectMany(c => c.NodeIds).Distinct()
                    .OrderBy(x => x, StringComparer.Ordinal).ToList()
            }).OrderBy(c => c.Id, StringComparer.Ordinal).ToList();

            return new CommunityLevel { Level = level, Clusters = clusters };
        }

        // ------------------------------------------------------------------
        // 报告上下文与渲染
        // ------------------------------------------------------------------

        private static string BuildLevelZeroContext(
            CommunityCluster cluster, Dictionary<string, Nodes> nodeMap, List<Edges> edges)
        {
            var sb = new StringBuilder();
            sb.AppendLine("--- 社区实体 ---");
            foreach (var id in cluster.NodeIds.Where(nodeMap.ContainsKey))
            {
                var n = nodeMap[id];
                sb.AppendLine($"Name:{n.Name}; Type:{n.Type}; Desc:{n.Desc}");
            }
            var members = cluster.NodeIds.ToHashSet();
            sb.AppendLine("--- 社区内部关系 ---");
            foreach (var e in edges.Where(e => members.Contains(e.Source) && members.Contains(e.Target)).Take(60))
            {
                var s = nodeMap.GetValueOrDefault(e.Source)?.Name ?? e.Source;
                var t = nodeMap.GetValueOrDefault(e.Target)?.Name ?? e.Target;
                sb.AppendLine($"Relationship: {s} -> {t}; Desc:{e.Relationship}");
            }
            return sb.ToString();
        }

        private static string BuildCommunitySummary(CommunityReportModel report)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(report.Title)) sb.AppendLine($"标题：{report.Title}");
            sb.AppendLine(report.Summary);
            foreach (var f in report.Findings ?? new List<CommunityFinding>())
                sb.AppendLine($"- {f.Summary}：{f.Explanation}");
            var text = sb.ToString();
            return text.Length > 3900 ? text[..3900] : text;
        }

        private static string RenderReport(CommunityReportModel report, string id)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"社区报告 ID: {id}");
            sb.AppendLine($"标题: {report.Title}");
            sb.AppendLine($"影响力评分: {report.Rating:0.0}");
            sb.AppendLine($"评分说明: {report.RatingExplanation}");
            sb.AppendLine($"摘要: {report.Summary}");
            sb.AppendLine("关键发现:");
            int i = 1;
            foreach (var f in report.Findings ?? new List<CommunityFinding>())
            {
                sb.AppendLine($"{i++}. {f.Summary}");
                sb.AppendLine($"   {f.Explanation}");
            }
            return sb.ToString();
        }
    }
}
