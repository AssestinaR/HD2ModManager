using System.Text.Json;
using HD2ModCore.Decoration;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliDecorationPackageExecutionResult(
	bool Success,
	string? OutputDirectory,
	string? DecorationPlanPath,
	int SelectedUnitCount,
	IReadOnlyList<CliDecorationPackageDiagnostic> Diagnostics);

/// <summary>
/// Publishes one validated decoration package to a fresh, external directory.
/// It intentionally has no Mod-library write session: the library is only read,
/// and the result is merely an importable package until a later command says otherwise.
/// </summary>
internal static class CliDecorationPackageExecutor
{
	public static async ValueTask<CliDecorationPackageExecutionResult> ApplyAsync(
		CliDecorationPackagePlanDocument document,
		CliWorkspace workspace,
		string outputDirectory,
		CancellationToken cancellationToken = default)
	{
		var validation = await CliDecorationPackagePlanArtifacts.ValidateAsync(document, workspace, cancellationToken).ConfigureAwait(false);
		if (!validation.IsValid || validation.Current is null)
			return new CliDecorationPackageExecutionResult(false, null, null, validation.ResolvedSelections.Count, validation.Diagnostics);

		var target = PrepareOutputDirectory(outputDirectory, workspace.LibraryRoot, workspace.GameDataRoot);
		var parent = Path.GetDirectoryName(target) ?? throw new CliUserException("DecorationPackageOutputInvalid", $"无法解析输出目录的父路径：{target}");
		Directory.CreateDirectory(parent);
		var staged = Path.Combine(parent, ".hd2mod-cli-decoration-package-" + Guid.NewGuid().ToString("N"));
		try
		{
			await using var reader = CoreServices.CreateModInformationReader();
			var service = CoreServices.CreateDecorationPackageSnapshotService(reader);
			await service.CaptureAsync(validation.Current.SourceNode, workspace.LibraryRoot!, validation.ResolvedSelections, staged, cancellationToken: cancellationToken).ConfigureAwait(false);
			var package = CreateDecorationDocument(document, validation.ResolvedSelections);
			var planPath = Path.Combine(staged, "decoration.json");
			await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(package, CliOutput.JsonOptions), cancellationToken).ConfigureAwait(false);
			if (Directory.Exists(target) || File.Exists(target))
				throw new CliUserException("DecorationPackageOutputAlreadyExists", $"输出目录在生成期间已出现：{target}。为避免混合旧文件，CLI 未发布本次结果。 ");
			Directory.Move(staged, target);
			return new CliDecorationPackageExecutionResult(true, target, Path.Combine(target, "decoration.json"), validation.ResolvedSelections.Count, validation.Diagnostics);
		}
		catch (CliUserException) { throw; }
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or DirectoryNotFoundException or FileNotFoundException)
		{
			throw new CliUserException("DecorationPackageWriteFailed", $"装饰外部包输出未完成：{exception.Message}");
		}
		finally
		{
			if (Directory.Exists(staged))
			{
				try { Directory.Delete(staged, recursive: true); }
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
			}
		}
	}

	private static DecorationPlanDocument CreateDecorationDocument(CliDecorationPackagePlanDocument plan, IReadOnlyList<DecorationSourceUnit> selections)
		=> new()
		{
			Name = plan.DisplayName,
			Version = 3,
			SourceStorageMode = "PatchSnapshot",
			SourceUnits = selections.Select(item => new DecorationSourceUnit { TypeId = item.TypeId, FileId = item.FileId, MeshInfoIndex = item.MeshInfoIndex, BodyVariant = item.BodyVariant, Layer = item.Layer, IsCulling = item.IsCulling }).ToList(),
			Plan = new DecorationAttachmentPlan
			{
				TargetModGuids = plan.Attachment.TargetHostNodeIds.ToList(),
				TargetPart = plan.Attachment.TargetPart,
				TargetBodyVariant = plan.Attachment.TargetBodyVariant,
				DualVariantMode = plan.Attachment.DualVariantMode,
				ReplaceWhenSourcePartLayerMatches = plan.Attachment.ReplaceWhenSourcePartLayerMatches,
				SourcePartLayers = plan.Attachment.SourcePartLayers.ToList(),
				CreatedUtc = DateTime.UtcNow,
			},
		};

	private static string PrepareOutputDirectory(string outputDirectory, string? libraryRoot, string? gameDataRoot)
	{
		if (string.IsNullOrWhiteSpace(outputDirectory))
			throw new CliUserException("DecorationPackageOutputRequired", "decoration package apply 必须显式提供 --output <新的输出目录>。 ");
		var target = CliOutput.NormalizeDirectoryPath(outputDirectory, "DecorationPackageOutputInvalid");
		if (File.Exists(target) || Directory.Exists(target))
			throw new CliUserException("DecorationPackageOutputAlreadyExists", $"装饰外部包只发布到新的目录，当前输出已存在：{target}。请指定一个新的目录，避免覆盖或混合旧文件。 ");
		if (!string.IsNullOrWhiteSpace(libraryRoot) && IsSameOrChildPath(target, libraryRoot))
			throw new CliUserException("DecorationPackageOutputInsideLibrary", "装饰外部包输出不能位于 Mod 库内；这会制造未登记内容。 ");
		if (!string.IsNullOrWhiteSpace(gameDataRoot))
		{
			var gameData = Path.GetFullPath(gameDataRoot);
			if (IsSameOrChildPath(target, gameData))
				throw new CliUserException("DecorationPackageOutputInsideGameData", "装饰外部包输出不能位于游戏 GameData 目录内；CLI 不会直接部署资源。 ");
			var gameRoot = Directory.GetParent(gameData)?.FullName;
			if (!string.IsNullOrWhiteSpace(gameRoot) && IsSameOrChildPath(target, gameRoot))
				throw new CliUserException("DecorationPackageOutputInsideGameRoot", "装饰外部包输出不能位于游戏根目录内；请使用游戏目录以外的新目录。 ");
		}
		return target;
	}

	private static bool IsSameOrChildPath(string candidate, string root)
	{
		var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
			|| normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
			|| normalizedCandidate.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}
}
