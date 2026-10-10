using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace FileDecryptor.Tests;

public sealed class DecryptorTests
{
    private static readonly byte[] ReceiverSecretKey = Enumerable.Range(1, Decryptor.KeyLength).Select(value => (byte)value).ToArray();
    private static readonly byte[] AuthKey = Enumerable.Range(101, Decryptor.KeyLength).Select(value => (byte)value).ToArray();

    [Fact]
    public void EncryptThenDecrypt_RoundTripsForSpecifiedRecipient()
    {
        var recipient = new Decryptor(ReceiverSecretKey, AuthKey);
        var plaintext = Encoding.UTF8.GetBytes("hello, encryption");
        var encrypted = recipient.EncodeData(plaintext, recipient.GetPublicKey());

        Assert.Equal(plaintext.Length + Decryptor.PublicKeyLength + Decryptor.AuthenticationTagLength, encrypted.Length);
        Assert.True(recipient.TryDecodeData(encrypted, out var decrypted));
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void TryDecodeData_ReturnsFalseForModifiedCiphertext()
    {
        var decryptor = new Decryptor(ReceiverSecretKey, AuthKey);
        var encrypted = decryptor.EncodeData([1, 2, 3], decryptor.GetPublicKey());
        encrypted[^1] ^= 0x01;

        Assert.False(decryptor.TryDecodeData(encrypted, out var decrypted));
        Assert.Null(decrypted);
    }

    [Fact]
    public void TryDecodeData_ReturnsFalseForWrongKeyOrInvalidLength()
    {
        var recipient = new Decryptor(ReceiverSecretKey, AuthKey);
        var otherRecipient = new Decryptor(Enumerable.Repeat((byte)42, Decryptor.KeyLength).ToArray(), AuthKey);
        var encrypted = recipient.EncodeData([1, 2, 3], recipient.GetPublicKey());

        Assert.False(otherRecipient.TryDecodeData(encrypted, out _));
        Assert.False(recipient.TryDecodeData([1, 2, 3], out _));
    }

    [Fact]
    public void EmbeddedKeyFactory_DecryptsExistingCiphertext()
    {
#pragma warning disable CS0618
        var decryptor = Decryptor.CreateWithEmbeddedKeys();
#pragma warning restore CS0618
        var ciphertext = Convert.FromBase64String("2OpVrF/rG+YdVNpSy0GA1IAm/7otYQEoHkYfxwRz7RNBxqkBJi1pfzeoqEA/0r/GWGGgffQdHUECeeH0zF7M5bbm/gC81tnRSak=");

        var plaintext = decryptor.DecodeData(ciphertext);

        Assert.Equal("123456六五四三二一\r\n", Encoding.UTF8.GetString(plaintext));
    }

    [Fact]
    public void ConstructorAndDecodeData_ValidateInputs()
    {
        Assert.Throws<ArgumentException>(() => new Decryptor([1], AuthKey));
        var decryptor = new Decryptor(ReceiverSecretKey, AuthKey);

        Assert.Throws<CryptographicException>(() => decryptor.DecodeData([1, 2, 3]));
    }
}
