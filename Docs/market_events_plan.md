# Dynamic Market-Impacting Events Timeline Proposal

This document outlines the architecture and implementation strategy for fetching and displaying major geopolitical, economic, and historical events (such as COVID-19, US Presidential elections, wars, etc.) as dynamic annotations on the portfolio growth chart.

---

## Architectural Options

### Option A: Community-Curated Public JSON Feed (Recommended)
* **How it works:** The web application fetches a curated `events.json` file from a public, open-source GitHub repository or Gist (e.g. hosted on your project's GitHub page) at startup.
* **Pros:** 
  * Highly reliable: human editors curate the description and ensure only major, relevant events are included.
  * Extensible: anyone in the community can submit a Pull Request to add new events as they happen.
  * Minimal overhead: simple `fetch` request, cached in-memory on the server.
* **Cons:** Requires a public repository or Gist to host the JSON file, though maintenance is virtually zero due to the infrequency of major global events (typically 2–5 per year).

### Option B: Algorithmic Market-Shock News Fetcher (Self-Sustaining)
* **How it works:** 
  1. The C# backend analyzes the historical data of a baseline index (such as `VOO` or `NIFTYBEES`) on startup.
  2. It automatically detects months with statistical anomalies (e.g. monthly returns falling >2.5 standard deviations below average, representing historical crashes like March 2020, Sept 2008, etc.).
  3. For each anomaly date, the server calls the free **Wikipedia Search API** to fetch the primary global events that occurred in that specific month and year, dynamically labeling the chart.
* **Pros:**
  * 100% automated and self-sustaining: requires zero human editing or hosting.
  * Dynamically scales to any dataset or historical timeframe.
* **Cons:** Wikipedia titles for a specific month might contain non-market-related trivia that requires basic regex filtering (e.g., filtering for keywords like "crisis", "election", "war", "pandemic", "interest rate").

### Option C: Wikipedia Current Events Portal Scraper
* **How it works:** The server scrapes the structured timelines from [Wikipedia's Current Events Portals](https://en.wikipedia.org/wiki/Portal:Current_events) or year summary pages (e.g., `https://en.wikipedia.org/wiki/2020_in_politics_and_government`) via the MediaWiki API, parsing headlines matching keywords associated with global economics and conflicts.
* **Pros:** Pulls directly from the world's largest collaborative encyclopedia.
* **Cons:** High parsing complexity; Wikipedia page structures change periodically, making scrapers fragile and prone to breaking.

---

## Proposed Changes (For Future Reference)

### Frontend (Chart.js Integration)
* Add the official `chartjs-plugin-annotation.min.js` library via CDN in the `@section Scripts` block of the views.
* Update the Chart.js configuration options to draw vertical line annotations at event dates:
  ```javascript
  plugins: {
      annotation: {
          annotations: {
              // Dynamically draw vertical dashed lines with tooltips on hover for each event date
          }
      }
  }
  ```

### Backend (Event Fetcher and Controller)
* Create a new helper class `Helper/EventTimelineHelper.cs` to handle fetching, caching, and filtering event records based on the chosen option.
* Include a list of relevant `MarketEvent` objects (containing `Date`, `Label`, and `Description`) in the JSON API responses for both the Portfolio and Compare endpoints, filtered to fall within the selected simulation period.
