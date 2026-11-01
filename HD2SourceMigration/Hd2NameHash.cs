using System.Text;

namespace HD2SourceMigration;

/// <summary>
/// HD2 的**骨名哈希**：64 位 Stingray 哈希的**高 32 位**。
/// <para>
/// 为什么需要它：给一个 Unit 插新骨时，<c>TransformInfo.NameHashes</c> 里放的必须是这个数，
/// 顶点权重的 <c>RetargetedInfluence.BoneHash</c> 也必须是它——两边对不上时游戏不会报错，
/// 只会让那批顶点**静默地不跟着新骨动**。
/// </para>
/// <para>
/// 口径是实测定的，不是猜的：拿仓库里的 <c>bonehash.txt</c>（游戏自己的 hash→name 表）交叉验证，
/// <c>head / chest / hips / spine1 / l_knee</c> 五个名字**全部等于 64 位哈希的高 32 位**，
/// 而低 32 位一个都对不上。样本值写进了单元测试，改算法就会被它挡住。
/// </para>
/// </summary>
public static class Hd2NameHash
{
	private const ulong Mix = 0xC6A4A7935BD1E995UL;

	/// <summary>字符串 → 骨名哈希（高 32 位）。</summary>
	public static uint Of(string name)
	{
		ArgumentNullException.ThrowIfNull(name);
		return (uint)(Hash64(name) >> 32);
	}

	/// <summary>完整的 64 位 Stingray 哈希；<see cref="Of"/> 取它的高 32 位。</summary>
	public static ulong Hash64(string name)
	{
		ArgumentNullException.ThrowIfNull(name);
		var data = Encoding.UTF8.GetBytes(name);
		unchecked
		{
			// 种子是 `长度 × Mix`（不是常量），这是 Stingray 这一族的特征。
			var hash = (ulong)data.Length * Mix;
			var offset = 0;
			while (offset + 8 <= data.Length)
			{
				var word = (ulong)data[offset]
					| ((ulong)data[offset + 1] << 8)
					| ((ulong)data[offset + 2] << 16)
					| ((ulong)data[offset + 3] << 24)
					| ((ulong)data[offset + 4] << 32)
					| ((ulong)data[offset + 5] << 40)
					| ((ulong)data[offset + 6] << 48)
					| ((ulong)data[offset + 7] << 56);
				word *= Mix;
				word ^= word >> 47;
				hash ^= word * Mix;
				hash *= Mix;
				offset += 8;
			}

			if (offset < data.Length)
			{
				ulong tail = 0;
				for (var i = 0; offset + i < data.Length; i++)
				{
					tail |= (ulong)data[offset + i] << (i * 8);
				}

				hash ^= tail;
				hash *= Mix;
			}

			hash ^= hash >> 47;
			hash *= Mix;
			return hash ^ (hash >> 47);
		}
	}
}
