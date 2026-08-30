using System.Text.Json;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// Read-only projection of one imported Decoration package and the current
/// activation scope it declares.  It never infers targets from display names
/// and never repairs an invalid decoration.json while inspecting it.
/// </summary>
internal sealed record CliDecorationManagementInspection(
	string DecorationNodeId,
	string DecorationName,
	string RelativePath,
	string SourceStorageMode,
	IReadOnlyList<CliDecorationStoredSourceUnit> SourceUnits,
	CliDecorationStoredAttachment Attachment,
	IReadOnlyList<CliDecorationDeclaredHost> DeclaredHosts,
	IReadOnlyList<CliDecorationManagementDiagnostic> Diagnostics)
{
	public bool IsReadable => !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

internal sealed record CliDecorationStoredSourceUnit(
	string AssetKey,
	int MeshInfoIndex,
	string BodyVariant,
	string Layer,
	bool IsCulling);

internal sealed record CliDecorationStoredAttachment(
	string TargetPart,
	string TargetBodyVariant,
	string DualVariantMode,
	bool ReplaceWhenSourcePartLayerMatches,
	IReadOnlyList<string> SourcePartLayers);

internal sealed record CliDecorationDeclaredHost(
	string DeclaredHostId,
	string? Name,
	string? Kind,
	bool Exists,
	bool IsAvailableForActivation,
	bool IsEnabled,
	string? RelativePath,
	int PatchGroupCount);

internal sealed record CliDecorationManagementDiagnostic(
	string Code,
	string Severity,
	bool Blocking,
	string Message,
	string? Path = null,
	string? NodeId = null,
	string? Details = null);

internal static class CliDecorationManagementInspectionArtifacts
{
	public static async ValueTask<CliDecorationManagementInspection> BuildAsync(
		CliWorkspace workspace,
		string decorationSelector,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot))
			throw new CliUserException("DecorationInspectManagerRootRequired", "decoration show 需要 --manager-root，以读取该库的装饰启用状态。 ");
		if (string.IsNullOrWhiteSpace(workspace.LibraryRoot))
			throw new CliUserException("LibraryNotConfigured", "decoration show 需要明确的 Mod 库。 ");

		var state = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
		var decoration = CliProjections.ResolveMod(state.Snapshot.Nodes, decorationSelector);
		if (decoration.Metadata.Kind != ModNodeKind.Decoration)
		{
			throw new CliUserException(
				"DecorationKindInvalid",
				$"Mod “{decoration.Metadata.Name}”不是装饰 Mod，不能读取 decoration.json。 ");
		}

		var paths = new StoragePaths(workspace.ManagerRoot, workspace.LibraryRoot);
		var directory = CliDecorationPackagePlanArtifacts.ResolveSourceDirectory(paths, decoration);
		var planPath = Path.Combine(directory, "decoration.json");
		if (!File.Exists(planPath))
		{
			return Empty(
				decoration,
				new CliDecorationManagementDiagnostic(
					"DecorationPlanMissing",
					"error",
					true,
					"装饰 Mod 缺少 decoration.json，不能安全读取或启用。",
					planPath,
					CliProjections.FormatId(decoration.Id)));
		}

		DecorationPlanDocument plan;
		try
		{
			var json = await File.ReadAllTextAsync(planPath, cancellationToken).ConfigureAwait(false);
			plan = JsonSerializer.Deserialize<DecorationPlanDocument>(json, CliOutput.JsonOptions)
				?? throw new InvalidDataException("decoration.json 为空或无法解析。");
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
		{
			return Empty(
				decoration,
				new CliDecorationManagementDiagnostic(
					"DecorationPlanUnreadable",
					"error",
					true,
					$"无法读取 decoration.json：{exception.Message}",
					planPath,
					CliProjections.FormatId(decoration.Id),
					exception.ToString()));
		}

		var diagnostics = new List<CliDecorationManagementDiagnostic>();
		if (!string.Equals(plan.Format, "HD2ModManager.Decoration", StringComparison.OrdinalIgnoreCase))
		{
			diagnostics.Add(new CliDecorationManagementDiagnostic(
				"DecorationPlanFormatUnexpected",
				"warning",
				false,
				$"decoration.json 使用了未识别的 Format：“{plan.Format}”。将继续按兼容字段读取。",
				planPath,
				CliProjections.FormatId(decoration.Id)));
		}
		if (plan.Plan is null)
		{
			diagnostics.Add(new CliDecorationManagementDiagnostic(
				"DecorationAttachmentMissing",
				"error",
				true,
				"decoration.json 缺少 Plan 附着策略。",
				planPath,
				CliProjections.FormatId(decoration.Id)));
			return Empty(decoration, diagnostics.ToArray());
		}

		var decorationKey = decoration.Id.Value.ToString("N");
		var enabledHosts = state.DecorationActivations.TryGetValue(decorationKey, out var currentEnabled)
			? currentEnabled
			: new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var declaredHosts = new List<CliDecorationDeclaredHost>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var rawHostId in plan.Plan.TargetModGuids ?? [])
		{
			if (string.IsNullOrWhiteSpace(rawHostId) || !seen.Add(rawHostId))
			{
				diagnostics.Add(new CliDecorationManagementDiagnostic(
					"DecorationDeclaredHostInvalid",
					"warning",
					false,
					"decoration.json 包含空白或重复的目标主体 ID。",
					planPath,
					CliProjections.FormatId(decoration.Id)));
				continue;
			}
			if (!Guid.TryParse(rawHostId, out var parsedId))
			{
				diagnostics.Add(new CliDecorationManagementDiagnostic(
					"DecorationDeclaredHostInvalid",
					"warning",
					false,
					$"decoration.json 的目标主体 ID 无效：{rawHostId}。",
					planPath,
					CliProjections.FormatId(decoration.Id)));
				declaredHosts.Add(new CliDecorationDeclaredHost(rawHostId, null, null, false, false, false, null, 0));
				continue;
			}
			var hostId = new ModNodeId(parsedId);
			if (!state.Snapshot.Nodes.TryGetValue(hostId, out var host))
			{
				diagnostics.Add(new CliDecorationManagementDiagnostic(
					"DecorationDeclaredHostMissing",
					"warning",
					false,
					$"装饰声明的主体已不在当前 Mod 库中：{CliProjections.FormatId(hostId)}。",
					planPath,
					CliProjections.FormatId(decoration.Id)));
				declaredHosts.Add(new CliDecorationDeclaredHost(CliProjections.FormatId(hostId), null, null, false, false, enabledHosts.Contains(hostId.Value.ToString("N")), null, 0));
				continue;
			}
			var available = host.Metadata.Kind is ModNodeKind.Standard or ModNodeKind.Option && host.PatchGroups.Count != 0;
			if (!available)
			{
				diagnostics.Add(new CliDecorationManagementDiagnostic(
					"DecorationDeclaredHostUnsupported",
					"warning",
					false,
					$"装饰声明的主体“{host.Metadata.Name}”当前不是带 Patch 的普通 Mod 或选项 Mod。",
					planPath,
					CliProjections.FormatId(decoration.Id)));
			}
			declaredHosts.Add(new CliDecorationDeclaredHost(
				CliProjections.FormatId(host.Id),
				host.Metadata.Name,
				host.Metadata.Kind.ToString().ToLowerInvariant(),
				true,
				available,
				enabledHosts.Contains(host.Id.Value.ToString("N")),
				host.RelativePath,
				host.PatchGroups.Count));
		}
		if (declaredHosts.Count == 0)
		{
			diagnostics.Add(new CliDecorationManagementDiagnostic(
				"DecorationDeclaredHostsMissing",
				"error",
				true,
				"decoration.json 没有声明任何目标主体，不能启用。",
				planPath,
				CliProjections.FormatId(decoration.Id)));
		}

		return new CliDecorationManagementInspection(
			CliProjections.FormatId(decoration.Id),
			decoration.Metadata.Name,
			decoration.RelativePath,
			plan.SourceStorageMode,
			(plan.SourceUnits ?? [])
				.Select(unit => new CliDecorationStoredSourceUnit(FormatAssetKey(unit.TypeId, unit.FileId), unit.MeshInfoIndex, unit.BodyVariant, unit.Layer, unit.IsCulling))
				.ToArray(),
			new CliDecorationStoredAttachment(
				plan.Plan.TargetPart,
				plan.Plan.TargetBodyVariant,
				plan.Plan.DualVariantMode,
				plan.Plan.ReplaceWhenSourcePartLayerMatches,
				(plan.Plan.SourcePartLayers ?? []).ToArray()),
			declaredHosts,
			diagnostics);
	}

	private static CliDecorationManagementInspection Empty(ModNode decoration, params CliDecorationManagementDiagnostic[] diagnostics)
		=> new(
			CliProjections.FormatId(decoration.Id),
			decoration.Metadata.Name,
			decoration.RelativePath,
			"unknown",
			Array.Empty<CliDecorationStoredSourceUnit>(),
			new CliDecorationStoredAttachment(string.Empty, string.Empty, string.Empty, false, Array.Empty<string>()),
			Array.Empty<CliDecorationDeclaredHost>(),
			diagnostics);

	private static string FormatAssetKey(ulong typeId, ulong fileId)
		=> $"0x{typeId:x16}/0x{fileId:x16}";
}
