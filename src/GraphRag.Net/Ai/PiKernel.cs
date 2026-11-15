// -----------------------------------------------------------------------------
// PiKernel.cs — the pi.net-backed replacement for SK's Kernel.
//
// Re-declared under the original Microsoft.SemanticKernel namespace so that the
// existing DI registrations, constructors and public signatures stay untouched.
// All LLM traffic now goes through pi.net's OpenAiCompletions provider; the
// original endpoint-rewriting HttpClientHandler and the skprompt.txt / config.json
// prompt assets are preserved verbatim (configuration compatibility).
// -----------------------------------------------------------------------------
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GraphRag.Net.Options;
using GraphRag.Net.Utils;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using static Pi.Ai.Types.Messages;

namespace Microsoft.SemanticKernel
{
    /// <summary>
    /// pi.net gateway. Constructed by DI (<c>AddGraphRagNet</c>); injecting a custom
    /// <see cref="StreamFn"/> allows fully offline operation (mocks / tests).
    /// </summary>
    public sealed partial class Kernel
    {
        private readonly StreamFn _streamFn;
        private readonly Model _model;
        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly int _defaultMaxTokens;
        private readonly double _defaultTemperature;
        private readonly HttpClient _httpClient;

        // Kernel is registered as Transient; sharing one handler-backed client avoids
        // creating (and leaking) an HttpClient per resolution. The handler reads the
        // static options per request, so sharing is safe even if config changes.
        private static readonly HttpClient SharedHttpClient =
            new(new OpenAIHttpClientHandler()) { Timeout = TimeSpan.FromMinutes(10) };

        public Kernel() : this(null, null) { }

        public Kernel(StreamFn? streamFn = null, HttpClient? httpClient = null)
        {
            var chatModel = string.IsNullOrWhiteSpace(GraphOpenAIOption.ChatModel)
                ? "gpt-4o" : GraphOpenAIOption.ChatModel!;
            _model = new Model(chatModel, chatModel, "openai-completions", "openai");
            _apiKey = GraphOpenAIOption.Key ?? string.Empty;
            _baseUrl = NormalizeBaseUrl(GraphOpenAIOption.EndPoint);
            _defaultMaxTokens = 4096;
            _defaultTemperature = 0.1;
            _httpClient = httpClient ?? SharedHttpClient;
            _streamFn = streamFn ?? BuildStreamFn();
        }

        // ------------------------------------------------------------------
        // pi.net StreamFn: the single boundary between the agent loop and the LLM.
        // ------------------------------------------------------------------
        private StreamFn BuildStreamFn()
        {
            return (model, context, options, cancellationToken) =>
            {
                try
                {
                    var spec = new ModelSpec
                    {
                        Id = model.Id,
                        Name = model.Name,
                        Api = model.Api,
                        Provider = model.Provider,
                        BaseUrl = _baseUrl,
                        MaxTokens = Math.Max(4096, _defaultMaxTokens),
                        ContextWindow = 128_000
                    };

                    var effective = (options ?? new SimpleStreamOptions()) with
                    {
                        ApiKey = string.IsNullOrEmpty(options?.ApiKey) ? _apiKey : options.ApiKey,
                        BaseUrl = string.IsNullOrEmpty(options?.BaseUrl) ? _baseUrl : options.BaseUrl,
                        Temperature = options?.Temperature ?? _defaultTemperature,
                        MaxTokens = options?.MaxTokens ?? Math.Max(2048, _defaultMaxTokens)
                    };

                    var stream = OpenAiCompletions.StreamSimple(
                        spec, context, effective, _httpClient, cancellationToken);
                    return Task.FromResult(stream);
                }
                catch (Exception ex)
                {
                    // StreamFn contract: never throw / never return a faulted task.
                    return Task.FromResult(ErrorStream(ex.Message));
                }
            };
        }

        private static IAssistantMessageEventStream ErrorStream(string message)
        {
            var stream = new AssistantMessageEventStream();
            var final = new AssistantMessage(
                new ContentBlock[] { new TextContent(string.Empty) },
                StopReason.Error, ErrorMessage: message);
            stream.Push(new AssistantMessageEvent.Error(StopReason.Error, message, final));
            stream.End(final);
            return stream;
        }

        // ------------------------------------------------------------------
        // Single-shot completion (replaces Kernel.InvokeAsync / InvokePromptAsync).
        // ------------------------------------------------------------------
        public Task<string> InvokePromptAsync(
            string prompt,
            double temperature = 0.1,
            int maxTokens = 2048,
            bool jsonMode = false,
            CancellationToken cancellationToken = default)
            => CompleteAsync(prompt, temperature, maxTokens, jsonMode, cancellationToken);

        /// <summary>Renders a named prompt plugin (skprompt.txt) and runs it through pi.net.</summary>
        public Task<string> InvokePluginAsync(
            string plugin,
            string function,
            IReadOnlyDictionary<string, object?> args,
            double? temperature = null,
            bool? jsonMode = null,
            CancellationToken cancellationToken = default)
        {
            var spec = PromptCatalog.Load(plugin, function, args);
            return CompleteAsync(
                spec.Prompt,
                temperature ?? spec.Temperature,
                Math.Max(2048, _defaultMaxTokens),
                jsonMode ?? spec.JsonMode,
                cancellationToken);
        }

