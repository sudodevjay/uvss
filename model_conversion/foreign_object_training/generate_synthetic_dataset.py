"""SYNTHETIC dataset generator -- pipeline verification only, NOT a real
foreign-object dataset. There is no real UVSS imagery of actual threats
anywhere in this project (or available to this script), so this exists
purely to prove the training -> ONNX export -> C# inference plumbing works
end-to-end. The resulting model has ZERO real-world detection capability
and must be replaced once genuine labeled data exists (see the training
process explained alongside this script).

Background: the one real chassis photo already in this project
(uvss_test_frame.jpg, extracted earlier from the reference UVSS video),
randomly cropped/flipped/brightness-jittered per sample for variety.

"Foreign object": a synthetic textured patch (random position/size/rotation)
painted onto the background -- a stand-in for "something that doesn't
belong under this vehicle", written out in YOLO label format
(class cx cy w h, normalized).
"""
import os
import random

import cv2
import numpy as np

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
BACKGROUND_PATH = os.path.normpath(os.path.join(SCRIPT_DIR, "..", "uvss_test_frame.jpg"))
OUT_DIR = SCRIPT_DIR
IMG_SIZE = 640
N_TRAIN = 240
N_VAL = 60
SEED = 42


def make_background(src: np.ndarray) -> np.ndarray:
    h, w = src.shape[:2]
    # Random square-ish crop from the source photo, then resize to IMG_SIZE.
    crop_size = random.randint(int(min(h, w) * 0.6), min(h, w))
    y0 = random.randint(0, h - crop_size)
    x0 = random.randint(0, w - crop_size)
    crop = src[y0 : y0 + crop_size, x0 : x0 + crop_size]
    img = cv2.resize(crop, (IMG_SIZE, IMG_SIZE))
    if random.random() < 0.5:
        img = cv2.flip(img, 1)
    # Brightness/contrast jitter so samples aren't near-duplicates.
    alpha = random.uniform(0.8, 1.2)
    beta = random.uniform(-25, 25)
    img = cv2.convertScaleAbs(img, alpha=alpha, beta=beta)
    return img


def paint_foreign_object(img: np.ndarray) -> tuple[np.ndarray, tuple[float, float, float, float]]:
    h, w = img.shape[:2]
    box_w = random.randint(int(w * 0.08), int(w * 0.22))
    box_h = random.randint(int(h * 0.06), int(h * 0.16))
    cx = random.randint(box_w, w - box_w)
    cy = random.randint(box_h, h - box_h)

    overlay = img.copy()
    # A shape + color/texture that stands out against the grey/metallic
    # chassis tones -- bright, saturated, with a bit of noise so it isn't a
    # flat color block a model could trivially key on by exact RGB alone.
    color = random.choice([(20, 60, 220), (30, 160, 230), (40, 200, 60)])  # BGR: red/orange/green-ish
    shape = random.choice(["rect", "ellipse"])
    x1, y1 = cx - box_w // 2, cy - box_h // 2
    x2, y2 = cx + box_w // 2, cy + box_h // 2
    if shape == "rect":
        cv2.rectangle(overlay, (x1, y1), (x2, y2), color, thickness=-1)
    else:
        cv2.ellipse(overlay, (cx, cy), (box_w // 2, box_h // 2), 0, 0, 360, color, thickness=-1)
    noise = np.random.randint(-25, 25, overlay.shape, dtype=np.int16)
    overlay = np.clip(overlay.astype(np.int16) + noise, 0, 255).astype(np.uint8)

    blended = img.copy()
    mask = np.zeros((h, w), dtype=np.uint8)
    if shape == "rect":
        cv2.rectangle(mask, (x1, y1), (x2, y2), 255, thickness=-1)
    else:
        cv2.ellipse(mask, (cx, cy), (box_w // 2, box_h // 2), 0, 0, 360, 255, thickness=-1)
    mask3 = cv2.merge([mask, mask, mask]).astype(np.float32) / 255.0
    blended = (blended.astype(np.float32) * (1 - mask3) + overlay.astype(np.float32) * mask3).astype(np.uint8)

    yolo_box = (cx / w, cy / h, box_w / w, box_h / h)
    return blended, yolo_box


def generate_split(background: np.ndarray, split: str, count: int) -> None:
    img_dir = os.path.join(OUT_DIR, "images", split)
    label_dir = os.path.join(OUT_DIR, "labels", split)
    for i in range(count):
        img = make_background(background)
        img, (cx, cy, bw, bh) = paint_foreign_object(img)
        name = f"synth_{split}_{i:04d}"
        cv2.imwrite(os.path.join(img_dir, f"{name}.jpg"), img)
        with open(os.path.join(label_dir, f"{name}.txt"), "w") as f:
            f.write(f"0 {cx:.6f} {cy:.6f} {bw:.6f} {bh:.6f}\n")


def main() -> None:
    random.seed(SEED)
    np.random.seed(SEED)
    background = cv2.imread(BACKGROUND_PATH)
    if background is None:
        raise SystemExit(f"Could not read background image: {BACKGROUND_PATH}")

    generate_split(background, "train", N_TRAIN)
    generate_split(background, "val", N_VAL)
    print(f"Wrote {N_TRAIN} train + {N_VAL} val synthetic samples to {OUT_DIR}")


if __name__ == "__main__":
    main()
