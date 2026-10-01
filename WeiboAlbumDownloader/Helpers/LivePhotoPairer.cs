using System;
using System.Collections.Generic;
using WeiboAlbumDownloader.Models;

namespace WeiboAlbumDownloader.Helpers
{
    /// <summary>
    /// 把 pics 的逐图结构与 Mblog.LivePhoto 配对成"封面 + 实况视频"媒体队列。
    /// 微博对"这张图的实况视频在哪"存在多套并行表达（pics[].videoSrc、live_photo[]、pic_video），
    /// 各接口回传的字段与数量并不总是对齐，因此这里不把"两个列表数量相等"当作前提，
    /// 配对优先级为：URL 身份 &gt; 顺序兜底 &gt; 图片自带的 videoSrc &gt; 无源。
    /// </summary>
    public static class LivePhotoPairer
    {
        /// <summary>封面原图直链模板，与下载链路其它位置保持一致。</summary>
        private const string JpgUrlTemplate = "https://wx4.sinaimg.cn/large/{0}.jpg";

        /// <summary>一条媒体项：封面原图直链 + 实况视频直链（非实况图或取不到源时为 null）。</summary>
        public sealed record MediaItem(string JpgUrl, string? MovUrl);

        /// <summary>
        /// 执行配对。<paramref name="unpairedLivePids"/> 返回"被标记为实况却拿不到任何视频源"的 pid，
        /// 由调用方决定如何提示；其余实况图均能拿到可下载的视频直链。
        /// </summary>
        /// <param name="pics">博文的逐图结构，顺序即发布顺序，决定 _1/_2 编号。</param>
        /// <param name="livePhotos">Mblog.LivePhoto，可为 null 或含空串。</param>
        /// <param name="unpairedLivePids">输出：确实没有可用视频源的实况图 pid。</param>
        public static List<MediaItem> Build(
            IReadOnlyList<Pic> pics,
            IReadOnlyList<string>? livePhotos,
            out List<string> unpairedLivePids)
        {
            var pool = new List<string>();
            if (livePhotos != null)
            {
                foreach (var url in livePhotos)
                {
                    if (!string.IsNullOrEmpty(url)) pool.Add(url);
                }
            }
            var used = new bool[pool.Count];

            //第一遍：真实况先按 URL 身份认领自己的条目，避免后面的"伪实况"按顺序把它偷走而错配
            var claimed = new int[pics.Count];
            for (int i = 0; i < pics.Count; i++)
            {
                claimed[i] = -1;
                var pic = pics[i];
                if (!IsLivePic(pic) || string.IsNullOrEmpty(pic?.VideoSrc)) continue;

                for (int j = 0; j < pool.Count; j++)
                {
                    if (used[j] || !string.Equals(pool[j], pic!.VideoSrc, StringComparison.Ordinal)) continue;
                    used[j] = true;
                    claimed[i] = j;
                    break;
                }
            }

            //第二遍：逐图取源并生成媒体项
            var result = new List<MediaItem>(pics.Count);
            var unpaired = new List<string>();
            for (int i = 0; i < pics.Count; i++)
            {
                var pic = pics[i];
                if (string.IsNullOrEmpty(pic?.Pid)) continue;   //无 pid 无法拼封面直链，也不消费实况源

                string? movUrl = null;
                if (IsLivePic(pic))
                {
                    if (claimed[i] >= 0)
                    {
                        //① 身份命中：与改造前取自 LivePhoto 的取值完全一致
                        movUrl = pool[claimed[i]];
                    }
                    else
                    {
                        //② 顺序兜底：保持改造前语义，live_photo 有货时仍优先用它
                        movUrl = TakeNextUnused(pool, used);
                    }

                    //③ 新增兜底：live_photo 供不上时，直接用图片自带的包装链接。
                    //   该链接形如 https://video.weibo.com/media/play?livephoto=<urlencoded .mov>，
                    //   会 302 到带签名的地址；一旦解码成裸链接即 403，故必须原样使用。
                    if (movUrl is null && !string.IsNullOrEmpty(pic!.VideoSrc))
                    {
                        movUrl = pic.VideoSrc;
                    }

                    if (movUrl is null)
                    {
                        unpaired.Add(pic!.Pid!);
                    }
                }

                result.Add(new MediaItem(string.Format(JpgUrlTemplate, pic!.Pid), movUrl));
            }

            unpairedLivePids = unpaired;
            return result;
        }

        /// <summary>判断 pics 元素是否为实况图：带实况视频链接，或带 livephoto 类型标记。</summary>
        private static bool IsLivePic(Pic? pic)
        {
            return !string.IsNullOrEmpty(pic?.VideoSrc)
                || string.Equals(pic?.Type, "livephoto", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>取出池中第一个未被消费的条目并标记为已用；取不到返回 null。</summary>
        private static string? TakeNextUnused(List<string> pool, bool[] used)
        {
            for (int i = 0; i < pool.Count; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                return pool[i];
            }
            return null;
        }
    }
}
