using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace CeeFind
{
    public class SearchSettings
    {
        [JsonIgnore]
        public bool IsVerbose { get; set; }
        public bool IncludeBinary { get; set; }
        public bool SearchInFiles { get; set; }
        [JsonIgnore]
        public bool IsSilent { get; set; }
        [JsonIgnore]
        public bool ShowHistory { get; set; }
        public bool ScanAllFiles { get; set; }
        [JsonIgnore]
        public bool OutputDirectoriesOnly { get; set; }
        public bool Up { get; internal set; }
        public bool First { get; set; }
        public bool SearchFilesOnly { get; set; }
        [JsonIgnore]
        public bool WriteStateAsJson { get; set; }
        [JsonIgnore]
        public bool NoRegexAssist { get; set; }
        public bool CaseSensitive { get; set; }
        [JsonIgnore]
        public bool ShowPreviousResults { get; set; }
        [JsonIgnore]
        public bool IgnoreNewLines { get; set; }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();

            foreach (string property in this.GetType().GetProperties().Select(p => p.Name + ": " + p.GetValue(this).ToString().ToLower()))
            {
                sb.Append('\t');
                sb.AppendLine(property);
            }

            return sb.ToString();
        }
    }
}
