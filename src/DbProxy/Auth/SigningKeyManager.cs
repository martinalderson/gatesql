using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace DbProxy.Auth;

public class SigningKeyManager
{
    private readonly ECDsa _key;
    public ECDsaSecurityKey SecurityKey { get; }
    public SigningCredentials SigningCredentials { get; }

    public SigningKeyManager(string keyPath)
    {
        _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        if (File.Exists(keyPath))
        {
            var pem = File.ReadAllText(keyPath);
            _key.ImportFromPem(pem);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            var pem = _key.ExportECPrivateKeyPem();
            File.WriteAllText(keyPath, pem);
        }

        SecurityKey = new ECDsaSecurityKey(_key);
        SigningCredentials = new SigningCredentials(SecurityKey, SecurityAlgorithms.EcdsaSha256);
    }
}
