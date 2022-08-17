using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace CeeFind
{
    internal class Metrics
    {
        public int FileCount { get; set; }
        public int DirectoryCount { get; set; }
        public int FileMatchCount { get; set; }
        public int FileMatchInsideCount { get; set; }
        public int MatchRowCount { get; set; }
        public TimeSpan Duration { get; set; }
        public long TotalBytesScanned { get; set; }
        public Dictionary<string, long> ScanSizeByExtension { get; set; }
        public Dictionary<string, long> ExcludedBinaries { get; set; }
        public DateTime SearchDate { get; set; }
        public SearchSettings Settings { get; }
        public bool IsComplete { get; set; }
        public string Args { get; set; }

        // summative calculated metrics
        [JsonIgnore]
        public bool IsFileNameSearchWithHumanReadableResults { get { return !Settings.SearchInFiles && FileMatchCount < 1000; } }
        [JsonIgnore]
        public bool IsFileSearchWithHumanReadableResults { get { return Settings.SearchInFiles && FileMatchInsideCount < 1000; } }
        [JsonIgnore]
        private const double PERCENT_READ_REQUIRED_DEEP_SCAN = 0.05; // 5%

        [JsonIgnore]
        public bool IsProbableDeepScan
        {
            get {
                return Settings.SearchInFiles && (double)FileMatchCount / FileCount > PERCENT_READ_REQUIRED_DEEP_SCAN;
            }
        }
        [JsonIgnore]
        public double OverallEfficiency { get; internal set; }

        public Metrics(SearchSettings settings, string args)
        {
            ScanSizeByExtension = new Dictionary<string, long>();
            ExcludedBinaries = new Dictionary<string, long>();
            SearchDate = DateTime.UtcNow;
            Settings = settings;
            Args = args;
        }

        internal void Clean()
        {
            ExcludedBinaries = null;
            ScanSizeByExtension = Top5(ScanSizeByExtension).ToDictionary(k => k.Key, v => v.Value);
        }

        internal IEnumerable<KeyValuePair<string, long>> Top5(Dictionary<string, long> dict)
        {
            return dict.OrderByDescending(ext => ext.Value).Take(5);
        }
    }
}
