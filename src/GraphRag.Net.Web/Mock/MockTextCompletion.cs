// pi.net-based mock (replaces the SK ITextGenerationService mock).
using Pi.Ai.Types;

namespace GraphRag.Net.Web.Mock
{
    public static class MockTextCompletion
    {
        public static StreamFn CreateStreamFn(string? text = null)
            => MockChatCompletion.CreateStreamFn(text);
    }
}
