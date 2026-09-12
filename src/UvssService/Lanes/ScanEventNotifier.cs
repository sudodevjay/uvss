namespace UvssService.Lanes;

/// <summary>Pure pub/sub signal, no data of its own -- ScanEvents now live
/// permanently in MySQL (see Data.Entities.ScanEvent), not in an in-memory
/// store, so there's nothing here for a page to read directly. This is only
/// what tells Home/ScanHistory/AnalyticsDashboard "a new scan was just
/// saved, go re-query the database" so they still update live instead of
/// only on the next unrelated re-render or a manual refresh.</summary>
public class ScanEventNotifier
{
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();
}
