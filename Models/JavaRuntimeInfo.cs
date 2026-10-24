using System.Text.Json.Serialization;
using PMCL.Localization;

namespace PMCL.Models;

/// <summary>
/// Where a Java runtime came from. Mojang runtimes are downloaded by CmlLib
/// into the shared <c>runtime</c> folder, while system runtimes are installed
/// by the user or another application.
/// </summary>
public enum JavaRuntimeSource
{
    System,
    Mojang
}

/// <summary>
/// A Java executable discovered by <c>JavaRuntimeService</c>. The value is
/// cached so the launcher can show the last scan result without touching the
/// machine again on every start.
/// </summary>
public sealed record JavaRuntimeInfo(
    string Path,
    string Version,
    int MajorVersion,
    string Architecture,
    string Vendor,
    JavaRuntimeSource Source)
{
    /// <summary>
    /// Human readable label for the Java picker. Vendor/source names are
    /// translated at display time so cached entries follow the UI language.
    /// </summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var version = MajorVersion > 0
                ? $"Java {MajorVersion}"
                : L.Tr("Java.UnknownVersion");

            var vendor = Source == JavaRuntimeSource.Mojang
                ? L.Tr("Java.MojangRuntime")
                : string.IsNullOrWhiteSpace(Vendor)
                    ? L.Tr("Java.SystemRuntime")
                    : Vendor;

            return string.IsNullOrWhiteSpace(Architecture)
                ? $"{version} · {vendor}"
                : $"{version} ({Architecture} · {vendor})";
        }
    }
}
