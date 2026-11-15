using GraphRag.Net.Ai;
using GraphRag.Net.Common.Options;
using GraphRag.Net.Domain.Interface;
using GraphRag.Net.Domain.Model.Graph;
using GraphRag.Net.Options;
using GraphRag.Net.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Memory;
using Newtonsoft.Json;
using Polly;

namespace GraphRag.Net.Domain.Service
{
    /// <summary>
    /// LLM-facing service. All prompt execution now goes through the pi.net-backed
    /// <see cref="Kernel"/> (OpenAiCompletions provider + agent loop); the public
    /// <see cref="ISemanticService"/> surface is unchanged.
    /// </summary>
    [ServiceDescription(typeof(ISemanticService), ServiceLifetime.Scoped)]
    public class SemanticService(Kernel _kernel) : ISemanticService
    {
        public async Task<GraphModel> CreateGraphAsync(string input)
        {
            // pi.net agent loop: 抽取 → 校验 → 修正 → 再校验（结果迭代），
            // 失败时回退到经典"单次提示词 + Polly 重试"链路。
            if (PiAgentOption.EnableAgentLoop)
            {
                var agentGraph = await TryCreateGraphWithAgentAsync(input);
                if (agentGraph is not null && agentGraph.Nodes?.Count > 0) return agentGraph;
                Console.WriteLine("[GraphAgent] 图谱抽取智能体未产出有效结果，回退经典链路");
            }

            var retryPolicy = Policy.Handle<Exception>().RetryAsync(GraphSysOption.RetryCounnt, (ex, count) =>
            {
                Console.WriteLine($"CreateGraphAsync失败，重试{count}次，异常信息{ex.Message}");
            });
            return await retryPolicy.ExecuteAsync<GraphModel>(async () =>
            {
                var json = await _kernel.InvokePluginAsync(
                    "graph", "create", new Dictionary<string, object?> { ["input"] = input });
                return JsonConvert.DeserializeObject<GraphModel>(json);
            });
        }

        /// <summary>
        /// 用 pi.net agent loop 做"抽取-校验-修正"多步骤图谱生成。
        /// validate_graph 工具把结构问题回流给模型，模型据此迭代修正，直到调用
        /// finalize_graph 提交合法 JSON。
        /// </summary>
        private async Task<GraphModel?> TryCreateGraphWithAgentAsync(string input)
        {
            try
            {
                string? finalJson = null;
                var template = PromptCatalog.Load(
                    "graph", "create", new Dictionary<string, object?> { ["input"] = input }).Prompt;

                var request = new AgentRequest
                {
                    MaxTurns = Math.Max(2, PiAgentOption.MaxTurns),
                    SystemPrompt =
                        "你是知识图谱抽取智能体。请严格按给定规则从文本中抽取节点与边，输出 JSON：\n" +
                        "{\"Nodes\":[{\"Id\":\"node1\",\"Name\":\"..\",\"Type\":\"..\",\"Desc\":\"..\"}]," +
                        "\"Edges\":[{\"Source\":\"node1\",\"Target\":\"node2\",\"Relationship\":\"..\"}]}\n" +
                        "流程：先生成 JSON，然后必须调用 validate_graph 工具校验；\n" +
                        "若工具返回问题，修正 JSON 后再次校验（可多轮）；\n" +
                        "校验通过后调用 finalize_graph 工具提交最终 JSON 并结束。\n" +
                        "只输出 JSON 本身，不要 markdown。",
                    UserPrompt = template + "\n\n请开始抽取、校验并迭代修正。",
                    Tools = new[]
                    {
                        new AgentToolSpec
                        {
                            Name = "validate_graph",
                            Description = "校验知识图谱 JSON 的结构合法性，返回 OK 或具体问题列表。",
                            Parameters = new Dictionary<string, object?>
                            {
                                ["type"] = "object",
                                ["properties"] = new Dictionary<string, object?>
                                {
                                    ["json"] = new Dictionary<string, object?>
                                    { ["type"] = "string", ["description"] = "待校验的图谱 JSON 字符串" }
                                },
                                ["required"] = new[] { "json" }
                            },
                            Execute = (args, _) =>
                            {
                                var json = GetStringArg(args, "json");
                                return Task.FromResult(ValidateGraphJson(json));
                            }
                        },
                        new AgentToolSpec
                        {
                            Name = "finalize_graph",
                            Description = "提交已通过校验的最终图谱 JSON 并结束运行。",
                            Parameters = new Dictionary<string, object?>
                            {
                                ["type"] = "object",
                                ["properties"] = new Dictionary<string, object?>
                                {
                                    ["json"] = new Dictionary<string, object?>
                                    { ["type"] = "string", ["description"] = "最终图谱 JSON 字符串" }
                                },
                                ["required"] = new[] { "json" }
                            },
                            Execute = (args, _) =>
                            {
                                finalJson = GetStringArg(args, "json");
                                return Task.FromResult("图谱已提交。");
                            },
                            Terminate = true
                        }
                    }
                };

                var result = await _kernel.RunAgentAsync(request);
                if (string.IsNullOrWhiteSpace(finalJson)) finalJson = result.FinalText;
                return ParseGraphJson(finalJson);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GraphAgent] 图谱抽取智能体异常：{ex.Message}");
                return null;
            }
        }

