using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace BilibiliVedioSpider
{
    class Program
    {

        private static HttpClient _httpClient = new HttpClient();
        private static IConfigurationRoot _config;
        private static bool _isAsync;
        private static string _cookie;
        private static string[] _unValidPathPart = new string[] { "\\","/",":","*","?","\"","<",">","|"};

        static async Task Main(string[] args)
        {
            
			Console.WriteLine("开始下载！");
            _config = new ConfigurationBuilder()
                .AddJsonFile("appsetting.json", optional: false)
                .Build();
            _isAsync = bool.Parse(_config["IsAsync"]);
            _cookie = _config["Cookie"];

            using (var streamReader = new StreamReader("vedio.txt"))
            {
                var bilibiliUrl = string.Empty;
                while ((bilibiliUrl = streamReader.ReadLine()) != null)
                {
                    if (string.IsNullOrEmpty(bilibiliUrl))
                    {
                        continue;
                    }
                    await StartProcessAsync(bilibiliUrl);
                }
            }
			Console.WriteLine("处理完成！");
			Console.ReadKey();
        }

        private static async Task StartProcessAsync(string bilibiliUrl)
        {
            var reqMsg = new HttpRequestMessage(HttpMethod.Get, bilibiliUrl);
            reqMsg.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip", 1));
            reqMsg.Headers.Add("Cookie", _cookie);
            var response = await _httpClient.SendAsync(reqMsg);
            var pageStream = await response.Content.ReadAsStreamAsync();
            var pageStr = string.Empty;
            using (GZipStream stream = new GZipStream(pageStream, CompressionMode.Decompress))
            {
                using (StreamReader reader = new StreamReader(stream))
                {
                    pageStr = reader.ReadToEnd();
                }
            }

            Regex titleRegex = new Regex("<title data-vue-meta=\"true\">(.*?)_哔哩哔哩 [(]゜-゜[)]つロ 干杯~-bilibili</title>");
            Regex pageDataRegex = new Regex("<script>window.__playinfo__=(.*?)</script>");
            var titleMatch = titleRegex.Match(pageStr);
            var pageDataMatch = pageDataRegex.Match(pageStr);
            if (titleMatch.Groups.Count==1 || pageDataMatch.Groups.Count == 1)
            {
                Console.WriteLine("不支持下载 "+ bilibiliUrl);
                return;
            }
            var title = ValidPath(titleMatch.Groups[1].Value);
            var pageDataStr = pageDataMatch.Groups[1].Value;

            Console.WriteLine($"{title}开始下载！");
            var jsonStr = pageDataStr;
            if (_isAsync)
            {
                StartProcessPage(title, jsonStr);
            }
            else
            {
                await StartProcessPageAsync(title, jsonStr);
            }
            Console.WriteLine($"{title}下载完成！");

        }

        private static string ValidPath(string value)
        {
            //windows的路径不能是:\/:*?"<>|
            foreach (var item in _unValidPathPart)
            {
                value = value.Replace(item,"");
            }
            return value;
        }

        private static void StartProcessPage(string title, string jsonStr)
        {
            var saveFileDirectory = title + ".flv";
            var url = string.Empty;
            var pagedata = JsonConvert.DeserializeObject<PageData>(jsonStr);
            Console.WriteLine($"{title} 的视频质量：{pagedata.quality}");

            var saveFileNameTemplate = saveFileDirectory.Replace(".flv", "{0}.flv");

            Task[] ts = new Task[pagedata.durl.Length];
            for (int i = 0; i < pagedata.durl.Length; i++)
            {
                var item = pagedata.durl[i];
                if (item.backup_url != null && item.backup_url.Length >= 1)
                {
                    url = item.backup_url[0];
                }
                else
                {
                    url = item.url;
                }
                var realSaveFileName = string.Format(saveFileNameTemplate, item.order);
                ts[i] = StartDownloadAsync(title, realSaveFileName, url);
            }
            Task.WaitAll(ts);
        }
        private static async Task StartProcessPageAsync(string title, string jsonStr)
        {
            var saveFileDirectory = title + ".flv";
            var url = string.Empty;
            var pagedata = JsonConvert.DeserializeObject<PageData>(jsonStr);

            var saveFileNameTemplate = saveFileDirectory.Replace(".flv", "{0}.flv");

            for (int i = 0; i < pagedata.durl.Length; i++)
            {
                var item = pagedata.durl[i];
                if (item.backup_url != null && item.backup_url.Length >= 1)
                {
                    url = item.backup_url[0];
                }
                else
                {
                    url = item.url;
                }
                var realSaveFileName = string.Format(saveFileNameTemplate, item.order);
                await StartDownloadAsync(title, realSaveFileName, url);
            }
        }

        private static async Task StartDownloadAsync(string saveFileDirectory, string realSaveFileName, string url)
        {
            var sw = new Stopwatch();
            sw.Start();

            long totalLen = 0;
            EnsureDirectory(saveFileDirectory);
            var fileName = "vedio/" + saveFileDirectory + "/" + realSaveFileName;
            if (File.Exists(fileName))
            {
                return;
            }
            using (var fs = new FileStream(fileName, FileMode.Create))
            {
                var sectionLen = 1024 * 1024;
                var from = 0;
                var to = sectionLen - 1;

                totalLen = (await DoRequestAsync(fs, from, to, url)).Value;

                var leftTotal = totalLen - sectionLen;
                from += sectionLen;
                while (leftTotal >= 0)
                {
                    to = from + sectionLen - 1;
                    await DoRequestAsync(fs, from, to, url);
                    leftTotal -= sectionLen;
                    from += sectionLen;
                }
            }
             
            sw.Stop();
            Console.WriteLine($"下载:{realSaveFileName},消耗秒数：{sw.Elapsed.TotalSeconds}");
        }


        static void EnsureDirectory(string saveFileDirectory)
        {
            if (!Directory.Exists("vedio"))
            {
                Directory.CreateDirectory("vedio");
            }
            if (!Directory.Exists("vedio/" + saveFileDirectory))
            {
                Directory.CreateDirectory("vedio/" + saveFileDirectory);
            }
        }

        private static async Task<long?> DoRequestAsync(FileStream fs, int from, int to, string url)
        {
            HttpRequestMessage msg = new HttpRequestMessage(HttpMethod.Get, url);
            msg.Headers.Add("Origin", "https://www.bilibili.com");
            msg.Headers.Add("Referer", "https://www.bilibili.com/video/av29705819");
            msg.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/67.0.3396.99 Safari/537.36");
            msg.Headers.Range = new RangeHeaderValue(from, to);


            var response = await _httpClient.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead);
            var stream = await response.Content.ReadAsStreamAsync();

            var totalLen = response.Content.Headers.ContentRange.Length;

            var buf = new byte[1024 * 1024];
            var total = response.Content.Headers.ContentLength;
            var readSum = 0;
            var readLen = 0;
            do
            {
                readLen = stream.Read(buf, 0, 1024 * 1024);
                fs.Write(buf, 0, readLen);
                readSum += readLen;
            } while (readLen != 0);

            stream.Dispose();

            return totalLen;
        }
    }

    public class PageData
    {
        public string from { get; set; }
        public string result { get; set; }
        public int quality { get; set; }
        public string format { get; set; }
        public int timelength { get; set; }
        public string accept_format { get; set; }
        public string[] accept_description { get; set; }
        public int[] accept_quality { get; set; }
        public int video_codecid { get; set; }
        public bool video_project { get; set; }
        public string seek_param { get; set; }
        public string seek_type { get; set; }
        public Durl[] durl { get; set; }
    }

    public class Durl
    {
        public int order { get; set; }
        public int length { get; set; }
        public int size { get; set; }
        public string url { get; set; }
        public string[] backup_url { get; set; }
    }
}



