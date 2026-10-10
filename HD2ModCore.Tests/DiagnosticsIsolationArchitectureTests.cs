namespace HD2ModCore.Tests;

// 作用：守住"生产代码不得依赖 _diagnostics 临时诊断工程"这条纪律。
// Purpose: Enforces the rule that production code must never depend on the temporary `_diagnostics` projects.
// Those projects are scratch experiments: they are not part of the solution, not shipped, and their source
// lives outside any build. Because they are not referenced by the build, nothing else would notice if a
// production project started depending on one — so the rule needs an explicit guard.
public sealed class DiagnosticsIsolationArchitectureTests
{
	private const string DiagnosticsDirectoryName = "_diagnostics";

	// Directories that are never part of the shipped build. Anything else at the repository root is treated
	// as production surface, so a newly added project is covered automatically.
	private static readonly HashSet<string> ExcludedRootDirectories = new(StringComparer.OrdinalIgnoreCase)
	{
		DiagnosticsDirectoryName,
		"_backup",
		"artifacts",
		"bin",
		"docs",
		"obj",
		"packages",
		"tmp",
		"tools",
		".git",
		".idea",
		".vs",
	};

	[Fact]
	public void ProductionProjectsDoNotReferenceDiagnosticsProjects()
	{
		var root = FindRepositoryRoot();
		var violations = EnumerateProductionFiles(root, "*.csproj")
			.SelectMany(path => File.ReadLines(path)
				.Select((line, number) => (line, number: number + 1))
				.Where(item => item.line.Contains("ProjectReference", StringComparison.OrdinalIgnoreCase)
					&& item.line.Contains(DiagnosticsDirectoryName, StringComparison.OrdinalIgnoreCase))
				.Select(item => $"{Path.GetRelativePath(root, path)}:{item.number}: {item.line.Trim()}"))
			.ToArray();

		Assert.Empty(violations);
	}

	[Fact]
	public void ProductionSourcesDoNotImportDiagnosticsCode()
	{
		var root = FindRepositoryRoot();
		var violations = EnumerateProductionFiles(root, "*.cs")
			.SelectMany(path => File.ReadLines(path)
				.Select((line, number) => (line, number: number + 1))
				.Where(item => item.line.TrimStart().StartsWith("using ", StringComparison.Ordinal)
					&& item.line.Contains(DiagnosticsDirectoryName, StringComparison.OrdinalIgnoreCase))
				.Select(item => $"{Path.GetRelativePath(root, path)}:{item.number}: {item.line.Trim()}"))
			.ToArray();

		Assert.Empty(violations);
	}

	[Fact]
	public void SolutionDoesNotContainDiagnosticsProjects()
	{
		var root = FindRepositoryRoot();
		var solution = File.ReadAllText(Path.Combine(root, "WpfApp1.sln"));

		Assert.DoesNotContain(DiagnosticsDirectoryName, solution, StringComparison.OrdinalIgnoreCase);
	}

	private static IEnumerable<string> EnumerateProductionFiles(string root, string pattern)
	{
		foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
		{
			if (ExcludedRootDirectories.Contains(Path.GetFileName(directory))) continue;
			foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
			{
				if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
				if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
				yield return file;
			}
		}
	}

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WpfApp1.sln")))
			directory = directory.Parent;
		return directory?.FullName ?? throw new DirectoryNotFoundException("Unable to locate the repository root.");
	}
}
