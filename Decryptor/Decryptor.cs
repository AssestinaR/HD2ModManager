using Sodium;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace FileDecryptor;

public sealed class Decryptor
{
    public const int KeyLength = 32;
    public const int PublicKeyLength = 32;
    public const int AuthenticationTagLength = 16;

    private readonly byte[] _secretKey;
    private readonly byte[] _authKey;

    public Decryptor(ReadOnlySpan<byte> secretKey, ReadOnlySpan<byte> authKey)
    {
        if (secretKey.Length != KeyLength)
        {
            throw new ArgumentException($"Secret key must be {KeyLength} bytes.", nameof(secretKey));
        }

        if (authKey.Length != KeyLength)
        {
            throw new ArgumentException($"Authentication key must be {KeyLength} bytes.", nameof(authKey));
        }

        _secretKey = secretKey.ToArray();
        _authKey = authKey.ToArray();
    }

    [Obsolete("Use the constructor overload that accepts keys from the calling application.")]
    public static Decryptor CreateWithEmbeddedKeys() => new(KeyProvider.GetSecretKey(), KeyProvider.GetAuthKey());

    public byte[] GetPublicKey() => ScalarMult.Base(_secretKey);

    public byte[] EncodeData(byte[] plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        return EncodeData(plainText, GetPublicKey());
    }

    public byte[] EncodeData(ReadOnlySpan<byte> plainText, ReadOnlySpan<byte> receiverPublicKey)
    {
        if (receiverPublicKey.Length != PublicKeyLength)
        {
            throw new ArgumentException($"Receiver public key must be {PublicKeyLength} bytes.", nameof(receiverPublicKey));
        }

        var ephemeral = PublicKeyBox.GenerateKeyPair();
        var senderPk = ephemeral.PublicKey;
        var nonce = DeriveNonce(senderPk);
        var cipherText = PublicKeyBox.Create(plainText.ToArray(), nonce, ephemeral.PrivateKey, receiverPublicKey.ToArray());

        var result = new byte[PublicKeyLength + cipherText.Length];
        Array.Copy(senderPk, 0, result, 0, PublicKeyLength);
        Array.Copy(cipherText, 0, result, PublicKeyLength, cipherText.Length);
        return result;
    }

    public byte[] DecodeData(ReadOnlySpan<byte> data)
    {
        if (TryDecodeData(data, out var plainText))
        {
            return plainText;
        }

        throw new CryptographicException("Ciphertext is invalid, has been modified, or was encrypted for a different key.");
    }

    public bool TryDecodeData(ReadOnlySpan<byte> data, [NotNullWhen(true)] out byte[]? plainText)
    {
        if (data.Length < PublicKeyLength + AuthenticationTagLength)
        {
            plainText = null;
            return false;
        }

        var senderPk = data[..PublicKeyLength].ToArray();
        var cipherText = data[PublicKeyLength..].ToArray();
        var nonce = DeriveNonce(senderPk);

        try
        {
            plainText = PublicKeyBox.Open(cipherText, nonce, _secretKey, senderPk);
            return true;
        }
        catch (CryptographicException)
        {
            plainText = null;
            return false;
        }
    }

    private byte[] DeriveNonce(byte[] senderPk)
    {
        var input = new byte[senderPk.Length + _authKey.Length];
        Array.Copy(senderPk, 0, input, 0, senderPk.Length);
        Array.Copy(_authKey, 0, input, senderPk.Length, _authKey.Length);
        return GenericHash.Hash(input, null, 24);
    }
}