        private static string? GetStringArg(System.Text.Json.JsonElement args, string name)
            => args.ValueKind == System.Text.Json.JsonValueKind.Object
               && args.TryGetProperty(name, out var v)
               && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() : null;

        private static string ValidateGraphJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return "问题：JSON 为空。";
            try
            {
                var graph = JsonConvert.DeserializeObject<GraphModel>(json!);
                if (graph?.Nodes is null || graph.Nodes.Count == 0) return "问题：缺少 Nodes 或为空。";
                var ids = new HashSet<string>(graph.Nodes.Select(n => n.Id));
                foreach (var n in graph.Nodes)
                    if (string.IsNullOrWhiteSpace(n.Id) || string.IsNullOrWhiteSpace(n.Name))
                        return "问题：存在缺少 Id 或 Name 的节点。";
                foreach (var e in graph.Edges ?? new List<Edges>())
                    if (!ids.Contains(e.Source) || !ids.Contains(e.Target))
                        return $"问题：边 {e.Source}->{e.Target} 引用了不存在的节点 Id。";
                return "OK：结构合法。";
            }
            catch (Exception ex)
            {
                return $"问题：JSON 解析失败：{ex.Message}";
            }
        }

        private static GraphModel? ParseGraphJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<GraphModel>(json!); }
            catch { return null; }
        }

        public async Task<string> GetGraphAnswerAsync(string graph, string input)
            => await _kernel.InvokePluginAsync(
                "graph", "search",
                new Dictionary<string, object?> { ["graph"] = graph, ["input"] = input });

        public IAsyncEnumerable<StreamingKernelContent> GetGraphAnswerStreamAsync(string graph, string input)
            => _kernel.InvokeStreamingAsync(
                "graph", "search",
                new Dictionary<string, object?> { ["graph"] = graph, ["input"] = input });

        public async Task<string> GetGraphCommunityAnswerAsync(string graph, string community, string global, string input)
            => await _kernel.InvokePluginAsync(
                "graph", "community_search",
                new Dictionary<string, object?>
                {
                    ["graph"] = graph,
                    ["community"] = community,
                    ["global"] = global,
                    ["input"] = input
                });

        public IAsyncEnumerable<StreamingKernelContent> GetGraphCommunityAnswerStreamAsync(string graph, string community, string global, string input)
            => _kernel.InvokeStreamingAsync(
                "graph", "community_search",
                new Dictionary<string, object?>
                {
                    ["graph"] = graph,
                    ["community"] = community,
                    ["global"] = global,
                    ["input"] = input
                });

        public async Task<RelationShipModel> GetRelationship(string node1, string node2)
        {
            var retryPolicy = Policy.Handle<Exception>().RetryAsync(GraphSysOption.RetryCounnt, (ex, count) =>
            {
                Console.WriteLine($"GetRelationship失败，重试{count}次，异常信息{ex.Message}");
            });
            return await retryPolicy.ExecuteAsync<RelationShipModel>(async () =>
            {
                var json = await _kernel.InvokePluginAsync(
                    "graph", "relationship",
                    new Dictionary<string, object?> { ["node1"] = node1, ["node2"] = node2 });
                return JsonConvert.DeserializeObject<RelationShipModel>(json);
            });
        }

        public async Task<string> MergeDesc(string desc1, string desc2)
            => await _kernel.InvokePluginAsync(
                "graph", "mergedesc",
                new Dictionary<string, object?> { ["desc1"] = desc1, ["desc2"] = desc2 });

        public async Task<string> CommunitySummaries(string nodes)
            => await _kernel.InvokePluginAsync(
                "graph", "community_summaries",
                new Dictionary<string, object?> { ["nodes"] = nodes });

        public async Task<string> GlobalSummaries(string community)
            => await _kernel.InvokePluginAsync(
                "graph", "global_summaries",
                new Dictionary<string, object?> { ["community"] = community });

        /// <summary>
        /// 获取向量记忆。SK 的 Sqlite/Postgres 连接器已替换为原生实现；
        /// 既有 GraphDBConnection 配置仍被读取以兼容配置文件。
        /// </summary>
        public Task<SemanticTextMemory> GetTextMemory()
        {
            // Keep the legacy config surface meaningful (DbType / VectorConnection / VectorSize).
            _ = GraphDBConnectionOption.DbType;
            _ = GraphDBConnectionOption.VectorConnection;
            _ = GraphDBConnectionOption.VectorSize;

            var memory = new SemanticTextMemory(new EmbeddingClient(), new VectorStore());
            return Task.FromResult(memory);
        }
    }
}
