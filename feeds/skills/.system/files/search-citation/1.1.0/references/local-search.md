# Local Search: Restaurants, Bars, Shops, Services

Guidance for searching businesses and services near a specific location.

## Context Preferences

Use these if available in your context — do not ask the user to restate them:

| Preference | How to use |
|------------|------------|
| Home location | Default for "near me" queries — only ask if nothing is available and it cannot be inferred |
| Dietary restrictions | Filter restaurant results (vegetarian, gluten-free, allergies) |
| Cuisine preferences | Prioritize matching cuisines when suggesting restaurants |
| Budget | Filter by price range |

## What to Include

For each result, include as much of the following as the search provides:

- Business name
- Address or neighborhood
- Hours of operation (note if these may vary on holidays or seasonally)
- Phone number
- Price range or price tier (for restaurants: $, $$, $$$)
- Cuisine type (for restaurants)

## Linking Rules

- Link to the business's own website or Google Maps listing
- Do not link to aggregator search result pages (e.g., Yelp search results)
- A direct Google Maps link or the business's own site lets the user see
  location, reviews, and hours in one place
- If multiple businesses are suggested, each one gets its own link

## Tips

- If the user says "near me" and no location is in context, ask once — then
  suggest storing it for future queries
- Note when hours or seasonal availability might affect the recommendation
- When comparing options, a brief structured list (name, distance, price tier,
  cuisine) helps the user scan quickly
