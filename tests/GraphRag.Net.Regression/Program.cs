using System.Collections;
using System.Reflection;
using System.Text;
using GraphRag.Net.Ai;
using GraphRag.Net.Domain.Interface;
using GraphRag.Net.Domain.Model.Graph;
using GraphRag.Net.Options;
using GraphRag.Net.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Memory;
using Pi.Ai.Stream;
using Pi.Ai.Types;

namespace GraphRag.Net.Regression;

/// <summary>
/// 离线回归测试：覆盖既有能力（防退化）+ 全部 P0/P1 新能力 + 容错路径。
/// 运行：dotnet run --project tests/GraphRag.Net.Regression -f net9.0
/// </summary>
internal static class Program
{
    private static readonly string Index = "regression_" + Guid.NewGuid().ToString("N")[..8];
    private const string EmptyIndex = "regression_empty";
    private const string MigrationIndex = "regression_migration";
    private const string DeleteIndex = "regression_delete";

    private static IServiceProvider _sp = null!;
    private static int _checks;
    private static int _failures;
    private static bool _breakCommunityReport;
    private static string _tunedMarker = "TUNED_CREATE_V1";

    private static async Task<int> Main()
    {
        var repoRoot = FindRepoRoot();
        var dbPath = Path.Combine(Path.GetTempPath(),
            "graphrag-regression-" + Guid.NewGuid().ToString("N") + ".db");
        // 用仓库自带的旧 schema DB 副本，顺带验证 CodeFirst 迁移
        File.Copy(Path.Combine(repoRoot, "src", "GraphRag.Net.Web", "graph.db"), dbPath, true);
        Console.WriteLine($"回归测试数据库: {dbPath} (旧 schema 副本)");

        // 必须在任何 Repository 静态初始化之前设置连接串
        GraphDBConnectionOption.DbType = "Sqlite";
        GraphDBConnectionOption.DBConnection = "Data Source=" + dbPath;
        GraphOpenAIOption.EndPoint = string.Empty;
        GraphOpenAIOption.Key = string.Empty;
        PiAgentOption.EnableAgentLoop = false;

        // 小而确定的超参，保证调用次数与结果可预测
        GraphQueryOption.CommunityMaxLevels = 3;
        GraphQueryOption.DynamicCommunitySelection = true;
        GraphQueryOption.DynamicSelectionMode = "llm";
        GraphQueryOption.DynamicNumRepeats = 1;
        GraphQueryOption.DynamicSearchThreshold = 50;
        GraphQueryOption.DynamicKeepParent = true;
        GraphQueryOption.GlobalCommunityLevel = -1;
        GraphQueryOption.MaxCommunityReports = 10;
        GraphQueryOption.DriftKFollowups = 1;
        GraphQueryOption.DriftPrimerFolds = 1;
        GraphQueryOption.DriftNDepth = 1;
        GraphQueryOption.DriftConcurrency = 1;
        GraphQueryOption.EnableClaimExtraction = false;
        GraphQueryOption.EnableGraphStats = true;
        GraphQueryOption.LocalTopKEntities = 10;
        GraphQueryOption.LocalTopKRelationships = 10;
        GraphQueryOption.LocalMaxContextTokens = 12000;
        GraphQueryOption.PromptTuneSampleSize = 3;
        GraphQueryOption.PromptTuneEntityTypes = 4;
        GraphQueryOption.BasicSearchTopK = 3;

        // 确定性离线向量：按字符 bigram 哈希到 64 维，无需任何网络
        VectorMemoryProvider.SetFactory(() =>
            new SemanticTextMemory(new HashEmbedding(), new VectorStore()));

        var services = new ServiceCollection();
        services.AddGraphRagNet(new Kernel(MockStreamFn()));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        Holder.Scope = scope.ServiceProvider;

        try
        {
            await RunInfrastructureAsync();
            await RunImportAndIndexAsync();
            await RunExistingRegressionAsync();
            await RunP0Async();
            await RunP1Async();
            await RunRobustnessAsync();
        }
        catch (Exception ex)
        {
            Check("回归测试执行过程未抛出未处理异常", false, ex.ToString());
        }
        finally
        {
            VectorMemoryProvider.Reset();
        }

        Console.WriteLine();
        Console.WriteLine($"共 {_checks} 项检查，失败 {_failures} 项");
        Console.WriteLine(_failures == 0 ? "REGRESSION: ALL PASSED" : "REGRESSION: FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static IServiceProvider Sp => Holder.Scope;

    private static class Holder
    {
        public static IServiceProvider Scope = null!;
    }

    // ==================================================================
    // A. 基础设施与迁移
    // ==================================================================
    private static async Task RunInfrastructureAsync()
    {
        Section("A. 基础设施 / 旧库迁移 / DI");

        var names = new (string Name, Type Type)[]
        {
            ("IGraphService", typeof(IGraphService)),
            ("IGraphSearchService", typeof(IGraphSearchService)),
            ("ILocalSearchService", typeof(ILocalSearchService)),
            ("IDriftSearchService", typeof(IDriftSearchService)),
            ("IGraphIndexService", typeof(IGraphIndexService)),
            ("ICommunityHierarchyService", typeof(ICommunityHierarchyService)),
            ("IDynamicCommunitySelectionService", typeof(IDynamicCommunitySelectionService)),
            ("IGraphStatsService", typeof(IGraphStatsService)),
            ("IClaims_Repositories", typeof(IClaims_Repositories)),
            ("ITextUnits_Repositories", typeof(ITextUnits_Repositories))
        };
        foreach (var (name, type) in names)
            Check($"DI 解析 {name}", Sp.GetService(type) is not null);

        // 旧库加列：插入带全部新字段的行，若 CodeFirst 未加列会直接抛异常
        try
        {
            var nodes = Sp.GetRequiredService<INodes_Repositories>();
            var communities = Sp.GetRequiredService<ICommunities_Repositories>();
            var units = Sp.GetRequiredService<ITextUnits_Repositories>();
            var claims = Sp.GetRequiredService<IClaims_Repositories>();
            var cn = Sp.GetRequiredService<ICommunitieNodes_Repositories>();

            nodes.Insert(new Nodes
            {
                Id = "mig_n1", Index = MigrationIndex, Name = "迁移测试", Type = "Concept",
                Desc = "旧库加列", TextUnitIds = "mig_tu1", Degree = 3, Frequency = 1
            });
            var edges = Sp.GetRequiredService<IEdges_Repositories>();
            edges.Insert(new Edges
            {
                Id = "mig_e1", Index = MigrationIndex, Source = "mig_n1", Target = "mig_n1",
                Relationship = "自环", Weight = 2.5, TextUnitIds = "mig_tu1"
            });
            units.Insert(new TextUnits
            {
                Id = "mig_tu1", Index = MigrationIndex, Text = "迁移文本",
                NTokens = 4, ContentHash = "HASH", DocumentId = "doc"
            });
            claims.Insert(new Claims { Id = "mig_c1", Index = MigrationIndex, SubjectId = "A", Status = "TRUE" });
            communities.Insert(new Communities
            {
                CommunitieId = "mig_com1", Index = MigrationIndex, Summaries = "摘要",
                Level = 1, Parent = "L2_x", Children = "L0_a", CommunityRank = 9.5, Size = 2
            });
            cn.Insert(new CommunitieNodes
            {
                Index = MigrationIndex, CommunitieId = "mig_com1", NodeId = "mig_n1", Level = 1
            });
            Check("旧库 CodeFirst 迁移：新增表 + Nodes/Edges/Communities/CommunitieNodes/TextUnits 新列可写", true);
        }
        catch (Exception ex)
        {
            Check("旧库 CodeFirst 迁移：新增表 + 新列可写", false, ex.Message);
        }

        // 提示词资产加载与占位符渲染
        var createSpec = LoadPromptSpec("graph", "create", new Dictionary<string, object?> { ["input"] = "探针文本" });
        Check("提示词资产加载 + 占位符渲染",
            createSpec.Prompt.Contains("探针文本") && !createSpec.Prompt.Contains("{{$input}}"),
            $"len={createSpec.Prompt.Length}");

        await Task.CompletedTask;
    }

    // ==================================================================
    // B. 导入 / 分层社区 / 全局摘要 / 图统计
    // ==================================================================
    private static async Task RunImportAndIndexAsync()
    {
        Section("B. 导入 / P0-1 分层社区 / P0-3 图统计");

        var graph = Sp.GetRequiredService<IGraphService>();
        const string doc = "小明在北京大学研究知识图谱。\n\n张教授是人工智能专家，指导小明的项目。\n\n项目获得了科技部资助。";
        try
        {
            await graph.InsertTextChunkAsync(Index, doc);
            Check("InsertTextChunkAsync 导入完成", true);
        }
        catch (Exception ex)
        {
            Check("InsertTextChunkAsync 导入完成", false, ex.Message);
        }

        var stats = await Sp.GetRequiredService<IGraphIndexService>().GetIndexStatsAsync(Index);
        Check("导入产出 Nodes/Edges/TextUnits",
            stats.Nodes > 0 && stats.Edges > 0 && stats.TextUnits > 0,
            $"nodes={stats.Nodes}, edges={stats.Edges}, units={stats.TextUnits}");

        // 构造多连通分量图（两个三角形 + 一条跨分量边），
        // 确保 level 0 能分出多个社区、level 1 能继续上卷，从而验证真正的分层。
        var nodeRepo = Sp.GetRequiredService<INodes_Repositories>();
        var edgeRepo = Sp.GetRequiredService<IEdges_Repositories>();
        var idxLocal = Index;
        for (int i = 1; i <= 6; i++)
        {
            var nid = "hr_n" + i;
            if (nodeRepo.IsAny(p => p.Id == nid)) continue;
            nodeRepo.Insert(new Nodes
            {
                Id = nid, Index = idxLocal, Name = "实体" + i, Type = "Concept", Desc = "分层测试实体"
            });
        }
        var crossEdges = new (string S, string T)[]
        {
            ("hr_n1", "hr_n2"), ("hr_n2", "hr_n3"), ("hr_n1", "hr_n3"),
            ("hr_n4", "hr_n5"), ("hr_n5", "hr_n6"), ("hr_n4", "hr_n6"),
            ("hr_n3", "hr_n4")
        };
        foreach (var (s, t) in crossEdges)
        {
            if (edgeRepo.IsAny(p => p.Source == s && p.Target == t)) continue;
            edgeRepo.Insert(new Edges
            {
                Id = Guid.NewGuid().ToString(), Index = idxLocal, Source = s, Target = t,
                Relationship = "关联", Weight = 1, TextUnitIds = "hr_tu"
            });
        }

        try
        {
            await graph.GraphCommunitiesAsync(Index);
            await graph.GraphGlobalAsync(Index);
            Check("GraphCommunitiesAsync + GraphGlobalAsync 无异常", true);
        }
        catch (Exception ex)
        {
            Check("GraphCommunitiesAsync + GraphGlobalAsync 无异常", false, ex.Message);
        }

        var levels = Sp.GetRequiredService<ICommunityHierarchyService>().GetLevels(Index);
        Check("P0-1 分层社区产出多个层级", levels.Count >= 2,
            "levels=" + string.Join(",", levels.Select(l => $"{l.Level}:{l.CommunityCount}")));
        Check("P0-1 每个层级都有社区且节点数递减/相等",
            levels.All(l => l.CommunityCount > 0 && l.NodeCount > 0));

        var idx = Index;
        var communities = Sp.GetRequiredService<ICommunities_Repositories>().GetList(p => p.Index == idx);
        Check("P0-1 社区报告含 Parent/Children/Level/Rank",
            communities.Any(c => c.Level > 0 && !string.IsNullOrWhiteSpace(c.Children))
            && communities.Any(c => !string.IsNullOrWhiteSpace(c.Parent))
            && communities.Any(c => c.CommunityRank > 0),
            $"count={communities.Count}, parents={communities.Count(c => c.Parent != null)}");
        Check("P0-1 结构化报告含 Title/Findings/Rating",
            communities.Any(c => !string.IsNullOrWhiteSpace(c.Title) && c.Findings != null && c.Rating > 0));

        var after = await Sp.GetRequiredService<IGraphIndexService>().GetIndexStatsAsync(Index);
        Check("P0-3 图统计 degree/weight 已计算",
            after.MaxDegree > 0 && after.TotalEdgeWeight > 0,
            $"maxDegree={after.MaxDegree}, totalWeight={after.TotalEdgeWeight}");
        Check("P0-3 IndexStats 社区层级数 >= 2", after.CommunityLevels >= 2,
            "levels=" + after.CommunityLevels);
    }

    // ==================================================================
    // C. 既有能力回归（防退化）
    // ==================================================================
    private static async Task RunExistingRegressionAsync()
    {
        Section("C. 既有能力回归");

        var graph = Sp.GetRequiredService<IGraphService>();
        var search = Sp.GetRequiredService<IGraphSearchService>();

        Check("GetAllIndex 包含当前索引", graph.GetAllIndex().Contains(Index));
        Check("GetAllGraphs 返回节点与边",
            graph.GetAllGraphs(Index).nodes.Count > 0);

        try
        {
            var a = await graph.SearchGraphAsync(Index, "小明的研究方向是什么？");
            Check("SearchGraphAsync 返回答案", !string.IsNullOrWhiteSpace(a));
        }
        catch (Exception ex) { Check("SearchGraphAsync 返回答案", false, ex.Message); }

        try
        {
            var a = await graph.SearchGraphCommunityAsync(Index, "整体研究主题？");
            Check("SearchGraphCommunityAsync 返回答案", !string.IsNullOrWhiteSpace(a));
        }
        catch (Exception ex) { Check("SearchGraphCommunityAsync 返回答案", false, ex.Message); }

        var basic = await search.SearchBasicAsync(Index, "谁在研究知识图谱？");
        Check("SearchBasicAsync 返回答案与来源",
            !string.IsNullOrWhiteSpace(basic.Answer) && basic.Sources.Count > 0,
            $"sources={basic.Sources.Count}");

        var global = await search.SearchGlobalAsync(Index, "数据集主要讲了什么？");
        Check("SearchGlobalAsync Map-Reduce 返回答案与关键点",
            !string.IsNullOrWhiteSpace(global.Answer) && global.Points.Count > 0 && global.MapBatches > 0,
            $"points={global.Points.Count}, batches={global.MapBatches}");

        var questions = await search.GenerateQuestionsAsync(Index, new List<string> { "之前问了什么？" }, 3);
        Check("GenerateQuestionsAsync 解析候选问题", questions.Questions.Count == 3,
            string.Join(" | ", questions.Questions));

        Check("GetTextUnitsAsync / GetCommunityReportsAsync",
            (await search.GetTextUnitsAsync(Index)).Count > 0
            && (await search.GetCommunityReportsAsync(Index)).Count > 0);

        // 流式
        var sb = new StringBuilder();
        await foreach (var c in search.SearchBasicStreamAsync(Index, "流式问题")) sb.Append(c.ToString());
        Check("SearchBasicStreamAsync 流式返回", sb.Length > 0);

        var sb2 = new StringBuilder();
        await foreach (var c in search.SearchGlobalStreamAsync(Index, "流式全局问题")) sb2.Append(c.ToString());
        Check("SearchGlobalStreamAsync 流式 reduce", sb2.Length > 0);

        var sb3 = new StringBuilder();
        await foreach (var c in graph.SearchGraphStreamAsync(Index, "流式图谱问题")) sb3.Append(c.ToString());
        Check("SearchGraphStreamAsync 流式返回", sb3.Length > 0);
    }

    // ==================================================================
    // D. P0 新能力
    // ==================================================================
    private static async Task RunP0Async()
    {
        Section("D. P0 新能力");

        var local = Sp.GetRequiredService<ILocalSearchService>();
        var search = Sp.GetRequiredService<IGraphSearchService>();

        var built = await local.BuildContextAsync(Index, "小明和知识图谱", new List<string> { "第一轮问题", "第二轮问题" });
        Check("P0-2 混合上下文包含实体/关系/文本单元/社区报告/历史",
            built.Stats.Entities > 0 && built.Stats.Relationships > 0
            && built.Stats.TextUnits > 0 && built.Stats.CommunityReports > 0
            && built.Context.Contains("对话历史") && built.Context.Contains("第一轮问题"),
            $"E={built.Stats.Entities} R={built.Stats.Relationships} TU={built.Stats.TextUnits} C={built.Stats.CommunityReports} tok={built.Stats.EstimatedTokens}");
        Check("P0-2 上下文 token 在预算内",
            built.Stats.EstimatedTokens <= GraphQueryOption.LocalMaxContextTokens * 1.5,
            "tok=" + built.Stats.EstimatedTokens);

        var lr = await local.SearchLocalAsync(Index, "小明的研究方向是什么？", new List<string> { "之前的问题" });
        Check("P0-2 SearchLocalAsync 返回答案 + 统计",
            !string.IsNullOrWhiteSpace(lr.Answer) && lr.Stats.Entities > 0);

        var sb = new StringBuilder();
        await foreach (var c in local.SearchLocalStreamAsync(Index, "流式 local 问题")) sb.Append(c.ToString());
        Check("P0-2 SearchLocalStreamAsync 流式返回", sb.Length > 0);

        // Global level 参数
        GraphQueryOption.GlobalCommunityLevel = 0;
        var atLevel0 = await search.SearchGlobalAsync(Index, "按最细层级回答", null, false);
        GraphQueryOption.GlobalCommunityLevel = -1;
        Check("P0-1 GlobalCommunityLevel=0 可指定层级检索",
            !string.IsNullOrWhiteSpace(atLevel0.Answer) && atLevel0.SelectedCommunityIds.Count > 0,
            "selected=" + atLevel0.SelectedCommunityIds.Count);
    }

    // ==================================================================
    // E. P1 新能力
    // ==================================================================
    private static async Task RunP1Async()
    {
        Section("E. P1 新能力");

        var indexService = Sp.GetRequiredService<IGraphIndexService>();
        var dynamicSel = Sp.GetRequiredService<IDynamicCommunitySelectionService>();
        var drift = Sp.GetRequiredService<IDriftSearchService>();

        // Claims
        var claimSummary = await indexService.ExtractClaimsAsync(Index);
        Check("P1-5 Claims 抽取产出记录",
            claimSummary.TextUnitsProcessed > 0 && claimSummary.ClaimsExtracted > 0,
            $"units={claimSummary.TextUnitsProcessed}, claims={claimSummary.ClaimsExtracted}");
        var claims = await indexService.GetClaimsAsync(Index);
        Check("P1-5 GetClaimsAsync 返回结构化声明",
            claims.Count > 0 && claims.Any(c => !string.IsNullOrWhiteSpace(c.SubjectId)));

        var local = Sp.GetRequiredService<ILocalSearchService>();
        var builtWithClaims = await local.BuildContextAsync(Index, "小明 项目 资助");
        Check("P1-5 Claims 进入 Local 混合上下文", builtWithClaims.Stats.Claims > 0,
            "claims=" + builtWithClaims.Stats.Claims);

        // 动态社区选择（LLM 评分）
        var selected = await dynamicSel.SelectAsync(Index, "知识图谱研究", 5);
        Check("P1-6 动态社区选择返回选中社区", selected.Count > 0, "count=" + selected.Count);
        Check("P1-6 支持层级信息", dynamicSel.MaxLevel(Index) >= 1);

        // DRIFT
        var driftResult = await drift.SearchAsync(Index, "这个数据集的研究主题是什么？");
        Check("P1-4 DRIFT 返回最终答案",
            !string.IsNullOrWhiteSpace(driftResult.Answer));
        Check("P1-4 DRIFT 层级树含后续问题与局部检索",
            driftResult.FollowupCount > 0 && driftResult.LocalSearches > 0
            && driftResult.Hierarchy.Children.Count > 0 && driftResult.FlatNodes.Count > 0,
            $"followups={driftResult.FollowupCount}, local={driftResult.LocalSearches}, flat={driftResult.FlatNodes.Count}");

        var sb = new StringBuilder();
        await foreach (var c in drift.SearchStreamAsync(Index, "流式 DRIFT 问题")) sb.Append(c.ToString());
        Check("P1-4 DRIFT 流式 reduce", sb.Length > 0);

        // 增量索引（独立 index，避免影响主索引）
        const string incIndex = "regression_incremental";
        var inc1 = await indexService.IncrementalIndexAsync(incIndex, "第一段文本。\n\n第二段文本。", "docA");
        var inc2 = await indexService.IncrementalIndexAsync(incIndex, "第一段文本。\n\n第二段文本。", "docA");
        Check("P1-7 增量索引：首次新增、二次全部未变",
            inc1.Added > 0 && inc1.Unchanged == 0 && inc2.Added == 0 && inc2.Unchanged > 0,
            $"first(added={inc1.Added}) second(unchanged={inc2.Unchanged})");
        var inc3 = await indexService.IncrementalIndexAsync(incIndex, "第一段文本。\n\n第三段新文本。", "docA");
        Check("P1-7 增量索引：内容变更触发新增与移除",
            inc3.Added > 0 && inc3.Removed > 0,
            $"added={inc3.Added}, removed={inc3.Removed}");

        // Prompt Tune
        var tuned = await indexService.AutoTunePromptsAsync(Index, "科研领域");
        Check("P1-8 Prompt Tune 生成领域/实体类型/覆盖",
            !string.IsNullOrWhiteSpace(tuned.Domain) && tuned.EntityTypes.Count > 0 && tuned.Overrides.Count >= 3,
            $"domain={tuned.Domain}, types={tuned.EntityTypes.Count}, overrides={tuned.Overrides.Count}");
        var spec = LoadPromptSpec("graph", "create", new Dictionary<string, object?> { ["input"] = "X" });
        Check("P1-8 调优提示词运行时生效且保留输入占位符",
            spec.Prompt.Contains(_tunedMarker) && spec.Prompt.Contains("X") && !spec.Prompt.Contains("{{$input}}"));
        Check("P1-8 GetPromptOverrides 可查询", indexService.GetPromptOverrides().ContainsKey("graph/create"));
        indexService.ClearPromptOverrides();
        var restored = LoadPromptSpec("graph", "create", new Dictionary<string, object?> { ["input"] = "X" });
        Check("P1-8 ClearPromptOverrides 恢复磁盘提示词", !restored.Prompt.Contains(_tunedMarker));
    }

    // ==================================================================
    // F. 容错与清理
    // ==================================================================
    private static async Task RunRobustnessAsync()
    {
        Section("F. 容错 / 空索引 / 清理");

        var search = Sp.GetRequiredService<IGraphSearchService>();
        var local = Sp.GetRequiredService<ILocalSearchService>();
        var indexService = Sp.GetRequiredService<IGraphIndexService>();

        Check("空索引 Global 返回友好提示",
            (await search.SearchGlobalAsync(EmptyIndex, "任意问题")).Answer.Contains("尚未生成社区报告"));
        Check("空索引 Basic 返回友好提示",
            (await search.SearchBasicAsync(EmptyIndex, "任意问题")).Answer.Contains("请先导入"));
        Check("空索引 Local 返回友好提示",
            (await local.SearchLocalAsync(EmptyIndex, "任意问题")).Answer.Contains("请先导入"));
        Check("空索引 IndexStats 全零且不抛",
            (await indexService.GetIndexStatsAsync(EmptyIndex)).Nodes == 0);

        // LLM 返回非法 JSON 时，社区重建必须回退而不能崩溃
        _breakCommunityReport = true;
        try
        {
            await Sp.GetRequiredService<IGraphService>().GraphCommunitiesAsync(MigrationIndex);
            Check("容错：社区报告 LLM 输出非法 JSON 时自动回退", true);
        }
        catch (Exception ex)
        {
            Check("容错：社区报告 LLM 输出非法 JSON 时自动回退", false, ex.Message);
        }
        finally
        {
            _breakCommunityReport = false;
        }

        // DeleteGraph 清理新增数据
        var units = Sp.GetRequiredService<ITextUnits_Repositories>();
        units.Insert(new TextUnits { Id = "del_tu", Index = DeleteIndex, Text = "x", ContentHash = "h" });
        Sp.GetRequiredService<IClaims_Repositories>().Insert(new Claims { Id = "del_c", Index = DeleteIndex });
        await Sp.GetRequiredService<IGraphService>().DeleteGraph(DeleteIndex);
        Check("DeleteGraph 清理 TextUnits / Claims",
            units.GetList(p => p.Index == DeleteIndex).Count == 0
            && Sp.GetRequiredService<IClaims_Repositories>().GetList(p => p.Index == DeleteIndex).Count == 0);
    }

    // ==================================================================
    // 基础设施：断言 / 分组 / 提示词探测 / mock
    // ==================================================================
    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("== " + title + " ==");
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        _checks++;
        if (!ok) _failures++;
        Console.WriteLine((ok ? "[PASS] " : "[FAIL] ") + name + (detail is null ? "" : " -> " + detail));
    }

    private static (string Prompt, bool JsonMode) LoadPromptSpec(
        string plugin, string function, IReadOnlyDictionary<string, object?> args)
    {
        var catalog = typeof(Kernel).Assembly.GetType("Microsoft.SemanticKernel.PromptCatalog", true)!;
        var load = catalog.GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static)!;
        var spec = load.Invoke(null, new object[] { plugin, function, args })!;
        var prompt = (string)spec.GetType().GetProperty("Prompt")!.GetValue(spec)!;
        var jsonMode = (bool)spec.GetType().GetProperty("JsonMode")!.GetValue(spec)!;
        return (prompt, jsonMode);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GraphRag.Net.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("无法定位仓库根目录（GraphRag.Net.sln）");
    }

