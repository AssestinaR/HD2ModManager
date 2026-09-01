using System.Text;

using System.Buffers.Binary;

namespace FileDecryptor;

internal static class KeyProvider
{
    public static byte[] GetSecretKey()
    {
        const ulong XK = 0xEB63463F0C116E2C;
        ulong[] magic2 =
        {
            0x1B3B4D0E8D7A478C,
            0x79A1E77CBFB8B63F,
            0x1776EDE6D312EE2C,
            0x8FF8F2773C453FDF
        };
        var bytes = new byte[32];
        for (int i = 0; i < 4; i++)
        {
            var val = magic2[i] ^ XK;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * sizeof(ulong), sizeof(ulong)), val);
        }
        return bytes;
    }

    public static byte[] GetAuthKey()
    {
        const ulong XK = 0xEB63463F0C116E2C;
        ulong[] magic1 =
        {
            0x3E8DA00F70BBA701,
            0x994B81AA021F93DB,
            0xAAAF66AAFB37DFB5,
            0xE79639D18E181BF2
        };
        var bytes = new byte[32];
        for (int i = 0; i < 4; i++)
        {
            var val = magic1[i] ^ XK;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * sizeof(ulong), sizeof(ulong)), val);
        }
        return bytes;
    }
}
