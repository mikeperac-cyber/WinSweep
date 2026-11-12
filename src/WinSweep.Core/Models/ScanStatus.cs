namespace WinSweep.Core.Models;

/// <summary>
/// How complete a measurement is. A partial or unavailable measurement is never
/// reported as zero bytes: unknown must stay visibly unknown.
/// </summary>
public enum ScanStatus
{
    /// <summary>The whole subtree was walked successfully.</summary>
    Complete,

    /// <summary>Some part of the subtree could not be read; the size is a lower bound.</summary>
    Partial,

    /// <summary>The size could not be measured at all.</summary>
    Unavailable
}
