namespace ForgeCore.Tests;

internal static class ContentFolderChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "forgecore-folders-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var dir in new[] { "B/ForgeContent", "a/x/forgecontent", "a/y/FORGECONTENT/inner", "c/other", "c/forgecontentx" })
                Directory.CreateDirectory(Path.Combine(root, dir));
            var index = ContentFolderIndex.Build(root);
            var found = index.Find("ForgeContent");
            var expected = new[] { Path.Combine(root, "B", "ForgeContent"), Path.Combine(root, "a", "x", "forgecontent"), Path.Combine(root, "a", "y", "FORGECONTENT") }
                .Select(Path.GetFullPath).OrderBy(p => p, StringComparer.Ordinal).ToList();
            check(found.SequenceEqual(expected), "content folders: nested matches of any case are found, sorted and distinct, near names are not");
            check(index.Find("forgecontent").SequenceEqual(found) && index.Find("missing").Count == 0, "content folders: lookup ignores case and a missing name gives an empty list");
            check(ContentFolderIndex.Build(Path.Combine(root, "nope")).Errors.Count == 1, "content folders: an unreadable root adds one error");
        }
        finally { Directory.Delete(root, true); }
    }
}
