using System.Collections.Concurrent;
using OpenCvSharp;

namespace UvssService.Lanes;

/// <summary>Lets LaneWorkerHostedService "tap into" the ANPR camera's
/// continuous frame stream (owned by CameraStreamingHostedService, which
/// runs regardless of whether a vehicle is present) only for the duration
/// of an Active pass -- each frame published while subscribed feeds that
/// pass's PlateTrack. The camera itself never starts/stops per pass
/// anymore; only this subscription does.</summary>
public class AnprFrameHub
{
    private readonly ConcurrentDictionary<string, Action<Mat>> _subscribers = new();

    public void Subscribe(string laneName, Action<Mat> handler) => _subscribers[laneName] = handler;

    public void Unsubscribe(string laneName) => _subscribers.TryRemove(laneName, out _);

    public void Publish(string laneName, Mat frame)
    {
        if (_subscribers.TryGetValue(laneName, out var handler))
        {
            handler(frame);
        }
    }
}
