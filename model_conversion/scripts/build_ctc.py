"""Stage 3: SVTR neck + CTC head, built on top of the same `feat` tensor the
SAR graphs already use. Validates against oracle's ctc_logits (captured via
model.head.ctc_encoder + model.head.ctc_head, the real MultiHead.forward
call chain for the "svtr" encoder_type -- see rec_multi_head.py:139-140 and
rec_ctc_head.py:100-117 -- confirmed from source, not guessed).

Activation note: this neck uses plain Swish (x*sigmoid(x)), NOT the
backbone's hard_swish -- a real, easy-to-make mixup (see SAR_ONNX_NOTES.md's
existing gotchas section for the backbone/neck activation split).
"""
import numpy as np
import onnx
from onnx import helper, TensorProto, numpy_helper
import onnxruntime as ort

from onnx_build_utils import conv2d, batchnorm, swish, matmul_add, layernorm, softmax, uid, const_init, add

WEIGHTS_NPZ = r"D:\bhikaji\bhikajianpr\uvss-service\model_conversion\weights_extracted\weights.npz"

SCALE = 15 ** -0.5  # head_dim=15 (120/8 heads) -- matches the paddle2onnx PIR dump's literal 0.258199 seen earlier


def conv_bn_swish(x, w, conv_key, bn_key, pad, out_name=None):
    nodes, inits = [], []
    cw = w[conv_key]
    n, i, c = conv2d(x, conv_key, cw, None, (1, 1), pad, 1); nodes += n; inits += i
    n, i, b = batchnorm(c, bn_key, w); nodes += n; inits += i
    n, i, a = swish(b, out_name); nodes += n; inits += i
    return nodes, inits, a


def squeeze_h_transpose_to_ntc(x, out_name=None):
    """[N,C,1,W] -> squeeze axis2 -> [N,C,W] -> transpose(0,2,1) -> [N,W,C]."""
    nodes, inits = [], []
    axes_name = uid("axes")
    inits.append(numpy_helper.from_array(np.array([2], dtype=np.int64), name=axes_name))
    sq = uid("sq")
    nodes.append(helper.make_node("Squeeze", [x, axes_name], [sq], name=uid("Squeeze")))
    out_name = out_name or uid("ntc")
    nodes.append(helper.make_node("Transpose", [sq], [out_name], name=uid("Transpose"), perm=[0, 2, 1]))
    return nodes, inits, out_name


def unsqueeze_transpose_to_nc1w(x, out_name=None):
    """[N,W,C] -> unsqueeze axis1 -> [N,1,W,C] -> transpose(0,3,1,2) -> [N,C,1,W]."""
    nodes, inits = [], []
    axes_name = uid("axes")
    inits.append(numpy_helper.from_array(np.array([1], dtype=np.int64), name=axes_name))
    un = uid("un")
    nodes.append(helper.make_node("Unsqueeze", [x, axes_name], [un], name=uid("Unsqueeze")))
    out_name = out_name or uid("nc1w")
    nodes.append(helper.make_node("Transpose", [un], [out_name], name=uid("Transpose"), perm=[0, 3, 1, 2]))
    return nodes, inits, out_name


