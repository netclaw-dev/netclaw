#!/usr/bin/env python3
"""Generates the tiny fixture ONNX model + WordPiece vocab used by
Netclaw.Embeddings.Tests (OnnxMemoryEmbedderTests).

Regeneration:
    python3 -m venv /tmp/onnxgen && source /tmp/onnxgen/bin/activate
    pip install onnx==1.22.0 numpy
    python3 generate_fixture_model.py <output-dir>

Graph shape (deliberately NOT a real BERT export — see below for why):

    input_ids       int64 [batch, seq]  --Gather(embedding_matrix)--> token_embeddings [batch, seq, dims]
    attention_mask  int64 [batch, seq]  --Cast/Unsqueeze-->            mask [batch, seq, 1]
    token_embeddings * mask --ReduceSum(axis=1)--> sum_embeddings [batch, 1, dims]
    mask --ReduceSum(axis=1)--> sum_mask [batch, 1, 1] --Clip(min=1e-9)-->
    last_hidden_state = sum_embeddings / sum_mask   [batch, 1, dims]

Why mean-pooling instead of a plain Gather + CLS passthrough: OnnxMemoryEmbedder
always reads position 0 along the sequence axis of `last_hidden_state` (CLS-token
selection — matches both allowlisted production models per their model cards).
A plain Gather has no cross-token mixing, so a fixture that just emits per-token
rows would make position 0 *always* equal the fixed [CLS]-token embedding row
regardless of the rest of the input — every text would embed identically, and a
bug that dropped the input text entirely would go uncaught. Attention-masked mean
pooling over all real (non-padding) tokens, reported as the graph's only sequence
position, makes the fixture's output genuinely depend on input content — exactly
like a real model's contextualized CLS output does — while keeping
OnnxMemoryEmbedder's "always read index 0" logic identical for fixture and
production graphs. The graph declares no token_type_ids input (unlike the real
BERT exports) on purpose: OnnxMemoryEmbedder must feed only the inputs a loaded
session actually declares (session.InputMetadata.Keys), never a hardcoded
assumption of the 3-input production signature.
"""
import sys
import numpy as np
import onnx
from onnx import helper, TensorProto, numpy_helper

VOCAB = [
    "[PAD]", "[UNK]", "[CLS]", "[SEP]",
    "the", "cat", "sat", "on", "mat",
    "dog", "run", "##ning",
    "hello", "world",
    "quarterly", "revenue", "grew", "percent",
]
DIMS = 8


def main(out_dir: str) -> None:
    vocab_size = len(VOCAB)

    # Fixed, deterministic embedding matrix: row i = [i*0.1, i*0.1+0.01, ...].
    # No randomness so the fixture (and its expected test vectors) never drifts
    # across regenerations.
    rows = []
    for i in range(vocab_size):
        rows.append([round(i * 0.1 + j * 0.01, 4) for j in range(DIMS)])
    embedding_matrix = np.array(rows, dtype=np.float32)

    input_ids = helper.make_tensor_value_info("input_ids", TensorProto.INT64, ["batch", "seq"])
    attention_mask = helper.make_tensor_value_info("attention_mask", TensorProto.INT64, ["batch", "seq"])
    last_hidden_state = helper.make_tensor_value_info(
        "last_hidden_state", TensorProto.FLOAT, ["batch", 1, DIMS]
    )

    initializers = [
        numpy_helper.from_array(embedding_matrix, name="embedding_matrix"),
        numpy_helper.from_array(np.array([1], dtype=np.int64), name="axis_1"),
        numpy_helper.from_array(np.array([-1], dtype=np.int64), name="axis_neg1"),
        numpy_helper.from_array(np.array(1e-9, dtype=np.float32), name="mask_floor"),
    ]

    nodes = [
        helper.make_node("Gather", ["embedding_matrix", "input_ids"], ["token_embeddings"], axis=0, name="gather_token_embeddings"),
        helper.make_node("Cast", ["attention_mask"], ["mask_float"], to=TensorProto.FLOAT, name="cast_mask"),
        helper.make_node("Unsqueeze", ["mask_float", "axis_neg1"], ["mask_expanded"], name="unsqueeze_mask"),
        helper.make_node("Mul", ["token_embeddings", "mask_expanded"], ["masked_embeddings"], name="apply_mask"),
        helper.make_node("ReduceSum", ["masked_embeddings", "axis_1"], ["sum_embeddings"], keepdims=1, name="sum_embeddings"),
        helper.make_node("ReduceSum", ["mask_expanded", "axis_1"], ["sum_mask"], keepdims=1, name="sum_mask"),
        helper.make_node("Clip", ["sum_mask", "mask_floor"], ["sum_mask_clipped"], name="clip_sum_mask"),
        helper.make_node("Div", ["sum_embeddings", "sum_mask_clipped"], ["last_hidden_state"], name="mean_pool"),
    ]

    graph = helper.make_graph(
        nodes=nodes,
        name="tiny_memory_embedder_fixture",
        inputs=[input_ids, attention_mask],
        outputs=[last_hidden_state],
        initializer=initializers,
    )

    model = helper.make_model(graph, producer_name="netclaw-fixture-generator", opset_imports=[helper.make_opsetid("", 18)])
    model.ir_version = 9
    onnx.checker.check_model(model)

    model_path = f"{out_dir}/tiny-embedder.onnx"
    onnx.save(model, model_path)

    vocab_path = f"{out_dir}/tiny-vocab.txt"
    with open(vocab_path, "w", encoding="utf-8") as f:
        f.write("\n".join(VOCAB) + "\n")

    print(f"wrote {model_path} ({vocab_size} vocab rows x {DIMS} dims)")
    print(f"wrote {vocab_path}")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else ".")
