using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class NuGetPackageTests
    {
        private static readonly string[] AlphaIndicators = ["alpha", "beta", "rc", "preview", "-a", "-pre"];
        private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

        /// <summary>
        /// Packages that have no stable release yet — exempted from the pre-release check.
        /// </summary>
        public static readonly string[] ExcludedPackages =
        [
            "OpenTelemetry.Instrumentation.Process",
        ];

        /// <summary>
        /// main is the release branch for this repo (equivalent to REL in other repos).
        /// topic/* branches are skipped.
        /// </summary>
        private static readonly Dictionary<string, string[]> ProtectedBranchMappings = new()
        {
            { "main", Array.Empty<string>() },
        };

        [Fact]
        public void Ensure_No_DisallowedAlpha_NuGet_Packages()
        {
            var currentBranch = Environment.GetEnvironmentVariable("CI_COMMIT_BRANCH");
            var targetBranch = Environment.GetEnvironmentVariable("CI_MERGE_REQUEST_TARGET_BRANCH_NAME");
            var branchToUse = string.IsNullOrEmpty(targetBranch) ? currentBranch : targetBranch;

            if (string.IsNullOrEmpty(branchToUse))
            {
                Assert.True(true, "No branch information found. Skipping validation.");
                return;
            }

            if (!ProtectedBranchMappings.TryGetValue(branchToUse, out var allowedByBranch))
            {
                Assert.True(true, $"Skipping alpha NuGet validation for unprotected branch: {branchToUse}");
                return;
            }

            var allowed = allowedByBranch ?? [];
            var allPackages = GetNuGetPackages();

            var alphaPackages = allPackages
                .Where(d =>
                    !ExcludedPackages.Any(e => d.Key.StartsWith($"{e},"))
                    && AlphaIndicators.Any(indicator =>
#if NET6_0_OR_GREATER
                        d.Key.Contains(indicator, StringComparison.OrdinalIgnoreCase)
#else
                        d.Key.IndexOf(indicator, StringComparison.OrdinalIgnoreCase) >= 0
#endif
                        && !allowed.Any(a => a == indicator)))
                .ToList();

            Assert.False(
                alphaPackages.Count > 0,
                alphaPackages.Count > 0
                    ? $"Found pre-release NuGet packages on '{branchToUse}': {JsonSerializer.Serialize(alphaPackages, PrettyJson)}"
                    : "No pre-release NuGet packages found.");
        }

        private static Dictionary<string, List<string>> GetNuGetPackages()
        {
            // Walk up from test assembly to find Directory.Packages.props
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
                dir = dir.Parent;

            Assert.NotNull(dir);

            var packages = new Dictionary<string, List<string>>();

            var packagesPropsPath = Path.Combine(dir!.FullName, "Directory.Packages.props");
            ReadPackageVersions(packagesPropsPath, packages);

            foreach (var csproj in Directory.GetFiles(dir.FullName, "*.csproj", SearchOption.AllDirectories))
                ReadPackageReferences(csproj, packages);

            return packages;
        }

        private static void ReadPackageVersions(string filePath, Dictionary<string, List<string>> packages)
        {
            var doc = XDocument.Load(filePath);
            foreach (var el in doc.Descendants("PackageVersion"))
            {
                var name = el.Attribute("Include")?.Value;
                var version = el.Attribute("Version")?.Value;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(version))
                    AddPackage(packages, name!, version!, Path.GetFileName(filePath));
            }
        }

        private static void ReadPackageReferences(string filePath, Dictionary<string, List<string>> packages)
        {
            try
            {
                var doc = XDocument.Load(filePath);
                foreach (var el in doc.Descendants("PackageReference"))
                {
                    var name = el.Attribute("Include")?.Value;
                    var version = el.Attribute("Version")?.Value ?? el.Element("Version")?.Value;
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(version))
                        AddPackage(packages, name!, version!, Path.GetFileName(filePath));
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to process project file: {filePath}. Error: {ex.Message}");
            }
        }

        private static void AddPackage(Dictionary<string, List<string>> packages, string name, string version, string source)
        {
            var key = $"{name}, {version}";
            if (packages.TryGetValue(key, out var existing))
                existing.Add(source);
            else
                packages[key] = [source];
        }
    }
}
