"""Build the final, dynamic-width production ONNX files and save them."""
import numpy as np
import onnx
from onnx import helper, TensorProto

from build_backbone import build_backbone_nodes
from build_encoder import build_encoder_nodes
from build_decoder_step import build_model as build_decoder_model

WEIGHTS_NPZ = r"D:\bhikaji\bhikajianpr\uvss-service\model_conversion\weights_extracted\weights.npz"
OUT_DIR = r"D:\bhikaji\bhikajianpr\uvss-service\model_conversion\output"


def make_dynamic_input(name, shape_with_dimparams):
    """shape_with_dimparams: list where a string entry becomes a dim_param."""
    tp = onnx.TypeProto()
    tp.tensor_type.elem_type = TensorProto.FLOAT
    for d in shape_with_dimparams:
        dim = tp.tensor_type.shape.dim.add()
        if isinstance(d, str):
            dim.dim_param = d
        else:
            dim.dim_value = d
    return onnx.ValueInfoProto(name=name, type=tp)


if __name__ == "__main__":
    w = dict(np.load(WEIGHTS_NPZ))

    # --- Graph A: backbone + encoder ---
    bb_nodes, bb_inits, feat_name = build_backbone_nodes(w)
    enc_nodes, enc_inits, attn_key, rnn_out, holistic = build_encoder_nodes(w, feat_name)
    nodes = bb_nodes + enc_nodes
    inits = bb_inits + enc_inits

    inp = make_dynamic_input("input", [1, 3, 48, "width"])
    vr_inp = make_dynamic_input("valid_ratio", [1])
    out_feat = make_dynamic_input(feat_name, [1, 512, 1, "feat_width"])
    out_attn_key = make_dynamic_input(attn_key, [1, 512, 1, "feat_width"])
    out_holistic = make_dynamic_input(holistic, [1, 512])

    graph = helper.make_graph(nodes, "sar_backbone_and_encoder", [inp, vr_inp],
                               [out_feat, out_attn_key, out_holistic], initializer=inits)
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)])
    model.ir_version = 8
    path_a = f"{OUT_DIR}\\sar_backbone_and_encoder.onnx"
    onnx.save(model, path_a)
    print("Saved", path_a)

    # sanity: reload and run once
    import onnxruntime as ort
    sess = ort.InferenceSession(path_a, providers=["CPUExecutionProvider"])
    x = np.random.randn(1, 3, 48, 200).astype(np.float32)
    vr = np.array([0.5], dtype=np.float32)
    outs = sess.run(None, {"input": x, "valid_ratio": vr})
    print("Graph A dynamic-width smoke test OK, shapes:", [o.shape for o in outs])

    # --- Graph B: decoder step (already dynamic W via None dims) ---
    dec_model = build_decoder_model(w)
    path_b = f"{OUT_DIR}\\sar_decoder_step.onnx"
    onnx.save(dec_model, path_b)
    print("Saved", path_b)

    sess_b = ort.InferenceSession(path_b, providers=["CPUExecutionProvider"])
    feat, attn_key_v, holistic_v = outs
    h0 = c0 = h1 = c1 = np.zeros((1, 512), dtype=np.float32)
    out_b = sess_b.run(None, {
        "prev_embedding": holistic_v.astype(np.float32),
        "h0_in": h0, "c0_in": c0, "h1_in": h1, "c1_in": c1,
        "feat": feat, "attn_key": attn_key_v, "holistic_feat": holistic_v, "valid_ratio": vr,
    })
    print("Graph B dynamic-width smoke test OK, logits shape:", out_b[0].shape)
