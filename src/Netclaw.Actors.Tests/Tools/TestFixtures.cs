namespace Netclaw.Actors.Tests.Tools;

internal static class TestFixtures
{
    public static string Load(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Tools", "Fixtures", filename);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Fixture not found: {path}");
        return File.ReadAllText(path);
    }
}
