using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic;
using Anthropic.Models;
using Anthropic.Models.Messages;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Services;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;

namespace HomelabBot.Services;

/// <summary>
/// SK-compatible chat completion service backed by the Anthropic Messages API.
/// Falls back to an OpenAI-compatible endpoint when the Anthropic call fails.
/// </summary>
public sealed class AnthropicChatCompletionService : IChatCompletionService
{
    private const int MaxToolIterations = 20;

    private readonly AnthropicClient _anthropicClient;
    private readonly IChatCompletionService? _fallback;
    private readonly string _model;
    private readonly ILogger<AnthropicChatCompletionService> _logger;
    private readonly IReadOnlyDictionary<string, object?> _attributes;

    // Cached tool definitions — plugins don't change after kernel build
    private IReadOnlyList<ToolUnion>? _cachedTools;

#pragma warning disable SA1201
    public AnthropicChatCompletionService(
        string apiKey,
        string baseUrl,
        string model,
        IChatCompletionService? fallback,
        ILogger<AnthropicChatCompletionService> logger)
    {
        _anthropicClient = new AnthropicClient { ApiKey = apiKey, BaseUrl = baseUrl };
        _fallback = fallback;
        _model = model;
        _logger = logger;

        _attributes = new Dictionary<string, object?>
        {
            [AIServiceExtensions.ModelIdKey] = model,
        };
    }

