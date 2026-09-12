"""Trains a YOLOv8n foreign-object detector on the SYNTHETIC dataset in
this folder (see generate_synthetic_dataset.py for why it's synthetic --
pipeline verification only, not a real detector) and exports it to ONNX.

All output (runs/, the exported .onnx) is written under this folder --
nothing in anpr-ai-service is read from except its Python venv's already-
installed ultralytics/torch, and nothing there is written to.
"""
import os
import shutil

from ultralytics import YOLO

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
DATA_YAML = os.path.join(SCRIPT_DIR, "data.yaml")
RUNS_DIR = os.path.join(SCRIPT_DIR, "runs")
RUN_NAME = "foreign_object_synthetic"
WEIGHTS_OUT = os.path.normpath(
    os.path.join(SCRIPT_DIR, "..", "..", "src", "UvssService", "weights", "foreign_object_detector.onnx")
)


def main() -> None:
    model = YOLO("yolov8n.pt")
    model.train(
        data=DATA_YAML,
        epochs=40,
        imgsz=640,
        batch=16,
        project=RUNS_DIR,
        name=RUN_NAME,
        device="cpu",
        exist_ok=True,
        verbose=True,
    )

    exported_path = model.export(format="onnx")
    os.makedirs(os.path.dirname(WEIGHTS_OUT), exist_ok=True)
    shutil.copyfile(exported_path, WEIGHTS_OUT)
    print(f"Exported ONNX model copied to: {WEIGHTS_OUT}")


if __name__ == "__main__":
    main()
