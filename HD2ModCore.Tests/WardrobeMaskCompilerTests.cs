using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 规格 → 掩码 → 校验这条链的契约测试。
///
/// 这里锁的三件事：
/// 1. 指纹**只能**来自映射表（规格里写不出指纹，也就写不错）；
/// 2. 任何一条错都要一次报全，且**有错就不写文件**——半份掩码会让运行时关错几何；
/// 3. 重建之后指纹变了必须被 <c>validate</c> 抓出来，而不是静默继续用。
/// </summary>
public sealed class WardrobeMaskCompilerTests
{
	private const string UnitA = "af431352d9b95e83";
	private const string UnitB = "cae35ee15168145f";
	private const string UnitC = "00000000000000ff";
	private const string FingerprintA0 = "a47787804c45e1a9-8852";
	private const string FingerprintA1 = "a648fd251c740b12-1024";
	private const string FingerprintB0 = "aadf05da56ef08cb-822";
	private const uint Target = 1745464610;

	private static WardrobeSectionMapDocument Map => WardrobeSectionMapTests.CreateMap();

	private static WardrobeMaskSpec Spec(
		IReadOnlyList<WardrobeMaskSpecSet>? sets = null,
		IReadOnlyList<WardrobeMaskSpecTarget>? targets = null,
		WardrobeMaskSpecPatch? patch = null,
		int revision = 3,
		bool enabled = true)
		=> new(
			WardrobeMaskSpec.CurrentSchema,
			revision,
			Sections: null,
			patch,
			sets ?? new[] { new WardrobeMaskSpecSet(1, new[] { new WardrobeMaskSpecSection(UnitA, 0) }) },
			targets ?? new[] { new WardrobeMaskSpecTarget(Target, 1) },
			enabled);

	private static WardrobeMaskDiagnostic[] Errors(WardrobeMaskCompileResult result)
		=> result.Diagnostics.Where(diagnostic => diagnostic.IsError).ToArray();

	[Fact]
	public void Compile_TakesFingerprintsFromTheMap()
	{
		var result = WardrobeMaskCompiler.Compile(
			Spec(
				new[] { new WardrobeMaskSpecSet(1, new[] { new WardrobeMaskSpecSection(UnitA, 0), new WardrobeMaskSpecSection(UnitA, 1) }) },
				new[] { new WardrobeMaskSpecTarget(Target, 1) }),
			Map);
		Assert.True(result.IsValid);
		Assert.Empty(Errors(result));
		Assert.Equal(
			"schema=2\nrevision=3\nenabled=true\n"
			+ "off=1," + UnitA + ",0," + FingerprintA0 + "\n"
			+ "off=1," + UnitA + ",1," + FingerprintA1 + "\n"
			+ "target=" + Target + ",1\n",
			WardrobeMaskFile.Serialize(result.Document));
	}

	[Fact]
	public void Compile_AcceptsUnitIdWithPrefixAndUppercase()
	{
		var result = WardrobeMaskCompiler.Compile(
			Spec(new[] { new WardrobeMaskSpecSet(1, new[] { new WardrobeMaskSpecSection("0x" + UnitA.ToUpperInvariant(), 0) }) }),
			Map);
		Assert.True(result.IsValid);
		var entry = Assert.Single(result.Document.Entries);
		Assert.Equal(UnitA, entry.Unit);
	}

