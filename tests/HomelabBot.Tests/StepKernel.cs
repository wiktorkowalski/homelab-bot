using HomelabBot.Models;
using Microsoft.SemanticKernel;

namespace HomelabBot.Tests;

// A kernel with a "Fake" plugin for runbook and healing chain steps. Calls holds the functions that ran, in order.
internal sealed class StepKernel
{
    public const string ErrorAnswer = "Error: Container 'plex' not found.";

    public StepKernel()
    {
        Kernel = Kernel.CreateBuilder().Build();
        Kernel.Plugins.AddFromFunctions("Fake",
        [
            KernelFunctionFactory.CreateFromMethod(() => Record("Works", "container is running"), "Works"),
            KernelFunctionFactory.CreateFromMethod(() => Record("AnswersError", ErrorAnswer), "AnswersError"),
            KernelFunctionFactory.CreateFromMethod(string () => throw new InvalidOperationException("boom"), "Throws"),
        ]);
    }

    public Kernel Kernel { get; }

    public List<string> Calls { get; } = [];

    public static List<RunbookStep> Steps(params string[] functions) =>
        functions.Select((function, index) => new RunbookStep
        {
            Order = index + 1,
            Description = $"step {function}",
            PluginName = "Fake",
            FunctionName = function,
        }).ToList();

    private string Record(string function, string answer)
    {
        Calls.Add(function);
        return answer;
    }
}
