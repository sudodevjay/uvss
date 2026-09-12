"""Builds the final, dynamic-width CTC+SVTR-neck ONNX file. Combines the
SAME backbone+SAR-encoder nodes save_final.py already emits into
sar_backbone_and_encoder.onnx, PLUS the SVTR neck + CTC head, into ONE
graph -- so the backbone only runs once per crop and C# gets feat/attn_key/
holistic_feat/ctc_logits from a single InferenceSession.Run call, instead of
needing to run backbone twice (once via sar_backbone_and_encoder.onnx, once
via a hypothetical separate ctc-only graph).

Saved under a NEW filename (ctc_backbone_and_head.onnx) -- does NOT
overwrite the existing sar_backbone_and_encoder.onnx, in case anything
already depends on that file's exact current input/output signature.
"""
import numpy as np
import onnx
from onnx import helper, TensorProto

from build_backbone import build_backbone_nodes
from build_encoder import build_encoder_nodes
from build_ctc import build_ctc_nodes
from save_final import make_dynamic_input

WEIGHTS_NPZ = r"D:\bhikaji\bhikajianpr\uvss-service\model_conversion\weights_extracted\weights.npz"
OUT_DIR = r"D:\bhikaji\bhikajianpr\uvss-service\model_conversion\output"


if __name__ == "__main__":
    w = dict(np.load(WEIGHTS_NPZ))

    bb_nodes, bb_inits, feat_name = build_backbone_nodes(w)
    enc_nodes, enc_inits, attn_key, rnn_out, holistic = build_encoder_nodes(w, feat_name)
    ctc_nodes, ctc_inits, ctc_out = build_ctc_nodes(w, feat_name)

    nodes = bb_nodes + enc_nodes + ctc_nodes
    inits = bb_inits + enc_inits + ctc_inits

    inp = make_dynamic_input("input", [1, 3, 48, "width"])
    vr_inp = make_dynamic_input("valid_ratio", [1])
    out_feat = make_dynamic_input(feat_name, [1, 512, 1, "feat_width"])
    out_attn_key = make_dynamic_input(attn_key, [1, 512, 1, "feat_width"])
    out_holistic = make_dynamic_input(holistic, [1, 512])
    out_ctc = make_dynamic_input(ctc_out, [1, "feat_width", 97])

    graph = helper.make_graph(
        nodes, "ctc_backbone_and_head", [inp, vr_inp],
        [out_feat, out_attn_key, out_holistic, out_ctc], initializer=inits)
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)])
    model.ir_version = 8

    path = f"{OUT_DIR}\\ctc_backbone_and_head.onnx"
    onnx.save(model, path)
    print("Saved", path)

    import onnxruntime as ort
    sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
    x = np.random.randn(1, 3, 48, 200).astype(np.float32)
    vr = np.array([0.5], dtype=np.float32)
    outs = sess.run(None, {"input": x, "valid_ratio": vr})
    print("Dynamic-width smoke test OK, shapes:", [o.shape for o in outs])
    print("Output order:", [o.name for o in sess.get_outputs()])
