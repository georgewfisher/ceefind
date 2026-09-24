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

        /// <summary>
        /// Written out by hand rather than by reflecting over the properties.
        ///
        /// GetProperties cannot be seen through by the trimmer, which warns that the
        /// properties it reaches may be removed - and in a published build they would be,
        /// leaving this printing nothing useful.
        /// </summary>
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();

            void Line(string name, bool value)
            {
                sb.Append('\t');
                sb.Append(name);
                sb.Append(": ");
                sb.AppendLine(value ? "true" : "false");
            }

            Line(nameof(IsVerbose), IsVerbose);
            Line(nameof(IncludeBinary), IncludeBinary);
            Line(nameof(SearchInFiles), SearchInFiles);
            Line(nameof(IsSilent), IsSilent);
            Line(nameof(ShowHistory), ShowHistory);
            Line(nameof(ScanAllFiles), ScanAllFiles);
            Line(nameof(OutputDirectoriesOnly), OutputDirectoriesOnly);
            Line(nameof(Up), Up);
            Line(nameof(First), First);
            Line(nameof(SearchFilesOnly), SearchFilesOnly);
            Line(nameof(WriteStateAsJson), WriteStateAsJson);
            Line(nameof(NoRegexAssist), NoRegexAssist);
            Line(nameof(CaseSensitive), CaseSensitive);
            Line(nameof(ShowPreviousResults), ShowPreviousResults);
            Line(nameof(IgnoreNewLines), IgnoreNewLines);

            return sb.ToString();
        }
    }
}
