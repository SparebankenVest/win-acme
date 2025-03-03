﻿using ACMESharp.Protocol;
using PKISharp.WACS.Acme;
using PKISharp.WACS.Configuration;
using PKISharp.WACS.DomainObjects;
using PKISharp.WACS.Extensions;
using PKISharp.WACS.Plugins.Interfaces;
using PKISharp.WACS.Services.Serialization;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using bc = Org.BouncyCastle;

namespace PKISharp.WACS.Services
{
    internal class CertificateService : ICertificateService
    {
        private readonly IInputService _inputService;
        private readonly ILogService _log;
        private readonly ISettingsService _settings;
        private readonly AcmeClient _client;
        private readonly DirectoryInfo _cache;
        private readonly PemService _pemService;

        public CertificateService(
            ILogService log,
            AcmeClient client,
            PemService pemService,
            IInputService inputService,
            ISettingsService settingsService)
        {
            _log = log;
            _client = client;
            _pemService = pemService;
            _cache = new DirectoryInfo(settingsService.Cache.Path);
            _settings = settingsService;
            _inputService = inputService;
            CheckStaleFiles();
        }

        /// <summary>
        /// List all files older than 120 days from the certificate
        /// cache, because that means that the certificates have been
        /// expired for 30 days. User might want to clean them up
        /// </summary>
        private void CheckStaleFiles()
        {
            var days = 120;
            var files = _cache.
                GetFiles().
                Where(x => x.LastWriteTime < DateTime.Now.AddDays(-days));
            var count = files.Count();
            if (count > 0)
            {
                _log.Warning("Found {nr} files older than {days} days in the cache path", count, days);
                if (_settings.Cache.DeleteStaleFiles)
                {
                    _log.Information("Deleting stale files");
                    try
                    {
                        foreach (var file in files)
                        {
                            file.Delete();
                        }
                        _log.Information("Stale files deleted");
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "Deleting stale files");
                    }
                }
            }
        }

        /// <summary>
        /// Delete cached files related to a specific renewal
        /// </summary>
        /// <param name="renewal"></param>
        private void ClearCache(Renewal renewal)
        {
            foreach (var f in _cache.GetFiles($"{renewal.Id}*"))
            {
                _log.Verbose("Deleting {file} from cache", f.Name);
                f.Delete();
            }
        }

        /// <summary>
        /// Encrypt or decrypt the cached private keys
        /// </summary>
        public void Encrypt()
        {
            foreach (var f in _cache.GetFiles($"*.keys"))
            {
                var x = new ProtectedString(File.ReadAllText(f.FullName));
                _log.Information("Rewriting {x}", f.Name);
                File.WriteAllText(f.FullName, x.DiskValue(_settings.Security.EncryptConfig));
            }
        }

        /// <summary>
        /// Find local certificate file based on naming conventions
        /// </summary>
        /// <param name="renewal"></param>
        /// <param name="postfix"></param>
        /// <param name="prefix"></param>
        /// <returns></returns>
        private string GetPath(Renewal renewal, string postfix, string prefix = "") => Path.Combine(_cache.FullName, $"{prefix}{renewal.Id}{postfix}");

        /// <summary>
        /// Read from the disk cache
        /// </summary>
        /// <param name="renewal"></param>
        /// <returns></returns>
        public CertificateInfo CachedInfo(Renewal renewal)
        {
            var pfxFileInfo = new FileInfo(PfxFilePath(renewal));
            if (pfxFileInfo.Exists)
            {
                try
                {
                    return new CertificateInfo()
                    {
                        Certificate = ReadForUse(pfxFileInfo, renewal.PfxPassword?.Value),
                        CacheFile = pfxFileInfo,
                        CacheFilePassword = renewal.PfxPassword?.Value
                    };
                }
                catch
                {
                    // File corrupt or invalid password?
                    _log.Warning("Unable to read from certificate cache");
                }
            }
            return null;
        }