	[Fact]
	public void Compile_RejectsUnitThatIsNotInThePatch()
	{
		var result = WardrobeMaskCompiler.Compile(
			Spec(new[] { new WardrobeMaskSpecSet(1, new[] { new WardrobeMaskSpecSection(UnitC, 0) }) }),
			Map);
		Assert.False(result.IsValid);
		var error = Assert.Single(Errors(result));
		Assert.Equal("unit_not_in_patch", error.Code);
		Assert.Contains(UnitC, error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Compile_RejectsSectionIndexOutOfRange()
	{
		var result = WardrobeMaskCompiler.Compile(
			Spec(new[] { new WardrobeMaskSpecSet(1, new[] { new WardrobeMaskSpecSection(UnitA, 7) }) }),
			Map);
		Assert.False(result.IsValid);
		var error = Assert.Single(Errors(result));
		Assert.Equal("section_not_in_unit", error.Code);
	}

	[Fact]
	public void Compile_RejectsDuplicateSectionInOneSet()
	{
		var result = WardrobeMaskCompiler.Compile(
			Spec(new[]
			{
				new WardrobeMaskSpecSet(1, new[]
				{
					new WardrobeMaskSpecSection(UnitA, 0),
					new WardrobeMaskSpecSection(UnitA, 0),
				}),
			}),
			Map);
		Assert.False(result.IsValid);
		var error = Assert.Single(Errors(result));
		Assert.Equal("entry_duplicate", error.Code);
	}

	[Fact]
	public void Compile_RejectsTargetPointingAtUndeclaredSet()
	{
		var result = WardrobeMaskCompiler.Compile(
			Spec(targets: new[] { new WardrobeMaskSpecTarget(Target, 9) }),
			Map);
		Assert.False(result.IsValid);
		var error = Assert.Single(Errors(result));
		Assert.Equal("target_set_unknown", error.Code);
	}

	[Fact]
	public void Compile_EmptySetIsAValidExplicitException()
	{
		var result = WardrobeMaskCompiler.Compile(
			Spec(
				new[] { new WardrobeMaskSpecSet(4) },
				new[] { new WardrobeMaskSpecTarget(Target, 4) }),
			Map);
		Assert.True(result.IsValid);
		Assert.Empty(result.Document.Entries);
		Assert.Equal("schema=2\nrevision=3\nenabled=true\ntarget=" + Target + ",4\n", WardrobeMaskFile.Serialize(result.Document));
	}

	[Fact]
	public void Compile_WarnsAboutSetsNoTargetUses()
	{
		var result = WardrobeMaskCompiler.Compile(
			Spec(sets: new[]
			{
				new WardrobeMaskSpecSet(1, new[] { new WardrobeMaskSpecSection(UnitA, 0) }),
				new WardrobeMaskSpecSet(2, new[] { new WardrobeMaskSpecSection(UnitB, 0) }),
			}),
			Map);
		Assert.True(result.IsValid);
		var warning = Assert.Single(result.Diagnostics);
		Assert.Equal("set_unused", warning.Code);
	}

	[Fact]
	public void Compile_RejectsPatchFingerprintMismatch()
	{
		var result = WardrobeMaskCompiler.Compile(
			Spec(patch: new WardrobeMaskSpecPatch(Sha256: new string('c', 64))),
			Map);
		Assert.False(result.IsValid);
		var error = Assert.Single(Errors(result));
		Assert.Equal("patch_sha256_mismatch", error.Code);
	}

	[Fact]
	public void Compile_RejectsDeclaredGpuFingerprintWhenTheMapHasNone()
	{
		var map = Map with { Patch = Map.Patch with { GpuResourcesSha256 = null } };
		var result = WardrobeMaskCompiler.Compile(
			Spec(patch: new WardrobeMaskSpecPatch(GpuResourcesSha256: new string('b', 64))),
			map);
		Assert.False(result.IsValid);
		var error = Assert.Single(Errors(result));
		Assert.Equal("patch_gpu_sha256_mismatch", error.Code);
	}

	[Fact]
	public void Compile_AcceptsMatchingPatchFingerprint()
	{
		var result = WardrobeMaskCompiler.Compile(
			Spec(patch: new WardrobeMaskSpecPatch(Map.Patch.Sha256, Map.Patch.GpuResourcesSha256)),
			Map);
		Assert.True(result.IsValid);
		Assert.Empty(result.Diagnostics);
	}

	[Fact]
	public void Compile_RejectsUnsupportedSpecSchema()
	{
		var spec = Spec() with { Schema = "hd2mod.wardrobe-spec/2" };
		var result = WardrobeMaskCompiler.Compile(spec, Map);
		Assert.False(result.IsValid);
		var error = Assert.Single(Errors(result));
		Assert.Equal("spec_schema_unsupported", error.Code);
	}

	[Fact]
	public void Parse_RejectsUnknownMembers()
	{
		// 把 sections 打成 section 这类手误必须报错：默认行为会静默忽略，
		// 然后编译出一份"一条都没关"的掩码，看起来还挺正常。
		var exception = Assert.Throws<InvalidDataException>(() => WardrobeMaskCompiler.Parse(
			"{ \"schema\": \"hd2mod.wardrobe-spec/1\", \"revision\": 1, \"section\": \"x.json\" }"));
		Assert.Contains("JSON", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Validate_AcceptsMaskCompiledFromTheSameMap()
	{
		var compiled = WardrobeMaskCompiler.Compile(
			Spec(new[]
			{
				new WardrobeMaskSpecSet(1, new[] { new WardrobeMaskSpecSection(UnitA, 0), new WardrobeMaskSpecSection(UnitA, 1) }),
			}),
			Map);
		var validation = WardrobeMaskCompiler.Validate(compiled.Document, Map);
		Assert.True(validation.IsValid);
		Assert.Empty(validation.Diagnostics);
		Assert.Equal(2, validation.EntryCount);
		Assert.Equal(2, validation.ResolvedEntryCount);
		var target = Assert.Single(validation.Targets);
		Assert.Equal(Target, target.ItemId);
		Assert.Equal(2, target.HiddenSections);
		// 这个 fixture 里 UnitA 只有 2 个 section，两个都关了 = 整块关闭。
		// 这不是"错误"，而是一个运行时可以用来跳过 Piece 注册的事实，必须报出来。
		Assert.Equal(UnitA, Assert.Single(target.FullyHiddenUnits));
	}

	[Fact]
	public void Validate_CatchesFingerprintDrift()
	{
		var compiled = WardrobeMaskCompiler.Compile(Spec(), Map);
		// 模拟重建：同一个下标的 section 内容变了。
		var rebuilt = Map with
		{
			Units = Map.Units
				.Select(unit => unit.FileId == UnitA
					? unit with
					{
						Sections = unit.Sections
							.Select(section => section.Index == 0
								? section with { Fingerprint = "ffffffffffffffff-8852" }
								: section)
							.ToArray(),
					}
					: unit)
				.ToArray(),
		};
		var validation = WardrobeMaskCompiler.Validate(compiled.Document, rebuilt);
		Assert.False(validation.IsValid);
		var error = Assert.Single(validation.Diagnostics);
		Assert.Equal("fingerprint_mismatch", error.Code);
		Assert.Equal(0, validation.ResolvedEntryCount);
	}

	[Fact]
	public void Validate_CatchesUnitThatVanishedFromThePatch()
	{
		var compiled = WardrobeMaskCompiler.Compile(Spec(), Map);
		var withoutUnitA = Map with { Units = Map.Units.Where(unit => unit.FileId != UnitA).ToArray() };
		var validation = WardrobeMaskCompiler.Validate(compiled.Document, withoutUnitA);
		Assert.False(validation.IsValid);
		var error = Assert.Single(validation.Diagnostics);
		Assert.Equal("unit_not_in_patch", error.Code);
	}

	[Fact]
	public void Validate_ReportsFullyHiddenUnits()
	{
		var compiled = WardrobeMaskCompiler.Compile(
			Spec(sets: new[] { new WardrobeMaskSpecSet(1, new[] { new WardrobeMaskSpecSection(UnitB, 0) }) }),
			Map);
		var validation = WardrobeMaskCompiler.Validate(compiled.Document, Map);
		Assert.True(validation.IsValid);
		var target = Assert.Single(validation.Targets);
		Assert.Equal(1, target.HiddenSections);
		Assert.Equal(UnitB, Assert.Single(target.FullyHiddenUnits));
	}

	[Fact]
	public void Validate_WarnsWhenATargetHidesNothing()
	{
		var compiled = WardrobeMaskCompiler.Compile(
			Spec(new[] { new WardrobeMaskSpecSet(4) }, new[] { new WardrobeMaskSpecTarget(Target, 4) }),
			Map);
		var validation = WardrobeMaskCompiler.Validate(compiled.Document, Map);
		Assert.True(validation.IsValid);
		var warning = Assert.Single(validation.Diagnostics);
		Assert.Equal("target_set_empty", warning.Code);
		var target = Assert.Single(validation.Targets);
		Assert.Equal(0, target.HiddenSections);
	}

	[Fact]
	public void Validate_WarnsAboutSetsNoTargetReferences()
	{
		var document = new WardrobeMaskDocument(
			1,
			true,
			new[]
			{
				new WardrobeMaskEntry(1, UnitA, 0, FingerprintA0),
				new WardrobeMaskEntry(2, UnitB, 0, FingerprintB0),
			},
			new Dictionary<uint, uint> { [Target] = 1 });
		var validation = WardrobeMaskCompiler.Validate(document, Map);
		Assert.True(validation.IsValid);
		var warning = Assert.Single(validation.Diagnostics);
		Assert.Equal("set_unreferenced", warning.Code);
	}

	[Fact]
	public void Validate_EmptyMaskIsValid()
	{
		var validation = WardrobeMaskCompiler.Validate(WardrobeMaskDocument.Empty, Map);
		Assert.True(validation.IsValid);
		Assert.Empty(validation.Diagnostics);
		Assert.Equal(0, validation.EntryCount);
		Assert.Empty(validation.Targets);
	}
}