        private async Task<string> CompleteAsync(
            string prompt, double temperature, int maxTokens, bool jsonMode, CancellationToken cancellationToken)
        {
            var context = new TranscriptContext(new List<ChatMessage> { UserText(prompt) });
            var options = new SimpleStreamOptions(
                ApiKey: _apiKey,
                BaseUrl: _baseUrl,
                MaxTokens: maxTokens,
                Temperature: temperature);

            if (jsonMode)
            {
                options = options with
                {
                    OnPayload = (payload, _) =>
                    {
                        payload["response_format"] = new JsonObject { ["type"] = "json_object" };
                        return Task.FromResult<JsonObject?>(payload);
                    }
                };
            }

            var stream = await _streamFn(_model, context, options, cancellationToken).ConfigureAwait(false);
            return await ReadAllAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------
        // Streaming completion (replaces Kernel.InvokeStreamingAsync).
        // ------------------------------------------------------------------
        public async IAsyncEnumerable<StreamingKernelContent> InvokeStreamingAsync(
            string plugin,
            string function,
            IReadOnlyDictionary<string, object?> args,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var spec = PromptCatalog.Load(plugin, function, args);
            var context = new TranscriptContext(new List<ChatMessage> { UserText(spec.Prompt) });
            var options = new SimpleStreamOptions(
                ApiKey: _apiKey,
                BaseUrl: _baseUrl,
                MaxTokens: Math.Max(2048, _defaultMaxTokens),
                Temperature: spec.Temperature);

            var stream = await _streamFn(_model, context, options, cancellationToken).ConfigureAwait(false);
            await foreach (var evt in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (evt is AssistantMessageEvent.TextDelta delta)
                    yield return new StreamingKernelContent(delta.Delta);
            }
        }

        internal static async Task<string> ReadAllAsync(
            IAssistantMessageEventStream stream, CancellationToken cancellationToken)
        {
            var sb = new StringBuilder();
            AssistantMessage? final = null;
            string? error = null;

            await foreach (var evt in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case AssistantMessageEvent.TextDelta delta:
                        sb.Append(delta.Delta);
                        break;
                    case AssistantMessageEvent.Done done:
                        final = done.Message;
                        break;
                    case AssistantMessageEvent.Error err:
                        final = err.Message;
                        error = err.ErrorMessage;
                        break;
                }
            }

            if (!string.IsNullOrEmpty(error))
                throw new InvalidOperationException($"LLM request failed: {error}");

            // Some providers report failure only on the terminal message (Done with
            // StopReason=Error) without emitting an explicit Error event.
            if (final is not null && final.StopReason == StopReason.Error)
                throw new InvalidOperationException(
                    $"LLM request failed: {final.ErrorMessage ?? error ?? "unknown error"}");

            var text = sb.ToString();
            if (text.Length == 0 && final is not null)
                text = string.Concat(final.Content.OfType<TextContent>().Select(b => b.Text));
            return text.Trim();
        }

        private static string NormalizeBaseUrl(string? endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return "https://api.openai.com/v1";
            return endpoint.TrimEnd('/') + "/v1";
        }
    }

    /// <summary>Loads the existing SK-style prompt assets without any SK dependency.</summary>
    internal static class PromptCatalog
    {
        internal sealed record PromptSpec(string Prompt, double Temperature, bool JsonMode);

        private static readonly Regex Placeholder =
            new(@"\{\{\$?([A-Za-z0-9_]+)\}\}", RegexOptions.Compiled);

        internal static PromptSpec Load(string plugin, string function, IReadOnlyDictionary<string, object?> args)
        {
            string? template = null;
            double temperature = 0.1;
            bool jsonMode = false;

            // Prompt tuning 产出的覆盖模板优先；执行参数仍从原 config.json 读取。
            PromptOverrides.TryGet(plugin, function, out var overridden);

            try
            {
                var dir = Path.Combine(RepoFiles.SamplePluginsPath(), plugin, function);
                var promptPath = Path.Combine(dir, "skprompt.txt");
                if (File.Exists(promptPath)) template = File.ReadAllText(promptPath);

                var configPath = Path.Combine(dir, "config.json");
                if (File.Exists(configPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                    if (doc.RootElement.TryGetProperty("execution_settings", out var es)
                        && es.TryGetProperty("default", out var def))
                    {
                        if (def.TryGetProperty("temperature", out var t) && t.ValueKind == JsonValueKind.Number)
                            temperature = t.GetDouble();
                        if (def.TryGetProperty("response_format", out var rf)
                            && rf.TryGetProperty("type", out var type)
                            && type.GetString() == "json_object")
                            jsonMode = true;
                    }
                }
            }
            catch
            {
                // Plugin assets are optional at runtime: fall back to the argument payload.
            }

            if (!string.IsNullOrWhiteSpace(overridden)) template = overridden;
            template ??= BuildFallbackPrompt(args);
            return new PromptSpec(Render(template, args), temperature, jsonMode);
        }

        private static string BuildFallbackPrompt(IReadOnlyDictionary<string, object?> args)
        {
            var sb = new StringBuilder();
            sb.AppendLine("请严格按任务要求处理以下输入，并只输出结果：");
            foreach (var (key, value) in args)
                sb.AppendLine($"[{key}]\n{value}");
            return sb.ToString();
        }

        private static string Render(string template, IReadOnlyDictionary<string, object?> args)
            => Placeholder.Replace(template, match =>
                args.TryGetValue(match.Groups[1].Value, out var value)
                    ? value?.ToString() ?? string.Empty
                    : string.Empty);
    }
}

namespace GraphRag.Net.Options
{
    /// <summary>Optional additive configuration for the pi.net agent loop.</summary>
    public class PiAgentOption
    {
        /// <summary>Master switch; when false the classic single-prompt path is used.</summary>
        public static bool EnableAgentLoop { get; set; } = true;

        /// <summary>Safety cap on agent turns (loop decision iterations).</summary>
        public static int MaxTurns { get; set; } = 6;
    }
}