        /// <summary>
        /// Request certificate from the ACME server
        /// </summary>
        /// <param name="binding"></param>
        /// <returns></returns>
        public async Task<CertificateInfo> RequestCertificate(ICsrPlugin csrPlugin, RunLevel runLevel, Renewal renewal, Target target, OrderDetails order)
        {
            // What are we going to get?
            var pfxFileInfo = new FileInfo(PfxFilePath(renewal));

            // Determine/check the common name
            var identifiers = target.GetHosts(false);
            var commonNameUni = target.CommonName;
            var commonNameAscii = string.Empty;
            if (!string.IsNullOrWhiteSpace(commonNameUni))
            {
                var idn = new IdnMapping();
                commonNameAscii = idn.GetAscii(commonNameUni);
                if (!identifiers.Contains(commonNameAscii, StringComparer.InvariantCultureIgnoreCase))
                {
                    _log.Warning($"Common name {commonNameUni} provided is invalid.");
                    commonNameAscii = identifiers.First();
                    commonNameUni = idn.GetUnicode(commonNameAscii);
                }
            }

            // Determine the friendly name
            var friendlyName = renewal.FriendlyName;
            if (string.IsNullOrEmpty(friendlyName))
            {
                friendlyName = target.FriendlyName;
            }
            if (string.IsNullOrEmpty(friendlyName))
            {
                friendlyName = commonNameUni;
            }

            // Try using cached certificate first to avoid rate limiting during
            // (initial?) deployment troubleshooting. Real certificate requests
            // will only be done once per day maximum unless the --force parameter 
            // is used.
            var cache = CachedInfo(renewal);
            if (cache != null &&
                cache.CacheFile.LastWriteTime > DateTime.Now.AddDays(_settings.Cache.ReuseDays * -1) &&
                cache.Match(target))
            {
                if (runLevel.HasFlag(RunLevel.IgnoreCache))
                {
                    _log.Warning("Cached certificate available but not used with --{switch}. Use 'Renew specific' or " +
                        "'Renew all' in the main menu to run unscheduled renewals without hitting rate limits.",
                        nameof(MainArguments.Force).ToLower());
                }
                else
                {
                    _log.Warning("Using cached certificate for {friendlyName}. To force issue of a new certificate within " +
                        "24 hours, delete the .pfx file from the CertificatePath or run with the --{switch} switch. " +
                        "Be ware that you might run into rate limits doing so.",
                        friendlyName,
                        nameof(MainArguments.Force).ToLower());
                    return cache;
                }
            }

            if (target.CsrBytes == null)
            {
                var csr = await csrPlugin.GenerateCsr(GetPath(renewal, ".keys"), commonNameAscii, identifiers);
                target.CsrBytes = csr.GetDerEncoded();
                target.PrivateKey = (await csrPlugin.GetKeys()).Private;
                File.WriteAllText(GetPath(renewal, "-csr.pem"), _pemService.GetPem("CERTIFICATE REQUEST", target.CsrBytes));
            }

            _log.Verbose("Submitting CSR");
            order = await _client.SubmitCsr(order, target.CsrBytes);
            if (order.Payload.Status != AcmeClient.OrderValid)
            {
                _log.Error("Unexpected order status {status}", order.Payload.Status);
                throw new Exception($"Unable to complete order");
            }

            _log.Information("Requesting certificate {friendlyName}", friendlyName);
            var rawCertificate = await _client.GetCertificate(order);
            if (rawCertificate == null)
            {
                throw new Exception($"Unable to get certificate");
            }

            byte[] certificateExport;
            using (var certificate = new X509Certificate2(rawCertificate))
            {
                certificateExport = certificate.Export(X509ContentType.Cert);
            }
            var crtPem = _pemService.GetPem("CERTIFICATE", certificateExport);

            // Get issuer certificate 
            var issuerCertificate = new X509Certificate2(rawCertificate.Skip(certificateExport.Length).ToArray());
            var issuerCertificateExport = issuerCertificate.Export(X509ContentType.Cert);
            var issuerPem = _pemService.GetPem("CERTIFICATE", issuerCertificateExport);
            issuerCertificate.Dispose();

            // Build pfx archive
            var pfx = new bc.Pkcs.Pkcs12Store();
            var bcCertificate = _pemService.ParsePem<bc.X509.X509Certificate>(crtPem);
            var bcCertificateEntry = new bc.Pkcs.X509CertificateEntry(bcCertificate);
            var bcCertificateAlias = bcCertificate.SubjectDN.ToString();
            pfx.SetCertificateEntry(bcCertificateAlias, bcCertificateEntry);
            if (target.PrivateKey != null)
            {
                var bcPrivateKeyEntry = new bc.Pkcs.AsymmetricKeyEntry(target.PrivateKey);
                pfx.SetKeyEntry(bcCertificateAlias, bcPrivateKeyEntry, new[] { bcCertificateEntry });
            }

            var bcIssuer = _pemService.ParsePem<bc.X509.X509Certificate>(issuerPem);
            var bcIssuerEntry = new bc.Pkcs.X509CertificateEntry(bcIssuer);
            var bcIssuerAlias = bcIssuer.SubjectDN.ToString();
            pfx.SetCertificateEntry(bcIssuerAlias, bcIssuerEntry);

            var pfxStream = new MemoryStream();
            pfx.Save(pfxStream, null, new bc.Security.SecureRandom());
            pfxStream.Position = 0;
            using var pfxStreamReader = new BinaryReader(pfxStream);

            var tempPfx = new X509Certificate2(
                pfxStreamReader.ReadBytes((int)pfxStream.Length),
                (string)null,
                X509KeyStorageFlags.MachineKeySet |
                X509KeyStorageFlags.PersistKeySet |
                X509KeyStorageFlags.Exportable);
            tempPfx.FriendlyName = $"{friendlyName} {_inputService.FormatDate(DateTime.Now)}";
            File.WriteAllBytes(pfxFileInfo.FullName, tempPfx.Export(X509ContentType.Pfx, renewal.PfxPassword?.Value));

            if (csrPlugin != null)
            {
                try
                {
                    var newVersion = await csrPlugin.PostProcess(tempPfx);
                    if (newVersion != tempPfx)
                    {
                        newVersion.FriendlyName = $"{friendlyName} {_inputService.FormatDate(DateTime.Now)}";
                        File.WriteAllBytes(pfxFileInfo.FullName, newVersion.Export(X509ContentType.Pfx, renewal.PfxPassword?.Value));
                        newVersion.Dispose();
                    }
                }
                catch (Exception)
                {
                    _log.Warning("Private key conversion error.");
                }
            }

            pfxFileInfo.Refresh();
            tempPfx.Dispose();

            // Update LastFriendlyName so that the user sees
            // the most recently issued friendlyName in
            // the WACS GUI
            renewal.LastFriendlyName = friendlyName;

            // Recreate X509Certificate2 with correct flags for Store/Install
            return new CertificateInfo()
            {
                Certificate = ReadForUse(pfxFileInfo, renewal.PfxPassword?.Value),
                CacheFile = pfxFileInfo,
                CacheFilePassword = renewal.PfxPassword?.Value
            };
        }


