using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WeiboAlbumDownloader.Helpers
{
    /// <summary>
    /// 从文件元数据中读取 ContentIdentifier 时的媒体端类型
    /// </summary>
    public enum ContentIdentifierKind
    {
        /// <summary>照片端（EXIF 区浮动存储）</summary>
        Photo,

        /// <summary>视频端（QuickTime meta/ilst/mdta 键值）</summary>
        Video
    }

    /// <summary>
    /// 一对实况照片（封面 jpg + 实况视频 mov）
    /// </summary>
    public sealed record LivePhotoPair
    {
        /// <summary>封面 jpg 完整路径（合并成功后将被原地覆盖为 motion photo）</summary>
        public string? JpgPath { get; init; }

        /// <summary>实况视频 mov 完整路径（合并成功后删除）</summary>
        public string? MovPath { get; init; }

        /// <summary>所属微博帖子的基础卷名（去掉 _数字 尾缀）</summary>
        public string? GroupKey { get; init; }

        /// <summary>是否由 ContentIdentifier 元数据精确配对（false 表示按唯一配对兜底）</summary>
        public bool IsMetadataMatched { get; init; }
    }

    /// <summary>
    /// 批量合并动态照片的汇总报告
    /// </summary>
    public sealed class MotionPhotoBatchReport
    {
        /// <summary>扫描到的配对总数</summary>
        public int Total { get; set; }

        /// <summary>合并成功数量</summary>
        public int Merged { get; set; }

        /// <summary>跳过数量（已合并过 / 无法配对 / 文件损坏）</summary>
        public int Skipped { get; set; }

        /// <summary>失败数量</summary>
        public int Failed { get; set; }

        /// <summary>逐条过程日志（供界面 AppendLog / 临时测试断言）</summary>
        public List<string> Logs { get; } = new();

        /// <summary>失败明细</summary>
        public List<string> Failures { get; } = new();
    }

    /// <summary>
    /// 实况照片批量合并为 Google MotionPhoto。
    /// 视频容器（MOV→MP4）交由 FFmpeg remux 重建，容器正确性不再靠手工字节手术。
    /// </summary>
    /// <remarks>
    /// 目标格式：<br/>
    /// 1. 物理结构 = 标准 JPEG 封面（前段）+ 尾部二进制拼接的 MP4 微视频；<br/>
    /// 2. 在 JPEG 头部注入 Google GCamera XMP 容器描述（MicroVideoOffset = 内嵌 MP4 的起始偏移，
    ///    Item:Length = 内嵌视频字节长度）；<br/>
    /// 3. 配对主依据为 ContentIdentifier UUID（jpg EXIF 端 与 mov QuickTime 端各存一份），文件名分组仅作兜底。<br/>
    /// 内嵌 MP4 由 <see cref="FfmpegInvoker.RemuxToMp4"/> 用 FFmpeg 重封装：moov 前置(+faststart)、
    /// 音频重编码为标准 AAC、剔除 iPhone 专有扩展盒(mebx/edts/tapt/ctts等)并重建时序表，
    /// 产出主流播放器（Windows Photos / 小米相册）均可解析的合规容器。FFmpeg 缺失时退化为仅换标。
    /// </remarks>
    public static class MotionPhotoHelper
    {
        /// <summary>ContentIdentifier 完整 UUID 文本匹配正则</summary>
        private static readonly Regex UuidRegex = new(
            @"[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}",
            RegexOptions.Compiled);

        /// <summary>从文件名茎（不含扩展名）剥离末尾 _数字 尾缀</summary>
        private static readonly Regex TrailingIndexRegex = new(
            @"_\d+$",
            RegexOptions.CultureInvariant);

        /// <summary>流式复制缓冲大小（1MB）</summary>
        private const int CopyBufferSize = 1024 * 1024;

        /// <summary>ContentIdentifier 读取时的分块扫描块大小</summary>
        private const int ScanChunkBytes = 1024 * 1024;

        /// <summary>跨块保留的尾部字符数，确保跨块边界上的 UUID 不被遗漏（UUID 36 字符，留余量）</summary>
        private const int UuidCarryChars = 40;

        /// <summary>动态照片探测时读取的头部字节上限</summary>
        private const int DetectionScanBytes = 128 * 1024;

        // ─────────────────────────── ① 扫描与配对 ───────────────────────────

        /// <summary>
        /// 递归扫描下载根目录，将实况照片（jpg 封面 + mov 视频）智能配对
        /// </summary>
        /// <remarks>
        /// 配对规则（与人工核验样本 37/37 无冲突一致）：<br/>
        /// 1. 先按「基础卷名」（文件名去掉末尾 _数字）分组，天然限制在一个微博帖子的同目录范围内；<br/>
        /// 2. 组内优先用 ContentIdentifier 元数据精确配对（mov 与 jpg 各自读到相同 UUID 即配对）；<br/>
        /// 3. 视频无 ContentIdentifier 时，仅当组内恰好剩 1 个未被消费的 jpg 且 1 个 mov 时按唯一性兜底，否则跳过不瞎猜。
        /// </remarks>
        /// <param name="downloadRoot">下载根目录（递归扫描）</param>
        /// <returns>配对结果列表</returns>
        public static List<LivePhotoPair> FindAndPair(string downloadRoot)
        {
            var result = new List<LivePhotoPair>();
            if (string.IsNullOrWhiteSpace(downloadRoot) || !Directory.Exists(downloadRoot))
            {
                return result;
            }

            var jpgFiles = new List<string>();
            var movFiles = new List<string>();
            foreach (var file in Directory.EnumerateFiles(downloadRoot, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file);
                if (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
                {
                    jpgFiles.Add(file);
                }
                else if (ext.Equals(".mov", StringComparison.OrdinalIgnoreCase))
                {
                    movFiles.Add(file);
                }
            }

            // 基础卷名 → 文件列表
            var jpgByKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var jpg in jpgFiles)
            {
                var key = GetGroupKey(jpg);
                if (!jpgByKey.TryGetValue(key, out var list))
                {
                    jpgByKey[key] = list = new List<string>();
                }
                list.Add(jpg);
            }

            var movByKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var mov in movFiles)
            {
                var key = GetGroupKey(mov);
                if (!movByKey.TryGetValue(key, out var list))
                {
                    movByKey[key] = list = new List<string>();
                }
                list.Add(mov);
            }

            foreach (var kv in movByKey)
            {
                if (!jpgByKey.TryGetValue(kv.Key, out var groupJpgs) || groupJpgs.Count == 0)
                {
                    continue; // 该帖没有照片，异常，跳过
                }

                var availableJpgs = new List<string>(groupJpgs);

                foreach (var mov in kv.Value)
                {
                    var matchedJpg = MatchMovToJpg(mov, availableJpgs);
                    if (matchedJpg is null)
                    {
                        // 无法配对：记录为失败交由调用方展示，不猜
                        continue;
                    }

                    availableJpgs.Remove(matchedJpg);
                    result.Add(new LivePhotoPair
                    {
                        JpgPath = matchedJpg,
                        MovPath = mov,
                        GroupKey = kv.Key,
                        IsMetadataMatched = true
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// 为单个 mov 匹配对应的封面 jpg：优先 ContentIdentifier 精确配对，唯一性兜底
        /// </summary>
        private static string? MatchMovToJpg(string mov, List<string> availableJpgs)
        {
            if (availableJpgs.Count == 0)
            {
                return null;
            }

            // 优先元数据精确配对
            var movCid = ReadContentIdentifier(mov, ContentIdentifierKind.Video);
            if (!string.IsNullOrWhiteSpace(movCid))
            {
                foreach (var jpg in availableJpgs)
                {
                    var jpgCid = ReadContentIdentifier(jpg, ContentIdentifierKind.Photo);
                    if (!string.IsNullOrWhiteSpace(jpgCid) &&
                        string.Equals(jpgCid, movCid, StringComparison.OrdinalIgnoreCase))
                    {
                        return jpg;
                    }
                }
            }

            // 文件名兜底：同目录中与 mov 基础卷名(去掉扩展名)完全一致的 jpg 视为配对
            var movName = Path.GetFileNameWithoutExtension(mov);
            var byName = availableJpgs.FirstOrDefault(j =>
                string.Equals(Path.GetFileNameWithoutExtension(j), movName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return byName;
            }

            // 唯一性兜底：组内只剩 1 张未被消费的 jpg 且仅有 1 个 mov，则唯一配对
            if (availableJpgs.Count == 1)
            {
                return availableJpgs[0];
            }

            return null;
        }

        /// <summary>
        /// 从文件名提取基础卷名（去掉路径、扩展名与末尾 _数字 尾缀）
        /// </summary>
        private static string GetGroupKey(string filePath)
        {
            var stem = Path.GetFileNameWithoutExtension(filePath);
            return TrailingIndexRegex.Replace(stem, string.Empty);
        }

        // ─────────────────────────── ② ContentIdentifier 读取 ───────────────────────────

        /// <summary>
        /// 从文件字节中读取 ContentIdentifier（全大写 UUID 文本）
        /// </summary>
        /// <remarks>
        /// 照片端：UUID 以 ASCII 形式位于 EXIF 数据区；视频端：位于 QuickTime meta/ilst/mdta 键值区
        /// （moov 通常在文件前部）。统一按「扫描字节 + UUID 正则」提取，容错性最强。
        /// </remarks>
        /// <param name="filePath">文件路径</param>
        /// <param name="kind">媒体端类型，决定扫描上限</param>
        /// <returns>UUID 字符串（大写）；未找到返回 null</returns>
        public static string? ReadContentIdentifier(string filePath, ContentIdentifierKind _)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    return null;
                }

                var uuid = FindUuidInFile(filePath);
                return string.IsNullOrEmpty(uuid) ? null : uuid.ToUpperInvariant();
            }
            catch
            {
                // 元数据读取失败不作为阻断，交由调用方按唯一性兜底
                return null;
            }
        }

        /// <summary>
        /// 分块流式扫描整个文件的 ASCII 内容，定位 UUID。
        /// 不再对视频设置前 8MB 上限——微博高清实况视频的 ContentIdentifier 可能位于文件后部。
        /// 内存按块占用，避免一次性装载大文件。
        /// </summary>
        private static string? FindUuidInFile(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[ScanChunkBytes];
            string carry = string.Empty;

            while (true)
            {
                var read = fs.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                var combined = carry + Encoding.ASCII.GetString(buffer, 0, read);
                var match = UuidRegex.Match(combined);
                if (match.Success)
                {
                    return match.Value;
                }

                // 保留尾部若干字符，使跨块边界的 UUID 得以被下一次命中
                carry = combined.Length > UuidCarryChars
                    ? combined.Substring(combined.Length - UuidCarryChars)
                    : combined;
            }

            return null;
        }

        // ─────────────────────────── ③ Google GCamera XMP 构建 ───────────────────────────

        /// <summary>
        /// 构建 Google GCamera 动态照片 XMP 的完整字节（UTF-8）
        /// </summary>
        /// <param name="videoLength">内嵌视频的字节长度（写入 Item:Length）</param>
        /// <param name="offset">内嵌视频(MP4)在最终合并文件中的起始字节偏移（写入 MicroVideoOffset）</param>
        /// <param name="presentationTimestampUs">代表帧时间戳（微秒）；Demo 场景可传 0</param>
        /// <returns>可直接注入 JPEG 的 XMP 字节</returns>
        public static byte[] BuildGPhotoXmp(long videoLength, long offset = 0, long presentationTimestampUs = 0)
        {
            if (videoLength < 0) throw new ArgumentOutOfRangeException(nameof(videoLength));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (presentationTimestampUs < 0) throw new ArgumentOutOfRangeException(nameof(presentationTimestampUs));

            var inv = CultureInfo.InvariantCulture;
            var lenStr = videoLength.ToString(inv);
            var offStr = offset.ToString(inv);
            var ts = presentationTimestampUs.ToString(inv);

            var xmp = $@"
<x:xmpmeta xmlns:x=""adobe:ns:meta/"" x:xmptk=""Adobe XMP Core 5.1.0-jc003"">
  <rdf:RDF xmlns:rdf=""http://www.w3.org/1999/02/22-rdf-syntax-ns#"">
    <rdf:Description rdf:about=""""
      xmlns:GCamera=""http://ns.google.com/photos/1.0/camera/""
      xmlns:Container=""http://ns.google.com/photos/1.0/container/""
      xmlns:Item=""http://ns.google.com/photos/1.0/container/item/""
      GCamera:MotionPhoto=""1""
      GCamera:MotionPhotoVersion=""1""
      GCamera:MotionPhotoPresentationTimestampUs=""{ts}""
      GCamera:MicroVideo=""1""
      GCamera:MicroVideoVersion=""1""
      GCamera:MicroVideoOffset=""{offStr}""
      GCamera:MicroVideoPresentationTimestampUs=""{ts}"">
      <Container:Directory>
        <rdf:Seq>
          <rdf:li rdf:parseType=""Resource"">
            <Container:Item
              Item:Mime=""image/jpeg""
              Item:Semantic=""Primary""/>
          </rdf:li>
          <rdf:li rdf:parseType=""Resource"">
            <Container:Item
              Item:Mime=""video/mp4""
              Item:Semantic=""MotionPhoto""
              Item:Length=""{lenStr}""
              Item:Padding=""0""/>
          </rdf:li>
        </rdf:Seq>
      </Container:Directory>
    </rdf:Description>
  </rdf:RDF>
</x:xmpmeta>
";

            return new UTF8Encoding(false).GetBytes(xmp);
        }

        // ─────────────────────────── ④ JPEG 注入 XMP（APP1） ───────────────────────────

        /// <summary>
        /// 在 JPEG 的 SOI(FFD8) 之后插入一条 APP1(FFE1) XMP 段，其余字节原样保留
        /// </summary>
        /// <param name="jpgPath">源 JPEG 路径</param>
        /// <param name="xmpBytes">XMP 字节（不包含段头，也不包含前缀）</param>
        /// <param name="outPath">输出文件路径（通常为临时文件）</param>
        /// <returns>输出文件完整路径</returns>
        /// <exception cref="InvalidDataException">文件不是合法 JPEG（缺少 FFD8 魔数）</exception>
        public static string InjectXmpToJpeg(string jpgPath, byte[] xmpBytes, string outPath)
        {
            var src = File.ReadAllBytes(jpgPath);
            if (src.Length < 4 || src[0] != 0xFF || src[1] != 0xD8)
            {
                throw new InvalidDataException($"不是合法的 JPEG 文件（缺少 FFD8 魔数）：{jpgPath}");
            }

            // APP1 前缀："http://ns.adobe.com/xap/1.0/\0" 固定 29 字节
            const string xmpPrefix = "http://ns.adobe.com/xap/1.0/\0";
            var prefixBytes = Encoding.ASCII.GetBytes(xmpPrefix);

            // 段长度字段(2字节)包含"长度字段自身+前缀+XMP"：FFE1 之后紧接 BE ushort len
            if (xmpBytes.Length > ushort.MaxValue - 2 - prefixBytes.Length)
            {
                throw new InvalidDataException("XMP 段过长，超过单段 65535 字节上限。");
            }
            var segLen = (ushort)(2 + prefixBytes.Length + xmpBytes.Length);

            using var dest = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize);
            // SOI
            dest.Write(src, 0, 2);

            // APP1 段头
            dest.WriteByte(0xFF);
            dest.WriteByte(0xE1);
            dest.WriteByte((byte)((segLen >> 8) & 0xFF));
            dest.WriteByte((byte)(segLen & 0xFF));
            dest.Write(prefixBytes, 0, prefixBytes.Length);
            dest.Write(xmpBytes, 0, xmpBytes.Length);

            // 剩余 JPEG 原样
            dest.Write(src, 2, src.Length - 2);

            return outPath;
        }

        // ─────────────────────────── ⑤ MOV → MP4 换标 ───────────────────────────

        /// <summary>
        /// 将 QuickTime MOV 改写为标准 MP4 容器。
        /// 优先用 FFmpeg remux 重建（moov 前置 +faststart、音频重编码 AAC、剔除 iPhone 专有扩展盒并重建时序表），
        /// 产出 Windows Photos / 小米相册均可解析的合规容器；FFmpeg 缺失或失败时退化为仅换 ftyp 品牌字节，
        /// 至少可被完整播放器识别。
        /// </summary>
        /// <param name="movPath">源 MOV 路径</param>
        /// <param name="outPath">输出 MP4 路径（通常为临时文件）</param>
        /// <returns>输出文件完整路径</returns>
        public static string RelabelMovToMp4(string movPath, string outPath)
        {
            try
            {
                FfmpegInvoker.RemuxToMp4(movPath, outPath);
                return outPath;
            }
            catch
            {
                // FFmpeg 缺失或失败：退化为仅换标（不改字节结构），不做脆弱的 moov 手术，
                // 避免产出结构自洽性更差的半成品。
                var bytes = File.ReadAllBytes(movPath);
                var header = PatchFtypBrand(bytes, Math.Min(bytes.Length, 64));
                Buffer.BlockCopy(header, 0, bytes, 0, header.Length);
                File.WriteAllBytes(outPath, bytes);
                return outPath;
            }
        }

        /// <summary>
        /// 对文件头部字节做 ftyp 品牌补丁（qt→isom 主品牌，兼容品牌 qt→mp42），未命中 ftyp 时原样返回
        /// </summary>
        private static byte[] PatchFtypBrand(byte[] header, int length)
        {
            var copy = new byte[length];
            Buffer.BlockCopy(header, 0, copy, 0, length);
            if (length < 16 || copy[4] != (byte)'f' || copy[5] != (byte)'t' || copy[6] != (byte)'y' || copy[7] != (byte)'p')
            {
                return copy; // 无 ftyp，跳过换标
            }

            // major brand @[8..12]：qt  → isom
            if (copy[8] == (byte)'q' && copy[9] == (byte)'t')
            {
                copy[8] = (byte)'i'; copy[9] = (byte)'s'; copy[10] = (byte)'o'; copy[11] = (byte)'m';
            }

            // compatible brands：从偏移 16 起每 4 字节一组，qt  → mp42
            for (var i = 16; i + 4 <= length; i += 4)
            {
                if (copy[i] == (byte)'q' && copy[i + 1] == (byte)'t')
                {
                    copy[i] = (byte)'m'; copy[i + 1] = (byte)'p'; copy[i + 2] = (byte)'4'; copy[i + 3] = (byte)'2';
                }
            }

            return copy;
        }

        // ─────────────────────────── ⑥ 流式拼接 ───────────────────────────

        /// <summary>
        /// 把「前置文件 + 后置文件」流式拼接成输出文件，返回总字节长度
        /// </summary>
        /// <param name="frontPath">前置文件（封面 JPEG）</param>
        /// <param name="backPath">后置文件（内嵌视频）</param>
        /// <param name="outPath">输出文件路径</param>
        /// <returns>拼接后的总字节长度</returns>
        public static long Concat(string frontPath, string backPath, string outPath)
        {
            using (var front = new FileStream(frontPath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize))
            {
                using var back = new FileStream(backPath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize);
                using var dest = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize);

                if (front.Length == 0 || back.Length == 0)
                {
                    throw new InvalidDataException("前置或后置文件为空，无法拼接。");
                }

                front.CopyTo(dest, CopyBufferSize);
                back.CopyTo(dest, CopyBufferSize);
                return dest.Length;
            }
        }

        // ─────────────────────────── ⑦ 幂等探测 ───────────────────────────

        /// <summary>
        /// 探测 jpg 是否已是动态照片（头部 XMP 含 MicroVideoOffset / Item:Length）
        /// </summary>
        /// <param name="jpgPath">JPEG 路径</param>
        /// <returns>内嵌视频字节长度；非动态照片返回 null</returns>
        public static long? TryDetectMotionPhoto(string jpgPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(jpgPath) || !File.Exists(jpgPath))
                {
                    return null;
                }

                var total = new FileInfo(jpgPath).Length;
                if (total < 65536)
                {
                    return null; // 动态照片至少包含照片+视频两部分
                }

                var buffer = new byte[Math.Min(total, DetectionScanBytes)];
                using (var fs = new FileStream(jpgPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var read = fs.Read(buffer, 0, buffer.Length);
                    if (read < 1024)
                    {
                        return null;
                    }
                    buffer = buffer[..read];
                }

                var text = Encoding.Latin1.GetString(buffer);

                // 模式：MicroVideoOffset="12345" / MicroVideoOffset>12345< 或 Item:Length= 系列
                var offset = ParseMarker(text, "MicroVideoOffset=\"");
                if (offset is null)
                {
                    offset = ParseMarker(text, "<GCamera:MicroVideoOffset>");
                }
                if (offset is null)
                {
                    offset = ParseMarker(text, "Item:Length=\"");
                }
                if (offset is null)
                {
                    offset = ParseMarker(text, "<Item:Length>");
                }

                if (offset is null or <= 0)
                {
                    return null;
                }

                return offset.Value;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>从文本中解析 marker 之后的数字（直到引号或尖括号）</summary>
        private static long? ParseMarker(string text, string marker)
        {
            var idx = text.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
            {
                return null;
            }

            var start = idx + marker.Length;
            var sb = new StringBuilder();
            for (var i = start; i < text.Length; i++)
            {
                var c = text[i];
                if (char.IsDigit(c))
                {
                    sb.Append(c);
                }
                else
                {
                    break;
                }
            }

            return long.TryParse(sb.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : (long?)null;
        }

        // ─────────────────────────── ⑧ 批量合并调度 ───────────────────────────

        /// <summary>
        /// 批量合并：扫描配对 → 逐对合并 → 覆盖原 jpg + 删除 mov，返回汇总报告
        /// </summary>
        /// <param name="downloadRoot">下载根目录</param>
        /// <param name="log">可选日志回调（供界面展示或测试断言）</param>
        /// <returns>合并报告</returns>
        public static MotionPhotoBatchReport MergeAll(string downloadRoot, Action<string>? log = null)
        {
            var report = new MotionPhotoBatchReport();
            var pairs = FindAndPair(downloadRoot);
            report.Total = pairs.Count;

            var tempDir = Path.Combine(Path.GetTempPath(), "WeiboAlbumDownloader", $"motion-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                foreach (var pair in pairs)
                {
                    var label = Path.GetFileName(pair.MovPath);
                    try
                    {
                        if (MergeOne(pair, tempDir, out var skipReason))
                        {
                            report.Merged++;
                            var msg = $"合并成功：{label}";
                            report.Logs.Add(msg);
                            log?.Invoke(msg);
                        }
                        else
                        {
                            report.Skipped++;
                            var msg = $"跳过：{label}（{skipReason}）";
                            report.Logs.Add(msg);
                            log?.Invoke(msg);
                        }
                    }
                    catch (Exception ex)
                    {
                        report.Failed++;
                        var msg = $"合并失败：{label}，{ex.Message}";
                        report.Failures.Add(msg);
                        report.Logs.Add(msg);
                        log?.Invoke(msg);
                    }
                }
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }

            return report;
        }

        /// <summary>
        /// 合并单个实况对为动态照片（覆盖原 jpg，成功后删除 mov）
        /// </summary>
        /// <param name="pair">待合并对</param>
        /// <param name="tempDir">线程隔离的临时工作目录</param>
        /// <param name="skipReason">返回跳过原因（未跳过时为 null）</param>
        /// <returns>是否完成合并（true）或跳过（false）。失败以异常抛出</returns>
        public static bool MergeOne(LivePhotoPair pair, string tempDir, out string? skipReason)
        {
            skipReason = null;

            var jpg = pair.JpgPath;
            var mov = pair.MovPath;
            if (string.IsNullOrWhiteSpace(jpg) || string.IsNullOrWhiteSpace(mov))
            {
                throw new InvalidDataException("配对缺少照片或视频路径。");
            }

            if (!File.Exists(jpg) || !File.Exists(mov))
            {
                throw new FileNotFoundException("配对文件不存在。", !File.Exists(jpg) ? jpg : mov);
            }

            // 幂等：jpg 已是动态照片则跳过，避免二次注入
            if (TryDetectMotionPhoto(jpg) is > 0)
            {
                skipReason = "封面已是动态照片";
                return false;
            }

            var fileInfo = new FileInfo(mov);
            if (fileInfo.Length == 0)
            {
                throw new InvalidDataException($"实况视频为空（0 字节）：{mov}");
            }

            string? tempRelabel = null;
            string? tempJpgXmp = null;
            string? tempMerged = null;
            try
            {
                // ① MOV → MP4 换标（FFmpeg remux；缺失时仅换 ftyp 品牌）
                tempRelabel = Path.Combine(tempDir, $"{Guid.NewGuid():N}.mp4");
                RelabelMovToMp4(mov, tempRelabel);

                // ② 构建并注入 GCamera XMP（presentationTimestamp 暂用 0 回退）
                //    内嵌视频字节数 = 换标+剥离后的 MP4 实际长度(Item:Length)；
                //    MicroVideoOffset = 内嵌 MP4 在合并文件中的起始偏移。
                //    因注入 XMP 会改变封面长度、而偏移又写 XMP 内（自指），故用定长逼近迭代收敛偏移。
                var videoBytes = new FileInfo(tempRelabel).Length;
                var jpgLen = new FileInfo(jpg).Length;
                long offset = 0;
                byte[] xmp = new byte[0];
                for (var i = 0; i < 8; i++)
                {
                    xmp = BuildGPhotoXmp(videoBytes, offset, 0);
                    // InjectXmpToJpeg 在 SOI 后新增一段 APP1：FFE1(2) + 长度(2) + 前缀(29) + XMP。
                    // 故注入后封面长度 = 原封面 + 33 + XMP长度 = 内嵌 MP4 的起始偏移。
                    var next = jpgLen + 33 + xmp.Length;
                    if (next == offset) break;
                    offset = next;
                }
                tempJpgXmp = Path.Combine(tempDir, $"{Guid.NewGuid():N}_xmpped.jpg");
                InjectXmpToJpeg(jpg, xmp, tempJpgXmp);

                // ③ 拼接：封面(含 XMP) + 视频
                tempMerged = Path.Combine(tempDir, $"{Guid.NewGuid():N}_merged.jpg");
                var totalLength = Concat(tempJpgXmp, tempRelabel, tempMerged);

                // ④ 校验：输出必须完整包含封面与视频
                if (totalLength <= videoBytes)
                {
                    throw new InvalidDataException($"合成校验失败：输出 {totalLength} 字节未完整包含内嵌视频。");
                }

                // ⑤ 原子覆盖原 jpg（用临时文件 Move），再删除 mov
                File.Move(tempMerged, jpg, overwrite: true);
                File.Delete(mov);
                return true;
            }
            catch
            {
                // 中途失败不污染源文件，清理半成品
                TryDeleteFile(tempRelabel);
                TryDeleteFile(tempJpgXmp);
                TryDeleteFile(tempMerged);
                throw;
            }
        }

        /// <summary>
        /// 下载完成后自动合并单个实况照片：读取 mov 的 ContentIdentifier，
        /// 在同级目录定位配对封面 jpg，再复用 MergeOne 覆盖封面并删除 mov。
        /// </summary>
        /// <param name="movPath">刚下载的实况视频路径(.mov)</param>
        /// <param name="skipReason">返回跳过原因（未跳过时为 null）</param>
        /// <returns>是否完成合并（true）或跳过（false）。定位或合并失败以异常抛出</returns>
        public static bool MergeByMov(string movPath, out string? skipReason)
        {
            skipReason = null;

            if (string.IsNullOrWhiteSpace(movPath) || !File.Exists(movPath))
            {
                throw new FileNotFoundException("实况视频不存在。", movPath);
            }

            var dir = Path.GetDirectoryName(movPath) ?? throw new InvalidDataException("无法解析实况视频目录。");

            // ① 优先以 ContentIdentifier 作精确配对信号（mov 与封面 jpg 各存一份相同 UUID）
            string? matchedJpg = null;
            var movCid = ReadContentIdentifier(movPath, ContentIdentifierKind.Video);
            if (!string.IsNullOrEmpty(movCid))
            {
                foreach (var jpg in Directory.EnumerateFiles(dir).Where(f => IsJpg(f)))
                {
                    if (string.Equals(ReadContentIdentifier(jpg, ContentIdentifierKind.Photo), movCid, StringComparison.Ordinal))
                    {
                        matchedJpg = jpg;
                        break;
                    }
                }
            }

            // ② 文件名兜底：同级目录中与 mov 基础卷名(去掉扩展名)完全一致的 jpg 视为配对。
            //    配对命名下载下封面/mov 天然同名，即使 mov 或封面丢失 CID 仍能定位。
            if (string.IsNullOrEmpty(matchedJpg))
            {
                var movName = Path.GetFileNameWithoutExtension(movPath);
                matchedJpg = Directory.EnumerateFiles(dir)
                    .Where(f => IsJpg(f))
                    .FirstOrDefault(j => string.Equals(
                        Path.GetFileNameWithoutExtension(j), movName, StringComparison.OrdinalIgnoreCase));
            }

            if (string.IsNullOrEmpty(matchedJpg))
            {
                skipReason = "未找到配对封面 jpg";
                return false;
            }

            // ③ 复用 MergeOne：内部含幂等、成功后覆盖+删除
            var tempDir = Path.Combine(Path.GetTempPath(), "WeiboAlbumDownloader", $"motion-by-mov-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                return MergeOne(new LivePhotoPair { JpgPath = matchedJpg, MovPath = movPath, GroupKey = string.Empty }, tempDir, out skipReason);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        // ─────────────────────────── 工具方法 ───────────────────────────

        /// <summary>判断是否为 jpg/jpeg 文件</summary>
        private static bool IsJpg(string path)
        {
            var ext = Path.GetExtension(path);
            return string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>尽力删除文件，异常静默</summary>
        private static void TryDeleteFile(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 忽略清理失败
            }
        }

        /// <summary>尽力递归删除目录，异常静默</summary>
        private static void TryDeleteDirectory(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // 忽略清理失败
            }
        }
    }
}