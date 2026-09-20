using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace WeiboAlbumDownloader.Helpers
{
    /// <summary>
    /// FFmpeg 封装：定位并调用 ffmpeg.exe，将 iPhone 实况 MOV 重新封装为标准 MP4。
    /// 容器正确性（moov 前置、时序表、chunk 偏移、音频标准化）全部交由 FFmpeg 这个成熟复用器保证，
    /// 而非手工字节级手术。
    /// </summary>
    public static class FfmpegInvoker
    {
        /// <summary>FFmpeg 单次执行的超时上限（毫秒）</summary>
        private const int EncoderTimeoutMs = 120_000;

        /// <summary>
        /// 定位 ffmpeg.exe：优先程序所在目录（随程序分发），其次当前工作目录，最后系统 PATH
        /// </summary>
        /// <returns>ffmpeg.exe 完整路径；未找到返回 null</returns>
        public static string? LocateFfmpeg()
        {
            var baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(baseDir))
            {
                var bundled = Path.Combine(baseDir, "ffmpeg.exe");
                if (File.Exists(bundled)) return bundled;
            }

            var cwd = Path.Combine(Environment.CurrentDirectory, "ffmpeg.exe");
            if (File.Exists(cwd)) return cwd;

            try
            {
                foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    var hit = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(hit)) return hit;
                }
            }
            catch
            {
                // PATH 解析失败不阻断，返回 null 由调用方回退
            }

            return null;
        }

        /// <summary>
        /// 将源视频（.mov）remux 为标准 MP4：
        /// 视频流零转码拷贝，音频重编码为标准 AAC，<c>-movflags +faststart</c> 使 moov 前置。
        /// 若 copy 模式因源参数不兼容失败，自动回退为 H.264 转码（libx264 / yuv420p）。
        /// </summary>
        /// <param name="srcPath">源视频路径（.mov / .mp4）</param>
        /// <param name="outPath">输出 MP4 路径（若存在将被覆盖）</param>
        /// <returns>输出文件完整路径</returns>
        /// <exception cref="FileNotFoundException">未找到 ffmpeg.exe</exception>
        /// <exception cref="InvalidDataException">FFmpeg remux 失败</exception>
        public static string RemuxToMp4(string srcPath, string outPath)
        {
            var exe = LocateFfmpeg()
                ?? throw new FileNotFoundException("未找到 ffmpeg.exe（应随程序一起分发，或加入系统 PATH）。");

            // 第一优先：视频零转码 remux，仅音频重编码 AAC。
            // 不加 -movflags +faststart：保持 moov 位于文件尾（ftyp,mdat,moov），
            // 与「确定可被 Windows Photos 识别」的 QQ 参考文件布局完全一致。
            var first = new[] { "-i", srcPath, "-map", "0:v:0", "-map", "0:a:0?", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", outPath };
            if (RunAndSucceeded(exe, first) && IsNonEmpty(outPath))
            {
                return outPath;
            }

            // 回退：copy 因源参数不兼容失败 → 真正的 H.264 转码（较慢但保证产出合规 MP4）
            File.Delete(outPath);
            var fallback = new[] { "-i", srcPath, "-map", "0:v:0", "-map", "0:a:0?", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", outPath };
            var err = RunAndCaptureError(exe, fallback);
            if (err == null && IsNonEmpty(outPath))
            {
                return outPath;
            }

            File.Delete(outPath);
            throw new InvalidDataException($"FFmpeg remux 失败：{err ?? "未知错误"}。");
        }

        /// <summary>执行 ffmpeg，返回是否成功（退出码 0）</summary>
        private static bool RunAndSucceeded(string exe, IReadOnlyList<string> args)
            => RunAndCaptureError(exe, args) == null;

        /// <summary>输出文件是否存在且非空</summary>
        private static bool IsNonEmpty(string path)
            => File.Exists(path) && new FileInfo(path).Length > 0;

        /// <summary>
        /// 以安静模式运行 ffmpeg。成功（退出码 0）返回 null；失败返回 stderr 摘要文本。
        /// 标准输出/错误重定向避免弹窗，超时则终止进程树。
        /// </summary>
        private static string? RunAndCaptureError(string exe, IReadOnlyList<string> args)
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null)
            {
                return "无法启动 ffmpeg 进程。";
            }

            var err = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();

            if (!p.WaitForExit(EncoderTimeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 终止失败忽略 */ }
                return "FFmpeg 执行超时。";
            }

            return p.ExitCode == 0 ? null : err;
        }
    }
}