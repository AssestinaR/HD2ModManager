using System.Buffers.Binary;

namespace HD2GameItemAssets;

public static class StingrayHash
{
	public static ulong Murmur64A(string value)
	{
		ArgumentNullException.ThrowIfNull(value);
		return Murmur64A(System.Text.Encoding.UTF8.GetBytes(value));
	}

	public static ulong Murmur64A(ReadOnlySpan<byte> value)
	{
		const ulong mix = 0xc6a4a7935bd1e995;
		const int shift = 47;
		ulong hash = (ulong)value.Length * mix;
		var offset = 0;
		while (offset + sizeof(ulong) <= value.Length)
		{
			var key = BinaryPrimitives.ReadUInt64LittleEndian(value[offset..]);
			key *= mix;
			key ^= key >> shift;
			key *= mix;
			hash ^= key;
			hash *= mix;
			offset += sizeof(ulong);
		}

		switch (value.Length - offset)
		{
			case 7: hash ^= (ulong)value[offset + 6] << 48; goto case 6;
			case 6: hash ^= (ulong)value[offset + 5] << 40; goto case 5;
			case 5: hash ^= (ulong)value[offset + 4] << 32; goto case 4;
			case 4: hash ^= (ulong)value[offset + 3] << 24; goto case 3;
			case 3: hash ^= (ulong)value[offset + 2] << 16; goto case 2;
			case 2: hash ^= (ulong)value[offset + 1] << 8; goto case 1;
			case 1:
				hash ^= value[offset];
				hash *= mix;
				break;
		}

		hash ^= hash >> shift;
		hash *= mix;
		hash ^= hash >> shift;
		return hash;
	}

	public static string Format(ulong hash) => $"0x{hash:x16}";
}
