using System;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;

namespace LunaKit
{
    /// Hashes and random bytes for node.js's crypto (createHash, createHmac, randomBytes), from
    /// Windows' own providers. Bytes go in and out as base64; an error is {err}.
    internal static class Crypto
    {
        public static string Hash(string alg, string base64, string keyBase64)
        {
            try
            {
                var data = CryptographicBuffer.DecodeFromBase64String(base64 ?? "");
                string name;
                switch ((alg ?? "").ToLowerInvariant().Replace("-", ""))
                {
                    case "md5": name = "MD5"; break;
                    case "sha1": name = "SHA1"; break;
                    case "sha256": name = "SHA256"; break;
                    case "sha384": name = "SHA384"; break;
                    case "sha512": name = "SHA512"; break;
                    default: return new JObj().Put("err", "Digest method not supported: " + alg).ToString();
                }
                Windows.Storage.Streams.IBuffer result;
                if (string.IsNullOrEmpty(keyBase64))
                    result = HashAlgorithmProvider.OpenAlgorithm(name).HashData(data);
                else
                {
                    var mac = MacAlgorithmProvider.OpenAlgorithm("HMAC_" + name);
                    var key = mac.CreateKey(CryptographicBuffer.DecodeFromBase64String(keyBase64));
                    result = CryptographicEngine.Sign(key, data);
                }
                return new JObj().Put("b64", CryptographicBuffer.EncodeToBase64String(result)).ToString();
            }
            catch (Exception e) { return new JObj().Put("err", e.Message).ToString(); }
        }

        public static string Random(int n)
        {
            return CryptographicBuffer.EncodeToBase64String(CryptographicBuffer.GenerateRandom((uint)Math.Max(0, Math.Min(n, 1 << 20))));
        }
    }
}
