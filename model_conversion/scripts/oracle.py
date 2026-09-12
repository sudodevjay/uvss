"""Ground-truth harness: runs the real Paddle SAR model exactly like
sar_refiner.py does, but dumps every intermediate tensor we need to diff
against the ONNX port. Read-only against anpr-ai-service.
"""
import math
import os
import sys

import cv2
import numpy as np

ANPR_DIR = r"D:\bhikaji\bhikajianpr\anpr-ai-service"
OUT_DIR = r"D:\bhikaji\bhikajianpr\uvss-service\model_conversion"
sys.path.insert(0, os.path.join(ANPR_DIR, "vendor"))
os.chdir(ANPR_DIR)  # ppocr postprocess sometimes expects relative dict paths

import paddle
from ppocr.modeling.architectures import build_model
from ppocr.postprocess import build_post_process

IMG_H = 48
IMG_W = 320

ARCHITECTURE_CONFIG = {
    "model_type": "rec",
    "algorithm": "SVTR_LCNet",
    "Transform": None,
    "Backbone": {
        "name": "MobileNetV1Enhance",
        "scale": 0.5,
        "last_conv_stride": [1, 2],
        "last_pool_type": "avg",
        "last_pool_kernel_size": [2, 2],
    },
    "Head": {
        "name": "MultiHead",
        "head_list": [
            {
                "CTCHead": {
                    "Neck": {"name": "svtr", "dims": 64, "depth": 2, "hidden_dims": 120, "use_guide": True},
                    "Head": {"fc_decay": 0.00001},
                }
            },
            {"SARHead": {"enc_dim": 512, "max_text_length": 25}},
        ],
    },
}


def build():
    char_dict_path = os.path.join(ANPR_DIR, "weights", "en_dict.txt")
    global_config = {
        "character_dict_path": char_dict_path,
        "use_space_char": True,
        "max_text_length": 25,
    }
    ctc_post = build_post_process({"name": "CTCLabelDecode"}, global_config)
    char_num = len(ctc_post.character)
    arch_config = dict(ARCHITECTURE_CONFIG)
    arch_config["Head"] = dict(arch_config["Head"])
    arch_config["Head"]["out_channels_list"] = {
        "CTCLabelDecode": char_num,
        "SARLabelDecode": char_num + 2,
    }
    model = build_model(arch_config)
    state_dict = paddle.load(os.path.join(ANPR_DIR, "weights", "indian_plate_rec_v2_sar_best_accuracy.pdparams"))
    model.set_state_dict(state_dict)
    model.eval()
    sar_post = build_post_process({"name": "SARLabelDecode"}, global_config)
    return model, sar_post, char_num


def resize_norm(img):
    h, w = img.shape[:2]
    ratio = w / float(h)
    max_wh_ratio = max(IMG_W / float(IMG_H), ratio)
    img_w = int(IMG_H * max_wh_ratio)
    resized_w = img_w if math.ceil(IMG_H * ratio) > img_w else math.ceil(IMG_H * ratio)
    resized = cv2.resize(img, (resized_w, IMG_H))
    resized = resized.astype("float32").transpose((2, 0, 1)) / 255.0
    resized -= 0.5
    resized /= 0.5
    padded = np.zeros((3, IMG_H, img_w), dtype=np.float32)
    padded[:, :, :resized_w] = resized
    valid_ratio = min(1.0, resized_w / img_w)
    return padded, valid_ratio


def run_one(model, sar_post, img_path):
    img = cv2.imread(img_path)
    assert img is not None, img_path
    padded, valid_ratio = resize_norm(img)
    x = paddle.to_tensor(padded[np.newaxis, :])
    vr = paddle.to_tensor(np.array([valid_ratio], dtype="float32"))

    dump = {"valid_ratio": np.array([valid_ratio], dtype="float32"), "input": padded[np.newaxis, :]}

    with paddle.no_grad():
        feat = model.backbone(x)
        dump["feat"] = feat.numpy()

        sar_head = model.head.sar_head
        # Replicate SAREncoder forward to capture holistic_feat.
        encoder = sar_head.encoder
        h_feat = feat.shape[2]
        if h_feat > 1:
            feat_v = paddle.nn.functional.max_pool2d(feat, kernel_size=(h_feat, 1), stride=1)
        else:
            feat_v = feat
        feat_v = feat_v.squeeze(2)  # [N,C,W]
        feat_v = paddle.transpose(feat_v, perm=[0, 2, 1])  # [N,W,C]
        holistic_feat_seq = encoder.rnn_encoder(feat_v)[0] if encoder.rnn_encoder is not None else feat_v
        dump["rnn_encoder_out"] = holistic_feat_seq.numpy()

        valid_hf = []
        T = holistic_feat_seq.shape[1]
        valid_step = min(T, math.ceil(valid_ratio * T)) - 1
        holistic_feat = holistic_feat_seq[:, valid_step, :]
        holistic_feat = encoder.linear(holistic_feat)
        dump["holistic_feat"] = holistic_feat.numpy()

        # attn_key precompute
        decoder = sar_head.decoder
        attn_key = decoder.conv3x3_1(feat)
        dump["attn_key"] = attn_key.numpy()

        # Full autoregressive forward_test via the real module, capturing per-step logits.
        sar_out = sar_head(feat, targets=[[""], vr])
        dump["sar_out"] = sar_out.numpy()

    text, confidence = sar_post(sar_out.numpy())[0]
    dump["decoded_text"] = text
    dump["decoded_confidence"] = confidence
    return dump


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
        d = run_one(model, sar_post, p)
        print(name, "->", d["decoded_text"], f"(conf={d['decoded_confidence']:.3f})")
        results[name] = d

    np.savez(os.path.join(OUT_DIR, "weights_extracted", "oracle_dump.npz"),
              **{f"{k}__{kk}": vv for k, v in results.items() for kk, vv in v.items() if kk != "decoded_text"})
    with open(os.path.join(OUT_DIR, "weights_extracted", "oracle_texts.txt"), "w") as f:
        for k, v in results.items():
            f.write(f"{k}\t{v['decoded_text']}\t{v['decoded_confidence']}\n")
    print("Saved oracle dump.")
