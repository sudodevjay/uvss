namespace UvssService.Cameras;

/// <summary>Shared "is this test path actually a video file?" check for the
/// simulated camera sources -- lets TestImageDir/TestImagePath point at a
/// single .mp4 (real recorded footage) instead of a folder of still .jpg
/// photos, without each camera class re-implementing the same extension
/// check.</summary>
internal static class VideoFileTestSource
{
    private static readonly string[] VideoExtensions = { ".mp4", ".avi", ".mov", ".mkv" };

    public static bool IsVideoFile(string path) =>
        !string.IsNullOrEmpty(path)
        && File.Exists(path)
        && VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
