"""Dumps ground-truth ctc_logits (post-softmax, eval-mode) from the real
Paddle model's actual MultiHead.forward call chain for svtr encoder_type:
model.head.ctc_encoder(feat) -> model.head.ctc_head(ctc_encoder_out) with
targets=None (eval branch applies softmax, rec_ctc_head.py:100-117).
Read-only against anpr-ai-service.
"""
import os
import numpy as np

from oracle import build, resize_norm, ANPR_DIR, OUT_DIR

import paddle


def run_one(model, img_path):
    import cv2
    img = cv2.imread(img_path)
    assert img is not None, img_path
    padded, valid_ratio = resize_norm(img)
    x = paddle.to_tensor(padded[np.newaxis, :])

    with paddle.no_grad():
        feat = model.backbone(x)
        ctc_encoder_out = model.head.ctc_encoder(feat)
        ctc_logits = model.head.ctc_head(ctc_encoder_out, targets=None)

    return {"input": padded[np.newaxis, :], "ctc_logits": ctc_logits.numpy()}


if __name__ == "__main__":
    import glob
    model, sar_post, char_num = build()
    print("char_num (CTC classes incl blank):", char_num)

    crops_dir = os.path.join(ANPR_DIR, "ocr_training", "release", "crops")
    all_crops = sorted(glob.glob(os.path.join(crops_dir, "*.jpg")))
    picks = all_crops[::max(1, len(all_crops) // 15)][:15]
    print("Testing", len(picks), "crops")

    results = {}
    for p in picks:
        name = os.path.basename(p)
        d = run_one(model, p)
        argmax_preview = d["ctc_logits"][0].argmax(axis=-1)
        print(name, "ctc_logits shape", d["ctc_logits"].shape, "argmax[:20]", argmax_preview[:20])
        results[name] = d

    np.savez(os.path.join(OUT_DIR, "weights_extracted", "ctc_oracle_dump.npz"),
              **{f"{k}__{kk}": vv for k, v in results.items() for kk, vv in v.items()})
    print("Saved CTC oracle dump.")
