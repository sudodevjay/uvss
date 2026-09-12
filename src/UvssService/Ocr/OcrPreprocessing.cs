using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace UvssService.Ocr;

/// <summary>Shared preprocessing for both OCR paths (CTC and SAR) -- both
/// use the exact same `_resize_norm` formula in anpr-ai-service (PlateOCR
/// and sar_refiner.py's copies are identical), so this is the single C#
/// implementation both OnnxCtcOcr and OnnxPlateOcr call, instead of two
/// copies that could silently drift apart.</summary>
public static class OcrPreprocessing
{
    public const int ImgHeight = 48;
    public const int ImgWidthRef = 320;

    public static (DenseTensor<float> Tensor, int ImgW, float ValidRatio) ResizeNorm(Mat img)
    {
        var h = img.Rows;
        var w = img.Cols;
        var ratio = w / (float)h;
        var maxWhRatio = Math.Max(ImgWidthRef / (float)ImgHeight, ratio);
        var imgW = (int)(ImgHeight * maxWhRatio);
        var ceilResizedW = (int)Math.Ceiling(ImgHeight * ratio);
        var resizedW = ceilResizedW > imgW ? imgW : ceilResizedW;
        resizedW = Math.Max(1, resizedW);
        imgW = Math.Max(imgW, resizedW);

        using var resized = new Mat();
        Cv2.Resize(img, resized, new Size(resizedW, ImgHeight));
        using var resizedF = new Mat();
        resized.ConvertTo(resizedF, MatType.CV_32FC3);

        var tensor = new DenseTensor<float>(new[] { 1, 3, ImgHeight, imgW });
        for (var y = 0; y < ImgHeight; y++)
        {
            for (var x = 0; x < resizedW; x++)
            {
                var px = resizedF.At<Vec3f>(y, x);
                tensor[0, 0, y, x] = (px.Item0 / 255f - 0.5f) / 0.5f;
                tensor[0, 1, y, x] = (px.Item1 / 255f - 0.5f) / 0.5f;
                tensor[0, 2, y, x] = (px.Item2 / 255f - 0.5f) / 0.5f;
            }
        }

        var validRatio = Math.Min(1.0f, resizedW / (float)imgW);
        return (tensor, imgW, validRatio);
    }
}
