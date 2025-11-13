using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Policy;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MinecraftLauncherConsole.Models
{
    public class MinecraftLauncher
    {
        #region PRIVATE_MEMBERS
        private List<Process> _ServerList { get; set; }
        private readonly Regex _VersionRegex = new Regex(@"[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+");
        // https://www.minecraft.net/bedrockdedicatedserver/bin-linux/bedrock-server-1.21.95.1.zip
        private readonly Regex _LatestDownloadableURLRegex = new Regex(@"https://www\.minecraft\.net/bedrockdedicatedserver/bin-(?'platform'\w+-?\w+?)?/(?'file_name'bedrock-server-[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+.zip)");
        private readonly HttpClient _WebClient;
        private readonly string _Platform;
        private Process _Server { get; set; }
        private List<string> _ValidCommands = new List<string>()
        {
            @"kick (?<playerNameOrXuid>\w+)\s(?<reason>\w+)?",
            @"stop",
            @"save (hold|resume|query)",
            @"allowlist (on|off|list|reload)",
            @"allowlist (add|remove) (?<playerNameOrXuid>\w+)",
            @"permission (list|reload)",
            @"op (?<playerNameOrXuid>\w+)",
            @"deop (?<playerNameOrXuid>\w+)",
            @"changesetting (?<setting>\w+) (?<value>\w+)",
            @"save hold",
            @"save query",
            @"save resume",
        };
        #endregion PRIVATE_MEMBERS

        #region PUBLIC_MEMBERS
        public Configuration Config { get; private set; }
        public bool Running { get => _Server.HasExited == false; }
        #endregion PUBLIC_MEMBERS

        #region PUBLIC_METHODS
        public MinecraftLauncher(Configuration config)
        {
            Config = config;
            HttpClientHandler handler = new HttpClientHandler()
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            _WebClient = new HttpClient(handler);
            _WebClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/apng,*/*;q=0.8");
            _WebClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            _WebClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
            _WebClient.DefaultRequestHeaders.Add("Accept-Language", "en-GB,en;q=0.9,en-US;q=0.8");
            _WebClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
            _WebClient.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
            _WebClient.DefaultRequestHeaders.Add("Pragma", "no-cache");
            _WebClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko; Google Page Speed Insights) Chrome/27.0.1453 Safari/537.36");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _Platform = (Config.Preview ? "Preview" : "") + "Windows";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _Platform = (Config.Preview ? "Preview" : "") + "Linux";
            }
        }

        async public Task<Tuple<Version, string>> CheckAvailableVersion()
        {
            var versionJson = await GetUrlString(Config.DownloadPage);
            var linksJson = ((JObject)JsonConvert.DeserializeObject(versionJson))["result"]["links"];
            var downloadableUrls = ((JArray)linksJson)
                .ToObject<List<Dictionary<string, string>>>()
                .ToDictionary(
                    o => o["downloadType"].Replace("serverBedrock", ""),
                    o => o["downloadUrl"]
                );
            var latestURL = downloadableUrls[_Platform];
            return new Tuple<Version, string>(new Version(_VersionRegex.Match(latestURL).Value), latestURL);
        }
        public async Task<ZipArchive> DownloadZipFile(string url)
        {
            return new ZipArchive(await GetUrlStream(url));
        }

        public async Task Update()
        {
            var versionAndUrl = await CheckAvailableVersion();
            var latestUrl = versionAndUrl.Item2;
            var latestInstallPath = Path.Combine(Config.TargetDir, Config.WorldName);

            if (!Directory.Exists(latestInstallPath))
            {
                Directory.CreateDirectory(latestInstallPath);
            }
            var serverPackage = await DownloadZipFile(versionAndUrl.Item2);
            var baseNames = new HashSet<string>();
            foreach (var entry in serverPackage.Entries)
            {
                var destPath = Path.Combine(latestInstallPath, entry.FullName);
                if (!Config.PreserveFiles.Contains(entry.FullName) || !Directory.Exists(destPath))
                {
                    if (entry.LastWriteTime > Directory.GetLastWriteTimeUtc(destPath))
                    {
                        var pathRoot = GetRootFolder(entry.FullName);
                        if (baseNames.Contains(pathRoot) == false)
                        {
                            Console.WriteLine($"Overwriting {pathRoot}");
                        }
                        baseNames.Add(pathRoot);
                        if (destPath.EndsWith("/"))
                        {
                            Directory.CreateDirectory(destPath);
                        }
                        else
                        {
                            entry.ExtractToFile(destPath, true);
                        }
                    }
                }
            }
        }

        public Version CheckProcessVersion(Process serverProcess)
        {
            string line;
            Version serverVersion = null;
            while ((line = serverProcess.StandardOutput.ReadLine()) != null)
            {
                var versionMatch = _VersionRegex.Match(line);
                if (versionMatch.Success)
                {
                    serverVersion = new Version(versionMatch.Value);
                }
            }
            return serverVersion;
        }

        public void Start()
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.UseShellExecute = false;
            startInfo.Arguments = String.Empty;
            startInfo.FileName = Path.Combine(Config.TargetDir, Config.WorldName, "bedrock_server.exe");
            _Server = new Process();
            _Server.StartInfo = startInfo;
            _Server.Start();
            _Server.OutputDataReceived += (sender, args) => Console.WriteLine(args.Data);
            _Server.BeginOutputReadLine();
        }

        public void Stop()
        {
            // Send Ctrl+C
            _Server.StandardInput.WriteLine("stop");
        }

        public int Join()
        {
            _Server.WaitForExit(1000);
            _Server.Kill();
            _Server.CancelOutputRead();
            return _Server.ExitCode;
        }

        public bool CheckCommand(string command)
        {
            bool valid = false;
            foreach (var pattern in _ValidCommands)
            {
                var match = Regex.Match(command, pattern);
                if (match.Success)
                {
                    valid = true;
                    break;
                }
            }
            return valid;
        }

        public void SendCommand(string command)
        {
            if (_Server.HasExited == false)
            {
                if (CheckCommand(command))
                {
                    _Server.StandardInput.WriteLine(command);
                }
                else
                {
                    Console.WriteLine($"Invalid command: '{command}'");
                }
            }
            else
            {
                Console.WriteLine("Server already exited");
            }
        }
        #endregion PUBLIC_METHODS

        #region PRIVATE_METHODS
        private async Task<string> GetUrlString(string url)
        {
            return await _WebClient.GetStringAsync(url);
        }
        private async Task<Stream> GetUrlStream(string url)
        {
            return await _WebClient.GetStreamAsync(url);
        }

        private string GetRootFolder(string path)
        {
            while (true)
            {
                string temp = Path.GetDirectoryName(path);
                if (String.IsNullOrEmpty(temp))
                    break;
                path = temp;
            }
            return path;
        }
        #endregion PRIVATE_METHODS
    }
}
