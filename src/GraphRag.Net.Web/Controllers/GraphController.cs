using GraphRag.Net.Domain.Interface;
using GraphRag.Net.Domain.Model.Graph;
using Microsoft.AspNetCore.Mvc;

namespace GraphRag.Net.Api.Controllers
{
    [Route("api/[controller]/[action]")]
    [ApiController]
    public class GraphController(
        IGraphService _graphService,
        IGraphSearchService _graphSearchService,
        ILocalSearchService _localSearchService,
        IDriftSearchService _driftSearchService,
        IGraphIndexService _graphIndexService,
        ICommunityHierarchyService _communityHierarchyService) : ControllerBase
    {
        /// <summary>
        /// 获取所有的索引数据
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public async Task<IActionResult> GetAllIndex()
        {
            var graphModel = _graphService.GetAllIndex();
            return Ok(graphModel);
        }


        /// <summary>
        /// 获取所有的图谱数据
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        [HttpGet]
        public async Task<IActionResult> GetAllGraphs(string index)
        {
            if (string.IsNullOrEmpty(index))
            {
                return Ok(new GraphViewModel());
            }
            var graphModel = _graphService.GetAllGraphs(index);
            return Ok(graphModel);
        }


        /// <summary>
        /// 插入文本数据
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        [HttpPost]
        public async Task<IActionResult> InsertGraphData(InputModel model)
        {
            await _graphService.InsertGraphDataAsync(model.Index, model.Input);
            return Ok();
        }

        /// <summary>
        /// 搜索递归获取节点相关的所有边和节点进行图谱对话
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        [HttpPost]
        public async Task<IActionResult> SearchGraph(InputModel model)
        {
            var result = await _graphService.SearchGraphAsync(model.Index, model.Input);
            return Ok(result);
        }

        /// <summary>
        /// 通过社区算法检索社区节点进行对话
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        [HttpPost]
        public async Task<IActionResult> SearchGraphCommunity(InputModel model)
        {
            var result = await _graphService.SearchGraphCommunityAsync(model.Index, model.Input);
            return Ok(result);
        }

        /// <summary>
        /// 导入txt文档
        /// </summary>
        /// <param name="index"></param>
        /// <param name="file"></param>
        /// <returns></returns>
        [HttpPost]
        public async Task<IActionResult> ImportTxt(string index,IFormFile file)
        {
            var forms = await Request.ReadFormAsync();
            using (var stream = new StreamReader(file.OpenReadStream()))
            {
                var txt = await stream.ReadToEndAsync();
                await _graphService.InsertTextChunkAsync(index,txt);
                return Ok();
            }
        }

        /// <summary>
        /// 通过社区检测生成社区和摘要
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        [HttpGet]
        public async Task<IActionResult> GraphCommunities(string index)
        {
            await _graphService.GraphCommunitiesAsync(index);
            return Ok();
        }      
        
        /// <summary>
        /// 通过社区摘要生成全局摘要
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        [HttpGet]
        public async Task<IActionResult> GraphGlobal(string index)
        {
            await _graphService.GraphGlobalAsync(index);
            return Ok();
        }

        /// <summary>
        /// 删除图谱数据
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        [HttpGet]
        public async Task<IActionResult> DeleteGraph(string index)
        {
            await _graphService.DeleteGraph(index);
            return Ok();
        }

        /// <summary>
        /// 基础向量检索（Basic Search）：直接基于原始文本单元回答，返回可追溯来源。
        /// 对应 microsoft/graphrag 的 basic_search，可作为图谱检索的基线与兜底。
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> SearchBasic(InputModel model, [FromQuery] int? topK = null)
        {
            var result = await _graphSearchService.SearchBasicAsync(model.Index, model.Input, topK);
            return Ok(result);
        }

        /// <summary>
        /// 全局检索（Global Search）：动态社区选择 → map 关键点打分 → reduce 汇总。
        /// 对应 microsoft/graphrag 的 global_search。
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> SearchGlobal(
            InputModel model,
            [FromQuery] string? responseType = null,
            [FromQuery] bool? dynamicSelection = null)
        {
            var result = await _graphSearchService.SearchGlobalAsync(
                model.Index, model.Input, responseType, dynamicSelection);
            return Ok(result);
        }

        /// <summary>
        /// 生成候选追问（Question Generation）。
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> GenerateQuestions(QuestionModel model)
        {
            var result = await _graphSearchService.GenerateQuestionsAsync(
                model.Index, model.History, model.Count);
            return Ok(result);
        }

        /// <summary>
        /// 列出索引下的原始文本单元（溯源检视）。
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetTextUnits(string index)
        {
            return Ok(await _graphSearchService.GetTextUnitsAsync(index));
        }

        /// <summary>
        /// 列出索引下的结构化社区报告（title / rating / findings）。
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetCommunityReports(string index)
        {
            return Ok(await _graphSearchService.GetCommunityReportsAsync(index));
        }

        // ---- P0 / P1 新增端点 ----

        /// <summary>Local Search 混合上下文检索（entities + relationships + text units + reports + claims）。</summary>
        [HttpPost]
        public async Task<IActionResult> SearchLocal(LocalQueryModel model)
        {
            return Ok(await _localSearchService.SearchLocalAsync(model.Index, model.Input, model.History));
        }

        /// <summary>DRIFT Search（Primer → Follow-up → 层级归并）。</summary>
        [HttpPost]
        public async Task<IActionResult> SearchDrift(InputModel model)
        {
            return Ok(await _driftSearchService.SearchAsync(model.Index, model.Input));
        }

        /// <summary>抽取 Claims / Covariates。</summary>
        [HttpPost]
        public async Task<IActionResult> ExtractClaims(ClaimQueryModel model)
        {
            return Ok(await _graphIndexService.ExtractClaimsAsync(model.Index, model.Force));
        }

        /// <summary>列出索引下的 claims。</summary>
        [HttpGet]
        public async Task<IActionResult> GetClaims(string index)
        {
            return Ok(await _graphIndexService.GetClaimsAsync(index));
        }

        /// <summary>增量索引：按内容哈希只处理变化块。</summary>
        [HttpPost]
        public async Task<IActionResult> IncrementalIndex(IncrementalQueryModel model)
        {
            return Ok(await _graphIndexService.IncrementalIndexAsync(model.Index, model.Input, model.DocumentId));
        }

        /// <summary>自动提示词调优（结果写入运行时覆盖，立即生效）。</summary>
        [HttpPost]
        public async Task<IActionResult> AutoTunePrompts(TuneQueryModel model)
        {
            return Ok(await _graphIndexService.AutoTunePromptsAsync(model.Index, model.Domain));
        }

        /// <summary>查看当前生效的提示词覆盖。</summary>
        [HttpGet]
        public IActionResult GetPromptOverrides()
        {
            return Ok(_graphIndexService.GetPromptOverrides());
        }

        /// <summary>清除提示词覆盖，恢复磁盘提示词。</summary>
        [HttpGet]
        public IActionResult ClearPromptOverrides()
        {
            _graphIndexService.ClearPromptOverrides();
            return Ok();
        }

        /// <summary>索引统计（节点/边/文本单元/社区层级/claims/度数/权重）。</summary>
        [HttpGet]
        public async Task<IActionResult> GetIndexStats(string index)
        {
            return Ok(await _graphIndexService.GetIndexStatsAsync(index));
        }

        /// <summary>分层社区各层级统计。</summary>
        [HttpGet]
        public IActionResult GetCommunityLevels(string index)
        {
            return Ok(_communityHierarchyService.GetLevels(index));
        }
    }

    public class LocalQueryModel : InputModel
    {
        /// <summary>对话历史</summary>
        public List<string>? History { get; set; }
    }

    public class ClaimQueryModel
    {
        public string Index { get; set; }

        /// <summary>是否强制重抽</summary>
        public bool Force { get; set; }
    }

    public class IncrementalQueryModel : InputModel
    {
        /// <summary>文档标识，用于按文档做变更检测</summary>
        public string? DocumentId { get; set; }
    }

    public class TuneQueryModel
    {
        public string Index { get; set; }
        public string? Domain { get; set; }
    }

    public class InputModel
    {
        public string Index { get; set; }
        public string Input { get; set; }
    }

    public class QuestionModel
    {
        public string Index { get; set; }

        /// <summary>历史问题（对话上下文），可为空</summary>
        public List<string>? History { get; set; }

        /// <summary>期望生成的候选问题数量</summary>
        public int? Count { get; set; }
    }
}
