using AutoMapper;
using AutoMapper.Internal;

namespace NewWords.Api.MappingProfiles;

/// <summary>
/// Central AutoMapper configuration. Applies a global recursion depth bound to
/// mitigate GHSA-rvv3-g6hj-g44x / CVE-2026-32933 (DoS via uncontrolled
/// recursion): AutoMapper recursively maps nested object graphs with no default
/// depth limit, so a deeply self-referencing source can trigger an uncatchable
/// <c>StackOverflowException</c>. No free/MIT AutoMapper release is patched, so
/// we cap recursion instead of upgrading. See GitHub issue #9.
/// </summary>
public static class AutoMapperConfiguration
{
    /// <summary>
    /// Maximum object-graph depth AutoMapper will map. Beyond this, deeper nodes
    /// are truncated (mapped to null) so a pathological graph faults gracefully
    /// rather than overflowing the stack.
    /// </summary>
    public const int MaxRecursionDepth = 64;

    /// <summary>
    /// Applies <see cref="MaxRecursionDepth"/> to every mapping — current and
    /// future — so a map added later cannot silently reopen the recursion DoS.
    /// </summary>
    public static void ApplyRecursionGuard(IMapperConfigurationExpression cfg)
        => cfg.Internal().ForAllMaps((_, map) => map.MaxDepth(MaxRecursionDepth));
}
