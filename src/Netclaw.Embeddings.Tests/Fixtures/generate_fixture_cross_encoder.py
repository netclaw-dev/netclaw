#!/usr/bin/env python3
"""Generates the tiny fixture cross-encoder ONNX model + WordPiece vocab used by
Netclaw.Embeddings.Tests (OnnxCrossEncoderScorerTests). Sibling to
generate_fixture_model.py (the bi-encoder embedder fixture) - same conventions,
different graph shape.

Regeneration:
    python3 -m venv /tmp/onnxgen && source /tmp/onnxgen/bin/activate
    pip install onnx==1.22.0 numpy
    python3 generate_fixture_cross_encoder.py <output-dir>

Graph shape (deliberately NOT a real BertForSequenceClassification export - see
below for why):

    input_ids       int64 [batch, seq]  --Gather(embedding_matrix)------> word_embeddings [batch, seq, 1]
    token_type_ids  int64 [batch, seq]  --Gather(type_scale_matrix)-----> type_scale [batch, seq, 1]
    word_embeddings * type_scale ------------------------------------------> combined [batch, seq, 1]
    attention_mask  int64 [batch, seq]  --Cast/Unsqueeze------------------>  mask [batch, seq, 1]
    combined * mask --ReduceSum(axis=1)------------------------------------> sum_embeddings [batch, 1, 1]
    mask --ReduceSum(axis=1)--> sum_mask [batch, 1, 1] --Clip(min=1e-9)-->
    pooled = sum_embeddings / sum_mask   [batch, 1, 1]
    pooled_2d = Reshape(pooled, [-1, 1]) [batch, 1]
    logits = MatMul(pooled_2d, classifier_weight[1,1]) + classifier_bias[1]  [batch, 1]

Why this shape: OnnxCrossEncoderScorer feeds three inputs (input_ids,
attention_mask, token_type_ids) and reads a single [batch, 1] "logits" output,
matching the real BertForSequenceClassification cross-encoder's declared
signature exactly (verified against the pinned Xenova/ms-marco-MiniLM-L-6-v2
export: 3 inputs, one [batch,1] float output). Unlike the bi-encoder fixture
(generate_fixture_model.py), this graph actually CONSUMES token_type_ids - and
it MULTIPLIES the per-position type scale into the word embedding rather than
adding a separate type term. Addition would not work as a test fixture:
sum_i(word_i) + sum_i(type_i) is invariant to WHICH position holds which word
(addition commutes), so swapping a word between the query and candidate
segments would not change the pooled total at all - the fixture would then
"pass" even if OnnxCrossEncoderScorer fed all-zero token_type_ids by mistake.
Multiplying ties each word's OWN contribution to its OWN segment, so swapping
a nonzero-valued word between segments changes the total whenever the two
segments' scales differ - exactly the property OnnxCrossEncoderScorerTests
needs to prove pair encoding assigns token_type_ids to the correct positions,
not just "some type-1 positions exist somewhere."

Single-dimension embeddings (DIMS=1) are a deliberate simplification versus the
embedder fixture's 8 dimensions: every test scenario in
OnnxCrossEncoderScorerTests computes its own expected sigmoid(logit) by hand
from the token counts and per-token scalar values below, so keeping the model
to one dimension keeps that hand computation tractable and exact rather than a
second matrix multiply to reason through.
"""
import sys
import numpy as np
import onnx
from onnx import helper, TensorProto, numpy_helper

# Special tokens carry a zero embedding so every "signal" scenario in the test
# suite can reason purely about which content words are present, without special
# tokens perturbing the mean. Content words:
#   "relevant" / "answer" -- strong positive signal (used as the pair's
#     "topic" word so a query/candidate sharing it scores high)
#   "irrelevant"          -- strong negative signal
#   "filler"/"the"/"cat"/"sat"/"on"/"mat" -- neutral filler, contributes 0, used
#     to pad candidates past the truncation budget without affecting the score
VOCAB = [
    "[PAD]", "[UNK]", "[CLS]", "[SEP]",
    "the", "cat", "sat", "on", "mat", "filler",
    "relevant", "answer", "irrelevant",
]
DIMS = 1

# index -> scalar embedding value. Special tokens and neutral filler are 0.0;
# see module docstring for why a single dimension keeps every test's expected
# value hand-computable.
WORD_VALUES = {
    "[PAD]": 0.0, "[UNK]": 0.0, "[CLS]": 0.0, "[SEP]": 0.0,
    "the": 0.0, "cat": 0.0, "sat": 0.0, "on": 0.0, "mat": 0.0, "filler": 0.0,
    "relevant": 10.0,
    "answer": 10.0,
    "irrelevant": -10.0,
}

# Per-segment multiplicative scale: query segment (type 0) leaves a word's own
# value unchanged; candidate segment (type 1) doubles it. See the module
# docstring for why multiplication (not addition) is required for this fixture
# to actually prove token_type_ids assignment rather than merely their presence.
TYPE_SCALES = [1.0, 2.0]

