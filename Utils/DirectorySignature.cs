using System;
using System.Collections.Generic;

namespace CeeFind.Utils
{
    /// <summary>
    /// What kind of directory this is, judged from the names it contains.
    ///
    /// Extensions, filename trigrams and modification spread all describe a directory's
    /// contents in bulk. These say what it is *for*: a Node package, a .NET project, a
    /// vendored dependency tree. That is readable on first sight, without anything ever
    /// having been searched for there, which is what makes it useful on a repository the
    /// index has never seen.
    /// </summary>
    [Flags]
    internal enum DirectoryMarkers
    {
        None = 0,
        NodePackage = 1 << 0,
        DotNetProject = 1 << 1,
        DotNetSolution = 1 << 2,
        Maven = 1 << 3,
        Gradle = 1 << 4,
        Rust = 1 << 5,
        Go = 1 << 6,
        Python = 1 << 7,
        TypeScript = 1 << 8,
        Make = 1 << 9,
        CMake = 1 << 10,
        Docker = 1 << 11,
        Lockfile = 1 << 12,

        /// <summary>Generated output: minified bundles and source maps.</summary>
        Generated = 1 << 13,

        /// <summary>A repository root, from a .git child directory.</summary>
        GitRepository = 1 << 14,

        /// <summary>Holds third-party code rather than the author's own.</summary>
        Vendored = 1 << 15,

        /// <summary>Build output.</summary>
        BuildOutput = 1 << 16,

        /// <summary>
        /// Git's own object store. Distinct from GitRepository: holding a .git marks a
        /// repository root, which is a good place to look; being .git marks internals,
        /// which is not.
        /// </summary>
        GitInternals = 1 << 17,

        /// <summary>
        /// Markers naming somebody's own project.
        ///
        /// NodePackage is deliberately absent. Every package inside node_modules carries a
        /// package.json, so the marker tags overwhelmingly fetched code - measured across
        /// 70,414 visits it scores 1.19 against the global rate, meaning it says nothing.
        /// It marks "a JS package", not "a JS project of yours".
        /// </summary>
        AuthoredProject =
            DotNetProject | DotNetSolution | Maven | Gradle |
            Rust | Go | Python | TypeScript | Make | CMake,

        /// <summary>
        /// Markers of the top of something somebody works on. Measured as by far the
        /// strongest positive signal available - a repository root scored 112 times the
        /// global rate and a lockfile 97 - which makes sense: it is where authored code
        /// begins, and both were previously given no weight at all.
        /// </summary>
        ProjectRoot = GitRepository | Lockfile,

        /// <summary>
        /// Markers of content that is written by tooling and searched by nobody. Measured
        /// across 16,978 visits these produced not one result.
        ///
        /// Vendored and Generated are deliberately absent despite the intuition. Measured
        /// on searches that actually express a preference they are ordinary - 1.40 and 1.28
        /// against global - so penalising them was punishing directories for being large
        /// rather than for being unhelpful.
        /// </summary>
        BarrenOutput = BuildOutput | GitInternals,
    }

    internal static class DirectorySignature
    {
        private static readonly Dictionary<string, DirectoryMarkers> ByExactName =
            new Dictionary<string, DirectoryMarkers>(StringComparer.OrdinalIgnoreCase)
            {
                ["package.json"] = DirectoryMarkers.NodePackage,
                ["pom.xml"] = DirectoryMarkers.Maven,
                ["build.gradle"] = DirectoryMarkers.Gradle,
                ["build.gradle.kts"] = DirectoryMarkers.Gradle,
                ["cargo.toml"] = DirectoryMarkers.Rust,
                ["go.mod"] = DirectoryMarkers.Go,
                ["pyproject.toml"] = DirectoryMarkers.Python,
                ["setup.py"] = DirectoryMarkers.Python,
                ["requirements.txt"] = DirectoryMarkers.Python,
                ["tsconfig.json"] = DirectoryMarkers.TypeScript,
                ["makefile"] = DirectoryMarkers.Make,
                ["cmakelists.txt"] = DirectoryMarkers.CMake,
                ["dockerfile"] = DirectoryMarkers.Docker,
                ["package-lock.json"] = DirectoryMarkers.Lockfile,
                ["yarn.lock"] = DirectoryMarkers.Lockfile,
                ["pnpm-lock.yaml"] = DirectoryMarkers.Lockfile,
                ["cargo.lock"] = DirectoryMarkers.Lockfile,
                ["poetry.lock"] = DirectoryMarkers.Lockfile,
            };

        private static readonly Dictionary<string, DirectoryMarkers> ByDirectoryName =
            new Dictionary<string, DirectoryMarkers>(StringComparer.OrdinalIgnoreCase)
            {
                [".git"] = DirectoryMarkers.GitInternals,
                ["node_modules"] = DirectoryMarkers.Vendored,
                ["vendor"] = DirectoryMarkers.Vendored,
                ["packages"] = DirectoryMarkers.Vendored,
                ["site-packages"] = DirectoryMarkers.Vendored,
                ["bin"] = DirectoryMarkers.BuildOutput,
                ["obj"] = DirectoryMarkers.BuildOutput,
                ["target"] = DirectoryMarkers.BuildOutput,
                ["dist"] = DirectoryMarkers.BuildOutput,
                ["__pycache__"] = DirectoryMarkers.BuildOutput,
            };

        internal static DirectoryMarkers FromFileNames(IEnumerable<string> fileNames)
        {
            DirectoryMarkers markers = DirectoryMarkers.None;

            foreach (string name in fileNames)
            {
                if (ByExactName.TryGetValue(name, out DirectoryMarkers exact))
                {
                    markers |= exact;
                    continue;
                }

                if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
                {
                    markers |= DirectoryMarkers.DotNetProject;
                }
                else if (name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                {
                    markers |= DirectoryMarkers.DotNetSolution;
                }
                else if (name.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
                {
                    markers |= DirectoryMarkers.Generated;
                }
            }

            return markers;
        }

        /// <summary>
        /// Markers a directory carries because of what it is called, or because of what its
        /// children are called - a .git child makes it a repository root.
        /// </summary>
        internal static DirectoryMarkers FromDirectoryNames(string self, IEnumerable<string> childNames)
        {
            DirectoryMarkers markers = DirectoryMarkers.None;

            if (self != null && ByDirectoryName.TryGetValue(self, out DirectoryMarkers own))
            {
                markers |= own;
            }

            foreach (string child in childNames)
            {
                if (string.Equals(child, ".git", StringComparison.OrdinalIgnoreCase))
                {
                    markers |= DirectoryMarkers.GitRepository;
                }
            }

            return markers;
        }
    }
}
