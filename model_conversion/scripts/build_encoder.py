"""Stage 2: SAR encoder (2-layer unidirectional LSTM + valid-step gather +
linear) and the loop-invariant attn_key = conv3x3_1(feat). Validates against
oracle's rnn_encoder_out / holistic_feat / attn_key.

Gate-order note: empirically verified (separate controlled test) that
paddle.nn.LSTM's weight_ih/weight_hh rows are in [i,f,g,o] order (g = cell
candidate, ONNX calls it "c"). ONNX's LSTM op expects [i,o,f,c]. We permute
rows once here rather than reimplementing LSTM from primitive ops.
"""
import numpy as np
import onnx
from onnx import helper, TensorProto, numpy_helper
import onnxruntime as ort

from onnx_build_utils import conv2d, uid, const_init

WEIGHTS_NPZ = r"D:\bhikaji\bhikajianpr\uvss-service\model_conversion\weights_extracted\weights.npz"


def permute_ifgo_to_iofc(arr):
    H = arr.shape[0] // 4
    i, f, g, o = arr[0:H], arr[H:2*H], arr[2*H:3*H], arr[3*H:4*H]
    return np.concatenate([i, o, f, g], axis=0)


def lstm_layer(x_seq_name, prefix, w, hidden, out_name=None):
    """x_seq_name: [seq_len, 1, input_size]. Returns Y squeezed to [seq_len,1,hidden]."""
    nodes, inits = [], []
    w_ih = permute_ifgo_to_iofc(w[prefix + ".weight_ih"])[np.newaxis, :, :]
    w_hh = permute_ifgo_to_iofc(w[prefix + ".weight_hh"])[np.newaxis, :, :]
    b_ih = permute_ifgo_to_iofc(w[prefix + ".bias_ih"])
    b_hh = permute_ifgo_to_iofc(w[prefix + ".bias_hh"])
    b = np.concatenate([b_ih, b_hh], axis=0)[np.newaxis, :]

    wname, rname, bname = prefix + "_W", prefix + "_R", prefix + "_B"
    inits += [const_init(wname, w_ih), const_init(rname, w_hh), const_init(bname, b)]

    y_name = uid("lstm_Y")
    yh_name = uid("lstm_Yh")
    yc_name = uid("lstm_Yc")
    nodes.append(helper.make_node(
        "LSTM", [x_seq_name, wname, rname, bname], [y_name, yh_name, yc_name],
        name=uid("LSTM"), hidden_size=hidden, direction="forward",
    ))
    # Y: [seq_len, num_directions=1, batch=1, hidden] -> squeeze axis=1
    out_name = out_name or uid("lstm_out")
    squeeze_axes_name = uid("axes")
    inits.append(numpy_helper.from_array(np.array([1], dtype=np.int64), name=squeeze_axes_name))
    nodes.append(helper.make_node("Squeeze", [y_name, squeeze_axes_name], [out_name], name=uid("Squeeze")))
    return nodes, inits, out_name


