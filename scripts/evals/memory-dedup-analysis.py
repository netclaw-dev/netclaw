#!/usr/bin/env python3
"""
Memory Deduplication Analysis Tool

Compares three approaches for finding duplicate memories:
1. Anchor-based: same anchor_id = duplicate
2. Title/alias fuzzy matching: token overlap on title + aliases
3. Vector embedding similarity: cosine similarity via Ollama embedding API

Usage:
    python3 scripts/evals/memory-dedup-analysis.py [--db PATH] [--ollama-url URL] [--model MODEL]
"""

import sqlite3
import json
import argparse
import sys
import time
from collections import defaultdict
from difflib import SequenceMatcher
from dataclasses import dataclass, field
from typing import Optional
import urllib.request
import urllib.error


@dataclass
class MemoryDoc:
    document_id: str
    anchor_id: str
    title: str
    memory_class: str
    body: str
    aliases: list = field(default_factory=list)
    facets: list = field(default_factory=list)
    embedding: Optional[list] = None


def _safe_json_list(raw: str | None) -> list:
    if not raw or not raw.strip().startswith('['):
        return []
    try:
        return json.loads(raw)
    except (json.JSONDecodeError, TypeError):
        return []


def load_memories(db_path: str) -> list[MemoryDoc]:
    """Load all memory documents AND records from SQLite."""
    conn = sqlite3.connect(db_path)
    docs = []

    # Documents
    cursor = conn.execute(
        "SELECT document_id, anchor_id, title, memory_class, "
        "markdown_body, aliases_json, facets_json FROM memory_documents"
    )
    for row in cursor:
        docs.append(MemoryDoc(
            document_id=row[0],
            anchor_id=row[1],
            title=row[2],
            memory_class=row[3],
            body=row[4],
            aliases=_safe_json_list(row[5]),
            facets=_safe_json_list(row[6]),
        ))

    # Records (use record_type as title, payload_json as body)
    cursor = conn.execute(
        "SELECT record_id, anchor_id, record_type, memory_class, "
        "payload_json, aliases_json, facets_json FROM memory_records"
    )
    for row in cursor:
        payload = row[4] or ""
        # Try to extract a readable title from the payload
        title = row[2]  # record_type as fallback title
        try:
            p = json.loads(payload)
            if isinstance(p, dict):
                title = p.get("title", p.get("summary", row[2]))
                if isinstance(title, str) and len(title) > 100:
                    title = title[:100]
        except (json.JSONDecodeError, TypeError):
            pass
        docs.append(MemoryDoc(
            document_id=row[0],
            anchor_id=row[1],
            title=str(title),
            memory_class=row[3],
            body=payload[:2000],  # cap payload for embedding
            aliases=_safe_json_list(row[5]),
            facets=_safe_json_list(row[6]),
        ))

    conn.close()
    return docs


# ── Approach 1: Anchor-based grouping ────────────────────────────────────────

def find_anchor_duplicates(docs: list[MemoryDoc]) -> dict[str, list[MemoryDoc]]:
    """Group documents by anchor_id. Groups with >1 doc are duplicates."""
    groups = defaultdict(list)
    for doc in docs:
        groups[doc.anchor_id].append(doc)
    return {k: v for k, v in groups.items() if len(v) > 1}


# ── Approach 2: Title/alias fuzzy matching ────────────────────────────────────

def tokenize(text: str) -> set[str]:
    """Simple whitespace + lowercase tokenization."""
    return set(text.lower().split())


def title_similarity(a: MemoryDoc, b: MemoryDoc) -> float:
    """Compute similarity between two docs using title + aliases."""
    # Combine title and aliases into a text blob
    text_a = " ".join([a.title] + a.aliases).lower()
    text_b = " ".join([b.title] + b.aliases).lower()

    # SequenceMatcher ratio (0.0 - 1.0)
    seq_ratio = SequenceMatcher(None, text_a, text_b).ratio()

    # Token overlap (Jaccard)
    tokens_a = tokenize(text_a)
    tokens_b = tokenize(text_b)
    if not tokens_a or not tokens_b:
        return seq_ratio
    jaccard = len(tokens_a & tokens_b) / len(tokens_a | tokens_b)

    # Weighted average
    return 0.6 * seq_ratio + 0.4 * jaccard


def find_title_duplicates(docs: list[MemoryDoc], threshold: float = 0.70
                          ) -> list[tuple[MemoryDoc, MemoryDoc, float]]:
    """Find pairs of docs with title/alias similarity above threshold."""
    pairs = []
    for i in range(len(docs)):
        for j in range(i + 1, len(docs)):
            sim = title_similarity(docs[i], docs[j])
            if sim >= threshold:
                pairs.append((docs[i], docs[j], sim))
    pairs.sort(key=lambda x: x[2], reverse=True)
    return pairs


