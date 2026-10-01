using System;
using System.IO;

namespace WeiboAlbumDownloader.Helpers
{
    /// <summary>
    /// 文件时间戳工具：把创建/修改/访问三个时间戳统一设为同一个值。
    /// 下载阶段用发帖日期调用它；合并阶段在覆盖封面后用它把发帖日期回填回去
    /// （合并产物是新文件，<see cref="File.Move(string, string, bool)"/> 会把它"此刻"的时间戳带到目标路径）。
    /// </summary>
    public static class FileTimeHelper
    {
        /// <summary>
        /// 将文件的创建、修改、访问时间统一设为 <paramref name="timestamp"/>（本地时间）。
        /// </summary>
        /// <param name="filename">目标文件路径</param>
        /// <param name="timestamp">要写入的时间戳</param>
        /// <exception cref="IOException">文件不存在或时间戳无法写入</exception>
        public static void SetFileTime(string filename, DateTime timestamp)
        {
            File.SetCreationTime(filename, timestamp);
            File.SetLastWriteTime(filename, timestamp);
            File.SetLastAccessTime(filename, timestamp);
        }
    }
}
