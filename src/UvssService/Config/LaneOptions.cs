namespace UvssService.Config;

public class LaneOptions
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = false;
    public IpCameraOptions DriverCamera { get; set; } = new();
    public IpCameraOptions AnprCamera { get; set; } = new();

    /// <summary>A 3rd wide-angle camera slot CameraStreamingHostedService
    /// still streams alongside Driver/ANPR -- this site only has 3 cameras
    /// per lane (driver, ANPR, UVSS/Basler), so there is no Overview camera
    /// row in Lane Setup and this always stays at its disabled default (see
    /// LaneRuntimeConfigLoader). Left in rather than ripped out, since a
    /// site that DOES have this camera could still enable it later without
    /// a data-model change.</summary>
    public IpCameraOptions OverviewCamera { get; set; } = new();
    public BaslerCameraOptions BaslerCamera { get; set; } = new();
    public InterlockOptions Interlock { get; set; } = new();
}

public class CameraDefaultsOptions
{
    public string SnapshotPath { get; set; } = "/axis-cgi/jpg/image.cgi";
    public string SnapshotResolution { get; set; } = "1920x1080";
    public int SnapshotCompression { get; set; } = 10;
    public int SnapshotTimeoutSeconds { get; set; } = 6;
    /// <summary>How often (ms) to poll for a fresh frame from the driver/ANPR
    /// cameras while a lane is Active -- this is what makes the UI "live".</summary>
    public int LivePollIntervalMs { get; set; } = 400;

    /// <summary>How often (ms) the continuous camera-streaming loop grabs a
    /// fresh snapshot for the /stream MJPEG endpoints (Driver/ANPR/Overview
    /// cameras) -- runs for the service's whole lifetime, independent of
    /// whether a vehicle is present, unlike LivePollIntervalMs above (which
    /// only ever applied during an Active pass). Lower = smoother video,
    /// more camera/CPU load.</summary>
    public int StreamIntervalMs { get; set; } = 150;
}

public class AreaScanOptions
{
    /// <summary>Camera frame width in pixels (matches the sensor's/ROI's
    /// configured Width).</summary>
    public int FrameWidthPx { get; set; } = 2048;

    /// <summary>Safety cap on the number of frame GRABS in a single pass
    /// (not total rows) -- an area-scan camera grabs far fewer, much taller
    /// frames per pass than a line-scan sensor would grab 1-row lines, so
    /// this is a much smaller number than the old line-count cap.</summary>
    public int MaxFramesPerPass { get; set; } = 2000;

    /// <summary>Target rows-per-metre for the final stitched under-vehicle
    /// image, used to rescale each pass to a known physical scale (see
    /// FrameStitcher.NormalizeHeight) -- a property of the desired OUTPUT
    /// image resolution, independent of how many rows arrive per camera
    /// grab.</summary>
    public int ReferenceRowsPerMetre { get; set; } = 500;
}

public class LoopSensorDefaultsOptions
{
    public double MaxPassSeconds { get; set; } = 30;
}

public class FaceDetectorOptions
{
    public bool Enabled { get; set; } = true;
    public string CascadePath { get; set; } = "weights/haarcascade_frontalface_default.xml";
    public int TrackMaxFrames { get; set; } = 30;
}

public class ForeignObjectDetectorOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>"yolo" -- OnnxForeignObjectDetector, a trained
    /// object-detector (needs real labelled threat-object photos to be
    /// meaningful; the currently-shipped weights are pipeline-verification
    /// only, see that class's own remarks).
    /// "patchcore" -- PatchCoreAnomalyDetector, flags anything that doesn't
    /// look like the known-clean reference images (see that class's own
    /// remarks) -- no threat-object photos needed, only clean ones.</summary>
    public string Method { get; set; } = "yolo";

    public string ModelPath { get; set; } = "weights/foreign_object_detector.onnx";
    public float Confidence { get; set; } = 0.45f;

    /// <summary>"patchcore" method only.</summary>
    public string PatchCoreBackbonePath { get; set; } = "weights/patchcore_backbone.onnx";

    /// <summary>"patchcore" method only. Build via
    /// `dotnet run -- --build-anomaly-bank &lt;cleanImagesDir&gt; &lt;outputPath&gt;`.</summary>
    public string PatchCoreMemoryBankPath { get; set; } = "weights/patchcore_memory_bank.bin";

    /// <summary>"patchcore" method only. A patch's nearest-neighbour
    /// distance (in the backbone's feature space) above this counts as
    /// anomalous. There's no universal correct value -- it depends on the
    /// backbone and how varied your own "clean" reference images are.
    /// This default (3.0) is NOT a guess -- it's empirically set from a
    /// real test: max clean-image score across 12 real test photos ranged
    /// 1.68-2.56 (zero false positives), while 3 synthetic foreign-object
    /// patches of different sizes/positions scored 3.19-4.18 (all 3
    /// correctly caught). Still, this was measured against THIS project's
    /// own test photos with THIS memory bank -- rebuild your memory bank
    /// from your own site's real clean scans (see BuildMemoryBank) and
    /// re-tune this threshold using ComputeAnomalyScores against your own
    /// clean/dirty examples before trusting it in production.</summary>
    public float PatchCoreAnomalyThreshold { get; set; } = 3.0f;

    // TEST-ONLY: forces every other pass to come back with one fake
    // detection, regardless of Method above -- lets the highlighted
    // dual-scan panel, Scan History's Threat/Denied rows, and the
    // Analytics threat count all be exercised end-to-end without a real
    // model. Leave false for anything resembling production.
    public bool SimulateForTesting { get; set; } = false;
}

public class PlateDetectorOptions
{
    public bool Enabled { get; set; } = true;
    public string ModelPath { get; set; } = "weights/plate_detector.onnx";
    public float Confidence { get; set; } = 0.4f;
}

public class PlateOcrOptions
{
    public bool Enabled { get; set; } = true;
    // ctc_backbone_and_head.onnx is a superset of the older
    // sar_backbone_and_encoder.onnx -- identical feat/attn_key/holistic_feat
    // outputs (same names), plus ctc_logits for the fast CTC crop-variant
    // search (OnnxCtcOcr). One backbone file, one InferenceSession, shared
    // by both the SAR and CTC OCR paths -- the backbone only runs once.
    public string BackboneModelPath { get; set; } = "weights/ctc_backbone_and_head.onnx";
    public string DecoderModelPath { get; set; } = "weights/sar_decoder_step.onnx";
    public string EmbeddingPath { get; set; } = "weights/sar_embedding_99x512.bin";
    public string CharDictPath { get; set; } = "weights/en_dict.txt";

    // Multi-frame aggregation -- mirrors anpr-ai-service's
    // PLATE_TRACK_MAX_FRAMES / PLATE_OCR_AGGREGATION_* settings exactly
    // (same defaults) so the same voting behaviour applies here.
    public bool AggregationEnabled { get; set; } = true;
    public int TrackMaxFrames { get; set; } = 40;
    public int AggregationWindowSize { get; set; } = 10;
    public int AggregationMinCandidates { get; set; } = 7;
}

public class StorageOptions
{
    public bool Enabled { get; set; } = true;
    public string DriverImageDir { get; set; } = "captured_driver";
    public string AnprImageDir { get; set; } = "captured_anpr";
    public string UndervehicleImageDir { get; set; } = "captured_undervehicle";
}
