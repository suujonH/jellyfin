namespace MediaBrowser.Model.Hls;

/// <summary>
/// The state of pre-encoded HLS.
/// </summary>
/// <param name="Enabled">Whether it is enabled; invalid settings turn it off.</param>
/// <param name="SettingsError">Why the saved settings are invalid, or <c>null</c>.</param>
/// <param name="StoreProblem">Why the store could not be trusted when it was last checked, or <c>null</c>.</param>
/// <param name="Counts">How many jobs, deletions, contents and published renditions there are.</param>
/// <param name="LastReverify">The result of the last verification of published renditions, or <c>null</c>.</param>
public sealed record HlsStatus(bool Enabled, string? SettingsError, string? StoreProblem, HlsJobCounts Counts, HlsReverifyReport? LastReverify);
