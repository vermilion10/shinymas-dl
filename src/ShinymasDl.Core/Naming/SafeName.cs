namespace ShinymasDl.Core.Naming;

public static class SafeName
{
    private static readonly HashSet<char> Invalid = [.. Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary> strips characters that are invalid on Windows too, so the tree copies across machines </summary>
    public static string Clean(string value)
    {
        var chars = value.Where(c => !Invalid.Contains(c) && !char.IsControl(c)).ToArray();
        return new string(chars).Trim().TrimEnd('.');
    }
}
