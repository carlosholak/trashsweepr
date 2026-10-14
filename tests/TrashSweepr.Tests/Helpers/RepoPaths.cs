namespace TrashSweepr.Tests.Helpers;

internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string TweaksDirectory => Path.Combine(Root, "tweaks");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "trashsweepr.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Não foi possível localizar a raiz do repositório (trashsweepr.sln).");
    }
}
