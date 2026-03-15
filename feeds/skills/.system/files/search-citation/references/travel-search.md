# Travel Search: Flights, Hotels, Bookings

Guidance for searching flights, hotels, and travel-related bookings.

## Context Preferences

Use these if available in your context — do not ask the user to restate them:

| Preference | How to use |
|------------|------------|
| Home airport | Default departure city |
| Preferred airline | Search that airline first or prioritize its results |
| Loyalty programs | Note when results earn miles/points in a program the user belongs to |
| Travel budget | Filter or sort by price range |

## What to Include

### Flights

- Airline and flight number (if available)
- Departure and arrival times with time zones
- Number of stops (nonstop, 1 stop, etc.)
- Price per person
- Link to the booking or listing page

### Hotels

- Hotel name and neighborhood/location
- Price per night
- Star rating or review score
- Key amenities (breakfast, parking, Wi-Fi)
- Link to the booking or listing page

## Linking Rules

- Link to specific booking or listing pages, not search result pages
- If comparing across providers (airline site vs. aggregator), link to each
- A direct booking link saves the user from re-searching

## Important Caveats

- Prices change frequently — note when the results were found
  ("as of [date/time]") so the user knows the information may already be stale
- Suggest the user verify the price before booking
- If the search returns a wide price range, present a few representative
  options (cheapest, best value, most convenient) rather than an exhaustive list
