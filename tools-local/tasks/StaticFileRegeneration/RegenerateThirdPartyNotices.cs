// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Build.Framework;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Microsoft.DotNet.Build.Tasks
{
    public class RegenerateThirdPartyNotices : BuildTask
    {
        private const string GitHubRawContentBaseUrl = "https://raw.githubusercontent.com/";

        private static readonly char[] NewlineChars = { '\n', '\r' };
        private static readonly char[] SectionSeparatorChars = { '-', '=' };

        /// <summary>
        /// The Third Party Notices file (TPN file) to regenerate.
        /// </summary>
        [Required]
        public string TpnFile { get; set; }

        /// <summary>
        /// Potential names for the file in various repositories. Each one is tried for each repo.
        /// </summary>
        [Required]
        public string[] PotentialTpnPaths { get; set; }

        /// <summary>
        /// %(Identity): The "{organization}/{name}" of a repo to gather TPN info from.
        /// %(Branch): The branch to pull from.
        /// </summary>
        [Required]
        public ITaskItem[] TpnRepos { get; set; }

        public override bool Execute()
        {
            using (var client = new HttpClient())
            {
                ExecuteAsync(client).Wait();
            }

            return !Log.HasLoggedErrors;
        }

        public async Task ExecuteAsync(HttpClient client)
        {
            var results = await Task.WhenAll(TpnRepos
                .SelectMany(item =>
                {
                    string repo = item.ItemSpec;
                    string branch = item.GetMetadata("Branch")
                        ?? throw new ArgumentException($"{item.ItemSpec} specifies no Branch.");

                    return PotentialTpnPaths.Select(path => new
                    {
                        Repo = repo,
                        Branch = branch,
                        PotentialPath = path,
                        Url = $"{GitHubRawContentBaseUrl}{repo}/{branch}/{path}"
                    });
                })
                .Select(async c =>
                {
                    TpnDocument content = null;

                    Log.LogMessage(
                        MessageImportance.High,
                        $"Getting {c.Url}");

                    HttpResponseMessage response = await client.GetAsync(c.Url);

                    if (response.StatusCode != HttpStatusCode.NotFound)
                    {
                        response.EnsureSuccessStatusCode();

                        string tpnContent = await response.Content.ReadAsStringAsync();

                        try
                        {
                            content = TpnDocument.Parse(tpnContent.Split(NewlineChars));
                        }
                        catch
                        {
                            Log.LogError($"Failed to parse response from {c.Url}");
                            throw;
                        }

                        Log.LogMessage($"Got content from URL: {c.Url}");
                    }
                    else
                    {
                        Log.LogMessage($"Checked for content, but does not exist: {c.Url}");
                    }

                    return new
                    {
                        c.Repo,
                        c.Branch,
                        c.PotentialPath,
                        c.Url,
                        Content = content
                    };
                }));

            foreach (var r in results.Where(r => r.Content != null).OrderBy(r => r.Repo))
            {
                Log.LogMessage(
                    MessageImportance.High,
                    $"Found TPN: {r.Repo} [{r.Branch}] {r.PotentialPath}");
            }

            // Ensure we found one (and only one) TPN file for each repo.
            foreach (var miscount in results
                .GroupBy(r => r.Repo)
                .Where(g => g.Count(r => r.Content != null) != 1))
            {
                Log.LogError($"Unable to find exactly one TPN for {miscount.Key}");
            }

            if (Log.HasLoggedErrors)
            {
                return;
            }

            TpnDocument existingTpn = TpnDocument.Parse(File.ReadAllLines(TpnFile));

            Log.LogMessage(
                MessageImportance.High,
                $"Existing TPN file preamble: {existingTpn.Preamble.Substring(0, 10)}...");

            foreach (var s in existingTpn.Sections.OrderBy(s => s.Header.Name))
            {
                Log.LogMessage(
                    MessageImportance.High,
                    $"- '{s.Header.Name}': {s.Content.Substring(0, 10)}...");
            }

            TpnDocument[] otherTpns = results
                .Select(r => r.Content)
                .Where(r => r != null)
                .ToArray();

            Section[] newSections = otherTpns
                .SelectMany(o => o.Sections)
                .Except(existingTpn.Sections, new Section.ByHeaderNameComparer())
                .OrderBy(s => s.Header.Name)
                .ToArray();

            foreach (Section existing in results
                .SelectMany(r => (r.Content?.Sections.Except(newSections)).NullAsEmpty())
                .Where(s => !newSections.Contains(s))
                .OrderBy(s => s.Header.Name))
            {
                Log.LogMessage(
                    MessageImportance.High,
                    $"Found already-imported section: '{existing.Header.Name}'");
            }

            foreach (var s in newSections)
            {
                Log.LogMessage(
                    MessageImportance.High,
                    $"New section to import: '{s.Header.Name}' of " +
                    string.Join(
                        ", ",
                        results
                            .Where(r => r.Content?.Sections.Contains(s) == true)
                            .Select(r => r.Url)) +
                    $" line {s.Header.StartLine}");
            }

            Log.LogMessage(MessageImportance.High, $"Importing {newSections.Length} sections...");

            existingTpn.Sections = existingTpn.Sections.Concat(newSections);


        }

        private class TpnDocument
        {
            public static TpnDocument Parse(string[] lines)
            {
                SectionHeader header;
                int lastHeaderLine = 0;

                string preamble = null;
                List<Section> sections = new List<Section>();

                while ((header = SectionHeader.ParseNext(lines, lastHeaderLine)) != null)
                {
                    string lastContent = string.Join(
                        Environment.NewLine,
                        lines.Skip(lastHeaderLine).Take(header.StartLine - lastHeaderLine - 1));

                    lastHeaderLine = header.StartLine + header.LineLength;

                    if (preamble == null)
                    {
                        preamble = lastContent;
                    }
                    else
                    {
                        sections.Last().Content = lastContent;
                    }

                    sections.Add(new Section
                    {
                        Header = header
                    });
                }

                if (sections.Count == 0)
                {
                    throw new ArgumentException($"No sections found.");
                }

                sections.Last().Content = string.Join(
                    Environment.NewLine,
                    lines.Skip(lastHeaderLine));

                return new TpnDocument
                {
                    Preamble = preamble,
                    Sections = sections
                };
            }

            public string Preamble { get; set; }

            public IEnumerable<Section> Sections { get; set; }

            public override string ToString() =>
                Preamble + Environment.NewLine + string.Join(Environment.NewLine, Sections);
        }

        private enum SectionHeaderFormat
        {
            /// <summary>
            /// {blank line}
            /// {name}
            /// {3+ section separator chars}
            /// {blank line}
            /// </summary>
            Underlined,

            /// <summary>
            /// {blank line}
            /// {3+ section separator chars}
            /// {blank line}
            /// {name}
            /// </summary>
            Separated,

            /// <summary>
            /// {blank line}
            /// {number}.{tab}{name}
            /// {blank line}
            /// </summary>
            Numbered
        }

        private class SectionHeader
        {
            private static readonly Regex NumberListPrefix = new Regex(@"^[0-9]+\.\t(?<name>.*)$");

            public static SectionHeader ParseNext(string[] lines, int startLine = 0)
            {
                for (int i = startLine; i < lines.Length; i++)
                {
                    if (i == 0)
                    {
                        continue;
                    }

                    string lineAbove = lines[i - 1];

                    if (lines[i].Length > 2 && lines[i].All(c => SectionSeparatorChars.Contains(c)))
                    {
                        if (!string.IsNullOrEmpty(lineAbove))
                        {
                            return new SectionHeader
                            {
                                Name = lineAbove,
                                SeparatorLine = lines[i],
                                Format = SectionHeaderFormat.Underlined,
                                StartLine = i - 1,
                                LineLength = 3
                            };
                        }

                        if (i + 2 < lines.Length &&
                            string.IsNullOrEmpty(lines[i + 1]))
                        {
                            return new SectionHeader
                            {
                                Name = lines[i + 2],
                                SeparatorLine = lines[i],
                                Format = SectionHeaderFormat.Separated,
                                StartLine = i - 1,
                                LineLength = 5
                            };
                        }
                    }

                    var numberListMatch = NumberListPrefix.Match(lines[i]);
                    if (string.IsNullOrEmpty(lineAbove) &&
                        string.IsNullOrEmpty(lines[i + 1]) &&
                        numberListMatch.Success)
                    {
                        return new SectionHeader
                        {
                            Name = numberListMatch.Groups["name"].Value,
                            SeparatorLine = lines[i],
                            Format = SectionHeaderFormat.Numbered,
                            StartLine = i - 1,
                            LineLength = 3
                        };
                    }
                }

                return null;
            }

            public string Name { get; set; }
            public string SeparatorLine { get; set; }

            public SectionHeaderFormat Format { get; set; }

            public int StartLine { get; set; }
            public int LineLength { get; set; }

            public override string ToString()
            {
                switch(Format)
                {
                    case SectionHeaderFormat.Underlined:
                        return Name + Environment.NewLine + SeparatorLine;

                    case SectionHeaderFormat.Separated:
                        return SeparatorLine + Environment.NewLine + SeparatorLine;

                    case SectionHeaderFormat.Numbered:
                        return SeparatorLine + Environment.NewLine;

                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        private class Section
        {
            public class ByHeaderNameComparer : EqualityComparer<Section>
            {
                public override bool Equals(Section x, Section y) =>
                    string.Equals(x.Header.Name, y.Header.Name, StringComparison.OrdinalIgnoreCase);

                public override int GetHashCode(Section obj) => obj.Header.Name.GetHashCode();
            }

            public SectionHeader Header { get; set; }
            public string Content { get; set; }

            public override string ToString() => Header + Environment.NewLine + Content;
        }
    }
}
