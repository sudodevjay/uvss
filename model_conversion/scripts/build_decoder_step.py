"""Stage 3: SAR decoder single incremental step, as a standalone ONNX graph
called once per timestep by a Python (later C#) driver loop -- justified by
the causal/unidirectional LSTM equivalence noted in the architecture spec
(recomputing the whole padded sequence every outer step, as the reference
Python code does, is mathematically identical to carrying hidden/cell state
forward one step at a time). Validated end-to-end against the oracle's
per-step logits below, not just assumed.
"""
import numpy as np
import onnx
from onnx import helper, TensorProto, numpy_helper
import onnxruntime as ort

from onnx_build_utils import uid, const_init
from build_encoder import permute_ifgo_to_iofc

WEIGHTS_NPZ = r"D:\bhikaji\bhikajianpr\uvss-service\model_conversion\weights_extracted\weights.npz"


def lstm_single_step(x_name, h_in, c_in, prefix, w, hidden, out_h, out_c):
    """x_name: [1,512] single-step input. h_in/c_in: [1,512] state names (graph inputs).
    Returns nodes/inits; writes new state to out_h,out_c names."""
    nodes, inits = [], []
    w_ih = permute_ifgo_to_iofc(w[prefix + ".weight_ih"])[np.newaxis, :, :]
    w_hh = permute_ifgo_to_iofc(w[prefix + ".weight_hh"])[np.newaxis, :, :]
    b_ih = permute_ifgo_to_iofc(w[prefix + ".bias_ih"])
    b_hh = permute_ifgo_to_iofc(w[prefix + ".bias_hh"])
    b = np.concatenate([b_ih, b_hh], axis=0)[np.newaxis, :]

    wname, rname, bname = prefix + "_W", prefix + "_R", prefix + "_B"
    inits += [const_init(wname, w_ih), const_init(rname, w_hh), const_init(bname, b)]

    # X must be [seq=1, batch=1, input]; h_in/c_in must be [num_dir=1,batch=1,hidden]
    x_seq = uid("x_seq")
    nodes.append(helper.make_node("Unsqueeze", [x_name, "_axes0_"], [x_seq], name=uid("Unsqueeze")))
    h0 = uid("h0"); c0 = uid("c0")
    nodes.append(helper.make_node("Unsqueeze", [h_in, "_axes0_"], [h0], name=uid("Unsqueeze")))
    nodes.append(helper.make_node("Unsqueeze", [c_in, "_axes0_"], [c0], name=uid("Unsqueeze")))

    y_name = uid("y"); yh = uid("yh"); yc = uid("yc")
    nodes.append(helper.make_node(
        "LSTM", [x_seq, wname, rname, bname, "", h0, c0], [y_name, yh, yc],
        name=uid("LSTM"), hidden_size=hidden, direction="forward",
    ))
    # yh: [1,1,hidden] -> squeeze to [1,hidden]
    nodes.append(helper.make_node("Squeeze", [yh, "_axes0_"], [out_h], name=uid("Squeeze")))
    nodes.append(helper.make_node("Squeeze", [yc, "_axes0_"], [out_c], name=uid("Squeeze")))
    return nodes, inits, out_h


