using static HD2SourceModel.Tests.SourceFormatFixtures;

namespace HD2SourceModel.Tests;

/// <summary>
/// VTX 解析：bodypart/model/LOD/mesh/strip group 这棵树，以及"两种 strip group 步长"的容错。
/// </summary>
public sealed class SourceIndexReaderTests
{
	private const int StrideV6 = 25;
	private const int StrideV7 = 33;

	/// <summary>一个 bodypart / 一个 model / 一个 mesh，含若干 strip group。</summary>
	private static VtxBodyPartSpec[] OneMesh(params VtxGroupSpec[] groups) =>
		[BodyPart(SingleMesh(groups))];

	/// <summary>两个 strip group，取值可区分，用来验证"解析出来的确实是这一组"。</summary>
	private static VtxBodyPartSpec[] TwoGroups() =>
		OneMesh(
			Group([0, 1, 2], [0, 1, 2, 0, 2, 1]),
			Group([3, 4, 5], [2, 1, 0, 0, 1, 2]));

	private static void AssertTwoGroupsParsed(IReadOnlyList<SourceIndexBodyPart> bodyParts)
	{
		var bodyPart = Assert.Single(bodyParts);
		var model = Assert.Single(bodyPart.Models);
		var lod = Assert.Single(model.Lods);
		var mesh = Assert.Single(lod.Meshes);
		var groups = mesh.Groups.ToArray();

		Assert.Equal(2, groups.Length);
		Assert.Equal(new ushort[] { 0, 1, 2 }, groups[0].OriginalVertices);
		Assert.Equal(new ushort[] { 0, 1, 2, 0, 2, 1 }, groups[0].Indices);
		Assert.Equal(new ushort[] { 3, 4, 5 }, groups[1].OriginalVertices);
		Assert.Equal(new ushort[] { 2, 1, 0, 0, 1, 2 }, groups[1].Indices);
	}

	[Fact]
	public void Read_ParsesTheV6Layout()
	{
		var bodyParts = SourceIndexReader.Read(BuildVtx(6, StrideV6, TwoGroups()), out var version, out var usedV7);

		Assert.Equal(6, version);
		Assert.False(usedV7);
		AssertTwoGroupsParsed(bodyParts);
	}

	[Fact]
	public void Read_ParsesTheV7Layout()
	{
		var bodyParts = SourceIndexReader.Read(BuildVtx(7, StrideV7, TwoGroups()), out var version, out var usedV7);

		Assert.Equal(7, version);
		Assert.True(usedV7);
		AssertTwoGroupsParsed(bodyParts);
	}

	[Fact]
	public void Read_TrustsTheActualLayoutOverTheVersionNumber()
	{
		// 实测存在版本号写着 7、strip group 步长却是 25 的文件。头里的版本号不足以判断步长，
		// 所以必须"两种都试"，而不是信版本号。这里版本说 7，真实步长是 25。
		var bodyParts = SourceIndexReader.Read(BuildVtx(7, StrideV6, TwoGroups()), out var version, out var usedV7);

		Assert.Equal(7, version);
		Assert.False(usedV7);
		AssertTwoGroupsParsed(bodyParts);
	}

	[Fact]
	public void Read_AlsoHandlesAVersion6FileWithTheV7Stride()
	{
		var bodyParts = SourceIndexReader.Read(BuildVtx(6, StrideV7, TwoGroups()), out _, out var usedV7);

		Assert.True(usedV7);
		AssertTwoGroupsParsed(bodyParts);
	}

	[Fact]
	public void Read_RejectsUnsupportedVersion()
	{
		Assert.Throws<NotSupportedException>(() =>
			SourceIndexReader.Read(BuildVtx(5, StrideV6, TwoGroups()), out _, out _));
	}

	[Fact]
	public void Read_RejectsAnIndexPastTheStripGroupVertexCount()
	{
		var bodyParts = OneMesh(Group([0, 1, 2], [0, 1, 5]));

		var error = Assert.Throws<InvalidDataException>(() =>
			SourceIndexReader.Read(BuildVtx(6, StrideV6, bodyParts), out _, out _));

		Assert.Contains("超出顶点数", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Read_RejectsAnIndexCountThatIsNotAMultipleOfThree()
	{
		// 这一层必须是三角形列表；不是 3 的倍数就说明步长选错了。
		var bodyParts = OneMesh(Group([0, 1, 2], [0, 1, 2, 0]));

		var error = Assert.Throws<InvalidDataException>(() =>
			SourceIndexReader.Read(BuildVtx(6, StrideV6, bodyParts), out _, out _));

		Assert.Contains("3 的倍数", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Read_RejectsATruncatedHeader()
	{
		Assert.Throws<InvalidDataException>(() => SourceIndexReader.Read(new byte[16], out _, out _));
	}

	[Fact]
	public void Read_ReportsBothStrideFailuresWhenNeitherLayoutWorks()
	{
		// 报错必须能定位问题，而不是只说"解不开"。这里第二个 strip group 的索引越界，
		// 两种步长都会失败（但失败原因不同），所以两条原因都应该出现在消息里。
		var bodyParts = OneMesh(
			Group([0, 1, 2], [0, 1, 2, 0, 2, 1]),
			Group([3, 4, 5], [2, 1, 5]));

		var error = Assert.Throws<InvalidDataException>(() =>
			SourceIndexReader.Read(BuildVtx(6, StrideV6, bodyParts), out _, out _));

		Assert.Contains("v6/25", error.Message, StringComparison.Ordinal);
		Assert.Contains("v7/33", error.Message, StringComparison.Ordinal);
	}
}
