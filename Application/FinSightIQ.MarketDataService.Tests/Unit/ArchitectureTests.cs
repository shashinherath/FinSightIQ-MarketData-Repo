using System.Reflection;
using FluentAssertions;

namespace FinSightIQ.MarketDataService.Tests.Unit;

public sealed class ArchitectureTests
{
    [Fact]
    public void CoreProject_ShouldNotReference_InfrastructureOrApi()
    {
        Assembly coreAssembly = typeof(Core.AssemblyReference).Assembly;
        AssemblyName[] referencedAssemblies = coreAssembly.GetReferencedAssemblies();

        referencedAssemblies
            .Select(a => a.Name)
            .Should()
            .NotContain("FinSightIQ.MarketDataService.Infrastructure")
            .And
            .NotContain("FinSightIQ.MarketDataService.Api");
    }

    [Fact]
    public void InfrastructureProject_ShouldReference_Core_AndNotApi()
    {
        Assembly infrastructureAssembly = typeof(Infrastructure.AssemblyReference).Assembly;
        string?[] referencedNames = infrastructureAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        referencedNames.Should().Contain("FinSightIQ.MarketDataService.Core");
        referencedNames.Should().NotContain("FinSightIQ.MarketDataService.Api");
    }
}

