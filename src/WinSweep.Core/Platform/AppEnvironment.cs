namespace WinSweep.Core.Platform;

/// <summary>
/// Describes the kind of installation this process is running as. The distinction
/// matters for capabilities that the Microsoft Store does not grant, such as
/// administrator elevation.
/// </summary>
public static class AppEnvironment
{
    /// <summary>
    /// True when this process has a package identity, which is the case for an MSIX
    /// package installed from the Microsoft Store or sideloaded.
    /// </summary>
    public static bool IsPackagedApp
    {
        get
        {
            try
            {
                var package = Windows.ApplicationModel.Package.Current;
                return package is not null && package.Id is not null;
            }
            catch (Exception)
            {
                // An unpackaged process has no package identity, so the call fails.
                // Any failure here means "not packaged".
                return false;
            }
        }
    }

    /// <summary>
    /// True when this build is intended for the Microsoft Store. A Store package has
    /// an identity and cannot elevate, so administrator tasks are unavailable.
    /// </summary>
    public static bool IsStoreBuild => IsPackagedApp;
}
