using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// The app project's source tree, for the grep-shaped rules that are tests rather than scripts
/// because a script is not run by CI and a test is.
/// </summary>
/// <remarks>
/// Found by walking up from the test binary to the solution file, which is inside the repository
/// for every way the suite is run.
/// </remarks>
internal static class PlayerSource
{
    /// <summary>The app project directory.</summary>
    public static string Directory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sendspin.Player.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Source tree not found: the test binary is running outside the repository.");

        return Path.Combine(directory!.FullName, "src", "Sendspin.Player");
    }

    /// <summary>Every axaml file in the app project, build output excluded.</summary>
    public static IEnumerable<string> AxamlFiles() => Files(".axaml");

    /// <summary>Every C# file in the app project, build output excluded.</summary>
    public static IEnumerable<string> CSharpFiles() => Files(".cs");

    /// <summary>Every axaml and C# file in the app project, build output excluded.</summary>
    public static IEnumerable<string> SourceFiles() => Files(".axaml", ".cs");

    private static IEnumerable<string> Files(params string[] extensions) =>
        System.IO.Directory.EnumerateFiles(Directory(), "*.*", SearchOption.AllDirectories)
            .Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.Ordinal))
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
}
