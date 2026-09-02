namespace Sendspin.Core.Platform;

/// <summary>
/// The family name the macOS head hands Avalonia as <c>$Default</c>, and the rule for when it
/// may.
/// </summary>
/// <remarks>
/// <para>
/// Left to itself, Avalonia's <c>DefaultFontFamily</c> resolves to Helvetica on macOS, not the
/// system face. <c>.AppleSystemUIFont</c> is the name that resolves to it (measured in the
/// "System font" section of <c>docs/ARCHITECTURE.md</c>). The same measurement found that an
/// unresolvable <c>DefaultFamilyName</c> does not degrade: it throws out of the first layout pass
/// and the process dies before a window appears, with an exception that names only
/// <c>$Default</c>. So the name is only ever used after the font manager has said it resolves.
/// </para>
/// <para>
/// The resolve check is handed in so the rule is testable without a Mac; the head passes
/// Skia's font manager.
/// </para>
/// </remarks>
public static class MacSystemFont
{
    /// <summary>The name macOS exposes its system UI font under.</summary>
    public const string FamilyName = ".AppleSystemUIFont";

    /// <summary>
    /// The family to hand Avalonia: <see cref="FamilyName"/> when <paramref name="resolves"/>
    /// says the font manager can match it, otherwise null, which leaves the platform default.
    /// </summary>
    public static string? Select(Func<string, bool> resolves)
    {
        ArgumentNullException.ThrowIfNull(resolves);
        return resolves(FamilyName) ? FamilyName : null;
    }
}
