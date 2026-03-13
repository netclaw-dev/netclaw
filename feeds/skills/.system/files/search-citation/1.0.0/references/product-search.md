# Product Search: Shopping, Hardware, Goods

Guidance for searching products, comparing prices, and finding specific items.

## Context Preferences

Use these if available in your context — do not ask the user to restate them:

| Preference | How to use |
|------------|------------|
| Budget | Filter or sort by price range |
| Preferred retailers | Prioritize results from those stores |
| Brand preferences | Narrow search to preferred brands when relevant |

## What to Include

For each product result:

- Product name and key specs
- Price and retailer
- Link to the specific product page

When comparing products, present in a structured format:

| Product | Price | Retailer | Key Specs |
|---------|-------|----------|-----------|
| Example | $X.XX | Store | Specs here |

## Linking Rules

- Link to the specific product page, not to search results or category pages
- A direct product page URL triggers rich previews (image, title, price) in
  most chat clients — this is far more useful than a generic search link
- If the same product is available from multiple retailers, link each one so
  the user can compare
- Prefer retailer product pages over review aggregator pages — let the user
  see the actual price and availability

## Important Caveats

- Note if prices may vary (marketplace/third-party sellers, regional pricing)
- Note if an item is sold by a third-party seller on a marketplace (e.g.,
  Amazon third-party vs. sold by Amazon)
- If stock or availability is uncertain, say so rather than implying it is
  in stock
- When the user asks for "cheap" options, search broadly — do not limit to
  one retailer
