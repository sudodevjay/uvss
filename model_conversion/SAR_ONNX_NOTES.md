# SAR plate-OCR ONNX port — implementation notes

Hand-authored ONNX graphs for the PP-OCRv3 SAR recognition model
(`anpr-ai-service/weights/indian_plate_rec_v2_sar_best_accuracy.pdparams`),
built with the plain `onnx` Python package (no paddle2onnx — that path is a
confirmed dead end on this machine: every combination of paddle2onnx
1.3.1/2.0.2rc1/2.1.0 with paddle 3.1.0/3.1.1/3.2.2/3.3.1 either fails to
import, fails to parse the PIR-format model, or segfaults on export).

Validated against the real Paddle model (loaded the same way
`sar_refiner.py` loads it) on **15 diverse crops** (real camera crops from
two cameras, older dataset crops, single-line and two-line synthetic
plates, valid_ratio ranging 0.29–0.43 so the attention masking path is
genuinely exercised, not just trivially passed). **All 15 decode to
character-identical text**, with softmax-probability max abs diff ≈
1e-6–2e-6 (float32 rounding noise) at every one of the 25 decode steps.
Validation scripts live in `scripts/` (`oracle.py`, `build_backbone.py`,
`build_encoder.py`, `build_decoder_step.py`, `validate_full.py`,
`save_final.py`) — rerun `oracle.py` then `validate_full.py` if the
checkpoint ever changes.

`anpr-ai-service` was not modified — read-only throughout (weights,
vendored `ppocr` source via `venv/Scripts/python.exe`).

## Output files

