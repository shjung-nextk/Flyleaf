﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;

using Newtonsoft.Json;

using FlyleafLib.MediaFramework.MediaInput;
using static FlyleafLib.Plugins.YoutubeDLJson;

namespace FlyleafLib.Plugins
{
    public class YoutubeDL : PluginBase, IOpen, IProvideAudio, IProvideVideo, IProvideSubtitles, ISuggestAudioInput, ISuggestVideoInput, ISuggestSubtitlesInput
    {
        YoutubeDLJson ytdl;

        public static string plugin_path = "Plugins\\YoutubeDL\\yt-dlp.exe";

        static JsonSerializerSettings settings = new JsonSerializerSettings();

        public List<AudioInput>     AudioInputs     { get; set; } = new List<AudioInput>();
        public List<VideoInput>     VideoInputs     { get; set; } = new List<VideoInput>();
        public List<SubtitlesInput> SubtitlesInputs { get; set; } = new List<SubtitlesInput>();

        public bool IsPlaylist => false;

        static YoutubeDL() { settings.NullValueHandling = NullValueHandling.Ignore; }

        public override void OnInitialized()
        {
            AudioInputs.Clear();
            VideoInputs.Clear();
            SubtitlesInputs.Clear();
            ytdl = null;
        }

        public override OpenResults OnOpenVideo(VideoInput input)
        {
            if (input.Plugin == null || input.Plugin.Name != Name) return null;

            Format fmt = (Format) input.Tag;

            bool gotReferer = false;
            Config.Demuxer.FormatOpt["headers"] = "";
            foreach (var hdr in fmt.http_headers)
            {
                if (hdr.Key.ToLower() == "referer")
                {
                    gotReferer = true;
                    Config.Demuxer.FormatOpt["referer"] = hdr.Value;
                }
                else if (hdr.Key.ToLower() != "user-agent")
                    Config.Demuxer.FormatOpt["headers"] += hdr.Key + ": " + hdr.Value + "\r\n";
            }

            if (!gotReferer)
                Config.Demuxer.FormatOpt["referer"] = Handler.UserInputUrl;

            return new OpenResults();
        }
        public override OpenResults OnOpenAudio(AudioInput input)
        {
            if (input.Plugin == null || input.Plugin.Name != Name) return null;

            Format fmt = (Format) input.Tag;

            var curFormatOpt = decoder.VideoStream == null ? Config.Demuxer.FormatOpt : Config.Demuxer.AudioFormatOpt;

            bool gotReferer = false;
            curFormatOpt["headers"] = "";
            foreach (var hdr in fmt.http_headers)
            {
                if (hdr.Key.ToLower() == "referer")
                {
                    gotReferer = true;
                    curFormatOpt["referer"] = hdr.Value;
                }
                else if (hdr.Key.ToLower() != "user-agent")
                    curFormatOpt["headers"] += hdr.Key + ": " + hdr.Value + "\r\n";
            }

            if (!gotReferer)
                curFormatOpt["referer"] = Handler.UserInputUrl;

            return new OpenResults();
        }

        public override OpenResults OnOpenSubtitles(SubtitlesInput input)
        {
            if (input.Plugin == null || input.Plugin.Name != Name) return null;

            var curFormatOpt = Config.Demuxer.SubtitlesFormatOpt;
            curFormatOpt["referer"] = Handler.UserInputUrl;

            return new OpenResults();
        }

        public bool IsValidInput(string url)
        {
            try
            {
                Uri uri = new Uri(url);
                if ((uri.Scheme.ToLower() != "http" && uri.Scheme.ToLower() != "https") || Utils.GetUrlExtention(uri.AbsolutePath).ToLower() == "m3u8") return false;
            } catch (Exception) { return false; }

            return true;
        }

        public OpenResults Open(Stream iostream)
        {
            return null;
        }

