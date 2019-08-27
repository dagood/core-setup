// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Build.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Microsoft.DotNet.Build.Tasks
{
    public class RegenerateThirdPartyNotices : BuildTask
    {
        private const string GitHubRawContentBaseUrl = "https://raw.githubusercontent.com/";

        /// <summary>
        /// The Third Party Notices file (TPN file) to regenerate.
        /// </summary>
        [Required]
        public string File { get; set; }

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
            var file = TpnFile.Parse(System.IO.File.ReadAllLines(File));

            Log.LogMessage(MessageImportance.High, file.Preamble);

            foreach (var s in file.Sections)
            {
                Log.LogMessage(MessageImportance.High, "");
                Log.LogMessage(MessageImportance.High, s.Header.Name);
                Log.LogMessage(MessageImportance.High, "---");
                Log.LogMessage(MessageImportance.High, s.Content);
                Log.LogMessage(MessageImportance.High, "---");
            }

            var results = await Task.WhenAll(TpnRepos
                .SelectMany(r =>
                {
                    string repo = r.ItemSpec;
                    string branch = r.GetMetadata("Branch")
                        ?? throw new ArgumentException($"{r.ItemSpec} specifies no Branch.");

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
                    string content = null;

                    HttpResponseMessage response = await client.GetAsync(c.Url);

                    if (response.StatusCode != HttpStatusCode.NotFound)
                    {
                        response.EnsureSuccessStatusCode();

                        content = await response.Content.ReadAsStringAsync();
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
                        Content = content
                    };
                }));

            foreach (var r in results.Where(r => r.Content != null).OrderBy(r => r.Repo))
            {
                Log.LogMessage(
                    MessageImportance.High,
                    $"Found TPN: {r.Repo} {r.Branch} - {r.PotentialPath}");
            }
        }

        private class TpnFile
        {

            public static TpnFile Parse(string[] lines)
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

                sections.Last().Content = string.Join(
                    Environment.NewLine,
                    lines.Skip(lastHeaderLine));

                return new TpnFile
                {
                    Preamble = preamble,
                    Sections = sections
                };
            }

            public string Preamble { get; set; }

            public IEnumerable<Section> Sections { get; set; }
        }

        private class SectionHeader
        {
            /// <summary>
            /// Find the index of the section after the start index. A section is either:
            /// 
            /// 1. A line of more than two '-' characters preceded immediately by a section name.
            ///    This is an "underlined" header.
            /// 2. A line of more than two '-' characters with blank lines above and below it and a
            ///    section name two lines below it.
            /// </summary>
            public static SectionHeader ParseNext(string[] lines, int startLine = 0)
            {
                for (int i = startLine; i < lines.Length; i++)
                {
                    if (i > 0 &&
                        lines[i].Length > 2 &&
                        lines[i].All(c => c == '-'))
                    {
                        // Type 1.
                        string lineAbove = lines[i - 1];
                        if (!string.IsNullOrEmpty(lineAbove))
                        {
                            return new SectionHeader
                            {
                                Name = lineAbove,
                                Underlined = true,
                                StartLine = i - 1,
                                LineLength = 3
                            };
                        }

                        // Type 2.
                        if (i + 2 < lines.Length &&
                            string.IsNullOrEmpty(lines[i + 1]))
                        {
                            return new SectionHeader
                            {
                                Name = lines[i + 2],
                                Underlined = false,
                                StartLine = i - 1,
                                LineLength = 5
                            };
                        }
                    }
                }

                return null;
            }

            public string Name { get; set; }

            public bool Underlined { get; set; }

            public int StartLine { get; set; }
            public int LineLength { get; set; }
        }

        private class Section
        {
            public SectionHeader Header { get; set; }
            public string Content { get; set; }
        }
    }
}
