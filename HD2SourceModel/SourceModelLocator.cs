namespace HD2SourceModel;

/// <summary>一个源模型需要的那三个文件。</summary>
/// <param name="Mdl">骨架、bodypart 结构与 jigglebone 参数。</param>
/// <param name="Vvd">顶点位置、法线、UV 与蒙皮权重。</param>
/// <param name="Vtx">三角形索引。</param>
public sealed record SourceModelFiles(VpkEntry Mdl, VpkEntry Vvd, VpkEntry Vtx);

/// <summary>
/// 在 VPK 里找出一个源模型的 MDL / VVD / VTX 三件套。
/// <para>
/// 三个文件必然同名（只差扩展名），所以定位的难点只在"选哪个模型"：一个 VPK 里通常同时有主模型、
/// 它的 <c>_light</c> 变体和第一人称手臂。默认取<b>体积最大</b>的那个，因为主模型总是比其余两个大得多。
/// </para>
/// </summary>
public static class SourceModelLocator
{
	/// <summary>
	/// 找出模型。不给 <paramref name="modelStem"/> 时取体积最大的 .mdl；给了就按"路径以它结尾"或
	/// "文件名包含它"匹配（大小写不敏感），仍然优先体积最大的那个。
	/// <para>
	/// <b>但 0 骨的 mdl 一律排到最后</b>：重打包工具会用"头里写 0 根骨、body 原样保留"的 mdl
	/// 来占位/封印（实测 L4D2-3831123094 的 <c>arcpav_nte_akane_fashion4/bone128.mdl</c>：
	/// 688 KB、和真模型同一个 checksum，但 <c>numbones = 0</c>）。
	/// 它往往还是体积最大的那个，只按大小选就会选中一个读不出骨架的壳。
	/// </para>
	/// </summary>
	public static VpkEntry FindModel(VpkArchive archive, string? modelStem)
	{
		ArgumentNullException.ThrowIfNull(archive);
		var models = archive.WithExtension("mdl").ToArray();
		if (models.Length == 0)
			throw new InvalidDataException("VPK 里没有任何 .mdl 文件。");

		if (string.IsNullOrWhiteSpace(modelStem))
			return Largest(models, archive);

		var needle = modelStem.Trim().Replace('\\', '/');
		var matches = models
			.Where(entry =>
				entry.Path.EndsWith(needle + ".mdl", StringComparison.OrdinalIgnoreCase)
				|| Path.GetFileName(entry.Path).Contains(needle, StringComparison.OrdinalIgnoreCase))
			.ToArray();

		if (matches.Length == 0)
			throw new InvalidDataException($"VPK 里找不到匹配 \"{modelStem}\" 的 .mdl。");

		return Largest(matches, archive);
	}

