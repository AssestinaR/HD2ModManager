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

	/// <summary>
	/// 宿主自己那次转换产出的材质 patch 在选项表里的名字。
	/// <para>
	/// 它和折并进来的材质包是**同一类东西**——都是一个 patch、都能勾选。
	/// 单独给它一个名字，是为了让用户在选项表里一眼认出"这是模型自带的，不是后来加的包"。
	/// </para>
	/// </summary>
	public const string BuiltInMaterialOptionName = "模型内置材质";

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
/// <param name="OptionName">
/// 材质**选项**的显示名；unit patch 为 <c>null</c>。
/// <para>
/// 每个材质选项恰好对应一个 patch。宿主自己那次转换产出的材质 patch 也是选项之一，
/// 名字固定为 <see cref="BuiltInMaterialOptionName"/>（"模型内置材质"）——它和别的选项
/// 一样可以被勾选/取消，不该被当成"永远生效的基底"而特殊对待。
/// </para>
/// </param>
/// <param name="UnitCount">里面有多少个 unit；未知时为 <c>null</c>。</param>
/// <param name="MaterialCount">里面有多少个材质；未知时为 <c>null</c>。</param>
/// <param name="TextureCount">里面有多少张贴图；未知时为 <c>null</c>。</param>
public sealed record SectionalPatchGroupEntry(
	int PatchIndex,
	string ArchiveHex16,
	SectionalPatchRole Role,
	string? SourceVpkName = null,
	string? OptionName = null,
	int? UnitCount = null,
	int? MaterialCount = null,
	int? TextureCount = null);

public sealed record SectionalPatchManifestDocument(
	string Schema,
	DateTimeOffset GeneratedUtc,
	IReadOnlyList<SectionalPatchGroupEntry> Patches);