        /// <summary>
        /// Read certificate for it to be exposed to the StorePlugin and InstallationPlugins
        /// </summary>
        /// <param name="source"></param>
        /// <param name="password"></param>
        /// <returns></returns>
        private X509Certificate2 ReadForUse(FileInfo source, string password)
        {
            // Flags used for the X509Certificate2 as 
            var externalFlags =
                X509KeyStorageFlags.MachineKeySet |
                X509KeyStorageFlags.PersistKeySet |
                X509KeyStorageFlags.Exportable;
            return new X509Certificate2(source.FullName, password, externalFlags);
        }

        /// <summary>
        /// Revoke previously issued certificate
        /// </summary>
        /// <param name="binding"></param>
        public async Task RevokeCertificate(Renewal renewal)
        {
            // Delete cached files
            var info = CachedInfo(renewal);
            if (info != null)
            {
                var certificateDer = info.Certificate.Export(X509ContentType.Cert);
                await _client.RevokeCertificate(certificateDer);
            }
            ClearCache(renewal);
            _log.Warning("Certificate for {target} revoked, you should renew immediately", renewal);
        }

        /// <summary>
        /// Path to the cached PFX file
        /// </summary>
        /// <param name="renewal"></param>
        /// <returns></returns>
        private string PfxFilePath(Renewal renewal) => GetPath(renewal, "-cache.pfx", "");

        /// <summary>
        /// Common filter for different store plugins
        /// </summary>
        /// <param name="friendlyName"></param>
        /// <returns></returns>
        public static Func<X509Certificate2, bool> ThumbprintFilter(string thumbprint) => new Func<X509Certificate2, bool>(x => string.Equals(x.Thumbprint, thumbprint));
    }
}
