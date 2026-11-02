namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// 抖动透明用的**阈值图案**。
/// <para>
/// 阈值图案决定"哪些像素被画出来"，也就是噪点的空间结构。两种的区别只在观感：
/// </para>
/// <list type="bullet">
/// <item><see cref="InterleavedGradient"/>：无周期性的细噪点，实机看不出排布规律（默认）。</item>
/// <item><see cref="Bayer"/>：8×8 有序矩阵，规则但会形成可见的方格纹路——早期版本用的就是它，
/// 实机反馈"点排得太整齐"。保留是为了对照与回退。</item>
/// </list>
/// </summary>
public enum DitherPattern
{
	/// <summary>交错梯度噪声：无可见重复结构（默认）。</summary>
	InterleavedGradient = 0,

	/// <summary>8×8 Bayer 有序抖动：规则、可复现，但近看能看出网格。</summary>
	Bayer = 1,
}
