---
name: search-citation
description: "REQUIRED before any web_search or web_fetch call. Read this first when the user asks you to search, look up, verify, price-check, or find anything online. Contains citation format rules and source-handling policies."
metadata:
  author: netclaw
  version: "1.0.0"
---

## Critical Rules Summary

1. Search-first for time-sensitive, verifiable, or specific factual queries.
2. Every claim from search results gets an inline hyperlink. No URL = do not state.
3. Never use footnotes, endnotes, or [1]-style references. Inline links only.
4. When web grant is disabled, tell the user — do not fall back to training data.

## When to Search

Use `web_search` when the answer is specific, time-sensitive, or verifiable:

- Prices, availability, current stock
- Current events, news, recent happenings
- Specific product info, specs, reviews
- Local businesses, restaurants, services
- Travel options (flights, hotels, bookings)
- Anything where being wrong has consequences or the data changes

## When Training Data Is Fine

Do not search for things that are general, stable, or conceptual:

- How things work, science, definitions
- Well-established facts that do not change
- Programming concepts, math, language questions
- Opinion or advice where your reasoning is the value
- General heuristics ("mid-week flights are usually cheaper") — these are fine
  as a first response before offering to search for specifics

## Use Context to Refine Searches

Your context may include user preferences relevant to the search — location,
preferred vendors, dietary needs, budget, loyalty programs, etc. Use these to
refine queries rather than asking the user to restate them.

- Mention the preference used so the user can correct if needed
  ("checking United since that's your preferred airline")
- See reference files for which preferences apply to each search type:
  - `references/local-search.md` — restaurants, bars, shops, services
  - `references/travel-search.md` — flights, hotels, bookings
  - `references/product-search.md` — products, hardware, goods

**Example flow:**

1. User asks about flights
2. Offer general advice from training data ("mid-week is usually cheapest")
3. Offer to search for specifics
4. When searching, use context-provided preferences (preferred airline, home
   airport) to tailor the query

## Citation Rules

Every specific factual claim from a search **must** include a source URL as an
**inline hyperlink** in the natural flow of your response.

**Format: inline hyperlinks only.** Use standard markdown link syntax —
`[descriptive text](url)` — placed directly in the sentence where the claim
appears. Do **not** use footnotes, endnotes, numbered reference lists, or
bracketed citation markers like `[1]`. The goal is natural, readable prose with
clickable links, not an academic paper.

| Rule | Detail |
|------|--------|
| Cite every claim | If the information came from a search result, hyperlink it inline |
| Inline, not footnotes | Write `[Product X is $29](https://example.com/product-x)` — never `Product X is $29 [1]` with a reference list at the bottom |
| Link all sources | When multiple sources were found, link each one inline where relevant — recommend one if you have a basis |
| Prefer specific pages | Link to the product page, restaurant page, or listing — not to a search results or category page |
| No URL, no fact | Do not present specific claims (prices, ratings, availability) without a source URL |
| Unlinkable content | If a source cannot be linked directly (form-post navigation, JS-rendered content), use browser automation to capture a screenshot and attach it via `attach_file` — a screenshot beats no source |

## When Search Is Not Available

If the `web` grant is not enabled and the query requires a search:

- Tell the user they need to enable web search for this type of query
- Do not fall back to training data for something that should be searched
- Do not guess at prices, availability, or other verifiable specifics

## When Search Comes Up Empty

If a search returns no useful results:

- Say so honestly: "I wasn't able to find current information on X"
- Do not guess or fabricate specifics to fill the gap
- Offer alternative approaches if possible (different search terms, checking
  a specific site with `web_fetch`, trying later)

## web_fetch Usage Notes

`web_fetch` defaults to raw HTML mode, preserving page structure including links
and images. This is ideal for crawling and extracting specific information. Use
`format='text'` only when you need plain text without markup (e.g., for
summarization of article body text).

## Cross-References

- Tool grants and capabilities: load `netclaw-operations`
- Search-type-specific guidance: see `references/` files in this skill directory
