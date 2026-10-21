namespace MediaBrowser.Model.Hls;

/// <summary>
/// How many jobs, deletions, media and published renditions pre-encoded HLS has.
/// </summary>
/// <param name="Pending">The pending jobs.</param>
/// <param name="Running">The running jobs.</param>
/// <param name="Verifying">The jobs whose outputs wait to be verified.</param>
/// <param name="Complete">The complete jobs.</param>
/// <param name="Failed">The failed jobs.</param>
/// <param name="Cancelled">The cancelled jobs.</param>
/// <param name="PendingDeletions">The directories waiting to be deleted.</param>
/// <param name="Media">The identified contents.</param>
/// <param name="PublishedRenditions">The published renditions.</param>
public sealed record HlsJobCounts(int Pending, int Running, int Verifying, int Complete, int Failed, int Cancelled, int PendingDeletions, int Media, int PublishedRenditions);