# Classifier weight/bias chosen so sigmoid(logit) lands well above 0.5 for a
# candidate containing "relevant"/"answer" paired with a matching query, and
# well below 0.5 otherwise -- see OnnxCrossEncoderScorerTests for the exact
# hand-computed expected values per scenario.
CLASSIFIER_WEIGHT = 1.0
CLASSIFIER_BIAS = 0.0


def main(out_dir: str) -> None:
    vocab_size = len(VOCAB)

    embedding_rows = np.array([[WORD_VALUES[tok]] for tok in VOCAB], dtype=np.float32)
    type_scale_rows = np.array([[v] for v in TYPE_SCALES], dtype=np.float32)
    classifier_weight = np.array([[CLASSIFIER_WEIGHT]], dtype=np.float32)
    classifier_bias = np.array([CLASSIFIER_BIAS], dtype=np.float32)

    input_ids = helper.make_tensor_value_info("input_ids", TensorProto.INT64, ["batch", "seq"])
    attention_mask = helper.make_tensor_value_info("attention_mask", TensorProto.INT64, ["batch", "seq"])
    token_type_ids = helper.make_tensor_value_info("token_type_ids", TensorProto.INT64, ["batch", "seq"])
    logits = helper.make_tensor_value_info("logits", TensorProto.FLOAT, ["batch", 1])

    initializers = [
        numpy_helper.from_array(embedding_rows, name="embedding_matrix"),
        numpy_helper.from_array(type_scale_rows, name="type_scale_matrix"),
        numpy_helper.from_array(classifier_weight, name="classifier_weight"),
        numpy_helper.from_array(classifier_bias, name="classifier_bias"),
        numpy_helper.from_array(np.array([1], dtype=np.int64), name="axis_1"),
        numpy_helper.from_array(np.array([-1], dtype=np.int64), name="axis_neg1"),
        numpy_helper.from_array(np.array(1e-9, dtype=np.float32), name="mask_floor"),
        numpy_helper.from_array(np.array([-1, DIMS], dtype=np.int64), name="reshape_2d_shape"),
    ]

    nodes = [
        helper.make_node("Gather", ["embedding_matrix", "input_ids"], ["word_embeddings"], axis=0, name="gather_word_embeddings"),
        helper.make_node("Gather", ["type_scale_matrix", "token_type_ids"], ["type_scale"], axis=0, name="gather_type_scale"),
        helper.make_node("Mul", ["word_embeddings", "type_scale"], ["combined_embeddings"], name="apply_type_scale"),
        helper.make_node("Cast", ["attention_mask"], ["mask_float"], to=TensorProto.FLOAT, name="cast_mask"),
        helper.make_node("Unsqueeze", ["mask_float", "axis_neg1"], ["mask_expanded"], name="unsqueeze_mask"),
        helper.make_node("Mul", ["combined_embeddings", "mask_expanded"], ["masked_embeddings"], name="apply_mask"),
        helper.make_node("ReduceSum", ["masked_embeddings", "axis_1"], ["sum_embeddings"], keepdims=1, name="sum_embeddings"),
        helper.make_node("ReduceSum", ["mask_expanded", "axis_1"], ["sum_mask"], keepdims=1, name="sum_mask"),
        helper.make_node("Clip", ["sum_mask", "mask_floor"], ["sum_mask_clipped"], name="clip_sum_mask"),
        helper.make_node("Div", ["sum_embeddings", "sum_mask_clipped"], ["pooled"], name="mean_pool"),
        helper.make_node("Reshape", ["pooled", "reshape_2d_shape"], ["pooled_2d"], name="reshape_pooled"),
        helper.make_node("MatMul", ["pooled_2d", "classifier_weight"], ["logits_matmul"], name="classifier_matmul"),
        helper.make_node("Add", ["logits_matmul", "classifier_bias"], ["logits"], name="classifier_bias_add"),
    ]

    graph = helper.make_graph(
        nodes=nodes,
        name="tiny_cross_encoder_fixture",
        inputs=[input_ids, attention_mask, token_type_ids],
        outputs=[logits],
        initializer=initializers,
    )

    model = helper.make_model(graph, producer_name="netclaw-fixture-generator", opset_imports=[helper.make_opsetid("", 18)])
    model.ir_version = 9
    onnx.checker.check_model(model)

    model_path = f"{out_dir}/tiny-cross-encoder.onnx"
    onnx.save(model, model_path)

    vocab_path = f"{out_dir}/tiny-cross-encoder-vocab.txt"
    with open(vocab_path, "w", encoding="utf-8") as f:
        f.write("\n".join(VOCAB) + "\n")

    print(f"wrote {model_path} ({vocab_size} vocab rows x {DIMS} dims)")
    print(f"wrote {vocab_path}")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else ".")
