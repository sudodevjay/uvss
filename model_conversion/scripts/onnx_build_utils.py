"""Shared helpers for hand-authoring the SAR ONNX graphs from raw Paddle
weights (paddle2onnx is a confirmed dead end on this machine -- see
SAR_ONNX_NOTES.md). Every helper returns (nodes, initializers) lists to
append to the graph being built.
"""
import numpy as np
import onnx
from onnx import helper, numpy_helper, TensorProto

_counter = [0]


def uid(prefix):
    _counter[0] += 1
    return f"{prefix}_{_counter[0]}"


def const_init(name, arr):
    arr = np.asarray(arr)
    return numpy_helper.from_array(arr.astype(np.float32) if arr.dtype.kind == "f" else arr, name=name)


def conv2d(x, w_name, w_arr, b_arr, stride, pad, groups, out_name=None):
    """w_arr: [out,in/groups,kh,kw] (Paddle layout == ONNX layout)."""
    out_name = out_name or uid("conv_out")
    inits = [const_init(w_name, w_arr)]
    inputs = [x, w_name]
    if b_arr is not None:
        bname = w_name + "_b"
        inits.append(const_init(bname, b_arr))
        inputs.append(bname)
    node = helper.make_node(
        "Conv", inputs, [out_name], name=uid("Conv"),
        kernel_shape=[w_arr.shape[2], w_arr.shape[3]],
        strides=list(stride), pads=[pad[0], pad[1], pad[0], pad[1]], group=groups,
    )
    return [node], inits, out_name


def batchnorm(x, prefix, w, out_name=None, eps=1e-5):
    out_name = out_name or uid("bn_out")
    gname, bname, mname, vname = [prefix + s for s in ("_g", "_b", "_m", "_v")]
    inits = [
        const_init(gname, w[prefix + ".weight"]),
        const_init(bname, w[prefix + ".bias"]),
        const_init(mname, w[prefix + "._mean"]),
        const_init(vname, w[prefix + "._variance"]),
    ]
    node = helper.make_node("BatchNormalization", [x, gname, bname, mname, vname], [out_name],
                             name=uid("BN"), epsilon=eps)
    return [node], inits, out_name


def hard_sigmoid(x, out_name=None):
    out_name = out_name or uid("hsig_out")
    node = helper.make_node("HardSigmoid", [x], [out_name], name=uid("HardSigmoid"),
                             alpha=1.0 / 6.0, beta=0.5)
    return [node], [], out_name


def hard_swish(x, out_name=None):
    nodes, inits, hs = hard_sigmoid(x)
    out_name = out_name or uid("hswish_out")
    nodes.append(helper.make_node("Mul", [x, hs], [out_name], name=uid("Mul")))
    return nodes, inits, out_name


def swish(x, out_name=None):
    """x * sigmoid(x)"""
    sig = uid("sig")
    out_name = out_name or uid("swish_out")
    nodes = [
        helper.make_node("Sigmoid", [x], [sig], name=uid("Sigmoid")),
        helper.make_node("Mul", [x, sig], [out_name], name=uid("Mul")),
    ]
    return nodes, [], out_name


def global_avg_pool(x, out_name=None):
    out_name = out_name or uid("gap_out")
    node = helper.make_node("GlobalAveragePool", [x], [out_name], name=uid("GAP"))
    return [node], [], out_name


def relu(x, out_name=None):
    out_name = out_name or uid("relu_out")
    node = helper.make_node("Relu", [x], [out_name], name=uid("Relu"))
    return [node], [], out_name


def mul_broadcast(a, b, out_name=None):
    out_name = out_name or uid("mul_out")
    node = helper.make_node("Mul", [a, b], [out_name], name=uid("Mul"))
    return [node], [], out_name


def add(a, b, out_name=None):
    out_name = out_name or uid("add_out")
    node = helper.make_node("Add", [a, b], [out_name], name=uid("Add"))
    return [node], [], out_name


def se_module(x, prefix, w, in_ch, out_name=None):
    """AdaptiveAvgPool(1) -> conv1(1x1,reduce) -> relu -> conv2(1x1) -> hardsigmoid -> mul(x, gate)."""
    all_nodes, all_inits = [], []
    n, i, gap = global_avg_pool(x); all_nodes += n; all_inits += i
    c1w = w[prefix + ".conv1.weight"]; c1b = w[prefix + ".conv1.bias"]
    n, i, c1 = conv2d(gap, prefix + ".conv1.weight", c1w, c1b, (1, 1), (0, 0), 1); all_nodes += n; all_inits += i
    n, i, r1 = relu(c1); all_nodes += n; all_inits += i
    c2w = w[prefix + ".conv2.weight"]; c2b = w[prefix + ".conv2.bias"]
    n, i, c2 = conv2d(r1, prefix + ".conv2.weight", c2w, c2b, (1, 1), (0, 0), 1); all_nodes += n; all_inits += i
    n, i, gate = hard_sigmoid(c2); all_nodes += n; all_inits += i
    out_name = out_name or uid("se_out")
    all_nodes.append(helper.make_node("Mul", [x, gate], [out_name], name=uid("Mul")))
    return all_nodes, all_inits, out_name


def matmul_add(x, w_name, w_arr, b_arr, out_name=None):
    """Linear: x @ w_arr + b_arr. w_arr expected already in [in,out] layout for MatMul."""
    out_name = out_name or uid("lin_out")
    inits = [const_init(w_name, w_arr)]
    mm = uid("mm")
    nodes = [helper.make_node("MatMul", [x, w_name], [mm], name=uid("MatMul"))]
    if b_arr is not None:
        bname = w_name + "_b"
        inits.append(const_init(bname, b_arr))
        nodes.append(helper.make_node("Add", [mm, bname], [out_name], name=uid("Add")))
        return nodes, inits, out_name
    return nodes, inits, mm


def layernorm(x, prefix, w, eps, out_name=None):
    out_name = out_name or uid("ln_out")
    gname, bname = prefix + "_g", prefix + "_b"
    inits = [const_init(gname, w[prefix + ".weight"]), const_init(bname, w[prefix + ".bias"])]
    node = helper.make_node("LayerNormalization", [x, gname, bname], [out_name], name=uid("LN"),
                             axis=-1, epsilon=eps)
    return [node], inits, out_name


def softmax(x, axis, out_name=None):
    out_name = out_name or uid("softmax_out")
    node = helper.make_node("Softmax", [x], [out_name], name=uid("Softmax"), axis=axis)
    return [node], [], out_name


def transpose(x, perm, out_name=None):
    out_name = out_name or uid("transpose_out")
    node = helper.make_node("Transpose", [x], [out_name], name=uid("Transpose"), perm=perm)
    return [node], [], out_name


def reshape(x, shape_name, shape_arr, out_name=None):
    out_name = out_name or uid("reshape_out")
    inits = [numpy_helper.from_array(np.asarray(shape_arr, dtype=np.int64), name=shape_name)]
    node = helper.make_node("Reshape", [x, shape_name], [out_name], name=uid("Reshape"))
    return [node], inits, out_name
