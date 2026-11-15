using System.Collections.Concurrent;

namespace GraphRag.Net.Options
{
    /// <summary>
    /// 运行时提示词覆盖。对齐 microsoft/graphrag 的 prompt tuning 思路：
    /// 调优后的提示词写入此处，PromptCatalog 会优先使用覆盖模板，而保留
    /// 原 config.json 中的 temperature / json_mode 等执行参数。
    /// </summary>
    public static class PromptOverrides
    {
        private static readonly ConcurrentDictionary<string, string> Overrides =
            new(StringComparer.OrdinalIgnoreCase);

        private static string Key(string plugin, string function) => plugin + "/" + function;

        public static void Set(string plugin, string function, string prompt)
            => Overrides[Key(plugin, function)] = prompt ?? string.Empty;

        public static bool TryGet(string plugin, string function, out string prompt)
            => Overrides.TryGetValue(Key(plugin, function), out prompt!);

        public static bool Remove(string plugin, string function)
            => Overrides.TryRemove(Key(plugin, function), out _);

        public static void Clear() => Overrides.Clear();

        public static IReadOnlyDictionary<string, string> Snapshot()
            => new Dictionary<string, string>(Overrides, StringComparer.OrdinalIgnoreCase);
    }
}
