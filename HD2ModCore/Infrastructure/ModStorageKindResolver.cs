using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// <see cref="ModStorageKind"/> 的**唯一判定处**。
/// <para>
/// <b>为什么必须只有一处。</b>以前"这是哪种 Mod"散在各处，每处都自己
/// <c>Directory.Exists(某个子目录)</c> 或数根上的文件：详情页按一种规则给功能、
/// CLI 按另一种报 <c>kind</c>、重建服务又按第三种判 <c>isUnitMod</c>。
/// 三种规则之间没有对照表，于是出现"详情页看得见、CLI 说存储不完整"这类自相矛盾。
/// </para>
/// <para>
/// <b>判据只看来源与内容层，不看结构文件。</b>结构文件（section 组表、衣柜、素体）
/// 决定"这个 Mod 能做什么"，不决定"它是怎么来的"——把两者混起来会让"缺一个 sidecar"
/// 变成"这是另一种 Mod"，而那种误判会连带改变部署与重建行为。
/// </para>
/// </summary>
public sealed class ModStorageKindResolver : IModStorageKindResolver
{
	private readonly IPatchFileNameParser _patchFileNameParser;

	public ModStorageKindResolver(IPatchFileNameParser patchFileNameParser)
		=> _patchFileNameParser = patchFileNameParser ?? throw new ArgumentNullException(nameof(patchFileNameParser));

	public ModStorageKind Resolve(string modDirectory)
	{
		if (string.IsNullOrWhiteSpace(modDirectory) || !Directory.Exists(modDirectory))
			return ModStorageKind.Unknown;

		var hasSourceVpk = false;
		var hasRootPatch = false;
		try
		{
			foreach (var path in Directory.EnumerateFiles(modDirectory, "*", SearchOption.TopDirectoryOnly))
			{
				var name = Path.GetFileName(path);
				if (name.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
				{
					hasSourceVpk = true;
					continue;
				}
				if (_patchFileNameParser.TryParse(name, out var info) && info?.SidecarKind == PatchSidecarKind.Base)
					hasRootPatch = true;
			}
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or DirectoryNotFoundException)
		{
			return ModStorageKind.Unknown;
		}

		var hasBaseLayer = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(modDirectory) is not null;

		// 有来源 VPK ⇒ vpk2patch 系。即使根上还留着早期转换产物也仍按 VPK 版算：
		// 来源 VPK 才是它的权威输入，重建走的就是它。
		if (hasSourceVpk) return ModStorageKind.Vpk2Patch;
		if (hasBaseLayer) return ModStorageKind.Sectional;
		if (hasRootPatch) return ModStorageKind.Legacy;
		// 什么都没有：只有带逻辑标记才算容器；否则是个认不出来的空壳
		// （残留清理会处理它，这里**不要**把它当容器，否则它会被当合法节点保护起来）。
		return ModNodeStorageProtocol.TryReadLogicalNodeId(modDirectory, out _)
			? ModStorageKind.Logical
			: ModStorageKind.Unknown;
	}
}
