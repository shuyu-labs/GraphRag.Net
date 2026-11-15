using AntDesign;
using GraphRag.Net.Domain.Interface;
using Microsoft.AspNetCore.Components;

namespace GraphRag.Net.Web.Pages.Graph
{
    public partial class Chat
    {
        [Inject] IGraphService _graphService { get; set; }
        [Inject] IGraphSearchService _graphSearchService { get; set; }
        [Inject] IMessageService _message { get; set; }
        private List<UploadFileItem> _fileList = [];
        private List<string> _indexList { get; set; }
        private bool loadding = false;
        private string _index;
        private string _input;
        private string _output;

        private string _importIndex;
        private string _importText;

        // 对齐 microsoft/graphrag 的四种检索模式
        private string _searchMode = "local";
        private List<SearchModeItem> _modeList = new()
        {
            new("local", "节点搜索 (Local)"),
            new("community", "社区搜索 (Community)"),
            new("global", "全局搜索 (Global Map-Reduce)"),
            new("basic", "基础检索 (Basic RAG)")
        };
        private List<string> _questions = new();

        protected override async Task OnInitializedAsync()
        {
            await base.OnInitializedAsync();
            _indexList = _graphService.GetAllIndex();
        }

        /// <summary>按所选检索模式分发查询。</summary>
        private async Task SearchByMode()
        {
            if (string.IsNullOrEmpty(_index) || string.IsNullOrWhiteSpace(_input))
            {
                _ = _message.Warning("请先选择索引并输入问题");
                return;
            }

            loadding = true;
            try
            {
                _output = _searchMode switch
                {
                    "global" => (await _graphSearchService.SearchGlobalAsync(_index, _input)).Answer,
                    "basic" => (await _graphSearchService.SearchBasicAsync(_index, _input)).Answer,
                    "community" => await _graphService.SearchGraphCommunityAsync(_index, _input),
                    _ => await _graphService.SearchGraphAsync(_index, _input)
                };
            }
            catch (Exception ex)
            {
                _ = _message.Error($"检索失败：{ex.Message}");
            }
            finally
            {
                loadding = false;
            }
        }

        /// <summary>基于数据集主题生成候选追问。</summary>
        private async Task GenerateQuestionsAsync()
        {
            if (string.IsNullOrEmpty(_index))
            {
                _ = _message.Warning("请先选择索引");
                return;
            }
            var history = string.IsNullOrWhiteSpace(_input) ? null : new List<string> { _input };
            var result = await _graphSearchService.GenerateQuestionsAsync(_index, history);
            _questions = result.Questions;
        }

        private void UseQuestion(string question) => _input = question;

        // 保留旧入口，避免外部引用失效
        private async Task Search() => await SearchByMode();
        private async Task Search1()
        {
            _searchMode = "community";
            await SearchByMode();
        }

        private void OnSelectedItemChangedHandler(string value)
        {
            _index = value;
        }


        private bool BeforeUpload(UploadFileItem file)
        {
            if (string.IsNullOrEmpty(_importIndex))
            {
                _message.Error("请先填写index");
                return false;
            }
            if (file.Type != "text/plain")
            {
                _message.Error("文件格式错误,请重新选择!");
                return false;
            }
            var IsLt500K = file.Size < 1024 * 1024 * 100;
            if (!IsLt500K)
            {
                _message.Error("文件需不大于100MB!");
                return false;
            }

            return true;
        }

        private void OnSingleCompleted(UploadInfo fileinfo)
        {
            _indexList = _graphService.GetAllIndex();
            _message.Info("导入完成");
        }

        private async Task InputText()
        {
            if (string.IsNullOrEmpty(_importIndex))
            {
                _message.Error("请先填写index");
            }
            await _graphService.InsertGraphDataAsync(_importIndex, _importText);

            //生成社区报告与全局摘要
            await _graphService.GraphCommunitiesAsync(_importIndex);
            await _graphService.GraphGlobalAsync(_importIndex);

            _indexList = _graphService.GetAllIndex();
            _message.Info("导入完成");
        }
    }

    public record SearchModeItem(string Value, string Label);
}