def svtr_attention(x, prefix, w):
    """x: [N,W,120]. Returns proj output [N,W,120] (caller adds the residual)."""
    nodes, inits = [], []
    qkv_w = w[prefix + ".mixer.qkv.weight"]  # [120,360], already [in,out]
    qkv_b = w[prefix + ".mixer.qkv.bias"]
    n, i, qkv = matmul_add(x, prefix + "_qkv_w", qkv_w, qkv_b); nodes += n; inits += i

    shape_name = uid("qkv_shape")
    inits.append(numpy_helper.from_array(np.array([0, -1, 3, 8, 15], dtype=np.int64), name=shape_name))
    qkv_r = uid("qkv_r")
    nodes.append(helper.make_node("Reshape", [qkv, shape_name], [qkv_r], name=uid("Reshape")))
    qkv_t = uid("qkv_t")
    nodes.append(helper.make_node("Transpose", [qkv_r], [qkv_t], name=uid("Transpose"), perm=[2, 0, 3, 1, 4]))

    q_raw, k, v = uid("q_raw"), uid("k"), uid("v")
    split_sizes_name = uid("split_sizes")
    inits.append(numpy_helper.from_array(np.array([1, 1, 1], dtype=np.int64), name=split_sizes_name))
    nodes.append(helper.make_node("Split", [qkv_t, split_sizes_name], [q_raw, k, v], name=uid("Split"), axis=0))
    sq_axes = uid("sq_axes0")
    inits.append(numpy_helper.from_array(np.array([0], dtype=np.int64), name=sq_axes))
    q_raw_sq, k_sq, v_sq = uid("q_sq"), uid("k_sq"), uid("v_sq")
    nodes.append(helper.make_node("Squeeze", [q_raw, sq_axes], [q_raw_sq], name=uid("Squeeze")))
    nodes.append(helper.make_node("Squeeze", [k, sq_axes], [k_sq], name=uid("Squeeze")))
    nodes.append(helper.make_node("Squeeze", [v, sq_axes], [v_sq], name=uid("Squeeze")))

    scale_name = uid("scale")
    inits.append(numpy_helper.from_array(np.array(SCALE, dtype=np.float32), name=scale_name))
    q_scaled = uid("q_scaled")
    nodes.append(helper.make_node("Mul", [q_raw_sq, scale_name], [q_scaled], name=uid("Mul")))

    k_t = uid("k_t")
    nodes.append(helper.make_node("Transpose", [k_sq], [k_t], name=uid("Transpose"), perm=[0, 1, 3, 2]))
    attn = uid("attn")
    nodes.append(helper.make_node("MatMul", [q_scaled, k_t], [attn], name=uid("MatMul")))
    attn_sm = uid("attn_sm")
    nodes.append(helper.make_node("Softmax", [attn], [attn_sm], name=uid("Softmax"), axis=-1))

    ctx = uid("ctx")
    nodes.append(helper.make_node("MatMul", [attn_sm, v_sq], [ctx], name=uid("MatMul")))
    ctx_t = uid("ctx_t")
    nodes.append(helper.make_node("Transpose", [ctx], [ctx_t], name=uid("Transpose"), perm=[0, 2, 1, 3]))
    merge_shape = uid("merge_shape")
    inits.append(numpy_helper.from_array(np.array([0, -1, 120], dtype=np.int64), name=merge_shape))
    ctx_merged = uid("ctx_merged")
    nodes.append(helper.make_node("Reshape", [ctx_t, merge_shape], [ctx_merged], name=uid("Reshape")))

    proj_w = w[prefix + ".mixer.proj.weight"]
    proj_b = w[prefix + ".mixer.proj.bias"]
    n, i, proj_out = matmul_add(ctx_merged, prefix + "_proj_w", proj_w, proj_b)
    nodes += n; inits += i
    return nodes, inits, proj_out


def svtr_mlp(x, prefix, w):
    nodes, inits = [], []
    fc1w, fc1b = w[prefix + ".mlp.fc1.weight"], w[prefix + ".mlp.fc1.bias"]
    n, i, fc1 = matmul_add(x, prefix + "_fc1_w", fc1w, fc1b); nodes += n; inits += i
    n, i, act = swish(fc1); nodes += n; inits += i
    fc2w, fc2b = w[prefix + ".mlp.fc2.weight"], w[prefix + ".mlp.fc2.bias"]
    n, i, fc2 = matmul_add(act, prefix + "_fc2_w", fc2w, fc2b); nodes += n; inits += i
    return nodes, inits, fc2


