namespace HD2ModCore.Infrastructure;

/// <summary>
/// 一个 Mod 里**真正会进游戏**的文件。
/// <para>
/// <b>为什么从"基底 + 同名覆盖"改成"看 Overwrite 有没有主"。</b>折并之后宿主里会有
/// 最多十几个材质 patch，它们全都平铺在基底目录里、只有一份该生效。继续用"基底全上、
/// Overwrite 顶同名"的话，那十三份会一起部署、按序号互相覆盖——用户看到的材质是错的，
/// 而且错得没法解释。
/// </para>
/// <para>
/// <b>为什么只在 Overwrite 有归属标记时才接管。</b>没有标记的 Overwrite 可能是用户手工放的，
/// 而我们今天的行为是"它顶掉同名基底 patch、其余基底照常部署"。直接改成"只认 Overwrite"
/// 会让这些用户的补丁凭空消失。所以规则保守：**只有管理器物化过的 Overwrite 才完全接管**，
/// 其余一律走原来的两层合并——老库在迁移完成之前行为一字节不变。
/// </para>
/// </summary>
public static class ModDeployableFiles
{
	/// <summary>按文件名返回"会进游戏"的文件；同名后者赢的语义由返回值本身承载。</summary>
	public static Dictionary<string, string> EnumerateByName(string nodeDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(nodeDirectory);
		var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(nodeDirectory);
		if (Directory.Exists(overwrite) && ManagedOverwriteDirectory.IsManaged(overwrite))
		{
			// 物化过的 Overwrite 就是完整的生效层：它里面有什么就部署什么，
			// 基底目录里的东西一律不参与（包括未被选中的那些材质变体）。
			var materialized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var path in Directory.EnumerateFiles(overwrite, "*", SearchOption.TopDirectoryOnly))
				materialized[Path.GetFileName(path)] = path;
			return materialized;
		}

		return ModNodePayloadLayout.EnumerateEffectiveFilesByName(nodeDirectory);
	}
}
