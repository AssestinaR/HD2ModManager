using System.Buffers.Binary;
using System.Text;
using HD2SourceMigration.VpkConversion;
using Xunit;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 规划阶段的职责是<b>分类</b>，不是让整批导入失败。
/// <para>
/// 实测反馈：用户的一个角色包里有一个模型版本号不认识（v13617），结果整个包导入直接变成
/// <c>UnhandledError</c>——另外几十个本来能转的 VPK 也一起没了。<c>SourceModelReader</c>
/// 遇到不认识的版本抛的是 <see cref="NotSupportedException"/>，而规划器的 catch 只收了
/// <see cref="IOException"/> / <see cref="InvalidDataException"/> / <see cref="KeyNotFoundException"/>。
/// </para>
/// <para>
/// 这里用合成 VPK 把三条降级路径钉住：版本不认识、模型太小、几何缺失，都必须变成
/// "跳过这一个包 + 一句能看懂的理由"，而不是异常。
/// </para>
/// </summary>
public sealed class VpkImportPlannerTests : IDisposable
{
	private readonly List<string> temporaryFiles = [];

	public void Dispose()
	{
		foreach (var path in temporaryFiles)
		{
			try { File.Delete(path); }
			catch (IOException) { }
		}
	}

	[Fact]
	public void Plan_UnsupportedModelVersion_IsSkippedWithTheVersionInTheReason()
	{
		var vpk = WriteVpk(
			("survivor.mdl", "models/survivors", BuildMdl(version: 13617)),
			("survivor.vvd", "models/survivors", new byte[64]),
			("survivor.dx90.vtx", "models/survivors", new byte[64]));

		var plan = VpkImportPlanner.Plan([vpk]);

		var candidate = Assert.Single(plan.Candidates);
		Assert.False(candidate.IsConvertible);
		Assert.Equal(VpkImportKind.Unsupported, candidate.Kind);
		Assert.Contains("13617", candidate.SkipReason, StringComparison.Ordinal);
		// 理由必须带上具体是哪个文件，否则支持侧无从下手。
		Assert.Contains("survivor.mdl", candidate.SkipReason, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Plan_TruncatedModel_IsSkippedInsteadOfThrowing()
	{
		var vpk = WriteVpk(
			("survivor.mdl", "models/survivors", Encoding.ASCII.GetBytes("IDST")),
			("survivor.vvd", "models/survivors", new byte[64]),
			("survivor.dx90.vtx", "models/survivors", new byte[64]));

		var plan = VpkImportPlanner.Plan([vpk]);

		var candidate = Assert.Single(plan.Candidates);
		Assert.False(candidate.IsConvertible);
		Assert.False(string.IsNullOrWhiteSpace(candidate.SkipReason));
	}

	[Fact]
	public void Plan_OneBadVpk_DoesNotStopTheOthersFromBeingClassified()
	{
		var badModel = WriteVpk(
			("survivor.mdl", "models/survivors", BuildMdl(version: 13617)),
			("survivor.vvd", "models/survivors", new byte[64]),
			("survivor.dx90.vtx", "models/survivors", new byte[64]));
		var material = WriteVpk(("white.vmt", "materials/lunalice", Encoding.UTF8.GetBytes("LightmappedGeneric")));

		var plan = VpkImportPlanner.Plan([badModel, material]);

		Assert.Equal(2, plan.Candidates.Count);
		Assert.Contains(plan.Candidates, item =>
			item.VpkPath.Equals(badModel, StringComparison.OrdinalIgnoreCase)
			&& item.SkipReason is not null
			&& item.SkipReason.Contains("13617", StringComparison.Ordinal));
	}

	/// <summary>一个只有头部的 MDL：magic + 版本号 + 补零，够 408 字节的门槛。</summary>
	private static byte[] BuildMdl(int version)
	{
		var mdl = new byte[512];
		Encoding.ASCII.GetBytes("IDST").CopyTo(mdl, 0);
		BinaryPrimitives.WriteInt32LittleEndian(mdl.AsSpan(4), version);
		return mdl;
	}

	private string WriteVpk(params (string Name, string Directory, byte[] Content)[] files)
	{
		var path = Path.Combine(Path.GetTempPath(), "hd2-planner-" + Guid.NewGuid().ToString("N") + ".vpk");
		File.WriteAllBytes(path, BuildVpk(files));
		temporaryFiles.Add(path);
		return path;
	}

	/// <summary>VPK v1：12 字节头 + 目录树 + 数据区；条目 18 字节。</summary>
	private static byte[] BuildVpk((string Name, string Directory, byte[] Content)[] files)
	{
		const int headerSize = 12;
		const int entrySize = 18;

		var split = files.Select(file =>
		{
			var dot = file.Name.LastIndexOf('.');
			return (file.Directory, Extension: file.Name[(dot + 1)..], Stem: file.Name[..dot], file.Content);
		}).ToArray();

		var offsets = new int[split.Length];
		var payloads = new List<byte>();
		for (var index = 0; index < split.Length; index++)
		{
			offsets[index] = payloads.Count;
			payloads.AddRange(split[index].Content);
		}

		var tree = new List<byte>();
		foreach (var extensionGroup in split.Select((item, index) => (item, index)).GroupBy(pair => pair.item.Extension))
		{
			WriteString(tree, extensionGroup.Key);
			foreach (var directoryGroup in extensionGroup.GroupBy(pair => pair.item.Directory))
			{
				WriteString(tree, directoryGroup.Key);
				foreach (var (item, index) in directoryGroup)
				{
					WriteString(tree, item.Stem);
					var entry = new byte[entrySize];
					BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(4), 0);
					BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(6), 0x7FFF);
					BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(8), (uint)offsets[index]);
					BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(12), (uint)item.Content.Length);
					BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(16), 0xFFFF);
					tree.AddRange(entry);
				}

				WriteString(tree, string.Empty);
			}

			WriteString(tree, string.Empty);
		}

		WriteString(tree, string.Empty);

		var result = new byte[headerSize + tree.Count + payloads.Count];
		BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0), 0x55AA1234);
		BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)tree.Count);
		tree.CopyTo(result, headerSize);
		payloads.CopyTo(result, headerSize + tree.Count);
		return result;
	}

	private static void WriteString(List<byte> buffer, string value)
	{
		buffer.AddRange(Encoding.UTF8.GetBytes(value));
		buffer.Add(0);
	}
}
