"""Stage 1: hand-author the MobileNetV1Enhance backbone as an ONNX graph and
validate it against the oracle's captured `feat` tensor for one fixed input
width (static-shape graph for now -- dynamic W comes later once the math is
verified end to end)."""
import numpy as np
import onnx
from onnx import helper, TensorProto
import onnxruntime as ort

from onnx_build_utils import conv2d, batchnorm, hard_swish, se_module, uid, add

WEIGHTS_NPZ = r"D:\bhikaji\bhikajianpr\uvss-service\model_conversion\weights_extracted\weights.npz"


def conv_bn_hswish(x, w, conv_key, bn_key, stride, pad, groups, out_name=None):
    nodes, inits = [], []
    cw = w[conv_key]
    n, i, c = conv2d(x, conv_key, cw, None, stride, pad, groups); nodes += n; inits += i
    n, i, b = batchnorm(c, bn_key, w); nodes += n; inits += i
    n, i, a = hard_swish(b, out_name); nodes += n; inits += i
    return nodes, inits, a


# (dw_kernel, dw_pad, stride_hw, has_se, out_ch) per block index 0..12
BLOCK_SPEC = [
    (3, 1, (1, 1), False),  # 0
    (3, 1, (1, 1), False),  # 1
    (3, 1, (1, 1), False),  # 2
    (3, 1, (2, 1), False),  # 3
    (3, 1, (1, 1), False),  # 4
    (3, 1, (2, 1), False),  # 5
    (5, 2, (1, 1), False),  # 6
    (5, 2, (1, 1), False),  # 7
    (5, 2, (1, 1), False),  # 8
    (5, 2, (1, 1), False),  # 9
    (5, 2, (1, 1), False),  # 10
    (5, 2, (2, 1), True),   # 11
    (5, 2, (1, 2), True),   # 12 (last_conv_stride)
]


def build_backbone_nodes(w, input_name="input"):
    nodes, inits = [], []

    n, i, x = conv_bn_hswish(input_name, w, "backbone.conv1._conv.weight", "backbone.conv1._batch_norm",
                              (2, 2), (1, 1), 1, out_name="conv1_out")
    nodes += n; inits += i

    for idx, (k, pad, stride, has_se) in enumerate(BLOCK_SPEC):
        prefix = f"backbone.block_list.{idx}"
        dw_conv_key = f"{prefix}._depthwise_conv._conv.weight"
        dw_bn_key = f"{prefix}._depthwise_conv._batch_norm"
        dw_ch = w[dw_conv_key].shape[0]  # depthwise: out==in==groups
        n, i, dw = conv_bn_hswish(x, w, dw_conv_key, dw_bn_key, stride, (pad, pad), dw_ch,
                                   out_name=f"block{idx}_dw")
        nodes += n; inits += i
        cur = dw
        if has_se:
            n, i, cur = se_module(cur, f"{prefix}._se", w, dw_ch, out_name=f"block{idx}_se")
            nodes += n; inits += i
        pw_conv_key = f"{prefix}._pointwise_conv._conv.weight"
        pw_bn_key = f"{prefix}._pointwise_conv._batch_norm"
        n, i, x = conv_bn_hswish(cur, w, pw_conv_key, pw_bn_key, (1, 1), (0, 0), 1,
                                  out_name=f"block{idx}_out")
        nodes += n; inits += i

    # Final AvgPool2D kernel=[2,2] stride=[2,2] pad=0
    feat_name = "feat"
    nodes.append(helper.make_node("AveragePool", [x], [feat_name], name=uid("AvgPool"),
                                   kernel_shape=[2, 2], strides=[2, 2], pads=[0, 0, 0, 0]))
    return nodes, inits, feat_name


def build_model_for_width(w, width):
    nodes, inits, feat_name = build_backbone_nodes(w)
    inp = helper.make_tensor_value_info("input", TensorProto.FLOAT, [1, 3, 48, width])
    out = helper.make_tensor_value_info(feat_name, TensorProto.FLOAT, [1, None, None, None])
    graph = helper.make_graph(nodes, "backbone", [inp], [out], initializer=inits)
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)])
    return model


if __name__ == "__main__":
    w = dict(np.load(WEIGHTS_NPZ))
    oracle = dict(np.load(r"D:\bhikaji\bhikajianpr\uvss-service\model_conversion\weights_extracted\oracle_dump.npz"))

    # pick first crop's dump keys
    names = sorted(set(k.rsplit("__", 1)[0] for k in oracle if k.endswith("__input")))
    for name in names:
        x = oracle[f"{name}__input"]
        expected_feat = oracle[f"{name}__feat"]
        width = x.shape[3]
        model = build_model_for_width(w, width)
        sess = ort.InferenceSession(model.SerializeToString(), providers=["CPUExecutionProvider"])
        got = sess.run(None, {"input": x.astype(np.float32)})[0]
        diff = np.abs(got - expected_feat)
        print(name, "width", width, "feat shape", got.shape, "vs expected", expected_feat.shape,
              "max abs diff", diff.max(), "mean abs diff", diff.mean())
