using System.Text.Json;
using Domain.AI.Sandbox;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// The single routine that turns a tool's own <c>ResourceParametersByOperation</c> declaration plus
/// one call's already-flattened parameters into a <see cref="ToolCallResourceRequest"/> — shared by
/// the three entry points a tool call reaches governance from (#418): the agent-turn path
/// (<see cref="GovernedAIFunction"/>, which flattens its raw JSON arguments first), the
/// direct-invoke HTTP path (<c>DirectToolInvoker</c>, whose parameters are already flat) and the
/// plan-step path (<c>ToolUseStepExecutor</c>, which normalizes its step arguments first).
/// </summary>
public static class ResourceParameterExtractor
{
    /// <summary>
    /// Extracts the requested paths/hosts for one tool call.
    /// </summary>
    /// <param name="operation">The operation this call names, or <see langword="null"/> if unreadable.</param>
    /// <param name="parameters">The call's already-flattened parameters, or <see langword="null"/>.</param>
    /// <param name="resourceParametersByOperation">
    /// The tool's own declaration (<c>ITool.ResourceParametersByOperation</c>).
    /// </param>
    /// <returns>
    /// <list type="bullet">
    ///   <item><description>
    ///     <see langword="null"/> — resource usage for this call is <b>unknown</b>, and
    ///     <c>CapabilityEnforcer</c> must treat that as a reason to refuse when scoping is configured,
    ///     not as "nothing to check". It is returned when the tool declares nothing, when
    ///     <paramref name="operation"/> could not be read, when it matches no operation the tool
    ///     declares, or when a declared path/host parameter carries a non-string, non-null value
    ///     (#604).
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="ToolCallResourceRequest.Empty"/> — only when the tool affirmatively recognizes
    ///     <paramref name="operation"/> and declares no resource parameters for it.
    ///   </description></item>
    ///   <item><description>
    ///     Otherwise, the paths/hosts found among <paramref name="parameters"/> for this operation's
    ///     declared keys. A declared key that is absent or null contributes nothing.
    ///   </description></item>
    /// </list>
    /// An unmatched operation string is exactly as unknown as a missing one: both could be an
    /// upstream-corrupted value (#587 — historically, a plan step's merged arguments could carry raw,
    /// quote-wrapped JSON text for an upstream-sourced string value; #595 fixed that mechanism for the
    /// plan-step path, but this method cannot know which caller produced the value or whether some other
    /// path still can), and treating either as "nothing to check" would silently allow a call this
    /// method has no basis to vouch for.
    /// </returns>
    public static ToolCallResourceRequest? Extract(
        string? operation,
        IReadOnlyDictionary<string, object?>? parameters,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ResourceParameterKind>>? resourceParametersByOperation)
    {
        if (resourceParametersByOperation is not { Count: > 0 } declaredByOperation)
            return null;

        if (operation is null)
            return null;

        if (!TryFindDeclaration(declaredByOperation, operation, out var declaredParameters))
            return null;

        if (declaredParameters.Count == 0)
            return ToolCallResourceRequest.Empty;

        var paths = new List<string>();
        var hosts = new List<string>();

        foreach (var (parameterName, kind) in declaredParameters)
        {
            if (parameters is null || !parameters.TryGetValue(parameterName, out var value))
                continue;

            // Length is deliberately NOT checked here (#595 code-review): a declared parameter that
            // resolves to a genuinely empty string is a present-but-invalid path/host value, not an
            // absent one. Excluding it here would drop it from the request entirely, and
            // CapabilityEnforcer treats an empty request as "nothing to check" and passes it —
            // silently bypassing scoping instead of letting the empty value reach path/host
            // validation, which correctly denies it as unparsable.
            //
            // A JSON null is the one non-string that means "no value supplied": it reaches the tool
            // exactly as an omitted parameter does, which is already treated as absent above. It
            // arrives as a CLR null from the agent-turn path (ToolParameters.Flatten) and as a boxed
            // JsonElement of kind Null/Undefined from the plan-step path, so both are absent.
            if (value is null or JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
                continue;

            // Any other non-string (#604) is a present value of the wrong type — a number or bool from
            // a corrupted or adversarial producer. Skipping it would leave RequestedPaths empty, which
            // CapabilityEnforcer reads as "nothing to check" and passes. Return null instead: resource
            // usage for this call is unknown, so scoping, when configured, refuses it. The whole call
            // is refused, not just this parameter, so a well-formed sibling cannot vouch for it.
            if (value is not string text)
                return null;

            switch (kind)
            {
                case ResourceParameterKind.Path:
                    paths.Add(text);
                    break;
                case ResourceParameterKind.Host:
                    hosts.Add(text);
                    break;
            }
        }

        return new ToolCallResourceRequest(paths, hosts);
    }

    /// <summary>
    /// Looks up <paramref name="operation"/> in <paramref name="declaredByOperation"/>, matching
    /// case-insensitively so a casing difference can never downgrade a genuinely-declared operation
    /// to "affirmatively nothing scoped" (<see cref="ToolCallResourceRequest.Empty"/>) — which
    /// <see cref="Application.AI.Common.Services.Sandbox.CapabilityEnforcer"/> treats as trusted and
    /// skips validating. Every other stage that admits an operation name — <c>AIToolConverter</c>'s
    /// operation validation and <c>DirectToolInvoker</c>'s — accepts it case-insensitively, and
    /// <c>FileSystemTool.ExecuteAsync</c> itself dispatches via <c>ToLowerInvariant()</c>; an
    /// ordinal-only lookup here was the one link in that chain that disagreed, letting
    /// <c>operation: "Read"</c> bypass a <c>DeniedPaths</c> configured for <c>"read"</c> entirely.
    /// </summary>
    private static bool TryFindDeclaration(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ResourceParameterKind>> declaredByOperation,
        string operation,
        out IReadOnlyDictionary<string, ResourceParameterKind> declaredParameters)
    {
        if (declaredByOperation.TryGetValue(operation, out var exact))
        {
            declaredParameters = exact;
            return true;
        }

        foreach (var (key, value) in declaredByOperation)
        {
            if (string.Equals(key, operation, StringComparison.OrdinalIgnoreCase))
            {
                declaredParameters = value;
                return true;
            }
        }

        declaredParameters = new Dictionary<string, ResourceParameterKind>();
        return false;
    }
}