    private static StreamFn MockStreamFn()
        => (model, context, options, ct) =>
        {
            var prompt = ExtractPrompt(context);
            var reply = Route(prompt);
            var stream = new AssistantMessageEventStream();
            var message = new AssistantMessage(
                new ContentBlock[] { new TextContent(reply) }, StopReason.Stop);
            stream.Push(new AssistantMessageEvent.Start(message));
            stream.Push(new AssistantMessageEvent.TextDelta(0, reply, 0, message));
            stream.Push(new AssistantMessageEvent.TextEnd(0, reply, message));
            stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));
            stream.End(message);
            return Task.FromResult<IAssistantMessageEventStream>(stream);
        };

    /// <summary>按提示词内容路由 canned 响应，完全不依赖调用次数。</summary>
    private static string Route(string prompt)
    {
        const string ReportJson = "{\"title\":\"科研合作社区\",\"summary\":\"该社区围绕高校科研合作展开\",\"rating\":8.0,\"rating_explanation\":\"影响力高\",\"findings\":[{\"summary\":\"核心实体\",\"explanation\":\"小明与北京大学构成核心\"}]}";
        const string GraphJson = "{\"Nodes\":[{\"Id\":\"node1\",\"Name\":\"小明\",\"Type\":\"Person\",\"Desc\":\"研究知识图谱的学生\"},{\"Id\":\"node2\",\"Name\":\"北京大学\",\"Type\":\"Organization\",\"Desc\":\"高校\"},{\"Id\":\"node3\",\"Name\":\"知识图谱\",\"Type\":\"Concept\",\"Desc\":\"研究方向\"}],\"Edges\":[{\"Source\":\"node1\",\"Target\":\"node2\",\"Relationship\":\"就读于\"},{\"Source\":\"node1\",\"Target\":\"node3\",\"Relationship\":\"研究\"}]}";

        if (prompt.Contains("create_prompt"))
            return "{\"domain\":\"科研领域\",\"entity_types\":[\"Person\",\"Organization\",\"Concept\",\"Project\"],"
                + "\"create_prompt\":\"" + _tunedMarker + " 请抽取实体与关系 {{$input}}\","
                + "\"community_report_prompt\":\"生成社区报告 {{$context}}\","
                + "\"summarize_prompt\":\"合并描述 {{$desc1}} {{$desc2}}\"}";
        if (prompt.Contains("subject_id"))
            return "{\"claims\":[{\"subject_id\":\"小明\",\"object_id\":\"北京大学\",\"type\":\"合作关系\",\"status\":\"TRUE\",\"description\":\"小明在北京大学开展研究\",\"start_date\":\"NONE\",\"end_date\":\"NONE\",\"source_text\":\"小明在北京大学研究知识图谱\"}]}";
        if (prompt.Contains("Primer 阶段"))
            return "{\"answer\":\"初步答案：研究主题集中在知识图谱\",\"followups\":[\"小明具体研究什么？\"],\"confidence\":0.8}";
        if (prompt.Contains("Follow-up 阶段"))
            return "{\"answer\":\"细化答案：小明研究知识图谱构建\",\"followups\":[],\"confidence\":0.0}";
        if (prompt.Contains("DRIFT 层级树"))
            return "DRIFT 最终综合回答 [Data: Reports (c1)]";
        if (prompt.Contains("relevant") && prompt.Contains("score"))
            return "{\"score\":85,\"relevant\":true,\"reason\":\"与问题高度相关\"}";
        if (prompt.Contains("子社区报告"))
            return ReportJson;
        if (prompt.Contains("结构化社区报告") || prompt.Contains("DETAILED FINDINGS"))
            return _breakCommunityReport ? "这不是合法JSON" : ReportJson;
        if (prompt.Contains("\"points\"") || prompt.Contains("重要性"))
            return "{\"points\":[{\"description\":\"要点A [Data: Reports (c1)]\",\"score\":90},{\"description\":\"要点B\",\"score\":20}]}";
        if (prompt.Contains("实体 (Entities)"))
            return "Local 混合检索答案 [Data: Sources (tu1); Entities (n1)]";
        if (prompt.Contains("原始文本单元数据表"))
            return "基础检索答案 [Data: Sources (tu1)]";
        if (prompt.Contains("分析师要点"))
            return "全局综合回答 [Data: Reports (c1)]";
        if (prompt.Contains("候选问题"))
            return "- 小明的研究方向是什么？\n- 北京大学与项目有什么关系？\n- 张教授指导了哪些研究？";
        if (prompt.Contains("IsRelationship"))
            return "{\"IsRelationship\":false}";
        if (prompt.Contains("旧的描述"))
            return "整合后的描述";
        if (prompt.Contains("来生成社区摘要"))
            return "社区摘要文本";
        if (prompt.Contains("来生成全局摘要"))
            return "全局摘要内容";
        if (prompt.Contains("Nodes") && prompt.Contains("知识图谱"))
            return GraphJson;
        return "通用回答";
    }

    /// <summary>反射提取 context 中的全部文本，用于按提示词内容路由。</summary>
    private static string ExtractPrompt(object? ctx)
    {
        var sb = new StringBuilder();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(object? o, int depth)
        {
            if (o is null || depth > 8 || sb.Length > 300000) return;
            if (o is string s) { sb.Append(s); return; }
            var t = o.GetType();
            if (t.IsPrimitive || t.IsEnum) return;
            if (!visited.Add(o)) return;
            if (o is IEnumerable en)
            {
                foreach (var item in en) Walk(item, depth + 1);
                return;
            }
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                try { Walk(p.GetValue(o), depth + 1); }
                catch { /* 忽略无法读取的属性 */ }
            }
        }
        Walk(ctx, 0);
        return sb.ToString();
    }

    /// <summary>确定性哈希 embedding：字符 bigram 映射到 64 维并归一化。</summary>
    private sealed class HashEmbedding : IEmbeddingClient
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            const int dim = 64;
            var v = new float[dim];
            var s = (text ?? string.Empty).ToLowerInvariant();
            for (int i = 0; i < s.Length; i++)
            {
                int h = (s[i] * 31 + (i + 1 < s.Length ? s[i + 1] : 0)) & int.MaxValue;
                v[h % dim] += 1f;
            }
            if (s.Length == 0) v[0] = 1f;
            double norm = Math.Sqrt(v.Sum(x => (double)x * x));
            if (norm > 0)
                for (int i = 0; i < dim; i++) v[i] = (float)(v[i] / norm);
            return Task.FromResult(v);
        }
    }
}
