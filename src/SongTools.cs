using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace YrtMusic
{
    public sealed class MusicJob
    {
        public int Index { get; set; }
        public string Query { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string State { get; set; }
        public string Detail { get; set; }
        public string FilePath { get; set; }
        public string SourcePage { get; set; }
    }

    public sealed class SongCandidate
    {
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Url { get; set; }
        public double Score { get; set; }
    }

    // Editor: YRT-GPT. These helpers do not perform network or browser operations.
    public static class SongTools
    {
        private static readonly Regex Versions = new Regex(
            @"(?i)\b(live|instrumental|karaoke|cover|remix|demo|dj)\b|现场|伴奏|翻唱|纯音乐|重混|合唱版",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static List<MusicJob> ParseLines(string text)
        {
            List<MusicJob> result = new List<MusicJob>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in (text ?? "").Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string query = raw.Trim();
                if (query.Length == 0 || !seen.Add(query)) continue;
                result.Add(new MusicJob { Index = result.Count + 1, Query = query,
                    Title = "", Artist = "", State = "待处理", Detail = "", FilePath = "", SourcePage = "" });
            }
            return result;
        }

        public static string DefaultOutputDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "音乐");
        }

        public static SongCandidate SelectBest(string query, IList<SongCandidate> candidates)
        {
            if (candidates == null) return null;
            string original = (query ?? "").Trim();
            string requestedTitle = original;
            string requestedArtist = "";
            string[] tabParts = original.Split(new char[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tabParts.Length == 2)
            {
                requestedTitle = tabParts[0].Trim();
                requestedArtist = tabParts[1].Trim();
            }
            else
            {
                Match separator = Regex.Match(original, @"^(.+?)\s+[-–—－]\s+(.+)$");
                if (separator.Success)
                {
                    requestedTitle = separator.Groups[1].Value.Trim();
                    requestedArtist = separator.Groups[2].Value.Trim();
                }
            }

            string normalizedQuery = Normalize(original);
            string normalizedTitle = Normalize(requestedTitle);
            string normalizedArtist = Normalize(requestedArtist);
            SongCandidate best = null;
            double bestScore = 0;
            foreach (SongCandidate candidate in candidates)
            {
                if (candidate == null) continue;
                candidate.Score = 0;
                string title = Normalize(candidate.Title);
                string artist = Normalize(candidate.Artist);
                if (normalizedTitle.Length == 0 || title.Length == 0) continue;
                string titleToMatch = requestedTitle;
                string titleKey = normalizedTitle;
                double artistScore = 1;
                bool artistSpecified = normalizedArtist.Length != 0;
                if (artistSpecified)
                {
                    artistScore = ArtistMatch(normalizedArtist, candidate.Artist);
                    if (artistScore < 0.9) continue;
                }
                else if (artist.Length != 0 && normalizedQuery.Length > title.Length &&
                    (normalizedQuery == title + artist || normalizedQuery == artist + title))
                {
                    // A space-separated title/artist is only inferred from an exact pair.
                    titleToMatch = candidate.Title ?? "";
                    titleKey = title;
                    artistSpecified = true;
                }
                else if (Regex.IsMatch(original, @"\s") && normalizedQuery.Length > title.Length &&
                    (normalizedQuery.StartsWith(title, StringComparison.Ordinal) || normalizedQuery.EndsWith(title, StringComparison.Ordinal)))
                {
                    // A title followed by an unmatching artist must not turn into a fuzzy title hit.
                    continue;
                }

                double titleScore = Similarity(titleKey, title);
                if (titleScore < 0.78)
                {
                    string stripped = Normalize(RemoveVersionBrackets(candidate.Title ?? ""));
                    if (stripped == titleKey) titleScore = 0.94;
                }
                if (titleScore < 0.78) continue;

                MatchCollection requestedVersions = Versions.Matches(titleToMatch);
                MatchCollection candidateVersions = Versions.Matches(candidate.Title ?? "");
                bool missingRequestedVersion = false;
                foreach (Match version in requestedVersions)
                {
                    if (!VersionPresent(version.Value, candidate.Title ?? "")) missingRequestedVersion = true;
                }
                if (missingRequestedVersion) continue;
                double penalty = 0;
                foreach (Match version in candidateVersions)
                {
                    if (!VersionPresent(version.Value, titleToMatch)) penalty += 0.18;
                }
                double score = titleScore - Math.Min(0.45, penalty);
                if (artistSpecified) score = score * 0.98 + artistScore * 0.02;
                candidate.Score = Math.Max(0, Math.Min(1, score));
                if (candidate.Score >= 0.83 && candidate.Score > bestScore)
                {
                    best = candidate;
                    bestScore = candidate.Score;
                }
            }
            return best;
        }

        private static bool VersionPresent(string version, string title)
        {
            string lower = version.ToLowerInvariant();
            string group = lower;
            if (lower == "live" || lower == "现场") group = "live|现场";
            else if (lower == "instrumental" || lower == "karaoke" || lower == "伴奏" || lower == "纯音乐")
                group = "instrumental|karaoke|伴奏|纯音乐";
            else if (lower == "cover" || lower == "翻唱") group = "cover|翻唱";
            else if (lower == "remix" || lower == "重混") group = "remix|重混";
            return Regex.IsMatch(title, group, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string RemoveVersionBrackets(string value)
        {
            return Regex.Replace(value, @"[\(（\[【]([^\)）\]】]*)[\)）\]】]", delegate(Match m)
            {
                return Versions.IsMatch(m.Groups[1].Value) ? "" : m.Value;
            });
        }

        private static double ArtistMatch(string expected, string actual)
        {
            if (Normalize(actual) == expected) return 1;
            foreach (string part in Regex.Split(actual ?? "", @"\s*(?:/|、|&|＆|,|，|;|；|\bfeat\.?\b|\bft\.?\b)\s*",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (Normalize(part) == expected) return 0.95;
            }
            return 0;
        }

        private static string Normalize(string value)
        {
            StringBuilder result = new StringBuilder();
            foreach (char c in (value ?? "").Normalize(NormalizationForm.FormKC).ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) result.Append(c);
            return result.ToString();
        }

        private static double Similarity(string left, string right)
        {
            if (left == right) return 1;
            if (left.Length == 0 || right.Length == 0) return 0;
            // Very short names must match exactly: one differing Chinese character is material.
            if (left.Length <= 4 || right.Length <= 4) return 0;
            if (Math.Abs(left.Length - right.Length) > Math.Max(left.Length, right.Length) / 3) return 0;
            int[] previous = new int[right.Length + 1];
            int[] current = new int[right.Length + 1];
            for (int j = 0; j <= right.Length; j++) previous[j] = j;
            for (int i = 1; i <= left.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= right.Length; j++)
                    current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1),
                        previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
                int[] swap = previous; previous = current; current = swap;
            }
            return 1.0 - (double)previous[right.Length] / Math.Max(left.Length, right.Length);
        }

        public static bool IsMp3(string path, out string reason)
        {
            reason = "";
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    long length = stream.Length;
                    if (length < 8) return Bad(out reason, "文件过短，不能构成连续 MP3 音频帧。");
                    byte[] first = new byte[10];
                    int initial = ReadAt(stream, 0, first, first.Length);
                    long offset = 0;
                    if (initial >= 3 && first[0] == 'I' && first[1] == 'D' && first[2] == '3')
                    {
                        if (initial < 10) return Bad(out reason, "ID3 标签头被截断。");
                        int major = first[3];
                        if (major < 2 || major > 4 || first[4] == 255)
                            return Bad(out reason, "ID3 标签版本无效。");
                        int allowedFlags = major == 2 ? 0xC0 : (major == 3 ? 0xE0 : 0xF0);
                        if ((first[5] & ~allowedFlags) != 0)
                            return Bad(out reason, "ID3 标签标志无效。");
                        for (int i = 6; i < 10; i++)
                            if ((first[i] & 128) != 0) return Bad(out reason, "ID3 标签长度无效。");
                        long tagSize = ((long)first[6] << 21) | ((long)first[7] << 14) |
                            ((long)first[8] << 7) | first[9];
                        offset = 10 + tagSize + (major == 4 && (first[5] & 16) != 0 ? 10 : 0);
                        if (offset > length - 8) return Bad(out reason, "ID3 标签或标签后的音频被截断。");
                        if (major == 4 && (first[5] & 16) != 0)
                        {
                            byte[] footer = new byte[10];
                            if (ReadAt(stream, offset - 10, footer, 10) != 10 ||
                                footer[0] != '3' || footer[1] != 'D' || footer[2] != 'I')
                                return Bad(out reason, "ID3v2.4 尾标无效。");
                            for (int i = 3; i < 10; i++)
                                if (footer[i] != first[i]) return Bad(out reason, "ID3v2.4 尾标与标签头不一致。");
                        }
                    }
                    byte[] prefix = new byte[512];
                    int prefixLength = ReadAt(stream, offset, prefix, prefix.Length);
                    if (LooksLikeOtherFormat(prefix, prefixLength))
                        return Bad(out reason, "下载内容是网页、其他音频格式或错误响应，并非 MP3。");

                    // Some encoders leave zero padding after their declared ID3 body.
                    // Only zero bytes are tolerated; arbitrary junk is never searched for frame sync.
                    int zeroPadding = 0;
                    stream.Position = offset;
                    while (offset < length && zeroPadding < 65536)
                    {
                        int value = stream.ReadByte();
                        if (value != 0) break;
                        offset++; zeroPadding++;
                    }
                    Frame one;
                    Frame two;
                    if (!ReadFrame(stream, offset, length, out one))
                        return Bad(out reason, "未找到完整、合法的 MPEG Layer III 首帧。");
                    if (!ReadFrame(stream, offset + one.Length, length, out two))
                        return Bad(out reason, "第二个连续 MP3 音频帧无效或被截断。");
                    if (one.Version != two.Version || one.SampleRate != two.SampleRate)
                        return Bad(out reason, "连续 MP3 帧的版本或采样率不一致。");
                    reason = "已确认两个连续且完整的 MPEG Layer III 音频帧。";
                    return true;
                }
            }
            catch (Exception exception)
            {
                if (!(exception is IOException) && !(exception is UnauthorizedAccessException) &&
                    !(exception is ArgumentException) && !(exception is NotSupportedException)) throw;
                return Bad(out reason, "无法读取下载文件：" + exception.Message);
            }
        }

        private sealed class Frame
        {
            public int Length;
            public int Version;
            public int SampleRate;
        }

        private static bool ReadFrame(FileStream stream, long offset, long fileLength, out Frame frame)
        {
            frame = null;
            if (offset < 0 || offset > fileLength - 4) return false;
            byte[] header = new byte[4];
            if (ReadAt(stream, offset, header, 4) != 4 || header[0] != 255 || (header[1] & 224) != 224) return false;
            int version = (header[1] >> 3) & 3;
            int layer = (header[1] >> 1) & 3;
            int bitrateIndex = (header[2] >> 4) & 15;
            int sampleIndex = (header[2] >> 2) & 3;
            if (version == 1 || layer != 1 || bitrateIndex == 0 || bitrateIndex == 15 || sampleIndex == 3 ||
                (header[3] & 3) == 2) return false;
            int[] rates = { 44100, 48000, 32000 };
            int[] bitrates = version == 3 ?
                new int[] { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 } :
                new int[] { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };
            int sampleRate = rates[sampleIndex] / (version == 3 ? 1 : (version == 2 ? 2 : 4));
            int frameLength = (version == 3 ? 144000 : 72000) * bitrates[bitrateIndex] / sampleRate + ((header[2] >> 1) & 1);
            if (frameLength < 12 || offset + frameLength > fileLength) return false;
            frame = new Frame { Length = frameLength, Version = version, SampleRate = sampleRate };
            return true;
        }

        private static int ReadAt(FileStream stream, long offset, byte[] target, int count)
        {
            stream.Position = offset;
            int read = 0;
            while (read < count)
            {
                int more = stream.Read(target, read, count - read);
                if (more == 0) break;
                read += more;
            }
            return read;
        }

        private static bool LooksLikeOtherFormat(byte[] bytes, int count)
        {
            if (count >= 4)
            {
                string four = Encoding.ASCII.GetString(bytes, 0, 4);
                if (four == "fLaC" || four == "OggS" || four == "RIFF") return true;
            }
            if (count >= 8 && Encoding.ASCII.GetString(bytes, 4, 4) == "ftyp") return true;
            string text = Encoding.UTF8.GetString(bytes, 0, count).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            return text.StartsWith("<", StringComparison.Ordinal) || text.StartsWith("{", StringComparison.Ordinal) ||
                text.StartsWith("[", StringComparison.Ordinal);
        }

        private static bool Bad(out string reason, string message)
        {
            reason = message;
            return false;
        }

        public static string MoveToMusic(string incomingFile, string outputDir, string title, string artist)
        {
            if (string.IsNullOrWhiteSpace(incomingFile)) throw new ArgumentException("下载文件路径不能为空。", "incomingFile");
            if (string.IsNullOrWhiteSpace(outputDir)) throw new ArgumentException("保存目录不能为空。", "outputDir");
            string source = Path.GetFullPath(incomingFile);
            string directory = Path.GetFullPath(outputDir);
            string directoryRoot = Path.GetPathRoot(directory);
            if (directory.Length > directoryRoot.Length)
                directory = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Directory.CreateDirectory(directory);
            int maxStem = Math.Min(170, 238 - directory.Length - 1 - 4 - 8);
            if (maxStem < 12) throw new PathTooLongException("保存目录过长，请选择较短的音乐文件夹路径。");
            string stem = SafeStem(string.IsNullOrWhiteSpace(artist) ? title : title + " - " + artist, maxStem);
            for (int number = 1; number <= 10000; number++)
            {
                string suffix = number == 1 ? "" : " (" + number.ToString(CultureInfo.InvariantCulture) + ")";
                string destination = Path.Combine(directory, stem + suffix + ".mp3");
                if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return source;
                try
                {
                    File.Move(source, destination);
                    return destination;
                }
                catch (IOException)
                {
                    if (!File.Exists(destination) && !Directory.Exists(destination)) throw;
                }
            }
            throw new IOException("同名文件过多，无法安全保存下载文件。");
        }

        private static string SafeStem(string value, int limit)
        {
            StringBuilder result = new StringBuilder();
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in (value ?? "").Normalize(NormalizationForm.FormKC))
            {
                bool prohibited = c < 32 || c == 127 || Array.IndexOf(invalid, c) >= 0 || "<>:\"/\\|?*".IndexOf(c) >= 0;
                result.Append(prohibited ? '_' : c);
            }
            string stem = Regex.Replace(result.ToString(), @"\s+", " ").Trim().TrimEnd('.', ' ');
            if (stem.Length == 0) stem = "未命名歌曲";
            if (stem.Length > limit)
            {
                stem = stem.Substring(0, limit);
                if (char.IsHighSurrogate(stem[stem.Length - 1])) stem = stem.Substring(0, stem.Length - 1);
                stem = stem.TrimEnd('.', ' ');
            }
            // Windows treats device names as reserved even when an extension is appended.
            string devicePart = stem.Split('.')[0].TrimEnd(' ');
            if (Regex.IsMatch(devicePart, @"^(CON|PRN|AUX|NUL|CLOCK\$|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                stem = "_" + stem;
            return stem;
        }
    }
}
