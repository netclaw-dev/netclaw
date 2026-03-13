---
name: search-citation
description: "Guides when to use web search vs. training data and how to cite sources. Ensures specific factual claims include source URLs and the agent does not hallucinate verifiable information."
metadata:
  author: netclaw
  version: "1.0.0"
  triggers: web search needed | cite sources | link results | price check | product search | find near me | verify facts
---

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

Every specific factual claim from a search **must** include a source URL.

| Rule | Detail |
|------|--------|
| Cite every claim | If the information came from a search result, link it |
| Link all sources | When multiple sources found, link all of them — recommend one if you have a basis |
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

## Cross-References

- Tool catalog and grant system: read `capability-reference`
- Memory patterns and recall: read `memory-usage`
- Search-type-specific guidance: see `references/` files listed above
