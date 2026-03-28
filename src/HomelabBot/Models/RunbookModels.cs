namespace HomelabBot.Models;

public sealed class RunbookStep
{
    public int Order { get; set; }

    public required string Description { get; set; }

    public required string PluginName { get; set; }

    public required string FunctionName { get; set; }

    public Dictionary<string, string> Parameters { get; set; } = new();
}

public sealed class RunbookStepResult
{
    public required int StepOrder { get; init; }

    public required string Description { get; init; }

    public required bool Executed { get; init; }

    public string? Result { get; init; }

    public bool Skipped { get; init; }

    public string? SkipReason { get; init; }
}