	/// <summary>MDL 头里的骨骼数（偏移 156）。读不出来返回 -1。</summary>
	private static int BoneCountOf(VpkArchive archive, VpkEntry entry)
	{
		try
		{
			var bytes = archive.Read(entry);
			if (bytes.Length < 160
				|| bytes[0] != (byte)'I' || bytes[1] != (byte)'D'
				|| bytes[2] != (byte)'S' || bytes[3] != (byte)'T')
				return -1;
			return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(156));
		}
		catch (IOException)
		{
			return -1;
		}
	}

	/// <summary>找出模型以及它的 VVD 与 VTX。</summary>
	/// <remarks>
	/// <b>为什么要看 MDL 的内部名（2026-11-05）</b>：国内有一批"重定向/重打包"包把
	/// <b>骨架</b>挂在 survivor 的 mdl 上、<b>几何</b>只留一份共用模型，
	/// 靠 mdl 头里的 <c>name</c> 字段把两者串起来（实测 L4D2-3831123094：
	/// <c>survivor_namvet.mdl</c> 内部名 = <c>arcpav_nte_akane_fashion4/bone128.mdl</c>，
	/// 而它同名的 vvd/vtx 只有 28 字节——一个合法的 <c>IDSV</c> 头 + 零数据）。
	/// 只按文件名找就会读到那个壳，报"VVD magic 不是 IDSV"，看着像文件坏了。
	/// 这里改成：先按文件名找，<b>校验 magic</b>；不通过再按内部名找，
	/// 两条都找不到才报错，并把两条路径都写出来。
	/// </remarks>
	public static SourceModelFiles Resolve(VpkArchive archive, string? modelStem)
	{
		ArgumentNullException.ThrowIfNull(archive);
		var mdl = FindModel(archive, modelStem);
		var stem = mdl.Path[..^4];
		var fileName = Path.GetFileName(stem);
		var stems = new List<string> { stem };
		var internalStem = InternalStemOf(archive, mdl);
		if (internalStem is not null && !string.Equals(internalStem, stem, StringComparison.OrdinalIgnoreCase))
			stems.Add(internalStem);

		foreach (var candidate in stems)
		{
			var vvd = FindSibling(archive, candidate, ".vvd")
				?? FindByFileName(archive, Path.GetFileName(candidate) + ".vvd", "vvd");
			if (vvd is null || !LooksLikeRealVvd(archive, vvd))
				continue;
			var vtx = FindSibling(archive, candidate, ".dx90.vtx")
				?? FindByFileName(archive, Path.GetFileName(candidate) + ".dx90.vtx", "vtx")
				?? archive.WithExtension("vtx")
					.Where(entry => Path.GetFileName(entry.Path)
						.StartsWith(Path.GetFileName(candidate) + ".", StringComparison.OrdinalIgnoreCase))
					.OrderBy(entry => entry.Path, StringComparer.Ordinal)
					.FirstOrDefault();
			if (vtx is null)
				continue;
			return new SourceModelFiles(mdl, vvd, vtx);
		}

		throw new InvalidDataException(
			$"找不到 {mdl.Path} 的顶点/索引文件。试过：{string.Join("；", stems.Select(s => s + ".vvd"))}。"
			+ "如果是\"几何共用一份\"的重打包包，请确认 mdl 的内部名指向的那个模型确实在 VPK 里。");
	}

	/// <summary>按候选 stem 直接找同名文件（大小写不敏感，交给 <see cref="VpkArchive.Find"/>）。</summary>
	private static VpkEntry? FindSibling(VpkArchive archive, string stem, string extension)
		=> archive.Find(stem + extension);

	/// <summary>退一步：按文件名（不含目录）找扩展名匹配的条目。</summary>
	private static VpkEntry? FindByFileName(VpkArchive archive, string fileName, string extension)
		=> archive.WithExtension(extension)
			.FirstOrDefault(entry =>
				string.Equals(Path.GetFileName(entry.Path), fileName, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// 这个 vvd 是不是"真的带顶点数据"。
	/// <para>
	/// <b>只查魔数不够</b>：那批重打包包里的壳恰好是"合法的 <c>IDSV</c> 头 + 零数据"
	/// （实测 28 字节，头里写着 100579 个顶点）。所以这里按 <see cref="SourceVertexReader"/>
	/// 的同一条规则验算：<c>vertexOffset + lod0VertexCount × 48</c> 必须落在文件内。
	/// 头部本身就谎报长度的，一律当成没有数据。
	/// </para>
	/// <para>
	/// 公开给导入规划用：它要认出"这个包的几何是占位"并给出人话理由，判据只能有一处，
	/// 不能在那里再抄一遍这条规则。
	/// </para>
	/// </summary>
	public static bool LooksLikeRealVvd(VpkArchive archive, VpkEntry entry)
	{
		try
		{
			var bytes = archive.Read(entry);
			if (bytes.Length < 64
				|| bytes[0] != (byte)'I' || bytes[1] != (byte)'D'
				|| bytes[2] != (byte)'S' || bytes[3] != (byte)'V')
				return false;
			var count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16));
			var offset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(56));
			return count > 0 && offset >= 0 && (long)offset + ((long)count * 48) <= bytes.Length;
		}
		catch (IOException)
		{
			return false;
		}
	}

	/// <summary>
	/// 从 MDL 头里取内部名（<c>studiohdr_t.name</c>，偏移 12、64 字节、NUL 结尾），
	/// 换成"VPK 里的完整路径 stem"。取不到就返回 <c>null</c>。
	/// <para>
	/// 路径规则来自实测：内部名本身不带 <c>models/</c> 前缀（例如
	/// <c>arcpav_nte_akane_fashion4/bone128.mdl</c>），加上前缀才是 VPK 里的条目路径。
	/// </para>
	/// </summary>
	private static string? InternalStemOf(VpkArchive archive, VpkEntry mdl)
	{
		try
		{
			var bytes = archive.Read(mdl);
			if (bytes.Length < 76 || bytes[0] != (byte)'I' || bytes[1] != (byte)'D'
				|| bytes[2] != (byte)'S' || bytes[3] != (byte)'T')
				return null;
			var raw = new byte[64];
			Array.Copy(bytes, 12, raw, 0, Math.Min(64, bytes.Length - 12));
			var end = Array.IndexOf(raw, (byte)0);
			if (end < 0) end = raw.Length;
			var name = System.Text.Encoding.UTF8.GetString(raw, 0, end).Trim();
			if (name.Length == 0)
				return null;
			name = name.Replace('\\', '/');
			if (name.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
				name = name[..^4];
			return name.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? name : "models/" + name;
		}
		catch (IOException)
		{
			return null;
		}
	}


	private static VpkEntry Largest(VpkEntry[] models, VpkArchive archive) =>
		models
			// 0 骨的壳排到最后（见 FindModel 的说明）。只有所有候选都是壳时才会选中壳。
			.OrderByDescending(entry => BoneCountOf(archive, entry) > 0)
			.ThenByDescending(entry => entry.Length)
			.ThenBy(entry => entry.Path, StringComparer.Ordinal)
			.First();
}
