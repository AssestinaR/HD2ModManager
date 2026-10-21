using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2SourceModel;

namespace HD2SourceMigration.Retarget;

/// <summary>一次重定向的全部输入。装在一起是为了让"读"与"算"分开：读需要文件系统，算不需要。</summary>
public sealed record RetargetInputs(
	string VpkPath,
	string ModelStem,
	string MdlPath,
	SourceModelDocument Model,
	SourceGeometry Geometry,
	Hd2Rig Rig);

/// <summary>
/// 从"一个 VPK + 一份 HD2 骨架"跑到"一组顶点位置"的门面。
/// <para>
/// 这一层刻意只碰文件系统与三个下层工程，不含任何数学——数学全在 <see cref="Retargeter"/> 里，
/// 所以它可以在没有 VPK、没有游戏目录的情况下被单元测试直接驱动。
/// </para>
/// </summary>
public static class SourceRetargetPipeline
{
	/// <summary>读一个 VPK 里的源模型，并装配成骨架 + 几何。</summary>
	/// <param name="modelStem">模型名片段；<c>null</c> 时取 VPK 里最大的那个 <c>.mdl</c>。</param>
	public static RetargetInputs LoadSource(string vpkPath, string? modelStem = null)
	{
		ArgumentNullException.ThrowIfNull(vpkPath);
		using var archive = VpkArchive.Open(vpkPath);
		var files = SourceModelLocator.Resolve(archive, modelStem);
		var stem = files.Mdl.Path[..^4];
		var model = SourceModelReader.Read(archive.Read(files.Mdl));
		var vertices = SourceVertexReader.Read(archive.Read(files.Vvd), out _, out _);
		var index = SourceIndexReader.Read(archive.Read(files.Vtx), out _, out _);
		var geometry = SourceGeometryBuilder.Build(model, vertices, index);
		return new RetargetInputs(vpkPath, stem, files.Mdl.Path, model, geometry, Hd2Rig.Empty);
	}

	/// <summary>
	/// 读 HD2 的 avatar 骨架。<paramref name="boneHashPath"/> 是 <c>hash name</c> 逐行的文本表
	/// （HD2SDK / HD2-BatchTool 都带一份）；传 <c>null</c> 时骨架仍然可用，只是没有骨名，
	/// 而重定向需要按名字找骨，所以那种情况下管线会明确报错而不是静默错位。
	/// </summary>
	public static Hd2Rig LoadRig(string avatarTocPath, string? boneHashPath = null)
	{
		ArgumentNullException.ThrowIfNull(avatarTocPath);
		var toc = File.ReadAllBytes(avatarTocPath);
		return LoadRigFromTocBytes(toc, boneHashPath);
	}

	/// <summary>
	/// 同 <see cref="LoadRig(string, string?)"/>，但直接吃 TocData 字节。
	/// <para>
	/// <b>为什么需要它。</b>avatar Unit 是<b>游戏自己的资源</b>，就在游戏 data 的
	/// <c>18235e0c9ec0e636</c> 包里。生产路径应当从 <c>--game-data</c> 读出来直接用，
	/// 而不是要求用户先把那段 TocData 导成一个文件——GUI 里根本没有"导出一个 TocData"
	/// 这种操作，要求它等于要求用户去命令行手抄。
	/// </para>
	/// </summary>
	public static Hd2Rig LoadRigFromTocBytes(ReadOnlySpan<byte> avatarToc, string? boneHashPath = null)
	{
		var transforms = UnitMeshReader.ReadTransformInfoFromUnitToc(avatarToc);
		return LoadRigFromTransformInfo(transforms, boneHashPath);
	}

	/// <summary>同 <see cref="LoadRigFromTocBytes"/>，但直接吃已经解析好的骨架变换。</summary>
	public static Hd2Rig LoadRigFromTransformInfo(UnitTransformInfo transforms, string? boneHashPath = null)
	{
		ArgumentNullException.ThrowIfNull(transforms);
		var names = boneHashPath is null ? null : ReadBoneHash(boneHashPath);
		return Hd2RigReader.FromTransformInfo(transforms, names);
	}

	/// <summary>读 <c>hash name</c> 逐行的骨名表。</summary>
	public static UnitBoneNames ReadBoneHash(string path)
	{
		ArgumentNullException.ThrowIfNull(path);
		var hashes = new List<uint>();
		var names = new List<string>();
		foreach (var line in File.ReadLines(path))
		{
			var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length >= 2 && uint.TryParse(parts[0], out var hash))
			{
				hashes.Add(hash);
				names.Add(parts[1]);
			}
		}

		return new UnitBoneNames(hashes, names);
	}

	/// <summary>把源模型与 HD2 骨架合到一起，跑一次重定向。</summary>
	public static RetargetResult Run(
		RetargetInputs inputs,
		Hd2Rig rig,
		BodyPoseProfile? profile = null,
		RetargetOptions? options = null,
		Action<string, Vec3[]>? stageCallback = null)
	{
		ArgumentNullException.ThrowIfNull(inputs);
		ArgumentNullException.ThrowIfNull(rig);
		var retargeter = new Retargeter(inputs.Model, inputs.Geometry, rig, profile, options, stageCallback);
		return retargeter.Run();
	}
}
