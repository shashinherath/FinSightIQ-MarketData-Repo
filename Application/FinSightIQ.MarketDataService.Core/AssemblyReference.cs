namespace FinSightIQ.MarketDataService.Core;

/// <summary>
/// Assembly marker for the Core domain and application layer.
/// </summary>
public static class AssemblyReference
{
    public static string Name => typeof(AssemblyReference).Assembly.GetName().Name ?? "FinSightIQ.MarketDataService.Core";
}

