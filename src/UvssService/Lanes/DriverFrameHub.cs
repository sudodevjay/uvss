using System.Collections.Concurrent;
using OpenCvSharp;

namespace UvssService.Lanes;

/// <summary>Same "tap into the continuous camera stream only during an
/// Active pass" pattern as AnprFrameHub, just for the driver camera --
/// feeds a pass's DriverTrack so it can pick the best (sharpest, most
/// frontal-face) frame across the whole pass instead of trusting whatever
/// single frame the camera happened to show at one instant.</summary>
public class DriverFrameHub
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
