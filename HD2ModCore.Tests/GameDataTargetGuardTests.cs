using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// Purpose: Verifies the read-only deployment target guard accepts only a recognizable local game data directory.
public sealed class GameDataTargetGuardTests
{
	[Fact]
	public void Validate_AcceptsNormalizedDataDirectory_WithCaseInsensitiveGameSignature()
	{
		using var fixture = GameInstallFixture.Create(createBin: true, createExecutable: true, uppercaseSignature: true);

		var result = new GameDataTargetGuard().Validate(fixture.DataDirectory + Path.DirectorySeparatorChar);

		Assert.True(result.IsValid);
		Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixture.DataDirectory)), result.GameDataDirectory);
		Assert.Equal(Path.GetFullPath(fixture.GameRootDirectory), result.GameRootDirectory);
		Assert.DoesNotContain(result.Issues, issue => issue.Severity == HD2ModCore.Domain.CoreIssueSeverity.Error);
	}

	[Fact]
	public void Validate_RejectsMissingDataDirectory()
	{
		using var fixture = GameInstallFixture.Create(createData: false, createBin: true, createExecutable: true);

		var result = new GameDataTargetGuard().Validate(fixture.DataDirectory);

		Assert.False(result.IsValid);
		Assert.Contains(result.Issues, issue => issue.Code == "GameDataDirectoryMissing");
	}

	[Fact]
	public void Validate_RejectsFileNamedData()
	{
		using var fixture = GameInstallFixture.Create(createData: false, createBin: true, createExecutable: true);
		File.WriteAllText(fixture.DataDirectory, "not a directory");

		var result = new GameDataTargetGuard().Validate(fixture.DataDirectory);

		Assert.False(result.IsValid);
		Assert.Contains(result.Issues, issue => issue.Code == "GameDataTargetIsFile");
	}

	[Fact]
	public void Validate_RejectsPathThatNormalizesOutsideDataDirectory()
	{
		using var fixture = GameInstallFixture.Create(createBin: true, createExecutable: true);
		var outsideDirectory = Path.Combine(fixture.GameRootDirectory, "outside");
		Directory.CreateDirectory(outsideDirectory);

		var result = new GameDataTargetGuard().Validate(Path.Combine(fixture.DataDirectory, "..", "outside"));

		Assert.False(result.IsValid);
		Assert.Contains(result.Issues, issue => issue.Code == "GameDataDirectoryNameInvalid");
	}

	[Fact]
	public void Validate_RejectsMissingGameSignature()
	{
		using var fixture = GameInstallFixture.Create(createBin: true, createExecutable: false);

		var result = new GameDataTargetGuard().Validate(fixture.DataDirectory);

		Assert.False(result.IsValid);
		Assert.Contains(result.Issues, issue => issue.Code == "GameExecutableMissing");
	}

	[Fact]
	public void Validate_RejectsUncPathBeforeFilesystemAccess()
	{
		var result = new GameDataTargetGuard().Validate(@"\\server\share\Helldivers 2\data");

		Assert.False(result.IsValid);
		Assert.Contains(result.Issues, issue => issue.Code == "GameDataNetworkPathRejected");
	}

	[Fact]
	public void Validate_RejectsReparsePointTarget_WhenSupportedByTheCurrentFileSystem()
	{
		using var fixture = GameInstallFixture.Create(createBin: true, createExecutable: true);
		var targetDirectory = Path.Combine(fixture.RootDirectory, "real-data");
		Directory.CreateDirectory(targetDirectory);
		Directory.Delete(fixture.DataDirectory);

		try
		{
			Directory.CreateSymbolicLink(fixture.DataDirectory, targetDirectory);
		}
		catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException or NotSupportedException)
		{
			return;
		}

		var result = new GameDataTargetGuard().Validate(fixture.DataDirectory);

		Assert.False(result.IsValid);
		Assert.Contains(result.Issues, issue => issue.Code == "GameDataPathReparsePoint");
	}

	private sealed class GameInstallFixture : IDisposable
	{
		private GameInstallFixture(string rootDirectory, string gameRootDirectory, string dataDirectory)
		{
			RootDirectory = rootDirectory;
			GameRootDirectory = gameRootDirectory;
			DataDirectory = dataDirectory;
		}

		public string RootDirectory { get; }
		public string GameRootDirectory { get; }
		public string DataDirectory { get; }

		public static GameInstallFixture Create(
			bool createData = true,
			bool createBin = true,
			bool createExecutable = true,
			bool uppercaseSignature = false)
		{
			var rootDirectory = Path.Combine(Path.GetTempPath(), "hd2-game-data-guard-" + Guid.NewGuid().ToString("N"));
			var gameRootDirectory = Path.Combine(rootDirectory, "Helldivers 2");
			var dataDirectory = Path.Combine(gameRootDirectory, "data");
			Directory.CreateDirectory(gameRootDirectory);
			if (createData) Directory.CreateDirectory(dataDirectory);
			if (createBin)
			{
				var binDirectory = Path.Combine(gameRootDirectory, uppercaseSignature ? "BIN" : "bin");
				Directory.CreateDirectory(binDirectory);
				if (createExecutable) File.WriteAllText(Path.Combine(binDirectory, uppercaseSignature ? "HELLDIVERS2.EXE" : "helldivers2.exe"), string.Empty);
			}
			return new GameInstallFixture(rootDirectory, gameRootDirectory, dataDirectory);
		}

		public void Dispose()
		{
			try { Directory.Delete(RootDirectory, recursive: true); } catch { }
		}
	}
}
