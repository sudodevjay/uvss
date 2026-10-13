using System.Threading.Channels;
using OpenCvSharp;

namespace UvssService.Imaging;

/// <summary>Debugging aid: writes every grabbed area-scan frame of a pass as
/// a numbered JPEG, so the pass can be replayed through FrameMosaic offline
/// and the registration tuned against real camera footage. Encoding runs on
/// its own task -- doing it on the grab loop would make it fall behind the
/// camera (the same reason frames are kept Mono8).</summary>
public sealed class RawFrameRecorder : IAsyncDisposable
{
    private readonly Channel<(int Index, Mat Frame)> _queue =
        Channel.CreateUnbounded<(int, Mat)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private int _next;

    public RawFrameRecorder(string dir)
    {
        Directory.CreateDirectory(dir);
        _writer = Task.Run(async () =>
        {
            var prm = new ImageEncodingParam(ImwriteFlags.JpegQuality, 95);
            await foreach (var (index, frame) in _queue.Reader.ReadAllAsync())
            {
                using (frame)
                {
                    Cv2.ImWrite(Path.Combine(dir, $"{index:D5}.jpg"), frame, prm);
                }
            }
        });
    }

    /// <summary>Queues a copy of `frame`; the caller keeps ownership.</summary>
    public void Add(Mat frame) => _queue.Writer.TryWrite((_next++, frame.Clone()));

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writer;
    }
}