        public OpenResults Open(string url)
        {
            try
            {
                Uri uri = new Uri(url);

                if (Regex.IsMatch(uri.DnsSafeHost, @"\.youtube\.", RegexOptions.IgnoreCase))
                {
                    var query = HttpUtility.ParseQueryString(uri.Query);
                    url = uri.Scheme + "://" + uri.Host + uri.AbsolutePath + "?v=" + query["v"];
                }

                string tmpFile = Path.GetTempPath() + Guid.NewGuid().ToString();

                Process proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName        = plugin_path,
                        Arguments       = $"--no-check-certificate --skip-download --youtube-skip-dash-manifest --write-info-json -o \"{tmpFile}\" \"{url}\"",
                        CreateNoWindow  = true,
                        UseShellExecute = false,
                        WindowStyle     = ProcessWindowStyle.Hidden
                    }
                };

                proc.Start();

                while (!proc.HasExited && !Handler.Interrupt)
                    Thread.Sleep(35);

                if (Handler.Interrupt)
                {
                    if (!proc.HasExited) proc.Kill();
                    return null;
                }

                if (!File.Exists($"{tmpFile}.info.json"))
                    return null;

                // Parse Json Object
                string json = File.ReadAllText($"{tmpFile}.info.json");
                ytdl = JsonConvert.DeserializeObject<YoutubeDLJson>(json, settings);
                if (ytdl == null) return null;

                Format fmt;
                InputData inputData = new InputData()
                {
                    Folder  = Path.GetTempPath(),
                    Title   = ytdl.title
                };

                // If no formats still could have a single format attched to the main root class
                if (ytdl.formats == null)
                {
                    ytdl.formats = new List<Format>();
                    ytdl.formats.Add(ytdl);
                }

                // Fix Nulls (we are not sure if they have audio/video)
                for (int i = 0; i < ytdl.formats.Count; i++)
                {
                    fmt = ytdl.formats[i];
                    if (ytdl.formats[i].vcodec == null) ytdl.formats[i].vcodec = "";
                    if (ytdl.formats[i].acodec == null) ytdl.formats[i].acodec = "";

                    if (HasVideo(fmt))
                    {
                        VideoInputs.Add(new VideoInput()
                        {
                            InputData   = inputData,
                            Tag         = fmt,
                            Url         = fmt.url,
                            Protocol    = fmt.protocol,
                            BitRate     = (long) fmt.vbr,
                            Codec       = fmt.vcodec,
                            Language    = Language.Get(fmt.language),
                            Width       = (int) fmt.width,
                            Height      = (int) fmt.height,
                            Fps         = fmt.fps
                        });
                    }

                    if (HasAudio(fmt))
                    {
                        AudioInputs.Add(new AudioInput()
                        {
                            InputData   = inputData,
                            Tag         = fmt,
                            Url         = fmt.url,
                            Protocol    = fmt.protocol,
                            BitRate     = (long) fmt.abr,
                            Codec       = fmt.acodec,
                            Language    = Language.Get(fmt.language)
                        });
                    }
                }

                if (ytdl.automatic_captions != null)
                foreach (var subtitle1 in ytdl.automatic_captions)
                {
                    if (!Config.Subtitles.Languages.Contains(Language.Get(subtitle1.Key))) continue;

                    foreach (var subtitle in subtitle1.Value)
                    {
                        if (subtitle.ext.ToLower() != "vtt") continue;

                        SubtitlesInputs.Add(new SubtitlesInput()
                        { 
                            Downloaded  = true,
                            Converted   = true,
                            Protocol    = subtitle.ext,
                            Language    = Language.Get(subtitle.name),
                            Url         = subtitle.url
                        });
                    }
                    
                }

                if (GetBestMatch() == null && GetAudioOnly() == null) return null;
            }
            catch (Exception e) { Debug.WriteLine($"[Youtube-DL] Error ... {e.Message}"); return new OpenResults(e.Message); }

