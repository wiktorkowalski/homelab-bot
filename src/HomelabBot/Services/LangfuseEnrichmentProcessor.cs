using System.Diagnostics;
using System.Text.Json;
using OpenTelemetry;

namespace HomelabBot.Services;

/// <summary>
/// Enriches spans for Langfuse. Required because:
/// 1. SK puts messages in Events, not attributes - Langfuse shows raw JSON without extraction.
/// 2. Session/user IDs need propagation for filtering.
/// </summary>
public sealed class LangfuseEnrichmentProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        PropagateFromParent(activity, "langfuse.session.id");
        PropagateFromParent(activity, "langfuse.user.id");

        // Our custom traces and spans (Chat, Generate Title, SmartRecall, etc.)
        if (activity.Source.Name == "HomelabBot.Chat")
        {
            // Root trace level
            CopyTag(activity, "langfuse.trace.input", "input.value");
            CopyTag(activity, "langfuse.trace.output", "output.value");

            // Child span level - map to observation for Input/Output preview
            CopyTag(activity, "langfuse.span.input", "langfuse.observation.input");
            CopyTag(activity, "langfuse.span.output", "langfuse.observation.output");
            return;
        }

        // SK spans - extract content for clean Langfuse preview
        if (activity.Source.Name.StartsWith("Microsoft.SemanticKernel"))
        {
            var operation = activity.GetTagItem("gen_ai.operation.name")?.ToString();

            if (operation == "execute_tool")
            {
                // Tool call - extract arguments and result
                var args = ExtractToolContent(activity, "gen_ai.tool.message");
                if (!string.IsNullOrEmpty(args))
                {
                    activity.SetTag("langfuse.observation.input", args);
                }

                var result = ExtractToolContent(activity, "gen_ai.tool.result");
                if (!string.IsNullOrEmpty(result))
                {
                    activity.SetTag("langfuse.observation.output", result);
                }
            }
            else
            {
                // Chat generation - extract user message and response
                var input = ExtractContent(activity, "gen_ai.user.message");
                if (!string.IsNullOrEmpty(input))
                {
                    activity.SetTag("langfuse.observation.input", input);
                }

                var output = ExtractContent(activity, "gen_ai.choice");
                if (!string.IsNullOrEmpty(output))
                {
                    activity.SetTag("langfuse.observation.output", output);
                }
            }
        }

        base.OnEnd(activity);
    }

    private static void PropagateFromParent(Activity activity, string key)
    {
        if (activity.GetTagItem(key) != null || activity.Parent == null)
        {
            return;
        }

        var value = activity.Parent.GetTagItem(key)?.ToString();
        if (!string.IsNullOrEmpty(value))
        {
            activity.SetTag(key, value);
        }
    }

    private static void CopyTag(Activity activity, string source, string target)
    {
        var value = activity.GetTagItem(source)?.ToString();
        if (!string.IsNullOrEmpty(value))
        {
            activity.SetTag(target, value);
        }
    }

    private static string? ExtractContent(Activity activity, string eventName)
    {
        string? lastContent = null;

        foreach (var evt in activity.Events)
        {
            if (evt.Name != eventName)
            {
                continue;
            }

            foreach (var tag in evt.Tags)
            {
                if (tag.Key != "gen_ai.event.content")
                {
                    continue;
                }

                var json = tag.Value?.ToString();
                if (string.IsNullOrEmpty(json))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(json);

                    // {"role":"user","content":"..."} or {"message":{"content":"..."}}
                    if (doc.RootElement.TryGetProperty("content", out var c))
                    {
                        lastContent = c.GetString();
                    }
                    else if (doc.RootElement.TryGetProperty("message", out var m) &&
                        m.TryGetProperty("content", out var mc))
                    {
                        lastContent = mc.GetString();
                    }
                }
                catch
                {
                    lastContent = json;
                }
            }
        }

        return lastContent;
    }

    private static string? ExtractToolContent(Activity activity, string eventName)
    {
        foreach (var evt in activity.Events)
        {
            if (evt.Name != eventName)
            {
                continue;
            }

            foreach (var tag in evt.Tags)
            {
                if (tag.Key != "gen_ai.event.content")
                {
                    continue;
                }

                var json = tag.Value?.ToString();
                if (string.IsNullOrEmpty(json))
                {
                    return null;
                }

                try
                {
                    using var doc = JsonDocument.Parse(json);

                    // Tool message: {"tool_call_id":"...","content":"..."} or just content string
                    if (doc.RootElement.TryGetProperty("content", out var c))
                    {
                        return c.GetString();
                    }

                    // Tool arguments: {"arguments":"..."}
                    if (doc.RootElement.TryGetProperty("arguments", out var args))
                    {
                        return args.GetString();
                    }

                    // Fallback to full JSON
                    return json;
                }
                catch
                {
                    return json;
                }
            }
        }

        return null;
    }
}
