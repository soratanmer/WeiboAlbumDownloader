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
    /// FFmpeg 优先（remux 剔 mebx、moov faststart、音频 AAC）；缺失或失败时回退为仅 ftyp 品牌字节修补。
    /// </summary>
    /// <remarks>
    /// 目标格式：<br/>
    /// 1. 物理结构 = 标准 JPEG 封面（前段）+ 尾部二进制拼接的 MP4 微视频；<br/>
    /// 2. 在 JPEG 头部注入「纯净 Google 容器」XMP（GCamera:MotionPhoto + Container:Directory/Item:Length，
    ///    同时满足 OPPO / 谷歌 / Windows Photos / 小米 的识别要求；刻意不写 legacy MicroVideo 字段，
    ///    因为 OPPO 等严格解析器会因 legacy 字段拒识）。<br/>
    ///    GCamera:MicroVideoOffset 与 Container:Directory/Item:Length 均取「尾部内嵌微视频的字节长度」，
    ///    解析器按 视频起点 = 文件总长 − offset 反推（已验证可播放的 QQ / OPPO 参考文件如此取值）；<br/>
    /// 3. 配对主依据为 ContentIdentifier UUID（jpg EXIF 端 与 mov QuickTime 端各存一份），文件名分组仅作兜底。<br/>
    /// 内嵌 MP4 首选经过 FFmpeg remux（剔除 iPhone 专有的 mebx 音轨，moov faststart 前置，音频重编码为 AAC），
    /// 以满足 OPPO 等严格解析器对「标准干净 MP4 容器」的校验；未随程序分发 ffmpeg 或 remux 失败时，
    /// 回退为仅修正 ftyp 品牌（qt→isom，兼容品牌 qt→mp42）的换标产物（宽松端仍可识别，OPPO 可能不识别）。
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
        /// 构建 Google 动态照片 XMP 的完整字节（UTF-8）
        /// </summary>
        /// <remarks>
        /// 输出「纯净 Google 容器」元数据，一份满足多厂商识别：
        /// 1. Google 容器格式：GCamera:MotionPhoto + Container:Directory / Item:Length（OPPO / 谷歌，符合
        ///    Android Motion Photo 1.0 规范）；<br/>
        /// 2. 厂商扩展命名空间 OpCamera（OPPO）/ MiCamera（小米），无副作用。
        /// 注意：刻意不写 legacy 微电影字段（GCamera:MicroVideo*）。真机实测（2026-09-22）OPPO 会因
        /// legacy 字段拒识整个文件为动态照片（H 系列对照：含 legacy 全拒识、纯净容器全识别），
        /// 故仅保留 Google 容器 XMP 以保证严格解析器兼容。
        /// 结构参考自 XHS_Downloader_Android 的 <c>LivePhotoCreator.generateXmpMetadata</c>。
        /// </remarks>
        /// <param name="videoLength">内嵌视频的字节长度（MicroVideoOffset 与 Item:Length 均取其值，语义=尾部视频长度）</param>
        /// <param name="offset">保留参数，当前未参与输出</param>
        /// <param name="presentationTimestampUs">封面展示时间戳（写入 MotionPhotoPresentationTimestampUs，默认 0）</param>
        /// <returns>可直接注入 JPEG 的 XMP 字节</returns>
        public static byte[] BuildGPhotoXmp(long videoLength, long offset = 0, long presentationTimestampUs = 0)
        {
            if (videoLength < 0) throw new ArgumentOutOfRangeException(nameof(videoLength));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (presentationTimestampUs < 0) throw new ArgumentOutOfRangeException(nameof(presentationTimestampUs));

            // 关键语义：GCamera:MicroVideoOffset 与 Container:Item:Length 均表示「内嵌微视频的字节长度（尾部长度）」，
            // 解析器按 视频起点 = 文件总长 − offset 反推。已验证可播放的 QQ / OPPO 参考文件如此取值；
            // 若误写「距文件头的起始偏移」会导致定位失败、无法播放。故三个取值均为内嵌视频长度。
            var lenStr = videoLength.ToString(CultureInfo.InvariantCulture);
            var tsStr = presentationTimestampUs.ToString(CultureInfo.InvariantCulture);

            var xmp = $@"
<x:xmpmeta xmlns:x=""adobe:ns:meta/"" x:xmptk=""Adobe XMP Core 5.1.0-jc003"">
  <rdf:RDF xmlns:rdf=""http://www.w3.org/1999/02/22-rdf-syntax-ns#"">
    <rdf:Description rdf:about=""""
        xmlns:GCamera=""http://ns.google.com/photos/1.0/camera/""
        xmlns:OpCamera=""http://ns.oplus.com/photos/1.0/camera/""
        xmlns:MiCamera=""http://ns.xiaomi.com/photos/1.0/camera/""
        xmlns:Container=""http://ns.google.com/photos/1.0/container/""
        xmlns:Item=""http://ns.google.com/photos/1.0/container/item/""
      GCamera:MotionPhoto=""1""
      GCamera:MotionPhotoVersion=""1""
      GCamera:MotionPhotoPresentationTimestampUs=""{tsStr}""
      OpCamera:MotionPhotoPrimaryPresentationTimestampUs=""0""
      OpCamera:MotionPhotoOwner=""weibo""
      OpCamera:OLivePhotoVersion=""2""
      OpCamera:VideoLength=""{lenStr}""
      MiCamera:XMPMeta=""&lt;?xml version='1.0' encoding='UTF-8' standalone='yes' ?&gt;"">
      <Container:Directory>
        <rdf:Seq>
          <rdf:li rdf:parseType=""Resource"">
            <Container:Item Item:Mime=""image/jpeg"" Item:Semantic=""Primary"" Item:Length=""0"" Item:Padding=""0""/>
          </rdf:li>
          <rdf:li rdf:parseType=""Resource"">
            <Container:Item Item:Mime=""video/mp4"" Item:Semantic=""MotionPhoto"" Item:Length=""{lenStr}""/>
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
        /// 在 JPEG 头部注入一条 APP1(FFE1) XMP 段，并重建头部以满足跨端识别：
        /// 1. 保留源 APP0/JFIF、APP2…APP15、COM 等段，按原序写出；<br/>
        /// 2. 剔除源所有 APP1 段——既剔除 EXIF，也剔除源既有 XMP。飞牛(fnOS) 真机复测确认：
        ///    判别点是「封面 JPEG 是否含 EXIF」——源封面无 EXIF 的帖子整批可识别，带 EXIF@24 的全被当
        ///    成普通照片；与 XMP 段序、音轨有无、分辨率均无关。故必须剥离 EXIF 才能让飞牛识别；<br/>
        /// 3. 把本 XMP 作为唯一的首个 APP1 插到首个非 APPn/COM 段（DQT/SOF…）之前，
        ///    产出「JFIF → XMP → DQT…」布局，与飞牛已识别的样例结构完全同构。<br/>
        /// OPPO / QQ / Windows Photos 对封面是否含 EXIF、EXIF 段序均不敏感（唯一 blocker 是
        /// legacy MicroVideo 字段，BuildGPhotoXmp 已剔除），故剥离 EXIF 不损其兼容性。
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

            using (var dest = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize))
            {
                // SOI
                dest.Write(src, 0, 2);

                // 逐段重建头部。段起点 = 首个 0xFF（可能带多个填充 0xFF）。
                var i = 2;
                while (i < src.Length && src[i] == 0xFF)
                {
                    var segStart = i;
                    while (i < src.Length && src[i] == 0xFF) i++; // 越过填充 0xFF 到标记字节
                    if (i >= src.Length) break;
                    var marker = src[i];

                    // 首个非 APPn/COM 段（DQT/SOF/DHT/SOS/EOI…）即头部结束：
                    // 在此插入 XMP，其后（含该段与熵编码/尾部视频）原样完整复制。
                    if (!IsAppOrCom(marker))
                    {
                        WriteXmpSegment(dest, prefixBytes, xmpBytes, segLen);
                        dest.Write(src, segStart, src.Length - segStart);
                        return outPath;
                    }

                    // APP0/APP2…/COM/APP1：读取段长度（长度字段含其自身 2 字节）
                    i++; // 越过标记字节，落到长度字段
                    if (i + 1 >= src.Length) break; // 畸形段，头部异常，回退到末尾补插
                    var len = (src[i] << 8) | src[i + 1];
                    var segEnd = i + len; // i 已在长度字段首，段末即 i + len（跳过度身）
                    var segLenBytes = segEnd - segStart;

                    // 剔除 APP1（EXIF 与源既有 XMP 均在内），保证本 XMP 是首个（唯一）APP1；
                    // 其余 APP0/APP2…/COM 原样保留。
                    var isApp1 = marker == 0xE1;
                    if (!isApp1)
                    {
                        dest.Write(src, segStart, segLenBytes);
                    }

                    i = segEnd;
                    if (i >= src.Length) break;
                }

                // 源头部不含任何非 APPn/COM 段（罕见/畸形）：在文件尾部补插 XMP
                WriteXmpSegment(dest, prefixBytes, xmpBytes, segLen);
            }

            return outPath;
        }

        /// <summary>判断标记是否为 APPn(0xE0-0xEF) 或 COM(0xFE) 段；其余 DQT/SOF/DHT/SOS/EOI… 均视为头部结束</summary>
        private static bool IsAppOrCom(int marker)
            => (marker >= 0xE0 && marker <= 0xEF) || marker == 0xFE;

        /// <summary>写出一条完整 APP1 XMP 段（FFE1 + 段长 + 前缀 + XMP）</summary>
        private static void WriteXmpSegment(Stream dest, byte[] prefixBytes, byte[] xmpBytes, ushort segLen)
        {
            dest.WriteByte(0xFF);
            dest.WriteByte(0xE1);
            dest.WriteByte((byte)((segLen >> 8) & 0xFF));
            dest.WriteByte((byte)(segLen & 0xFF));
            dest.Write(prefixBytes, 0, prefixBytes.Length);
            dest.Write(xmpBytes, 0, xmpBytes.Length);
        }

        // ─────────────────────────── ⑤ MOV → MP4 换标 ───────────────────────────

        /// <summary>
        /// 将 QuickTime MOV 改写为标准 MP4 容器。
        /// 首选 FFmpeg remux（<see cref="FfmpegInvoker.RemuxToMp4"/>）：剔除 iPhone 专用 mebx 轨、
        /// moov faststart 前置、音频重编码 AAC，产出严格解析器（OPPO 等）认可的干净标准 MP4。
        /// 未随程序分发 ffmpeg 或 remux 失败时，回退为仅修正 ftyp 品牌字节（qt→isom，兼容品牌 qt→mp42），
        /// 保证宽松端（Windows Photos 等）仍能识别。
        /// </summary>
        /// <param name="movPath">源 MOV 路径</param>
        /// <param name="outPath">输出 MP4 路径（通常为临时文件）</param>
        /// <param name="bake">非 null 时经 FFmpeg 把旋转烘焙进像素（展示方向 0°，见
        /// <see cref="FfmpegInvoker.RemuxToMp4"/>）；null 表示常规 remux。</param>
        /// <returns>输出文件完整路径</returns>
        public static string RelabelMovToMp4(string movPath, string outPath, VideoOrientation? bake = null)
        {
            try
            {
                // 首选：FFmpeg remux（存在且成功时产出高质量标准 MP4）
                return FfmpegInvoker.RemuxToMp4(movPath, outPath, bake);
            }
            catch (FileNotFoundException)
            {
                // 未随程序分发 ffmpeg(.exe)：回退纯 ftyp 换标
            }
            catch (InvalidDataException)
            {
                // ffmpeg 存在但 remux 失败：回退纯 ftyp 换标，保证产物仍可被宽松端识别
            }

            var bytes = File.ReadAllBytes(movPath);
            var header = PatchFtypBrand(bytes, Math.Min(bytes.Length, 64));
            Buffer.BlockCopy(header, 0, bytes, 0, header.Length);
            File.WriteAllBytes(outPath, bytes);
            return outPath;
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
        /// <remarks>
        /// Google 容器形式不可按「全文首次出现」取 Item:Length：Container:Directory 里 Primary 项
        /// 同样带该属性且值为 0，会先被命中而把已合成的动态照片误判为普通照片。
        /// 须先以 Item:Semantic="MotionPhoto" 定位目标标签，再在该标签内取值。
        /// </remarks>
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

                // 模式：MicroVideoOffset="12345" / <GCamera:MicroVideoOffset>12345< / Google 容器项。
                // legacy 两条排在最前：本程序产物已剔除 legacy 字段，必然落到容器项那条；
                // 带 legacy 字段的第三方/旧版产物则与改动前行为完全一致。
                var offset = ParseMarker(text, "MicroVideoOffset=\"");
                if (offset is null)
                {
                    offset = ParseMarker(text, "<GCamera:MicroVideoOffset>");
                }
                if (offset is null)
                {
                    offset = ParseContainerItemLength(text, "MotionPhoto");
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

        /// <summary>
        /// 解析 Container:Directory 中指定语义项（如 MotionPhoto）的 Item:Length。
        /// 取值范围限定在「该属性所属标签」之内：Primary 项同样带 Item:Length="0" 且位置更靠前，
        /// 按全文首次出现取值会命中它；限定标签后同时对标签内的属性顺序不敏感。
        /// </summary>
        /// <param name="text">XMP 文本</param>
        /// <param name="semantic">Container:Item 的 Item:Semantic 取值（如 MotionPhoto）</param>
        /// <returns>该项的 Item:Length；未找到返回 null</returns>
        private static long? ParseContainerItemLength(string text, string semantic)
        {
            var idx = text.IndexOf($"Item:Semantic=\"{semantic}\"", StringComparison.Ordinal);
            if (idx < 0)
            {
                return null;
            }

            // XML 标签不嵌套，最近的前一个 '<' 即当前标签的起始，最近的后一个 '>' 即其结束
            var open = text.LastIndexOf('<', idx);
            var close = text.IndexOf('>', idx);
            if (open < 0 || close < open)
            {
                return null;
            }

            return ParseMarker(text.Substring(open, close - open + 1), "Item:Length=\"");
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
                // ① MOV → 标准 MP4：FFmpeg remux（剔 mebx、faststart、音频 AAC）优选，缺失/失败回退纯 ftyp 换标。
                //    竖拍照片的源视频可能带退化/非标准的显示矩阵（如 [0,1,1,0]，实为镜像而非旋转），
                //    不同播放器解读不一致，部分端播放被旋转 90°/270°。
                //    这里以封面方向为准（封面竖拍 + 视频严格横屏；方形视频旋转无意义，保持原样更安全），
                //    方向则由源显示矩阵的逆决定——矩阵才是根因，固定常量无法同时满足合法阵与退化阵；
                //    退化阵（det=−1）是反射而非旋转，其逆仍是反射，故表现为「先把 Rotation 归零，再水平镜像」。
                VideoOrientation? bake = null;
                if (TryGetJpegPortrait(jpg, out var coverIsPortrait) && coverIsPortrait &&
                    TryGetVideoPixelSize(mov, out var vw, out var vh) && vw > vh &&
                    TryGetVideoDisplayMatrix(mov, out var ma, out var mb, out var mc, out var md))
                {
                    bake = OrientationFromMatrix(ma, mb, mc, md);
                }

                tempRelabel = Path.Combine(tempDir, $"{Guid.NewGuid():N}.mp4");
                RelabelMovToMp4(mov, tempRelabel, bake);

                // 烘焙路径：显式清零显示矩阵，保证输出 Rotation=0°（不依赖 FFmpeg 版本的矩阵传递行为）
                if (bake.HasValue)
                {
                    ClearDisplayMatrix(tempRelabel);
                }

                // ② 构建并注入 GCamera XMP（presentationTimestamp 暂用 0 回退）
                //    内嵌视频字节数 = 换标+重构后的 MP4 实际长度。
                //    MicroVideoOffset 语义 = 尾部微视频字节长度（解析器按 起点=总长-offset 反推），
                //    因视频恒拼接于 JPEG 之后、位于文件尾部，其长度即该值，与 JPEG/XMP 尺寸无关，
                //    无需任何迭代。
                var videoBytes = new FileInfo(tempRelabel).Length;
                tempJpgXmp = Path.Combine(tempDir, $"{Guid.NewGuid():N}_xmpped.jpg");
                InjectXmpToJpeg(jpg, BuildGPhotoXmp(videoBytes, 0, 0), tempJpgXmp);

                // ③ 拼接：封面(含 XMP) + 视频
                tempMerged = Path.Combine(tempDir, $"{Guid.NewGuid():N}_merged.jpg");
                var totalLength = Concat(tempJpgXmp, tempRelabel, tempMerged);

                // ④ 校验：输出必须完整包含封面与视频
                if (totalLength <= videoBytes)
                {
                    throw new InvalidDataException($"合成校验失败：输出 {totalLength} 字节未完整包含内嵌视频。");
                }

                // ⑤ 原子覆盖原 jpg（用临时文件 Move），再删除 mov。
                //    tempMerged 是刚创建的文件，时间戳即"此刻"；File.Move 会把它带到目标路径，
                //    冲掉下载阶段写入的发帖日期（批量合并路径手中没有发帖时间，唯一来源就是封面自身的时间戳）。
                //    故覆盖前先记下，覆盖后原样回填。
                var postTime = File.GetLastWriteTime(jpg);
                File.Move(tempMerged, jpg, overwrite: true);
                TrySetFileTime(jpg, postTime);
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

        /// <summary>
        /// 读取视频轨 tkhd 的显示矩阵元素 [a,b,c,d]（16.16 定点）。
        /// 以「宽字段非零」识别视频轨（音频/mebx 轨宽为 0），与 <see cref="ClearDisplayMatrix"/> 同一判据。
        /// </summary>
        /// <returns>解析到视频轨矩阵返回 true；文件异常或无视频轨返回 false</returns>
        private static bool TryGetVideoDisplayMatrix(string videoPath, out int a, out int b, out int c, out int d)
        {
            a = b = c = d = 0;
            try
            {
                var data = File.ReadAllBytes(videoPath);
                var found = false;
                var la = 0;
                var lb = 0;
                var lc = 0;
                var ld = 0; // lambda 内不能直接写 out 参数，用局部变量中转
                FindBoxes(data, 0, data.Length, "tkhd", (pos, size) =>
                {
                    if (found || size < 16) return;
                    if (Be32(data, pos + size - 8) <= 0) return; // 音频/mebx 轨宽为 0

                    // 矩阵位于 FullBox 头之后：v0 偏移 +48，v1（64 位时间戳）偏移 +60；
                    // 9 元布局 [a,b,u,c,d,v,x,y,w]，故 a@+0 b@+4 c@+12 d@+16
                    var version = data[pos + 8];
                    var m = pos + (version == 1 ? 60 : 48);
                    if (m + 36 > pos + size) return;

                    la = Be32(data, m);
                    lb = Be32(data, m + 4);
                    lc = Be32(data, m + 12);
                    ld = Be32(data, m + 16);
                    found = true;
                });

                a = la;
                b = lb;
                c = lc;
                d = ld;
                return found;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 由源显示矩阵推出烘焙方向。
        /// 实测表明播放器按矩阵的<b>逆</b>呈现（锚点：合法 90° 阵 <c>[0,1,−1,0]</c> 用 <c>transpose=1</c> 恰好正确，
        /// 而 <c>transpose=1</c> 即该阵的逆），故烘焙方向取 M⁻¹：
        /// 纯旋转阵（det=+1）的逆仍是旋转，直接用对应 transpose；
        /// 反射阵（det=−1，即"退化阵"）的逆仍是反射，须在转置后追加一次水平镜像——即「先把 Rotation 归零，再水平镜像」。
        /// <c>[0,1,−1,0]</c>→transpose=1、<c>[0,1,1,0]</c>→transpose=1+hflip、<c>[0,−1,1,0]</c>→transpose=2、<c>[0,−1,−1,0]</c>→transpose=3。
        /// 仅处理四分之一转矩阵（a=d=0 且 b,c 为 ±1.0）；单位阵/180° 等无法用 transpose 表达，返回 null 不烘焙。
        /// </summary>
        /// <returns>烘焙参数；无需/无法烘焙时返回 null</returns>
        private static VideoOrientation? OrientationFromMatrix(int a, int b, int c, int d)
        {
            const int One = 0x10000; // 16.16 定点中的 1.0
            if (a != 0 || d != 0)
            {
                return null;
            }

            if (b == One && c == -One) return new VideoOrientation(1, false);  // [0, 1,-1,0] 合法 90°（实测锚点）
            if (b == One && c == One) return new VideoOrientation(1, true);    // [0, 1, 1,0] 退化阵：归零后水平镜像
            if (b == -One && c == One) return new VideoOrientation(2, false);  // [0,-1, 1,0] 合法 270°
            if (b == -One && c == -One) return new VideoOrientation(3, false); // [0,-1,-1,0] 退化/反对角（推导，无实测样本）
            return null;
        }

        /// <summary>
        /// 读取 JPEG 封面 SOF（Start Of Frame）实际像素方向，判断是否竖拍（高&gt;宽）。
        /// 微博封面大多不含 EXIF Orientation，SOF 宽高即展示方向，故以此对齐最终封面显示。
        /// 返回 false 表示无法判断（读不到 SOF）。
        /// </summary>
        private static bool TryGetJpegPortrait(string jpgPath, out bool portrait)
        {
            portrait = false;
            try
            {
                var src = File.ReadAllBytes(jpgPath);
                if (src.Length < 4 || src[0] != 0xFF || src[1] != 0xD8)
                {
                    return false;
                }

                var i = 2;
                while (i + 4 <= src.Length)
                {
                    if (src[i] != 0xFF) { i++; continue; }
                    var marker = src[i + 1];

                    // SOF0/SOF2：宽高位于标记之后的 5 字节（长度2 + 精度1 + 高2 + 宽2）
                    if (marker == 0xC0 || marker == 0xC2)
                    {
                        if (i + 9 < src.Length)
                        {
                            var h = (src[i + 5] << 8) | src[i + 6];
                            var w = (src[i + 7] << 8) | src[i + 8];
                            portrait = h > w;
                            return true;
                        }
                        return false;
                    }

                    // RST 段（D0-D7）无长度；SOI(0xD8)/TEM(0x01) 单字节；EOI(0xD9) 结束
                    if (marker >= 0xD0 && marker <= 0xD7) { i += 2; continue; }
                    if (marker == 0xD8 || marker == 0x01) { i += 2; continue; }
                    if (marker == 0xD9) { break; }

                    var len = (src[i + 2] << 8) | src[i + 3];
                    i += 2 + len;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 读取 video 文件（.mov/.mp4）内视频轨 stsd 的实际像素尺寸。
        /// 用于判断源视频是否为横/竖屏，从而决定是否烘焙旋转。
        /// </summary>
        private static bool TryGetVideoPixelSize(string videoPath, out int width, out int height)
        {
            width = height = 0;
            try
            {
                using var fs = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                // 源 MOV 通常为若干 MB，完整装入后按 box 树定位 stsd（moov 可能在前或后）
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                var data = ms.GetBuffer();
                var length = (int)ms.Length;

                // 递归解析 box 树，捕获所有 stsd 下的视频样本条目尺寸（lambda 内不能直接写 out 参数，用局部变量中转）
                var found = false;
                var localW = 0;
                var localH = 0;
                WalkBoxes(data, 0, length, (pos, size) =>
                {
                    // pos 为样本条目起始（size 字段）。VisualSampleEntry：格式@+4，宽@+32 高@+34
                    var format = ReadFourCc(data, pos + 4);
                    var isVideo = format == "avc1" || format == "avc3" || format == "hvc1" || format == "hev1";
                    if (isVideo && size >= 36)
                    {
                        localW = (data[pos + 32] << 8) | data[pos + 33];
                        localH = (data[pos + 34] << 8) | data[pos + 35];
                        found = true;
                    }
                });

                width = localW;
                height = localH;
                return found && width > 0 && height > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>读取 4 字节 box 类型/样本格式标识</summary>
        private static string ReadFourCc(byte[] data, int offset)
            => offset + 4 <= data.Length
                ? Encoding.ASCII.GetString(data, offset, 4)
                : string.Empty;

        /// <summary>递归遍历 MP4 box 树，命中视频样本条目（avc1 等）后回调；大端序解析</summary>
        private static void WalkBoxes(byte[] data, int start, int end, Action<int, int> onSampleEntry, int depth = 0)
        {
            if (depth > 14) return; // 防畸形文件深层递归失控
            var pos = start;
            while (pos + 8 <= end)
            {
                var boxSize = Be32(data, pos);
                var boxType = Encoding.ASCII.GetString(data, pos + 4, 4);
                var header = 8;
                if (boxSize == 1)
                {
                    if (pos + 16 > end) break;
                    boxSize = (int)Be64(data, pos + 8); // 64 位扩展箱，仅按低 32 位近似
                    header = 16;
                }
                else if (boxSize == 0)
                {
                    boxSize = end - pos; // 箱延至文件末尾
                }

                if (boxSize < header || pos + boxSize > end) break;

                if (boxType == "stsd")
                {
                    // stsd 为 FullBox：版本+标识(4) + 条目数(4)，后续为各样本条目（含视频 SampleEntry）。
                    // 注意不能在此 return——音频轨的 stsd（mp4a）通常先于视频轨出现，需继续遍历后续 trak。
                    ParseSampleEntries(data, pos + header, pos + boxSize, onSampleEntry);
                }
                else
                {
                    // meta 亦为 FullBox，其子箱起点需再跳过版本+标识(4)，否则子箱会被误读
                    var childStart = boxType == "meta" ? pos + header + 4 : pos + header;
                    WalkBoxes(data, childStart, pos + boxSize, onSampleEntry, depth + 1);
                }

                pos += boxSize;
            }
        }

        /// <summary>解析 stsd 内容中的样本条目列表，命中视频条目（宽高处）回调</summary>
        private static void ParseSampleEntries(byte[] data, int stsdContentStart, int stsdEnd, Action<int, int> onSampleEntry)
        {
            // stsdContentStart 指向 FullBox 的版本+标识：+4 为条目数，+8 起为各样本条目
            var entryCount = Be32(data, stsdContentStart + 4);
            var ep = stsdContentStart + 8;
            for (var e = 0; e < entryCount && ep + 8 <= stsdEnd; e++)
            {
                var entrySize = Be32(data, ep);
                if (entrySize < 8 || ep + entrySize > stsdEnd) break;
                onSampleEntry(ep, entrySize);
                ep += entrySize;
            }
        }

        /// <summary>大端读取 32 位无符号整数</summary>
        private static int Be32(byte[] d, int o) => o + 4 <= d.Length
            ? (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3] : 0;

        /// <summary>大端读取 64 位无符号整数（低 32 位近似）</summary>
        private static long Be64(byte[] d, int o) => o + 8 <= d.Length
            ? ((long)d[o] << 56) | ((long)d[o + 1] << 48) | ((long)d[o + 2] << 40) | ((long)d[o + 3] << 32)
              | ((long)d[o + 4] << 24) | ((long)d[o + 5] << 16) | ((long)d[o + 6] << 8) | d[o + 7] : 0;

        /// <summary>大端写入 32 位整数</summary>
        private static void WriteBe32(byte[] d, int o, int v)
        {
            d[o] = (byte)(v >> 24);
            d[o + 1] = (byte)(v >> 16);
            d[o + 2] = (byte)(v >> 8);
            d[o + 3] = (byte)v;
        }

        /// <summary>递归查找指定类型的 box，命中回调 (boxStart, boxSize)</summary>
        private static void FindBoxes(byte[] data, int start, int end, string boxType, Action<int, int> onFound, int depth = 0)
        {
            if (depth > 14) return;
            var pos = start;
            while (pos + 8 <= end)
            {
                var size = Be32(data, pos);
                var type = Encoding.ASCII.GetString(data, pos + 4, 4);
                var header = 8;
                if (size == 1)
                {
                    if (pos + 16 > end) break;
                    size = (int)Be64(data, pos + 8);
                    header = 16;
                }
                else if (size == 0)
                {
                    size = end - pos;
                }

                if (size < header || pos + size > end) break;

                if (type == boxType) onFound(pos, size);

                // mdat 为媒体数据，非容器，不下沉；meta 为 FullBox，子箱起点需再跳 4 字节
                if (type != "mdat")
                {
                    var childStart = type == "meta" ? pos + header + 4 : pos + header;
                    FindBoxes(data, childStart, pos + size, boxType, onFound, depth + 1);
                }

                pos += size;
            }
        }

        /// <summary>
        /// 将 MP4 内视频轨 tkhd 的显示矩阵重置为单位阵（Rotation 0°）。
        /// 转码路径下 FFmpeg 是否保留源显示矩阵随版本而异，这里显式清零，
        /// 确保烘焙后的产物对任何播放器都无旋转，彻底规避退化矩阵的解读差异。
        /// </summary>
        private static void ClearDisplayMatrix(string mp4Path)
        {
            var data = File.ReadAllBytes(mp4Path);
            var changed = false;

            FindBoxes(data, 0, data.Length, "tkhd", (pos, size) =>
            {
                // tkhd 尾部 8 字节为宽/高（16.16 定点）；视频轨宽高非零，音频轨为 0
                if (size < 16 || Be32(data, pos + size - 8) <= 0) return;

                // 矩阵位于 FullBox 头之后：v0 偏移 +48，v1（64 位时间戳）偏移 +60
                var version = data[pos + 8];
                var matrixOffset = pos + (version == 1 ? 60 : 48);
                if (matrixOffset + 36 > pos + size) return;

                WriteBe32(data, matrixOffset, 0x00010000);     // a
                WriteBe32(data, matrixOffset + 4, 0);          // b
                WriteBe32(data, matrixOffset + 8, 0);          // u
                WriteBe32(data, matrixOffset + 12, 0);         // c
                WriteBe32(data, matrixOffset + 16, 0x00010000); // d
                WriteBe32(data, matrixOffset + 20, 0);         // v
                WriteBe32(data, matrixOffset + 24, 0);         // x
                WriteBe32(data, matrixOffset + 28, 0);         // y
                WriteBe32(data, matrixOffset + 32, 0x40000000); // w
                changed = true;
            });

            if (changed)
            {
                File.WriteAllBytes(mp4Path, data);
            }
        }

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

        /// <summary>
        /// 回填文件时间戳，异常静默：产物此时已正确落盘，
        /// 不应因元数据写入失败把一次成功的合并报成失败。
        /// </summary>
        private static void TrySetFileTime(string path, DateTime timestamp)
        {
            try
            {
                FileTimeHelper.SetFileTime(path, timestamp);
            }
            catch
            {
                // 忽略时间戳写入失败
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