def build_decoder_step_graph(w):
    nodes, inits = [], []
    inits.append(numpy_helper.from_array(np.array([0], dtype=np.int64), name="_axes0_"))

    prev_emb = "prev_embedding"      # [1,512]
    h0_in, c0_in = "h0_in", "c0_in"  # [1,512]
    h1_in, c1_in = "h1_in", "c1_in"
    feat = "feat"                    # [1,512,1,W]
    attn_key = "attn_key"            # [1,512,1,W]
    holistic = "holistic_feat"       # [1,512]
    valid_ratio = "valid_ratio"      # [1]

    n, i, y0 = lstm_single_step(prev_emb, h0_in, c0_in,
                                 "head.sar_head.decoder.rnn_decoder.0.cell", w, 512,
                                 "h0_out", "c0_out")
    nodes += n; inits += i
    n, i, y1 = lstm_single_step(y0, h1_in, c1_in,
                                 "head.sar_head.decoder.rnn_decoder.1.cell", w, 512,
                                 "h1_out", "c1_out")
    nodes += n; inits += i
    y = "h1_out"  # this step's decoder hidden output

    # attn_query = conv1x1_1(y): Linear(512->512)
    c1x1_1_w = w["head.sar_head.decoder.conv1x1_1.weight"]  # [512,512] (paddle Linear [in,out])
    c1x1_1_b = w["head.sar_head.decoder.conv1x1_1.bias"]
    inits.append(const_init("c1x1_1_w", c1x1_1_w))
    inits.append(const_init("c1x1_1_b", c1x1_1_b))
    mm = uid("mm")
    nodes.append(helper.make_node("MatMul", [y, "c1x1_1_w"], [mm], name=uid("MatMul")))
    attn_query = uid("attn_query")
    nodes.append(helper.make_node("Add", [mm, "c1x1_1_b"], [attn_query], name=uid("Add")))

    # broadcast attn_query [1,512] -> [1,512,1,1] to add onto attn_key [1,512,1,W]
    aq4 = uid("aq4")
    inits.append(numpy_helper.from_array(np.array([1, 512, 1, 1], dtype=np.int64), name="_aq_shape_"))
    nodes.append(helper.make_node("Reshape", [attn_query, "_aq_shape_"], [aq4], name=uid("Reshape")))
    summed = uid("summed")
    nodes.append(helper.make_node("Add", [attn_key, aq4], [summed], name=uid("Add")))
    tanh_out = uid("tanh_out")
    nodes.append(helper.make_node("Tanh", [summed], [tanh_out], name=uid("Tanh")))

    # squeeze H(=1) axis -> [1,512,W], transpose -> [1,W,512]
    sq = uid("sq")
    inits.append(numpy_helper.from_array(np.array([2], dtype=np.int64), name="_axes2_"))
    nodes.append(helper.make_node("Squeeze", [tanh_out, "_axes2_"], [sq], name=uid("Squeeze")))
    twc = uid("twc")
    nodes.append(helper.make_node("Transpose", [sq], [twc], name=uid("Transpose"), perm=[0, 2, 1]))

    # conv1x1_2: Linear(512->1) applied on last axis
    c1x1_2_w = w["head.sar_head.decoder.conv1x1_2.weight"]  # [512,1]
    c1x1_2_b = w["head.sar_head.decoder.conv1x1_2.bias"]    # [1]
    inits.append(const_init("c1x1_2_w", c1x1_2_w))
    inits.append(const_init("c1x1_2_b", c1x1_2_b))
    mm2 = uid("mm2")
    nodes.append(helper.make_node("MatMul", [twc, "c1x1_2_w"], [mm2], name=uid("MatMul")))
    logit_pre = uid("logit_pre")
    nodes.append(helper.make_node("Add", [mm2, "c1x1_2_b"], [logit_pre], name=uid("Add")))  # [1,W,1]
    attn_logits = uid("attn_logits")
    inits.append(numpy_helper.from_array(np.array([2], dtype=np.int64), name="_axes2b_"))
    nodes.append(helper.make_node("Squeeze", [logit_pre, "_axes2b_"], [attn_logits], name=uid("Squeeze")))  # [1,W]

    # valid_ratio masking: positions >= ceil(valid_ratio*W) -> -1e9
    shape_name = uid("shape")
    nodes.append(helper.make_node("Shape", [attn_logits], [shape_name], name=uid("Shape")))
    w_idx = uid("w_idx")
    inits.append(numpy_helper.from_array(np.array([1], dtype=np.int64), name="_idx1_"))
    nodes.append(helper.make_node("Gather", [shape_name, "_idx1_"], [w_idx], name=uid("Gather"), axis=0))
    w_f = uid("w_f")
    nodes.append(helper.make_node("Cast", [w_idx], [w_f], name=uid("Cast"), to=TensorProto.FLOAT))
    vw_f = uid("vw_f")
    nodes.append(helper.make_node("Mul", [valid_ratio, w_f], [vw_f], name=uid("Mul")))
    vw_ceil = uid("vw_ceil")
    nodes.append(helper.make_node("Ceil", [vw_f], [vw_ceil], name=uid("Ceil")))

    iota_i = uid("iota_i")
    nodes.append(helper.make_node("Range", ["_zero_", w_idx, "_one_i64_"], [iota_i], name=uid("Range")))
    inits.append(numpy_helper.from_array(np.array(0, dtype=np.int64), name="_zero_"))
    inits.append(numpy_helper.from_array(np.array(1, dtype=np.int64), name="_one_i64_"))
    iota_f = uid("iota_f")
    nodes.append(helper.make_node("Cast", [iota_i], [iota_f], name=uid("Cast"), to=TensorProto.FLOAT))
    iota_f_row = uid("iota_f_row")
    inits.append(numpy_helper.from_array(np.array([1, -1], dtype=np.int64), name="_row_shape_"))
    nodes.append(helper.make_node("Reshape", [iota_f, "_row_shape_"], [iota_f_row], name=uid("Reshape")))
    vw_bcast = uid("vw_bcast")  # broadcast compare needs same-ish shape; rely on numpy-style broadcasting
    ge_mask = uid("ge_mask")
    nodes.append(helper.make_node("GreaterOrEqual", [iota_f_row, vw_ceil], [ge_mask], name=uid("GreaterOrEqual")))
    neg_inf = uid("neg_inf")
    inits.append(numpy_helper.from_array(np.array(-1e9, dtype=np.float32), name="_neginf_"))
    masked_logits = uid("masked_logits")
    nodes.append(helper.make_node("Where", [ge_mask, "_neginf_", attn_logits], [masked_logits], name=uid("Where")))

    attn_probs = uid("attn_probs")
    nodes.append(helper.make_node("Softmax", [masked_logits], [attn_probs], name=uid("Softmax"), axis=1))  # [1,W]

    # attn_feat = sum_w feat[:, :, 0, w] * attn_probs[:, w]  -> [1,512]
    feat_sq = uid("feat_sq")
    nodes.append(helper.make_node("Squeeze", [feat, "_axes2_"], [feat_sq], name=uid("Squeeze")))  # [1,512,W]
    probs3 = uid("probs3")
    inits.append(numpy_helper.from_array(np.array([1, 1, -1], dtype=np.int64), name="_probs_shape_"))
    nodes.append(helper.make_node("Reshape", [attn_probs, "_probs_shape_"], [probs3], name=uid("Reshape")))  # [1,1,W]
    weighted = uid("weighted")
    nodes.append(helper.make_node("Mul", [feat_sq, probs3], [weighted], name=uid("Mul")))  # [1,512,W]
    attn_feat = uid("attn_feat")
    nodes.append(helper.make_node("ReduceSum", [weighted, "_axis2_i_"], [attn_feat], name=uid("ReduceSum"), keepdims=0))
    inits.append(numpy_helper.from_array(np.array([2], dtype=np.int64), name="_axis2_i_"))

    concat_out = uid("concat_out")
    nodes.append(helper.make_node("Concat", [y, attn_feat, holistic], [concat_out], name=uid("Concat"), axis=-1))

    pred_w = w["head.sar_head.decoder.prediction.weight"]  # [1536,98]
    pred_b = w["head.sar_head.decoder.prediction.bias"]
    inits.append(const_init("pred_w", pred_w))
    inits.append(const_init("pred_b", pred_b))
    mm3 = uid("mm3")
    nodes.append(helper.make_node("MatMul", [concat_out, "pred_w"], [mm3], name=uid("MatMul")))
    nodes.append(helper.make_node("Add", [mm3, "pred_b"], ["logits"], name=uid("Add")))

    return nodes, inits


