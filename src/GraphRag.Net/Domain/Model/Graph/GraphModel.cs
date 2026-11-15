using GraphRag.Net.Repositories;

namespace GraphRag.Net.Domain.Model.Graph
{
    public class GraphModel
    {
        // 默认空集合：检索无结果时调用方仍可安全遍历（避免 NullReferenceException）
        public List<Nodes> Nodes { get; set; } = new();
        public List<Edges> Edges { get; set; } = new();
    }
}
