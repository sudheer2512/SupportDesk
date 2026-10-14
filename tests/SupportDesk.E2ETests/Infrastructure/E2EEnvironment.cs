using System.Runtime.InteropServices;

namespace SupportDesk.E2ETests.Infrastructure;

/// <summary>
/// Where things are and whether the suite can run here. The suite needs an installed Google Chrome (Playwright drives
/// it through <c>Channel = "chrome"</c>; no Playwright browsers are downloaded), SQL Server LocalDB and Node (for the
/// Angular build). Set <c>SUPPORTDESK_E2E=0</c> to skip it.
/// </summary>
public static class E2EEnvironment
{
    /// <summary>Why the suite is skipped on this machine (null = run it).</summary>
    public static string? SkipReason { get; } = DetermineSkipReason();

    /// <summary>The repository root (folder containing SupportDesk.slnx).</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>Build configuration of this test assembly; the API is built with the same one (project reference).</summary>
    public const string Configuration =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    /// <summary>Show the browser while the tests run (<c>SUPPORTDESK_E2E_HEADED=1</c>).</summary>
    public static bool Headed => Environment.GetEnvironmentVariable("SUPPORTDESK_E2E_HEADED") == "1";

    /// <summary>Skip <c>npm run build</c> when wwwroot is already up to date (<c>SUPPORTDESK_E2E_SKIP_CLIENT_BUILD=1</c>).</summary>
    public static bool SkipClientBuild => Environment.GetEnvironmentVariable("SUPPORTDESK_E2E_SKIP_CLIENT_BUILD") == "1";

    /// <summary>Screenshots of failed tests and the API log go here.</summary>
    public static string ArtifactsDirectory
    {
        get
        {
            var dir = Path.Combine(RepoRoot, "TestResults", "e2e");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string? ChromePath => CandidateChromePaths().FirstOrDefault(File.Exists);

    private static IEnumerable<string> CandidateChromePaths()
    {
        if (Environment.GetEnvironmentVariable("SUPPORTDESK_E2E_CHROME") is { Length: > 0 } custom) yield return custom;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                     }.Where(r => r.Length > 0))
                yield return Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
        }
        else
        {
            yield return "/opt/google/chrome/chrome";
            yield return "/usr/bin/google-chrome";
            yield return "/usr/bin/google-chrome-stable";
        }
    }

    private static string? DetermineSkipReason()
    {
        if (Environment.GetEnvironmentVariable("SUPPORTDESK_E2E") == "0")
            return "Browser E2E tests are disabled (SUPPORTDESK_E2E=0).";
        if (ChromePath is null)
            return "Browser E2E tests need an installed Google Chrome (not found; set SUPPORTDESK_E2E_CHROME to its path).";
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Environment.GetEnvironmentVariable("SUPPORTDESK_E2E_CONNECTION") is null)
            return "Browser E2E tests use SQL Server LocalDB (Windows); elsewhere set SUPPORTDESK_E2E_CONNECTION to a SQL Server master connection string.";
        return null;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SupportDesk.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Cannot find the repository root (SupportDesk.slnx) above " + AppContext.BaseDirectory);
    }
}

/// <summary>An xunit fact that is skipped with a clear message when the E2E prerequisites are missing.</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (E2EEnvironment.SkipReason is { } reason) Skip = reason;
    }
}