            return new OpenResults();
        }
        public VideoInput SuggestVideo()
        {
            if (Handler.OpenedPlugin == null || Handler.OpenedPlugin.Name != Name) return null;

            Format fmt = GetBestMatch();
            if (fmt == null) return null;

            foreach(var input in VideoInputs)
                if (fmt.url == input.Url) return input;

            return null;
        }
        public AudioInput SuggestAudio()
        {
            if (Handler.OpenedPlugin == null || Handler.OpenedPlugin.Name != Name) return null;

            var fmt = GetAudioOnly();
            if (fmt == null) return null;

            foreach(var input in AudioInputs)
                if (fmt.url == input.Url) return input;

            return null;
        }

        public SubtitlesInput SuggestSubtitles(Language lang)
        {
            if (Handler.OpenedPlugin == null || Handler.OpenedPlugin.Name != Name) return null;

            foreach (var subtitle in SubtitlesInputs)
                if (subtitle.Language == lang) return subtitle;

            return null;
        }

        private Format GetAudioOnly()
        {
            // Prefer best with no video (dont waste bandwidth)
            for (int i = ytdl.formats.Count - 1; i >= 0; i--)
                if (ytdl.formats[i].vcodec == "none" && ytdl.formats[i].acodec.Trim() != "" && ytdl.formats[i].acodec != "none")
                    return ytdl.formats[i];

            // Prefer audio from worst video
            for (int i = 0; i < ytdl.formats.Count; i++)
                if (ytdl.formats[i].acodec.Trim() != "" && ytdl.formats[i].acodec != "none")
                    return ytdl.formats[i];

            return null;
        }
        private Format GetBestMatch()
        {
            // TODO: Expose in settings (vCodecs Blacklist) || Create a HW decoding failed list dynamic (check also for whitelist)
            List<string> vCodecsBlacklist = new List<string>() { "vp9" };

            // Video Streams Order based on Screen Resolution
            var iresults =
                from    format in ytdl.formats
                where   format.height <= Config.Video.MaxVerticalResolution && format.vcodec != "none" && (format.protocol == null || !Regex.IsMatch(format.protocol, "dash", RegexOptions.IgnoreCase))
                orderby format.tbr      descending
                orderby format.fps      descending
                orderby format.height   descending
                orderby format.width    descending
                select  format;
            
            if (iresults == null || iresults.Count() == 0)
            {
                // Fall-back to any
                iresults =
                    from    format in ytdl.formats
                    where   format.vcodec != "none"
                    orderby format.tbr      descending
                    orderby format.fps      descending
                    orderby format.height   descending
                    orderby format.width    descending
                    select  format;

                if (iresults == null || iresults.Count() == 0) return null;
            }

            List<Format> results = iresults.ToList();

            // Best Resolution
            int bestWidth = (int)results[0].width;
            int bestHeight = (int)results[0].height;

            // Choose from the best resolution (0. with acodec and not blacklisted 1. not blacklisted 2. any)
            int priority = 0;
            while (priority < 3)
            {
                for (int i = 0; i < results.Count; i++)
                {
                    if (results[i].width != bestWidth || results[i].height != bestHeight) break;

                    if (priority == 0 && !IsBlackListed(vCodecsBlacklist, results[i].vcodec) && results[i].acodec != "none")
                        return results[i];
                    else if (priority == 1 && !IsBlackListed(vCodecsBlacklist, results[i].vcodec))
                        return results[i];
                    else if (priority == 2)
                        return results[i];
                }

                priority++;
            }

            return ytdl.formats[ytdl.formats.Count - 1]; // Fallback: Youtube-DL best match
        }
        private static bool IsBlackListed(List<string> blacklist, string codec)
        {
            foreach (string codec2 in blacklist)
                if (Regex.IsMatch(codec, codec2, RegexOptions.IgnoreCase))
                    return true;

            return false;
        }
        private static bool HasVideo(Format fmt) 
        {
            if ((fmt.height > 0 || fmt.vbr > 0 || (fmt.abr == 0 && (string.IsNullOrEmpty(fmt.acodec) || fmt.acodec != "none"))) || (!string.IsNullOrEmpty(fmt.vcodec) && fmt.vcodec != "none"))
                return true;

            return false; 
        }
        private static bool HasAudio(Format fmt)
        {
            if (fmt.abr > 0 ||  (!string.IsNullOrEmpty(fmt.acodec) && fmt.acodec != "none"))
                return true;

            return false;
        }
    }
}