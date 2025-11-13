using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MinecraftLauncherConsole.Models
{
    public class Configuration
    {
        public string DownloadPage { get; set; }
        public List<string> PreserveFiles { get; set; }
        public string TargetDir { get; set; }
        public string WorldName { get; set; }
        public bool Preview { get; set; }
    }
}
