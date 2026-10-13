using System.Runtime.CompilerServices;

namespace HomelabBot.Tests;

internal static class TestAssemblySetup
{
    // Same switches as Program.cs. Semantic Kernel reads them once, into static fields, so they
    // must be on before the first test touches a kernel: a test class cannot set them in time
    // when another class ran first.
    [ModuleInitializer]
    internal static void EnableSemanticKernelDiagnostics()
    {
        AppContext.SetSwitch("Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnostics", true);
        AppContext.SetSwitch("Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnosticsSensitive", true);
    }
}
