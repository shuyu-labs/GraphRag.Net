// pi.net-based mock. Semantic Kernel's IChatCompletionService mock was removed
// with the SK migration; a StreamFn is now the only model boundary.
using Pi.Ai.Stream;
using Pi.Ai.Types;

namespace GraphRag.Net.Web.Mock
{
    public static class MockChatCompletion
    {
        /// <summary>
        /// A scripted pi.net StreamFn that always streams the given text.
        /// Inject it via <c>new Kernel(MockChatCompletion.CreateStreamFn("..."))</c>
        /// to run the whole agent loop without any API key.
        /// </summary>
        public static StreamFn CreateStreamFn(string? text = null)
        {
            return (model, context, options, cancellationToken) =>
            {
                var stream = new AssistantMessageEventStream();
                var last = context.Messages.LastOrDefault();
                var reply = text ?? $"这是一条Mock数据，便于聊天测试，你的消息是：{last}";
                var message = new AssistantMessage(
                    new ContentBlock[] { new TextContent(reply) }, StopReason.Stop);

                stream.Push(new AssistantMessageEvent.Start(message));
                stream.Push(new AssistantMessageEvent.TextDelta(0, reply, 0, message));
                stream.Push(new AssistantMessageEvent.TextEnd(0, reply, message));
                stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));
                stream.End(message);
                return Task.FromResult<IAssistantMessageEventStream>(stream);
            };
        }
    }
}
