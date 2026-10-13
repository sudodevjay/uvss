using OpenCvSharp;
using UvssService.Imaging;
// Synthetic passes: a real textured object (box face from a recorded pass)
// moving up across a real static background, with exact known motion.
var outDir = args[0];
using var bg = Cv2.ImRead(Path.Combine(outDir, "synth_src", "bg.jpg"), ImreadModes.Grayscale);
using var src = Cv2.ImRead(Path.Combine(outDir, "synth_src", "obj.jpg"), ImreadModes.Grayscale);
const int X0 = 200, W = 1100;
using var face = src[new Rect(X0, 0, W, src.Rows)].Clone();
using var flip = new Mat(); Cv2.Flip(face, flip, FlipMode.X);
using var obj = new Mat(); Cv2.VConcat(new[] { face, flip, face }, obj);   // ~3648 rows long
int H = bg.Rows, L = obj.Rows;
var rng = new Random(7);
var distort = args.Contains("distort");
using var mapX = new Mat(H, bg.Cols, MatType.CV_32F); using var mapY = new Mat(H, bg.Cols, MatType.CV_32F);
{
    double cx = bg.Cols / 2.0, cy = H / 2.0, norm = Math.Sqrt(cx * cx + cy * cy);
    for (int y = 0; y < H; y++) for (int x = 0; x < bg.Cols; x++)
    {
        double dx = (x - cx) / norm, dy = (y - cy) / norm, r2 = dx * dx + dy * dy, k = 1 + 0.35 * r2;
        mapX.Set(y, x, (float)(cx + (x - cx) * k)); mapY.Set(y, x, (float)(cy + (y - cy) * k));
    }
}
void Lens(Mat f) { if (!distort) return; using var o = new Mat(); Cv2.Remap(f, o, mapX, mapY, InterpolationFlags.Linear); o.CopyTo(f); }
if (args.Length > 1 && args[1] == "diag")
{
    var speed = double.Parse(args[2]);
    using var est = new DisMotionEstimator();
    double tp = H;
    for (int i = 0; i < (args.Length > 3 ? int.Parse(args[3]) : 160); i++)
    {
        var v = i < 10 ? 0 : speed;
        tp -= v;
        using var f = bg.Clone();
        int t = (int)Math.Round(tp);
        int y0 = Math.Max(0, t), y1 = Math.Min(H, t + L);
        if (y1 > y0) { using var part = obj.RowRange(y0 - t, y1 - t); part.CopyTo(f[new Rect(X0, y0, W, y1 - y0)]); }
        using var noise = new Mat(f.Size(), MatType.CV_16S); Cv2.Randn(noise, 0, 4);
        using var f16 = new Mat(); f.ConvertTo(f16, MatType.CV_16S); Cv2.Add(f16, noise, f16); f16.ConvertTo(f, MatType.CV_8U);
        var r = est.Next(f);
        if (i % (args.Length > 4 ? int.Parse(args[4]) : 8) == 0) Console.WriteLine($"{i,4} true={-v,7:F1} est={(r.HasValue ? r.Value.Dy.ToString("F1") : "null"),7} objTop={t}");
    }
    return;
}


double[] Profile(string name)
{
    var v = new List<double>();
    void Seg(double speed, int n) { for (int i = 0; i < n; i++) v.Add(speed); }
    switch (name)
    {
        case "slow": Seg(0, 20); Seg(3, (H + L) / 3 + 10); Seg(0, 20); break;
        case "medium": Seg(0, 20); Seg(15, (H + L) / 15 + 5); Seg(0, 20); break;
        case "fast": Seg(0, 20); Seg(45, (H + L) / 45 + 3); Seg(0, 20); break;
        case "fast_stop_go": Seg(0, 20); Seg(45, 40); Seg(0, 90); Seg(45, (H + L - 1800) / 45 + 3); Seg(0, 20); break;
        case "stop_go": Seg(0, 20); for (int k = 0; k < 12; k++) { Seg(12, 30); Seg(0, 30); } Seg(12, (H + L - 4320) / 12 + 5 > 0 ? (H + L - 4320) / 12 + 5 : 0); Seg(0, 20); break;
        case "accel": Seg(0, 20); for (double s = 1; s < 60; s += 0.5) v.Add(s); Seg(0, 20); break;
    }
    return v.ToArray();
}

foreach (var name in new[] { "slow", "medium", "fast", "fast_stop_go", "stop_go", "accel" })
{ if (args.Length > 2 && !args.Contains(name) && args[2] != "all") continue;
{
    var prof = Profile(name);
    using var m = new FrameMosaic(-1);
    double top = H;            // object top edge (row); starts just below the frame
    double travelled = 0;      // truth: how far the object has moved so far
    var lagLog = new List<string>();
    int maxLag = 0, frozenFrames = 0, lastRows = 0, rowsAtHead = -1, rowsAtTail = -1;
    for (int i = 0; i < prof.Length; i++)
    {
        top -= prof[i]; travelled += prof[i];
        using var f = bg.Clone();
        // paste visible part of object
        int t = (int)Math.Round(top);
        int y0 = Math.Max(0, t), y1 = Math.Min(H, t + L);
        if (y1 > y0)
        {
            using var part = obj.RowRange(y0 - t, y1 - t);
            part.CopyTo(f[new Rect(X0, y0, W, y1 - y0)]);
        }
        Lens(f);
        using var noise = new Mat(f.Size(), MatType.CV_16S); Cv2.Randn(noise, 0, 4);
        using var f16 = new Mat(); f.ConvertTo(f16, MatType.CV_16S); Cv2.Add(f16, noise, f16); f16.ConvertTo(f, MatType.CV_8U);
        m.Add(f);
        // "truth rows" = how much object has crossed the centre line
        int centre = H / 2;
        double crossed = Math.Clamp(centre - top, 0, L);
        int lag = (int)(crossed - m.MosaicRows);
        maxLag = Math.Max(maxLag, Math.Abs(lag));
        if (prof[i] > 0 && crossed > 0 && crossed < L && m.MosaicRows == lastRows) frozenFrames++;
        lastRows = m.MosaicRows;
        if (rowsAtHead < 0 && top <= centre) rowsAtHead = m.MosaicRows;
        if (rowsAtTail < 0 && top + L <= centre) rowsAtTail = m.MosaicRows;
    }
    int truth = (int)Math.Clamp(H / 2 - top, 0, L);
    using var img = m.Build()!;
    using var r = new Mat(); Cv2.Rotate(img, r, RotateFlags.Rotate90Counterclockwise);
    Cv2.ImWrite(Path.Combine(outDir, $"synth_{name}.jpg"), r);
    var objRows = (rowsAtTail < 0 ? m.MosaicRows : rowsAtTail) - rowsAtHead;
    var objTruth = rowsAtTail < 0 ? truth : L;
    Console.WriteLine($"{(distort ? "lens " : "")}{name,-13} frames={prof.Length,4} object: truth={objTruth,5} in image={objRows,5} ({100.0 * objRows / objTruth,5:F1}%)  maxLag={maxLag,4}px  movingFramesWithNoGrowth={frozenFrames}");
}
}
