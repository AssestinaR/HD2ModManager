namespace HD2ModCore.Domain;

/// <summary>
/// <c>sectional/patches.json</c> 的内容：说明基底目录里每一个 patch 组是"谁"。
/// <para>
/// <b>为什么不复用根上的转换报告。</b>转换报告描述的是"这一次转换产出了什么"；而基底目录
/// 里最终会有转换之后又被增删的东西（换甲产物、材质变体）。所以目录需要一份**它自己的**
/// 清单：读的人不必回溯历史，直接看这个文件就知道每个 <c>patch_N</c> 是 unit 还是材质、
/// 来自哪个来源 VPK、对应哪个变体。
/// </para>
/// <para>
/// <b>它是派生的，允许缺失或过期。</b>真正的权威是目录里实际存在的 patch 文件；清单只是
/// 让目录可读、让工具不必靠文件名猜。所以读不到时一律**回退到按目录列举现推**，不报错。
/// </para>
/// </summary>
public static class SectionalPatchManifest
{
	public const string FileName = "patches.json";

	public const string CurrentSchema = "hd2mod.patch-groups/1";

	/// <summary>能读的 schema；将来加字段时把新值加进来，旧值继续能读。</summary>
	public static IReadOnlyList<string> ReadableSchemas { get; } = [CurrentSchema];
}

/// <summary>一个 patch 组在 Mod 里扮演的角色。</summary>
public enum SectionalPatchRole
{
	/// <summary>认不出来（清单里没有对应记录，或文件名不符合约定）。</summary>
	Unknown = 0,

	/// <summary>Unit 几何。约定恒为 <c>patch_0</c>。</summary>
	Unit,

	/// <summary>材质 / 贴图。约定从 <c>patch_1</c> 起向后排。</summary>
	Material,
}

/// <param name="PatchIndex">文件名里的序号（<c>&lt;archive&gt;.patch_N</c> 的 N）。</param>
/// <param name="ArchiveHex16">同名 patch 的目标归档。</param>
/// <param name="Role">unit 还是材质。</param>
/// <param name="SourceVpkName">这个 patch 由哪个来源 VPK 转出来；认不出来时为 <c>null</c>。</param>
/// <param name="Variant">材质变体名（P3 起使用）；未命名时为 <c>null</c>。</param>
/// <param name="UnitCount">里面有多少个 unit；未知时为 <c>null</c>。</param>
/// <param name="MaterialCount">里面有多少个材质；未知时为 <c>null</c>。</param>
/// <param name="TextureCount">里面有多少张贴图；未知时为 <c>null</c>。</param>
public sealed record SectionalPatchGroupEntry(
	int PatchIndex,
	string ArchiveHex16,
	SectionalPatchRole Role,
	string? SourceVpkName = null,
	string? Variant = null,
	int? UnitCount = null,
	int? MaterialCount = null,
	int? TextureCount = null);

public sealed record SectionalPatchManifestDocument(
	string Schema,
	DateTimeOffset GeneratedUtc,
	IReadOnlyList<SectionalPatchGroupEntry> Patches);
