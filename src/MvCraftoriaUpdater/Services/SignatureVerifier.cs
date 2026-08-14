using System.Security.Cryptography;

namespace MvCraftoriaUpdater.Services;

internal static class SignatureVerifier
{
    private const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEGvKotSJrvd/J54VKl8Y804wNnvBr
        UDGUAMBM7gTlm8rear/IcBbpNp4+u3+rCjW879tOj0ZNQdVovIHIwnUY6Q==
        -----END PUBLIC KEY-----
        """;

    internal static void Verify(byte[] content, string base64Signature)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(base64Signature.Trim());
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The release signature is malformed.", exception);
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(PublicKeyPem);
        if (!ecdsa.VerifyData(content, signature, HashAlgorithmName.SHA256))
        {
            throw new CryptographicException("The release signature is not valid. The update was rejected.");
        }
    }
}
