using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <param name="FilesByName">会进游戏的文件（按文件名）。</param>
/// <param name="SkipReason">这一条不部署的原因；部署了就为 <c>null</c>。</param>
public sealed record ModDeployableSet(
	IReadOnlyDictionary<string, string> FilesByName,
	string? SkipReason);

/// <summary>
/// 一个 Mod 里**真正会进游戏**的文件。判定只看"有哪一层"，不逐个 patch 判断。
/// <para>
/// 顺序（与部署器的实际行为一一对应）：
/// <list type="number">
/// <item><b>Overwrite 里有有效 patch</b> ⇒ 只部署它，这个 Mod 结束。"有效"= 名字像 patch
/// <b>且</b>文件头是 patch TOC 魔数（见 <see cref="HasReadablePatchToc"/>）。</item>
/// <item><b>sectional / vpk2patch 类</b>：有基底目录 ⇒ 就部署它；没有 ⇒ 跳过这条并说明原因。</item>
/// <item><b>legacy / logical</b>：根目录有 patch ⇒ 部署；没有 ⇒ 跳过这条并说明原因。</item>
/// </list>
/// </para>
/// <para>
/// <b>判定不写盘。</b>这一步只回答"哪些文件会进游戏"，一个字节都不创建——因为
/// <c>deploy plan</c> 用的就是它，而计划是只读的。把基底硬链接进 Overwrite 是**写操作**，
/// 它发生在用户真正动手的地方（改材质选择、折并），不是发生在"看一眼会发生什么"的时候。
/// 实测踩过：跑一次计划就在 40 多个 Mod 里凭空出现 <c>Overwrite/</c>，用户完全没预期。
/// </para>
/// <para>
/// <b>为什么不需要"每个槽位在基底里必须有一份默认版"。</b>那条规则是我多加的约束：
/// 部署器根本不按槽位判断，它按"这一层在不在"判断。基底里有什么就链什么，
/// 少一个槽位也只是少一个可选材质，不影响部署。
/// </para>
/// </summary>
public static class ModDeployableFiles
{
	public static ModDeployableSet Resolve(string nodeDirectory, IPatchFileNameParser parser)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(nodeDirectory);
		ArgumentNullException.ThrowIfNull(parser);

		var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(nodeDirectory);

		// ① 生效层已经有内容 ⇒ 它就是全部，不再看任何别的层。
		if (HasReadablePatchToc(overwrite, parser))
			return new ModDeployableSet(EnumerateFiles(overwrite), null);

		var kind = new ModStorageKindResolver(parser).Resolve(nodeDirectory);

		// ② sectional / vpk2patch：基底就是答案，把它物化成生效层。
		if (kind is ModStorageKind.Sectional or ModStorageKind.Vpk2Patch)
		{
			var payload = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(nodeDirectory);
			if (payload is null)
				return new ModDeployableSet(Empty, $"这个 Mod 是 {kind} 类，但没有基底目录（sectional/），没有可部署的 patch。");
			return new ModDeployableSet(EnumerateFiles(payload), null);
		}

		// ③ legacy / logical：patch 在根目录上。
		if (HasValidPatch(nodeDirectory, parser))
			return new ModDeployableSet(EnumerateFiles(nodeDirectory), null);

		return new ModDeployableSet(Empty,
			$"这个 Mod 是 {kind} 类，根目录也没有 patch，没有可部署的东西。");
	}

	private static readonly Dictionary<string, string> Empty = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// 目录里至少有一份"能被解析成 patch"的文件——不要求每一个都是，也**不读文件内容**。
	/// <para>用于"根目录有没有东西可部署"这种低成本判断：部署一份读不出来的文件不会有别的副作用。</para>
	/// </summary>
	private static bool HasValidPatch(string directory, IPatchFileNameParser parser)
	{
		if (!Directory.Exists(directory)) return false;
		foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
		{
			if (parser.TryParse(Path.GetFileName(path), out var info)
				&& info is not null
				&& info.SidecarKind == PatchSidecarKind.Base)
				return true;
		}
		return false;
	}

	/// <summary>
	/// 目录里至少有一份**读得出来**的 patch：名字像，且文件头是 patch TOC 的魔数。
	/// <para>
	/// <b>为什么 <c>Overwrite/</c> 的接管必须用这一条、而不能只看名字。</b>
	/// 接管是"整个 Mod 只剩这一层"，代价最大，所以要最硬的证据。只看名字时，
	/// 一次中断留下的半截文件、或者用户手工塞进 <c>Overwrite/</c> 的同名文件，都会让这个 Mod
	/// 变成"只部署它"：基线层里的模型与材质一个都不部署，用户看到的是"模型在、材质没了"。
	/// 而重新转换不碰 <c>Overwrite/</c>，所以重构建怎么修都好不了，只有删掉重加才行 ——
	/// 社区反馈的正是这个形状（2026-11-07，1.5.0 之后）。
	/// </para>
	/// <para>
	/// 判据同时与 <see cref="ModNodePayloadLayout.EnumerateEffectiveFilesByName"/> 对齐：
	/// 那边一直要求读得出来的 TOC，这边当初只解析了文件名，两套判定对着同一批文件给出不同答案。
	/// </para>
	/// </summary>
	private static bool HasReadablePatchToc(string directory, IPatchFileNameParser parser)
	{
		if (!Directory.Exists(directory)) return false;
		foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
		{
			if (parser.TryParse(Path.GetFileName(path), out var info)
				&& info is not null
				&& info.SidecarKind == PatchSidecarKind.Base
				&& ModNodePayloadLayout.IsPatchToc(path))
				return true;
		}
		return false;
	}

	private static Dictionary<string, string> EnumerateFiles(string directory)
	{
		var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
			files[Path.GetFileName(path)] = path;
		return files;
	}
}
