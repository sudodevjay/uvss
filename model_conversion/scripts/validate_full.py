"""End-to-end validation: backbone+encoder ONNX graph -> incremental
decoder-step ONNX graph driven 25x from Python -> compare against oracle's
full sar_out per-step logits and final decoded text."""
import numpy as np
import onnx
from onnx import helper, TensorProto
import onnxruntime as ort

from build_backbone import build_backbone_nodes
from build_encoder import build_encoder_nodes
from build_decoder_step import build_model as build_decoder_model

WEIGHTS_NPZ = r"D:\bhikaji\bhikajianpr\uvss-service\model_conversion\weights_extracted\weights.npz"
ORACLE_NPZ = r"D:\bhikaji\bhikajianpr\uvss-service\model_conversion\weights_extracted\oracle_dump.npz"

START_IDX = 97
END_IDX = 97
PADDING_IDX = 98


def build_backbone_encoder_session(w, width):
    bb_nodes, bb_inits, feat_name = build_backbone_nodes(w)
    enc_nodes, enc_inits, attn_key, rnn_out, holistic = build_encoder_nodes(w, feat_name)
    nodes = bb_nodes + enc_nodes
    inits = bb_inits + enc_inits
    inp = helper.make_tensor_value_info("input", TensorProto.FLOAT, [1, 3, 48, width])
    vr_inp = helper.make_tensor_value_info("valid_ratio", TensorProto.FLOAT, [1])
    outs = [helper.make_tensor_value_info(n, TensorProto.FLOAT, None) for n in [feat_name, attn_key, holistic]]
    graph = helper.make_graph(nodes, "backbone_encoder", [inp, vr_inp], outs, initializer=inits)
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)])
    sess = ort.InferenceSession(model.SerializeToString(), providers=["CPUExecutionProvider"])
    return sess, [feat_name, attn_key, holistic]


def decode_greedy(char_list, indices):
    out = []
    for idx in indices:
        if idx == PADDING_IDX:
            continue
        if idx == END_IDX:
            break
        out.append(char_list[idx])
    return "".join(out)


def build_char_list():
    with open(r"D:\bhikaji\bhikajianpr\anpr-ai-service\weights\en_dict.txt", encoding="utf-8") as f:
        dict_chars = [line.strip("\n") for line in f]
    base = dict_chars + [" "]  # use_space_char=True
    base = base + ["<UKN>", "<BOS/EOS>", "<PAD>"]
    return base


if __name__ == "__main__":
    w = dict(np.load(WEIGHTS_NPZ))
    oracle = dict(np.load(ORACLE_NPZ))
    emb = w["head.sar_head.decoder.embedding.weight"]  # [99,512]
    char_list = build_char_list()

    dec_model = build_decoder_model(w)
    dec_sess = ort.InferenceSession(dec_model.SerializeToString(), providers=["CPUExecutionProvider"])

    names = sorted(set(k.rsplit("__", 1)[0] for k in oracle if k.endswith("__input")))

    with open(r"D:\bhikaji\bhikajianpr\uvss-service\model_conversion\weights_extracted\oracle_texts.txt") as f:
        oracle_texts = dict(line.rstrip("\n").split("\t")[:2] for line in f)

    all_ok = True
    for name in names:
        x = oracle[f"{name}__input"].astype(np.float32)
        vr = oracle[f"{name}__valid_ratio"].astype(np.float32)
        width = x.shape[3]

        sess, out_names = build_backbone_encoder_session(w, width)
        feat, attn_key, holistic = sess.run(None, {"input": x, "valid_ratio": vr})

        state = {
            "h0": np.zeros((1, 512), dtype=np.float32), "c0": np.zeros((1, 512), dtype=np.float32),
            "h1": np.zeros((1, 512), dtype=np.float32), "c1": np.zeros((1, 512), dtype=np.float32),
        }

        def step(prev_embedding):
            out = dec_sess.run(None, {
                "prev_embedding": prev_embedding, "h0_in": state["h0"], "c0_in": state["c0"],
                "h1_in": state["h1"], "c1_in": state["c1"],
                "feat": feat, "attn_key": attn_key, "holistic_feat": holistic, "valid_ratio": vr,
            })
            logits, state["h0"], state["c0"], state["h1"], state["c1"] = out
            return logits

        # prime step: input = holistic_feat itself
        _ = step(holistic.astype(np.float32))

        pred_indices = []
        all_logits = []
        prev_tok_emb = emb[START_IDX][np.newaxis, :].astype(np.float32)
        for i in range(25):
            logits = step(prev_tok_emb)
            all_logits.append(logits[0])
            pred = int(np.argmax(logits[0]))
            pred_indices.append(pred)
            prev_tok_emb = emb[pred][np.newaxis, :].astype(np.float32)

        all_logits = np.array(all_logits)  # [25,98]
        # forward_test applies softmax BEFORE storing into outputs (rec_sar_head.py:336) --
        # oracle's sar_out is already post-softmax, so compare probabilities, not raw logits.
        exp_probs = oracle[f"{name}__sar_out"][0]  # [25,98]
        all_probs = np.exp(all_logits - all_logits.max(axis=1, keepdims=True))
        all_probs /= all_probs.sum(axis=1, keepdims=True)
        diff = np.abs(all_probs - exp_probs)

        text = decode_greedy(char_list, pred_indices)
        expected_text = oracle_texts[name]

        ok = text == expected_text
        all_ok = all_ok and ok
        print(name)
        print("  logits max diff:", diff.max(), "mean diff:", diff.mean())
        print("  port text:", repr(text), " oracle text:", repr(expected_text), "MATCH" if ok else "MISMATCH")

    print()
    print("ALL MATCH" if all_ok else "SOME MISMATCHES")
