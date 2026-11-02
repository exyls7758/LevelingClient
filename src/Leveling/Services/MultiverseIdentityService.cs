using Multiverse.Models;
using System;
using System.Security.Cryptography;
using System.Text;

namespace Multiverse.Services
{
    /// <summary>
    /// Stable per-user Multiverse identity. The ECDSA private key lives in the
    /// Windows CNG user key store and is never serialized to app settings.
    /// </summary>
    internal sealed class MultiverseIdentityService : IDisposable
    {
        private static readonly Lazy<MultiverseIdentityService> LazyInstance =
            new(() => new MultiverseIdentityService());

        public static MultiverseIdentityService Instance => LazyInstance.Value;

        private const string DeviceIdSetting = "Multiverse.Identity.DeviceId";
        private const string DisplayNameSetting = "Multiverse.Identity.DisplayName";
        private const string KeyPrefix = "Multiverse.ECDSA.P256.";

        private readonly MultiverseSettingsService _settings = MultiverseSettingsService.Instance;
        private readonly object _sync = new();
        private readonly string _deviceId;
        private readonly string _keyName;
        private ECDsaCng? _signer;
        private bool _disposed;

        private MultiverseIdentityService()
        {
            string stored = _settings.GetString(DeviceIdSetting, "");
            if (!Guid.TryParse(stored, out Guid deviceGuid))
            {
                deviceGuid = Guid.NewGuid();
                _settings.Set(DeviceIdSetting, deviceGuid.ToString("D"));
            }

            _deviceId = deviceGuid.ToString("D");
            _keyName = KeyPrefix + deviceGuid.ToString("N");
            EnsureSigner();
        }

        public string DeviceId => _deviceId;

        public string DisplayName
        {
            get
            {
                string value = _settings.GetString(DisplayNameSetting, "");
                return string.IsNullOrWhiteSpace(value)
                    ? Environment.MachineName
                    : value.Trim();
            }
            set
            {
                string normalized = string.IsNullOrWhiteSpace(value)
                    ? Environment.MachineName
                    : value.Trim();
                _settings.Set(DisplayNameSetting, normalized);
            }
        }

        public string PublicKeySpki
        {
            get
            {
                lock (_sync)
                {
                    EnsureSigner();
                    return Convert.ToBase64String(_signer!.ExportSubjectPublicKeyInfo());
                }
            }
        }

        public string Sign(string canonicalText)
        {
            byte[] data = Encoding.UTF8.GetBytes(canonicalText ?? "");
            lock (_sync)
            {
                EnsureSigner();
                return Convert.ToBase64String(
                    _signer!.SignData(data, HashAlgorithmName.SHA256));
            }
        }

        public static bool Verify(
            string publicKeySpki,
            string canonicalText,
            string signatureBase64)
        {
            if (string.IsNullOrWhiteSpace(publicKeySpki) ||
                string.IsNullOrWhiteSpace(signatureBase64))
            {
                return false;
            }

            try
            {
                using ECDsa verifier = ECDsa.Create();
                byte[] keyBytes = Convert.FromBase64String(publicKeySpki);
                verifier.ImportSubjectPublicKeyInfo(keyBytes, out _);
                byte[] signature = Convert.FromBase64String(signatureBase64);
                byte[] data = Encoding.UTF8.GetBytes(canonicalText ?? "");
                return verifier.VerifyData(data, signature, HashAlgorithmName.SHA256);
            }
            catch
            {
                return false;
            }
        }

        private void EnsureSigner()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MultiverseIdentityService));
            if (_signer is not null)
                return;

            CngKey key;
            try
            {
                key = CngKey.Open(
                    _keyName,
                    CngProvider.MicrosoftSoftwareKeyStorageProvider);
            }
            catch (CryptographicException)
            {
                var parameters = new CngKeyCreationParameters
                {
                    Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                    KeyUsage = CngKeyUsages.Signing,
                    ExportPolicy = CngExportPolicies.None
                };

                key = CngKey.Create(CngAlgorithm.ECDsaP256, _keyName, parameters);
            }

            _signer = new ECDsaCng(key)
            {
                HashAlgorithm = CngAlgorithm.Sha256
            };
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _signer?.Dispose();
                _signer = null;
            }
        }
    }
}