    public IReadOnlyDictionary<string, object?> Attributes => _attributes;
#pragma warning restore SA1201

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await CallAnthropicAsync(chatHistory, executionSettings, kernel, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw; // Don't fall back on cancellation
        }
        catch (Exception ex) when (_fallback != null)
        {
            _logger.LogWarning(ex, "Anthropic API failed, falling back to OpenRouter");

            // Build proper OpenAI settings with tool calling for fallback
            var maxTokens = ExtractMaxTokens(executionSettings);
            var fallbackSettings = new OpenAIPromptExecutionSettings
            {
                Temperature = 0.7,
                MaxTokens = maxTokens,
                ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
            };

            return await _fallback.GetChatMessageContentsAsync(
                chatHistory, fallbackSettings, kernel, cancellationToken);
        }
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var results = await GetChatMessageContentsAsync(chatHistory, executionSettings, kernel, cancellationToken);
        foreach (var result in results)
        {
            yield return new StreamingChatMessageContent(result.Role, result.Content);
        }
    }

    private async Task<IReadOnlyList<ChatMessageContent>> CallAnthropicAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings,
        Kernel? kernel,
        CancellationToken ct)
    {
        var (systemPrompt, messages) = ConvertHistory(chatHistory);
        var tools = kernel != null ? GetOrConvertTools(kernel) : null;
        var maxTokens = ExtractMaxTokens(executionSettings);

        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = maxTokens,
            System = systemPrompt!,
            Messages = messages,
            Tools = tools,
        };

        var response = await _anthropicClient.Messages.Create(parameters, cancellationToken: ct);

        var currentMessages = new List<MessageParam>(messages);
        var currentResponse = response;
        var iterations = 0;

        while (currentResponse.StopReason == "tool_use" && kernel != null && iterations < MaxToolIterations)
        {
            iterations++;
            var assistantContent = new List<ContentBlockParam>();
            var toolResults = new List<ContentBlockParam>();

            foreach (var block in currentResponse.Content)
            {
                if (block.TryPickToolUse(out var toolUse))
                {
                    assistantContent.Add(new ToolUseBlockParam
                    {
                        ID = toolUse.ID,
                        Name = toolUse.Name,
                        Input = toolUse.Input,
                    });

                    var result = await ExecuteToolAsync(kernel, toolUse.Name, toolUse.Input, ct);

                    toolResults.Add(new ToolResultBlockParam
                    {
                        ToolUseID = toolUse.ID,
                        Content = result,
                    });
                }
                else if (block.TryPickText(out var text))
                {
                    assistantContent.Add(new TextBlockParam { Text = text.Text });
                }
            }

            currentMessages.Add(new MessageParam
            {
                Role = Role.Assistant,
                Content = assistantContent,
            });
            currentMessages.Add(new MessageParam
            {
                Role = Role.User,
                Content = toolResults,
            });

            var nextParams = new MessageCreateParams
            {
                Model = _model,
                MaxTokens = maxTokens,
                System = parameters.System,
                Messages = currentMessages,
                Tools = tools,
            };

            currentResponse = await _anthropicClient.Messages.Create(nextParams, cancellationToken: ct);
        }

        if (iterations >= MaxToolIterations)
        {
            _logger.LogWarning("Tool use loop hit max iterations ({Max})", MaxToolIterations);
        }

        // Extract final text in a single pass
        var textParts = new List<string>();
        foreach (var block in currentResponse.Content)
        {
            if (block.TryPickText(out var t))
            {
                textParts.Add(t.Text);
            }
        }

        var responseText = string.Join("", textParts);

        var metadata = new Dictionary<string, object?>
        {
            ["Usage"] = new AnthropicTokenUsage(
                (int)currentResponse.Usage.InputTokens,
                (int)currentResponse.Usage.OutputTokens),
        };

        return [new ChatMessageContent(
            AuthorRole.Assistant,
            responseText,
            modelId: _model,
            metadata: metadata)];
    }

    private static (string? SystemPrompt, List<MessageParam> Messages) ConvertHistory(ChatHistory history)
    {
        string? systemPrompt = null;
        var messages = new List<MessageParam>();

        foreach (var message in history)
        {
            if (message.Role == AuthorRole.System)
            {
                systemPrompt = message.Content;
                continue;
            }

            var role = message.Role == AuthorRole.User ? Role.User : Role.Assistant;
            messages.Add(new MessageParam
            {
                Role = role,
                Content = message.Content ?? string.Empty,
            });
        }

        // Anthropic requires messages to start with a user message
        if (messages.Count > 0 && messages[0].Role != Role.User)
        {
            messages.Insert(0, new MessageParam { Role = Role.User, Content = "Hello" });
        }

        return (systemPrompt, messages);
    }

    private IReadOnlyList<ToolUnion>? GetOrConvertTools(Kernel kernel)
    {
        if (_cachedTools != null)
            return _cachedTools;

        _cachedTools = ConvertTools(kernel);
        return _cachedTools;
    }

    private static IReadOnlyList<ToolUnion>? ConvertTools(Kernel kernel)
    {
        var tools = new List<ToolUnion>();

        foreach (var plugin in kernel.Plugins)
        {
            foreach (var function in plugin)
            {
                var userParams = function.Metadata.Parameters
                    .Where(p => p.ParameterType != typeof(Kernel))
                    .ToList();

                var properties = new Dictionary<string, JsonElement>();
                var required = new List<string>();

                foreach (var param in userParams)
                {
                    var prop = new Dictionary<string, string>
                    {
                        ["type"] = GetJsonType(param.ParameterType),
                    };

                    if (!string.IsNullOrEmpty(param.Description))
                    {
                        prop["description"] = param.Description;
                    }

                    properties[param.Name] = JsonSerializer.SerializeToElement(prop);

                    if (param.IsRequired)
                    {
                        required.Add(param.Name);
                    }
                }

                var inputSchema = new InputSchema
                {
                    Type = JsonSerializer.SerializeToElement("object"),
                    Properties = properties.Count > 0 ? properties : null,
                    Required = required.Count > 0 ? required : null,
                };

                tools.Add(new Tool
                {
                    Name = $"{plugin.Name}_{function.Name}",
                    Description = function.Description,
                    InputSchema = inputSchema,
                });
            }
        }

        return tools.Count > 0 ? tools : null;
    }

    private static async Task<string> ExecuteToolAsync(
        Kernel kernel, string toolName, object? input, CancellationToken ct)
    {
        try
        {
            var parts = toolName.Split('_', 2);
            if (parts.Length != 2)
                return $"Error: invalid tool name format: {toolName}";

            var pluginName = parts[0];
            var functionName = parts[1];

            if (!kernel.Plugins.TryGetFunction(pluginName, functionName, out var function))
                return $"Error: tool not found: {toolName}";

            var args = new KernelArguments();

            if (input is JsonElement jsonInput)
            {
                foreach (var prop in jsonInput.EnumerateObject())
                {
                    args[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()
                        : prop.Value.ToString();
                }
            }

            var result = await kernel.InvokeAsync(function, args, ct);
            return result.ToString() ?? "No output";
        }
        catch (Exception ex)
        {
            return $"Error executing tool: {ex.Message}";
        }
    }

    private static int ExtractMaxTokens(PromptExecutionSettings? settings)
    {
        if (settings?.ExtensionData?.TryGetValue("max_tokens", out var maxObj) == true)
        {
            if (maxObj is int maxInt) return maxInt;
            if (maxObj is long maxLong) return (int)maxLong;
        }

        return 2048;
    }

    private static string GetJsonType(System.Type? type)
    {
        if (type == null) return "string";
        if (type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(float))
            return "number";
        if (type == typeof(bool))
            return "boolean";
        return "string";
    }

#pragma warning disable SA1201
    public sealed class AnthropicTokenUsage
    {
        public AnthropicTokenUsage(int inputTokens, int outputTokens)
        {
            InputTokenCount = inputTokens;
            OutputTokenCount = outputTokens;
        }

        public int InputTokenCount { get; }

        public int OutputTokenCount { get; }
    }
#pragma warning restore SA1201
}
