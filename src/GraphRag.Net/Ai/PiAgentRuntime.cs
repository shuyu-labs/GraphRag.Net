// -----------------------------------------------------------------------------
// PiAgentRuntime.cs — real pi.net agent loop (循环决策 / 多步骤执行 / 结果迭代)
//
// Exposed as Kernel.RunAgentAsync so business services keep using the injected
// Kernel type. Each run builds a Pi.Agent.Agent with a StreamFn (pi.net LLM
// boundary) + executable AgentTools; the loop keeps taking turns until the model
// stops calling tools or MaxTurns is reached. Every tool result is fed back into
// the transcript, enabling iterative refinement.
// -----------------------------------------------------------------------------
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Agent;
using Pi.Agent.Types;
using Pi.Ai.Types;

namespace Microsoft.SemanticKernel
{
    /// <summary>Declarative tool exposed to the agent loop.</summary>
    public sealed class AgentToolSpec
    {
        public required string Name { get; init; }
        public required string Description { get; init; }
        public IReadOnlyDictionary<string, object?> Parameters { get; init; } =
            new Dictionary<string, object?>();
        public required Func<JsonElement, CancellationToken, Task<string>> Execute { get; init; }

        /// <summary>End the run immediately after this tool executes.</summary>
        public bool Terminate { get; init; }
    }

    public sealed class AgentRequest
    {
        public string SystemPrompt { get; init; } = string.Empty;
        public string UserPrompt { get; init; } = string.Empty;
        public int MaxTurns { get; init; } = 6;
        public double Temperature { get; init; } = 0.2;
        public IReadOnlyList<AgentToolSpec> Tools { get; init; } = Array.Empty<AgentToolSpec>();
        public Action<AgentStep>? OnStep { get; init; }
        public Action<string>? OnTextDelta { get; init; }
    }

    public sealed record AgentStep(string Kind, string Name, string Detail);

    public sealed class AgentRunResult
    {
        public string FinalText { get; internal set; } = string.Empty;
        public List<AgentStep> Steps { get; } = new();
        public int Turns { get; internal set; }
        public bool Success { get; internal set; }
    }

    public sealed partial class Kernel
    {
        public async Task<AgentRunResult> RunAgentAsync(
            AgentRequest request, CancellationToken cancellationToken = default)
        {
            var result = new AgentRunResult();
            var allText = new StringBuilder();
            string? finalText = null;

            var tools = request.Tools.Select(spec => new AgentTool(
                Name: spec.Name,
                Description: spec.Description,
                Parameters: new ToolSchema(spec.Parameters),
                Label: spec.Name,
                Execute: async (_, args, ct, _) =>
                {
                    var json = JsonSerializer.SerializeToElement(args);
                    try
                    {
                        var output = await spec.Execute(json, ct).ConfigureAwait(false);
                        return new AgentToolResult(
                            new ContentBlock[] { new TextContent(output ?? string.Empty) },
                            Terminate: spec.Terminate);
                    }
                    catch (Exception ex)
                    {
                        return AgentToolResult.FromError(ex.Message);
                    }
                })).ToList();

            var agent = new Agent(new AgentOptions
            {
                SystemPrompt = request.SystemPrompt,
                Model = _model,
                StreamFn = _streamFn,
                Tools = tools,
                ToolExecution = ToolExecutionMode.Sequential,
                ThinkingLevel = ThinkingLevel.Off,
                GetApiKey = _ => Task.FromResult<string?>(_apiKey),
                // Agent 循环自身不带 Temperature 选项；经 OnPayload 把每轮请求的
                // 温度注入 pi.net 的请求体，使 AgentRequest.Temperature 真正生效。
                OnPayload = (payload, _) =>
                {
                    payload["temperature"] = request.Temperature;
                    return Task.FromResult<JsonObject?>(payload);
                },
                FinishTurn = (turnCtx, _) =>
                {
                    result.Turns++;
                    if (result.Turns >= Math.Max(1, request.MaxTurns))
                        return Task.FromResult<AgentTurnDecision?>(new AgentTurnDecision.End());
                    return Task.FromResult<AgentTurnDecision?>(null);
                }
            });

            using var subscription = agent.Subscribe((evt, _) =>
            {
                switch (evt)
                {
                    case AgentEvent.MessageUpdate { AssistantMessageEvent: AssistantMessageEvent.TextDelta delta }:
                        allText.Append(delta.Delta);
                        request.OnTextDelta?.Invoke(delta.Delta);
                        break;
                    case AgentEvent.ToolExecutionStart start:
                        var step = new AgentStep("tool_call", start.ToolName, JsonSerializer.Serialize(start.Args));
                        result.Steps.Add(step);
                        request.OnStep?.Invoke(step);
                        break;
                    case AgentEvent.ToolExecutionEnd end:
                        var done = new AgentStep("tool_result", end.ToolName, end.IsError ? "error" : "ok");
                        result.Steps.Add(done);
                        request.OnStep?.Invoke(done);
                        break;
                    case AgentEvent.AgentEnd finish:
                        // 取最后一条「有正文」的助手消息，避免终止工具轮次只剩
                        // toolCall（无文本）时误把中间推理全文当成最终答案。
                        finalText = finish.Messages
                            .OfType<AssistantMessage>()
                            .Select(m => string.Concat(m.Content.OfType<TextContent>().Select(b => b.Text)))
                            .LastOrDefault(t => !string.IsNullOrWhiteSpace(t));
                        break;
                }
                return Task.CompletedTask;
            });

            try
            {
                using var registration = cancellationToken.Register(agent.Abort);
                // Agent.Prompt runs the full loop (all tool rounds) before returning.
                await agent.Prompt(request.UserPrompt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result.Steps.Add(new AgentStep("error", "agent", ex.Message));
            }

            result.FinalText = (finalText ?? allText.ToString()).Trim();
            result.Success = result.FinalText.Length > 0;
            return result;
        }
    }
}