# ── Approach 3: Vector embedding similarity ───────────────────────────────────

def get_embedding(text: str, ollama_url: str, model: str) -> list[float]:
    """Get embedding vector from Ollama API."""
    payload = json.dumps({"model": model, "input": text}).encode("utf-8")
    req = urllib.request.Request(
        f"{ollama_url}/api/embed",
        data=payload,
        headers={"Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            data = json.loads(resp.read())
            # Ollama returns {"embeddings": [[...]]} for /api/embed
            if "embeddings" in data and data["embeddings"]:
                return data["embeddings"][0]
            # Fallback for older API format
            if "embedding" in data:
                return data["embedding"]
            raise ValueError(f"Unexpected response format: {list(data.keys())}")
    except urllib.error.URLError as e:
        print(f"  [WARN] Embedding failed for text[:50]='{text[:50]}...': {e}", file=sys.stderr)
        return []


def cosine_similarity(a: list[float], b: list[float]) -> float:
    """Compute cosine similarity between two vectors."""
    if not a or not b or len(a) != len(b):
        return 0.0
    dot = sum(x * y for x, y in zip(a, b))
    norm_a = sum(x * x for x in a) ** 0.5
    norm_b = sum(x * x for x in b) ** 0.5
    if norm_a == 0 or norm_b == 0:
        return 0.0
    return dot / (norm_a * norm_b)


def compute_embeddings(docs: list[MemoryDoc], ollama_url: str, model: str) -> None:
    """Compute embeddings for all docs (modifies in place)."""
    total = len(docs)
    for i, doc in enumerate(docs):
        # Embed title + first 500 chars of body for efficiency
        text = f"{doc.title}\n{doc.body[:500]}"
        doc.embedding = get_embedding(text, ollama_url, model)
        if (i + 1) % 25 == 0 or i == total - 1:
            print(f"  Embedded {i + 1}/{total} documents...", file=sys.stderr)


def find_embedding_duplicates(docs: list[MemoryDoc], threshold: float = 0.85
                              ) -> list[tuple[MemoryDoc, MemoryDoc, float]]:
    """Find pairs of docs with embedding similarity above threshold."""
    pairs = []
    for i in range(len(docs)):
        if not docs[i].embedding:
            continue
        for j in range(i + 1, len(docs)):
            if not docs[j].embedding:
                continue
            sim = cosine_similarity(docs[i].embedding, docs[j].embedding)
            if sim >= threshold:
                pairs.append((docs[i], docs[j], sim))
    pairs.sort(key=lambda x: x[2], reverse=True)
    return pairs


# ── Reporting ─────────────────────────────────────────────────────────────────

def print_anchor_report(duplicates: dict[str, list[MemoryDoc]]) -> None:
    total_dupes = sum(len(v) - 1 for v in duplicates.values())
    print(f"\n{'='*70}")
    print(f"APPROACH 1: Anchor-Based Duplicates")
    print(f"{'='*70}")
    print(f"Duplicate groups: {len(duplicates)}")
    print(f"Redundant documents: {total_dupes}")
    print()
    for anchor_id, group in sorted(duplicates.items(), key=lambda x: -len(x[1])):
        print(f"  Anchor: {anchor_id} ({len(group)} docs)")
        for doc in group:
            print(f"    - [{doc.memory_class}] {doc.title[:70]}")
            print(f"      id={doc.document_id}")
    print()


def print_title_report(pairs: list[tuple[MemoryDoc, MemoryDoc, float]],
                       limit: int = 30) -> None:
    print(f"\n{'='*70}")
    print(f"APPROACH 2: Title/Alias Fuzzy Matching (>= 70%)")
    print(f"{'='*70}")
    print(f"Similar pairs found: {len(pairs)}")
    print()
    for a, b, sim in pairs[:limit]:
        same_anchor = "SAME ANCHOR" if a.anchor_id == b.anchor_id else "diff anchor"
        print(f"  [{sim:.0%}] ({same_anchor})")
        print(f"    A: {a.title[:70]}")
        print(f"    B: {b.title[:70]}")
        print()
    if len(pairs) > limit:
        print(f"  ... and {len(pairs) - limit} more pairs")
    print()


def print_embedding_report(pairs: list[tuple[MemoryDoc, MemoryDoc, float]],
                           limit: int = 30) -> None:
    print(f"\n{'='*70}")
    print(f"APPROACH 3: Vector Embedding Similarity (>= 85%)")
    print(f"{'='*70}")
    print(f"Similar pairs found: {len(pairs)}")
    print()
    for a, b, sim in pairs[:limit]:
        same_anchor = "SAME ANCHOR" if a.anchor_id == b.anchor_id else "diff anchor"
        print(f"  [{sim:.0%}] ({same_anchor})")
        print(f"    A: {a.title[:70]}")
        print(f"    B: {b.title[:70]}")
        print()
    if len(pairs) > limit:
        print(f"  ... and {len(pairs) - limit} more pairs")
    print()


def print_comparison(anchor_dupes: dict, title_pairs: list, embedding_pairs: list,
                     total_docs: int) -> None:
    """Compare what each approach found."""
    print(f"\n{'='*70}")
    print(f"COMPARISON SUMMARY ({total_docs} total documents)")
    print(f"{'='*70}")

    anchor_pairs_set = set()
    for group in anchor_dupes.values():
        ids = sorted(d.document_id for d in group)
        for i in range(len(ids)):
            for j in range(i + 1, len(ids)):
                anchor_pairs_set.add((ids[i], ids[j]))

    title_pairs_set = {
        tuple(sorted([a.document_id, b.document_id])) for a, b, _ in title_pairs
    }

    embedding_pairs_set = {
        tuple(sorted([a.document_id, b.document_id])) for a, b, _ in embedding_pairs
    }

    print(f"\n  Anchor-based pairs:    {len(anchor_pairs_set)}")
    print(f"  Title/alias pairs:     {len(title_pairs_set)}")
    print(f"  Embedding pairs:       {len(embedding_pairs_set)}")

    if anchor_pairs_set and embedding_pairs_set:
        overlap_ae = anchor_pairs_set & embedding_pairs_set
        print(f"\n  Anchor ∩ Embedding:    {len(overlap_ae)}")
        only_anchor = anchor_pairs_set - embedding_pairs_set
        only_embed = embedding_pairs_set - anchor_pairs_set
        print(f"  Only in Anchor:        {len(only_anchor)}")
        print(f"  Only in Embedding:     {len(only_embed)}")

    if title_pairs_set and embedding_pairs_set:
        overlap_te = title_pairs_set & embedding_pairs_set
        print(f"\n  Title ∩ Embedding:     {len(overlap_te)}")
        only_title = title_pairs_set - embedding_pairs_set
        only_embed2 = embedding_pairs_set - title_pairs_set
        print(f"  Only in Title:         {len(only_title)}")
        print(f"  Only in Embedding:     {len(only_embed2)}")

    # Unique to embedding (things only vector search found)
    if embedding_pairs_set:
        unique_to_embed = embedding_pairs_set - anchor_pairs_set - title_pairs_set
        if unique_to_embed:
            print(f"\n  Unique to embedding (not found by other approaches): {len(unique_to_embed)}")

    print()


def main():
    parser = argparse.ArgumentParser(description="Memory deduplication analysis")
    parser.add_argument("--db", default="~/.netclaw/netclaw.db", help="Path to netclaw.db")
    parser.add_argument("--ollama-url", default="http://100.113.176.76:11434", help="Ollama API URL")
    parser.add_argument("--model", default="qwen3-embedding:4b", help="Embedding model name")
    parser.add_argument("--skip-embeddings", action="store_true", help="Skip embedding computation")
    parser.add_argument("--title-threshold", type=float, default=0.70, help="Title similarity threshold")
    parser.add_argument("--embedding-threshold", type=float, default=0.85, help="Embedding similarity threshold")
    args = parser.parse_args()

    import os
    db_path = os.path.expanduser(args.db)

    print(f"Loading memories from {db_path}...")
    docs = load_memories(db_path)
    print(f"Loaded {len(docs)} documents")

    # Approach 1: Anchor-based
    print("\nRunning anchor-based duplicate detection...")
    anchor_dupes = find_anchor_duplicates(docs)
    print_anchor_report(anchor_dupes)

    # Approach 2: Title/alias fuzzy matching
    print("Running title/alias fuzzy matching...")
    title_pairs = find_title_duplicates(docs, threshold=args.title_threshold)
    print_title_report(title_pairs)

    # Approach 3: Vector embeddings
    embedding_pairs = []
    if not args.skip_embeddings:
        print(f"Computing embeddings via {args.ollama_url} ({args.model})...")
        t0 = time.time()
        compute_embeddings(docs, args.ollama_url, args.model)
        elapsed = time.time() - t0
        print(f"Embeddings computed in {elapsed:.1f}s")

        print("Running embedding similarity search...")
        embedding_pairs = find_embedding_duplicates(docs, threshold=args.embedding_threshold)
        print_embedding_report(embedding_pairs)

    # Comparison
    print_comparison(anchor_dupes, title_pairs, embedding_pairs, len(docs))


if __name__ == "__main__":
    main()
