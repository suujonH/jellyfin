using System.Collections.Generic;

namespace MediaBrowser.Model.Hls;

/// <summary>
/// The state of pre-encoded HLS.
/// </summary>
/// <param name="Enabled">Whether it is enabled; invalid settings turn it off.</param>
/// <param name="SettingsError">Why the saved settings are invalid, or <c>null</c>.</param>
/// <param name="StoreProblems">The roots of HLS stores in use that could not be trusted when they were last checked, and why.</param>
/// <param name="Counts">How many jobs, deletions, contents and published renditions there are.</param>
/// <param name="LastReverify">The result of the last verification of published renditions, or <c>null</c>.</param>
public sealed record HlsStatus(bool Enabled, string? SettingsError, IReadOnlyList<HlsStoreProblem> StoreProblems, HlsJobCounts Counts, HlsReverifyReport? LastReverify);
