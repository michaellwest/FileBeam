using System.Reflection;

namespace FileBeam;

/// <summary>
/// Reads files embedded in the assembly at compile time via &lt;EmbeddedResource&gt;.
/// Results are cached after the first load.
/// </summary>
public static class ResourceLoader
{
    private static readonly Assembly Asm = Assembly.GetExecutingAssembly();

    // Lazy cached values so streams are only opened once
    private static readonly Lazy<string> _template = new(() => Load("wwwroot.index.html"));
    private static readonly Lazy<string> _appJs    = new(() => Load("wwwroot.app.js"));

    public static string Template => _template.Value;
    public static string AppJs    => _appJs.Value;

    private static string Load(string relativeName)
    {
        // Embedded resource names are: <RootNamespace>.<path with dots>
        var resourceName = $"FileBeam.{relativeName}";

        using var stream = Asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' not found. " +
                $"Available: {string.Join(", ", Asm.GetManifestResourceNames())}");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