def build_ctc_nodes(w, feat_name="feat"):
    nodes, inits = [], []
    h = feat_name  # shortcut, saved before conv1/conv2 -- this IS feat, the neck's own input

    n, i, c1 = conv_bn_swish(feat_name, w, "head.ctc_encoder.encoder.conv1.conv.weight",
                              "head.ctc_encoder.encoder.conv1.norm", (1, 1)); nodes += n; inits += i
    n, i, c2 = conv_bn_swish(c1, w, "head.ctc_encoder.encoder.conv2.conv.weight",
                              "head.ctc_encoder.encoder.conv2.norm", (0, 0)); nodes += n; inits += i

    n, i, z = squeeze_h_transpose_to_ntc(c2); nodes += n; inits += i  # [N,W,120]

    for idx in (0, 1):
        prefix = f"head.ctc_encoder.encoder.svtr_block.{idx}"
        n, i, ln1 = layernorm(z, prefix + ".norm1", w, eps=1e-5); nodes += n; inits += i
        n, i, attn_out = svtr_attention(ln1, prefix, w); nodes += n; inits += i
        n, i, z = add(z, attn_out); nodes += n; inits += i
        n, i, ln2 = layernorm(z, prefix + ".norm2", w, eps=1e-5); nodes += n; inits += i
        n, i, mlp_out = svtr_mlp(ln2, prefix, w); nodes += n; inits += i
        n, i, z = add(z, mlp_out); nodes += n; inits += i

    n, i, z = layernorm(z, "head.ctc_encoder.encoder.norm", w, eps=1e-6); nodes += n; inits += i  # [N,W,120]

    n, i, z_nc1w = unsqueeze_transpose_to_nc1w(z); nodes += n; inits += i  # [N,120,1,W]

    n, i, c3 = conv_bn_swish(z_nc1w, w, "head.ctc_encoder.encoder.conv3.conv.weight",
                             "head.ctc_encoder.encoder.conv3.norm", (0, 0)); nodes += n; inits += i  # [N,512,1,W]

    concat_out = uid("concat")
    nodes.append(helper.make_node("Concat", [h, c3], [concat_out], name=uid("Concat"), axis=1))  # [N,1024,1,W]

    n, i, c4 = conv_bn_swish(concat_out, w, "head.ctc_encoder.encoder.conv4.conv.weight",
                             "head.ctc_encoder.encoder.conv4.norm", (1, 1)); nodes += n; inits += i  # [N,64,1,W]
    n, i, c5 = conv_bn_swish(c4, w, "head.ctc_encoder.encoder.conv1x1.conv.weight",
                             "head.ctc_encoder.encoder.conv1x1.norm", (0, 0)); nodes += n; inits += i  # [N,64,1,W]

    n, i, seq64 = squeeze_h_transpose_to_ntc(c5); nodes += n; inits += i  # [N,W,64]

    fc_w = w["head.ctc_head.fc.weight"]  # [64,97], [in,out]
    fc_b = w["head.ctc_head.fc.bias"]
    n, i, pre = matmul_add(seq64, "ctc_fc_w", fc_w, fc_b); nodes += n; inits += i
    n, i, ctc_logits = softmax(pre, axis=2, out_name="ctc_logits"); nodes += n; inits += i

    return nodes, inits, ctc_logits


if __name__ == "__main__":
    from build_backbone import build_backbone_nodes

    w = dict(np.load(WEIGHTS_NPZ))
    oracle = dict(np.load(r"D:\bhikaji\bhikajianpr\uvss-service\model_conversion\weights_extracted\ctc_oracle_dump.npz"))
    names = sorted(set(k.rsplit("__", 1)[0] for k in oracle if k.endswith("__input")))

    for name in names:
        x = oracle[f"{name}__input"].astype(np.float32)
        width = x.shape[3]

        bb_nodes, bb_inits, feat_name = build_backbone_nodes(w)
        ctc_nodes, ctc_inits, ctc_out = build_ctc_nodes(w, feat_name)
        nodes = bb_nodes + ctc_nodes
        inits = bb_inits + ctc_inits

        inp = helper.make_tensor_value_info("input", TensorProto.FLOAT, [1, 3, 48, width])
        out = helper.make_tensor_value_info(ctc_out, TensorProto.FLOAT, None)
        graph = helper.make_graph(nodes, "ctc", [inp], [out], initializer=inits)
        model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)])

        sess = ort.InferenceSession(model.SerializeToString(), providers=["CPUExecutionProvider"])
        got = sess.run(None, {"input": x})[0]
        expected = oracle[f"{name}__ctc_logits"]
        diff = np.abs(got - expected)
        print(name, "width", width, "shape", got.shape, "vs", expected.shape,
              "max abs diff", diff.max(), "mean abs diff", diff.mean())
