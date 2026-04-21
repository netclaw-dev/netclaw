---
name: web-content-retrieval
description: "Load when fetching content from URLs, especially social media or JavaScript-heavy sites. Explains when web_fetch works vs when browser automation is required."
keywords:
  - url
  - fetch
  - content
  - link
  - twitter
  - x.com
  - tweet
  - social media
  - instagram
  - linkedin
  - facebook
  - javascript
  - blocked
  - scrape
metadata:
  author: netclaw
  version: "1.0.0"
---

# Web Content Retrieval

This skill helps you choose the right tool for fetching web content.

## Quick Decision

| Site type | Tool | Why |
|-----------|------|-----|
| Static HTML (blogs, docs, news articles) | `web_fetch` | Fast, simple, works |
| Social media (Twitter/X, Instagram, LinkedIn) | Browser automation | JS-rendered, auth walls |
| SPAs, dynamic dashboards | Browser automation | Content loaded via JS |
| APIs, JSON endpoints | `web_fetch` | Direct data access |

## When web_fetch Works

`web_fetch` sends a simple HTTP request and returns the response. It works for:
- Static HTML pages
- Server-rendered content
- Public APIs and JSON endpoints
- News articles, blog posts, documentation

## When web_fetch Fails

`web_fetch` will return empty, partial, or error content when:
- The page requires JavaScript to render (SPAs, React/Vue apps)
- The site blocks non-browser user agents
- Content is behind a login wall
- The site uses anti-bot protections

**Common sites that require browser automation:**
- Twitter/X (`x.com`, `twitter.com`)
- Instagram
- LinkedIn
- Facebook
- Most modern social media platforms

## Using Browser Automation

If a browser MCP server is available (e.g., Playwright), use it for JS-heavy sites:

```
# First, check if browser tools are available
search_tools(query: "browser navigate")

# If available, use browser navigation
browser_navigate(url: "https://x.com/username/status/123")
```

Browser automation:
1. Renders JavaScript
2. Handles dynamic content loading
3. Can interact with pages (scroll, click)
4. Takes screenshots for verification

## When Browser Automation Is Unavailable

If no browser MCP is configured, tell the user honestly:

> "This URL appears to be from a JavaScript-heavy site (Twitter/X) that typically
> requires browser automation to fetch content. I don't have a browser tool
> available in this session. You could:
> 1. Share the content directly (copy/paste the tweet text)
> 2. Enable a browser MCP server like Playwright
> 3. I can try web_fetch, but it will likely return incomplete content"

**Do not silently fail.** If you attempt `web_fetch` on a known JS-heavy site and
get empty/blocked content, explain why and suggest alternatives.

## Fallback Strategy

1. **Check the domain** — is it a known JS-heavy site?
2. **If yes, check for browser tools** — `search_tools(query: "browser")`
3. **If browser available** — use it
4. **If no browser** — inform the user, offer alternatives
5. **If uncertain** — try `web_fetch` first, but be ready to explain if it fails

## Cross-References

- Citation and source rules: load `search-citation`
- Tool discovery: see `netclaw-operations` → Tool Discovery
