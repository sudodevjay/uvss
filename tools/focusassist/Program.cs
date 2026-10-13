// Focus Assist: live view + sharpness score of the centre of the image.
// Turn the lens focus ring slowly; the best focus is where SHARPNESS peaks.
// Keys: Q/Esc = quit, S = save full-res frame. Optional arg: seconds to run.
using Basler.Pylon;
using OpenCvSharp;
var runSeconds = args.Length > 0 ? int.Parse(args[0]) : 600;
var saveDir = args.Length > 1 ? args[1] : ".";
using var cam = new Camera();
cam.Open();
var p = cam.Parameters;
p[PLCamera.UserSetSelector].SetValue(PLCamera.UserSetSelector.Default); p[PLCamera.UserSetLoad].Execute();
p[PLCamera.PixelFormat].SetValue(PLCamera.PixelFormat.Mono8);
p[PLCamera.GevSCPSPacketSize].SetValue(8192);
p[PLCamera.AcquisitionFrameRateEnable].SetValue(true); p[PLCamera.AcquisitionFrameRate].SetValue(10);
p[PLCamera.AutoFunctionProfile].SetValue(PLCamera.AutoFunctionProfile.MinimizeGain);
p[PLCamera.AutoExposureTimeLowerLimit].SetValue(100.0); p[PLCamera.AutoExposureTimeUpperLimit].SetValue(2000.0);
p[PLCamera.AutoGainUpperLimit].SetValue(24.0);
p[PLCamera.ExposureAuto].SetValue(PLCamera.ExposureAuto.Continuous); p[PLCamera.GainAuto].SetValue(PLCamera.GainAuto.Continuous);
cam.StreamGrabber.Start(GrabStrategy.LatestImages, GrabLoop.ProvidedByUser);
double best = 0; var sw = System.Diagnostics.Stopwatch.StartNew(); var last = new Mat();
Cv2.NamedWindow("UVSS Focus Assist", WindowFlags.Normal); Cv2.ResizeWindow("UVSS Focus Assist", 1200, 760);
while (sw.Elapsed.TotalSeconds < runSeconds)
{
    using var r = cam.StreamGrabber.RetrieveResult(1000, TimeoutHandling.Return);
    if (r == null || !r.GrabSucceeded) { var k0 = Cv2.WaitKey(1); if (k0 == 'q' || k0 == 27) break; continue; }
    var buf = (byte[])r.PixelData;
    using var img = Mat.FromPixelData(r.Height, r.Width, MatType.CV_8UC1, buf);
    img.CopyTo(last);
    // Sharpness = variance of the Laplacian over the central 50% of the frame
    // (the lens is softest at the edges; the strips are cut at the centre).
    var roi = new Rect(img.Cols / 4, img.Rows / 4, img.Cols / 2, img.Rows / 2);
    using var centre = img[roi];
    using var lap = new Mat(); Cv2.Laplacian(centre, lap, MatType.CV_64F, 3);
    Cv2.MeanStdDev(lap, out _, out var sd);
    var sharp = sd.Val0 * sd.Val0;
    best = Math.Max(best, sharp);
    var exp = p[PLCamera.ExposureTime].GetValue(); var gain = p[PLCamera.Gain].GetValue();
    using var view = new Mat(); Cv2.CvtColor(img, view, ColorConversionCodes.GRAY2BGR);
    Cv2.Rectangle(view, roi, new Scalar(0, 255, 255), 3);
    var col = sharp >= 0.95 * best ? new Scalar(0, 220, 0) : new Scalar(0, 0, 255);
    Cv2.Rectangle(view, new Rect(0, 0, view.Cols, 150), new Scalar(0, 0, 0), -1);
    Cv2.PutText(view, $"SHARPNESS {sharp,7:F0}   best {best:F0}", new Point(20, 60), HersheyFonts.HersheySimplex, 2.0, col, 4);
    Cv2.PutText(view, $"exposure {exp:F0} us   gain {gain:F1} dB   (S = save, Q = quit)", new Point(20, 125), HersheyFonts.HersheySimplex, 1.2, new Scalar(255, 255, 255), 2);
    Cv2.ImShow("UVSS Focus Assist", view);
    Console.WriteLine($"{sw.Elapsed.TotalSeconds,6:F1}s sharp={sharp:F0} best={best:F0} exp={exp:F0} gain={gain:F1}");
    var k = Cv2.WaitKey(1);
    if (k == 'q' || k == 27) break;
    if (k == 's') { var fn = Path.Combine(saveDir, $"focus_{DateTime.Now:HHmmss}.png"); Cv2.ImWrite(fn, last); Console.WriteLine("saved " + fn); }
}
Cv2.ImWrite(Path.Combine(saveDir, "focus_last.png"), last);
cam.StreamGrabber.Stop(); cam.Close(); Cv2.DestroyAllWindows();