- `output/sar_backbone_and_encoder.onnx` — MobileNetV1Enhance backbone +
  SAR encoder (2-layer LSTM) + the loop-invariant `attn_key`. Dynamic width.
  - Inputs: `input` float32 `[1,3,48,width]` (preprocessed exactly like
    `PlateOCR._resize_norm`/`sar_refiner.py`'s `_resize_norm`: resize to
    height 48, `(x/255-0.5)/0.5`, zero-pad on the right to the computed
    `img_w`), `valid_ratio` float32 `[1]` (real content width / padded
    width, same value already computed by preprocessing).
  - Outputs: `feat` float32 `[1,512,1,feat_width]` (feat_width ≈ width/8,
    exact formula: conv1 stride2 halves width, block 12's
    `last_conv_stride` halves it again, final AvgPool2d(k2,s2) halves it a
    third time — matches `floor` arithmetic, don't assume exact division),
    `attn_key` float32 `[1,512,1,feat_width]`, `holistic_feat` float32
    `[1,512]`.
- `output/sar_decoder_step.onnx` — one incremental decode step (see
  "Incremental decoder" below).
  - Inputs: `prev_embedding` float32 `[1,512]`, `h0_in`/`c0_in`/`h1_in`/`c1_in`
    float32 `[1,512]` (decoder LSTM layer-0/layer-1 hidden/cell state),
    `feat`, `attn_key`, `holistic_feat`, `valid_ratio` (same tensors as
    graph A's outputs — pass the SAME per-image values into every call).
  - Outputs: `logits` float32 `[1,98]` (**raw, pre-softmax** — see gotcha
    below), `h0_out`/`c0_out`/`h1_out`/`c1_out` float32 `[1,512]` (feed
    back in as next step's `h*_in`/`c*_in`).

## Driving the decode loop (C# must reproduce this exactly)

1. Run graph A once per plate crop → `feat`, `attn_key`, `holistic_feat`.
2. Zero-init `h0,c0,h1,c1` to `[1,512]` zeros.
3. **Priming call**: run graph B with `prev_embedding = holistic_feat`
   (yes, the encoder's output vector itself, not a token embedding) and
   the zero state. Discard its `logits` output — only its returned
   `h*/c*` state matters, it seeds the LSTM before any real decode step.
4. Set `prev_token = embedding_table[97]` (`START_IDX = 97`).
5. For `step in 1..25`: run graph B with `prev_embedding = prev_token` and
   the carried state; read `logits`; `argmax` → `pred`; if `pred == 97`
   (`END_IDX`, same value as start — it's a shared BOS/EOS token) **stop
   and discard this step's character** (matches
   `SARLabelDecode.decode`'s break-before-append at the end token,
   `rec_postprocess.py:715-719`); else if `pred == 98` (`PADDING_IDX`)
   skip appending but keep decoding; else append `character_list[pred]` to
   the output string. Set `prev_token = embedding_table[pred]` and
   continue to the next step regardless (the reference loop always runs
   the full 25 steps — there is no early `break` out of the *loop*, only
   out of what gets appended to the decoded string).
6. `character_list` = 95 lines of `en_dict.txt` + `" "` (space) +
   `"<UKN>"` (idx 96) + `"<BOS/EOS>"` (idx 97) + `"<PAD>"` (idx 98) — 99
   entries, matching the embedding table's vocab size. `embedding_table`
   is `head.sar_head.decoder.embedding.weight`, shape `[99,512]` — plain
   row lookup by token id, do the lookup with the raw extracted weight
   matrix (in C#: bake it as a constant float array, or run it through a
   tiny ONNX `Gather` if that's more convenient — it's just a row copy,
   no need for a full ONNX Embedding graph).

## Gotchas found during validation (each one cost real debugging time)

- **`sar_out`/`logits` scale mismatch (the big one)**: the reference
  `SARHead.forward_test` (`rec_sar_head.py:336`) applies
  `F.softmax(char_output, -1)` **before** appending to its per-step output
  list. If you compare this port's raw `logits` output directly against
  a captured `sar_out` dump from the real model, you'll see huge (~10-20)
  absolute differences and think something is badly broken — it isn't;
  you're comparing raw logits to softmax probabilities. Apply softmax to
  this port's `logits` before comparing to anything captured from the
  real model's `forward_test`/`sar_out`. (The `logits` output itself is
  intentionally left un-softmaxed in the ONNX graph — softmax it in C#
  right before/at the same time as the argmax, or apply it and ignore the
  normalization since argmax is invariant to it anyway.)
- **LSTM gate order**: Paddle's `nn.LSTM` weight/bias row layout is
  `[i,f,g,o]` (input, forget, cell-candidate, output), confirmed
  empirically (a tiny controlled LSTM with known random weights, compared
  against manual gate math under several candidate orderings — `ifgo`
  matched to ~3e-8, every other ordering was off by 0.08-0.27). ONNX's
  `LSTM` op wants `[i,o,f,c]` (`c` = same thing as Paddle's `g`). Every
  weight/bias tensor fed to an ONNX `LSTM` node in these graphs has
  already been row-permuted from Paddle's order to ONNX's — see
  `permute_ifgo_to_iofc` in `scripts/build_encoder.py` (reused by
  `build_decoder_step.py`). If you ever rebuild these graphs from scratch
  rather than reusing that helper, re-derive this — don't assume a
  "standard" gate order, frameworks disagree.
- **Two aliased LSTM parameter names in the checkpoint** (e.g.
  `rnn_encoder.weight_ih_l0` vs `rnn_encoder.0.cell.weight_ih`): confirmed
  these are the same underlying parameters exposed under two of Paddle's
  own naming conventions (verified: a fresh, untrained `paddle.nn.LSTM`'s
  `state_dict()` already shows both keys for the same tensors). Only the
  `*.0.cell.*` / `*.1.cell.*` naming was used when building these graphs;
  the `*_l0`/`*_l1` aliases in the manifest can be ignored.
- **Incremental single-step decoder vs. the reference's
  recompute-the-whole-sequence-every-step form**: the architecture spec
  flagged this equivalence as *unverified* (causal/unidirectional LSTM
  math says it should hold, but hadn't been checked against the real
  model). It's now empirically confirmed correct — this whole port relies
  on it, and the ~1e-6 max diff across all 25 steps on all 15 test crops
  is the proof. Don't reintroduce the reference's expensive
  recompute-everything form in C#; the incremental form is both correct
  and far cheaper.
- **Backbone/neck activations differ**: the MobileNetV1Enhance backbone
  uses `hard_swish` (`x * hardsigmoid(x)`, with `hardsigmoid`'s
  `alpha=1/6, beta=0.5` — **not** ONNX `HardSigmoid`'s own default
  `alpha=0.2`, must be set explicitly). Nothing in the SAR path itself
  uses the SVTR neck's plain `swish`/SiLU (`x*sigmoid(x)`) — that
  activation only matters if/when the CTC crop-selection path (see
  "Not built" below) gets ported.
- **`attn_key` is loop-invariant**: `conv3x3_1(feat)` doesn't depend on
  decoder state, only on the per-image `feat` — computed once in graph A,
  passed unchanged into every graph-B call. Don't recompute it per step.
- **Attention masking threshold is `>=`, not `>`**: the reference masks
  `attn_weight[i, :, :, valid_width:, :] = -inf` (`rec_sar_head.py:255`,
  a Python slice starting at `valid_width`), i.e. the position *at* index
  `valid_width` is itself masked. Graph B's `GreaterOrEqual` against
  `ceil(valid_ratio * feat_width)` matches this exactly — don't change it
  to `>`.

## Not built in this pass (deferred, documented so it isn't silently forgotten)

- **CTC head + SVTR neck**: only used in production for the *fast*
  multi-crop-variant search that picks which single crop gets the
  (much slower, ~6-7x per call) SAR refinement — SAR is what actually
  produces the accurate final text (98.81% exact-match vs CTC's 87.75%,
  per `sar_refiner.py`'s own docstring), and SAR is what this pass
  ported and validated. The architecture-research fork's report already
  has the full SVTR neck spec (conv1→conv2→2×transformer
  blocks→norm→conv3→conv4→conv1x1, combined-QKV self-attention with
  `scale=15**-0.5`, `swish` activations, LayerNorm eps 1e-6 for the final
  norm vs 1e-5 per-block) if a future pass wants CTC-based crop selection
  running natively in C# too — it was left out of scope here per the
  parent task's explicit priority ("don't let CTC block progress").
- **Plate-crop preprocessing (`_resize_norm`) itself**: not implemented
  in C# here — this pass only covers the model. The exact formula (fixed
  height 48, `img_w = int(48 * max(320/48, aspect_ratio))`, resize,
  `(x/255-0.5)/0.5`, zero-pad on the right, `valid_ratio = min(1, resized_w/img_w)`)
  is in `scripts/oracle.py`'s `resize_norm` (copied verbatim from
  `sar_refiner.py`) and must be ported to C# (likely shared with the
  already-planned OCR preprocessing work) to produce graph A's `input`/
  `valid_ratio` tensors correctly.

## CTC + SVTR neck (added in a later pass)

Needed for production's multi-crop-variant fast-search (`main.py`'s
`recognize_candidates`/`recognize_best`): CTC is ~6-7x faster per call than
SAR, used to cheaply score ~6 crop/enhancement variants per frame before SAR
refines only the single winning crop. SAR alone (the earlier pass) can't
drive that search on its own -- it's too slow to run 6x per frame.

**Output file**: `output/ctc_backbone_and_head.onnx` -- a single combined
graph, NOT a standalone CTC-only graph. It contains the identical
backbone+SAR-encoder computation `sar_backbone_and_encoder.onnx` already
has, PLUS the SVTR neck + CTC head on top, so **the backbone only runs
once per crop** and one `InferenceSession.Run` call returns everything
needed for both paths:

- Inputs: `input` float32 `[1,3,48,width]`, `valid_ratio` float32 `[1]`
  (same preprocessing as the SAR graph -- see the existing "Driving the
  decode loop" section above for the exact `_resize_norm` formula).
- Outputs (in this order): `feat` `[1,512,1,feat_width]`, `attn_key`
  `[1,512,1,feat_width]`, `holistic_feat` `[1,512]` (all three identical to
  `sar_backbone_and_encoder.onnx`'s outputs -- feed these into
  `sar_decoder_step.onnx` exactly as before if SAR refinement is wanted for
  this crop), `ctc_logits` float32 `[1,feat_width,97]` (**post-softmax**,
  per-timestep class probabilities over the 97 CTC classes -- index 0 is
  blank, matching `["blank"] + en_dict.txt lines + [" "]`).

**Wiring guidance for whoever builds the C# multi-crop-variant search**:
call this ONE graph per crop/variant image instead of the old SAR-only
graph -- it's a strict superset (same SAR outputs plus `ctc_logits`), so
there's no reason to keep calling `sar_backbone_and_encoder.onnx`
separately once this is wired in. Decode `ctc_logits` with a plain greedy
CTC decode (argmax per timestep, collapse repeated non-blank runs, drop
blanks) for the fast per-variant scoring pass; only the winning variant's
`feat`/`attn_key`/`holistic_feat` (already available from this same call,
no extra backbone run needed) get fed into `sar_decoder_step.onnx` for the
final SAR-refined reading. Note: production's actual CTC decode
(`PlateOCR._decode_ctc` in `anpr-ai-service/main.py`) has a repeated-
character-recovery step beyond plain greedy collapse (see the original
project plan/summary for the exact median-run-length threshold logic) --
port that too if exactly matching production's raw-CTC text output
(before SAR refinement) matters, not just using CTC for fast scoring.

**Validation**: built from source (`ppocr/modeling/necks/rnn.py`'s
`EncoderWithSVTR.forward`, `ppocr/modeling/backbones/rec_svtrnet.py`'s
`Attention`/`Mlp`/`Block`, `ppocr/modeling/heads/rec_ctc_head.py`), then
checked against the real Paddle model's actual eval-mode call chain
(`model.head.ctc_encoder(feat)` -> `model.head.ctc_head(ctc_encoder_out,
targets=None)`, confirmed from `rec_multi_head.py:139-140` -- the "svtr"
`encoder_type` branch runs `self.encoder(x)` directly on the raw
`[N,512,1,W]` backbone output, THEN reshapes, not the other way round).
15 test crops (real + synthetic, single/two-line, three different widths),
worst-case max abs diff across all of them: `feat` 1.26e-04, `attn_key`
6.51e-05, `holistic_feat` 4.77e-06, `ctc_logits` 7.78e-06 -- all float32
rounding noise (feature values are O(1-10) magnitude), same tightness bar
as the original SAR validation, no correctness issues. Validation scripts:
`scripts/ctc_oracle.py` (dumps ground-truth `ctc_logits` from the real
model), `scripts/build_ctc.py` (builds + validates the SVTR+CTC nodes
against a fixed-width test graph), `scripts/save_ctc_final.py` (builds and
saves the final dynamic-width combined graph, run this if the checkpoint
ever changes).

**Gotchas hit**:
- **Activation mixup risk (same family as the existing backbone/neck
  gotcha)**: this neck's `conv1`-`conv4`/`conv1x1` all use plain `Swish`
  (`x*sigmoid(x)`), matching the SAR backbone's SVTR-adjacent parts, NOT
  the MobileNetV1Enhance backbone's `hard_swish`. Confirmed from
  `rec_svtrnet.py`'s `ConvBNLayer` usage in `rnn.py`'s `EncoderWithSVTR.__init__`
  (`act=nn.Swish` passed explicitly to every one of these convs).
- **ONNX opset 17's `Split` op needs split sizes as an input tensor, not an
  attribute** -- passing `split=[1,1,1]` as a node attribute (the pre-opset-13
  convention) fails at graph-load time with `Unrecognized attribute: split`.
  Fixed by adding a small int64 initializer tensor as the node's second
  input instead.
- **Paddle's `nn.Linear` weight layout is `[in,out]`**, matching what
  `matmul_add`'s `MatMul` node wants directly -- confirmed again here for
  the SVTR attention's combined `qkv` weight (`[120,360]`) and every other
  Linear in this neck; no transpose needed before feeding into `matmul_add`.
- **The `paddle.reshape([0, H, W, C]).transpose([0,3,1,2])` idiom** (going
  from the transformer-block sequence shape `[N,W,C]` back to `[N,C,1,W]`
  for the following convs) is exactly equivalent to `Unsqueeze(axis=1)` then
  `Transpose(perm=[0,3,1,2])` given `H=1` always holds for this pipeline's
  fixed 48px input height -- implemented that way rather than a literal
  Reshape, since Paddle's `0` placeholder (`H=1`) is awkward to express as a
  static ONNX Reshape shape when the other dimension (`W`) is dynamic;
  `Squeeze(axis=[2])` + `Transpose([0,2,1])` handles the reverse direction
  (`[N,C,1,W]` -> `[N,W,C]`) the same way, reusing the exact pattern
  `build_encoder.py` already established for `feat`'s own squeeze/transpose.
