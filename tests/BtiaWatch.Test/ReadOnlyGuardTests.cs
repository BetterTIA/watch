using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace BtiaWatch.Test
{
    /// <summary>
    /// The server must stay read-only: no API that writes values to the PLC may appear in the sources.
    /// The patterns are assembled from pieces so this file does not contain them either.
    /// </summary>
    [TestClass]
    public class ReadOnlyGuardTests
    {
        private static readonly string[] Banned =
        {
            "Load" + "SnapshotAsActualValues",
            "Load" + "StartValuesAsActualValues",
            "Down" + "load",
            "Mod" + "ify",
            @"\b" + "For" + @"ce\b",
            // "WatchAndForceTableGroup" is the only Openness route to the watch tables, so match whole words only.
            @"\b" + "For" + @"ceTables\b",
            @"\bPlc" + "For" + "ceTable",
        };

        [TestMethod]
        public void Sources_ContainNoWritingApi()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", ".."));
            var files = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .ToList();

            Assert.IsTrue(files.Count > 3, "Sources not found below " + root);

            var hits = (from file in files
                        from pattern in Banned
                        where Regex.IsMatch(File.ReadAllText(file), pattern, RegexOptions.IgnoreCase)
                        select $"{Path.GetFileName(file)}: {pattern}").ToList();

            Assert.AreEqual(0, hits.Count, string.Join(Environment.NewLine, hits));
        }

        private static string ThisFile([CallerFilePath] string path = "") => path;
    }
}
