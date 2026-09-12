namespace UvssService.Streaming;

/// <summary>Holds the latest JPEG frame for one camera and lets any number
/// of HTTP clients (browser &lt;img&gt; tags pointed at the MJPEG endpoint)
/// wait for "the next frame after the one I last saw" -- this is what
/// turns a continuously-updating still image into what the browser renders
/// as live video, without WebRTC/signaling complexity. One instance per
/// camera per lane, published to continuously by that camera's capture
/// loop regardless of whether any client is currently watching.</summary>
public class MjpegBroadcaster
{
    private readonly object _lock = new();
    private byte[]? _latestFrame;
    private int _version;
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Publish(byte[] jpeg)
    {
        lock (_lock)
        {
            _latestFrame = jpeg;
            _version++;
            var old = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            old.TrySetResult();
        }
    }

    /// <summary>Blocks until a frame newer than `lastSeenVersion` is
    /// published (or `ct` cancels). Returns the frame and its version --
    /// pass the returned version back in on the next call.</summary>
    public async Task<(byte[] Frame, int Version)?> WaitForFrameAfterAsync(int lastSeenVersion, CancellationToken ct)
    {
        while (true)
        {
            Task waitTask;
            lock (_lock)
            {
                if (_latestFrame != null && _version != lastSeenVersion)
                {
                    return (_latestFrame, _version);
                }
                waitTask = _signal.Task;
            }
            var completed = await Task.WhenAny(waitTask, Task.Delay(Timeout.Infinite, ct));
            if (completed != waitTask)
            {
                ct.ThrowIfCancellationRequested();
            }
        }
    }
}
