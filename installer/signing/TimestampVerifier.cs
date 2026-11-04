// Read-only public signature/time inspection. No network, key access or trust changes.
// Cryptographic timestamp validity is separate from certificate-chain trust and
// the PE image digest. Windows/existing release verification handles those checks.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace YrtReleaseSigning
{
    public sealed class TimestampEvidence
    {
        public string Kind { get; set; }
        public string Signer { get; set; }
        public string CertificateSubject { get; set; }
        public string CertificateThumbprint { get; set; }
        public string TimeUtc { get; set; }
        public string TimeShanghai { get; set; }
        public string RawEncodedTime { get; set; }
        public string DigestAlgorithmOid { get; set; }
        public bool CryptographicSignatureValid { get; set; }
        public bool ParentSignatureDigestMatches { get; set; }
        public bool HasTimeStampingEku { get; set; }
        public bool CertificateValidAtTimestamp { get; set; }
        public bool UsesDifferentCertificateFromPrimary { get; set; }
        public bool CertificateChainTrustEvaluated { get; set; }
        public string Error { get; set; }
        public bool CryptographicallyVerified
        {
            get { return CryptographicSignatureValid && ParentSignatureDigestMatches && !String.IsNullOrEmpty(TimeUtc); }
        }
    }

    public sealed class TimestampInspection
    {
        public bool EmbeddedSignaturePresent { get; set; }
        public bool CmsSignatureValid { get; set; }
        public string Signer { get; set; }
        public string SignerSubject { get; set; }
        public string SignerThumbprint { get; set; }
        public string CertificateNotBeforeUtc { get; set; }
        public string CertificateNotAfterUtc { get; set; }
        public string SignerReportedTimeUtc { get; set; }
        public string SignerReportedTimeShanghai { get; set; }
        public string SignerReportedRawTime { get; set; }
        public string TimestampKind { get; set; }
        public string TsaTimeUtc { get; set; }
        public string TsaTimeShanghai { get; set; }
        public string TsaSigner { get; set; }
        public bool TimestampCryptographicallyVerified { get; set; }
        public bool CertificateChainTrustEvaluated { get; set; }
        public bool PeFileDigestVerifiedByThisInspector { get; set; }
        public string Error { get; set; }
        public List<string> SignedAttributeOids { get; private set; }
        public List<string> UnsignedAttributeOids { get; private set; }
        public List<TimestampEvidence> Timestamps { get; private set; }
        public List<string> Warnings { get; private set; }
        public TimestampInspection()
        {
            TimestampKind = "None";
            SignedAttributeOids = new List<string>();
            UnsignedAttributeOids = new List<string>();
            Timestamps = new List<TimestampEvidence>();
            Warnings = new List<string>();
        }
    }

    public static class TimestampVerifier
    {
        private const string SigningTimeOid = "1.2.840.113549.1.9.5";
        private const string MessageDigestOid = "1.2.840.113549.1.9.4";
        private const string Rfc3161Oid = "1.3.6.1.4.1.311.3.3.1";
        private sealed class Der
        {
            internal byte Tag;
            internal int Start, Content, Length, End;
        }
        private static void Need(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }
        private static Der Read(byte[] b, ref int p, int end)
        {
            Need(p >= 0 && end <= b.Length && p < end, "Truncated ASN.1 value.");
            int start = p, tag = b[p++];
            Need((tag & 31) != 31 && p < end, "Unsupported ASN.1 tag.");
            int first = b[p++];
            long length = first;
            if ((first & 128) != 0)
            {
                int count = first & 127;
                Need(count > 0 && count <= 4 && p <= end - count && b[p] != 0, "Invalid ASN.1 length.");
                length = 0;
                for (int i = 0; i < count; i++) length = (length << 8) | b[p++];
                Need(length >= 128, "Non-minimal ASN.1 length.");
            }
            Need(length <= Int32.MaxValue && length <= end - p, "ASN.1 value exceeds its container.");
            Der value = new Der { Tag = (byte)tag, Start = start, Content = p, Length = (int)length, End = p + (int)length };
            p = value.End;
            return value;
        }
        private static byte[] Copy(byte[] b, int p, int count)
        {
            byte[] result = new byte[count]; Buffer.BlockCopy(b, p, result, 0, count); return result;
        }
        private static int U32(byte[] b, int p)
        {
            Need(p >= 0 && p <= b.Length - 4, "PE field out of bounds.");
            uint result = BitConverter.ToUInt32(b, p);
            Need(result <= Int32.MaxValue, "PE offset/length exceeds supported range.");
            return (int)result;
        }
        private static ushort U16(byte[] b, int p)
        {
            Need(p >= 0 && p <= b.Length - 2, "PE word out of bounds."); return BitConverter.ToUInt16(b, p);
        }
        private static byte[] EmbeddedPkcs7(byte[] b)
        {
            Need(b.Length >= 256 && U16(b, 0) == 0x5A4D, "Not a PE executable.");
            int pe = U32(b, 0x3C);
            Need(pe >= 64 && pe <= b.Length - 24 && U32(b, pe) == 0x4550, "Missing PE header.");
            int optional = pe + 24, optionalSize = U16(b, pe + 20);
            Need(optional <= b.Length - optionalSize, "Invalid PE optional header.");
            int magic = U16(b, optional);
            Need(magic == 0x10B || magic == 0x20B, "Unsupported PE optional header.");
            int directories = optional + (magic == 0x20B ? 112 : 96);
            int directoryCount = optional + (magic == 0x20B ? 108 : 92);
            Need(directoryCount + 4 <= optional + optionalSize && U32(b, directoryCount) >= 5 &&
                 directories + 40 <= optional + optionalSize, "PE has no security directory.");
            int offset = U32(b, directories + 32), size = U32(b, directories + 36);
            if (offset == 0 || size == 0) return null;
            Need(offset <= b.Length - size && size >= 8 && (offset & 7) == 0, "Invalid certificate table.");
            int end = offset + size;
            while (offset < end)
            {
                Need(offset <= end - 8, "Truncated WIN_CERTIFICATE.");
                int length = U32(b, offset);
                Need(length >= 8 && length <= end - offset, "Invalid WIN_CERTIFICATE length.");
                if (U16(b, offset + 4) == 0x0200 && U16(b, offset + 6) == 2)
                {
                    int p = offset + 8, start = p;
                    Der value = Read(b, ref p, offset + length);
                    Need(value.Tag == 0x30, "Invalid PKCS#7 certificate data.");
                    return Copy(b, start, p - start);
                }
                int aligned = checked((length + 7) & ~7);
                Need(aligned <= end - offset, "Certificate padding exceeds table.");
                offset += aligned;
            }
            return null;
        }
        private static byte[] PrimarySignatureValue(byte[] pkcs7)
        {
            int p = 0;
            Der outer = Read(pkcs7, ref p, pkcs7.Length); Need(outer.Tag == 0x30, "Invalid CMS ContentInfo.");
            p = outer.Content;
            Read(pkcs7, ref p, outer.End); // signedData OID; SignedCms also validates it.
            Der wrapper = Read(pkcs7, ref p, outer.End); Need(wrapper.Tag == 0xA0, "Missing CMS signedData.");
            p = wrapper.Content;
            Der data = Read(pkcs7, ref p, wrapper.End); Need(data.Tag == 0x30, "Invalid CMS signedData.");
            p = data.Content;
            Read(pkcs7, ref p, data.End); // version
            Read(pkcs7, ref p, data.End); // digestAlgorithms
            Read(pkcs7, ref p, data.End); // encapContentInfo
            Der signers = null;
            while (p < data.End)
            {
                Der field = Read(pkcs7, ref p, data.End);
                if (field.Tag == 0x31) { signers = field; break; }
                Need(field.Tag == 0xA0 || field.Tag == 0xA1, "Unexpected CMS signedData field.");
            }
            Need(signers != null, "CMS signerInfos missing.");
            p = signers.Content;
            Der signer = Read(pkcs7, ref p, signers.End);
            Need(signer.Tag == 0x30 && p == signers.End, "Expected exactly one primary CMS signer.");
            p = signer.Content;
            Read(pkcs7, ref p, signer.End); // version
            Read(pkcs7, ref p, signer.End); // signer identifier
            Read(pkcs7, ref p, signer.End); // digest algorithm
            Der algorithmOrAttributes = Read(pkcs7, ref p, signer.End);
            if (algorithmOrAttributes.Tag == 0xA0) Read(pkcs7, ref p, signer.End);
            else Need(algorithmOrAttributes.Tag == 0x30, "Missing signer algorithm.");
            Der signature = Read(pkcs7, ref p, signer.End);
            Need(signature.Tag == 0x04 && signature.Length > 0, "Primary signature OCTET STRING missing.");
            return Copy(pkcs7, signature.Content, signature.Length);
        }
        private static DateTime ParseTime(byte[] raw, out string encodedText)
        {
            int p = 0; Der value = Read(raw, ref p, raw.Length);
            Need(p == raw.Length && (value.Tag == 0x17 || value.Tag == 0x18), "Unsupported signed time encoding.");
            string text = Encoding.ASCII.GetString(raw, value.Content, value.Length);
            encodedText = text;
            Need(text.EndsWith("Z", StringComparison.Ordinal), "Signed time must use UTC Z.");
            string body = text.Substring(0, text.Length - 1), fraction = "";
            int dot = body.IndexOf('.');
            if (dot >= 0) { fraction = body.Substring(dot + 1); body = body.Substring(0, dot); }
            foreach (char c in fraction) Need(c >= '0' && c <= '9', "Invalid fractional time.");
            string pattern = value.Tag == 0x17 ? "yyMMddHHmmss" : "yyyyMMddHHmmss";
            Need(body.Length == (value.Tag == 0x17 ? 12 : 14), "Signed time must include seconds.");
            if (value.Tag == 0x17)
            {
                int year = Int32.Parse(body.Substring(0, 2), CultureInfo.InvariantCulture);
                body = (year >= 50 ? "19" : "20") + body;
                pattern = "yyyyMMddHHmmss";
            }
            DateTime utc = DateTime.ParseExact(body, pattern, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            if (fraction.Length > 0)
            {
                string ticks = fraction.Length > 7 ? fraction.Substring(0, 7) : fraction.PadRight(7, '0');
                utc = utc.AddTicks(Int64.Parse(ticks, CultureInfo.InvariantCulture));
            }
            return DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        }
        private static DateTime? SigningTime(SignerInfo signer, out string rawText)
        {
            rawText = null; DateTime? result = null;
            foreach (CryptographicAttributeObject attribute in signer.SignedAttributes)
                if (attribute.Oid.Value == SigningTimeOid)
                    foreach (AsnEncodedData value in attribute.Values)
                    {
                        Need(!result.HasValue, "Multiple signingTime values are ambiguous.");
                        result = ParseTime(value.RawData, out rawText);
                    }
            return result;
        }
        private static string Utc(DateTime time) { return time.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); }
        private static string Shanghai(DateTime time)
        {
            TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
            DateTime converted = TimeZoneInfo.ConvertTimeFromUtc(time.ToUniversalTime(), zone);
            return new DateTimeOffset(converted, zone.GetUtcOffset(time)).ToString("o", CultureInfo.InvariantCulture);
        }
        private static byte[] Digest(string oid, byte[] bytes)
        {
            HashAlgorithm hash;
            switch (oid)
            {
                case "1.3.14.3.2.26": hash = SHA1.Create(); break;
                case "2.16.840.1.101.3.4.2.1": hash = SHA256.Create(); break;
                case "2.16.840.1.101.3.4.2.2": hash = SHA384.Create(); break;
                case "2.16.840.1.101.3.4.2.3": hash = SHA512.Create(); break;
                default: throw new InvalidDataException("Unsupported timestamp message-imprint digest: " + oid);
            }
            using (hash) return hash.ComputeHash(bytes);
        }
        private static bool Same(byte[] a, byte[] b)
        {
            int difference = a.Length ^ b.Length;
            for (int i = 0; i < a.Length && i < b.Length; i++) difference |= a[i] ^ b[i];
            return difference == 0;
        }
        private static byte[] SignedMessageDigest(SignerInfo signer)
        {
            byte[] result = null;
            foreach (CryptographicAttributeObject attribute in signer.SignedAttributes)
                if (attribute.Oid.Value == MessageDigestOid)
                    foreach (AsnEncodedData value in attribute.Values)
                    {
                        Need(result == null, "Multiple timestamp messageDigest values.");
                        int p = 0; Der octets = Read(value.RawData, ref p, value.RawData.Length);
                        Need(octets.Tag == 4 && p == value.RawData.Length, "Invalid timestamp messageDigest.");
                        result = Copy(value.RawData, octets.Content, octets.Length);
                    }
            Need(result != null, "Timestamp signed messageDigest missing."); return result;
        }
        private static bool TimestampEku(X509Certificate2 certificate)
        {
            foreach (X509Extension extension in certificate.Extensions)
                if (extension.Oid.Value == "2.5.29.37")
                {
                    X509EnhancedKeyUsageExtension eku = new X509EnhancedKeyUsageExtension(); eku.CopyFrom(extension);
                    foreach (Oid usage in eku.EnhancedKeyUsages) if (usage.Value == "1.3.6.1.5.5.7.3.8") return true;
                }
            return false;
        }
        private static void CertificateInfo(TimestampEvidence evidence, X509Certificate2 certificate, X509Certificate2 primary, DateTime? time)
        {
            Need(certificate != null, "Timestamp signer certificate missing.");
            evidence.Signer = certificate.GetNameInfo(X509NameType.SimpleName, false);
            evidence.CertificateSubject = certificate.Subject;
            evidence.CertificateThumbprint = certificate.Thumbprint;
            evidence.HasTimeStampingEku = TimestampEku(certificate);
            evidence.UsesDifferentCertificateFromPrimary = !String.Equals(certificate.Thumbprint, primary.Thumbprint, StringComparison.OrdinalIgnoreCase);
            evidence.CertificateValidAtTimestamp = time.HasValue &&
                certificate.NotBefore.ToUniversalTime() <= time.Value &&
                certificate.NotAfter.ToUniversalTime() >= time.Value;
        }
        private static TimestampEvidence Legacy(SignerInfo counter, byte[] primaryValue, X509Certificate2 primary)
        {
            TimestampEvidence evidence = new TimestampEvidence { Kind = "AuthenticodeCounterSignature" };
            try
            {
                string raw; DateTime? time = SigningTime(counter, out raw);
                evidence.RawEncodedTime = raw;
                if (time.HasValue) { evidence.TimeUtc = Utc(time.Value); evidence.TimeShanghai = Shanghai(time.Value); }
                evidence.DigestAlgorithmOid = counter.DigestAlgorithm.Value;
                evidence.ParentSignatureDigestMatches = Same(SignedMessageDigest(counter), Digest(counter.DigestAlgorithm.Value, primaryValue));
                Need(evidence.ParentSignatureDigestMatches, "Counter-signature messageDigest does not bind to the primary signature.");
                counter.CheckSignature(true);
                evidence.CryptographicSignatureValid = true;
                CertificateInfo(evidence, counter.Certificate, primary, time);
                Need(time.HasValue, "Counter-signature has no signed signingTime.");
            }
            catch (Exception ex) { evidence.Error = ex.Message; }
            return evidence;
        }
        private static string DigestOid(byte[] b, Der oid)
        {
            Need(oid.Tag == 6, "Expected message-imprint digest OID.");
            string hex = BitConverter.ToString(Copy(b, oid.Content, oid.Length)).Replace("-", "");
            if (hex == "2B0E03021A") return "1.3.14.3.2.26";
            if (hex == "608648016503040201") return "2.16.840.1.101.3.4.2.1";
            if (hex == "608648016503040202") return "2.16.840.1.101.3.4.2.2";
            if (hex == "608648016503040203") return "2.16.840.1.101.3.4.2.3";
            throw new InvalidDataException("Unsupported RFC3161 digest OID.");
        }
        private static TimestampEvidence Rfc3161(byte[] tokenBytes, byte[] primaryValue, X509Certificate2 primary)
        {
            TimestampEvidence evidence = new TimestampEvidence { Kind = "RFC3161" };
            try
            {
                SignedCms token = new SignedCms(); token.Decode(tokenBytes);
                Need(token.ContentInfo.ContentType.Value == "1.2.840.113549.1.9.16.1.4" && token.SignerInfos.Count == 1, "Invalid RFC3161 TimeStampToken.");
                token.CheckSignature(true); evidence.CryptographicSignatureValid = true;
                byte[] content = token.ContentInfo.Content; int p = 0;
                Der outer = Read(content, ref p, content.Length); Need(outer.Tag == 0x30 && p == content.Length, "Invalid RFC3161 TSTInfo.");
                p = outer.Content;
                Read(content, ref p, outer.End); // version
                Read(content, ref p, outer.End); // policy
                Der imprint = Read(content, ref p, outer.End); Need(imprint.Tag == 0x30, "Missing RFC3161 messageImprint.");
                int ip = imprint.Content;
                Der algorithm = Read(content, ref ip, imprint.End); Need(algorithm.Tag == 0x30, "Missing RFC3161 digest algorithm.");
                int ap = algorithm.Content;
                evidence.DigestAlgorithmOid = DigestOid(content, Read(content, ref ap, algorithm.End));
                Der expected = Read(content, ref ip, imprint.End); Need(expected.Tag == 4 && ip == imprint.End, "Invalid RFC3161 hashedMessage.");
                evidence.ParentSignatureDigestMatches = Same(Copy(content, expected.Content, expected.Length), Digest(evidence.DigestAlgorithmOid, primaryValue));
                Need(evidence.ParentSignatureDigestMatches, "RFC3161 messageImprint does not bind to the primary signature.");
                Read(content, ref p, outer.End); // serialNumber
                Der encodedTime = Read(content, ref p, outer.End); Need(encodedTime.Tag == 0x18, "RFC3161 genTime must be GeneralizedTime.");
                string raw; DateTime time = ParseTime(Copy(content, encodedTime.Start, encodedTime.End - encodedTime.Start), out raw);
                evidence.RawEncodedTime = raw; evidence.TimeUtc = Utc(time); evidence.TimeShanghai = Shanghai(time);
                CertificateInfo(evidence, token.SignerInfos[0].Certificate, primary, time);
            }
            catch (Exception ex) { evidence.Error = ex.Message; }
            return evidence;
        }
        public static TimestampInspection Inspect(string path)
        {
            TimestampInspection result = new TimestampInspection();
            try
            {
                byte[] pkcs7 = EmbeddedPkcs7(File.ReadAllBytes(path));
                if (pkcs7 == null) { result.Warnings.Add("No embedded Authenticode signature is present."); return result; }
                result.EmbeddedSignaturePresent = true;
                SignedCms cms = new SignedCms(); cms.Decode(pkcs7);
                Need(cms.SignerInfos.Count == 1, "Expected one primary signer.");
                SignerInfo primary = cms.SignerInfos[0];
                foreach (CryptographicAttributeObject attr in primary.SignedAttributes) result.SignedAttributeOids.Add(attr.Oid.Value);
                foreach (CryptographicAttributeObject attr in primary.UnsignedAttributes) result.UnsignedAttributeOids.Add(attr.Oid.Value);
                X509Certificate2 certificate = primary.Certificate;
                Need(certificate != null, "Primary signer certificate missing.");
                result.Signer = certificate.GetNameInfo(X509NameType.SimpleName, false);
                result.SignerSubject = certificate.Subject; result.SignerThumbprint = certificate.Thumbprint;
                result.CertificateNotBeforeUtc = Utc(certificate.NotBefore);
                result.CertificateNotAfterUtc = Utc(certificate.NotAfter);
                try { cms.CheckSignature(true); result.CmsSignatureValid = true; }
                catch (Exception ex) { result.Warnings.Add("CMS signature verification failed: " + ex.Message); }
                string raw;
                DateTime? reported = SigningTime(primary, out raw);
                if (reported.HasValue)
                {
                    result.SignerReportedTimeUtc = Utc(reported.Value);
                    result.SignerReportedTimeShanghai = Shanghai(reported.Value);
                    result.SignerReportedRawTime = raw;
                }
                byte[] primaryValue = PrimarySignatureValue(pkcs7);
                foreach (SignerInfo counter in primary.CounterSignerInfos)
                    result.Timestamps.Add(Legacy(counter, primaryValue, certificate));
                foreach (CryptographicAttributeObject attribute in primary.UnsignedAttributes)
                    if (attribute.Oid.Value == Rfc3161Oid)
                        foreach (AsnEncodedData value in attribute.Values)
                            result.Timestamps.Add(Rfc3161(value.RawData, primaryValue, certificate));
                TimestampEvidence selected = null;
                foreach (TimestampEvidence timestamp in result.Timestamps)
                {
                    if (selected == null || (!selected.CryptographicallyVerified && timestamp.CryptographicallyVerified)) selected = timestamp;
                    if (!String.IsNullOrEmpty(timestamp.Error)) result.Warnings.Add(timestamp.Kind + ": " + timestamp.Error);
                }
                if (selected != null)
                {
                    result.TimestampKind = selected.Kind;
                    result.TsaTimeUtc = selected.TimeUtc; result.TsaTimeShanghai = selected.TimeShanghai;
                    result.TsaSigner = selected.Signer;
                    result.TimestampCryptographicallyVerified = selected.CryptographicallyVerified;
                }
                if (String.IsNullOrEmpty(result.SignerReportedTimeUtc)) result.Warnings.Add("The primary signature has no signingTime attribute; certificate validity and file modification time are not signing time.");
                if (selected == null) result.Warnings.Add("No TSA counter-signature or RFC3161 token is embedded.");
                result.Warnings.Add("This read-only inspector verifies timestamp signatures and digest binding. Certificate-chain trust, revocation and PE file digest are separate checks; no network is used.");
            }
            catch (Exception ex) { result.Error = ex.Message; }
            return result;
        }
    }
}
