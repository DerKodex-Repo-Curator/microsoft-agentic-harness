using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Infrastructure.AI.Tests.Agents;

/// <summary>
/// Pins the trust boundary that lets <c>AgentSearchPathResolver</c> check agent roots with a plain
/// <see cref="Directory.Exists(string?)"/> instead of the sandboxed reader the skill side uses (#710).
/// </summary>
/// <remarks>
/// <para>
/// Skill roots are sandboxed because they are not purely operator-supplied: <c>PluginLoader</c> appends
/// plugin skill directories to <c>SkillsConfig.AdditionalPaths</c> at runtime. Agent roots have no such
/// writer, so the only source is host configuration. That premise is what makes the unsandboxed check
/// acceptable, so it is enforced by the type rather than left as a comment: <see cref="AgentsConfig"/>'s
/// path properties are init-only, and the compiler refuses any later assignment.
/// </para>
/// <para>
/// If a future change needs to add agent roots at runtime (for example plugin-contributed agents), the
/// first test below fails. That is the prompt to route the existence check, and the registry's directory
/// walk, through a sandboxed reader first — not to make the property settable again.
/// </para>
/// </remarks>
public sealed class AgentSearchPathTrustBoundaryTests
{
    [Theory]
    [InlineData(nameof(AgentsConfig.BasePath))]
    [InlineData(nameof(AgentsConfig.AdditionalPaths))]
    public void AgentsConfig_PathProperty_IsInitOnly(string propertyName)
    {
        var setter = typeof(AgentsConfig).GetProperty(propertyName)!.SetMethod!;

        // Matched by name: the Neo4j driver ships its own IsExternalInit, so typeof() is ambiguous here.
        setter.ReturnParameter.GetRequiredCustomModifiers().Select(t => t.FullName).Should().Contain(
            "System.Runtime.CompilerServices.IsExternalInit",
            "agent roots must come only from host configuration; a settable path lets runtime code add "
            + "a root that the unsandboxed existence check in AgentSearchPathResolver would then trust");
    }

    [Fact]
    public void AgentsConfig_BindsInitOnlyPathsFromConfiguration()
    {
        // Control for the test above: init-only is only acceptable if configuration binding still
        // populates the properties. A binder that skipped them would leave agent discovery silently
        // empty, which no other test would notice.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfig:AI:Agents:BasePath"] = "agents",
                ["AppConfig:AI:Agents:AdditionalPaths:0"] = "extra-one",
                ["AppConfig:AI:Agents:AdditionalPaths:1"] = "extra-two",
            })
            .Build();

        var agents = configuration.GetSection("AppConfig:AI:Agents").Get<AgentsConfig>();

        agents.Should().NotBeNull();
        agents!.BasePath.Should().Be("agents");
        agents.AdditionalPaths.Should().Equal("extra-one", "extra-two");
        agents.AllPaths.Should().Equal("agents", "extra-one", "extra-two");
    }
}
