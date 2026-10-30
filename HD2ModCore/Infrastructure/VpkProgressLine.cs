namespace HD2ModCore.Infrastructure;

/// <summary>
/// 转换器（<c>hd2mod vpk2patch</c>）与管理器之间的<b>进度行</b>格式。
/// <para>
/// <b>为什么要有这么个东西。</b>一次 VPK 导入实测要跑几分钟（4K 材质包一个包就 55 秒），
/// 而这段时间里管理器唯一能拿到的信息就是子进程的输出。stdout 是结论（<c>--json</c> 时是一整份报告），
/// 往里混进度行会让它不再是合法 JSON，所以进度走 stderr，用固定前缀加结构化字段。
/// </para>
/// <para>
/// 格式：<c>#progress &lt;阶段键&gt; &lt;已完成&gt; &lt;总数&gt; &lt;文案&gt;</c>
/// </para>
/// <list type="bullet">
/// <item><c>phase</c>：稳定的英文标识。目前有 <c>queue</c>（这一批里第几个包）、
/// <c>materials</c> / <c>textures</c> / <c>units</c>（这个包里第几个材质 / 贴图 / Unit）。
/// 管理器按它决定"两个计数怎么合成一个百分比"——<b>只有 <c>queue</c> 那一条是分母</b>。</item>
/// <item><c>text</c>：给人看的中文，可以带空格，所以解析时只切前三刀。</item>
/// </list>
/// <para>
/// 放在 <c>HD2ModCore</c> 而不是各写一份：写的一方在 CLI、读的一方在管理器，
/// 格式漂移的症状是"进度条不动"——而进度条不动看起来像卡死，不像 bug。
/// </para>
/// </summary>
public static class VpkProgressLine
{
	/// <summary>前缀。行首精确匹配它，别的前缀（含 <c>#progressx</c>）一律不算。</summary>
	public const string Prefix = "#progress ";

	/// <summary>这一批里的第几个包。它的 <c>done</c> 是<b>已完成</b>的包数，是合成百分比的分母来源。</summary>
	public const string QueuePhase = "queue";

	/// <summary>拼一行。文案里的换行会被压成空格——一行一个记录是解析的前提。</summary>
	public static string Format(string phase, int done, int total, string text)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(phase);
		var flattened = (text ?? string.Empty)
			.Replace('\r', ' ')
			.Replace('\n', ' ')
			.Trim();
		return $"{Prefix}{phase} {done} {total} {flattened}";
	}

	/// <summary>解析一行。不是进度行、或字段不合法时返回 <c>false</c>（调用方应当把它当普通输出处理）。</summary>
	public static bool TryParse(
		string? line, out string phase, out int done, out int total, out string text)
	{
		phase = string.Empty;
		done = 0;
		total = 0;
		text = string.Empty;
		if (line is null || !line.StartsWith(Prefix, StringComparison.Ordinal))
			return false;

		// 只切前三刀：文案里可以有空格（"正在转换 2/5：xxx.vpk"）。
		var fields = line[Prefix.Length..].Split(' ', 4);
		if (fields.Length < 3 || string.IsNullOrWhiteSpace(fields[0]))
			return false;
		if (!int.TryParse(fields[1], System.Globalization.NumberStyles.Integer,
				System.Globalization.CultureInfo.InvariantCulture, out done))
			return false;
		if (!int.TryParse(fields[2], System.Globalization.NumberStyles.Integer,
				System.Globalization.CultureInfo.InvariantCulture, out total))
			return false;

		phase = fields[0];
		text = fields.Length > 3 ? fields[3] : phase;
		return true;
	}
}