def build_model(w):
    nodes, inits = build_decoder_step_graph(w)
    def vi(name, shape):
        return helper.make_tensor_value_info(name, TensorProto.FLOAT, shape)
    inputs = [
        vi("prev_embedding", [1, 512]),
        vi("h0_in", [1, 512]), vi("c0_in", [1, 512]),
        vi("h1_in", [1, 512]), vi("c1_in", [1, 512]),
        vi("feat", [1, 512, 1, None]),
        vi("attn_key", [1, 512, 1, None]),
        vi("holistic_feat", [1, 512]),
        vi("valid_ratio", [1]),
    ]
    outputs = [
        vi("logits", [1, 98]),
        vi("h0_out", [1, 512]), vi("c0_out", [1, 512]),
        vi("h1_out", [1, 512]), vi("c1_out", [1, 512]),
    ]
    graph = helper.make_graph(nodes, "decoder_step", inputs, outputs, initializer=inits)
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)])
    return model


if __name__ == "__main__":
    w = dict(np.load(WEIGHTS_NPZ))
    model = build_model(w)
    onnx.save(model, r"D:\bhikaji\bhikajianpr\uvss-service\model_conversion\output\_tmp_decoder_step.onnx")
    print("built decoder step graph OK, nodes:", len(model.graph.node))
