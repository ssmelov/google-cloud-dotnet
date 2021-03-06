﻿// Copyright 2016 Google Inc. All Rights Reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Google.GCloud.Tools.GenerateSnippetMarkdown
{
    // Snippet format:

    // Snippet: (name of snippet) - or Sample: (name of snippet)
    // Additional: (member name to match) - 0 or more times
    // Lines of text
    // End snippet (or End sample, equivalently - just for symmetry)

    // A snippet can start with Sample: instead of snippet, in which case the ID must be a valid docfx snippet name,
    // (A-Z, a-z, 0-9, _, .) and the name isn't matched against members.
    //
    // Otherwise, it (and any "additional" member names) must match a member name within the type specified
    // implicitly by the project/source file containing the snippet:
    //   - If there's only one such member, it can be specified without any qualification, e.g. Create
    //   - Otherwise, if it can be specified by arity, use just wildcards, e.g. Create(*,*) for a two parameter overload
    //   - Otherwise, fill in enough parameters to distinguish it from other overloads, e.g. Create(string,)
    //     Precise nature of parameter matching is TBD... we'll do our best.

    // Additionally, outside a sample, a comment starting with "Resource: " indicates that the specified
    // resource should be copied into the text output as if it were a sample in the current file. The snippet ID is provided
    // as a second value, as the filename is unlikely to be a valid snippet ID. For example:
    // Resource: foo.xml sample_foo
    // creates a snippet with an ID of "sample_foo" with the content of "foo.xml".

    /// <summary>
    /// Simple program to generate markdown and text files for docfx to consume when generating documentation.
    /// The file basically links the snippets projects with the client libraries.
    /// </summary>
    public sealed class Program
    {
        private const string Resource = "// Resource: ";
        private const string StartSample = "// Sample: ";
        private const string StartSnippet = "// Snippet: ";
        private const string AdditionalMember = "// Additional: ";
        private const string EndSnippet = "// End snippet";
        private const string EndSample = "// End sample";
        private static readonly Regex DocfxSnippetPattern = new Regex(@"^[\w\.]+$", RegexOptions.Compiled);

        private static int Main(string[] args)
        {
            try
            {
                return MainImpl(args);
            }
            catch (UserErrorException e)
            {
                Console.WriteLine($"Error: {e.Message}");
                return 1;
            }
        }

        private static int MainImpl(string[] args)
        {
            string root = DetermineRootDirectory();
            if (root == null)
            {
                throw new UserErrorException("Unable to determine root directory. Please run within google-cloud-dotnet repository.");
            }

            string snippetsSource = Path.Combine(root, "snippets");
            if (!Directory.Exists(snippetsSource))
            {
                throw new UserErrorException($"Snippets directory {snippetsSource} doesn't exist. Aborting.");
            }

            string metadata = Path.Combine(root, "docs", "obj", "api");
            if (!Directory.Exists(metadata))
            {
                throw new UserErrorException($"Metadata directory {metadata} doesn't exist. Aborting.");
            }

            string output = Path.Combine(root, "docs", "obj", "snippets");
            if (!Directory.Exists(output))
            {
                Directory.CreateDirectory(output);
            }
            else
            {
                foreach (var file in Directory.GetFiles(output))
                {
                    File.Delete(file);
                }
            }

            var memberLookup = LoadMembersByType(metadata);
            Console.WriteLine($"Loaded {memberLookup.Count} types with {memberLookup.Sum(x => x.Count())} members");
            List<string> errors = new List<string>();
            var snippets = LoadAllSnippets(snippetsSource, errors);
            Console.WriteLine($"Loaded {snippets.Sum(x => x.Count())} snippets");
            foreach (var entry in snippets)
            {
                string snippetFile = entry.Key + ".txt";
                GenerateSnippetText(Path.Combine(output, snippetFile), entry);
                MapMetadataUids(entry, memberLookup[entry.Key], errors);
                GenerateSnippetMarkdown(Path.Combine(output, entry.Key + ".md"), snippetFile, entry);
            }
            if (errors.Any())
            {
                foreach (var error in errors)
                {
                    Console.Error.WriteLine(error);
                }
                return 1;
            }

            return 0;
        }

        private static ILookup<string, Snippet> LoadAllSnippets(string snippetSourceDir, List<string> errors)
        {
            var query = from project in Directory.GetDirectories(snippetSourceDir)
                            // Path.GetFileName just takes the last part of the name; it doesn't know that it's a directory.
                        let projectName = TrimSuffix(Path.GetFileName(project), ".Snippets")
                        from sourceFile in Directory.GetFiles(project, "*.cs")
                        let type = projectName + "." + TrimSuffix(Path.GetFileName(sourceFile), "Snippets.cs")
                        from snippet in LoadFileSnippets(sourceFile, errors)
                        select new { Type = type, Snippet = snippet };
            return query.ToLookup(item => item.Type, item => item.Snippet);
        }

        // TODO: Can we break this method up at all? Feels like there should be alternatives available...
        private static IEnumerable<Snippet> LoadFileSnippets(string file, List<string> errors)
        {
            Snippet currentSnippet = null;
            int lineNumber = 0;
            // Only keep the filename part for diagnostics; that's usually going to be obvious enough.
            string sourceFile = Path.GetFileName(file);
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                // 5 kinds of line to consider:
                // StartSnippet / StartSample: only valid when not in a snippet
                // EndSnippet / EndSample: only valid when in a snippet
                // Additional: only valid when at the start of a snippet
                // Resource: only valid when not in a snippet
                // Regular line; valid in either case, but with different results.

                string location = $"{sourceFile}:{lineNumber}";

                if (line.Contains(StartSnippet) || line.Contains(StartSample))
                {
                    if (currentSnippet == null)
                    {
                        bool sample = line.Contains(StartSample);
                        string id = GetContentAfterPrefix(line, sample ? StartSample : StartSnippet);
                        if (sample && !DocfxSnippetPattern.IsMatch(id))
                        {
                            errors.Add($"{location}: Sample ID '{id}' is not a valid docfx snippet ID");
                        }
                        else if (!IsValidId(id))
                        {
                            errors.Add($"{location}: Invalid snippet ID '{id}'");
                            // We'll also get an "end snippet with no start" error later, but that's
                            // not too bad.
                        }
                        else
                        {
                            currentSnippet = new Snippet
                            {
                                SnippetId = id,
                                SourceFile = sourceFile,
                                SourceStartLine = lineNumber
                            };
                            if (!sample)
                            {
                                currentSnippet.MetadataMembers.Add(id);
                            }
                        }
                    }
                    else
                    {
                        errors.Add($"{location}: Invalid start of nested sample/snippet");
                    }
                }
                else if (line.Contains(EndSample) || line.Contains(EndSnippet))
                {
                    if (currentSnippet != null)
                    {
                        currentSnippet.TrimLeadingSpaces();
                        yield return currentSnippet;
                        currentSnippet = null;
                    }
                    else
                    {
                        errors.Add($"{location}: Snippet/sample end without start");
                    }
                }
                else if (line.Contains(AdditionalMember))
                {
                    if (currentSnippet != null)
                    {
                        string id = GetContentAfterPrefix(line, AdditionalMember);
                        if (currentSnippet.Lines.Count == 0)
                        {
                            if (IsValidId(id))
                            {
                                currentSnippet.MetadataMembers.Add(id);
                            }
                            else
                            {
                                errors.Add($"{location}: Invalid additional member ID '{id}'");
                            }
                        }
                        else
                        {
                            errors.Add($"{location}: Additional member ID part way through snippet");
                        }
                    }
                    else
                    {
                        errors.Add($"{location}: Additional member ID not in snippet");
                    }
                }
                else if (line.Contains(Resource))
                {
                    if (currentSnippet == null)
                    {
                        string[] fileAndId = GetContentAfterPrefix(line, Resource).Split(' ');
                        if (fileAndId.Length != 2)
                        {
                            errors.Add($"{location}: Resource must specify file and snippet ID");
                        }
                        else if (!DocfxSnippetPattern.IsMatch(fileAndId[1]))
                        {
                            errors.Add($"{location}: Resource snippet ID {fileAndId[1]} is not a valid docfx ID");
                        }
                        else
                        {
                            string id = fileAndId[1];
                            string directory = Path.GetDirectoryName(file);
                            string resourceFile = Path.Combine(directory, fileAndId[0]);
                            string[] resourceContent = File.ReadAllLines(resourceFile);

                            var resourceSnippet = new Snippet
                            {
                                SnippetId = id,
                                SourceFile = resourceFile,
                                SourceStartLine = 1
                            };
                            resourceSnippet.Lines.AddRange(resourceContent);
                            yield return resourceSnippet;
                        }
                    }
                    else
                    {
                        errors.Add($"{location}: Resource specified within snippet");
                    }
                }
                else if (currentSnippet != null)
                {
                    currentSnippet.Lines.Add(line);
                }
            }
            if (currentSnippet != null)
            {
                errors.Add($"{currentSnippet.SourceLocation}: Snippet '{currentSnippet.SnippetId}' didn't end");
            }
        }

        // Is this a valid ID for a member match? We only check that if there's an open-paren,
        // the ID must end with a close-paren.
        private static bool IsValidId(string id) =>
            !(id.Contains("(") && !id.EndsWith(")"));

        private static string GetContentAfterPrefix(string line, string prefix)
        {
            int index = line.IndexOf(prefix);
            if (index == -1)
            {
                throw new ArgumentException($"'{line}' doesn't contain '{prefix}'");
            }
            return line.Substring(index + prefix.Length).Trim();
        }

        /// <summary>
        /// Generate a file containing all the given snippets. In the future, we may add
        /// some extra processing to include more text within the snippet flie, such as using directives...
        /// or we could munge any occurrence of template values (e.g. projectId) to string literals ("YOUR PROJECT ID").
        /// Any snippet with an ID which is a valid docfx snippet ID is wrapped in the snippet tags, for the sake
        /// of referring to it from documentation.
        /// </summary>
        private static void GenerateSnippetText(string outputFile, IEnumerable<Snippet> snippets)
        {
            using (var writer = File.CreateText(outputFile))
            {
                int lineIndex = 1;
                foreach (var snippet in snippets)
                {
                    // We produce a docfx snippet for any sample, and any snippet where the ID
                    // is also valid, as we might want to refer to it in conceptual docs *and* API docs.
                    bool validDocfxId = DocfxSnippetPattern.IsMatch(snippet.SnippetId);
                    writer.WriteLine($"----- Snippet {snippet.SnippetId} -----");
                    lineIndex++;
                    if (validDocfxId)
                    {
                        writer.WriteLine(snippet.DocfxSnippetStart);
                        lineIndex++;
                    }
                    snippet.StartLine = lineIndex;
                    snippet.Lines.ForEach(writer.WriteLine);
                    lineIndex += snippet.Lines.Count;
                    snippet.EndLine = lineIndex - 1;
                    if (validDocfxId)
                    {
                        writer.WriteLine(snippet.DocfxSnippetEnd);
                        lineIndex++;
                    }
                    writer.WriteLine();
                    lineIndex++;
                }
            }
        }

        /// <summary>
        /// For each snippet, try to find a single matching member and save it in the MetadataUid property.
        /// Errors are collected rather than an exception being immediately thrown, so that many errors can be
        /// detected and fixed in a single run.
        /// </summary>
        private static void MapMetadataUids(IEnumerable<Snippet> snippets, IEnumerable<Member> members, List<string> errors)
        {
            foreach (var snippet in snippets)
            {
                foreach (var snippetMemberId in snippet.MetadataMembers)
                {
                    var matches = members.Where(member => IsMemberMatch(member.Id, snippetMemberId)).ToList();
                    // We could potentially allow ambiguous matches and just use all of them...
                    if (matches.Count > 1)
                    {
                        errors.Add($"{snippet.SourceLocation}: Member ID '{snippetMemberId}' matches multiple members ({string.Join(", ", matches.Select(m => m.Id))}).");
                    }
                    else if (matches.Count == 0)
                    {
                        errors.Add($"{snippet.SourceLocation}: Member ID '{snippetMemberId}' matches no members.");
                    }
                    else
                    {
                        snippet.MetadataUids.Add(matches.First().Uid);
                    }
                }
            }
        }

        private static void GenerateSnippetMarkdown(string outputFile, string relativeSnippetFile, IEnumerable<Snippet> snippets)
        {
            if (!snippets.SelectMany(snippet => snippet.MetadataUids).Any())
            {
                return;
            }
            using (var writer = File.CreateText(outputFile))
            {
                foreach (var snippet in snippets)
                {
                    foreach (var metadataUid in snippet.MetadataUids)
                    {
                        writer.WriteLine("---");
                        writer.WriteLine($"uid: {metadataUid}");
                        writer.WriteLine("---");
                        writer.WriteLine();
                        writer.WriteLine("Example:");
                        writer.WriteLine($"[!code-cs[]({relativeSnippetFile}#L{snippet.StartLine}-L{snippet.EndLine})]");
                        writer.WriteLine();
                    }
                }
            }
        }

        private static bool IsMemberMatch(string memberId, string snippetId)
        {
            int openParen = snippetId.IndexOf("(");
            if (openParen != -1)
            {
                // Check member name first
                if (!memberId.StartsWith(snippetId.Substring(0, openParen + 1)))
                {
                    return false;
                }
                // Note: this will fail for generic types with an arity more than 1.
                // Let's cross that bridge when we come to it.
                string snippetParameters = snippetId.Substring(openParen + 1, snippetId.Length - 2 - openParen);
                string memberParameters = memberId.Substring(openParen + 1, memberId.Length - 2 - openParen);

                // Avoid issue of Foo() looking like it has a single parameter.
                if (memberParameters == "")
                {
                    return snippetParameters == "";
                }

                string[] splitSnippetParameters = snippetParameters.Split(',');
                string[] splitMemberParameters = memberParameters.Split(',');
                if (splitMemberParameters.Length != splitSnippetParameters.Length)
                {
                    return false;
                }
                return splitMemberParameters.Zip(splitSnippetParameters, IsParameterMatch).All(x => x);
            }
            else
            {
                return memberId.StartsWith(snippetId + "(");
            }
        }

        // This needs to be a *lot* smarter...
        private static bool IsParameterMatch(string memberParameter, string snippetParameter) =>
            snippetParameter == "*"
                || (snippetParameter == "string" && memberParameter == "System.String")
                || (memberParameter.Split('.').Last() == snippetParameter.Split('.').Last());

        /// <summary>
        /// Loads all the members from YAML metadata files, and group them by parent type.
        /// </summary>
        private static ILookup<string, Member> LoadMembersByType(string metadataDir)
        {
            var dictionary = new Dictionary<string, List<Member>>();
            // Urgh - there must be a nicer way of doing this.
            foreach (var file in Directory.GetFiles(metadataDir, "Google*.yml"))
            {
                using (var input = File.OpenText(file))
                {
                    var model = new Deserializer(namingConvention: new CamelCaseNamingConvention(), ignoreUnmatched: true).Deserialize<CodeModel>(input);
                    // Assume we only want classes and structs at the moment...
                    var type = model.Items.FirstOrDefault(x => x.Type == "Class" || x.Type == "Struct");
                    if (type == null)
                    {
                        continue;
                    }
                    var members = model.Items.Where(x => x.Parent == type.Uid).ToList();
                    dictionary[type.Uid] = members;
                }
            }
            return dictionary
                .SelectMany(pair => pair.Value.Select(m => new { pair.Key, Value = m }))
                .ToLookup(pair => pair.Key, pair => pair.Value);
        }        

        /// <summary>
        /// Find the root directory of the project. We expect this to contain "GoogleApis.sln" and "LICENSE".
        /// </summary>
        /// <returns></returns>
        private static string DetermineRootDirectory()
        {
            var directory = new DirectoryInfo(Environment.CurrentDirectory);
            while (directory != null &&
                (!File.Exists(Path.Combine(directory.FullName, "LICENSE"))
                || !File.Exists(Path.Combine(directory.FullName, "GoogleApis.sln"))))
            {
                directory = directory.Parent;
            }
            return directory?.FullName;
        }

        private static string TrimSuffix(string text, string suffix)
            => text.EndsWith(suffix) ? text.Substring(0, text.Length - suffix.Length) : text;
    }
}
