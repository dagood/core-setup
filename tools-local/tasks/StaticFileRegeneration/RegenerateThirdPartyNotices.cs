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
                    $"{s.Header.StartLine + 1}:{s.Header.StartLine + s.Header.LineLength} {s.Header.Format} '{s.Header.Name}'");
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

            var newTpn = new TpnDocument
            {
                Preamble = existingTpn.Preamble,
                Sections = existingTpn.Sections.Concat(newSections)
            };

            File.WriteAllText(TpnFile, newTpn.ToString());

            Log.LogMessage(MessageImportance.High, $"Wrote new TPN contents to {TpnFile}.");
        }

        private class TpnDocument
        {
            public static TpnDocument Parse(string[] lines)
            {
                var headers = SectionHeader.ParseAll(lines).ToArray();

                var sections = headers
                    .Select((h, i) =>
                    {
                        int headerEndLine = h.StartLine + h.LineLength + 1;
                        int linesUntilNext = lines.Length - headerEndLine;

                        if (i + 1 < headers.Length)
                        {
                            linesUntilNext = headers[i + 1].StartLine - headerEndLine;
                        }

                        return new Section
                        {
                            Header = h,
                            Content = string.Join(
                                Environment.NewLine,
                                lines
                                    .Skip(headerEndLine)
                                    .Take(linesUntilNext)
                                    // Trim empty line at the end of the section.
                                    .Reverse()
                                    .SkipWhile(line => string.IsNullOrWhiteSpace(line))
                                    .Reverse())
                        };
                    })
                    .ToArray();

                if (sections.Length == 0)
                {
                    throw new ArgumentException($"No sections found.");
                }

                return new TpnDocument
                {
                    Preamble = string.Join(
                        Environment.NewLine,
                        lines.Take(sections.First().Header.StartLine)),

                    Sections = sections
                };
            }

            public string Preamble { get; set; }

            public IEnumerable<Section> Sections { get; set; }

            public override string ToString() =>
                Preamble + Environment.NewLine +
                string.Join(Environment.NewLine + Environment.NewLine, Sections);
        }

        private enum SectionHeaderFormat
        {
            /// <summary>
            /// {blank line}
            /// {3+ section separator chars}
            /// {blank line}
            /// {name}
            /// </summary>
            Separated,

            /// <summary>
            /// {blank line}
            /// {name (multiline)}
            /// {3+ section separator chars}
            /// {blank line}
            /// </summary>
            Underlined,

            /// <summary>
            /// {blank line}
            /// {number}.{tab}{name}
            /// {blank line}
            /// </summary>
            Numbered
        }

        private class SectionHeader
        {
            private static readonly char[] SectionSeparatorChars = { '-', '=' };
            private static readonly Regex NumberListPrefix = new Regex(@"^[0-9]+\.\t(?<name>.*)$");

            public static IEnumerable<SectionHeader> ParseAll(string[] lines)
            {
                // A separator line can't represent a section if it's on the first or last line.
                for (int i = 1; i < lines.Length - 1; i++)
                {
                    string lineAbove = lines[i - 1].Trim();
                    string line = lines[i].Trim();
                    string lineBelow = lines[i + 1].Trim();

                    if (line.Length > 2 &&
                        IsSeparatorLine(line) &&
                        string.IsNullOrEmpty(lineBelow))
                    {
                        // 'line' is a separator line. Check around to see what kind it is.

                        if (string.IsNullOrEmpty(lineAbove))
                        {
                            string[] nameLines = lines
                                .Skip(i + 2)
                                .TakeWhile(s => !string.IsNullOrWhiteSpace(s))
                                .ToArray();

                            string name = string.Join(Environment.NewLine, nameLines);

                            // If there's a separator line as the last line in the name, this line
                            // doesn't indicate a section. Underlined handler will detect it later.
                            if (nameLines.Any(IsSeparatorLine))
                            {
                                if (nameLines.Take(nameLines.Length - 1).Any(IsSeparatorLine))
                                {
                                    throw new ArgumentException(
                                        $"Separator line detected inside name '{name}'");
                                }
                            }
                            else
                            {
                                yield return new SectionHeader
                                {
                                    Name = name,

                                    SeparatorLine = line,
                                    Format = SectionHeaderFormat.Separated,

                                    StartLine = i,
                                    LineLength = 2 + nameLines.Length
                                };
                            }
                        }
                        else
                        {
                            string[] nameLines = lines
                                .Take(i)
                                .Reverse()
                                .TakeWhile(s => !string.IsNullOrWhiteSpace(s))
                                .Reverse()
                                .ToArray();

                            int nameStartLine = i - nameLines.Length;

                            yield return new SectionHeader
                            {
                                Name = string.Join(Environment.NewLine, nameLines),

                                SeparatorLine = line,
                                Format = SectionHeaderFormat.Underlined,

                                StartLine = nameStartLine,
                                LineLength = nameLines.Length + 1
                            };
                        }
                    }

                    Match numberListMatch;

                    if (string.IsNullOrWhiteSpace(lineAbove) &&
                        string.IsNullOrWhiteSpace(lines[i + 1]) &&
                        (numberListMatch = NumberListPrefix.Match(line)).Success)
                    {
                        yield return new SectionHeader
                        {
                            Name = numberListMatch.Groups["name"].Value,

                            SeparatorLine = line,
                            Format = SectionHeaderFormat.Numbered,

                            StartLine = i,
                            LineLength = 1
                        };
                    }
                }
            }

            private static bool IsSeparatorLine(string line)
            {
                return line.All(c => SectionSeparatorChars.Contains(c));
            }

            public string Name { get; set; }
            public string SeparatorLine { get; set; }

            public SectionHeaderFormat Format { get; set; }

            public int StartLine { get; set; }
            public int LineLength { get; set; }

            public override string ToString()
            {
                switch (Format)
                {
                    case SectionHeaderFormat.Separated:
                        return
                            SeparatorLine + Environment.NewLine +
                            Environment.NewLine +
                            Name;

                    case SectionHeaderFormat.Underlined:
                        return
                            Name + Environment.NewLine +
                            SeparatorLine;

                    case SectionHeaderFormat.Numbered:
                        return SeparatorLine;

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

            public override string ToString() =>
                Header + Environment.NewLine + Environment.NewLine + Content;
        }
    }
}