def build_encoder_nodes(w, feat_name="feat"):
    nodes, inits = [], []

    # attn_key = conv3x3_1(feat), 512->512, k3 pad1 stride1
    cw = w["head.sar_head.decoder.conv3x3_1.weight"]
    cb = w["head.sar_head.decoder.conv3x3_1.bias"]
    n, i, attn_key = conv2d(feat_name, "conv3x3_1_w", cw, cb, (1, 1), (1, 1), 1, out_name="attn_key")
    nodes += n; inits += i

    # feat [1,512,1,W] -> squeeze axis2 -> [1,512,W] -> transpose(0,2,1) -> [1,W,512]
    sq_axes = uid("axes")
    inits.append(numpy_helper.from_array(np.array([2], dtype=np.int64), name=sq_axes))
    feat_sq = uid("feat_sq")
    nodes.append(helper.make_node("Squeeze", [feat_name, sq_axes], [feat_sq], name=uid("Squeeze")))
    feat_ntc = uid("feat_ntc")  # [1, W, 512] (batch, time, ch) -- matches oracle feat_v layout
    nodes.append(helper.make_node("Transpose", [feat_sq], [feat_ntc], name=uid("Transpose"), perm=[0, 2, 1]))
    # -> [W, 1, 512] (seq, batch, input) for ONNX LSTM
    feat_seq = uid("feat_seq")
    nodes.append(helper.make_node("Transpose", [feat_ntc], [feat_seq], name=uid("Transpose"), perm=[1, 0, 2]))

    n, i, y0 = lstm_layer(feat_seq, "head.sar_head.encoder.rnn_encoder.0.cell", w, 512)
    nodes += n; inits += i
    n, i, y1 = lstm_layer(y0, "head.sar_head.encoder.rnn_encoder.1.cell", w, 512)
    nodes += n; inits += i
    # y1: [W,1,512] -> transpose(1,0,2) -> [1,W,512]  == rnn_encoder_out
    rnn_out = "rnn_encoder_out"
    nodes.append(helper.make_node("Transpose", [y1], [rnn_out], name=uid("Transpose"), perm=[1, 0, 2]))

    # valid_step = min(T, ceil(valid_ratio*T)) - 1
    shape_name = uid("shape")
    nodes.append(helper.make_node("Shape", [rnn_out], [shape_name], name=uid("Shape")))
    t_idx_name = uid("t_idx")
    inits.append(numpy_helper.from_array(np.array([1], dtype=np.int64), name=t_idx_name))
    t_i64 = uid("T_i64")
    nodes.append(helper.make_node("Gather", [shape_name, t_idx_name], [t_i64], name=uid("Gather"), axis=0))
    t_f = uid("T_f")
    nodes.append(helper.make_node("Cast", [t_i64], [t_f], name=uid("Cast"), to=TensorProto.FLOAT))
    vr_t = uid("vr_times_T")
    nodes.append(helper.make_node("Mul", ["valid_ratio", t_f], [vr_t], name=uid("Mul")))
    vr_ceil = uid("vr_ceil")
    nodes.append(helper.make_node("Ceil", [vr_t], [vr_ceil], name=uid("Ceil")))
    vr_ceil_i = uid("vr_ceil_i")
    nodes.append(helper.make_node("Cast", [vr_ceil], [vr_ceil_i], name=uid("Cast"), to=TensorProto.INT64))
    min_val = uid("min_val")
    nodes.append(helper.make_node("Min", [vr_ceil_i, t_i64], [min_val], name=uid("Min")))
    one_name = uid("one")
    inits.append(numpy_helper.from_array(np.array(1, dtype=np.int64), name=one_name))
    valid_step = uid("valid_step")
    nodes.append(helper.make_node("Sub", [min_val, one_name], [valid_step], name=uid("Sub")))
    # min_val/valid_step currently shape [1] (from Gather with 1-elem index); squeeze to scalar for Gather-as-index
    vs_scalar = uid("valid_step_scalar")
    sq_axes0 = uid("axes0")
    inits.append(numpy_helper.from_array(np.array([0], dtype=np.int64), name=sq_axes0))
    nodes.append(helper.make_node("Squeeze", [valid_step, sq_axes0], [vs_scalar], name=uid("Squeeze")))

    gathered = uid("gathered")
    nodes.append(helper.make_node("Gather", [rnn_out, vs_scalar], [gathered], name=uid("Gather"), axis=1))
    # gathered: data[1,T,512] rank3, indices scalar rank0 -> output rank2 [1,512]

    lw = w["head.sar_head.encoder.linear.weight"]  # [512,512] paddle Linear weight is [in,out]
    lb = w["head.sar_head.encoder.linear.bias"]
    lin_w_name = uid("enc_linear_w")
    inits.append(const_init(lin_w_name, lw))
    mm = uid("mm")
    nodes.append(helper.make_node("MatMul", [gathered, lin_w_name], [mm], name=uid("MatMul")))
    lin_b_name = uid("enc_linear_b")
    inits.append(const_init(lin_b_name, lb))
    nodes.append(helper.make_node("Add", [mm, lin_b_name], ["holistic_feat"], name=uid("Add")))

    return nodes, inits, attn_key, rnn_out, "holistic_feat"


if __name__ == "__main__":
    from build_backbone import build_backbone_nodes

    w = dict(np.load(WEIGHTS_NPZ))
    oracle = dict(np.load(r"D:\bhikaji\bhikajianpr\uvss-service\model_conversion\weights_extracted\oracle_dump.npz"))
    names = sorted(set(k.rsplit("__", 1)[0] for k in oracle if k.endswith("__input")))

    for name in names:
        x = oracle[f"{name}__input"].astype(np.float32)
        vr = oracle[f"{name}__valid_ratio"].astype(np.float32)
        width = x.shape[3]

        bb_nodes, bb_inits, feat_name = build_backbone_nodes(w)
        enc_nodes, enc_inits, attn_key, rnn_out, holistic = build_encoder_nodes(w, feat_name)

        nodes = bb_nodes + enc_nodes
        inits = bb_inits + enc_inits
        inp = helper.make_tensor_value_info("input", TensorProto.FLOAT, [1, 3, 48, width])
        vr_inp = helper.make_tensor_value_info("valid_ratio", TensorProto.FLOAT, [1])
        outs = [helper.make_tensor_value_info(n, TensorProto.FLOAT, None) for n in
                [feat_name, attn_key, rnn_out, holistic]]
        graph = helper.make_graph(nodes, "backbone_encoder", [inp, vr_inp], outs, initializer=inits)
        model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)])

        sess = ort.InferenceSession(model.SerializeToString(), providers=["CPUExecutionProvider"])
        got_feat, got_attn_key, got_rnn_out, got_holistic = sess.run(None, {"input": x, "valid_ratio": vr})

        exp_attn_key = oracle[f"{name}__attn_key"]
        exp_rnn_out = oracle[f"{name}__rnn_encoder_out"]
        exp_holistic = oracle[f"{name}__holistic_feat"]

        print(name)
        print("  attn_key   max diff:", np.abs(got_attn_key - exp_attn_key).max())
        print("  rnn_out    max diff:", np.abs(got_rnn_out - exp_rnn_out).max())
        print("  holistic   max diff:", np.abs(got_holistic - exp_holistic).max(),
              "shapes", got_holistic.shape, exp_holistic.shape)
