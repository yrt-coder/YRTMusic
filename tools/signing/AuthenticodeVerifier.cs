// Verifies the embedded PKCS#7 independently of the Windows trust verdict.
// Supported layout: ordinary PE32/PE32+ files with contiguous raw sections and
// an attribute certificate table at EOF, as produced by this project's compiler.
// Reject unfamiliar layouts rather than treating them as verified.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace YrtReleaseSigning
{
    public sealed class VerificationResult
    {
        public bool EmbeddedSignaturePresent;
        public bool CmsSignatureValid;
        public bool AuthenticodeFileDigestMatches;
        public string SignerCommonName;
        public string SignerSubject;
        public string SignerThumbprint;
        public string FileDigestAlgorithm;
        public string SignedDigestHex;
        public string ComputedDigestHex;
        public int CertificateTableOffset;
        public int CertificateTableSize;
        public int Pkcs7Length;
    }

    public static class AuthenticodeVerifier
    {
        private sealed class Section
        {
            internal int Start;
            internal int Size;
        }

        private sealed class DerValue
        {
            internal byte Tag;
            internal int Content;
            internal int Length;
            internal int End;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }

        private static ushort U16(byte[] bytes, int offset)
        {
            Require(offset >= 0 && offset <= bytes.Length - 2, "PE word is out of bounds.");
            return BitConverter.ToUInt16(bytes, offset);
        }

        private static uint U32(byte[] bytes, int offset)
        {
            Require(offset >= 0 && offset <= bytes.Length - 4, "PE DWORD is out of bounds.");
            return BitConverter.ToUInt32(bytes, offset);
        }

        private static int Int32Range(uint value, string name)
        {
            Require(value <= Int32.MaxValue, name + " exceeds the supported file size.");
            return (int)value;
        }

        private static DerValue ReadDer(byte[] bytes, ref int cursor, int limit)
        {
            Require(limit >= 0 && limit <= bytes.Length && cursor >= 0 && cursor < limit, "Missing DER value.");
            int tag = bytes[cursor++];
            Require((tag & 31) != 31, "High-tag-number DER is not expected here.");
            Require(cursor < limit, "Missing DER length.");
            int first = bytes[cursor++];
            long length;
            if ((first & 128) == 0) length = first;
            else
            {
                int octets = first & 127;
                Require(octets > 0 && octets <= 4 && cursor <= limit - octets, "Invalid DER length.");
                Require(bytes[cursor] != 0, "Non-minimal DER length.");
                length = 0;
                for (int i = 0; i < octets; i++) length = (length << 8) | bytes[cursor++];
                Require(length >= 128, "Non-minimal long DER length.");
            }
            Require(length <= Int32.MaxValue && length <= limit - cursor, "DER value extends outside its container.");
            DerValue result = new DerValue { Tag = (byte)tag, Content = cursor, Length = (int)length, End = cursor + (int)length };
            cursor = result.End;
            return result;
        }

        private static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "");
        }

        private static byte[] Slice(byte[] bytes, int offset, int count)
        {
            byte[] result = new byte[count];
            Buffer.BlockCopy(bytes, offset, result, 0, count);
            return result;
        }

        private static byte[] ReadSignedSha256Digest(byte[] content)
        {
            int cursor = 0;
            DerValue outer = ReadDer(content, ref cursor, content.Length);
            Require(outer.Tag == 0x30 && cursor == content.Length, "SpcIndirectDataContent is not a single DER sequence.");
            cursor = outer.Content;
            // SpcAttributeTypeAndOptionalValue: integrity data about the PE image.
            DerValue data = ReadDer(content, ref cursor, outer.End);
            Require(data.Tag == 0x30, "Missing Authenticode image data sequence.");
            DerValue digestInfo = ReadDer(content, ref cursor, outer.End);
            Require(digestInfo.Tag == 0x30 && cursor == outer.End, "Missing Authenticode DigestInfo.");
            cursor = digestInfo.Content;
            DerValue algorithm = ReadDer(content, ref cursor, digestInfo.End);
            Require(algorithm.Tag == 0x30, "Missing digest AlgorithmIdentifier.");
            int algorithmCursor = algorithm.Content;
            DerValue oid = ReadDer(content, ref algorithmCursor, algorithm.End);
            byte[] sha256Oid = new byte[] { 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01 };
            Require(oid.Tag == 0x06 && oid.Length == sha256Oid.Length, "The file digest is not SHA-256.");
            for (int i = 0; i < sha256Oid.Length; i++)
                Require(content[oid.Content + i] == sha256Oid[i], "The file digest is not SHA-256.");
            if (algorithmCursor < algorithm.End)
            {
                DerValue parameters = ReadDer(content, ref algorithmCursor, algorithm.End);
                Require(parameters.Tag == 0x05 && parameters.Length == 0, "Unexpected digest algorithm parameters.");
            }
            Require(algorithmCursor == algorithm.End, "Extra digest algorithm data.");
            DerValue digest = ReadDer(content, ref cursor, digestInfo.End);
            Require(digest.Tag == 0x04 && digest.Length == 32 && cursor == digestInfo.End, "Invalid SHA-256 digest.");
            return Slice(content, digest.Content, digest.Length);
        }

        private static void HashRange(HashAlgorithm algorithm, byte[] bytes, int offset, int count)
        {
            Require(offset >= 0 && count >= 0 && offset <= bytes.Length - count, "Hash range is out of bounds.");
            if (count > 0) algorithm.TransformBlock(bytes, offset, count, bytes, offset);
        }

        // A transient sign/verify checks access to the private key without exporting it.
        public static string ProbePrivateKey(X509Certificate2 certificate)
        {
            if (!certificate.HasPrivateKey) return null;
            byte[] challenge = System.Text.Encoding.UTF8.GetBytes("YRT-GPT local code-signing key availability check");
            try
            {
                using (RSA privateRsa = RSACertificateExtensions.GetRSAPrivateKey(certificate))
                {
                    if (privateRsa != null)
                    {
                        if (privateRsa.KeySize < 2048) return null;
                        byte[] signature = privateRsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                        using (RSA publicRsa = RSACertificateExtensions.GetRSAPublicKey(certificate))
                        {
                            if (publicRsa != null && publicRsa.VerifyData(challenge, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                                return "RSA-" + privateRsa.KeySize;
                        }
                        return null;
                    }
                }
                using (ECDsa privateEc = ECDsaCertificateExtensions.GetECDsaPrivateKey(certificate))
                {
                    if (privateEc == null) return null;
                    byte[] signature = privateEc.SignData(challenge, HashAlgorithmName.SHA256);
                    using (ECDsa publicEc = ECDsaCertificateExtensions.GetECDsaPublicKey(certificate))
                    {
                        if (publicEc != null && publicEc.VerifyData(challenge, signature, HashAlgorithmName.SHA256))
                            return "ECDSA-" + privateEc.KeySize;
                    }
                }
            }
            catch (CryptographicException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            return null;
        }

        public static VerificationResult Verify(string filePath, string expectedThumbprint)
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            Require(bytes.Length >= 256 && U16(bytes, 0) == 0x5A4D, "The file is not a DOS/PE executable.");
            int pe = Int32Range(U32(bytes, 0x3C), "PE header offset");
            Require(pe >= 64 && pe <= bytes.Length - 24 && U32(bytes, pe) == 0x00004550, "Missing PE signature.");
            int sectionCount = U16(bytes, pe + 6);
            int optionalLength = U16(bytes, pe + 20);
            int optional = pe + 24;
            Require(sectionCount > 0 && sectionCount <= 1024 && optionalLength >= 96 &&
                    optional <= bytes.Length - optionalLength, "Invalid PE optional header.");
            ushort magic = U16(bytes, optional);
            Require(magic == 0x10B || magic == 0x20B, "Only PE32/PE32+ images are supported.");
            int directoryStart = optional + (magic == 0x20B ? 112 : 96);
            int numberOfDirectories = optional + (magic == 0x20B ? 108 : 92);
            Require(numberOfDirectories + 4 <= optional + optionalLength && U32(bytes, numberOfDirectories) >= 5, "PE has no security data directory.");
            int securityDirectory = directoryStart + 4 * 8;
            Require(securityDirectory + 8 <= optional + optionalLength, "Security data directory is out of bounds.");
            int checksumOffset = optional + 64;
            int headerSize = Int32Range(U32(bytes, optional + 60), "SizeOfHeaders");
            int sectionTable = optional + optionalLength;
            Require(headerSize >= sectionTable + sectionCount * 40 && headerSize <= bytes.Length, "Invalid PE header size.");

            int tableOffset = Int32Range(U32(bytes, securityDirectory), "Certificate table offset");
            int tableSize = Int32Range(U32(bytes, securityDirectory + 4), "Certificate table size");
            Require(tableOffset > 0 && tableSize >= 8, "No embedded Authenticode signature is present.");
            Require((tableOffset & 7) == 0 && (long)tableOffset + tableSize == bytes.Length,
                    "The certificate table must be aligned and end at EOF.");
            Require(tableOffset >= headerSize && checksumOffset + 4 <= securityDirectory &&
                    securityDirectory + 8 <= headerSize, "Invalid certificate/hash exclusion ranges.");

            List<Section> sections = new List<Section>();
            for (int i = 0; i < sectionCount; i++)
            {
                int entry = sectionTable + i * 40;
                int size = Int32Range(U32(bytes, entry + 16), "Section raw size");
                int start = Int32Range(U32(bytes, entry + 20), "Section raw offset");
                if (size == 0) continue;
                Require(start >= headerSize && (long)start + size <= tableOffset, "A PE section overlaps headers or certificates.");
                sections.Add(new Section { Start = start, Size = size });
            }
            sections.Sort(delegate(Section a, Section b) { return a.Start.CompareTo(b.Start); });
            int next = headerSize;
            foreach (Section section in sections)
            {
                Require(section.Start == next, "Non-contiguous PE sections are outside this verifier's supported layout.");
                next = checked(next + section.Size);
            }
            Require(next <= tableOffset, "PE section data exceeds the image.");

            int position = tableOffset;
            byte[] pkcs7 = null;
            int pkcs7Entries = 0;
            while (position < bytes.Length)
            {
                Require(position <= bytes.Length - 8, "Truncated WIN_CERTIFICATE entry.");
                int length = Int32Range(U32(bytes, position), "WIN_CERTIFICATE length");
                Require(length >= 8 && (long)position + length <= bytes.Length, "Invalid WIN_CERTIFICATE length.");
                ushort revision = U16(bytes, position + 4);
                ushort kind = U16(bytes, position + 6);
                Require(revision == 0x0200 && kind == 0x0002, "Unexpected certificate entry type/revision.");
                int derCursor = position + 8;
                int derStart = derCursor;
                DerValue value = ReadDer(bytes, ref derCursor, position + length);
                Require(value.Tag == 0x30, "Embedded PKCS#7 is not a DER sequence.");
                for (int padding = derCursor; padding < position + length; padding++)
                    Require(bytes[padding] == 0, "Nonzero unsigned certificate padding.");
                pkcs7 = Slice(bytes, derStart, derCursor - derStart);
                pkcs7Entries++;
                int paddedLength = checked((length + 7) & ~7);
                Require((long)position + paddedLength <= bytes.Length, "Certificate padding is out of bounds.");
                for (int padding = position + length; padding < position + paddedLength; padding++)
                    Require(bytes[padding] == 0, "Nonzero inter-certificate padding.");
                position += paddedLength;
            }
            Require(position == bytes.Length && pkcs7Entries == 1 && pkcs7 != null, "Expected exactly one embedded PKCS#7 signature.");

            SignedCms cms = new SignedCms();
            cms.Decode(pkcs7);
            Require(cms.ContentInfo.ContentType.Value == "1.3.6.1.4.1.311.2.1.4", "PKCS#7 is not Authenticode SpcIndirectDataContent.");
            Require(cms.SignerInfos.Count == 1, "Expected exactly one primary signer.");
            // true verifies the cryptographic signatures, without granting certificate trust.
            cms.CheckSignature(true);
            SignerInfo signer = cms.SignerInfos[0];
            X509Certificate2 certificate = signer.Certificate;
            Require(certificate != null, "PKCS#7 signer certificate is missing.");
            string commonName = certificate.GetNameInfo(X509NameType.SimpleName, false);
            Require(String.Equals(commonName, "YRT-GPT", StringComparison.Ordinal) &&
                    String.Equals(certificate.Subject, "CN=YRT-GPT", StringComparison.Ordinal), "Signer must be exactly CN=YRT-GPT.");
            Require(String.Equals(certificate.Thumbprint, expectedThumbprint, StringComparison.OrdinalIgnoreCase), "Embedded signer differs from the selected certificate.");
            Require(signer.DigestAlgorithm.Value == "2.16.840.1.101.3.4.2.1", "The signer digest is not SHA-256.");

            byte[] signedDigest = ReadSignedSha256Digest(cms.ContentInfo.Content);
            byte[] computedDigest;
            using (SHA256 algorithm = SHA256.Create())
            {
                // Contiguous PE sections make these ranges identical to the documented
                // Authenticode sequence: headers, raw sections, then pre-certificate overlay.
                HashRange(algorithm, bytes, 0, checksumOffset);
                HashRange(algorithm, bytes, checksumOffset + 4, securityDirectory - checksumOffset - 4);
                HashRange(algorithm, bytes, securityDirectory + 8, tableOffset - securityDirectory - 8);
                algorithm.TransformFinalBlock(new byte[0], 0, 0);
                computedDigest = algorithm.Hash;
            }
            int difference = signedDigest.Length ^ computedDigest.Length;
            for (int i = 0; i < signedDigest.Length && i < computedDigest.Length; i++) difference |= signedDigest[i] ^ computedDigest[i];
            Require(difference == 0, "Authenticode file digest mismatch: the executable differs from the signed image.");

            return new VerificationResult
            {
                EmbeddedSignaturePresent = true,
                CmsSignatureValid = true,
                AuthenticodeFileDigestMatches = true,
                SignerCommonName = commonName,
                SignerSubject = certificate.Subject,
                SignerThumbprint = certificate.Thumbprint,
                FileDigestAlgorithm = "SHA256",
                SignedDigestHex = Hex(signedDigest),
                ComputedDigestHex = Hex(computedDigest),
                CertificateTableOffset = tableOffset,
                CertificateTableSize = tableSize,
                Pkcs7Length = pkcs7.Length
            };
        }
    }
}
